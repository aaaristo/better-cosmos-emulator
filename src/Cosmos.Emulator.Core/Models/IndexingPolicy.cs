using System.Text.Json.Serialization;

namespace Cosmos.Emulator.Core.Models;

public class IndexingPolicy
{
    [JsonPropertyName("indexingMode")]
    public string IndexingMode { get; set; } = "consistent";

    [JsonPropertyName("automatic")]
    public bool Automatic { get; set; } = true;

    [JsonPropertyName("includedPaths")]
    public List<IncludedPath> IncludedPaths { get; set; } =
    [
        new() { Path = "/*" }
    ];

    [JsonPropertyName("excludedPaths")]
    public List<ExcludedPath> ExcludedPaths { get; set; } =
    [
        new() { Path = "/\"_etag\"/?" }
    ];

    [JsonPropertyName("compositeIndexes")]
    public List<List<CompositeIndex>>? CompositeIndexes { get; set; }
}

public class IncludedPath
{
    [JsonPropertyName("path")]
    public required string Path { get; set; }
}

public class ExcludedPath
{
    [JsonPropertyName("path")]
    public required string Path { get; set; }
}

public class CompositeIndex
{
    [JsonPropertyName("path")]
    public required string Path { get; set; }

    [JsonPropertyName("order")]
    public string Order { get; set; } = "ascending";
}
