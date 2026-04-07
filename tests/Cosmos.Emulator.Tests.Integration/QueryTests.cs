using Microsoft.Azure.Cosmos;
using Shouldly;

namespace Cosmos.Emulator.Tests.Integration;

[Collection("Emulator")]
public class QueryTests
{
    private readonly CosmosClient _client;

    public QueryTests(EmulatorFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task SelectStar_ShouldReturnAllDocuments()
    {
        var container = await SeedTestData();

        var query = container.GetItemQueryIterator<dynamic>("SELECT * FROM c");
        var results = new List<dynamic>();
        while (query.HasMoreResults)
        {
            var page = await query.ReadNextAsync();
            results.AddRange(page);
        }

        results.Count.ShouldBe(3);
    }

    [Fact]
    public async Task WhereEquals_ShouldFilter()
    {
        var container = await SeedTestData();

        var query = container.GetItemQueryIterator<dynamic>(
            new QueryDefinition("SELECT * FROM c WHERE c.city = @city")
                .WithParameter("@city", "Seattle"));

        var results = new List<dynamic>();
        while (query.HasMoreResults)
        {
            var page = await query.ReadNextAsync();
            results.AddRange(page);
        }

        results.Count.ShouldBe(1);
        ((string)results[0].name).ShouldBe("Alice");
    }

    [Fact]
    public async Task WhereGreaterThan_ShouldFilter()
    {
        var container = await SeedTestData();

        var query = container.GetItemQueryIterator<dynamic>(
            "SELECT * FROM c WHERE c.age > 28");

        var results = new List<dynamic>();
        while (query.HasMoreResults)
        {
            var page = await query.ReadNextAsync();
            results.AddRange(page);
        }

        results.Count.ShouldBe(2); // Alice (30) and Charlie (35)
    }

    [Fact]
    public async Task OrderBy_ShouldSort()
    {
        var container = await SeedTestData();

        var query = container.GetItemQueryIterator<dynamic>(
            "SELECT * FROM c ORDER BY c.age ASC");

        var results = new List<dynamic>();
        while (query.HasMoreResults)
        {
            var page = await query.ReadNextAsync();
            results.AddRange(page);
        }

        results.Count.ShouldBe(3);
        ((string)results[0].name).ShouldBe("Bob");     // age 25
        ((string)results[1].name).ShouldBe("Alice");   // age 30
        ((string)results[2].name).ShouldBe("Charlie"); // age 35
    }

    [Fact]
    public async Task Top_ShouldLimitResults()
    {
        var container = await SeedTestData();

        var query = container.GetItemQueryIterator<dynamic>(
            "SELECT TOP 2 * FROM c ORDER BY c.age ASC");

        var results = new List<dynamic>();
        while (query.HasMoreResults)
        {
            var page = await query.ReadNextAsync();
            results.AddRange(page);
        }

        results.Count.ShouldBe(2);
    }

    [Fact]
    public async Task SelectSpecificFields_ShouldProjection()
    {
        var container = await SeedTestData();

        var query = container.GetItemQueryIterator<dynamic>(
            "SELECT c.name, c.age FROM c WHERE c.city = 'Seattle'");

        var results = new List<dynamic>();
        while (query.HasMoreResults)
        {
            var page = await query.ReadNextAsync();
            results.AddRange(page);
        }

        results.Count.ShouldBe(1);
        ((string)results[0].name).ShouldBe("Alice");
        ((int)results[0].age).ShouldBe(30);
    }

    [Fact]
    public async Task ContainsFunction_ShouldFilter()
    {
        var container = await SeedTestData();

        var query = container.GetItemQueryIterator<dynamic>(
            "SELECT * FROM c WHERE CONTAINS(c.name, 'li')");

        var results = new List<dynamic>();
        while (query.HasMoreResults)
        {
            var page = await query.ReadNextAsync();
            results.AddRange(page);
        }

        results.Count.ShouldBe(2); // Alice and Charlie
    }

    [Fact]
    public async Task NestedProperty_ShouldWork()
    {
        var container = await CreateTempContainer();

        await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk", address = new { city = "Seattle", zip = "98101" } },
            new PartitionKey("pk"));
        await container.CreateItemAsync(
            new { id = "2", partitionKey = "pk", address = new { city = "Portland", zip = "97201" } },
            new PartitionKey("pk"));

        var query = container.GetItemQueryIterator<dynamic>(
            "SELECT * FROM c WHERE c.address.city = 'Seattle'");

        var results = new List<dynamic>();
        while (query.HasMoreResults)
        {
            var page = await query.ReadNextAsync();
            results.AddRange(page);
        }

        results.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ArrayContains_WithParameterArray_ShouldWork()
    {
        // Regression: EF Core generates ARRAY_CONTAINS(@param, c["Property"])
        // where @param is a JSON array parameter. This is a cross-partition query pattern.
        var container = await SeedTestData();

        var query = new QueryDefinition(
            "SELECT VALUE c FROM root c WHERE ARRAY_CONTAINS(@names, c[\"name\"])")
            .WithParameter("@names", new[] { "Alice", "Charlie" });

        var results = new List<dynamic>();
        using var iterator = container.GetItemQueryIterator<dynamic>(query);
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            results.AddRange(page);
        }

        results.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ArrayContains_WithParameterArray_CrossPartitionBracketNotation_ShouldWork()
    {
        // Exact production pattern: EF Core cross-partition batch lookup
        // SELECT VALUE c FROM root c WHERE ARRAY_CONTAINS(@pathsToLookup, c["Path"])
        var dbName = $"test-db-{Guid.NewGuid():N}";
        var db = (await _client.CreateDatabaseAsync(dbName)).Database;
        var container = (await db.CreateContainerAsync($"test-coll-{Guid.NewGuid():N}", "/partitionKey")).Container;

        // Insert items in different partitions
        await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk1", Path = "/repo/item-a" },
            new PartitionKey("pk1"));
        await container.CreateItemAsync(
            new { id = "2", partitionKey = "pk2", Path = "/repo/item-b" },
            new PartitionKey("pk2"));
        await container.CreateItemAsync(
            new { id = "3", partitionKey = "pk1", Path = "/repo/item-c" },
            new PartitionKey("pk1"));

        // Cross-partition ARRAY_CONTAINS query — no partition key filter
        var query = new QueryDefinition(
            "SELECT VALUE c FROM root c WHERE ARRAY_CONTAINS(@pathsToLookup, c[\"Path\"])")
            .WithParameter("@pathsToLookup", new[] { "/repo/item-a", "/repo/item-b" });

        var results = new List<dynamic>();
        using var iterator = container.GetItemQueryIterator<dynamic>(query,
            requestOptions: new QueryRequestOptions { MaxItemCount = 100 });
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            results.AddRange(page);
        }

