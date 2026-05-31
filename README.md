# Development Plan: S3 OHLCV Storage and Querying

This project is an experiment to use S3 files for OHLCV (Open, High, Low, Close, Volume) storage and querying. In this document, OHLCV data is often referred to as "candles".

## Required Features

1. **Binary Storage**
   - Store OHLCV data as binary in files encoded from C# structs representing the OHLCV.

2. **File Naming Convention**
   - File names should follow the pattern: `{Symbol}-{CandleSizeMinutes}-{periodStart}-{periodEnd}-{version}.bin`

3. **Data Organization**
   - All candles must be sorted by timestamp in each file, with no gaps except for holidays, weekends, and out-of-market hours for stock data. Crypto assets should have no gaps.

4. **File Size**
   - Target file size is approximately 50 MB (subject to change).

5. **C# Interface**
   - Design a C# interface `ICandlesRepository` to store candles. Candles will be supplied in sorted order as a list or as an `IAsyncEnumerable`.
   - The interface should allow fetching candles by symbol, time period, and candle interval.

6. **Implementations**
   - Provide two implementations of `ICandlesRepository`:
     - One for the filesystem.
     - One for S3 object storage.
   - **Implementation Requirements:**
     1. Store candle file names in memory for fast query processing.
     2. Support reading files from the middle if required by a time interval query (using seek operations).
     3. Support reading and merging data from multiple files to fulfill a user request.
     4. Avoid producing temporary memory allocations (no garbage production); return data as `IAsyncEnumerable`.
   - Both implementations must support all interface methods. The system is intended for use by a single application instance.

7. **Minimal HTTP API**
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
| **S3CandlesDemo.Candles** | Core library — `ICandlesRepository`, binary serialization, filesystem & S3 implementations |
| **S3CandlesDemo.Api** | ASP.NET Minimal API — HTTP endpoints for candle storage/retrieval |
| **S3CandlesDemo.KrakenLatestCollector** | Scheduled batch job — collects latest OHLCV data from Kraken API and stores to S3 |
| **S3CandlesDemo.KrakenHistoricalImporter** | One-shot batch job — imports full historical OHLCV data from Kraken's Google Drive archive into S3 |
| **S3CandlesDemo.CsvLoader** | Scheduled batch job — fills gaps in candle data by streaming CSV files from S3 (AOT-compiled minimal API) |
| **S3CandlesDemo.Tests** | xUnit tests — unit, repository, and integration tests (uses MinIO via Testcontainers) |
| **S3CandlesDemo.StressTests** | k6 stress tests — ramp-up load tests targeting the candles fetch endpoint |

## Docker Compose Deployment

A full-stack `docker-compose.yml` runs all services together with MinIO as the S3 backend:

| Service | Purpose | Port |
|---------|---------|------|
| `minio` | S3-compatible storage (persistent volume) | `9000` (API), `9001` (console) |
| `minio-setup` | Init container — creates buckets, seeds shared config files | — |
| `api` | Candles HTTP API | `5044` |
| `kraken-collector` | Kraken data collector (scheduled daily job) | `5099` (health) |
| `csv-loader` | CSV gap-filling loader (scheduled daily job, AOT-compiled) | `5043` (health) |
| `k6-stress` | k6 stress test runner (opt-in, `stress` profile only) | — |

```bash
# Start everything
docker compose up -d

# Watch logs
docker compose logs -f

# Re-run the collector on demand
docker compose restart kraken-collector

# MinIO console
open http://localhost:9001  # minioadmin / minioadmin

# API Reference (Scalar/Swagger)
open http://localhost:5044/scalar
```

**Unified Single-Bucket Layout:**
All data is stored in a **single S3 bucket** under three fixed path prefixes:

| Prefix | Content |
|--------|---------|
| `candles/` | Binary `.bin` candle files served by the API |
| `csv/` | CSV source files consumed by `S3CandlesDemo.CsvLoader` |
| `config/` | Job config CSV (`config/kraken-collector-config.csv`) |

All scheduled jobs (Kraken collector, CSV loader, historical importer) read symbols and intervals via **`ICandlesRepository.GetJobConfigAsync()`**, which reads `config/kraken-collector-config.csv` from the shared bucket and returns a list of `PairJobConfig` records. This file is seeded by `minio-setup` on first start from `S3CandlesDemo.KrakenLatestCollector/kraken-collector-config.csv`.

## Stress Testing (k6)

The `S3CandlesDemo.StressTests/` folder contains a [k6](https://k6.io/) load test targeting the `GET /candles/{intervalMinutes}` endpoint.

The test ramps virtual users (VUs) in stages (5 → 20 → 50 → 0) and reports average response time, 95th-percentile latency, and failed request rate.

**Run via Docker Compose** (requires the `api` and MinIO stack to be up):

```bash
# Start the core stack first (if not already running)
docker compose up -d

# Run k6 against the running api container
docker compose --profile stress run --rm k6-stress
```

**Customise targets** via environment variables:

```bash
docker compose --profile stress run --rm \
  -e SYMBOL="ETH/USD" \
  -e INTERVAL="60" \
  -e FROM="2024-06-01T00:00:00Z" \
  -e TO="2024-06-30T23:59:59Z" \
  k6-stress
```

**Run locally** (without Docker, requires `k6` installed):

```bash
cd S3CandlesDemo.StressTests
k6 run candles-stress.js

# With live browser dashboard at http://localhost:5665
k6 run --out web-dashboard candles-stress.js
```

## Local Development (without Docker)
```bash
# Start MinIO only
bash startMinio.sh

# Run API (uses appsettings.Development.json → localhost:7000)
dotnet run --project S3CandlesDemo.Api

# API Reference (Scalar/Swagger)
open http://localhost:5044/scalar

# Run collector
dotnet run --project S3CandlesDemo.KrakenLatestCollector

# Run tests
dotnet test
```

## S3 File Index Polling

The API maintains an in-memory index of all candle files stored in S3 for fast query processing (no `ListObjects` call per request). The index is built once at startup and then refreshed every **1 minute** by a background service (`FileIndexPollingService`).

This means:
- Files added externally (e.g. by the collector or importer) will be visible to the API within ~1 minute.
- Write operations through the API update the index immediately and do not wait for the next poll.
- The polling interval can be adjusted in `FileIndexPollingService` in `S3CandlesDemo.Api/Program.cs`.

## Additional Recommendations

- **Testing**: Define unit and integration tests for all major components, especially for file operations and API endpoints.
- **Error Handling**: Specify error handling strategies for file I/O, S3 operations, and API failures.
- **Performance**: Consider performance benchmarks for reading, writing, and merging operations.
- **Documentation**: Document the API endpoints, configuration options, and usage instructions.
- **Deployment**: Provide basic deployment instructions for running the service locally and in production.

---

*Last updated: April 15, 2026*
*Author: denisvlah*