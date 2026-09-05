#!/usr/bin/env bash
# ==============================================================================
# Setup Azure OIDC Authentication & Terraform State Backend for GitHub Actions
#
# This script is RE-ENTRANT (safe to run multiple times without errors or duplicates).
#
# It performs the following steps:
# 1. Validates Azure CLI login and tools.
# 2. Resolves or creates an Azure AD App Registration.
# 3. Resolves or creates the corresponding Service Principal.
# 4. Configures Federated Identity Credentials for GitHub Actions OIDC.
# 5. Creates the Azure Resource Group.
# 6. Assigns required RBAC roles (Contributor + RBAC Administrator) to the SP.
# 7. Creates an Azure Storage Account and Blob Container for Terraform Remote State.
# 8. Assigns Storage Blob Data Contributor role on the state backend.
# 9. Prints all GitHub Repository Secrets needed for the GitHub Actions workflow.
# ==============================================================================

set -euo pipefail

# Text formatting helpers
COLOR_RESET="\033[0m"
COLOR_INFO="\033[0;36m"
COLOR_SUCCESS="\033[0;32m"
COLOR_WARN="\033[0;33m"
COLOR_ERROR="\033[0;31m"
COLOR_BOLD="\033[1m"

log_info() {
  echo -e "${COLOR_INFO}[INFO]${COLOR_RESET} $*"
}

log_success() {
  echo -e "${COLOR_SUCCESS}[SUCCESS]${COLOR_RESET} $*"
}

log_warn() {
  echo -e "${COLOR_WARN}[WARN]${COLOR_RESET} $*"
}

log_error() {
  echo -e "${COLOR_ERROR}[ERROR]${COLOR_RESET} $*" >&2
}

# ------------------------------------------------------------------------------
# Auto-detect defaults
# ------------------------------------------------------------------------------
detect_github_repo() {
  if git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    local remote_url
    remote_url=$(git config --get remote.origin.url 2>/dev/null || true)
    if [[ "$remote_url" =~ github\.com[:/]([^/]+)/([^/.]+)(\.git)?$ ]]; then
      echo "${BASH_REMATCH[1]}/${BASH_REMATCH[2]}"
      return 0
    fi
  fi
  echo "denisvlah/MarketDataDemo"
}

detect_github_branch() {
  if git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    local branch
    branch=$(git branch --show-current 2>/dev/null || true)
    if [[ -n "$branch" ]]; then
      echo "$branch"
      return 0
    fi
  fi
  echo "main"
}

# ------------------------------------------------------------------------------
# Configuration Variables (can be overridden via CLI flags or ENV vars)
# ------------------------------------------------------------------------------
GITHUB_REPO="${GITHUB_REPO:-$(detect_github_repo)}"
GITHUB_BRANCH="${GITHUB_BRANCH:-$(detect_github_branch)}"
AZURE_APP_NAME="${AZURE_APP_NAME:-marketdata-demo-github-actions}"
AZURE_RG="${AZURE_RG:-market-data-demo-rg}"
AZURE_LOCATION="${AZURE_LOCATION:-eastus}"
AZURE_SUBSCRIPTION_ID="${AZURE_SUBSCRIPTION_ID:-}"
TF_STATE_CONTAINER="${TF_STATE_CONTAINER:-tfstate}"

# Parse CLI options
usage() {
  cat <<EOF
Usage: $(basename "$0") [options]

Options:
  -r, --repo <org/repo>          GitHub repository (default: ${GITHUB_REPO})
  -b, --branch <branch>          GitHub branch for OIDC subject (default: ${GITHUB_BRANCH})
  -a, --app-name <name>          Azure AD App registration name (default: ${AZURE_APP_NAME})
  -g, --resource-group <name>    Azure Resource Group name (default: ${AZURE_RG})
  -l, --location <region>        Azure region (default: ${AZURE_LOCATION})
  -s, --subscription <id>        Azure Subscription ID (default: current active subscription)
  -h, --help                     Show this help message
EOF
  exit 0
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    -r|--repo)
      GITHUB_REPO="$2"
      shift 2
      ;;
    -b|--branch)
      GITHUB_BRANCH="$2"
      shift 2
      ;;
    -a|--app-name)
      AZURE_APP_NAME="$2"
      shift 2
      ;;
    -g|--resource-group)
      AZURE_RG="$2"
      shift 2
      ;;
    -l|--location)
      AZURE_LOCATION="$2"
      shift 2
      ;;
    -s|--subscription)
      AZURE_SUBSCRIPTION_ID="$2"
      shift 2
      ;;
    -h|--help)
      usage
      ;;
    *)
      log_error "Unknown option: $1"
      usage
      ;;
  esac
