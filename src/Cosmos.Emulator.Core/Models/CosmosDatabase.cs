using System.Text.Json.Serialization;

namespace Cosmos.Emulator.Core.Models;

public class CosmosDatabase
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

    [JsonPropertyName("_colls")]
    public string Colls => "colls/";

    [JsonPropertyName("_users")]
    public string Users => "users/";
}
