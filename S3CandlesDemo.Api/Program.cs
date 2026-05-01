using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Amazon.S3;
using S3CandlesDemo.Candles;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();

builder.Services.AddSingleton<ICandlesRepository>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    try
    {
        logger.LogInformation("Initializing S3CandlesRepository...");
        var s3Config = sp.GetRequiredService<IConfiguration>().GetSection("S3Candles");
        var bucket = s3Config.GetValue<string>("Bucket");
        var prefix = s3Config.GetValue<string>("Prefix");
        var awsConfig = s3Config.GetSection("AWS");
        var accessKey = awsConfig.GetValue<string>("AccessKey");
        var secretKey = awsConfig.GetValue<string>("SecretKey");
        var region = awsConfig.GetValue<string>("Region");
        var url = awsConfig.GetValue<string>("Url");

        if (string.IsNullOrEmpty(bucket) || string.IsNullOrEmpty(accessKey) ||
            string.IsNullOrEmpty(secretKey) || string.IsNullOrEmpty(region))
        {
            logger.LogCritical("S3 configuration is incomplete. Bucket, AccessKey, SecretKey, and Region are required.");
            Thread.Sleep(1000); // Ensure log is flushed before exit
            Environment.Exit(1);
        }

        AmazonS3Client s3Client;
        if (!string.IsNullOrWhiteSpace(url))
            s3Client = new AmazonS3Client(accessKey, secretKey, new AmazonS3Config
            {
                ServiceURL = url,
                ForcePathStyle = true,
                UseHttp = true
            });
        else
            s3Client = new AmazonS3Client(accessKey, secretKey, Amazon.RegionEndpoint.GetBySystemName(region));

        var r=  new S3CandlesRepository(bucket, prefix, s3Client);
        logger.LogInformation("S3CandlesRepository initialized successfully.");
        return r;
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "Failed to initialize S3CandlesRepository. S3 connection is not available.");
        Thread.Sleep(1000); // Ensure log is flushed before exit
        Environment.Exit(1);
        throw; // Never reached, but satisfies compiler
    }
});

// Poll S3 every minute to refresh the in-memory file index.
// This avoids rebuilding the index on every API request while still catching externally added files.
builder.Services.AddHostedService<FileIndexPollingService>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

//app.UseHttpsRedirection();

// Store candles (bulk)
app.MapPost("/candles/{symbol}/{intervalMinutes}/bulk", async (string symbol, int intervalMinutes, List<Candle> candles, ICandlesRepository repo, CancellationToken cancellationToken) =>
{
    await repo.StoreCandlesAsync(symbol, intervalMinutes, candles, cancellationToken);
    return Results.Ok();
});

// Store candles (async stream, for advanced clients)
// Note: Minimal API does not natively support IAsyncEnumerable from body, so this is omitted for now.

// Fetch candles by symbol, time period, and interval
app.MapGet("/candles/{symbol}/{intervalMinutes}", async (string symbol, int intervalMinutes, DateTime? from, DateTime? to, ICandlesRepository repo, CancellationToken ct) =>
    {
        if (from is null || to is null)
            return Results.BadRequest("Query parameters 'from' and 'to' are required (e.g. ?from=2024-02-01&to=2024-02-12).");

        symbol = HttpUtility.UrlDecode(symbol);

        var candles = repo.FetchCandlesAsync(symbol, intervalMinutes, from.Value, to.Value, ct);
        return Results.Stream(async (stream) =>
        {
            await stream.WriteAsync(JsonStreamBytes.ArrayOpen, ct);
            bool first = true;
            int i = 0;
            await foreach (var item in candles.WithCancellation(ct))
            {
                if (!first) await stream.WriteAsync(JsonStreamBytes.Comma, ct);
                first = false;
                i++;
                await JsonSerializer.SerializeAsync(stream, item, AppJsonSerializerContext.Default.Candle, ct);
                if (i % 1000 == 0)
                    await stream.FlushAsync(ct);
            }
            await stream.WriteAsync(JsonStreamBytes.ArrayClose, ct);
            await stream.FlushAsync(ct);
        }, "application/json");
        // ctx.Response.ContentType = "application/json";
        // await ctx.Response.WriteAsync("[");
        // bool first = true;

        // await foreach (var candle in candles)
        // {
        //     if (!first) await ctx.Response.WriteAsync(",");
        //     first = false;

        //     await JsonSerializer.SerializeAsync(
        //         ctx.Response.Body,
        //         candle,
        //         AppJsonSerializerContext.Default.Candle,
        //         ctx.RequestAborted);
        // }
        // await ctx.Response.WriteAsync("]");
    });

