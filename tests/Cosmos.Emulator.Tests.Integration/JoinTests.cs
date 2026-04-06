using Microsoft.Azure.Cosmos;
using Shouldly;

namespace Cosmos.Emulator.Tests.Integration;

[Collection("Emulator")]
public class JoinTests
{
    private readonly CosmosClient _client;

    public JoinTests(EmulatorFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task Join_ScalarArray_ShouldFlatten()
    {
        var container = await CreateTempContainer();

        await container.CreateItemAsync(
            new { id = "1", pk = "a", name = "Alice", tags = new[] { "admin", "user" } },
            new PartitionKey("a"));

        var query = container.GetItemQueryIterator<dynamic>(
            "SELECT c.name, t AS tag FROM c JOIN t IN c.tags");

        var results = await Drain(query);
        results.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Join_ObjectArray_ShouldFlatten()
    {
        var container = await CreateTempContainer();

        await container.CreateItemAsync(new
        {
            id = "1", pk = "a", name = "Product A",
            sizes = new[] {
                new { key = "s", description = "Small" },
                new { key = "m", description = "Medium" },
                new { key = "l", description = "Large" }
            }
        }, new PartitionKey("a"));

        var query = container.GetItemQueryIterator<dynamic>(
            "SELECT c.name, s.key, s.description FROM c JOIN s IN c.sizes");

        var results = await Drain(query);
        results.Count.ShouldBe(3);
        ((string)results[0].key).ShouldBe("s");
        ((string)results[2].description).ShouldBe("Large");
    }

    [Fact]
    public async Task Join_WithFilter_ShouldFilterJoinedElements()
    {
        var container = await CreateTempContainer();

        await container.CreateItemAsync(new
        {
            id = "1", pk = "a", name = "Product A",
            sizes = new[] {
                new { key = "s", description = "Small" },
                new { key = "l", description = "Large" }
            }
        }, new PartitionKey("a"));

        var query = container.GetItemQueryIterator<dynamic>(
            "SELECT c.name, s.description FROM c JOIN s IN c.sizes WHERE s.key = 'l'");

        var results = await Drain(query);
        results.Count.ShouldBe(1);
        ((string)results[0].description).ShouldBe("Large");
    }

    [Fact]
    public async Task Join_MultipleDocuments_ShouldFlattenAll()
    {
        var container = await CreateTempContainer();

        await container.CreateItemAsync(
            new { id = "1", pk = "a", name = "Alice", skills = new[] { "C#", "SQL" } },
            new PartitionKey("a"));
        await container.CreateItemAsync(
            new { id = "2", pk = "a", name = "Bob", skills = new[] { "Python" } },
            new PartitionKey("a"));

        var query = container.GetItemQueryIterator<dynamic>(
            "SELECT c.name, s AS skill FROM c JOIN s IN c.skills");

        var results = await Drain(query);
        results.Count.ShouldBe(3); // Alice×2 + Bob×1
    }

    [Fact]
    public async Task Join_SelectValue_ShouldReturnFlatList()
    {
        var container = await CreateTempContainer();

        await container.CreateItemAsync(
            new { id = "1", pk = "a", tags = new[] { "a", "b" } },
            new PartitionKey("a"));
        await container.CreateItemAsync(
            new { id = "2", pk = "a", tags = new[] { "c" } },
            new PartitionKey("a"));

        var query = container.GetItemQueryIterator<dynamic>(
            "SELECT VALUE t FROM c JOIN t IN c.tags");

        var results = await Drain(query);
        results.Count.ShouldBe(3);
    }

    private async Task<List<dynamic>> Drain(FeedIterator<dynamic> iterator)
    {
        var results = new List<dynamic>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            results.AddRange(page);
        }
        return results;
    }

    private async Task<Container> CreateTempContainer()
    {
        var dbName = $"test-db-{Guid.NewGuid():N}";
        var db = (await _client.CreateDatabaseAsync(dbName)).Database;
        return (await db.CreateContainerAsync($"test-coll-{Guid.NewGuid():N}", "/pk")).Container;
    }
}
