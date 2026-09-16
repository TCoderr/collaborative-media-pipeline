variable "name_prefix" {
  type = string
}

variable "resource_group_name" {
  type = string
}

variable "location" {
  type = string
}

variable "log_retention_days" {
  type    = number
  default = 30
}

variable "sampling_percentage" {
  description = "Ingestion sampling. 100 in dev where volume is trivial; lower in production for cost."
  type        = number
  default     = 100
}

variable "alert_email_addresses" {
  description = "On-call recipients. Empty disables the action group and every alert with it."
  type        = list(string)
  default     = []
}

variable "outbox_age_alert_seconds" {
  description = "Oldest pending outbox row before alerting. Must exceed the publisher's maximum backoff (300s)."
  type        = number
  default     = 600

  validation {
    condition     = var.outbox_age_alert_seconds > 300
    error_message = "Must exceed the outbox publisher's 300s maximum backoff, or normal retries would page someone."
  }
}

variable "dead_letter_alert_threshold" {
  description = "Permanent processing failures in a 15-minute window before alerting."
  type        = number
  default     = 5
}

variable "api_error_rate_slo_percent" {
  description = "5xx percentage that breaches the availability objective."
  type        = number
  default     = 1
}

variable "tags" {
  type    = map(string)
  default = {}
}
