# ─────────────────────────────────────────────────────────────────────────────
# Quire — compute.
#
# Separate frontend, API and media worker images. They
# scale on different signals because they are under different pressure - the API
# on concurrent HTTP, the worker on how much media is waiting.
#
# No Kubernetes. See docs/ARCHITECTURE.md: two stateless
# containers, one queue and one database do not need a control plane, and an
# AKS cluster would add a node pool, an upgrade cadence and an RBAC surface that
# nothing here asks for.
# ─────────────────────────────────────────────────────────────────────────────

terraform {
  required_version = ">= 1.16.3, < 2.0.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 5.5"
    }
  }
}

locals {
  # nonsensitive() is safe here: the decision is whether a secret was supplied,
  # not the secret itself, and for_each cannot accept a sensitive value.
  rabbitmq_secret_enabled = nonsensitive(var.rabbitmq_secret_id != null)
  rabbitmq_scaler_enabled = nonsensitive(var.rabbitmq_scaler_connection_secret != null)
}

# One identity for both apps. It is what authorises Blob and Key Vault access,
# so no storage key or vault credential exists anywhere in the deployment.
resource "azurerm_user_assigned_identity" "apps" {
  name                = "id-${var.name_prefix}"
  resource_group_name = var.resource_group_name
  location            = var.location
  tags                = var.tags
}

# Data-plane access only. The identity can read and write blobs; it cannot
# create containers, change the account, or read account keys.
resource "azurerm_role_assignment" "blob_contributor" {
  scope                = var.storage_account_id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_user_assigned_identity.apps.principal_id
}

resource "azurerm_role_assignment" "key_vault_secrets" {
  scope                = var.key_vault_id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_user_assigned_identity.apps.principal_id
}

resource "azurerm_role_assignment" "acr_pull" {
  scope                = var.container_registry_id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.apps.principal_id
}

resource "azurerm_container_app_environment" "main" {
  name                       = "cae-${var.name_prefix}"
  resource_group_name        = var.resource_group_name
  location                   = var.location
  log_analytics_workspace_id = var.log_analytics_workspace_id

  # Injected into the VNet so apps reach PostgreSQL, Blob and Key Vault over
  # private endpoints rather than the public internet.
  infrastructure_subnet_id       = var.container_apps_subnet_id
  internal_load_balancer_enabled = var.internal_only

  tags = var.tags
}

# ── Migration job ────────────────────────────────────────────────────────────
# Schema changes run once here, as a manual job, before a rollout. Replicas
# never migrate on start: N instances racing the same DDL on every deploy is a
# risk with no upside. See docs/ARCHITECTURE.md.
resource "azurerm_container_app_job" "migrate" {
  name                         = "job-migrate-${var.name_prefix}"
  resource_group_name          = var.resource_group_name
  location                     = var.location
  container_app_environment_id = azurerm_container_app_environment.main.id

  replica_timeout_in_seconds = 600
  replica_retry_limit        = 0 # a failed migration must be looked at, not retried blindly

  manual_trigger_config {
    parallelism              = 1
    replica_completion_count = 1
  }

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.apps.id]
  }

  registry {
    server   = var.container_registry_login_server
    identity = azurerm_user_assigned_identity.apps.id
  }

  secret {
    name                = "database-connection-string"
    key_vault_secret_id = var.migration_database_secret_id
    identity            = azurerm_user_assigned_identity.apps.id
  }

  template {
    container {
      name   = "migrate"
      image  = "${var.container_registry_login_server}/collaborative-media-pipeline-api:${var.image_tag}"
      cpu    = 0.5
      memory = "1Gi"

      args = ["--migrate"]

      env {
        name        = "ConnectionStrings__MediaPipeline"
        secret_name = "database-connection-string"
      }
    }
  }

  tags = var.tags
}

