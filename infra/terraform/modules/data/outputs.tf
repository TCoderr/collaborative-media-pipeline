output "postgres_fqdn" {
  description = "Private FQDN of the Flexible Server; resolvable only inside the VNet."
  value       = azurerm_postgresql_flexible_server.main.fqdn
}

output "postgres_server_id" {
  value = azurerm_postgresql_flexible_server.main.id
}

output "storage_account_id" {
  value = azurerm_storage_account.media.id
}

output "storage_account_name" {
  value = azurerm_storage_account.media.name
}

output "storage_blob_endpoint" {
  value = azurerm_storage_account.media.primary_blob_endpoint
}

output "assets_container_name" {
  value = azurerm_storage_container.assets.name
}

output "key_vault_id" {
  value = azurerm_key_vault.main.id
}

output "key_vault_uri" {
  value = azurerm_key_vault.main.vault_uri
}

output "database_secret_name" {
  description = "Key Vault secret the container apps reference for the connection string."
  value       = azurerm_key_vault_secret.database_connection_string.name
}

output "database_secret_id" {
  value     = azurerm_key_vault_secret.database_connection_string.versionless_id
  sensitive = true
}

output "rabbitmq_secret_id" {
  value     = try(azurerm_key_vault_secret.rabbitmq_password[0].versionless_id, null)
  sensitive = true
}

output "keyring_blob_uri" {
  value = "${azurerm_storage_account.media.primary_blob_endpoint}${azurerm_storage_container.keyring.name}/keys.xml"
}
output "keyring_key_uri" { value = azurerm_key_vault_key.keyring.versionless_id }
