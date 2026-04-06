using Microsoft.Azure.Cosmos;
using Shouldly;

namespace Cosmos.Emulator.Tests.Integration;

[Collection("Emulator")]
public class ReadOnlyTest
{
    private readonly CosmosClient _client;
    public ReadOnlyTest(EmulatorFixture fixture) { _client = fixture.Client; }

    [Fact(Timeout = 15000)]
    public async Task ReadNonExistentItem_ShouldReturn404()
    {
        var dbName = $"test-{Guid.NewGuid():N}"[..20];
        var db = (await _client.CreateDatabaseAsync(dbName)).Database;
        var container = (await db.CreateContainerAsync("rcoll", "/pk")).Container;

        // Don't create any items — just try to read
        var ex = await Should.ThrowAsync<CosmosException>(
            () => container.ReadItemAsync<dynamic>("nonexistent", new PartitionKey("pk1")));
        ex.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
    }
}
