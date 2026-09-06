# S3 Candles Demo

This project is an experiment to use S3 files for OHLCV (Open, High, Low, Close, Volume) data. The goal is to evaluate if this approach is suitable for backtesting purposes.

## Requirements

1. **Daily Candle Files**
   - Each day of candles will be stored in a separate file.
   - Files will be named using the date: `YYYY-MM-DD.bin` (e.g., `2024-01-01.bin`).
   - The contents of each file will be an array of `Candle` structures, sorted by timestamp in ascending order.
   - For a 1-minute interval, a full day will contain 1440 candles.
   - If no candles are present for a given time range within a day, those intervals will be omitted (files may have fewer than 1440 candles).
   - Only non-zero volume candles will be stored. If a minute has no trades, it will not have a candle in the file.

2. **Candle Size Support**
   - The system must support different candle sizes (e.g., 1 minute, 5 minutes, 1 hour).
   - Files will be stored in folders named after the candle size in minutes (e.g., `1/`, `5/`, `60/`).
   - The full path for a file will be: `{CandleSizeMinutes}/{Symbol}/{YYYY-MM-DD}.bin`.

3. **Symbol Support**
   - The system must support multiple symbols (e.g., `BTCUSD`, `ETHUSD`).
   - Files will be partitioned by symbol in the folder path: `{CandleSizeMinutes}/{Symbol}/{YYYY-MM-DD}.bin`.

4. **Binary Storage Format**
   - Files will be stored in a compact binary format.
   - Each candle will be serialized using a custom binary writer/reader for maximum performance and minimal storage footprint.

5. **Storage Backends**
   - **Local File System**: For local development and testing.
   - **AWS S3 / S3-Compatible (MinIO)**: For production and cloud-native deployments.
   - **Google Cloud Storage (GCS)**: Supported as an alternative object store backend.
   - **Azure Blob Storage**: Production storage backend with Managed Identity RBAC.

6. **Candles Repository Interface**
   - Implement an `ICandlesRepository` interface providing methods to:
     - Store a batch of candles.
     - Retrieve candles for a given symbol, candle size, and date range.
     - List available symbols and date ranges.

7. **API Layer**
   - Implement a minimal HTTP API around the `ICandlesRepository` interface.

8. **Background Merging**
   - Implement a background process to merge small candle files into larger files.

9. **CSV Data Loading**
   - On startup, the API should read candles from CSV files in the `csv/` folder and load them into the `ICandlesRepository`.
   - CSV file names follow the pattern: `{Symbol}-{CandleSizeMinutes}.csv`.
   - If data is already present in the binary files, those candles should be skipped.
   - All actions must be properly logged.

## Project Structure

| Project | Description |
|---------|-------------|
| **MarketDataDemo.Candles** | Core library — `ICandlesRepository`, binary serialization, filesystem, GCS & Azure Blob implementations |
| **MarketDataDemo.Api** | ASP.NET Minimal API — HTTP endpoints for candle storage/retrieval with CORS support |
| **MarketDataDemo.UI** | React / TypeScript / Vite frontend Single Page Application (deployed to Azure Static Web Apps) |
| **MarketDataDemo.KrakenLatestCollector** | Scheduled batch job — collects latest OHLCV data from Kraken API and stores to blob/S3 storage |
| **MarketDataDemo.KrakenHistoricalImporter** | One-shot batch job — imports full historical OHLCV data from Kraken's Google Drive archive |
| **MarketDataDemo.CsvLoader** | Scheduled batch job — fills gaps in candle data by streaming CSV files (AOT-compiled minimal API) |
| **MarketDataDemo.Tests** | xUnit tests — unit, repository, and integration tests (uses MinIO via Testcontainers) |
| **MarketDataDemo.StressTests** | k6 stress tests — ramp-up load tests targeting the candles fetch endpoint |

## Terraform Deployment to Azure (Production)

For production deployments to Azure, use the Terraform scripts in the `terraform/` directory. These scripts provision:
- **Azure Container App**: Hosts `MarketDataDemo.Api` (.NET 10 Native AOT) with ingress and CORS configuration.
- **Azure Static Web App**: Hosts `MarketDataDemo.UI` frontend SPA in the same resource group and location.
- **Azure Blob Storage**: Storage account (`candlesdata<suffix>`) and container (`candles-data`) with managed identity RBAC.

## How to Use Terraform

1. **Set Required Variables**
   - Create a `terraform.tfvars` file in the `terraform/` directory with your configuration:
   ```terraform
   location            = "westeurope"
   resource_group_name = "market-data-demo-app-rg"
   image_tag           = "latest"
   ```

