# Input variables for the Terraform configuration

variable "location" {
  description = "Azure region where resources will be deployed"
  type        = string
}

variable "resource_group_base_name" {
  description = "Base name for the resource group"
  type        = string
  default     = "market-data-demo-rg"
}

variable "image_tag" {
  description = "Tag of the Docker image to deploy"
  type        = string
  default     = "latest"
}

variable "env_suffix" {
  description = "Suffix for environment to make storage account name unique"
  type        = string
  default     = ""
}

variable "base_name" {
  description = "Base name for container app and environment"
  type        = string
  default     = "market-data-demo-api"
}