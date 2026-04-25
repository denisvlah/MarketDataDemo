using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace S3CandlesDemo.CsvLoader;

/// <summary>
/// Reports healthy as long as gap-filling has made progress within the last 5 minutes.
/// Progress is signaled by calling <see cref="ReportProgress"/> whenever candles are stored.
/// </summary>
public class GapFillingHealthCheck : IHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private DateTime _lastProgress = DateTime.UtcNow;
    private readonly object _lock = new();

    public void ReportProgress()
    {
        lock (_lock)
            _lastProgress = DateTime.UtcNow;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        DateTime last;
        lock (_lock)
            last = _lastProgress;

        var elapsed = DateTime.UtcNow - last;
        if (elapsed > Timeout)
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"No progress for {elapsed.TotalMinutes:F1} minutes"));

        return Task.FromResult(HealthCheckResult.Healthy("Gap-filling in progress"));
    }
}
