using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Cosmos.Emulator.Api.Endpoints;
using Cosmos.Emulator.Api.Middleware;
using Cosmos.Emulator.Core.Auth;
using Cosmos.Emulator.QueryEngine;
using Cosmos.Emulator.Storage;
using Cosmos.Emulator.Storage.Repositories;

// Support --data, --port, --key as simple CLI aliases
var switchMappings = new Dictionary<string, string>
{
    ["--data"] = "CosmosEmulator:DataPath",
    ["--port"] = "CosmosEmulator:Port",
    ["--key"] = "CosmosEmulator:MasterKey",
    ["--inmemory"] = "CosmosEmulator:InMemory",
};

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddCommandLine(args, switchMappings);

var config = builder.Configuration;
var masterKey = config.GetValue<string>("CosmosEmulator:MasterKey")
    ?? "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
var dataPath = config.GetValue<string>("CosmosEmulator:DataPath")
    ?? Path.Combine(Directory.GetCurrentDirectory(), "data");
var port = config.GetValue<int?>("CosmosEmulator:Port") ?? 8081;
var inMemory = config.GetValue<bool>("CosmosEmulator:InMemory");

// Generate self-signed certificate for HTTPS
var cert = GenerateSelfSignedCert();

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenAnyIP(port, listenOptions =>
    {
        listenOptions.UseHttps(cert);
    });
});

// Configure JSON to preserve property name casing (Cosmos API uses mixed casing)
// and use relaxed escaping so _etag quotes serialize as \" not \u0022
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
    options.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

// Register services
builder.Services.AddSingleton(new SqliteStorageProvider(dataPath, inMemory));
builder.Services.AddSingleton(new HmacSignatureValidator(masterKey));
builder.Services.AddTransient<DatabaseRepository>();
builder.Services.AddTransient<ContainerRepository>();
builder.Services.AddTransient<DocumentRepository>();
builder.Services.AddTransient<ChangeFeedRepository>();
builder.Services.AddSingleton<CosmosSqlQueryEngine>();
builder.Services.AddHostedService<Cosmos.Emulator.Api.Services.TtlExpirationService>();

var app = builder.Build();

// Initialize storage
app.Services.GetRequiredService<SqliteStorageProvider>().Initialize();

// Global error handler
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (Exception ex)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { code = "InternalServerError", message = ex.Message });
        }
    }
});

// Buffer responses to use Content-Length instead of chunked transfer encoding.
// EF Core's Cosmos provider hangs reading chunked response bodies under concurrent load.
app.Use(async (context, next) =>
{
    // Swap the response body with a MemoryStream to capture the full response
    var originalBody = context.Response.Body;
    var buffer = new MemoryStream();
    context.Response.Body = buffer;

    try
    {
        await next();
    }
    finally
    {
        // Strip charset from Content-Type to match official emulator
        var ct = context.Response.ContentType;
        if (ct != null && ct.Contains("charset"))
            context.Response.ContentType = "application/json";

        // Write the buffered response with Content-Length
        context.Response.Body = originalBody;
        context.Response.ContentLength = buffer.Length;
        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody);
        await buffer.DisposeAsync();
    }
});

// Request logging at Debug level — enable with Logging:LogLevel:RequestLog=Debug
app.Use(async (context, next) =>
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RequestLog");
    if (logger.IsEnabled(LogLevel.Debug))
    {
        var method = context.Request.Method;
        var path = context.Request.Path;
        var isQuery = context.Request.Headers.ContainsKey("x-ms-documentdb-isquery");
        var aim = context.Request.Headers["A-IM"].FirstOrDefault() ?? "";
        logger.LogDebug("[REQ] {Method} {Path} isQuery={IsQuery} A-IM={AIM}", method, path, isQuery, aim);
        await next();
        logger.LogDebug("[RES] {Method} {Path} → {Status} len={Len}", method, path, context.Response.StatusCode, context.Response.ContentLength ?? -1);
    }
    else
    {
        await next();
    }
});

// Middleware
app.UseMiddleware<VersionHeaderMiddleware>();
app.UseMiddleware<AuthenticationMiddleware>();

// Endpoints
app.MapAccountEndpoints();
app.MapDatabaseEndpoints();
app.MapContainerEndpoints();
app.MapDocumentEndpoints();
app.MapPartitionKeyRangeEndpoints();

app.Run();

public partial class Program
{
    static X509Certificate2 GenerateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth

        // Add SAN for localhost + 127.0.0.1
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(sanBuilder.Build());

        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(5));

        // Export and re-import to make the private key available on Windows
        return new X509Certificate2(
            cert.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
    }
}
