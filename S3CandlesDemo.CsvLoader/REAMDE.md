# S3CandlesDemo CSV Loader

## Purpose

The CsvLoader is a service designed to efficiently load historical OHLCV (Open, High, Low, Close, Volume) candle data from CSV files stored in AWS S3 and merge them into the S3-backed candles repository. It automatically detects gaps in existing candle data and fills them using the provided CSV files.

## Architecture & Design

### Memory-Efficient Streaming
- Uses **Sylvan.Data.Csv** library for near-zero memory allocation
- CSV data streams directly from S3 without ever being downloaded to disk
- Records are processed one at a time using an async enumerable pattern
- Entire large CSV files are never loaded into memory

### Parallel Processing
- Configurable worker degree via `WORKERS` environment variable
- Multiple symbols/intervals can be processed concurrently
- Optimized for I/O-bound S3 operations

## CSV File Format

### Source Bucket
CSV files are located in a **separate S3 bucket** named `csv`.

### File Naming Convention
```
{Symbol}_{IntervalMinutes}_{StartDateTime}_{EndDateTime}.csv
```

**Components:**
- **Symbol**: Asset pair name (e.g., `ETH/EUR`, `BTC/USD`)
- **IntervalMinutes**: Candle size in minutes (e.g., `1`, `5`, `60`, `1440`)
- **StartDateTime**: ISO 8601 format - `yyyy-MM-dd HH:mm:ss` (first candle timestamp)
- **EndDateTime**: ISO 8601 format - `yyyy-MM-dd HH:mm:ss` (last candle timestamp)

**Example:** `ETH/EUR_60_2024-01-01 00:00:00_2024-12-31 23:59:59.csv`

### File Content Requirements
- **No header row** — data starts immediately with first record
- **Sorted by timestamp** — records must be in ascending time order
- **No gaps** — all expected candles for the time range must be present
- **Fixed 6 columns** (no variations):
  1. Timestamp (long) — Unix seconds timestamp
  2. Open (double) — Opening price
  3. High (double) — Highest price
  4. Low (double) — Lowest price
  5. Close (double) — Closing price
  6. Volume (long) — Trading volume / trade count

**Example CSV row:**
```
1704067200,52000.50,52150.75,51900.25,52100.00,150000
```

## Configuration

### Unified Configuration File
The CsvLoader uses a **single unified configuration file** shared across all projects (Kraken Collector, CsvLoader, and other services) to define symbols and intervals:

**File location:** `candles-config` S3 bucket → `kraken-collector-config.csv`

**Format:** CSV with columns (no header row):
- `Asset pair` — Canonical symbol name for storage (e.g., `BTCUSD`, `ETHUSD`)
- `Kraken pair` — Pair name as recognized by Kraken API (may differ from canonical name)
- `Interval` — Candle size in minutes (e.g., `1`, `5`, `60`, `1440`)
- `Start date` — Earliest date to collect from (`yyyy-MM-dd`)

This single configuration source eliminates symbol synchronization issues across multiple services and ensures all projects process the same assets. The CsvLoader reads this same file to fill data gaps.

### Environment Variables
- **WORKERS** (optional, default=1): Number of parallel worker threads for concurrent symbol/interval processing
- **ASPNETCORE_ENVIRONMENT** (default=Development): Controls which appsettings file is loaded (Development, Staging, Production)
- **S3Candles:ConfigBucket** (optional): S3 bucket containing the config file (default: `candles-config`)
- **S3Candles:ConfigKey** (optional): S3 key for the config file (default: `kraken-collector-config.csv`)

### S3 Configuration
- **Source bucket**: Must contain CSV files following the naming convention above
- **Target bucket**: The candles repository where merged candles are stored
- Credentials and endpoint settings should be configured via appsettings or environment variables

## Gap Detection & Filling Algorithm

1. **Queries the candles repository** to identify existing time ranges for each (symbol, interval) pair
2. **Detects gaps** in the existing data (missing time periods)
3. **Scans CSV bucket** for files that cover detected gaps
4. **Streams matching CSV files** directly from S3, parsing only relevant records
5. **Merges new candles** into the repository without re-downloading or modifying existing data
6. **Updates version numbers** for affected candle files as per repository versioning policy

## Sylvan.Data.Csv Library

The CsvLoader is built on **Sylvan.Data.Csv**, a high-performance, zero-allocation CSV parser:

### Key Benefits
- **Ultra-low memory footprint**: Uses ref structs and `ValueTask` patterns
- **Direct S3 streaming**: Works seamlessly with `Stream` objects from S3
- **Headerless CSV support**: Native support for files without header rows
- **Fast enumeration**: Optimized for sequential record processing
- **No temporary allocations**: Suitable for high-throughput scenarios

### Usage Pattern
```csharp
// CSV streams directly from S3 into the parser
var opts = new CsvDataReaderOptions { HasHeaders = false };
using var csvReader = CsvDataReader.Create(s3Stream, opts);

while (csvReader.Read())
{
    long timestamp = csvReader.GetInt64(0);  // Column 1: Timestamp
    double open = csvReader.GetDouble(1);    // Column 2: Open
    double high = csvReader.GetDouble(2);    // Column 3: High
    double low = csvReader.GetDouble(3);     // Column 4: Low
    double close = csvReader.GetDouble(4);   // Column 5: Close
    long volume = csvReader.GetInt64(5);     // Column 6: Volume
    
    // Process candle record
}
```

## Deployment Architecture