done

echo -e "${COLOR_BOLD}======================================================${COLOR_RESET}"
echo -e "${COLOR_BOLD}  Azure OIDC & Terraform Setup for GitHub Actions    ${COLOR_RESET}"
echo -e "${COLOR_BOLD}======================================================${COLOR_RESET}"
log_info "Target GitHub Repo:    ${COLOR_BOLD}${GITHUB_REPO}${COLOR_RESET}"
log_info "Target Branch:         ${COLOR_BOLD}${GITHUB_BRANCH}${COLOR_RESET}"
log_info "Azure AD App Name:     ${COLOR_BOLD}${AZURE_APP_NAME}${COLOR_RESET}"
log_info "Resource Group:        ${COLOR_BOLD}${AZURE_RG}${COLOR_RESET}"
log_info "Location:              ${COLOR_BOLD}${AZURE_LOCATION}${COLOR_RESET}"
echo ""

# ------------------------------------------------------------------------------
# Step 1: Pre-flight checks & Azure Account
# ------------------------------------------------------------------------------
log_info "Checking prerequisites..."
if ! command -v az &>/dev/null; then
  log_error "Azure CLI ('az') is not installed. Please install it: https://aka.ms/installazurecli"
  exit 1
fi

if ! az account show &>/dev/null; then
  log_warn "Azure CLI is not logged in. Running 'az login'..."
  az login --use-device-code >/dev/null
fi

if [[ -n "$AZURE_SUBSCRIPTION_ID" ]]; then
  log_info "Setting active subscription to: $AZURE_SUBSCRIPTION_ID"
  az account set --subscription "$AZURE_SUBSCRIPTION_ID"
else
  AZURE_SUBSCRIPTION_ID=$(az account show --query "id" -o tsv)
fi

AZURE_TENANT_ID=$(az account show --query "tenantId" -o tsv)
AZURE_SUBSCRIPTION_NAME=$(az account show --query "name" -o tsv)

log_success "Active Subscription: ${AZURE_SUBSCRIPTION_NAME} (${AZURE_SUBSCRIPTION_ID})"
log_success "Tenant ID:           ${AZURE_TENANT_ID}"
echo ""

# ------------------------------------------------------------------------------
# Step 2: Ensure Azure AD Application Registration exists
# ------------------------------------------------------------------------------
log_info "Step 2: Checking Azure AD Application Registration ('${AZURE_APP_NAME}')..."
APP_ID=$(az ad app list --display-name "$AZURE_APP_NAME" --query "[0].appId" -o tsv 2>/dev/null || true)
APP_OBJECT_ID=$(az ad app list --display-name "$AZURE_APP_NAME" --query "[0].id" -o tsv 2>/dev/null || true)

if [[ -z "$APP_ID" || "$APP_ID" == "null" ]]; then
  log_info "Creating new Azure AD Application Registration: ${AZURE_APP_NAME}"
  APP_ID=$(az ad app create --display-name "$AZURE_APP_NAME" --query "appId" -o tsv)
  APP_OBJECT_ID=$(az ad app show --id "$APP_ID" --query "id" -o tsv)
  log_success "Created Azure AD App ID: ${APP_ID}"
else
  log_success "Existing Azure AD App Registration found (App ID: ${APP_ID})"
fi

# ------------------------------------------------------------------------------
# Step 3: Ensure Service Principal exists
# ------------------------------------------------------------------------------
log_info "Step 3: Checking Service Principal for App ID '${APP_ID}'..."
SP_ID=$(az ad sp list --filter "appId eq '$APP_ID'" --query "[0].id" -o tsv 2>/dev/null || true)

if [[ -z "$SP_ID" || "$SP_ID" == "null" ]]; then
  log_info "Creating Service Principal for App ID: ${APP_ID}"
  SP_ID=$(az ad sp create --id "$APP_ID" --query "id" -o tsv)
  log_success "Created Service Principal (Object ID: ${SP_ID})"
  # Allow Azure AD replication delay
  sleep 5
else
  log_success "Existing Service Principal found (Object ID: ${SP_ID})"
fi

