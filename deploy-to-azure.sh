#!/bin/bash

# =============================================================================
# Azure Container Apps Deployment Script for Market Data API
# =============================================================================
# This script deploys the S3CandlesDemo.Api to Azure Container Apps with:
# - Managed Identity for Azure Blob Storage authentication
# - Autoscaling from 0 to 1 instance
# - CORS support for static web app frontend
# - Idempotent operations (safe to rerun)
# =============================================================================

set -e  # Exit on error

# =============================================================================
# CONFIGURATION
# =============================================================================

# Resource naming (can be overridden via environment variables)
RESOURCE_GROUP="${RESOURCE_GROUP:-marketdata-rg}"
LOCATION="${LOCATION:-westeurope}"
ACR_NAME="${ACR_NAME:-marketdataacr}"
CONTAINER_APP_ENV="${CONTAINER_APP_ENV:-marketdata-env}"
CONTAINER_APP_NAME="${CONTAINER_APP_NAME:-marketdata-api}"
MANAGED_IDENTITY_NAME="${MANAGED_IDENTITY_NAME:-marketdata-identity}"

# Existing storage account (already exists)
STORAGE_ACCOUNT_NAME="${STORAGE_ACCOUNT_NAME:-candlesdata}"
STORAGE_ACCOUNT_RESOURCE_GROUP="${STORAGE_ACCOUNT_RESOURCE_GROUP:-${RESOURCE_GROUP}}"
BLOB_CONTAINER="${BLOB_CONTAINER:-candles-data}"
BLOB_PREFIX="${BLOB_PREFIX:-candles}"

# Container image
IMAGE_TAG="${IMAGE_TAG:-latest}"
IMAGE_NAME="marketdata-api"
IMAGE_FULL_NAME="${ACR_NAME}.azurecr.io/${IMAGE_NAME}:${IMAGE_TAG}"

# CORS configuration (update with your Static Web App URL)
CORS_ALLOWED_ORIGINS="${CORS_ALLOWED_ORIGINS:-https://*.azurestaticapps.net}"

# =============================================================================
# LOGGING FUNCTIONS
# =============================================================================

# ANSI color codes
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color
BOLD='\033[1m'

# Log levels
LOG_INFO=0
LOG_SUCCESS=1
LOG_WARNING=2
LOG_ERROR=3
LOG_DEBUG=4

# Logging function with timestamps and colors
log() {
    local level=$1
    local message=$2
    local timestamp=$(date '+%Y-%m-%d %H:%M:%S')
    
    case $level in
        $LOG_INFO)
            echo -e "${BLUE}[INFO]${NC} ${timestamp} ${message}"
            ;;
        $LOG_SUCCESS)
            echo -e "${GREEN}[SUCCESS]${NC} ${timestamp} ${message}"
            ;;
        $LOG_WARNING)
            echo -e "${YELLOW}[WARNING]${NC} ${timestamp} ${message}"
            ;;
        $LOG_ERROR)
            echo -e "${RED}[ERROR]${NC} ${timestamp} ${message}" >&2
            ;;
        $LOG_DEBUG)
            if [ "${DEBUG:-0}" = "1" ]; then
                echo -e "${CYAN}[DEBUG]${NC} ${timestamp} ${message}"
            fi
            ;;
    esac
}

log_section() {
    echo ""
    echo -e "${BOLD}${CYAN}==============================================================================${NC}"
    echo -e "${BOLD}${CYAN}  $1${NC}"
    echo -e "${BOLD}${CYAN}==============================================================================${NC}"
    echo ""
}

log_step() {
    echo -e "${BOLD}${BLUE}→ ${NC}$1"
}

# Progress spinner for long-running operations
log_progress() {
    local message=$1
    local duration=$2
    
    log_step "$message"
    if [ -n "$duration" ]; then
        echo -e "  ${YELLOW}Estimated time: ~${duration}${NC}"
    fi
}

# =============================================================================
# UTILITY FUNCTIONS
# =============================================================================

# Check if a command exists
command_exists() {
    command -v "$1" >/dev/null 2>&1
}

# Check if resource exists
resource_exists() {
    local resource_type=$1
    local resource_name=$2
    local resource_group=$3
    
    case $resource_type in
        "group")
            az group show --name "$resource_name" --query id --output tsv >/dev/null 2>&1
            ;;
        "acr")
            az acr show --name "$resource_name" --resource-group "$resource_group" --query id --output tsv >/dev/null 2>&1
            ;;
        "containerapp-env")
            az containerapp env show --name "$resource_name" --resource-group "$resource_group" --query id --output tsv >/dev/null 2>&1
            ;;
        "identity")
            az identity show --name "$resource_name" --resource-group "$resource_group" --query id --output tsv >/dev/null 2>&1
            ;;
        "containerapp")
            az containerapp show --name "$resource_name" --resource-group "$resource_group" --query id --output tsv >/dev/null 2>&1
            ;;
        "role-assignment")
            return 0  # Always try to create, Azure handles duplicates
            ;;
    esac
}

