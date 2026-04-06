using System.Net;
using Microsoft.Azure.Cosmos;
using Shouldly;

namespace Cosmos.Emulator.Tests.Integration;

[Collection("Emulator")]
public class QuerySdkTest
{
    private readonly CosmosClient _client;

    public QuerySdkTest(EmulatorFixture fixture) { _client = fixture.Client; }

    [Fact(Timeout = 15000)]
    public async Task QueryItems_ShouldWork()
    {
        var dbName = $"test-{Guid.NewGuid():N}"[..20];
        var db = (await _client.CreateDatabaseAsync(dbName)).Database;
        var container = (await db.CreateContainerAsync("qcoll", "/pk")).Container;

        await container.CreateItemAsync(new { id = "1", pk = "a", name = "Alice" }, new PartitionKey("a"));

        var query = container.GetItemQueryIterator<dynamic>(
            "SELECT * FROM c",
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey("a") });

        var results = new List<dynamic>();
        while (query.HasMoreResults)
        {
            var page = await query.ReadNextAsync();
            results.AddRange(page);
        }

        results.Count.ShouldBe(1);
    }
}
