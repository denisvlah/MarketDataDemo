# MarketDataDemo CSV Loader

## Purpose

The CsvLoader is a service designed to efficiently load historical OHLCV (Open, High, Low, Close, Volume) candle data from CSV files stored in AWS S3 and merge them into the S3-backed candles repository. It automatically detects gaps in existing candle data and fills only those gaps using the provided CSV files.

## Important: CSV File Symbol Name Matching

CSV files in S3 use **canonical symbol names without slashes** (e.g., `BTCUSD`, `ETHEUR`). The config file may contain symbols with slashes (e.g., `BTC/USD`, `ETH/EUR`). The loader must:

1. **Strip `/` from the config symbol** — e.g., `ETH/EUR` → `ETHEUR`
2. **Try alternate names** — for each config row, try matching CSV files using both the `Asset pair` column (e.g., `BTCUSD`) and the `Kraken pair` column with `/` removed (e.g., `XBT/USD` → `XBTUSD`). This handles cases where the canonical name differs from the Kraken name used to generate the CSV files.

All CSV filename lookups should use these normalized names.

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
- **Symbol**: Asset pair name **without slashes** (e.g., `ETHEUR`, `BTCUSD`)
- **IntervalMinutes**: Candle size in minutes (e.g., `1`, `5`, `60`, `1440`)
- **StartDateTime**: ISO 8601 format - `yyyy-MM-dd HH:mm:ss` (first candle timestamp)
- **EndDateTime**: ISO 8601 format - `yyyy-MM-dd HH:mm:ss` (last candle timestamp)

**Example:** `ETHEUR_60_2024-01-01 00:00:00_2024-12-31 23:59:59.csv`

### File Content Requirements
- **No header row** — data starts immediately with first record
- **Sorted by timestamp** — records must be in ascending time order
- **No gaps** — all expected candles for the time range must be present
- **Fixed 7 columns** (no variations):
  1. Timestamp (int32) — Unix seconds timestamp. **Must be read as `Int32`** to reject invalid/out-of-range values at parse time rather than silently producing wrong dates
  2. Open (double) — Opening price
  3. High (double) — Highest price
  4. Low (double) — Lowest price
  5. Close (double) — Closing price
  6. Volume (double) — Trading volume
  7. Trade count (int32) — Number of trades in the candle

**Example CSV row:**
```
1704067200,52000.50,52150.75,51900.25,52100.00,150000,42
```

## Configuration

### Unified Configuration File
The CsvLoader uses a **single unified configuration file** shared across all projects (Kraken Collector, CsvLoader, and other services) to define symbols and intervals:

**File location:** `config/kraken-collector-config.csv` key within the shared S3 bucket.

**Format:** CSV with columns (no header row):
- `Asset pair` — Canonical symbol name for storage (e.g., `BTCUSD`, `ETHUSD`)
- `Kraken pair` — Pair name as recognized by Kraken API (may differ from canonical name)
- `Interval` — Candle size in minutes (e.g., `1`, `5`, `60`, `1440`)
- `Start date` — Earliest date to collect from (`yyyy-MM-dd`)

Job config is now read via `ICandlesRepository.GetJobConfigAsync()`, which uses `PairJobConfig` as the shared record type. This eliminates the need for per-project config readers and ensures all projects process the same assets. The **Start date** column is used as the `minDate` parameter for `ICandlesRepository.GetGaps()` — gaps are only detected from this date forward.

### Environment Variables
- **WORKERS** (optional, default=1): Number of parallel worker threads for concurrent symbol/interval processing
- **ASPNETCORE_ENVIRONMENT** (default=Development): Controls which appsettings file is loaded (Development, Staging, Production)

### S3 Configuration
Configured via `appsettings.json` (or environment variables using `:` → `__` notation):
- **S3Candles:Bucket** — Single shared bucket for candles, CSV files, and config (under `candles/`, `csv/`, and `config/` prefixes)
- **S3Candles:Prefix** — Key prefix for binary candle files (always `candles`)
- **S3Candles:AWS:AccessKey** — AWS / MinIO access key
- **S3Candles:AWS:SecretKey** — AWS / MinIO secret key
- **S3Candles:AWS:Region** — AWS region (e.g., `us-east-1`)
- **S3Candles:AWS:Url** *(optional)* — Custom S3-compatible endpoint (e.g., MinIO `http://localhost:7000`). When set, forces path-style addressing and HTTP