        results.Count.ShouldBe(2);
    }

    [Fact]
    public async Task SelectValue_CrossPartition_ShouldReturnAllDocuments()
    {
        // Verify basic cross-partition SELECT VALUE c works
        var dbName = $"test-db-{Guid.NewGuid():N}";
        var db = (await _client.CreateDatabaseAsync(dbName)).Database;
        var container = (await db.CreateContainerAsync($"test-coll-{Guid.NewGuid():N}", "/partitionKey")).Container;

        await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk1", Path = "/repo/item-a" },
            new PartitionKey("pk1"));
        await container.CreateItemAsync(
            new { id = "2", partitionKey = "pk2", Path = "/repo/item-b" },
            new PartitionKey("pk2"));

        // Simple cross-partition query without ARRAY_CONTAINS
        var query = new QueryDefinition("SELECT VALUE c FROM root c");

        var results = new List<dynamic>();
        using var iterator = container.GetItemQueryIterator<dynamic>(query);
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            results.AddRange(page);
        }

        results.Count.ShouldBe(2);
    }

    private async Task<Container> SeedTestData()
    {
        var container = await CreateTempContainer();

        await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk1", name = "Alice", age = 30, city = "Seattle" },
            new PartitionKey("pk1"));
        await container.CreateItemAsync(
            new { id = "2", partitionKey = "pk1", name = "Bob", age = 25, city = "Portland" },
            new PartitionKey("pk1"));
        await container.CreateItemAsync(
            new { id = "3", partitionKey = "pk1", name = "Charlie", age = 35, city = "Portland" },
            new PartitionKey("pk1"));

        return container;
    }

    private async Task<Container> CreateTempContainer()
    {
        var dbName = $"test-db-{Guid.NewGuid():N}";
        var db = (await _client.CreateDatabaseAsync(dbName)).Database;
        return (await db.CreateContainerAsync($"test-coll-{Guid.NewGuid():N}", "/partitionKey")).Container;
    }
}