### Implementation Pattern
The CsvLoader is implemented as a **minimal ASP.NET API** (no controllers, pure endpoint routing) optimized for:
- **AOT Compilation**: Fully AOT-compiled for minimal runtime overhead and startup time
- **Scheduled Execution**: Designed to run once daily via external scheduler (cron, Azure Scheduler, AWS Lambda, or Kubernetes CronJob)
- **Automatic Start**: Gap-filling begins immediately on app startup; no manual trigger required
- **Health Check Endpoint**: Exposes `/health` endpoint for scheduler validation (indicates loading progress and app health)

### Startup Behavior
On application start:
1. App initializes and loads configuration from S3
2. Gap-filling process **starts immediately** in the background
3. `/health` endpoint becomes available for monitoring
4. App remains running until all symbols have been processed (success or failure)
5. Exits with code `0` regardless of outcome

### Health Check Behavior
The `/health` endpoint returns:
- **Status 200 (Healthy)**: Loading is in progress or completed successfully
- **Status 503 (Unhealthy)**: No progress detected for 5+ minutes (indicates stuck or failed operation)

**Progress Detection:** Updated whenever candles are successfully merged for any symbol/interval. If no updates occur within 5 minutes, the service is considered unhealthy.

### Example Scheduled Invocation
```bash
# Cron job running daily at 2 AM UTC
# The app will start, run automatically, and exit when complete
0 2 * * * docker run --rm csvloader-image

# Or with Kubernetes CronJob:
# spec:
#   schedule: "0 2 * * *"
#   jobTemplate:
#     spec:
#       template:
#         spec:
#           containers:
#           - name: csvloader
#             image: csvloader-image
#           restartPolicy: Never
```

## Logging Strategy

The CsvLoader includes **structured, non-verbose logging** suitable for scheduled batch operations:

### Log Levels
- **Information**: Gap detection results, file counts, merge operations started/completed
- **Warning**: Malformed CSV records, missing S3 files, retry attempts
- **Error**: S3 connectivity failures, corrupted data, load failures for a symbol/interval pair

### Sample Log Output
```
[INFO] Gap detection started for 5 symbols
[INFO] ETH/EUR (60min): Detected 2 gaps covering 4,320 records
[INFO] BTC/USD (1min): No gaps detected
[INFO] ETH/EUR: Loading from csv-gap-2024-01-01_2024-06-30.csv
[INFO] ETH/EUR: 4,320 candles loaded and merged
[INFO] All gap-filling operations completed in 45.2s
```

### Log Configuration
- Logs are written to console (container-friendly)
- Structured JSON logging available via `Serilog` for external aggregation (ELK, CloudWatch, etc.)
- Log level configurable via `appsettings.json` or `LOG_LEVEL` environment variable

## Development & Local Testing

### MinIO Setup
In development mode, the CsvLoader works with **MinIO** (S3-compatible storage):
- MinIO is started via the main `docker-compose.yml`
- Alternatively, use `startMinio.sh` script to launch MinIO directly
- Configuration: See `appsettings.Development.json`

### Running Locally
```bash
# Build the project (standard build, AOT enabled in Release)
dotnet build

# Set environment variables (optional)
export WORKERS=4
export LOG_LEVEL=Information

# Run with development settings
dotnet run --project S3CandlesDemo.CsvLoader --environment Development

# Build for AOT compilation (Release mode)
dotnet build -c Release

# Run AOT-compiled binary
./S3CandlesDemo.CsvLoader/bin/Release/net10.0/linux-x64/publish/S3CandlesDemo.CsvLoader
```

### Trigger Manual Load
```bash
# Start the app (loading begins automatically)
dotnet run --project S3CandlesDemo.CsvLoader --environment Development

# In another terminal, check health status
curl http://localhost:5043/health
```

### Configuration File Format
The loader reads symbols from the unified `kraken-collector-config.csv` in the `candles-config` S3 bucket. Each row defines a symbol/interval pair to check for gaps:

```csv
BTCUSD,XBTUSD,60,2024-01-01
ETHUSD,ETHUSD,1440,2024-06-01
SOLUSD,SOLUSD,15,2025-01-01
```

The first three columns (Asset pair, Kraken pair, Interval) are used by the CSV loader to identify gaps. The Start date is for reference by the Kraken collector.

### Testing with Sample Data
1. Upload CSV files to the MinIO `csv` bucket following the naming convention
2. The loader will automatically detect gaps and populate the candles repository
3. Monitor logs for import progress and any validation errors

## Performance Considerations

- **Streaming reduces memory**: CSV files are never fully loaded; only one record at a time
- **S3 I/O optimized**: Parallel workers reduce overall wall-clock time for large imports
- **No disk usage**: Files stream directly from S3 to repository; no temporary files
- **Worker scaling**: Increase `WORKERS` environment variable for higher throughput (balance against S3 rate limits)

## Error Handling & Resilience

- **Invalid CSV format**: Malformed records are logged with line number and skipped; file processing continues
- **Timestamp/numeric errors**: Gracefully logged; record skipped; next record processed
- **Missing symbol**: If no CSV file exists for a detected gap, logged as warning; processing continues for other symbols
- **S3 failures**: Transient failures trigger exponential backoff retry (3 attempts); persistent failures are logged and gap-filling skipped for that symbol
- **Repository write errors**: Failed candle merges are logged; processing continues for other gaps
- **Incomplete gaps**: Not all gaps may be filled if corresponding CSV files don't exist (e.g., most recent candles are typically missing from CSV sources); this is expected behavior
- **Partial completion**: If some symbols load successfully and others fail, the app still exits with code 0

## Exit Codes

- **0**: App completed execution (successfully filled gaps, partial completion, or no gaps found)
  - Most recent candles may be missing from CSV files — this is expected and does not cause non-zero exit
- **1**: Critical startup failure (invalid configuration, S3 unreachable, cannot load config file)
- **2**: Critical runtime error (app crashed during gap-filling)