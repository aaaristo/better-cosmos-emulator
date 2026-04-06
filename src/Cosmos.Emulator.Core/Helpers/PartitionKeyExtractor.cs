using System.Text.Json;

namespace Cosmos.Emulator.Core.Helpers;

public static class PartitionKeyExtractor
{
    /// <summary>
    /// Extracts partition key values from a JSON document given the partition key paths.
    /// Returns a JSON array string, e.g. '["value1"]' or '["value1","value2"]'.
    /// </summary>
    public static string Extract(JsonElement document, IReadOnlyList<string> paths)
    {
        var values = new List<string>();

        foreach (var path in paths)
        {
            // Path is like "/tenantId" or "/address/city"
            var segments = path.TrimStart('/').Split('/');
            var current = document;
            var found = true;

            foreach (var segment in segments)
            {
                if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out var next))
                {
                    current = next;
                }
                else
                {
                    found = false;
                    break;
                }
            }

            if (!found)
            {
                values.Add("{}");
            }
            else
            {
                values.Add(FormatValue(current));
            }
        }

        return $"[{string.Join(",", values)}]";
    }

    /// <summary>
    /// Parses a partition key header value like ["value"] into the canonical form.
    /// </summary>
    public static string FromHeader(string headerValue)
    {
        // The header value is already a JSON array, normalize it
        using var doc = JsonDocument.Parse(headerValue);
        var array = doc.RootElement;

        if (array.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Partition key must be a JSON array");

        var values = new List<string>();
        foreach (var element in array.EnumerateArray())
        {
            values.Add(FormatValue(element));
        }

        return $"[{string.Join(",", values)}]";
    }

    private static string FormatValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => $"\"{EscapeJsonString(element.GetString()!)}\"",
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => "{}"
        };
    }

    private static string EscapeJsonString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
