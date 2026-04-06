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
