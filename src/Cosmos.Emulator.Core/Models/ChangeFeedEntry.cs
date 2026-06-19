using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cosmos.Emulator.Core.Models;

public class ChangeFeedEntry
{
    public long Lsn { get; set; }
    public required string DocumentId { get; set; }
    public required string PartitionKey { get; set; }
    public required string Operation { get; set; }
    public JsonElement? Body { get; set; }
    public JsonElement? PreviousBody { get; set; }
    public long Ts { get; set; }
    public required string Etag { get; set; }
}

public class ChangeFeedResponse
{
    [JsonPropertyName("Documents")]
    public required List<JsonElement> Documents { get; set; }

    [JsonPropertyName("_count")]
    public int Count { get; set; }
}

public class AllVersionsChangeFeedItem
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("current")]
    public JsonElement? Current { get; set; }

    [JsonPropertyName("metadata")]
    public required ChangeFeedMetadata Metadata { get; set; }
}

public class ChangeFeedMetadata
{
    [JsonPropertyName("operationType")]
    public required string OperationType { get; set; }

    [JsonPropertyName("lsn")]
    public long Lsn { get; set; }

    [JsonPropertyName("crts")]
    public long Crts { get; set; }

    [JsonPropertyName("previousImageLSN")]
    public long PreviousImageLSN { get; set; }

    [JsonPropertyName("timeToLiveExpired")]
    public bool TimeToLiveExpired { get; set; }
}
