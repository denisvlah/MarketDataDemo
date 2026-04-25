# S3CandlesDemo.KrakenHistoricalImporter

Imports **historical** OHLCVT candle data from [Kraken's quarterly Google Drive archives](https://drive.google.com/drive/folders/15RSlNuW_h0kVM8or8McOGOMfHeBFvFGI) and stores it in S3 using the shared `S3CandlesDemo.Candles` binary format.

> **Note:** For collecting the latest/incremental candle data from Kraken's REST API, use the separate `S3CandlesDemo.KrakenLatestCollector` project.

## How It Works

1. **Reads config** from a CSV file in S3 (`S3Candles:ConfigBucket` / `S3Candles:ConfigKey`), same format as `S3CandlesDemo.KrakenLatestCollector`. Falls back to a local file if S3 config is not set.
2. For each configured pair/interval, **queries S3** (`ICandlesRepository`) to find the latest stored candle timestamp.
3. **Determines which quarterly ZIP files** are needed — only quarters that could contain data newer than what's already stored. Available archives: `Kraken_OHLCVT_Q{1-4}_{year}.zip` from Q1 2023 onward.
4. **Downloads only the required ZIPs** from Google Drive to a temporary directory (skips already-downloaded files).
5. **Extracts** each ZIP and **parses** the relevant CSV (format: `timestamp,open,high,low,close,volume,trades` — no header row).
6. **Filters** out candles already covered by existing S3 data and **stores** only the missing candles.
7. **Exits** with code `0` (success) or `1` (failure).

## Configuration

### Configuration Source

The importer uses the **unified configuration file** shared across all projects (`kraken-collector-config.csv`), with identical format to `S3CandlesDemo.KrakenLatestCollector`. It has 4 columns (no header row):

| Column | Type | Description | Example |
|--------|------|-------------|---------|
| Asset pair | string | Canonical pair name used for storage in S3 | `BTCUSD` |
| Kraken pair | string | Pair name as it appears in the archive filenames | `XBTUSD` |
| Interval | int | Candle size in minutes | `60` |
| Start date | date | Earliest date to import from (`yyyy-MM-dd`) | `2023-01-01` |

The config CSV is loaded from **S3** using the `S3Candles:ConfigBucket` and `S3Candles:ConfigKey` settings. If these settings are empty, the app falls back to a **local file** in the project output directory. All projects (Kraken Collector, Historical Importer, CSV Loader) share this single `kraken-collector-config.csv` configuration.

### `appsettings.json`

```json
{
  "S3Candles": {
    "Bucket": "candles-data",
    "Prefix": "kraken-candles",
    "ConfigBucket": "candles-config",
    "ConfigKey": "kraken-collector-config.csv",
    "AWS": {
      "AccessKey": "...",
      "SecretKey": "...",
      "Region": "us-east-1",
      "Url": "http://localhost:7000"
    }
  },
  "HistoricalImport": {
    "TempDirectory": "/tmp/kraken-historical"
  }
}
```

| Setting | Description |
|---------|-------------|
| `S3Candles:ConfigBucket` | S3 bucket containing the config CSV |
| `S3Candles:ConfigKey` | S3 key for the config CSV |
| `HistoricalImport:TempDirectory` | Local directory for downloading and extracting ZIP archives |

### Available Intervals

The Kraken archive includes CSVs for intervals: `1`, `5`, `15`, `30`, `60`, `240`, `720`, `1440` minutes.

### Quarterly Archive Naming

ZIP files on Google Drive follow the pattern: `Kraken_OHLCVT_Q{quarter}_{year}.zip`

Inside each ZIP, CSV files are named `{KrakenPair}_{IntervalMinutes}.csv` (e.g., `XBTUSD_60.csv`).

### Google Drive File IDs

The importer resolves quarterly ZIP file IDs from a built-in lookup table covering Q1 2023 through the latest available quarter. New quarters can be added to the `QuarterlyArchives` dictionary in `HistoricalImporter.cs` without changing the config.

## Scheduling & Lifecycle

- Designed to run as a **one-shot batch job** (not a long-running service).
- Safe to run repeatedly — only downloads ZIPs and imports candles that are missing.
- Schedule it alongside `S3CandlesDemo.KrakenLatestCollector` to fill gaps the API can't cover.
- The health check endpoint (`/health`) is available for orchestration probes during execution.
- Requires ephemeral disk space proportional to the number of quarterly ZIPs needed (200-550 MB each).

## Error Handling

- If the config CSV is missing or malformed, log an error and exit with code `1`.
- If a Google Drive download fails, the job for that quarter fails but others continue.
- Already-downloaded ZIPs are reused on re-run (idempotent downloads).
- Each pair/interval is processed independently — a failure in one doesn't block others.
- Partial progress is preserved — candles already stored in S3 remain; the next run resumes.

## Quick Start

```bash
# Start MinIO (from repo root)
bash startMinio.sh

# Run the importer
dotnet run --project S3CandlesDemo.KrakenHistoricalImporter

# Health check
curl http://localhost:5098/health
```
