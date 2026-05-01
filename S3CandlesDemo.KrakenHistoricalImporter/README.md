# S3CandlesDemo.KrakenHistoricalImporter

Downloads all quarterly OHLCVT ZIP archives from [Kraken's Google Drive folder](https://drive.google.com/drive/folders/15RSlNuW_h0kVM8or8McOGOMfHeBFvFGI), extracts the CSV files, renames them to match the `S3CandlesDemo.CsvLoader` naming convention, and uploads them to the S3 `csv/` folder.

> **Note:** After uploading, run `S3CandlesDemo.CsvLoader` to convert the CSVs into the binary candle format consumed by the API.

## How It Works

1. **Lists existing CSV files** in S3 (`csv/` prefix) to build a skip-set — files already uploaded are not re-uploaded.
2. **Lists all files** in the Google Drive folder via the Drive API v3. Filters to quarterly ZIP archives (`Kraken_OHLCVT_Q{quarter}_{year}.zip`).
3. **Downloads each ZIP** to the local temp directory. Already-cached ZIPs are reused; corrupt ones are re-downloaded.
4. **Extracts each ZIP** to a subdirectory. Already-extracted directories are reused.
5. For every `.csv` file found inside the archive:
   - Parses the Kraken filename (`{KrakenPair}_{IntervalMinutes}.csv`) to get the symbol and interval.
   - Reads the **first and last rows** to determine the exact timestamp range.
   - Renames to `{KrakenPair}_{IntervalMinutes}_{StartDateTime}_{EndDateTime}.csv` (datetime format: `yyyy-MM-dd HH:mm:ss`).
   - **Uploads** the renamed file to `csv/` in S3 (skips if already present).
6. **Exits** with code `0` (success) or `1` (partial failure).

## CSV Naming Convention

The `S3CandlesDemo.CsvLoader` expects files named:

```
{Symbol}_{IntervalMinutes}_{StartDateTime}_{EndDateTime}.csv
```

Example: `XBTUSD_60_2023-01-01 00:00:00_2023-03-31 23:59:00.csv`

The importer derives start and end datetimes directly from the first and last rows of each CSV, so the filename always reflects actual data coverage.

## Configuration

### `appsettings.json`

```json
{
  "S3Candles": {
    "Bucket": "candles-data",
    "AWS": {
      "AccessKey": "...",
      "SecretKey": "...",
      "Region": "us-east-1",
      "Url": "http://localhost:7000"
    }
  },
  "HistoricalImport": {
    "TempDirectory": "/tmp/kraken-historical",
    "GoogleApiKey": "your-google-api-key"
  }
}
```

| Setting | Description |
|---------|-------------|
| `S3Candles:Bucket` | S3 bucket that holds all data (`csv/`, `candles/`, `config/` prefixes) |
| `S3Candles:AWS:Url` | Optional — custom endpoint for MinIO or other S3-compatible stores |
| `HistoricalImport:TempDirectory` | Local directory for ZIP downloads and extraction |
| `HistoricalImport:GoogleApiKey` | Google Drive API v3 key (free, from Google Cloud Console) — required |

### Getting a Google API Key

1. Go to [Google Cloud Console](https://console.cloud.google.com/) → APIs & Services → Credentials.
2. Create an API key and restrict it to the **Google Drive API**.
3. Set `HistoricalImport:GoogleApiKey` to this key.

## Source Data Format

Kraken archives are available at: `https://drive.google.com/drive/folders/15RSlNuW_h0kVM8or8McOGOMfHeBFvFGI`

- **Archive naming**: `Kraken_OHLCVT_Q{1-4}_{year}.zip`
- **CSV naming inside archives**: `{KrakenPair}_{IntervalMinutes}.csv` (e.g. `XBTUSD_60.csv`)
- **CSV format**: `timestamp,open,high,low,close,volume,trades` — no header row, timestamp is Unix seconds
- **Available intervals**: 1, 5, 15, 30, 60, 240, 720, 1440 minutes

## Scheduling & Lifecycle

- Designed as a **one-shot batch job** — run it once to seed the `csv/` folder with all available history.
- Safe to re-run — already-uploaded files are detected via S3 key lookup and skipped.
- The health check endpoint (`/health`) is available for orchestration probes during execution.
- Requires ephemeral disk space for ZIP files and extracted CSVs (~200–550 MB per quarterly archive).

## Error Handling

- If `GoogleApiKey` is missing, exits immediately with code `1`.
- If a single archive fails (download or extraction error), the error is logged and remaining archives continue.
- Already-uploaded CSV keys are tracked in memory — a process restart will re-query S3 on the next run.

## Quick Start

```bash
# Start MinIO (from repo root)
bash startMinio.sh

# Run the importer
dotnet run --project S3CandlesDemo.KrakenHistoricalImporter

# Health check
curl http://localhost:5098/health
```