# Wait for resource to be ready
wait_for_resource() {
    local resource_type=$1
    local resource_name=$2
    local resource_group=$3
    local max_attempts=${4:-60}
    local delay=${5:-10}
    
    log_step "Waiting for $resource_type '$resource_name' to be ready..."
    
    local attempt=0
    while [ $attempt -lt $max_attempts ]; do
        if resource_exists "$resource_type" "$resource_name" "$resource_group"; then
            log_success "$resource_type '$resource_name' is ready"
            return 0
        fi
        
        attempt=$((attempt + 1))
        if [ $attempt -lt $max_attempts ]; then
            sleep $delay
        fi
    done
    
    log_error "Timeout waiting for $resource_type '$resource_name'"
    return 1
}

# =============================================================================
# PREREQUISITE CHECKS
# =============================================================================

check_prerequisites() {
    log_section "Checking Prerequisites"
    
    # Check Azure CLI
    if ! command_exists az; then
        log_error "Azure CLI (az) is not installed. Please install it from https://docs.microsoft.com/cli/azure/"
        exit 1
    fi
    
    log_success "Azure CLI is installed"
    
    # Check Azure login
    if ! az account show >/dev/null 2>&1; then
        log_error "Not logged in to Azure. Please run 'az login' first."
        exit 1
    fi
    
    local subscription=$(az account show --query name -o tsv)
    log_success "Logged in to Azure (Subscription: $subscription)"
    
    # Check Docker
    if ! command_exists docker; then
        log_error "Docker is not installed. Please install it from https://docs.docker.com/"
        exit 1
    fi
    
    log_success "Docker is installed"
    
    # Check if Docker is running
    if ! docker info >/dev/null 2>&1; then
        log_error "Docker daemon is not running. Please start Docker."
        exit 1
    fi
    
    log_success "Docker daemon is running"
    
    # Check if .NET SDK is available (for building)
    if ! command_exists dotnet; then
        log_warning ".NET SDK is not installed. Make sure Dockerfile.api can build the image."
    else
        log_success ".NET SDK is installed"
    fi
}

# =============================================================================
# INFRASTRUCTURE DEPLOYMENT
# =============================================================================

deploy_resource_group() {
    log_section "Deploying Resource Group"
    
    if resource_exists "group" "$RESOURCE_GROUP" ""; then
        log_warning "Resource group '$RESOURCE_GROUP' already exists. Skipping creation."
        return 0
    fi
    
    log_progress "Creating resource group '$RESOURCE_GROUP' in '$LOCATION'" "30s"
    
    az group create \
        --name "$RESOURCE_GROUP" \
        --location "$LOCATION" \
        --tags environment=production application=marketdata-api
    
    log_success "Resource group '$RESOURCE_GROUP' created successfully"
}

deploy_acr() {
    log_section "Deploying Azure Container Registry"
    
    if resource_exists "acr" "$ACR_NAME" "$RESOURCE_GROUP"; then
        log_warning "ACR '$ACR_NAME' already exists. Skipping creation."
        return 0
    fi
    
    log_progress "Creating ACR '$ACR_NAME'" "60s"
    
    az acr create \
        --resource-group "$RESOURCE_GROUP" \
        --name "$ACR_NAME" \
        --sku Basic \
        --admin-enabled true \
        --tags application=marketdata-api
    
    log_success "ACR '$ACR_NAME' created successfully"
}

deploy_container_apps_environment() {
    log_section "Deploying Container Apps Environment"
    
    if resource_exists "containerapp-env" "$CONTAINER_APP_ENV" "$RESOURCE_GROUP"; then
        log_warning "Container Apps Environment '$CONTAINER_APP_ENV' already exists. Skipping creation."
        return 0
    fi
    
    log_progress "Creating Container Apps Environment '$CONTAINER_APP_ENV'" "120s"
    
    az containerapp env create \
        --name "$CONTAINER_APP_ENV" \
        --resource-group "$RESOURCE_GROUP" \
        --location "$LOCATION" \
        --tags application=marketdata-api
    
    log_success "Container Apps Environment '$CONTAINER_APP_ENV' created successfully"
}

