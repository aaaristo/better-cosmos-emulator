using Microsoft.Azure.Cosmos;
using Shouldly;

namespace Cosmos.Emulator.Tests.Integration;

[Collection("Emulator")]
public class PaginationTests
{
    private readonly CosmosClient _client;

    public PaginationTests(EmulatorFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task ListDatabases_WithPagination_ShouldPageThrough()
    {
        // Create 5 databases
        var prefix = $"pg-{Guid.NewGuid():N}"[..10];
        for (int i = 0; i < 5; i++)
            await _client.CreateDatabaseAsync($"{prefix}-db{i}");

        // Page through with max 2 per page
        var allDbs = new List<DatabaseProperties>();
        var iterator = _client.GetDatabaseQueryIterator<DatabaseProperties>(
            requestOptions: new QueryRequestOptions { MaxItemCount = 2 });

        int pageCount = 0;
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            allDbs.AddRange(page);
            pageCount++;
        }

        // Should have found all 5 (plus any from other tests)
        allDbs.Count.ShouldBeGreaterThanOrEqualTo(5);
        // With MaxItemCount=2, should need at least 3 pages for 5 items
        pageCount.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task ListContainers_WithPagination_ShouldPageThrough()
    {
        var dbName = $"test-db-{Guid.NewGuid():N}";
        var db = (await _client.CreateDatabaseAsync(dbName)).Database;

        // Create 4 containers
        for (int i = 0; i < 4; i++)
            await db.CreateContainerAsync($"coll{i}", "/pk");

        // Page through with max 2 per page
        var allColls = new List<ContainerProperties>();
        var iterator = db.GetContainerQueryIterator<ContainerProperties>(
            requestOptions: new QueryRequestOptions { MaxItemCount = 2 });

        int pageCount = 0;
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            allColls.AddRange(page);
            pageCount++;
        }

        allColls.Count.ShouldBe(4);
        pageCount.ShouldBe(2); // 4 items / 2 per page = 2 pages
    }

    [Fact]
    public async Task ListDocuments_WithPagination_ShouldPageThrough()
    {
        var dbName = $"test-db-{Guid.NewGuid():N}";
        var db = (await _client.CreateDatabaseAsync(dbName)).Database;
        var container = (await db.CreateContainerAsync($"coll-{Guid.NewGuid():N}", "/pk")).Container;

        // Create 5 documents
        for (int i = 0; i < 5; i++)
            await container.CreateItemAsync(
                new { id = $"doc{i}", pk = "a", value = i },
                new PartitionKey("a"));

        // Query with max 2 per page
        var allDocs = new List<dynamic>();
        var iterator = container.GetItemQueryIterator<dynamic>(
            "SELECT * FROM c",
            requestOptions: new QueryRequestOptions { MaxItemCount = 2 });

        int pageCount = 0;
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            allDocs.AddRange(page);
            pageCount++;
        }

        allDocs.Count.ShouldBe(5);
        pageCount.ShouldBeGreaterThanOrEqualTo(3); // 5 items / 2 per page = at least 3 pages
    }
}
