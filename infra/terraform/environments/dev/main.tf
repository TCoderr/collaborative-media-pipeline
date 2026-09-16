# ─────────────────────────────────────────────────────────────────────────────
# Quire — dev.
#
# The same modules as production, sized down. Nothing here is HA, geo-redundant
# or zone-redundant: a dev outage costs nobody anything, and paying for
# resilience nobody needs is a cost, not a virtue.
#
# terraform plan is safe to run. terraform apply is an explicit human action.
# ─────────────────────────────────────────────────────────────────────────────

terraform {
  required_version = ">= 1.16.3, < 2.0.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 5.5"
    }
  }

  # Remote state with locking. Production state must never live on a laptop:
  # two people applying at once against local state is how infrastructure gets
  # silently diverged.
  #
  # Configured at init time so no subscription id is committed:
  #   terraform init \
  #     -backend-config="resource_group_name=rg-tfstate" \
  #     -backend-config="storage_account_name=sttfstatecmp" \
  #     -backend-config="container_name=tfstate" \
  #     -backend-config="key=collaborative-media-pipeline/dev.tfstate"
  backend "azurerm" {
    use_azuread_auth = true
  }
}

provider "azurerm" {
  # Register required Azure resource providers separately during subscription setup.
  resource_provider_registrations = "none"
  storage_use_azuread             = true

  features {
    key_vault {
      # Soft-deleted vaults are recoverable; purging one is not.
      purge_soft_delete_on_destroy = false
    }

    resource_group {
      # Refuse to delete a resource group that still contains anything.
      prevent_deletion_if_contains_resources = true
    }
  }
}

data "azurerm_client_config" "current" {}

locals {
  name_prefix = "cmp-dev"

  tags = {
    application = "collaborative-media-pipeline"
    environment = "dev"
    managed_by  = "terraform"
    repository  = "collaborative-media-pipeline"
  }
}

resource "azurerm_resource_group" "main" {
  name     = "rg-${local.name_prefix}"
  location = var.location
  tags     = local.tags
}

resource "azurerm_container_registry" "main" {
  name                = replace("cr${local.name_prefix}", "-", "")
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  sku                 = "Standard"

  # Pulls use the workload's managed identity; there is no admin credential.
  admin_enabled = false

  tags = local.tags
}

module "network" {
  source = "../../modules/network"

  name_prefix         = local.name_prefix
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  address_space       = var.address_space
  tags                = local.tags
}

module "observability" {
  source = "../../modules/observability"

  name_prefix           = local.name_prefix
  resource_group_name   = azurerm_resource_group.main.name
  location              = azurerm_resource_group.main.location
  log_retention_days    = 30
  sampling_percentage   = 100
  alert_email_addresses = var.alert_email_addresses
  tags                  = local.tags
}

module "data" {
  source = "../../modules/data"

  name_prefix                = local.name_prefix
  resource_group_name        = azurerm_resource_group.main.name
  location                   = azurerm_resource_group.main.location
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  virtual_network_id         = module.network.virtual_network_id
  database_subnet_id         = module.network.database_subnet_id
  private_endpoint_subnet_id = module.network.private_endpoint_subnet_id

  # Production sizing: sustained CPU rather than burstable credits, HA across
  # zones, geo-redundant backups, and a month of point-in-time recovery.
  # Burstable is right here: development load is a handful of requests a day.
  database_sku                   = "B_Standard_B1ms"
  database_storage_mb            = 32768
  database_storage_tier          = "P4"
  database_high_availability     = false
  database_backup_retention_days = 7
  database_geo_redundant_backup  = false
  database_max_pool_size         = 10

  storage_replication        = "LRS"
  blob_soft_delete_days      = 7
  key_vault_purge_protection = false

  rabbitmq_password = var.rabbitmq_password
  tags              = local.tags
}

module "compute" {
  source = "../../modules/container-apps"

  name_prefix                = local.name_prefix
  resource_group_name        = azurerm_resource_group.main.name
  location                   = azurerm_resource_group.main.location
  container_apps_subnet_id   = module.network.container_apps_subnet_id
  log_analytics_workspace_id = module.observability.log_analytics_workspace_id

  container_registry_id           = azurerm_container_registry.main.id
  container_registry_login_server = azurerm_container_registry.main.login_server
  image_tag                       = var.image_tag

  storage_account_id           = module.data.storage_account_id
  storage_blob_endpoint        = module.data.storage_blob_endpoint
  assets_container_name        = module.data.assets_container_name
  key_vault_id                 = module.data.key_vault_id
  database_secret_id           = var.runtime_database_secret_id
  migration_database_secret_id = module.data.database_secret_id
  keyring_blob_uri             = module.data.keyring_blob_uri
  keyring_key_uri              = module.data.keyring_key_uri
  front_door_origin_cidrs      = var.front_door_origin_cidrs

