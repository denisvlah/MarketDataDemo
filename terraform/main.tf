# Core infrastructure configuration
terraform {
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 3.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.0"
    }
  }
}

provider "azurerm" {
  features {}
}

resource "random_id" "suffix" {
  byte_length = 4
}

# Create resource group
resource "azurerm_resource_group" "main" {
  name     = "${var.resource_group_base_name}-${random_id.suffix.hex}"
  location = var.location
}



# Container app environment
resource "azurerm_container_app_environment" "main" {
  name                = "${var.base_name}-env${var.env_suffix != "" ? "-${var.env_suffix}" : ""}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
}

# Container app with exact CPU/RAM specs
resource "azurerm_container_app" "main" {
  name                         = "${var.base_name}${var.env_suffix != "" ? "-${var.env_suffix}" : ""}"
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = azurerm_resource_group.main.name
  revision_mode                = "Single"

  template {
    container {
      name   = "mddemo-api"
      image  = "vlahdenis/marketdatademo.api:${var.image_tag}"
      cpu    = 0.5
      memory = "1Gi"

      # Added environment variable
      env {
        name  = "StorageType"
        value = "azure"
      }
    }
  }

  identity {
    type = "SystemAssigned"
  }
}

# Blob storage with specific configuration
resource "azurerm_storage_account" "candles" {
  name                     = "candlesdata${random_id.suffix.hex}"
  resource_group_name      = azurerm_resource_group.main.name
  location                 = azurerm_resource_group.main.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
}

resource "azurerm_storage_container" "candles_data" {
  name                  = "candles-data"
  storage_account_name  = azurerm_storage_account.candles.name
  container_access_type = "private"
}

# Storage data contributor role (read/write/list/delete)
resource "azurerm_role_assignment" "storage_access" {
  scope                = azurerm_storage_account.candles.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_container_app.main.identity[0].principal_id
}