# ------------------------------------------------------------------------------
# Step 4: Configure Federated Identity Credentials for GitHub OIDC
# ------------------------------------------------------------------------------
log_info "Step 4: Configuring GitHub OIDC Federated Credentials..."

FED_BRANCH_NAME="gh-${GITHUB_BRANCH}-branch"
FED_BRANCH_SUBJECT="repo:${GITHUB_REPO}:ref:refs/heads/${GITHUB_BRANCH}"

# 4a. Main Branch Credential
if az ad app federated-credential show --id "$APP_ID" --federated-credential-id "$FED_BRANCH_NAME" &>/dev/null; then
  log_success "Federated credential '${FED_BRANCH_NAME}' already exists."
else
  log_info "Creating federated credential '${FED_BRANCH_NAME}' for subject: ${FED_BRANCH_SUBJECT}"
  az ad app federated-credential create \
    --id "$APP_ID" \
    --parameters "{
      \"name\": \"${FED_BRANCH_NAME}\",
      \"issuer\": \"https://token.actions.githubusercontent.com\",
      \"subject\": \"${FED_BRANCH_SUBJECT}\",
      \"description\": \"GitHub Actions OIDC credential for branch ${GITHUB_BRANCH}\",
      \"audiences\": [\"api://AzureADTokenExchange\"]
    }" >/dev/null
  log_success "Created federated credential for branch: ${GITHUB_BRANCH}"
fi

# 4b. Environment Credential (optional support for GitHub Environments named 'production')
FED_ENV_NAME="gh-env-production"
FED_ENV_SUBJECT="repo:${GITHUB_REPO}:environment:production"
if ! az ad app federated-credential show --id "$APP_ID" --federated-credential-id "$FED_ENV_NAME" &>/dev/null; then
  log_info "Creating optional federated credential '${FED_ENV_NAME}' for GitHub environment 'production'"
  az ad app federated-credential create \
    --id "$APP_ID" \
    --parameters "{
      \"name\": \"${FED_ENV_NAME}\",
      \"issuer\": \"https://token.actions.githubusercontent.com\",
      \"subject\": \"${FED_ENV_SUBJECT}\",
      \"description\": \"GitHub Actions OIDC credential for environment production\",
      \"audiences\": [\"api://AzureADTokenExchange\"]
    }" >/dev/null 2>&1 || true
fi

# ------------------------------------------------------------------------------
# Step 5: Ensure Resource Group & Assign RBAC Roles
# ------------------------------------------------------------------------------
log_info "Step 5: Ensuring Resource Group '${AZURE_RG}' exists in '${AZURE_LOCATION}'..."
az group create --name "$AZURE_RG" --location "$AZURE_LOCATION" >/dev/null
log_success "Resource group '${AZURE_RG}' is ready."

# Role 1: Contributor on Subscription (allows managing Container Apps, Storage, Network, etc.)
log_info "Checking 'Contributor' role on subscription..."
HAS_CONTRIBUTOR=$(az role assignment list \
  --assignee "$APP_ID" \
  --role "Contributor" \
  --scope "/subscriptions/$AZURE_SUBSCRIPTION_ID" \
  --query "[0].id" -o tsv 2>/dev/null || true)

if [[ -z "$HAS_CONTRIBUTOR" || "$HAS_CONTRIBUTOR" == "null" ]]; then
  log_info "Assigning 'Contributor' role on subscription ${AZURE_SUBSCRIPTION_ID}..."
  az role assignment create \
    --assignee "$APP_ID" \
    --role "Contributor" \
    --scope "/subscriptions/$AZURE_SUBSCRIPTION_ID" >/dev/null
  log_success "Assigned 'Contributor' role."
else
  log_success "'Contributor' role already assigned."
fi

# Role 2: Role Based Access Control Administrator or User Access Administrator
# Needed so Terraform can create role assignments (e.g. Storage Blob Data Contributor for Container App Managed Identity)
log_info "Checking Role Assignment permission on subscription..."
HAS_RBAC_ADMIN=$(az role assignment list \
  --assignee "$APP_ID" \
  --role "Role Based Access Control Administrator" \
  --scope "/subscriptions/$AZURE_SUBSCRIPTION_ID" \
  --query "[0].id" -o tsv 2>/dev/null || true)

