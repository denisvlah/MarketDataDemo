# Input variables for the Terraform configuration

variable "location" {
  description = "Azure region where resources will be deployed"
  type        = string
  default     = "eastus"
}

variable "resource_group_name" {
  description = "Name of the Azure resource group"
  type        = string
  default     = "market-data-demo-rg"
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
  description = "Base name for container app and environment"
  type        = string
  default     = "market-data-demo-api"
}