# ── API ──────────────────────────────────────────────────────────────────────
resource "azurerm_container_app" "api" {
  name                         = "ca-api-${var.name_prefix}"
  resource_group_name          = var.resource_group_name
  container_app_environment_id = azurerm_container_app_environment.main.id
  revision_mode                = "Single"
  tags                         = var.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.apps.id]
  }

  registry {
    server   = var.container_registry_login_server
    identity = azurerm_user_assigned_identity.apps.id
  }

  secret {
    name                = "database-connection-string"
    key_vault_secret_id = var.database_secret_id
    identity            = azurerm_user_assigned_identity.apps.id
  }

  dynamic "secret" {
    for_each = local.rabbitmq_secret_enabled ? toset(["rabbitmq"]) : toset([])
    content {
      name                = "rabbitmq-password"
      key_vault_secret_id = var.rabbitmq_secret_id
      identity            = azurerm_user_assigned_identity.apps.id
    }
  }

  ingress {
    external_enabled = false
    target_port      = 8080
    transport        = "auto" # negotiates HTTP/2 and WebSockets for SignalR

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }

    # Only the frontend in this environment can route to the internal API.

  }

  template {
    min_replicas = var.api_min_replicas
    max_replicas = var.api_max_replicas

    container {
      name   = "api"
      image  = "${var.container_registry_login_server}/collaborative-media-pipeline-api:${var.image_tag}"
      cpu    = var.api_cpu
      memory = var.api_memory

      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = var.aspnetcore_environment
      }

      env {
        name        = "ConnectionStrings__MediaPipeline"
        secret_name = "database-connection-string"
      }

      env {
        name  = "DataProtection__BlobUri"
        value = var.keyring_blob_uri
      }
      env {
        name  = "DataProtection__KeyVaultKeyUri"
        value = var.keyring_key_uri
      }
      # Storage authenticates with the workload identity.
      env {
        name  = "Storage__Provider"
        value = "AzureBlob"
      }

      env {
        name  = "Storage__AzureBlob__ServiceUri"
        value = var.storage_blob_endpoint
      }

      env {
        name  = "Storage__AzureBlob__ContainerName"
        value = var.assets_container_name
      }

      env {
        name  = "AZURE_CLIENT_ID"
        value = azurerm_user_assigned_identity.apps.client_id
      }

      env {
        name  = "RabbitMq__HostName"
        value = var.rabbitmq_host
      }

      env {
        name  = "RabbitMq__Port"
        value = tostring(var.rabbitmq_port)
      }

      env {
        name  = "RabbitMq__VirtualHost"
        value = var.rabbitmq_vhost
      }

      env {
        name  = "RabbitMq__UserName"
        value = var.rabbitmq_username
      }

      dynamic "env" {
        for_each = local.rabbitmq_secret_enabled ? toset(["rabbitmq"]) : toset([])
        content {
          name        = "RabbitMq__Password"
          secret_name = "rabbitmq-password"
        }
      }

      env {
        name  = "RabbitMq__UseTls"
        value = "true"
      }

      env {
        name  = "OpenTelemetry__OtlpEndpoint"
        value = var.otlp_endpoint
      }

      env {
        name  = "Cors__AllowedOrigins__0"
        value = var.web_origin
      }

      # Defence in depth; frontend CIDR restrictions enforce origin isolation.
      env {
        name  = "FrontDoor__ExpectedId"
        value = var.front_door_id == null ? "" : var.front_door_id
      }

      # Liveness asks only whether the process is up. Pointing it at readiness
      # would restart every replica during a database blip and turn a degraded
      # system into an outage.
      liveness_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/health/live"
        initial_delay           = 10
        interval_seconds        = 30
        failure_count_threshold = 3
      }

      # Readiness takes a replica out of rotation while its dependencies are
      # unreachable, without killing it.
      readiness_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/health/ready"
        interval_seconds        = 10
        failure_count_threshold = 3
      }

      # Startup gives migrations and the first connection time to settle before
      # liveness starts counting failures.
      startup_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/health/live"
        interval_seconds        = 5
        failure_count_threshold = 12
      }
    }

    http_scale_rule {
      name                = "http-concurrency"
      concurrent_requests = tostring(var.api_concurrent_requests_per_replica)
    }
  }

  depends_on = [
    azurerm_role_assignment.acr_pull,
    azurerm_role_assignment.key_vault_secrets,
  ]
}