if [[ -z "$HAS_RBAC_ADMIN" || "$HAS_RBAC_ADMIN" == "null" ]]; then
  log_info "Attempting to assign 'Role Based Access Control Administrator' role..."
  if ! az role assignment create \
    --assignee "$APP_ID" \
    --role "Role Based Access Control Administrator" \
    --scope "/subscriptions/$AZURE_SUBSCRIPTION_ID" >/dev/null 2>&1; then
    log_warn "Assigning 'Role Based Access Control Administrator' failed, trying 'User Access Administrator'..."
    az role assignment create \
      --assignee "$APP_ID" \
      --role "User Access Administrator" \
      --scope "/subscriptions/$AZURE_SUBSCRIPTION_ID" >/dev/null 2>&1 || {
        log_warn "Could not assign User Access Administrator at subscription scope. Attempting at Resource Group scope..."
        az role assignment create \
          --assignee "$APP_ID" \
          --role "User Access Administrator" \
          --scope "/subscriptions/$AZURE_SUBSCRIPTION_ID/resourceGroups/$AZURE_RG" >/dev/null 2>&1 || true
      }
  fi
  log_success "Role assignment administrator permissions configured."
else
  log_success "'Role Based Access Control Administrator' role already assigned."
fi

# ------------------------------------------------------------------------------
# Step 6: Create Terraform Remote State Storage Account & Container
# ------------------------------------------------------------------------------
log_info "Step 6: Setting up Azure Blob Storage for Terraform Remote State..."

# Generate a deterministic, valid storage account name (3-24 lowercase alphanumeric chars)
HASH_INPUT="${AZURE_SUBSCRIPTION_ID}${AZURE_RG}tfstate"
UNIQUE_SUFFIX=$(echo -n "$HASH_INPUT" | md5sum | tr -dc 'a-z0-9' | head -c 14)
TF_STATE_SA="tfstate${UNIQUE_SUFFIX}"

log_info "Checking Terraform State Storage Account: ${TF_STATE_SA}"
SA_EXISTS=$(az storage account show --name "$TF_STATE_SA" --resource-group "$AZURE_RG" --query "name" -o tsv 2>/dev/null || true)

if [[ -z "$SA_EXISTS" || "$SA_EXISTS" == "null" ]]; then
  log_info "Creating Storage Account '${TF_STATE_SA}' for Terraform state in '${AZURE_RG}'..."
  az storage account create \
    --name "$TF_STATE_SA" \
    --resource-group "$AZURE_RG" \
    --location "$AZURE_LOCATION" \
    --sku Standard_LRS \
    --min-tls-version TLS1_2 \
    --allow-blob-public-access false >/dev/null
  log_success "Created storage account: ${TF_STATE_SA}"
else
  log_success "Storage account '${TF_STATE_SA}' already exists."
fi

# Ensure Blob Container exists
CONTAINER_EXISTS=$(az storage container exists \
  --account-name "$TF_STATE_SA" \
  --name "$TF_STATE_CONTAINER" \
  --auth-mode login \
  --query "exists" -o tsv 2>/dev/null || true)

if [[ "$CONTAINER_EXISTS" != "true" ]]; then
  log_info "Creating blob container '${TF_STATE_CONTAINER}' in storage account '${TF_STATE_SA}'..."
  az storage container create \
    --account-name "$TF_STATE_SA" \
    --name "$TF_STATE_CONTAINER" \
    --auth-mode login >/dev/null
  log_success "Created blob container: ${TF_STATE_CONTAINER}"
else
  log_success "Blob container '${TF_STATE_CONTAINER}' already exists."
fi

# Assign Storage Blob Data Contributor on State Storage Account to SP
TF_SA_SCOPE="/subscriptions/${AZURE_SUBSCRIPTION_ID}/resourceGroups/${AZURE_RG}/providers/Microsoft.Storage/storageAccounts/${TF_STATE_SA}"
HAS_STORAGE_ROLE=$(az role assignment list \
  --assignee "$APP_ID" \
  --role "Storage Blob Data Contributor" \
  --scope "$TF_SA_SCOPE" \
  --query "[0].id" -o tsv 2>/dev/null || true)

if [[ -z "$HAS_STORAGE_ROLE" || "$HAS_STORAGE_ROLE" == "null" ]]; then
  log_info "Assigning 'Storage Blob Data Contributor' on state storage account to Service Principal..."
  az role assignment create \
    --assignee "$APP_ID" \
    --role "Storage Blob Data Contributor" \
    --scope "$TF_SA_SCOPE" >/dev/null
  log_success "Assigned 'Storage Blob Data Contributor' on state storage account."
