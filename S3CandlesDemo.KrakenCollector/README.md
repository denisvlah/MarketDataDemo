# S3CandlesDemo.KrakenCollector

Collects OHLCV candle data from the [Kraken REST API](https://docs.kraken.com/api/docs/rest-api/get-ohlc-data) and stores it in S3 using the shared `S3CandlesDemo.Candles` binary format.

## Requirements

### Configuration Source

A CSV file `kraken-collector-config.csv` defines the collection jobs. It has 4 columns (no header row):

| Column | Type | Description | Example |
|--------|------|-------------|---------|
| Asset pair | string | Canonical pair name used for storage in S3 | `BTCUSD` |
| Kraken pair | string | Pair name as recognized by the Kraken API (may differ from canonical name) | `XBTUSD` |
| Interval | int | Candle size in minutes — must be one of Kraken's supported intervals: `1`, `5`, `15`, `30`, `60`, `240`, `1440`, `10080`, `21600` | `60` |
| Start date | date | Earliest date to collect from (`yyyy-MM-dd`) | `2024-01-01` |

The **Asset pair** column is the name used when calling `ICandlesRepository` (the symbol stored in S3 filenames). The **Kraken pair** column is the name sent to the Kraken API. When there is no naming difference, both columns have the same value.

**Example `kraken-collector-config.csv`:**
```csv
BTCUSD,XBTUSD,60,2024-01-01
ETHUSD,ETHUSD,1440,2024-06-01
SOLUSD,SOLUSD,15,2025-01-01
```

The config CSV is loaded from **S3** using the `S3Candles:ConfigBucket` and `S3Candles:ConfigKey` settings. This allows centralised management of the collection schedule without redeploying the app. If these settings are empty, the app falls back to a **local file** in the project output directory.

### Collection Logic

1. **On startup**, read `kraken-collector-config.csv` and register a collection job for each row.
2. For each job (asset pair + interval):
   - Query S3 (`ICandlesRepository`) for existing candle files to find the **last stored candle timestamp**.
   - If candles already exist, resume from that timestamp (incremental update).
   - If no candles exist, start from the **start date** in the CSV.
   - Collect candles up to **the beginning of the current UTC day** (`DateTime.UtcNow.Date`).
3. **Kraken API pagination**: The OHLC endpoint returns a maximum of **720 entries** per request. Use the `since` (unix timestamp) parameter to paginate through historical data in a loop until the target date is reached.
4. **Discard the last entry** in each Kraken OHLC response — it represents the current, not-yet-committed candle.
5. Store each batch of candles to S3 via `ICandlesRepository.StoreCandlesAsync()`.
6. After all jobs complete, the application **exits with code 0**.

### Kraken API Details

- **Endpoint**: `GET https://api.kraken.com/0/public/OHLC?pair={pair}&interval={interval}&since={since}`
- **Public API** — no authentication required.
- **Response format**: Each tick is `[time, open, high, low, close, vwap, volume, count]` where prices are strings, time and count are integers.
- **Rate limiting**: Respect Kraken's public API rate limits. Add a small delay between requests (e.g., 1-2 seconds).
- **`since` parameter**: Unix timestamp (seconds). The response includes a `last` field to use as the next `since` value for pagination.

### NuGet SDK

Use the [`KrakenExchange.Net`](https://www.nuget.org/packages/KrakenExchange.Net) package (by Jkorf, v7.9+) — a well-maintained, strongly-typed .NET client for Kraken with built-in rate limiting.

### Scheduling & Lifecycle

- This app is designed to run as a **one-shot batch job**, not a long-running service.
- It will be scheduled externally (e.g., cron, Kubernetes CronJob) to run daily at **00:01 UTC**.
- The health check endpoint (`/health`) is available for orchestration probes during execution.
- On completion, exit with code `0` (success) or `1` (failure).

### Error Handling

- If the config CSV is missing or malformed, log an error and exit with code `1`.
- If a Kraken API call fails, retry up to 3 times with exponential backoff before failing the job.
- Partial progress is preserved — candles already stored in S3 remain; the next run will resume from the last stored timestamp.
- Log each job's progress: pair, interval, candles fetched, time range covered.

### Integration testing
This project has a bunch of integration tests in the S3CandlesDemo.Tests project.

I uses minio to simulate the s3 storage.

It test the internal logic of collector classes and cleans up the mess after test finish.

## Quick Start

```bash
# Start MinIO (from repo root)
bash startMinio.sh

# Run the collector
dotnet run --project S3CandlesDemo.KrakenCollector

# Health check
curl http://localhost:5099/health

# Scalar API docs (dev only)
# http://localhost:5099/scalar/v1
```
