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

output "static_web_app_name" {
  description = "Name of the deployed Azure Static Web App"
  value       = azurerm_static_web_app.ui.name
}

output "static_web_app_url" {
  description = "URL of the deployed Azure Static Web App"
  value       = "https://${azurerm_static_web_app.ui.default_host_name}"
}

output "static_web_app_default_host_name" {
  description = "Default host name of the Azure Static Web App"
  value       = azurerm_static_web_app.ui.default_host_name
}

output "static_web_app_api_key" {
  description = "API token for deploying to the Azure Static Web App"
  value       = azurerm_static_web_app.ui.api_key
  sensitive   = true
}