deploy_managed_identity() {
    log_section "Deploying Managed Identity"
    
    if resource_exists "identity" "$MANAGED_IDENTITY_NAME" "$RESOURCE_GROUP"; then
        log_warning "Managed Identity '$MANAGED_IDENTITY_NAME' already exists. Skipping creation."
    else
        log_progress "Creating Managed Identity '$MANAGED_IDENTITY_NAME'" "30s"
        
        az identity create \
            --name "$MANAGED_IDENTITY_NAME" \
            --resource-group "$RESOURCE_GROUP" \
            --tags application=marketdata-api
        
        log_success "Managed Identity '$MANAGED_IDENTITY_NAME' created successfully"
    fi
    
    # Get IDs for role assignment
    local storage_id=$(az storage account show \
        --name "$STORAGE_ACCOUNT_NAME" \
        --resource-group "$STORAGE_ACCOUNT_RESOURCE_GROUP" \
        --query id \
        --output tsv)
    
    local identity_principal=$(az identity show \
        --name "$MANAGED_IDENTITY_NAME" \
        --resource-group "$RESOURCE_GROUP" \
        --query principalId \
        --output tsv)
    
    # Grant Storage Blob Data Reader role
    log_step "Granting 'Storage Blob Data Reader' role to managed identity..."
    
    az role assignment create \
        --assignee "$identity_principal" \
        --scope "$storage_id" \
        --role "Storage Blob Data Reader" \
        --description "Allow marketdata-api to read candle data from Azure Blob Storage"
    
    log_success "Managed identity has been granted access to storage account '$STORAGE_ACCOUNT_NAME'"
}

# =============================================================================
# CONTAINER IMAGE BUILD AND PUSH
# =============================================================================

build_and_push_image() {
    log_section "Building and Pushing Container Image"
    
    # Login to ACR
    log_step "Logging in to ACR '$ACR_NAME'..."
    az acr login --name "$ACR_NAME"
    log_success "Logged in to ACR successfully"
    
    # Build the image
    log_progress "Building Docker image '$IMAGE_FULL_NAME'" "5m"
    
    docker build \
        -f Dockerfile.api \
        -t "$IMAGE_FULL_NAME" \
        --build-arg BUILD_CONFIGURATION=Release \
        .
    
    log_success "Docker image built successfully"
    
    # Push to ACR
    log_progress "Pushing image to ACR" "5m"
    
    docker push "$IMAGE_FULL_NAME"
    
    log_success "Image pushed to ACR successfully"
}

# =============================================================================
# CONTAINER APP DEPLOYMENT
# =============================================================================

deploy_container_app() {
    log_section "Deploying Container App"
    
    # Get managed identity client ID
    local identity_client_id=$(az identity show \
        --name "$MANAGED_IDENTITY_NAME" \
        --resource-group "$RESOURCE_GROUP" \
        --query clientId \
        --output tsv)
    
    if resource_exists "containerapp" "$CONTAINER_APP_NAME" "$RESOURCE_GROUP"; then
        log_warning "Container App '$CONTAINER_APP_NAME' already exists. Updating..."
        
        # Update existing container app
        az containerapp update \
            --name "$CONTAINER_APP_NAME" \
            --resource-group "$RESOURCE_GROUP" \
            --image "$IMAGE_FULL_NAME"
        
        log_success "Container App '$CONTAINER_APP_NAME' updated successfully"
    else
        log_progress "Creating Container App '$CONTAINER_APP_NAME'" "60s"
        
        # Create new container app
        az containerapp create \
            --name "$CONTAINER_APP_NAME" \
            --resource-group "$RESOURCE_GROUP" \
            --image "$IMAGE_FULL_NAME" \
            --environment "$CONTAINER_APP_ENV" \
            --ingress external \
            --target-port 8080 \
            --min-replicas 0 \
            --max-replicas 1 \
            --registry-server "${ACR_NAME}.azurecr.io" \
            --assign-identity "$MANAGED_IDENTITY_NAME" \
            --identity-client-id "$identity_client_id" \
            --env-vars \
                "StorageType=azure" \
                "AzureBlob__Container=$BLOB_CONTAINER" \
                "AzureBlob__Prefix=$BLOB_PREFIX" \
                "AzureBlob__StorageAccountName=$STORAGE_ACCOUNT_NAME" \
            --cors-allowed-origins "$CORS_ALLOWED_ORIGINS" \
            --cors-allowed-methods "GET,POST,PUT,DELETE,OPTIONS" \
            --cors-allowed-headers "*" \
            --cors-expose-headers "*" \
            --cors-allow-credentials true \
            --tags application=marketdata-api
        
        log_success "Container App '$CONTAINER_APP_NAME' created successfully"
    fi
    
    # Configure autoscaling
    log_step "Configuring autoscaling rules..."
    
    az containerapp autoscale config create \
        --name "$CONTAINER_APP_NAME" \
        --resource-group "$RESOURCE_GROUP" \
        --min-replicas 0 \
        --max-replicas 1 \
        --cool-down 60 \
        --rule-name http-rule \
        --metric-name http-request \
        --metric-threshold 1
    
    log_success "Autoscaling configured (0-1 replicas, scale on HTTP request)"
}

