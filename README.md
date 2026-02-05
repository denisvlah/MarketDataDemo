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

## Additional Recommendations

- **Testing**: Define unit and integration tests for all major components, especially for file operations and API endpoints.
- **Error Handling**: Specify error handling strategies for file I/O, S3 operations, and API failures.
- **Performance**: Consider performance benchmarks for reading, writing, and merging operations.
- **Documentation**: Document the API endpoints, configuration options, and usage instructions.
- **Deployment**: Provide basic deployment instructions for running the service locally and in production.

---

*Last updated: January 5, 2026*
*Author: denisvlah*