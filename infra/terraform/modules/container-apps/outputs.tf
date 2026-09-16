output "api_fqdn" {
  value = azurerm_container_app.api.ingress[0].fqdn
}

output "api_app_id" {
  value = azurerm_container_app.api.id
}

output "worker_app_id" {
  value = azurerm_container_app.worker.id
}

output "migration_job_name" {
  description = "Run before every rollout: az containerapp job start --name <this>."
  value       = azurerm_container_app_job.migrate.name
}

output "identity_principal_id" {
  value = azurerm_user_assigned_identity.apps.principal_id
}

output "identity_client_id" {
  value = azurerm_user_assigned_identity.apps.client_id
}

output "environment_id" {
  value = azurerm_container_app_environment.main.id
}

output "frontend_fqdn" { value = azurerm_container_app.frontend.ingress[0].fqdn }
