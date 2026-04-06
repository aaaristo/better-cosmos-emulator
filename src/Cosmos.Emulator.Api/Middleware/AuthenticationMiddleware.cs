namespace Cosmos.Emulator.Api.Middleware;

/// <summary>
/// For now, just checks that the Authorization and x-ms-date headers are present.
/// Does not validate the HMAC signature — the emulator accepts any valid-looking auth.
/// </summary>
public class AuthenticationMiddleware
{
    private readonly RequestDelegate _next;

    public AuthenticationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
        var dateHeader = context.Request.Headers["x-ms-date"].FirstOrDefault();

        if (string.IsNullOrEmpty(authHeader) || string.IsNullOrEmpty(dateHeader))
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                code = "Unauthorized",
                message = "Missing Authorization or x-ms-date header."
            });
            return;
        }

        await _next(context);
    }
}