# =============================================================================
# VERIFICATION
# =============================================================================

verify_deployment() {
    log_section "Verifying Deployment"
    
    # Get container app details
    log_step "Fetching container app details..."
    
    local endpoint=$(az containerapp show \
        --name "$CONTAINER_APP_NAME" \
        --resource-group "$RESOURCE_GROUP" \
        --query properties.configuration.ingress.fqdn \
        --output tsv)
    
    if [ -z "$endpoint" ]; then
        log_error "Failed to get container app endpoint"
        return 1
    fi
    
    log_success "Container App endpoint: https://$endpoint"
    
    # Check container app status
    log_step "Checking container app status..."
    
    local provisioning_state=$(az containerapp show \
        --name "$CONTAINER_APP_NAME" \
        --resource-group "$RESOURCE_GROUP" \
        --query properties.provisioningState \
        --output tsv)
    
    if [ "$provisioning_state" = "Succeeded" ]; then
        log_success "Container App provisioning state: $provisioning_state"
    else
        log_warning "Container App provisioning state: $provisioning_state"
    fi
    
    # Test API endpoint
    log_step "Testing API endpoint..."
    
    local response=$(curl -s -o /dev/null -w "%{http_code}" "https://$endpoint/candles/symbols" 2>/dev/null || echo "000")
    
    if [ "$response" = "200" ]; then
        log_success "API endpoint is responding (HTTP $response)"
    else
        log_warning "API endpoint returned HTTP $response (may need a few moments to start)"
    fi
    
    # Display useful information
    echo ""
    log_section "Deployment Summary"
    
    echo -e "${BOLD}Resource Group:${NC}    $RESOURCE_GROUP"
    echo -e "${BOLD}Location:${NC}          $LOCATION"
    echo -e "${BOLD}ACR Name:${NC}          $ACR_NAME"
    echo -e "${BOLD}Container App:${NC}     $CONTAINER_APP_NAME"
    echo -e "${BOLD}Managed Identity:${NC}  $MANAGED_IDENTITY_NAME"
    echo -e "${BOLD}API Endpoint:${NC}      https://$endpoint"
    echo ""
    
    # Display useful commands
    echo -e "${BOLD}Useful Commands:${NC}"
    echo ""
    echo "  # View container app logs"
    echo "  az containerapp logs show --name $CONTAINER_APP_NAME --resource-group $RESOURCE_GROUP --follow"
    echo ""
    echo "  # Check container app status"
    echo "  az containerapp show --name $CONTAINER_APP_NAME --resource-group $RESOURCE_GROUP"
    echo ""
    echo "  # Test API endpoint"
    echo "  curl 'https://$endpoint/candles/symbols'"
    echo ""
    echo "  # Test specific candle data"
    echo "  curl 'https://$endpoint/candles/BTCUSD/1?from=2024-01-01&to=2024-01-07'"
    echo ""
}

# =============================================================================
# MAIN EXECUTION
# =============================================================================

main() {
    log_section "Azure Container Apps Deployment - Market Data API"
    
    echo -e "${BOLD}Configuration:${NC}"
    echo "  Resource Group:        $RESOURCE_GROUP"
    echo "  Location:              $LOCATION"
    echo "  ACR Name:              $ACR_NAME"
    echo "  Container App:         $CONTAINER_APP_NAME"
    echo "  Managed Identity:      $MANAGED_IDENTITY_NAME"
    echo "  Storage Account:       $STORAGE_ACCOUNT_NAME"
    echo "  Blob Container:        $BLOB_CONTAINER"
    echo "  Image Tag:             $IMAGE_TAG"
    echo ""
    
    # Confirm deployment
    read -p "Continue with deployment? [y/N] " -n 1 -r
    echo ""
    
    if [[ ! $REPLY =~ ^[Yy]$ ]]; then
        log_warning "Deployment cancelled by user"
        exit 0
    fi
    
    # Execute deployment steps
    check_prerequisites
    deploy_resource_group
    deploy_acr
    deploy_container_apps_environment
    deploy_managed_identity
    build_and_push_image
    deploy_container_app
    verify_deployment
    
    log_section "Deployment Complete"
    log_success "All deployment steps completed successfully!"
}

# Run main function
main "$@"
