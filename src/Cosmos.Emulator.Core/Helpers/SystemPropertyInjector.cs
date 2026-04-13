using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cosmos.Emulator.Core.Helpers;

public static class SystemPropertyInjector
{
    public static JsonElement Inject(
        JsonElement document,
        string rid,
        string selfLink,
        string etag,
        long ts,
        string attachments)
    {
        var node = JsonNode.Parse(document.GetRawText())!.AsObject();

        node["_rid"] = rid;
        node["_self"] = selfLink;
        node["_etag"] = etag;
        node["_ts"] = ts;
        node["_attachments"] = attachments;

        return JsonParse(node.ToJsonString());
    }

    public static JsonElement UpdateSystemProperties(
        JsonElement document,
        string etag,
        long ts)
    {
        var node = JsonNode.Parse(document.GetRawText())!.AsObject();

        node["_etag"] = etag;
        node["_ts"] = ts;

        return JsonParse(node.ToJsonString());
    }

    private static JsonElement JsonParse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
