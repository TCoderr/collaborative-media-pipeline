variable "name_prefix" {
  type = string
}

variable "resource_group_name" {
  type = string
}

variable "location" {
  type = string
}

variable "address_space" {
  description = "VNet CIDR. Must be at least a /22 to leave room for the Container Apps /23."
  type        = string
  default     = "10.40.0.0/22"

  validation {
    condition     = tonumber(split("/", var.address_space)[1]) <= 22
    error_message = "address_space must be /22 or larger; Container Apps alone needs a /23."
  }
}

variable "tags" {
  type    = map(string)
  default = {}
}