else
  log_success "'Storage Blob Data Contributor' already assigned on state storage account."
fi

# ------------------------------------------------------------------------------
# Step 7: Print GitHub Secrets
# ------------------------------------------------------------------------------
echo ""
echo -e "${COLOR_BOLD}===============================================================================${COLOR_RESET}"
echo -e "${COLOR_SUCCESS}${COLOR_BOLD}                 SETUP COMPLETE! AZURE OIDC IS CONFIGURED                     ${COLOR_RESET}"
echo -e "${COLOR_BOLD}===============================================================================${COLOR_RESET}"
echo ""
echo -e "Add the following secrets to your GitHub repository at:"
echo -e "${COLOR_INFO}https://github.com/${GITHUB_REPO}/settings/secrets/actions${COLOR_RESET}"
echo ""
echo -e "${COLOR_BOLD}---------------------- Azure Authentication Secrets -------------------------${COLOR_RESET}"
printf "%-32s : %s\n" "AZURE_CLIENT_ID" "$APP_ID"
printf "%-32s : %s\n" "AZURE_TENANT_ID" "$AZURE_TENANT_ID"
printf "%-32s : %s\n" "AZURE_SUBSCRIPTION_ID" "$AZURE_SUBSCRIPTION_ID"
printf "%-32s : %s\n" "AZURE_RG" "$AZURE_RG"
printf "%-32s : %s\n" "AZURE_LOCATION" "$AZURE_LOCATION"
echo ""
echo -e "${COLOR_BOLD}------------------- Terraform Remote State Secrets --------------------------${COLOR_RESET}"
printf "%-32s : %s\n" "TF_STATE_STORAGE_ACCOUNT_NAME" "$TF_STATE_SA"
printf "%-32s : %s\n" "TF_STATE_CONTAINER_NAME" "$TF_STATE_CONTAINER"
printf "%-32s : %s\n" "TF_STATE_RESOURCE_GROUP_NAME" "$AZURE_RG"
echo ""
echo -e "${COLOR_BOLD}---------------------- Docker Hub Secrets ----------------------------------${COLOR_RESET}"
printf "%-32s : %s\n" "DOCKERHUB_USERNAME" "<your-dockerhub-username>"
printf "%-32s : %s\n" "DOCKERHUB_TOKEN" "<your-dockerhub-personal-access-token>"
echo ""
echo -e "${COLOR_BOLD}===============================================================================${COLOR_RESET}"

# If GitHub CLI is installed and authenticated, optionally set secrets directly
if command -v gh &>/dev/null; then
  if gh auth status &>/dev/null; then
    echo ""
    log_info "GitHub CLI ('gh') is authenticated. Would you like to automatically set these secrets now? (y/N)"
    read -r -t 10 -p "Set secrets automatically via GitHub CLI? [y/N]: " sync_choice || sync_choice="n"
    echo ""
    if [[ "$sync_choice" =~ ^[Yy]$ ]]; then
      log_info "Setting secrets in repository '${GITHUB_REPO}'..."
      gh secret set AZURE_CLIENT_ID -b "$APP_ID" -R "$GITHUB_REPO"
      gh secret set AZURE_TENANT_ID -b "$AZURE_TENANT_ID" -R "$GITHUB_REPO"
      gh secret set AZURE_SUBSCRIPTION_ID -b "$AZURE_SUBSCRIPTION_ID" -R "$GITHUB_REPO"
      gh secret set AZURE_RG -b "$AZURE_RG" -R "$GITHUB_REPO"
      gh secret set AZURE_LOCATION -b "$AZURE_LOCATION" -R "$GITHUB_REPO"
      gh secret set TF_STATE_STORAGE_ACCOUNT_NAME -b "$TF_STATE_SA" -R "$GITHUB_REPO"
      gh secret set TF_STATE_CONTAINER_NAME -b "$TF_STATE_CONTAINER" -R "$GITHUB_REPO"
      gh secret set TF_STATE_RESOURCE_GROUP_NAME -b "$AZURE_RG" -R "$GITHUB_REPO"
      log_success "Azure and Terraform secrets have been automatically set in GitHub repository!"
      log_info "Remember to set DOCKERHUB_USERNAME and DOCKERHUB_TOKEN manually if not already configured."
    fi
  fi
fi
