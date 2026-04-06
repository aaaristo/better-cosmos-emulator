using System.Text.Json;

namespace Cosmos.Emulator.Api.Endpoints;

public static class PaginationHelper
{
    /// <summary>
    /// Applies pagination to a list based on x-ms-max-item-count and x-ms-continuation headers.
    /// Returns the page of items and sets the continuation header if more results exist.
    /// </summary>
    public static List<T> Apply<T>(HttpContext context, List<T> all)
    {
        var maxItems = int.MaxValue;
        if (context.Request.Headers.TryGetValue("x-ms-max-item-count", out var maxHeader))
        {
            if (int.TryParse(maxHeader.FirstOrDefault(), out var parsed) && parsed > 0)
                maxItems = parsed;
        }

        var offset = 0;
        var continuation = context.Request.Headers["x-ms-continuation"].FirstOrDefault();
        if (continuation is not null)
        {
            try
            {
                var tokenBytes = Convert.FromBase64String(continuation);
                var tokenJson = JsonDocument.Parse(tokenBytes);
                offset = tokenJson.RootElement.GetProperty("offset").GetInt32();
            }
            catch { }
        }

        var page = all.Skip(offset).Take(maxItems).ToList();

        context.Response.Headers["x-ms-item-count"] = page.Count.ToString();

        // Set continuation if there are more results
        if (offset + page.Count < all.Count)
        {
            var nextToken = Convert.ToBase64String(
                JsonSerializer.SerializeToUtf8Bytes(new { offset = offset + page.Count }));
            context.Response.Headers["x-ms-continuation"] = nextToken;
        }

        return page;
    }
}
