variable "location" {
  type    = string
  default = "westeurope"
}

variable "address_space" {
  type    = string
  default = "10.41.0.0/22"
}

variable "image_tag" {
  description = <<-EOT
    The exact artefact to run: a commit SHA or image digest built once and
    promoted unchanged from dev to staging to production.
  EOT
  type        = string
}

variable "web_origin" {
  description = "Origin serving the frontend, allowed through CORS with credentials."
  type        = string
}

variable "otlp_endpoint" {
  type    = string
  default = ""
}

# ── RabbitMQ ─────────────────────────────────────────────────────────────────
# Azure has no first-party managed RabbitMQ, so the broker is external (a
# managed provider such as CloudAMQP). It is deliberately NOT swapped for Azure
# Service Bus: see docs/ARCHITECTURE.md.
variable "rabbitmq_host" {
  type = string
}

variable "rabbitmq_username" {
  type = string
}

variable "rabbitmq_password" {
  description = "Injected by the pipeline from its own secret store. Never written to a tfvars file."
  type        = string
  sensitive   = true
  default     = null
}

variable "rabbitmq_scaler_secret_id" {
  description = "Key Vault secret holding the AMQP URI KEDA uses to read queue depth."
  type        = string
  sensitive   = true
  default     = null
}

variable "alert_email_addresses" {
  type    = list(string)
  default = []
}

variable "runtime_database_secret_id" {
  description = "Versionless Key Vault secret URI for a separately provisioned runtime role with DML only. Never use the migration administrator."
  type        = string
  sensitive   = true
}
variable "front_door_origin_cidrs" {
  description = "Current reviewed AzureFrontDoor.Backend IPv4 ranges."
  type        = list(string)
}
