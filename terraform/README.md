# Azure Container Apps Deployment

## Infrastructure Components
- **Container App**: 0.5 CPU / 1GB RAM
- **Blob Storage**: 
  - Account: `candlesdata`
  - Container: `candles-data`
- **Managed Identity**: Storage Blob Data Contributor role

## Security Configuration
- 🔒 OIDC Authentication for GitHub Actions
- 🔑 Zero hardcoded secrets
- 🔄 Short-lived tokens

## Required GitHub Secrets
| Secret Name | Purpose |
|-------------|---------|
| `AZURE_CLIENT_ID` | Azure AD Application ID |
| `AZURE_TENANT_ID` | Azure AD Tenant ID |
| `AZURE_SUBSCRIPTION_ID` | Azure Subscription ID |
| `AZURE_RG` | Resource Group Name |
| `AZURE_LOCATION` | Azure Region |

## Environment Variables
```env
StorageType=azure
```

## Setup Steps
1. Create Azure AD App Registration
2. Configure Federated Credentials:
   ```bash
   az ad app federated-credential create \
     --id <APPLICATION_ID> \
     --parameters @oidc.json
   ```
   
   `oidc.json`:
   ```json
   {
     "name": "github-actions",
     "issuer": "https://token.actions.githubusercontent.com",
     "subject": "repo:<your-org>/<your-repo>:ref:refs/heads/main",
     "audiences": ["api://AzureADTokenExchange"]
   }
   ```
3. Add secrets to GitHub repository

## Deployment Workflow
- Triggers on changes to:
  - `MarketDataDemo.Api/**`
  - `MarketDataDemo.Candles/**`
  - `Dockerfile.api`
- Builds Docker image
- Pushes to Docker Hub
- Deploys via Terraform