### Example `appsettings.json`
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "S3Candles": {
    "Bucket": "your-candles-bucket",
    "Prefix": "candles",
    "AWS": {
      "AccessKey": "your-access-key",
      "SecretKey": "your-secret-key",
      "Region": "us-east-1"
    }
  }
}
```

### Example `appsettings.Development.json`
```json
{
  "S3Candles": {
    "Bucket": "minio-test-bucket",
    "Prefix": "candles",
    "AWS": {
      "AccessKey": "minioadmin",
      "SecretKey": "minioadmin",
      "Region": "us-east-1",
      "Url": "http://localhost:7000"
    }
  }
}
```

## Gap Detection & Filling Algorithm

1. **Queries the candles repository** to identify existing time ranges for each (symbol, interval) pair
2. **Detects gaps** in the existing data (missing time periods)
3. **Matches CSV files to gaps** by comparing each gap's time range with CSV filename ranges (no files are downloaded at this stage)
4. **Streams matching CSV files** directly from S3, seeking to the gap start and parsing only relevant records
5. **Stores gap candles** into the repository via `ICandlesRepository.StoreCandlesAsync()`
6. **Versioning** is handled automatically by the repository implementation

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
    int timestamp = csvReader.GetInt32(0);     // Column 1: Timestamp (Unix seconds, int32)
    double open = csvReader.GetDouble(1);      // Column 2: Open
    double high = csvReader.GetDouble(2);      // Column 3: High
    double low = csvReader.GetDouble(3);       // Column 4: Low
    double close = csvReader.GetDouble(4);     // Column 5: Close
    double volume = csvReader.GetDouble(5);    // Column 6: Volume
    int tradeCount = csvReader.GetInt32(6);    // Column 7: Trade count
    
    var candle = new Candle
    {
        Timestamp = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime,
        Open = open, High = high, Low = low, Close = close,
        Volume = volume, TradeCount = tradeCount
    };
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
4. App remains running until all symbols have been processed or a critical error occurs
5. Exits with code `0` on success, or non-zero on failure (see [Exit Codes](#exit-codes))

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


### Sample Log Output
```
[INFO] Gap detection started for 5 symbols
[INFO] ETH/EUR (60min): Detected 2 gaps covering 4,320 records
[INFO] BTC/USD (1min): No gaps detected
[INFO] ETH/EUR: Loading from csv-gap-2024-01-01_2024-06-30.csv
[INFO] ETH/EUR: 4,320 candles loaded and merged
[INFO] All gap-filling operations completed in 45.2s
```

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
dotnet run --project MarketDataDemo.CsvLoader --environment Development

# Build for AOT compilation (Release mode)
dotnet build -c Release

# Run AOT-compiled binary
./MarketDataDemo.CsvLoader/bin/Release/net10.0/linux-x64/publish/MarketDataDemo.CsvLoader
```

