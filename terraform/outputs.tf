# Output values for the Terraform configuration

output "container_app_url" {
  description = "URL of the deployed container app"
  value       = "https://${azurerm_container_app.main.ingress[0].fqdn}"
}

output "container_app_fqdn" {
  description = "FQDN of the deployed container app"
  value       = azurerm_container_app.main.ingress[0].fqdn
}

output "storage_account_name" {
  description = "Name of the created storage account for candles data"
  value       = azurerm_storage_account.candles.name
}

output "storage_container_name" {
  description = "Name of the storage container for candles data"
  value       = azurerm_storage_container.candles_data.name
}

output "resource_group_name" {
  description = "Name of the resource group"
  value       = azurerm_resource_group.main.name
}
