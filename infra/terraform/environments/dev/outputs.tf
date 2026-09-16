output "front_door_hostname" {
  description = "The public entry point."
  value       = azurerm_cdn_frontdoor_endpoint.main.host_name
}

output "api_internal_fqdn" {
  description = "Container Apps FQDN. Reachable only from Front Door; useful for diagnosis."
  value       = module.compute.api_fqdn
}

output "migration_job_name" {
  description = "Run before every rollout: az containerapp job start --name <this> -g <rg>."
  value       = module.compute.migration_job_name
}

output "container_registry" {
  value = azurerm_container_registry.main.login_server
}

output "key_vault_uri" {
  value = module.data.key_vault_uri
}

output "postgres_fqdn" {
  description = "Private FQDN; resolvable only inside the VNet."
  value       = module.data.postgres_fqdn
}