### Trigger Manual Load
```bash
# Start the app (loading begins automatically)
dotnet run --project MarketDataDemo.CsvLoader --environment Development

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

All four columns are used by the CSV loader: Asset pair and Kraken pair for symbol name matching (see [Symbol Name Matching](#important-csv-file-symbol-name-matching)), Interval for the candle size, and Start date as the `minDate` for gap detection.

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

- **S3 I/O errors on CSV open**: Retried with exponential backoff (3 attempts) before giving up on that file
- **Runtime errors during gap-filling**: If an unrecoverable error occurs mid-processing (e.g., corrupted stream, repository write failure), the app exits immediately with a non-zero exit code. The scheduler is expected to restart the app with a short delay; already-filled gaps will be skipped on the next run since the repository is up to date for those ranges
- **Missing CSV coverage**: If no CSV file covers a detected gap, logged as warning; processing continues for other symbols/intervals
- **Incomplete gaps**: Not all gaps may be filled if corresponding CSV files don't exist (e.g., most recent candles are typically missing from CSV sources); this is expected behavior and results in exit code 0
- **No retry within a run**: Apart from the initial CSV file open retry, failures are not retried within the same run. Recovery relies on the scheduler restarting the app

## Exit Codes

- **0**: All gap-filling completed successfully, or no gaps were found. Partial coverage due to missing CSV files is considered success
- **1**: Critical startup failure (invalid configuration, S3 unreachable, cannot load config file)
- **2**: Runtime error during gap-filling (I/O failure, corrupted data, repository write error). The scheduler should restart the app with a short delay — already-filled gaps will be skipped automatically

## Implementation Details

This project uses the **MarketDataDemo.Candles** library (`ICandlesRepository` / `CandlesRepositoryBase`) for all candle storage and gap detection.

### Startup Sequence
1. Load configuration from the unified config file in S3 (`kraken-collector-config.csv`)
2. Build the repository file index via `ICandlesRepository.RebuildFileIndexAsync()`
3. List all CSV file names from the `csv` S3 bucket (files are **not** downloaded — only their names are read to extract the time range from the filename)
4. Begin gap-filling for each (symbol, interval) pair

### Gap Detection
For each (symbol, interval) pair defined in the config, the loader calls `ICandlesRepository.GetGaps(symbol, intervalMinutes, minDate)` where `minDate` is the **Start date** value from the config row. This method inspects the in-memory file index and returns a list of `(Start, End)` ranges where no binary candle files exist. The gap list is computed purely from file metadata — no candle data is read at this stage.

### CSV-to-Gap Matching
Each CSV file's time range is encoded in its filename: `{Symbol}_{IntervalMinutes}_{Start}_{End}.csv`. The loader compares each gap's `(Start, End)` with the available CSV file ranges to find files that overlap the gap.

### Streaming & Seeking
When a matching CSV file is found, it is opened as a stream directly from S3 (never downloaded to disk). The stream is wrapped as `IAsyncEnumerable<Candle>` using the Sylvan CSV reader. The enumerator is advanced (skipping records) until the gap's start timestamp is reached, then records are fed into `ICandlesRepository.StoreCandlesAsync()` until the gap's end timestamp.

### Enumerator Reuse Across Gaps
A single CSV file may cover multiple consecutive gaps (e.g., if some gaps were partially filled by earlier runs). To avoid re-opening and re-seeking the same file, the loader **keeps the `IAsyncEnumerable<Candle>` enumerator and its last-read timestamp alive** between gaps. If the next gap falls within the same CSV file's range, iteration simply continues from where it left off. A new CSV stream is opened only when the current enumerator is exhausted or the next gap requires a different file.

> **Implementation note:** The enumerator reuse and gap-boundary yielding logic is complex. The implementing agent should leave this as a **`// TODO`** stub method (e.g., `YieldCandlesForGap(...)`) with the signature and doc comment in place but no implementation body. This will be implemented manually.

### Background Job & App Lifecycle
The gap-filling work is driven by a single `IHostedService` (e.g., `GapFillingService : BackgroundService`). On `ExecuteAsync`:
1. Load config and build the list of (symbol, interval) pairs
2. Create a `Task` for each pair, throttled by a `SemaphoreSlim(WORKERS)` to limit parallelism
3. `await Task.WhenAll(tasks)` — wait for every pair to finish
4. Call `Environment.Exit(0)` on success, or `Environment.Exit(2)` if any task threw an unrecoverable error

No coordination is needed between workers since each operates on a distinct (symbol, interval) key.

### Health Check
Implement using the standard `IHealthCheck` interface registered via `builder.Services.AddHealthChecks().AddCheck<GapFillingHealthCheck>("gap-filling")`. The health check tracks a `DateTime lastProgress` timestamp updated whenever a worker successfully stores candles. Reports `Unhealthy` if no progress for 5+ minutes.

### Versioning
Candle file versioning is handled entirely by `ICandlesRepository`. When `StoreCandlesAsync()` writes a new binary file for a time range that overlaps existing files, the repository automatically increments the version number. The loader does not manage versions directly.

## Project Setup

### Project Type
`Microsoft.NET.Sdk.Web` — Minimal ASP.NET API (same pattern as `MarketDataDemo.Api`).

### Required Project References
```xml
<ItemGroup>
  <ProjectReference Include="..\MarketDataDemo.Candles\MarketDataDemo.Candles.csproj" />
</ItemGroup>
```

### Required NuGet Packages
```xml
<ItemGroup>
  <PackageReference Include="Sylvan.Data.Csv" Version="*" />
  <PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="*" />
  <PackageReference Include="Scalar.AspNetCore" Version="*" />
</ItemGroup>
```

`AWSSDK.S3` is already a transitive dependency via `MarketDataDemo.Candles`.

### AOT & Trimming
```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
  <PublishAot>true</PublishAot>
  <InvariantGlobalization>true</InvariantGlobalization>
  <PublishTrimmed>true</PublishTrimmed>
  <IsAotCompatible>true</IsAotCompatible>
</PropertyGroup>
<PropertyGroup Condition="'$(Configuration)' == 'Debug'">
  <PublishAot>false</PublishAot>
</PropertyGroup>
```


