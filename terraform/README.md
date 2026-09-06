# Azure Container Apps & Static Web App Infrastructure Deployment

This folder contains the Terraform configuration and automated setup script for provisioning the MarketDataDemo system on Azure, comprising the backend .NET API on **Azure Container Apps** with Azure Blob Storage and the frontend SPA on **Azure Static Web Apps**.

## Infrastructure Architecture

```
                                    +-----------------------------------------+
                                    |         Azure Static Web App            |
                                    |          (MarketDataDemo.UI)            |
                                    |   https://<app>.azurestaticapps.net     |
                                    +--------------------+--------------------+
                                                         |
                                        HTTPS / CORS     | (VITE_API_BASE_URL)
                                                         v
+-----------------------------------------------------------------------------+
| Azure Container App Environment                                             |
|                                                                             |
|   +---------------------------------------------------------------------+   |
|   | Azure Container App (0.5 vCPU / 1.0 GiB RAM)                        |   |
|   | - .NET 10 Native AOT API (mddemo-api)                               |   |
|   | - Ingress: Port 8080 External HTTPS                                 |   |
|   | - CORS: Allowed origins include SWA default host & local dev hosts  |   |
|   | - Managed Identity: System-Assigned                                 |   |
|   +-----------------------------------+---------------------------------+   |
+---------------------------------------|-------------------------------------+
                                        | Managed Identity RBAC
                                        | (Storage Blob Data Contributor)
                                        v
                    +---------------------------------------+
                    | Azure Blob Storage Account            |
                    | - Storage Account: candlesdata<hex>   |
                    | - Container: candles-data             |
                    | - Prefix: candles                     |
                    +---------------------------------------+
```

### Components

- **Shared Resource Group & Region**: Both the Static Web App and Container App reside in the same Resource Group (`var.resource_group_name`) and Azure Region (`var.location`).
- **Azure Static Web App**: Hosts the React/TypeScript/Vite frontend (`MarketDataDemo.UI`). Automatically provided with free SSL and global CDN distribution.
- **Azure Container App**: 0.5 vCPU / 1.0 GiB RAM running the .NET 10 Native AOT API (`MarketDataDemo.Api`).
- **Container App Ingress & CORS**: External HTTPS enabled on port 8080 with native CORS policy permitting requests from the Static Web App domain and development origins.
- **Azure Blob Storage**: Storage account (`candlesdata<suffix>`) and container (`candles-data`) for binary OHLCV `.bin` files.
- **Managed Identity & RBAC**: System-Assigned Managed Identity on the Container App with `Storage Blob Data Contributor` access to the storage account.
- **Remote State**: Terraform state stored securely in an Azure Blob Storage container (`tfstate`) within `market-data-demo-rg`.
- **Application Resource Group**: `market-data-demo-app-rg` managed and created solely by Terraform.

---

## Prerequisites

1. **Azure CLI**: Install and authenticate with `az login`
2. **Terraform**: Version `>= 1.5.0`
3. **Azure Subscription**: With Contributor and User Access Administrator permissions

---

## Quick Start (Automated Deployment)

The included [`setup-azure-resources.sh`](setup-azure-resources.sh) script handles the full bootstrap process:

```bash
chmod +x setup-azure-resources.sh
./setup-azure-resources.sh
```

What the script does:
1. Verifies Azure CLI login and prompts for subscription if multiple exist
2. Creates the base Resource Group for Terraform remote state (`market-data-demo-rg` in `westeurope`)
3. Creates a unique Storage Account and `tfstate` blob container with TLS 1.2 and blob versioning
4. Creates a GitHub OIDC Federated Identity Credential on your Azure AD App (no secrets needed)
5. Assigns `Contributor` and `User Access Administrator` roles on the subscription to the service principal
6. Automatically creates/updates `.env` with all necessary GitHub Secrets values

---

## Required GitHub Repository Secrets

Configure these secrets in your repository under **Settings > Secrets and variables > Actions**:

| Secret Name | Description | Example |
|---|---|---|
| `AZURE_CLIENT_ID` | Application (client) ID of Azure App Registration | `00000000-0000-0000-0000-000000000000` |
| `AZURE_TENANT_ID` | Directory (tenant) ID of Azure tenant | `00000000-0000-0000-0000-000000000000` |
| `AZURE_SUBSCRIPTION_ID` | Azure Subscription ID | `00000000-0000-0000-0000-000000000000` |
| `TF_STATE_STORAGE_ACCOUNT_NAME` | Storage account holding Terraform state | `tfstatemddemo1234abcd` |
| `TF_STATE_CONTAINER_NAME` | Blob container name for tfstate | `tfstate` |
| `TF_STATE_RESOURCE_GROUP_NAME` | Resource group for state storage account | `market-data-demo-rg` |
| `DOCKERHUB_USERNAME` | Docker Hub username for pushing images | `vlahdenis` |
| `DOCKERHUB_TOKEN` | Docker Hub Personal Access Token | `dckr_pat_...` |