// Retrieve all file info for a symbol/interval
app.MapGet("/candles/{symbol}/{intervalMinutes}/files", async (string symbol, int intervalMinutes, ICandlesRepository repo, CancellationToken cancellationToken) =>
    await repo.GetCandleFilesAsync(symbol, intervalMinutes, cancellationToken));

// Remove all files for a symbol/interval
app.MapDelete("/candles/{symbol}/{intervalMinutes}/files", async (string symbol, int intervalMinutes, ICandlesRepository repo, CancellationToken cancellationToken) =>
{
    await repo.RemoveCandleFilesAsync(symbol, intervalMinutes, cancellationToken);
    return Results.Ok();
});

// Remove a specific file by file info
app.MapDelete("/candles/file", async ([Microsoft.AspNetCore.Mvc.FromBody] CandleFileInfo fileInfo, ICandlesRepository repo, CancellationToken cancellationToken) =>
{
    await repo.RemoveCandleFileAsync(fileInfo, cancellationToken);
    return Results.Ok();
});

// List available symbols with their intervals
app.MapGet("/candles/symbols", async (ICandlesRepository repo, CancellationToken cancellationToken) =>
{
    var files = await repo.GetAllCandleFilesAsync(cancellationToken);
    return files
        .GroupBy(f => f.Symbol)
        .Select(g => new SymbolIntervals(g.Key, g.Select(f => f.IntervalMinutes).Distinct().OrderBy(i => i).ToArray()))
        .OrderBy(s => s.Symbol)
        .ToList();
});

// List all files in the repository with size and candle count
app.MapGet("/candles/files", async (ICandlesRepository repo, CancellationToken cancellationToken) =>
    await repo.GetAllCandleFilesAsync(cancellationToken));

app.Run();

record SymbolIntervals(string Symbol, int[] Intervals);

static class JsonStreamBytes
{
    public static readonly byte[] ArrayOpen = [(byte)'['];
    public static readonly byte[] ArrayClose = [(byte)']'];
    public static readonly byte[] Comma = [(byte)','];
}

[JsonSerializable(typeof(Candle))]
[JsonSerializable(typeof(List<Candle>))]
[JsonSerializable(typeof(IReadOnlyList<CandleFileInfo>))]
[JsonSerializable(typeof(CandleFileInfo))]
[JsonSerializable(typeof(IReadOnlyList<CandleFileInfoDetail>))]
[JsonSerializable(typeof(CandleFileInfoDetail))]
[JsonSerializable(typeof(List<SymbolIntervals>))]
[JsonSerializable(typeof(SymbolIntervals))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{

}

namespace S3CandlesDemo.Api
{
    // Expose Program for WebApplicationFactory<T> in integration tests
    public partial class Program { }
}

// Rebuilds the repository's in-memory file index every minute so the API
// always reflects files added externally (e.g. by the collector or importer)
// without the overhead of a per-request S3 ListObjects call.
public class FileIndexPollingService(ICandlesRepository repo, ILogger<FileIndexPollingService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await repo.RebuildFileIndexAsync(cancellationToken);
            logger.LogInformation("File index built on startup.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to build file index on startup. Will retry in {Interval}.", Interval);
        }
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(Interval, stoppingToken);
            try
            {
                await repo.RebuildFileIndexAsync(stoppingToken);
                logger.LogDebug("File index rebuilt.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to rebuild file index.");
            }
        }
    }
}