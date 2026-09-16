variable "name_prefix" {
  description = "Short, unique prefix for every resource name in this environment."
  type        = string
}

variable "resource_group_name" {
  type = string
}

variable "location" {
  type = string
}

variable "tenant_id" {
  description = "Entra tenant that owns the Key Vault."
  type        = string
}

variable "virtual_network_id" {
  type = string
}

variable "database_subnet_id" {
  description = "Subnet delegated to PostgreSQL Flexible Server."
  type        = string
}

variable "private_endpoint_subnet_id" {
  description = "Subnet holding the storage and Key Vault private endpoints."
  type        = string
}

# ── Database sizing ──────────────────────────────────────────────────────────
variable "database_admin_username" {
  type    = string
  default = "collaborative-media-pipelineadmin"
}

variable "database_sku" {
  description = "Flexible Server SKU. Burstable is right for dev; production needs sustained CPU."
  type        = string
  default     = "B_Standard_B1ms"
}

variable "database_storage_mb" {
  type    = number
  default = 32768
}

variable "database_storage_tier" {
  type    = string
  default = "P4"
}

variable "database_backup_retention_days" {
  type    = number
  default = 7

  validation {
    condition     = var.database_backup_retention_days >= 7
    error_message = "Backup retention below 7 days leaves no room to notice a problem before the backup expires."
  }
}

variable "database_geo_redundant_backup" {
  type    = bool
  default = false
}

variable "database_high_availability" {
  description = "Zone-redundant HA. Doubles compute cost, so production only."
  type        = bool
  default     = false
}

variable "database_max_pool_size" {
  description = "Npgsql pool ceiling per replica. Replicas x pool must stay under the server's connection limit."
  type        = number
  default     = 20
}

# ── Storage ──────────────────────────────────────────────────────────────────
variable "storage_replication" {
  type    = string
  default = "LRS"

  validation {
    condition     = contains(["LRS", "ZRS", "GRS", "GZRS"], var.storage_replication)
    error_message = "storage_replication must be one of LRS, ZRS, GRS, GZRS."
  }
}

variable "blob_soft_delete_days" {
  description = "Window in which a deleted blob or container can still be recovered."
  type        = number
  default     = 14
}

variable "blob_cool_after_days" {
  type    = number
  default = 90
}

variable "blob_version_retention_days" {
  type    = number
  default = 180
}

# ── Key Vault ────────────────────────────────────────────────────────────────
variable "key_vault_purge_protection" {
  description = "Blocks permanent deletion during the soft-delete window. Must be on in production."
  type        = bool
  default     = false
}

variable "rabbitmq_password" {
  description = "External RabbitMQ password, supplied by the pipeline from its own secret store. Never in a tfvars file."
  type        = string
  default     = null
  sensitive   = true
}

variable "tags" {
  type    = map(string)
  default = {}
}
