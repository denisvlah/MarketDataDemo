using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Amazon.S3;
using Azure.Identity;
using Azure.Storage.Blobs;
using S3CandlesDemo.Candles;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();

// Configure logging for Azure: JSON format, single-line, always include exceptions
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.JsonWriterOptions = new System.Text.Json.JsonWriterOptions { Indented = false };
});

builder.Services.AddSingleton<ICandlesRepository>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var config = sp.GetRequiredService<IConfiguration>();
    var storageType = config.GetValue<string>("StorageType") ?? "S3";

    try
    {
        return storageType.ToLowerInvariant() switch
        {
            "azure" => CreateAzureBlobRepository(config, logger),
            _ => CreateS3Repository(config, logger)
        };
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "[App] Startup failed | storage={StorageType} error={Message}", storageType, ex.Message);
        Thread.Sleep(1000); // Ensure log is flushed before exit
        Environment.Exit(1);
        throw; // Never reached, but satisfies compiler
    }
});

static ICandlesRepository CreateS3Repository(IConfiguration config, ILogger logger)
{
    try
    {
        logger.LogInformation("[S3] Initializing repository | bucket={Bucket} prefix={Prefix}", 
            config.GetSection("S3Candles").GetValue<string>("Bucket"),
            config.GetSection("S3Candles").GetValue<string>("Prefix"));
        
        var s3Config = config.GetSection("S3Candles");
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
            throw new InvalidOperationException($"S3 config incomplete: bucket={bucket}, region={region}, keys present={!string.IsNullOrEmpty(accessKey)}");
        }

        AmazonS3Client s3Client;
        if (!string.IsNullOrWhiteSpace(url))
        {
            logger.LogInformation("[S3] Using custom endpoint | url={Url}", url);
            s3Client = new AmazonS3Client(accessKey, secretKey, new AmazonS3Config
            {
                ServiceURL = url,
                ForcePathStyle = true,
                UseHttp = true
            });
        }
        else
        {
            logger.LogInformation("[S3] Using AWS endpoint | region={Region}", region);
            s3Client = new AmazonS3Client(accessKey, secretKey, Amazon.RegionEndpoint.GetBySystemName(region));
        }

        var repo = new S3CandlesRepository(bucket, prefix, s3Client);
        logger.LogInformation("[S3] Repository initialized successfully");
        return repo;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[S3] Initialization failed | error={Message}", ex.Message);
        throw;
    }
}

static ICandlesRepository CreateAzureBlobRepository(IConfiguration config, ILogger logger)
{
    try
    {
        var azureConfig = config.GetSection("AzureBlob");
        var connectionString = azureConfig.GetValue<string>("ConnectionString");
        var container = azureConfig.GetValue<string>("Container");
        var prefix = azureConfig.GetValue<string>("Prefix");
        var storageAccountName = azureConfig.GetValue<string>("StorageAccountName");

        // Use managed identity if connection string is not provided or is a placeholder
        if (string.IsNullOrEmpty(connectionString) || connectionString.Equals("USE_USER_SECRETS", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(storageAccountName) || string.IsNullOrEmpty(container))
            {
                throw new InvalidOperationException($"Azure config incomplete: account={storageAccountName}, container={container}");
            }

            logger.LogInformation("[Azure] Initializing with managed identity | account={StorageAccount} container={Container}", storageAccountName, container);
            try
            {
                var credential = new DefaultAzureCredential();
                var serviceClient = new BlobServiceClient(
                    new Uri($"https://{storageAccountName}.blob.core.windows.net"),
                    credential);
                logger.LogInformation("[Azure] Repository initialized with managed identity");
                return new AzureBlobCandlesRepository(serviceClient, container, prefix);
            }
            catch (Exception authEx)
            {
                logger.LogError(authEx, "[Azure] Managed identity authentication failed | account={StorageAccount}", storageAccountName);
                throw;
            }
        }

        // Use connection string (backward compatible)
        if (string.IsNullOrEmpty(container))
        {
            throw new InvalidOperationException("Container name is required for connection string auth");
        }

        logger.LogInformation("[Azure] Initializing with connection string | container={Container}", container);
        try
        {
            var containerClient = new BlobContainerClient(connectionString, container);
            logger.LogInformation("[Azure] Repository initialized with connection string");
            return new AzureBlobCandlesRepository(containerClient, prefix);
        }
        catch (Exception connEx)
        {
            logger.LogError(connEx, "[Azure] Connection string authentication failed | container={Container}", container);
            throw;
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[Azure] Initialization failed | error={Message}", ex.Message);
        throw;
    }
}

// Poll S3 every minute to refresh the in-memory file index.
// This avoids rebuilding the index on every API request while still catching externally added files.
builder.Services.AddHostedService<FileIndexPollingService>();

// Log method, path, query, status code, and duration for every request.
// Request/response bodies are excluded to avoid memory overhead on streaming endpoints.
builder.Services.AddHttpLogging(logging =>
{
    logging.LoggingFields = Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestMethod
        | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestPath
        | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestQuery
        | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.ResponseStatusCode
        | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.Duration;
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

var app = builder.Build();

app.UseHttpLogging();

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
app.MapGet("/candles/{intervalMinutes}", async (int intervalMinutes,string symbol, DateTime? from, DateTime? to, ICandlesRepository repo, ILogger<Program> logger, CancellationToken ct) =>
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
            try
            {
                await foreach (var item in candles.WithCancellation(ct))
                {
                    if (!first) await stream.WriteAsync(JsonStreamBytes.Comma, ct);
                    first = false;
                    i++;
                    await JsonSerializer.SerializeAsync(stream, item, AppJsonSerializerContext.Default.Candle, ct);
                    if (i % 1000 == 0)
                        await stream.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "S3 read failed for {Symbol}/{Interval} after {Count} candles", symbol, intervalMinutes, i);
                if (!first) await stream.WriteAsync(JsonStreamBytes.Comma, ct);
                await JsonSerializer.SerializeAsync(stream, "error", AppJsonSerializerContext.Default.String, ct);
            }
            await stream.WriteAsync(JsonStreamBytes.ArrayClose, ct);
            await stream.FlushAsync(ct);
        }, "application/json");
        
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

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTime?))]
[JsonSerializable(typeof(Candle))]
[JsonSerializable(typeof(List<Candle>))]
[JsonSerializable(typeof(IReadOnlyList<CandleFileInfo>))]
[JsonSerializable(typeof(CandleFileInfo))]
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
            logger.LogError(ex, "Failed to build file index on startup. Will retry in {Interval}. {m}", Interval, ex.Message);
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