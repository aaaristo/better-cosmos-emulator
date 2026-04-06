using System.Text.Json;

namespace Cosmos.Emulator.Core.Models;

public class CosmosDocument
{
    public required string Id { get; set; }
    public required string Rid { get; set; }
    public required string PartitionKey { get; set; }
    public required JsonElement Body { get; set; }
    public required string Etag { get; set; }
    public long Ts { get; set; }
    public bool IsDeleted { get; set; }
    public long Lsn { get; set; }
}
