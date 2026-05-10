# AI Coding Agent Instructions for S3CandlesDemo

## General Agent Behavior

1. **Research before implementing** — always check for the latest library changes and best practices before writing code. Verify current API signatures for AWS SDK, .NET, and other dependencies.
2. **Challenge questionable requests** — if asked to do something insecure (e.g., exposing S3 credentials), wrong, or likely to cause performance degradation (e.g., breaking `IAsyncEnumerable` streaming with `.ToList()`), explain the consequences first and proceed only after explicit user confirmation.
3. **Minimal modifications only** — never do full rewrites. Add small, targeted changes to existing code. This is critical because the binary serialization format (52-byte records) and streaming patterns are tightly coupled.
4. **Inline documentation preferred** — write documentation as source code comments, not separate files, unless the user explicitly asks otherwise. Keep comments minimal and purposeful.

## Project Overview

This is a **C# .NET 10.0 project** that implements efficient OHLCV (Open, High, Low, Close, Volume) candle data storage and querying using binary file format. The system supports both filesystem and AWS S3 backends with memory-efficient streaming via `IAsyncEnumerable<T>`.

### Core Architecture

**Three-project structure:**
- **S3CandlesDemo.Api** (ASP.NET Minimal API): HTTP endpoints for candle storage/retrieval using Scalar OpenAPI docs
- **S3CandlesDemo.Candles** (Core library): `ICandlesRepository` interface with filesystem and S3 implementations
- **S3CandlesDemo.Tests** (xUnit): Unit and integration tests for all repository implementations

## Key Concepts & Patterns

### 1. Binary Serialization Pattern
- **Location**: [../S3CandlesDemo.Candles/Candle.cs](../S3CandlesDemo.Candles/Candle.cs)
- Candles are serialized as fixed-size binary records: 8 bytes (timestamp ticks) + 40 bytes (5 doubles) + 4 bytes (trade count) = **52 bytes total**
- Use `Candle.CandleToBytes()` and `Candle.BytesToCandle()` for conversions
- Zero JSON serialization overhead for storage; JSON only used in HTTP responses
- **Key field names in JSON responses** (short form): `t` (timestamp), `o` (open), `h` (high), `l` (low), `c` (close), `v` (volume), `n` (trade count)

### 2. Repository Pattern with Template Method
- **Location**: [../S3CandlesDemo.Candles/CandlesRepositoryBase.cs](../S3CandlesDemo.Candles/CandlesRepositoryBase.cs)
- Abstract base class defines file naming: `{Symbol}_{Interval}_{StartDateTime}_{EndDateTime}_v{Version}.bin`
- Datetime format in filenames: `yyyyMMdd'T'HHmmss` (e.g., `20240101T120000`)
- File index is cached in `ConcurrentDictionary<(symbol, interval), List<CandleFileInfoInternal>>` for fast lookups
- Implementations ([../S3CandlesDemo.Candles/FileSystemCandlesRepository.cs](../S3CandlesDemo.Candles/FileSystemCandlesRepository.cs), [../S3CandlesDemo.Candles/S3CandlesRepository.cs](../S3CandlesDemo.Candles/S3CandlesRepository.cs)) override only:
  - `EnumerateFiles()` - list available binary files
  - `OpenWriteStreamAsync()` - temp file creation for writes
  - `MoveTempToFinalAsync()` - atomic rename after write completion
  - `OpenReadStreamAsync()` - open at specific offset for partial reads

### 3. Streaming & Memory Efficiency
- **All data flows** use `IAsyncEnumerable<Candle>` to avoid loading entire datasets in memory
- HTTP fetch endpoint streams JSON-serialized candles directly to response body
- Store operations accept both `IEnumerable<Candle>` and `IAsyncEnumerable<Candle>`
- No temporary allocations; uses fixed 52-byte buffer for serialization

### 4. File Index Polling (S3)
- **Location**: `FileIndexPollingService` in [../S3CandlesDemo.Api/Program.cs](../S3CandlesDemo.Api/Program.cs)
- The `S3CandlesRepository` builds its in-memory `_fileIndex` once on startup via `BuildFileIndexAsync()`
- A background `IHostedService` (`FileIndexPollingService`) calls `ICandlesRepository.RebuildFileIndexAsync()` every **1 minute**
- This keeps the index current without the overhead of calling `S3:ListObjects` on every API request
- `RebuildFileIndexAsync()` is exposed on `ICandlesRepository` and implemented in `CandlesRepositoryBase` (delegates to `BuildFileIndexAsync()`)
- Do **not** add per-request index rebuilds; always rely on the polling service or an explicit store operation (which updates `_fileIndex` directly)

