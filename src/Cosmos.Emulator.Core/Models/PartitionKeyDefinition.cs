using System.Text.Json.Serialization;

namespace Cosmos.Emulator.Core.Models;

public class PartitionKeyDefinition
{
    [JsonPropertyName("paths")]
    public required List<string> Paths { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "Hash";

    [JsonPropertyName("version")]
    public int Version { get; set; } = 2;
}
