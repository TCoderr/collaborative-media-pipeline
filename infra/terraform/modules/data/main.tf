# ─────────────────────────────────────────────────────────────────────────────
# Quire — stateful services.
#
# PostgreSQL, Blob Storage and Key Vault. Everything here holds customer work,
# so the defaults lean closed: private networking, TLS-only, no public access,
# and identity-based data-plane authorisation rather than keys.
# ─────────────────────────────────────────────────────────────────────────────

terraform {
  required_version = ">= 1.16.3, < 2.0.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 5.5"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }
}

# ── PostgreSQL Flexible Server ───────────────────────────────────────────────
resource "azurerm_postgresql_flexible_server" "main" {
  name                = "psql-${var.name_prefix}"
  resource_group_name = var.resource_group_name
  location            = var.location
  version             = "16"

  # Private DNS + delegated subnet: the server has no public endpoint at all,
  # so a leaked connection string is not by itself a path to the data.
  delegated_subnet_id           = var.database_subnet_id
  private_dns_zone_id           = azurerm_private_dns_zone.postgres.id
  public_network_access_enabled = false

  administrator_login    = var.database_admin_username
  administrator_password = random_password.database.result

  storage_mb   = var.database_storage_mb
  storage_tier = var.database_storage_tier
  sku_name     = var.database_sku

  backup_retention_days        = var.database_backup_retention_days
  geo_redundant_backup_enabled = var.database_geo_redundant_backup

  # Zone-redundant HA in production only: it doubles the compute cost, and a
  # development outage costs nobody anything.
  dynamic "high_availability" {
    for_each = var.database_high_availability ? [1] : []
    content {
      mode = "ZoneRedundant"
    }
  }

  maintenance_window {
    day_of_week  = 0 # Sunday
    start_hour   = 3
    start_minute = 0
  }

  tags = var.tags

  lifecycle {
    # Losing the server means losing every project, asset and review in it.
    prevent_destroy = true
  }

  depends_on = [azurerm_private_dns_zone_virtual_network_link.postgres]
}

resource "azurerm_postgresql_flexible_server_database" "collaborative-media-pipeline" {
  name      = "collaborative-media-pipeline"
  server_id = azurerm_postgresql_flexible_server.main.id
  collation = "en_US.utf8"
  charset   = "utf8"

  lifecycle {
    prevent_destroy = true
  }
}

# TLS is not optional: the application always connects over an encrypted channel.
resource "azurerm_postgresql_flexible_server_configuration" "require_tls" {
  name      = "require_secure_transport"
  server_id = azurerm_postgresql_flexible_server.main.id
  value     = "on"
}

# Slow-query visibility. The threshold is deliberately above the P95 the
# application targets, so the log is a signal rather than a firehose.
resource "azurerm_postgresql_flexible_server_configuration" "log_min_duration" {
  name      = "log_min_duration_statement"
  server_id = azurerm_postgresql_flexible_server.main.id
  value     = "1000"
}

