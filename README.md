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
| **S3CandlesDemo.KrakenLatestCollector** | One-shot batch job — collects latest OHLCV data from Kraken API and stores to S3 |
| **S3CandlesDemo.KrakenHistoricalImporter** | One-shot batch job — imports full historical OHLCV data from Kraken's Google Drive archive into S3 |
| **S3CandlesDemo.Tests** | xUnit tests — unit, repository, and integration tests (uses MinIO via Testcontainers) |

## Docker Compose Deployment

A full-stack `docker-compose.yml` runs all services together with MinIO as the S3 backend:

| Service | Purpose | Port |
|---------|---------|------|
| `minio` | S3-compatible storage (persistent volume) | `9000` (API), `9001` (console) |
| `minio-setup` | Init container — creates buckets, seeds `kraken-collector-config.csv` | — |
| `api` | Candles HTTP API | `5044` |
| `kraken-collector` | Kraken data collector (runs once, then exits) | `5099` (health) |

```bash
# Start everything
docker compose up -d

# Watch logs
docker compose logs -f

# Re-run the collector on demand
docker compose restart kraken-collector

# MinIO console
open http://localhost:9001  # minioadmin / minioadmin
```

The collector reads its schedule from `kraken-collector-config.csv` stored in the `candles-config` S3 bucket. On first start, `minio-setup` seeds it from `S3CandlesDemo.KrakenLatestCollector/kraken-collector-config.csv`. Edit it via the MinIO console without rebuilding images.

## Local Development (without Docker)

```bash
# Start MinIO only
bash startMinio.sh

# Run API (uses appsettings.Development.json → localhost:7000)
dotnet run --project S3CandlesDemo.Api

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