Optional override secrets:
- `AZURE_APP_RG`: Override default application Resource Group (default: `market-data-demo-app-rg`)
- `AZURE_LOCATION`: Override default Azure region (default: `westeurope`)

---

## GitHub Actions Deployment Workflow

The workflow at [`.github/workflows/deploy.yaml`](../.github/workflows/deploy.yaml) executes the complete build and deployment pipeline:
1. **Docker Build & Push**: Builds the native AOT container image from [`Dockerfile.api`](../Dockerfile.api) and pushes to Docker Hub.
2. **Azure OIDC Authentication**: Authenticates to Azure with short-lived tokens (no static client secrets).
3. **Terraform Init & Apply**: Initializes Terraform with the Azure Blob remote backend (`market-data-demo-rg`) and applies the infrastructure plan:
   - Provisions shared resource group `market-data-demo-app-rg` in the configured `location`.
   - Provisions Azure Static Web App in the same resource group and location.
   - Provisions Azure Container App with ingress CORS rules allowing the Static Web App origin.
   - Provisions Blob Storage account & container with RBAC role assignments.
4. **Terraform Output Resolution**: Reads the deployed Container App URL and Static Web App deployment token (`static_web_app_api_key`).
5. **Frontend UI Build**: Builds `MarketDataDemo.UI` with `VITE_API_BASE_URL` pointing to the Container App HTTPS endpoint.
6. **Deploy UI to Static Web App**: Deploys the built frontend assets (`dist/`) directly to Azure Static Web Apps using `Azure/static-web-apps-deploy@v1`.

---

## Terraform Variables

| Variable | Description | Default |
|---|---|---|
| `location` | Azure region for all application resources (Container App, Static Web App, Storage) | `westeurope` |
| `resource_group_name` | Resource Group name for all application resources | `market-data-demo-app-rg` |
| `image_tag` | Docker image tag to deploy | `latest` |
| `base_name` | Base name for Container App, Static Web App, and Environment | `market-data-demo-api` |
| `env_suffix` | Optional environment suffix for isolated deployments | `""` |
| `static_web_app_sku_tier` | SKU tier for Static Web App (`Free` or `Standard`) | `Free` |
| `static_web_app_sku_size` | SKU size for Static Web App (`Free` or `Standard`) | `Free` |
| `additional_cors_origins` | Additional allowed origins for Container App CORS policy | `["http://localhost:5173", "http://localhost:3000"]` |

---

## Terraform Outputs

| Output Name | Description |
|---|---|
| `container_app_url` | Full HTTPS URL of the deployed Azure Container App API |
| `container_app_fqdn` | Fully qualified domain name (FQDN) of the Container App |
| `storage_account_name` | Name of the storage account for candle data |
| `storage_container_name` | Name of the storage container (`candles-data`) |
| `resource_group_name` | Name of the deployed application resource group |
| `static_web_app_name` | Name of the deployed Azure Static Web App |
| `static_web_app_url` | HTTPS URL of the deployed Frontend UI |
| `static_web_app_default_host_name` | Hostname of the Azure Static Web App |
| `static_web_app_api_key` | Deployment token for Azure Static Web Apps (sensitive) |

---

## Manual Deployment Guide

If you wish to deploy manually from your local machine:

```bash
# 1. Login to Azure
az login

# 2. Initialize and Apply Terraform
cd terraform
terraform init -backend=false  # or configure remote backend
terraform apply \
  -var="location=westeurope" \
  -var="resource_group_name=market-data-demo-app-rg" \
  -var="image_tag=latest"

# 3. Get outputs
API_URL=$(terraform output -raw container_app_url)
SWA_TOKEN=$(terraform output -raw static_web_app_api_key)

# 4. Build and deploy UI
cd ../MarketDataDemo.UI
export VITE_API_BASE_URL="$API_URL"
npm ci
npm run build

# 5. Deploy with Azure Static Web Apps CLI (or az staticwebapp)
npx @azure/static-web-apps-cli deploy ./dist --deployment-token "$SWA_TOKEN" --env production
```
