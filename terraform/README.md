# Azure Container Apps & Infrastructure Deployment

This folder contains the Terraform configuration and automated setup script for provisioning the MarketDataDemo API on Azure Container Apps with Azure Blob Storage backend.

## Infrastructure Architecture

- **Azure Container App**: 0.5 vCPU / 1.0 GiB RAM running the .NET 10 Native AOT API
- **Ingress**: External HTTPS enabled on port 8080
- **Azure Blob Storage**: Storage account (`candlesdata<suffix>`) and container (`candles-data`)
- **Managed Identity & RBAC**: System-Assigned Managed Identity on the Container App with `Storage Blob Data Contributor` access to the storage account
- **Remote State**: Terraform state stored securely in an Azure Blob Storage container (`tfstate`)

---

## 🚀 Automated Azure & GitHub OIDC Setup

A re-entrant script is provided to configure your Azure subscription, Azure AD App Registration, GitHub OIDC federated credentials, and Terraform state storage:

```bash
cd terraform
./setup-azure-oidc.sh
```

### Script Options

```bash
./setup-azure-oidc.sh [options]

Options:
  -r, --repo <org/repo>          GitHub repository (default: auto-detected or denisvlah/MarketDataDemo)
  -b, --branch <branch>          GitHub branch for OIDC subject (default: main)
  -a, --app-name <name>          Azure AD App registration name (default: marketdata-demo-github-actions)
  -g, --resource-group <name>    Azure Resource Group name (default: market-data-demo-rg)
  -l, --location <region>        Azure region (default: eastus)
  -s, --subscription <id>        Azure Subscription ID (default: current active subscription)
  -h, --help                     Show help message
```

The script is safe to run multiple times. It will:
1. Detect/create Azure AD Application and Service Principal.
2. Create or verify Federated Identity Credentials for GitHub OIDC (`repo:<org>/<repo>:ref:refs/heads/<branch>`).
3. Ensure the Resource Group exists and assign required RBAC roles (`Contributor` and RBAC/User Access Administrator).
4. Create an Azure Storage Account and `tfstate` container for Terraform state persistence.
5. Output the exact secrets to configure in GitHub.
6. (Optional) If GitHub CLI (`gh`) is authenticated, automatically upload secrets to GitHub.

---

## Required GitHub Secrets

Configure these secrets in your repository under **Settings > Secrets and variables > Actions**:

| Secret Name | Description | Source / Example |
|---|---|---|
| `AZURE_CLIENT_ID` | Azure AD Application (Client) ID | Output by `setup-azure-oidc.sh` |
| `AZURE_TENANT_ID` | Azure AD Tenant ID | Output by `setup-azure-oidc.sh` |
| `AZURE_SUBSCRIPTION_ID` | Azure Subscription ID | Output by `setup-azure-oidc.sh` |
| `AZURE_RG` | Azure Resource Group Name | `market-data-demo-rg` |
| `AZURE_LOCATION` | Azure Region | `eastus` |
| `TF_STATE_STORAGE_ACCOUNT_NAME` | Storage Account for Terraform state | Output by `setup-azure-oidc.sh` |
| `TF_STATE_CONTAINER_NAME` | Blob Container for Terraform state | `tfstate` |
| `DOCKERHUB_USERNAME` | Docker Hub Username | `vlahdenis` |
| `DOCKERHUB_TOKEN` | Docker Hub Personal Access Token | From Docker Hub Account Settings |

---

## GitHub Actions Deployment Workflow

The workflow at [`.github/workflows/deploy.yaml`](../.github/workflows/deploy.yaml) performs:
1. **Docker Build & Push**: Builds the native AOT container image from [`Dockerfile.api`](../Dockerfile.api) and pushes to Docker Hub.
2. **Azure OIDC Authentication**: Authenticates to Azure with short-lived tokens (no static client secrets).
3. **Terraform Init & Apply**: Initializes Terraform with the Azure Blob remote backend and applies the configuration, passing the built image tag.

---

## Terraform Variables

| Variable | Description | Default |
|---|---|---|
| `location` | Azure region | `eastus` |
| `resource_group_name` | Resource Group name | `market-data-demo-rg` |
| `image_tag` | Docker image tag to deploy | `latest` |
| `base_name` | Base name for Container App & Environment | `market-data-demo-api` |
| `env_suffix` | Optional environment suffix | `""` |
