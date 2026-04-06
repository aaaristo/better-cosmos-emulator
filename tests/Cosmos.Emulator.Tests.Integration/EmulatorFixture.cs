using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cosmos.Emulator.Tests.Integration;

/// <summary>
/// Logging handler that writes every request/response to a file for debugging.
/// Console.WriteLine is buffered by test runners, so we write directly to a file.
/// </summary>
public class LoggingHandler : DelegatingHandler
{
    public static readonly string LogFile = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "sdk-debug.log");
    private static readonly object Lock = new();

    public LoggingHandler() : base(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    })
    {
        Log($"--- New session {DateTime.Now:O} ---");
    }

    public LoggingHandler(HttpMessageHandler inner) : base(inner)
    {
        Log($"--- New session {DateTime.Now:O} ---");
    }

    private static void Log(string msg)
    {
        lock (Lock) File.AppendAllText(LogFile, msg + "\n");
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Log($"[SDK] {request.Method} {request.RequestUri}");
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            Log($"[SDK] EXCEPTION: {ex.Message}");
            throw;
        }
        Log($"[SDK] => {(int)response.StatusCode} {response.StatusCode}");
        // Log bodies and request headers for docs endpoint
        if (request.RequestUri?.PathAndQuery.EndsWith("/docs") == true)
        {
            Log($"[SDK] Request headers:");
            foreach (var h in request.Headers)
                Log($"[SDK]   {h.Key}: {string.Join(", ", h.Value)}");
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            Log($"[SDK] Body ({body.Length}c): {body[..Math.Min(body.Length, 800)]}");
            response.Content = new StringContent(body, System.Text.Encoding.UTF8, response.Content.Headers.ContentType?.MediaType ?? "application/json");
        }
        else if ((int)response.StatusCode >= 400)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            Log($"[SDK] Body: {body[..Math.Min(body.Length, 500)]}");
            response.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        }
        return response;
    }
}

public class EmulatorFixture : IAsyncLifetime
{
    private WebApplication? _app;
    public CosmosClient Client { get; private set; } = null!;

    private const string MasterKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
    private string _dataPath = null!;

    public async Task InitializeAsync()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), $"cosmos-emulator-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataPath);

        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.WebHost.UseUrls("https://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ConfigureHttpsDefaults(https =>
            {
                https.AllowAnyClientCertificate();
            });
        });
        builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = null;
        });

        var storage = new Storage.SqliteStorageProvider(_dataPath);
        builder.Services.AddSingleton(storage);
        builder.Services.AddSingleton(new Core.Auth.HmacSignatureValidator(MasterKey));
        builder.Services.AddTransient<Storage.Repositories.DatabaseRepository>();
        builder.Services.AddTransient<Storage.Repositories.ContainerRepository>();
        builder.Services.AddTransient<Storage.Repositories.DocumentRepository>();
        builder.Services.AddTransient<Storage.Repositories.ChangeFeedRepository>();
        builder.Services.AddSingleton<QueryEngine.CosmosSqlQueryEngine>();

        _app = builder.Build();

        storage.Initialize();

        // Error handler
        _app.Use(async (context, next) =>
        {
            try { await next(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[EMULATOR ERROR] {context.Request.Method} {context.Request.Path}: {ex}");
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = 500;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new { code = "InternalServerError", message = ex.Message });
                }
            }
        });

        _app.UseMiddleware<Api.Middleware.VersionHeaderMiddleware>();
        _app.UseMiddleware<Api.Middleware.AuthenticationMiddleware>();

        Api.Endpoints.AccountEndpoints.MapAccountEndpoints(_app);
        Api.Endpoints.DatabaseEndpoints.MapDatabaseEndpoints(_app);
        Api.Endpoints.ContainerEndpoints.MapContainerEndpoints(_app);
        Api.Endpoints.DocumentEndpoints.MapDocumentEndpoints(_app);
        Api.Endpoints.PartitionKeyRangeEndpoints.MapPartitionKeyRangeEndpoints(_app);

        await _app.StartAsync();

        var baseUrl = _app.Urls.First();
        File.WriteAllText(LoggingHandler.LogFile, $"Emulator started at {baseUrl}\n");

        // Enable SDK internal trace logging
        var traceListener = new CosmosTraceListener(LoggingHandler.LogFile);
        System.Diagnostics.Trace.Listeners.Add(traceListener);

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };

        var loggingHandler = new LoggingHandler(handler);

        Client = new CosmosClient(
            baseUrl,
            MasterKey,
            new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                LimitToEndpoint = true,
                MaxRetryAttemptsOnRateLimitedRequests = 0,
                RequestTimeout = TimeSpan.FromSeconds(5),
                HttpClientFactory = () => new HttpClient(loggingHandler, disposeHandler: false)
            });
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();

        if (_app is not null)
            await _app.StopAsync();

        try
        {
            if (Directory.Exists(_dataPath))
                Directory.Delete(_dataPath, true);
        }
        catch { }
    }
}

/// <summary>
/// Buffers response bodies so they are sent with Content-Length instead of chunked encoding.
/// </summary>
public class BufferingHandler : DelegatingHandler
{
    public BufferingHandler(HttpMessageHandler inner) : base(inner) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.Content != null)
        {
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/json";
            var newContent = new ByteArrayContent(body);
            newContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
            // Copy other content headers
            foreach (var h in response.Content.Headers)
            {
                if (h.Key != "Content-Type" && h.Key != "Content-Length")
                    newContent.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            response.Content = newContent;
        }
        return response;
    }
}

public class CosmosTraceListener : System.Diagnostics.TraceListener
{
    private readonly string _logFile;
    private static readonly object Lock = new();

    public CosmosTraceListener(string logFile) { _logFile = logFile; }

    public override void Write(string? message)
    {
        if (message is not null)
            lock (Lock) File.AppendAllText(_logFile, message);
    }

    public override void WriteLine(string? message)
    {
        if (message is not null)
            lock (Lock) File.AppendAllText(_logFile, $"[TRACE] {message}\n");
    }
}

[CollectionDefinition("Emulator")]
public class EmulatorCollection : ICollectionFixture<EmulatorFixture>;
