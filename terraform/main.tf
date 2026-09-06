# Core infrastructure configuration
terraform {
  required_version = ">= 1.5.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.0"
    }
  }

  # Remote backend for state storage in Azure Blob Storage
  # Backend configuration supplied at runtime via -backend-config
  backend "azurerm" {}
}

provider "azurerm" {
  features {}
  use_oidc = true
}

resource "random_id" "suffix" {
  byte_length = 4
}

# Create shared resource group for all services
resource "azurerm_resource_group" "main" {
  name     = var.resource_group_name
  location = var.location
}

# Container app environment
resource "azurerm_container_app_environment" "main" {
  name                = "${var.base_name}-env${var.env_suffix != "" ? "-${var.env_suffix}" : ""}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
}

# Blob storage with specific configuration
resource "azurerm_storage_account" "candles" {
  name                     = "candlesdata${random_id.suffix.hex}"
  resource_group_name      = azurerm_resource_group.main.name
  location                 = azurerm_resource_group.main.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
  min_tls_version          = "TLS1_2"
}

resource "azurerm_storage_container" "candles_data" {
  name                  = "candles-data"
  storage_account_name  = azurerm_storage_account.candles.name
  container_access_type = "private"
}

# Azure Static Web App for Frontend UI (resides in same resource group & location)
resource "azurerm_static_web_app" "ui" {
  name                = "${var.base_name}-ui${var.env_suffix != "" ? "-${var.env_suffix}" : ""}"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  sku_tier            = var.static_web_app_sku_tier
  sku_size            = var.static_web_app_sku_size
}

# Container app with exact CPU/RAM specs, ingress, CORS policy, and managed identity
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

      env {
        name  = "StorageType"
        value = "azure"
      }

      env {
        name  = "AzureBlob__StorageAccountName"
        value = azurerm_storage_account.candles.name
      }

      env {
        name  = "AzureBlob__Container"
        value = azurerm_storage_container.candles_data.name
      }

      env {
        name  = "AzureBlob__Prefix"
        value = "candles"
      }
    }
  }

  ingress {
    external_enabled = true
    target_port      = 8080
    transport        = "auto"

    cors {
      allowed_origins = distinct(concat(
        ["https://${azurerm_static_web_app.ui.default_host_name}"],
        var.additional_cors_origins
      ))
      allowed_methods           = ["GET", "POST", "OPTIONS"]
      allowed_headers           = ["*"]
      allow_credentials_enabled = false
      max_age_in_seconds        = 3600
    }

    traffic_weight {
      percentage      = 100
      latest_revision = true
    }
  }

  identity {
    type = "SystemAssigned"
  }
}

# Storage data contributor role (read/write/list/delete)
resource "azurerm_role_assignment" "storage_access" {
  scope                = azurerm_storage_account.candles.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_container_app.main.identity[0].principal_id
}
