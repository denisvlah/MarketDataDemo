# Output values for the Terraform configuration

output "container_app_url" {
  description = "URL of the deployed container app"
  value       = azurerm_container_app.main.ingress[0].fqdn
}

output "storage_account_name" {
  description = "Name of the created storage account for candles data"
  value       = azurerm_storage_account.candles.name
}