resource "azurerm_private_dns_zone" "postgres" {
  name                = "${var.name_prefix}.postgres.database.azure.com"
  resource_group_name = var.resource_group_name
  tags                = var.tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "postgres" {
  name                = "pdnsz-link-${var.name_prefix}"
  private_dns_zone_id = azurerm_private_dns_zone.postgres.id
  virtual_network_id  = var.virtual_network_id
  tags                = var.tags
}

# The generated password is written straight to Key Vault and never surfaces in
# a tfvars file or a pipeline variable.
resource "random_password" "database" {
  length           = 40
  special          = true
  override_special = "!#$%&*()-_=+[]{}<>:?"
}

# ── Blob Storage ─────────────────────────────────────────────────────────────
resource "azurerm_storage_account" "media" {
  name                = replace("st${var.name_prefix}", "-", "")
  resource_group_name = var.resource_group_name
  location            = var.location

  account_tier             = "Standard"
  account_replication_type = var.storage_replication
  account_kind             = "StorageV2"

  # Every one of these closes a default that would otherwise expose media.
  https_traffic_only_enabled      = true
  min_tls_version                 = "TLS1_2"
  allow_nested_items_to_be_public = false
  public_network_access           = "Disabled"
  shared_access_key_enabled       = false # reject Shared Key authorization
  default_to_oauth_authentication = true

  blob_properties {
    versioning_enabled = true

    delete_retention_policy {
      days = var.blob_soft_delete_days
    }

    container_delete_retention_policy {
      days = var.blob_soft_delete_days
    }
  }

  tags = var.tags

  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_storage_container" "assets" {
  name                  = "assets"
  storage_account_id    = azurerm_storage_account.media.id
  container_access_type = "private" # nothing is ever served straight from storage
}

# Derivatives are regenerable from the source, so cold-tiering them after a
# quiet period costs nothing but a reprocess if they are ever needed again.
resource "azurerm_storage_management_policy" "lifecycle" {
  storage_account_id = azurerm_storage_account.media.id

  rule {
    name    = "cool-old-derivatives"
    enabled = true

    filters {
      blob_types   = ["blockBlob"]
      prefix_match = ["assets/projects"]
    }

    actions {
      base_blob {
        tier_to_cool_after_days_since_modification_greater_than = var.blob_cool_after_days
      }

      version {
        delete_after_days_since_creation = var.blob_version_retention_days
      }
    }
  }
}

resource "azurerm_private_endpoint" "storage" {
  name                = "pe-st-${var.name_prefix}"
  resource_group_name = var.resource_group_name
  location            = var.location
  subnet_id           = var.private_endpoint_subnet_id
  tags                = var.tags

  private_service_connection {
    name                           = "psc-st-${var.name_prefix}"
    private_connection_resource_id = azurerm_storage_account.media.id
    subresource_names              = ["blob"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "blob"
    private_dns_zone_ids = [azurerm_private_dns_zone.blob.id]
  }
}

resource "azurerm_private_dns_zone" "blob" {
  name                = "privatelink.blob.core.windows.net"
  resource_group_name = var.resource_group_name
  tags                = var.tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "blob" {
  name                = "pdnsz-blob-link-${var.name_prefix}"
  private_dns_zone_id = azurerm_private_dns_zone.blob.id
  virtual_network_id  = var.virtual_network_id
  tags                = var.tags
}

# ── Key Vault ────────────────────────────────────────────────────────────────
# Holds only what managed identity cannot replace: the database password and the
# external RabbitMQ credentials. Storage needs no secret at all.
resource "azurerm_key_vault" "main" {
  name                = "kv-${substr(replace(var.name_prefix, "-", ""), 0, 20)}"
  resource_group_name = var.resource_group_name
  location            = var.location
  tenant_id           = var.tenant_id

  sku_name                      = "standard"
  rbac_authorization_enabled    = true # RBAC, not access policies
  purge_protection_enabled      = var.key_vault_purge_protection
  soft_delete_retention_days    = 7
  public_network_access_enabled = false

  network_acls {
    bypass         = "AzureServices"
    default_action = "Deny"
  }

  tags = var.tags
}

resource "azurerm_private_endpoint" "key_vault" {
  name                = "pe-kv-${var.name_prefix}"
  resource_group_name = var.resource_group_name
  location            = var.location
  subnet_id           = var.private_endpoint_subnet_id
  tags                = var.tags

  private_service_connection {
    name                           = "psc-kv-${var.name_prefix}"
    private_connection_resource_id = azurerm_key_vault.main.id
    subresource_names              = ["vault"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "vault"
    private_dns_zone_ids = [azurerm_private_dns_zone.key_vault.id]
  }
}

resource "azurerm_private_dns_zone" "key_vault" {
  name                = "privatelink.vaultcore.azure.net"
  resource_group_name = var.resource_group_name
  tags                = var.tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "key_vault" {
  name                = "pdnsz-kv-link-${var.name_prefix}"
  private_dns_zone_id = azurerm_private_dns_zone.key_vault.id
  virtual_network_id  = var.virtual_network_id
  tags                = var.tags
}

resource "azurerm_key_vault_secret" "database_connection_string" {
  name         = "ConnectionStrings--MediaPipeline"
  key_vault_id = azurerm_key_vault.main.id

  value = join(";", [
    "Host=${azurerm_postgresql_flexible_server.main.fqdn}",
    "Port=5432",
    "Database=${azurerm_postgresql_flexible_server_database.collaborative-media-pipeline.name}",
    "Username=${var.database_admin_username}",
    "Password=${random_password.database.result}",
    "SSL Mode=VerifyFull",
    "Maximum Pool Size=${var.database_max_pool_size}",
  ])

  content_type = "text/plain"
  tags         = var.tags
}

# RabbitMQ is the one dependency that still needs a stored credential: it is not
# an Azure resource, so there is no managed identity to use.
# See docs/ARCHITECTURE.md.
resource "azurerm_key_vault_secret" "rabbitmq_password" {
  count = var.rabbitmq_password == null ? 0 : 1

  name         = "RabbitMq--Password"
  key_vault_id = azurerm_key_vault.main.id
  value        = var.rabbitmq_password
  content_type = "text/plain"
  tags         = var.tags
}

resource "azurerm_storage_container" "keyring" {
  name                  = "keyring"
  storage_account_id    = azurerm_storage_account.media.id
  container_access_type = "private"
}
resource "azurerm_key_vault_key" "keyring" {
  name         = "session-keyring"
  key_vault_id = azurerm_key_vault.main.id
  key_type     = "RSA"
  key_size     = 3072
  key_opts     = ["wrapKey", "unwrapKey"]
  rotation_policy {
    automatic { time_before_expiry = "P30D" }
    expire_after         = "P90D"
    notify_before_expiry = "P29D"
  }
  lifecycle { prevent_destroy = true }
}