2. **Terraform Commands**
   ```bash
   cd terraform
   terraform init
   terraform plan -out=deployment.plan
   terraform apply deployment.plan
   ```

## Key Terraform Variables

The following variables are defined in `terraform/variables.tf` and can be customized:

- **location** (string): Azure region where all resources (Resource Group, Container App, Static Web App, Storage) will be deployed (default: `"westeurope"`)
- **resource_group_name** (string): Name of Azure resource group for all resources (default: `"market-data-demo-app-rg"`)
- **image_tag** (string): Docker image tag to deploy (default: `"latest"`)
- **env_suffix** (string): Suffix to make resource names unique when testing (default: `""`)
- **base_name** (string): Base name for container app, static web app, and environment (default: `"market-data-demo-api"`)
- **static_web_app_sku_tier** (string): SWA SKU tier (default: `"Free"`)
- **static_web_app_sku_size** (string): SWA SKU size (default: `"Free"`)
- **additional_cors_origins** (list): Extra CORS origins permitted on the Container App API (default: `["http://localhost:5173", "http://localhost:3000"]`)

All variables can be overridden using `-var`:
```bash
terraform apply -var="base_name=custom-app-name"
```

## Running Tests

### Unit & Integration Tests (dotnet test)
```bash
# Run all tests (unit, repository, MinIO integration tests)
dotnet test
```

### k6 Stress & Load Testing
```bash
# Prerequisites: k6 installed (e.g. `brew install k6` or `sudo apt install k6`)
# Ensure the API is running at http://localhost:5044 (or customize URL via API_BASE_URL env var)

# Standard stress test (10 -> 50 -> 100 -> 200 -> 300 VUs over ~2.5 minutes)
k6 run candles-stress.js

# Target a different host (e.g., Azure Container App or custom port)
API_BASE_URL=https://my-app.azurecontainerapps.io/candles k6 run candles-stress.js

# Run with web dashboard (k6 v0.49+)
k6 run --out web-dashboard candles-stress.js
```

## Running Locally

```bash
# Start MinIO only
bash startMinio.sh

# Run API (uses appsettings.Development.json -> localhost:7000)
dotnet run --project MarketDataDemo.Api

# API Reference (Scalar/Swagger)
open http://localhost:5044/scalar

# Run frontend UI
cd MarketDataDemo.UI
npm install
npm run dev

# Run collector
dotnet run --project MarketDataDemo.KrakenLatestCollector

# Run CSV loader (fill data gaps from CSV)
dotnet run --project MarketDataDemo.CsvLoader
```

## Design Decisions

- **Folder Structure**: `{CandleSizeMinutes}/{Symbol}/{YYYY-MM-DD}.bin` allows efficient querying of date ranges without reading unnecessary files.
- **Binary Format**: Raw binary serialization avoids the overhead of JSON/Protobuf while keeping candle records small (36 bytes each: `long Timestamp`, `double Open, High, Low, Close, Volume`, `int Count`).
- **GCS & Azure Storage**: Alongside S3 and filesystem repositories, GCS and Azure Blob Storage implementations are provided so the same core library can run on AWS, GCP, or Azure without code modifications.
- **Native AOT**: `MarketDataDemo.Api` and `MarketDataDemo.CsvLoader` are compiled with Native AOT for minimal image sizes (~20MB) and instant cold-start times.
- **Daily Partitioning**: Queries spanning multiple days simply fetch the relevant daily files concurrently.
- **Dynamic File Discovery**: Endpoint `GET /candles/files` inspects all daily partitions to return the exact list of available symbols and candle sizes dynamically.

### What is Not Checked

1. **Data Completeness**:
   - The system does not verify that all expected candles are present in a file. Missing intervals (e.g., zero volume) are intentionally allowed.

2. **Strict Time Ordering on Ingestion**:
   - Ingested candle arrays are expected to be ordered by timestamp, but deduplication and sorting are handled during repository write operations.

3. **Multi-Writer Concurrency**:
   - Assumes one writer per symbol/interval/day at a time. Simultaneous writes to the same daily file should be coordinated by the caller or batch merge process.

---

### Understanding the 36-Byte Binary Format

Each candle record in the daily `.bin` file is 36 bytes:

```
[Timestamp: 8B] [Open: 8B] [High: 8B] [Low: 8B] [Close: 8B] [Volume: 8B] [Count: 4B] = 36 bytes
```

This means:
- **1 Day of 1-minute candles** (1440 entries) $\approx 51.84 \text{ KB}$
- **1 Month** $\approx 1.55 \text{ MB}$
- **1 Year** $\approx 18.9 \text{ MB}$

---

*Last updated: April 15, 2026*
*Author: denisvlah*
