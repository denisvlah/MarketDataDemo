using Amazon.S3;
using S3CandlesDemo.Candles;
using S3CandlesDemo.KrakenHistoricalImporter;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// OpenAPI
builder.Services.AddOpenApi();

// Health checks
builder.Services.AddHealthChecks();

// S3 client
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

// Historical importer
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<HistoricalImporter>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// Health check endpoints
app.MapHealthChecks("/health");

app.MapGet("/", () => Results.Redirect("/health"));

// Run the importer as a background task, then shut down
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
_ = Task.Run(async () =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    try
    {
        // Read config CSV from S3, fall back to local file (same pattern as KrakenLatestCollector)
        var s3Config = app.Services.GetRequiredService<IConfiguration>().GetSection("S3Candles");
        var configBucket = s3Config.GetValue<string>("ConfigBucket");
        var configKey = s3Config.GetValue<string>("ConfigKey");

        List<ImportJobConfig> jobs;
        if (!string.IsNullOrEmpty(configBucket) && !string.IsNullOrEmpty(configKey))
        {
            logger.LogInformation("Reading config from S3: {Bucket}/{Key}", configBucket, configKey);
            var s3Client = app.Services.GetRequiredService<IAmazonS3>();
            jobs = await ImportConfigReader.ReadFromS3Async(s3Client, configBucket, configKey);
        }
        else
        {
            var csvPath = Path.Combine(AppContext.BaseDirectory, "kraken-historical-config.csv");
            if (!File.Exists(csvPath))
                csvPath = Path.Combine(Directory.GetCurrentDirectory(), "kraken-historical-config.csv");

            logger.LogInformation("No S3 config source configured, reading config from local file: {CsvPath}", csvPath);
            jobs = ImportConfigReader.ReadFromFile(csvPath);
        }

        logger.LogInformation("Loaded {Count} import jobs", jobs.Count);

        var importConfig = app.Services.GetRequiredService<IConfiguration>().GetSection("HistoricalImport");
        var tempDir = importConfig.GetValue<string>("TempDirectory") ?? Path.Combine(Path.GetTempPath(), "kraken-historical");
        var googleApiKey = importConfig.GetValue<string>("GoogleApiKey");

        if (string.IsNullOrEmpty(googleApiKey))
        {
            logger.LogCritical("HistoricalImport:GoogleApiKey is required to list Google Drive folder contents.");
            Environment.ExitCode = 1;
            return;
        }

        var importer = app.Services.GetRequiredService<HistoricalImporter>();
        var success = await importer.RunAllAsync(jobs, tempDir, googleApiKey, ct: default);

        if (success)
            logger.LogInformation("All historical import jobs completed successfully.");
        else
            logger.LogError("Some historical import jobs failed.");

        Environment.ExitCode = success ? 0 : 1;
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "Fatal error in historical importer.");
        Environment.ExitCode = 1;
    }
    finally
    {
        lifetime.StopApplication();
    }
});

app.Run();

namespace S3CandlesDemo.KrakenHistoricalImporter
{
    // Expose Program for test discoverability
    public partial class Program { }
}