  rabbitmq_host                     = var.rabbitmq_host
  rabbitmq_username                 = var.rabbitmq_username
  rabbitmq_secret_id                = module.data.rabbitmq_secret_id
  rabbitmq_scaler_connection_secret = var.rabbitmq_scaler_secret_id

  # At least two API replicas so a revision restart is a rolling change rather
  # than a gap in service.
  api_min_replicas = 1
  api_max_replicas = 3
  api_cpu          = 0.5
  api_memory       = "1Gi"

  # The worker holds a decoded bitmap plus an encoder working set per in-flight
  # message, so memory is sized against concurrency, not file size.
  worker_min_replicas = 1
  worker_max_replicas = 2
  worker_cpu          = 1.0
  worker_memory       = "2Gi"
  worker_prefetch     = 4
  worker_concurrency  = 2

  aspnetcore_environment = "Production"
  otlp_endpoint          = var.otlp_endpoint
  web_origin             = var.web_origin
  front_door_id          = azurerm_cdn_frontdoor_profile.main.resource_guid

  tags = local.tags
}

# ── Front Door ───────────────────────────────────────────────────────────────
# The only public entry point: TLS termination, WAF, and one origin group in
# front of the API.
resource "azurerm_cdn_frontdoor_profile" "main" {
  name                = "afd-${local.name_prefix}"
  resource_group_name = azurerm_resource_group.main.name
  sku_name            = "Premium_AzureFrontDoor" # Premium is required for the managed WAF
  tags                = local.tags
}

resource "azurerm_cdn_frontdoor_endpoint" "main" {
  name                     = "fde-${local.name_prefix}"
  cdn_frontdoor_profile_id = azurerm_cdn_frontdoor_profile.main.id
  tags                     = local.tags
}

resource "azurerm_cdn_frontdoor_origin_group" "api" {
  name                     = "og-api"
  cdn_frontdoor_profile_id = azurerm_cdn_frontdoor_profile.main.id

  load_balancing {
    sample_size                 = 4
    successful_samples_required = 3
  }

  # Front Door checks liveness, not readiness: readiness is the Container Apps
  # probe's job, and a database blip should drain a replica, not an origin.
  health_probe {
    path                = "/health/live"
    protocol            = "Https"
    request_type        = "GET"
    interval_in_seconds = 30
  }
}

resource "azurerm_cdn_frontdoor_origin" "api" {
  name                          = "origin-api"
  cdn_frontdoor_origin_group_id = azurerm_cdn_frontdoor_origin_group.api.id
  enabled                       = true

  host_name          = module.compute.frontend_fqdn
  origin_host_header = module.compute.frontend_fqdn
  https_port         = 443
  http_port          = 80
  priority           = 1
  weight             = 1000

  certificate_name_check_enabled = true
}

resource "azurerm_cdn_frontdoor_route" "api" {
  name                          = "route-api"
  cdn_frontdoor_endpoint_id     = azurerm_cdn_frontdoor_endpoint.main.id
  cdn_frontdoor_origin_group_id = azurerm_cdn_frontdoor_origin_group.api.id
  cdn_frontdoor_origin_ids      = [azurerm_cdn_frontdoor_origin.api.id]

  supported_protocols    = ["Http", "Https"]
  patterns_to_match      = ["/*"]
  forwarding_protocol    = "HttpsOnly"
  https_redirect_enabled = true
  link_to_default_domain = true

  # Private media and SignalR frames must never be cached at the edge.

}

resource "azurerm_cdn_frontdoor_firewall_policy" "main" {
  name                = replace("waf${local.name_prefix}", "-", "")
  resource_group_name = azurerm_resource_group.main.name
  sku_name            = azurerm_cdn_frontdoor_profile.main.sku_name
  enabled             = true
  mode                = "Prevention"
  tags                = local.tags

  managed_rule {
    type    = "Microsoft_DefaultRuleSet"
    version = "2.1"
    action  = "Block"
  }

  managed_rule {
    type    = "Microsoft_BotManagerRuleSet"
    version = "1.0"
    action  = "Block"
  }
}

resource "azurerm_cdn_frontdoor_security_policy" "main" {
  name                     = "sp-${local.name_prefix}"
  cdn_frontdoor_profile_id = azurerm_cdn_frontdoor_profile.main.id

  security_policies {
    firewall {
      cdn_frontdoor_firewall_policy_id = azurerm_cdn_frontdoor_firewall_policy.main.id

      association {
        patterns_to_match = ["/*"]

        domain {
          cdn_frontdoor_domain_id = azurerm_cdn_frontdoor_endpoint.main.id
        }
      }
    }
  }
}
