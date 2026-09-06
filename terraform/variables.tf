# Input variables for the Terraform configuration

variable "location" {
  description = "Azure region where all resources (Resource Group, Container App, Static Web App, Storage Account) will be deployed"
  type        = string
  default     = "westeurope"
}

variable "resource_group_name" {
  description = "Name of the Azure resource group for all resources"
  type        = string
  default     = "market-data-demo-app-rg"
}

variable "image_tag" {
  description = "Tag of the Docker image to deploy"
  type        = string
  default     = "latest"
}

variable "env_suffix" {
  description = "Suffix for environment to make names unique"
  type        = string
  default     = ""
}

variable "base_name" {
  description = "Base name for container app, static web app, and environment"
  type        = string
  default     = "market-data-demo-api"
}

variable "static_web_app_sku_tier" {
  description = "SKU tier for the Azure Static Web App (Free or Standard)"
  type        = string
  default     = "Free"
}

variable "static_web_app_sku_size" {
  description = "SKU size for the Azure Static Web App (Free or Standard)"
  type        = string
  default     = "Free"
}

variable "additional_cors_origins" {
  description = "List of additional allowed CORS origins for the Container App API (in addition to the Static Web App origin)"
  type        = list(string)
  default     = ["http://localhost:5173", "http://localhost:3000"]
}
