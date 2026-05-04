using Amazon.S3;
using Kraken.Net.Clients;
using Kraken.Net.Interfaces.Clients;
using S3CandlesDemo.Candles;
using S3CandlesDemo.KrakenLatestCollector;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// OpenAPI
builder.Services.AddOpenApi();

// Health checks
builder.Services.AddHealthChecks();

// S3 client (shared between candle repository and config reader)
builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var s3Config = sp.GetRequiredService<IConfiguration>().GetSection("S3Candles");
    var awsConfig = s3Config.GetSection("AWS");
    var accessKey = awsConfig.GetValue<string>("AccessKey");
    var secretKey = awsConfig.GetValue<string>("SecretKey");
    var region = awsConfig.GetValue<string>("Region");
    var url = awsConfig.GetValue<string>("Url");

    if (string.IsNullOrEmpty(accessKey) || string.IsNullOrEmpty(secretKey) || string.IsNullOrEmpty(region))
    {
        logger.LogCritical("S3 configuration is incomplete. AccessKey, SecretKey, and Region are required.");
        Environment.Exit(1);
    }

    if (!string.IsNullOrWhiteSpace(url))
        return new AmazonS3Client(accessKey, secretKey, new AmazonS3Config
        {
            ServiceURL = url,
            ForcePathStyle = true,
            UseHttp = true
        });

    return new AmazonS3Client(accessKey, secretKey, Amazon.RegionEndpoint.GetBySystemName(region));
});

// S3 candle repository
builder.Services.AddSingleton<ICandlesRepository>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    try
    {
        var s3Config = sp.GetRequiredService<IConfiguration>().GetSection("S3Candles");
        var bucket = s3Config.GetValue<string>("Bucket");
        var prefix = s3Config.GetValue<string>("Prefix");

        if (string.IsNullOrEmpty(bucket))
        {
            logger.LogCritical("S3Candles:Bucket is required.");
            Environment.Exit(1);
        }

        var s3Client = sp.GetRequiredService<IAmazonS3>();
        return new S3CandlesRepository(bucket, prefix, s3Client);
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "Failed to initialize S3CandlesRepository.");
        Environment.Exit(1);
        throw;
    }
});

// Kraken REST client
builder.Services.AddSingleton<IKrakenRestClient>(new KrakenRestClient());

// Kraken OHLC service
builder.Services.AddSingleton<IKrakenOhlcService, KrakenOhlcService>();

// Candle collector
builder.Services.AddSingleton<CandleCollector>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// Health check endpoints
app.MapHealthChecks("/health");

app.MapGet("/", () => Results.Redirect("/health"));

// Run the collector as a background task, then shut down
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
_ = Task.Run(async () =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    try
    {
        var repo = app.Services.GetRequiredService<ICandlesRepository>();
        var jobs = await repo.GetJobConfigAsync();

        logger.LogInformation("Loaded {Count} collection jobs", jobs.Count);

        var collector = app.Services.GetRequiredService<CandleCollector>();
        var cutoff = DateTime.UtcNow.Date;
        var success = await collector.RunAllAsync(jobs, cutoff);

        if (success)
            logger.LogInformation("All collection jobs completed successfully.");
        else
            logger.LogError("Some collection jobs failed.");

        Environment.ExitCode = success ? 0 : 1;
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "Fatal error in collector.");
        Environment.ExitCode = 1;
    }
    finally
    {
        lifetime.StopApplication();
    }
});

app.Run();

namespace S3CandlesDemo.KrakenLatestCollector
{
    // Expose Program for test discoverability
    public partial class Program { }
}
