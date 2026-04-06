using System.Text.Json.Serialization;

namespace Cosmos.Emulator.Core.Models;

public class CosmosContainer
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("_rid")]
    public required string Rid { get; set; }

    [JsonPropertyName("_self")]
    public required string Self { get; set; }

    [JsonPropertyName("_etag")]
    public required string Etag { get; set; }

    [JsonPropertyName("_ts")]
    public long Ts { get; set; }

    [JsonPropertyName("partitionKey")]
    public required PartitionKeyDefinition PartitionKey { get; set; }

    [JsonPropertyName("indexingPolicy")]
    public required IndexingPolicy IndexingPolicy { get; set; }

    [JsonPropertyName("defaultTtl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DefaultTtl { get; set; }

    [JsonPropertyName("_docs")]
    public string Docs => "docs/";

    [JsonPropertyName("_sprocs")]
    public string Sprocs => "sprocs/";

    [JsonPropertyName("_triggers")]
    public string Triggers => "triggers/";

    [JsonPropertyName("_udfs")]
    public string Udfs => "udfs/";

    [JsonPropertyName("_conflicts")]
    public string Conflicts => "conflicts/";
}
