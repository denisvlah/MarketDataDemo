using Amazon.S3;
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

// Historical importer
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<HistoricalImporter>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var bucket = config.GetSection("S3Candles").GetValue<string>("Bucket") ?? string.Empty;
    return new HistoricalImporter(
        sp.GetRequiredService<IAmazonS3>(),
        sp.GetRequiredService<HttpClient>(),
        bucket,
        sp.GetRequiredService<ILogger<HistoricalImporter>>()
    );
});

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
        var importConfig = app.Services.GetRequiredService<IConfiguration>().GetSection("HistoricalImport");
        var tempDir = importConfig.GetValue<string>("TempDirectory") ?? Path.Combine(Path.GetTempPath(), "kraken-historical");
        var googleApiKey = importConfig.GetValue<string>("GoogleApiKey");

        if (string.IsNullOrEmpty(googleApiKey))
        {
            logger.LogCritical("HistoricalImport:GoogleApiKey is required to access Google Drive.");
            Environment.ExitCode = 1;
            return;
        }

        var importer = app.Services.GetRequiredService<HistoricalImporter>();
        var success = await importer.RunAllAsync(tempDir, googleApiKey, ct: default);

        if (success)
            logger.LogInformation("All historical archives processed successfully.");
        else
            logger.LogError("Some archives failed to process.");

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
