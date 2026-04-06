namespace Cosmos.Emulator.Api.Middleware;

public class VersionHeaderMiddleware
{
    private readonly RequestDelegate _next;

    public VersionHeaderMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Add standard Cosmos DB response headers
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["x-ms-version"] = "2020-07-15";
            headers["x-ms-activity-id"] = Guid.NewGuid().ToString();
            headers["x-ms-request-charge"] = "1.0";
            headers["x-ms-gatewayversion"] = "version=2.14.0";

            if (!headers.ContainsKey("x-ms-session-token"))
                headers["x-ms-session-token"] = "0:-1#0";

            return Task.CompletedTask;
        });

        await _next(context);
    }
}