### 5. S3 Configuration
- **Location**: [../S3CandlesDemo.Api/appsettings.json](../S3CandlesDemo.Api/appsettings.json)
- **Dev Configuration**: Local MinIO (Docker) via `minio-dockercompose.yaml` and `startMinio.sh`
- Required settings: `S3Candles:Bucket`, `S3Candles:Prefix`, `S3Candles:AWS:{AccessKey,SecretKey,Region}`
- Optional: `S3Candles:AWS:Url` for custom S3-compatible endpoints (MinIO); forces path-style and HTTP
- **Startup Behavior**: If S3 config is incomplete, logs critical error and exits with code 1
- **Single-bucket layout**: All data lives in one bucket under three fixed prefixes:
  - `candles/` — binary `.bin` candle files
  - `csv/` — CSV source files for `S3CandlesDemo.CsvLoader`
  - `config/` — job config CSV (`config/kraken-collector-config.csv`)
- **No `ConfigBucket`, `CsvBucket`, or `ConfigKey` settings** — these have been removed. Use only `Bucket` (single bucket) and `Prefix: "candles"`.

## Critical Development Workflows

### Build & Run
```bash
# Development build (debug, AOT disabled for Swagger in Program.cs)
dotnet build

# Run tests
dotnet test

# Start API locally (uses appsettings.Development.json by default)
dotnet run --project S3CandlesDemo.Api

# Start MinIO backend for S3 testing
bash startMinio.sh  # or docker-compose up -f minio-dockercompose.yaml
```

### Testing Strategy
- **xUnit** framework with global using [../S3CandlesDemo.Tests/GlobalUsings.cs](../S3CandlesDemo.Tests/GlobalUsings.cs)
- Test patterns: [../S3CandlesDemo.Tests/CandleSerializationTests.cs](../S3CandlesDemo.Tests/CandleSerializationTests.cs) (unit), [../S3CandlesDemo.Tests/FileSystemCandlesRepositoryTests.cs](../S3CandlesDemo.Tests/FileSystemCandlesRepositoryTests.cs) (repository), [../S3CandlesDemo.Tests/S3CandlesRepositoryIntegrationTests.cs](../S3CandlesDemo.Tests/S3CandlesRepositoryIntegrationTests.cs) (requires MinIO)
- Binary round-trip tests validate serialization precision
- File naming regex validation in repository tests

### CSV Data Loading
- **Startup behavior**: [../S3CandlesDemo.Api/Program.cs](../S3CandlesDemo.Api/Program.cs) initializes `ICandlesRepository` before app runs
- CSV files in `csv/` folder follow pattern: `{Symbol}_{IntervalMinutes}.csv`
- If binary files already exist for a symbol/interval, CSV is skipped (no duplicates)
- CSV loading is logged; failures should be logged but don't crash startup

## Important Implementation Details

### File Versioning & Conflicts
- When storing candles for existing (symbol, interval) pair, new version number increments
- Multiple overlapping time-range files can coexist; fetch merges them automatically
- S3 rebuild index on every `GetCandleFilesAsync()` call to catch external modifications

### Seek-Based Partial Reads
- Repository implementations support offset-based file reads via `OpenReadStreamAsync(path, offset)`
- Used internally to skip candles before the requested time range
- Offset calculated: `recordIndex * Candle.CandleByteSize`

### Error Handling Pattern
- Stream operations are wrapped in `await using` for guaranteed cleanup
- Temp files cleaned up on write failure or empty datasets
- S3 operations use AWS SDK cancellation token support
- API returns `Results.Ok()` for success, unhandled exceptions become HTTP 500

### JSON Serialization Context
- **Location**: [../S3CandlesDemo.Api/Program.cs](../S3CandlesDemo.Api/Program.cs) line ~51
- Uses `AppJsonSerializerContext.Default.Candle` for trimmed/AOT-compatible serialization
- Enables `PublishAot=true` and `InvariantGlobalization=true` in Release build
- In Debug, AOT is disabled to allow Swagger docs

## Common Modifications

**Adding a new repository backend**: Inherit `CandlesRepositoryBase`, implement 5 abstract methods (EnumerateFiles, GetFileName, OpenWrite/ReadStreamAsync, MoveTempToFinal), and override `GetJobConfigAsync`.

**Adding HTTP endpoints**: [../S3CandlesDemo.Api/Program.cs](../S3CandlesDemo.Api/Program.cs) MapPost/MapGet at lines 60+. Use `ICandlesRepository` injected from DI container.

**Adjusting file size limits**: Logic in `CandlesRepositoryBase.StoreCandlesAsync()` around version increment; no hard limit currently enforced.

**Performance debugging**: Check `_fileIndex` cache; if slow, ensure `BuildFileIndex()` completes before large queries.

**Reading job config**: All projects use `await _repository.GetJobConfigAsync()` (returns `IReadOnlyList<PairJobConfig>`) instead of project-local config readers. `PairJobConfig` and `PairJobConfigReader` are defined in `S3CandlesDemo.Candles`. The S3 key is hard-coded as `config/kraken-collector-config.csv`.
