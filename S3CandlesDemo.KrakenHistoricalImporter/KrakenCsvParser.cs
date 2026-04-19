using System.Globalization;
using S3CandlesDemo.Candles;

namespace S3CandlesDemo.KrakenHistoricalImporter;

/// <summary>
/// Parses Kraken's historical OHLCVT CSV format.
/// Each row: timestamp,open,high,low,close,volume,trades (no header row).
/// Timestamp is a Unix timestamp in seconds.
/// </summary>
public static class KrakenCsvParser
{
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Streams candles from a CSV file as IAsyncEnumerable, sorted by timestamp.
    /// </summary>
    public static async IAsyncEnumerable<Candle> ParseFileAsync(
        string csvPath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(csvPath);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            var parts = line.Split(',');
            if (parts.Length < 7)
                continue;

            var timestamp = UnixEpoch.AddSeconds(long.Parse(parts[0], CultureInfo.InvariantCulture));
            var open = double.Parse(parts[1], CultureInfo.InvariantCulture);
            var high = double.Parse(parts[2], CultureInfo.InvariantCulture);
            var low = double.Parse(parts[3], CultureInfo.InvariantCulture);
            var close = double.Parse(parts[4], CultureInfo.InvariantCulture);
            var volume = double.Parse(parts[5], CultureInfo.InvariantCulture);
            var tradeCount = int.Parse(parts[6], CultureInfo.InvariantCulture);

            yield return new Candle
            {
                Timestamp = timestamp,
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = volume,
                TradeCount = tradeCount
            };
        }
    }
}