# ── Worker ───────────────────────────────────────────────────────────────────
resource "azurerm_container_app" "worker" {
  name                         = "ca-worker-${var.name_prefix}"
  resource_group_name          = var.resource_group_name
  container_app_environment_id = azurerm_container_app_environment.main.id
  revision_mode                = "Single"
  tags                         = var.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.apps.id]
  }

  registry {
    server   = var.container_registry_login_server
    identity = azurerm_user_assigned_identity.apps.id
  }

  secret {
    name                = "database-connection-string"
    key_vault_secret_id = var.database_secret_id
    identity            = azurerm_user_assigned_identity.apps.id
  }

  dynamic "secret" {
    for_each = local.rabbitmq_secret_enabled ? toset(["rabbitmq"]) : toset([])
    content {
      name                = "rabbitmq-password"
      key_vault_secret_id = var.rabbitmq_secret_id
      identity            = azurerm_user_assigned_identity.apps.id
    }
  }

  # KEDA dials the broker itself to read queue depth, so it needs its own AMQP
  # URI rather than the application's username and password.
  dynamic "secret" {
    for_each = local.rabbitmq_scaler_enabled ? toset(["scaler"]) : toset([])
    content {
      name                = "rabbitmq-scaler-connection"
      key_vault_secret_id = var.rabbitmq_scaler_connection_secret
      identity            = azurerm_user_assigned_identity.apps.id
    }
  }

  # No ingress: the worker is driven by the queue and exposes only its probe
  # port inside the environment.

  template {
    min_replicas = var.worker_min_replicas
    max_replicas = var.worker_max_replicas

    container {
      name   = "worker"
      image  = "${var.container_registry_login_server}/collaborative-media-pipeline-worker:${var.image_tag}"
      cpu    = var.worker_cpu
      memory = var.worker_memory

      env {
        name  = "DOTNET_ENVIRONMENT"
        value = var.aspnetcore_environment
      }

      env {
        name        = "ConnectionStrings__MediaPipeline"
        secret_name = "database-connection-string"
      }

      env {
        name  = "Storage__Provider"
        value = "AzureBlob"
      }

      env {
        name  = "Storage__AzureBlob__ServiceUri"
        value = var.storage_blob_endpoint
      }

      env {
        name  = "Storage__AzureBlob__ContainerName"
        value = var.assets_container_name
      }

      env {
        name  = "AZURE_CLIENT_ID"
        value = azurerm_user_assigned_identity.apps.client_id
      }

      env {
        name  = "RabbitMq__HostName"
        value = var.rabbitmq_host
      }

      env {
        name  = "RabbitMq__Port"
        value = tostring(var.rabbitmq_port)
      }

      env {
        name  = "RabbitMq__VirtualHost"
        value = var.rabbitmq_vhost
      }

      env {
        name  = "RabbitMq__UserName"
        value = var.rabbitmq_username
      }

      dynamic "env" {
        for_each = local.rabbitmq_secret_enabled ? toset(["rabbitmq"]) : toset([])
        content {
          name        = "RabbitMq__Password"
          secret_name = "rabbitmq-password"
        }
      }

      env {
        name  = "RabbitMq__UseTls"
        value = "true"
      }

      # Prefetch and concurrency together bound how much media one replica can
      # hold. Decoding is CPU- and memory-hungry, so a generous prefetch would
      # let one replica hoard the queue while its siblings sit idle.
      env {
        name  = "RabbitMq__PrefetchCount"
        value = tostring(var.worker_prefetch)
      }

      env {
        name  = "RabbitMq__MaxConcurrentMessages"
        value = tostring(var.worker_concurrency)
      }

      env {
        name  = "OpenTelemetry__OtlpEndpoint"
        value = var.otlp_endpoint
      }

      liveness_probe {
        transport               = "HTTP"
        port                    = 8081
        path                    = "/health/live"
        initial_delay           = 10
        interval_seconds        = 30
        failure_count_threshold = 3
      }

      # The broker check reports Degraded, not Unhealthy, so a broker blip takes
      # a replica out of rotation instead of restarting it into a crash loop
      # exactly when the broker is already struggling.
      readiness_probe {
        transport               = "HTTP"
        port                    = 8081
        path                    = "/health/ready"
        interval_seconds        = 15
        failure_count_threshold = 4
      }
    }

    # Scale on queue depth: the honest signal for this workload, since a worker's
    # load is how much media is waiting, not how much CPU it is currently using.
    #
    # Only wired up when the broker exposes credentials KEDA can use. Without it
    # the worker runs at a fixed replica count, which is a documented limitation
    # rather than a silent one - see docs/ARCHITECTURE.md.
    dynamic "custom_scale_rule" {
      for_each = local.rabbitmq_scaler_enabled ? toset(["scaler"]) : toset([])
      content {
        name             = "media-queue-depth"
        custom_rule_type = "rabbitmq"

        metadata = {
          queueName = "collaborative-media-pipeline.media.process"
          mode      = "QueueLength"
          value     = tostring(var.worker_queue_length_target)
          protocol  = "amqp"
        }

        authentication {
          secret_name       = "rabbitmq-scaler-connection"
          trigger_parameter = "host"
        }
      }
    }
  }

  depends_on = [
    azurerm_role_assignment.acr_pull,
    azurerm_role_assignment.key_vault_secrets,
  ]
}

resource "azurerm_role_assignment" "keyring_crypto" {
  scope                = var.key_vault_id
  role_definition_name = "Key Vault Crypto Service Encryption User"
  principal_id         = azurerm_user_assigned_identity.apps.principal_id
}
resource "azurerm_container_app" "frontend" {
  name                         = "ca-web-${var.name_prefix}"
  resource_group_name          = var.resource_group_name
  container_app_environment_id = azurerm_container_app_environment.main.id
  revision_mode                = "Single"
  tags                         = var.tags
  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.apps.id]
  }
  registry {
    server   = var.container_registry_login_server
    identity = azurerm_user_assigned_identity.apps.id
  }
  ingress {
    external_enabled = true
    target_port      = 8080
    transport        = "auto"
    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
    dynamic "ip_security_restriction" {
      for_each = toset(var.front_door_origin_cidrs)
      content {
        name             = "afd-${replace(replace(ip_security_restriction.value, ".", "-"), "/", "-")}"
        action           = "Allow"
        ip_address_range = ip_security_restriction.value
      }
    }
  }
  template {
    min_replicas = var.api_min_replicas
    max_replicas = var.api_max_replicas
    container {
      name   = "frontend"
      image  = "${var.container_registry_login_server}/collaborative-media-pipeline-frontend:${var.image_tag}"
      cpu    = 0.25
      memory = "0.5Gi"
      env {
        name  = "API_UPSTREAM"
        value = "https://${azurerm_container_app.api.ingress[0].fqdn}"
      }
      env {
        name  = "API_HOST"
        value = azurerm_container_app.api.ingress[0].fqdn
      }
      liveness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/health/live"
      }
      readiness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/health/live"
      }
    }
  }
  depends_on = [azurerm_role_assignment.acr_pull]
}
