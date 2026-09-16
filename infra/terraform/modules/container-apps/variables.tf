variable "name_prefix" {
  type = string
}

variable "resource_group_name" {
  type = string
}

variable "location" {
  type = string
}

variable "container_apps_subnet_id" {
  type = string
}

variable "log_analytics_workspace_id" {
  type = string
}

variable "internal_only" {
  description = "Keeps the environment's ingress inside the VNet. Production fronts it with Front Door instead."
  type        = bool
  default     = false
}

# ── Image ────────────────────────────────────────────────────────────────────
variable "container_registry_id" {
  type = string
}

variable "container_registry_login_server" {
  type = string
}

variable "image_tag" {
  description = <<-EOT
    Immutable image identifier. Build once, promote the same artefact: a commit
    SHA or a digest, never "latest". Re-resolving a tag per environment means the
    thing tested in staging is not provably the thing running in production.
  EOT
  type        = string

  validation {
    condition     = var.image_tag != "latest"
    error_message = "image_tag must be an immutable identifier (commit SHA or digest), never 'latest'."
  }
}

# ── Data plane ───────────────────────────────────────────────────────────────
variable "storage_account_id" {
  type = string
}

variable "storage_blob_endpoint" {
  type = string
}

variable "assets_container_name" {
  type = string
}

variable "key_vault_id" {
  type = string
}

variable "database_secret_id" {
  type      = string
  sensitive = true
}

# ── Broker ───────────────────────────────────────────────────────────────────
variable "rabbitmq_host" {
  description = "Managed RabbitMQ endpoint. Not an Azure resource; see docs/ARCHITECTURE.md."
  type        = string
}

variable "rabbitmq_port" {
  type    = number
  default = 5671 # AMQPS
}

variable "rabbitmq_vhost" {
  type    = string
  default = "collaborative-media-pipeline"
}

variable "rabbitmq_username" {
  type = string
}

variable "rabbitmq_secret_id" {
  description = "Key Vault secret holding the broker password."
  type        = string
  default     = null
  sensitive   = true
}

variable "rabbitmq_scaler_connection_secret" {
  description = <<-EOT
    Key Vault secret holding an AMQP URI for KEDA. Separate from the application
    credential because the scaler dials the broker itself. When unset the worker
    runs at a fixed replica count instead of scaling on queue depth.
  EOT
  type        = string
  default     = null
  sensitive   = true
}

# ── API sizing ───────────────────────────────────────────────────────────────
variable "api_cpu" {
  type    = number
  default = 0.5
}

variable "api_memory" {
  type    = string
  default = "1Gi"
}

variable "api_min_replicas" {
  description = "Zero is fine for dev; production keeps at least two so a revision restart is never a full outage."
  type        = number
  default     = 1
}

variable "api_max_replicas" {
  type    = number
  default = 5
}

variable "api_concurrent_requests_per_replica" {
  type    = number
  default = 60
}

# ── Worker sizing ────────────────────────────────────────────────────────────
variable "worker_cpu" {
  description = "Image decoding is CPU-bound; a full core per replica keeps processing time predictable."
  type        = number
  default     = 1.0
}

variable "worker_memory" {
  description = "Must hold the decoded bitmap plus the encoder's working set, not just the compressed file."
  type        = string
  default     = "2Gi"
}

variable "worker_min_replicas" {
  type    = number
  default = 1
}

variable "worker_max_replicas" {
  type    = number
  default = 6
}

variable "worker_prefetch" {
  description = "Unacked messages one replica may hold. Small on purpose: a large prefetch lets one replica hoard the queue."
  type        = number
  default     = 4

  validation {
    condition     = var.worker_prefetch >= 1 && var.worker_prefetch <= 32
    error_message = "worker_prefetch must be between 1 and 32; larger values starve sibling replicas."
  }
}

variable "worker_concurrency" {
  description = "Messages processed in parallel inside one replica. Should not exceed worker_cpu by much."
  type        = number
  default     = 2
}

variable "worker_queue_length_target" {
  description = "Messages waiting per replica before KEDA adds another."
  type        = number
  default     = 10
}

# ── Misc ─────────────────────────────────────────────────────────────────────
variable "aspnetcore_environment" {
  type    = string
  default = "Production"
}

variable "otlp_endpoint" {
  description = "OTLP collector endpoint. Vendor-neutral: swapping the backend is configuration, not code."
  type        = string
  default     = ""
}

variable "web_origin" {
  description = "Origin serving the frontend, allowed through CORS with credentials."
  type        = string
}

variable "front_door_id" {
  description = "When set, the app accepts traffic only from Front Door so the WAF cannot be bypassed."
  type        = string
  default     = null
}

variable "tags" {
  type    = map(string)
  default = {}
}

variable "keyring_blob_uri" { type = string }
variable "keyring_key_uri" { type = string }
variable "migration_database_secret_id" {
  type      = string
  sensitive = true
}
variable "front_door_origin_cidrs" {
  description = "Reviewed current AzureFrontDoor.Backend CIDRs. Required to restrict public frontend ingress."
  type        = list(string)
  validation {
    condition     = length(var.front_door_origin_cidrs) > 0 && alltrue([for cidr in var.front_door_origin_cidrs : can(cidrnetmask(cidr)) && cidr != "0.0.0.0/0"])
    error_message = "Supply explicit Front Door backend IPv4 CIDRs; unrestricted ingress is prohibited."
  }
}
