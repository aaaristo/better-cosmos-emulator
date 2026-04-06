using Microsoft.Azure.Cosmos;
using Shouldly;

namespace Cosmos.Emulator.Tests.Integration;

[Collection("Emulator")]
public class AdvancedQueryTests
{
    private readonly CosmosClient _client;

    public AdvancedQueryTests(EmulatorFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task OffsetLimit_ShouldPaginate()
    {
        var container = await SeedTestData();

        var query = container.GetItemQueryIterator<dynamic>(
            "SELECT * FROM c ORDER BY c.age OFFSET 1 LIMIT 2");

        var results = await Drain(query);
        results.Count.ShouldBe(2);
        ((string)results[0].name).ShouldBe("Alice");  // age 30, skipped Bob (25)
        ((string)results[1].name).ShouldBe("Charlie"); // age 35
    }

    [Fact]
    public async Task Distinct_ShouldRemoveDuplicates()
    {
        var container = await SeedTestData();

        var query = container.GetItemQueryIterator<dynamic>(
            "SELECT DISTINCT VALUE c.city FROM c");

        var results = await Drain(query);
        results.Count.ShouldBe(2); // Seattle, Portland
    }

    [Fact]
    public async Task CountAggregate_ShouldReturnCount()
    {
        var container = await SeedTestData();

        var query = container.GetItemQueryIterator<dynamic>(
            "SELECT VALUE COUNT(1) FROM c");

        var results = await Drain(query);
        results.Count.ShouldBe(1);
        ((int)results[0]).ShouldBe(3);
    }

    [Fact]
    public async Task SumAggregate_ShouldSum()
    {
        var container = await SeedTestData();

        var query = container.GetItemQueryIterator<dynamic>(
            "SELECT VALUE SUM(c.age) FROM c");

        var results = await Drain(query);
        results.Count.ShouldBe(1);
        ((int)results[0]).ShouldBe(90); // 25 + 30 + 35
    }

    [Fact]
    public async Task AvgAggregate_ShouldAverage()
    {
        var container = await SeedTestData();

        var query = container.GetItemQueryIterator<dynamic>(
            "SELECT VALUE AVG(c.age) FROM c");

        var results = await Drain(query);
        results.Count.ShouldBe(1);
        ((double)results[0]).ShouldBe(30.0);
    }

    [Fact]
    public async Task MinMaxAggregate_ShouldWork()
    {
        var container = await SeedTestData();

        var minQ = container.GetItemQueryIterator<dynamic>("SELECT VALUE MIN(c.age) FROM c");
        var maxQ = container.GetItemQueryIterator<dynamic>("SELECT VALUE MAX(c.age) FROM c");

        var minResults = await Drain(minQ);
        var maxResults = await Drain(maxQ);

        ((int)minResults[0]).ShouldBe(25);
        ((int)maxResults[0]).ShouldBe(35);
    }

    [Fact]
    public async Task InOperator_ShouldFilter()
    {
        var container = await SeedTestData();

        var query = container.GetItemQueryIterator<dynamic>(
            "SELECT * FROM c WHERE c.city IN ('Seattle', 'Denver')");

        var results = await Drain(query);
        results.Count.ShouldBe(1);
        ((string)results[0].name).ShouldBe("Alice");
    }

    [Fact]
    public async Task BetweenOperator_ShouldFilter()
    {
        var container = await SeedTestData();

        var query = container.GetItemQueryIterator<dynamic>(
            "SELECT * FROM c WHERE c.age BETWEEN 26 AND 34");

        var results = await Drain(query);
        results.Count.ShouldBe(1);
        ((string)results[0].name).ShouldBe("Alice"); // age 30
    }

    [Fact]
    public async Task StartsWithFunction_ShouldFilter()
    {
        var container = await SeedTestData();

        var query = container.GetItemQueryIterator<dynamic>(
            "SELECT * FROM c WHERE STARTSWITH(c.name, 'Al')");

        var results = await Drain(query);
        results.Count.ShouldBe(1);
        ((string)results[0].name).ShouldBe("Alice");
    }

    [Fact]
    public async Task ArrayContains_ShouldFilter()
    {
        var container = await CreateTempContainer();

        await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk1", name = "Alice", tags = new[] { "admin", "user" } },
            new PartitionKey("pk1"));
        await container.CreateItemAsync(
            new { id = "2", partitionKey = "pk1", name = "Bob", tags = new[] { "user" } },
            new PartitionKey("pk1"));

        var query = container.GetItemQueryIterator<dynamic>(
            "SELECT * FROM c WHERE ARRAY_CONTAINS(c.tags, 'admin')");

        var results = await Drain(query);
        results.Count.ShouldBe(1);
        ((string)results[0].name).ShouldBe("Alice");
    }

    [Fact]
    public async Task IsDefinedFunction_ShouldFilter()
    {
        var container = await CreateTempContainer();

        await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk1", name = "Alice", nickname = "Ali" },
            new PartitionKey("pk1"));
        await container.CreateItemAsync(
            new { id = "2", partitionKey = "pk1", name = "Bob" },
            new PartitionKey("pk1"));

        var query = container.GetItemQueryIterator<dynamic>(
            "SELECT * FROM c WHERE IS_DEFINED(c.nickname)");

        var results = await Drain(query);
        results.Count.ShouldBe(1);
        ((string)results[0].name).ShouldBe("Alice");
    }

    [Fact]
    public async Task StringFunctions_ShouldWork()
    {
        var container = await SeedTestData();

        var query = container.GetItemQueryIterator<dynamic>(
            "SELECT VALUE UPPER(c.name) FROM c WHERE c.name = 'Alice'");

        var results = await Drain(query);
        results.Count.ShouldBe(1);
        ((string)results[0]).ShouldBe("ALICE");
    }

    [Fact(Timeout = 10000)]
    public async Task GroupBy_ShouldAggregate()
    {
        var container = await SeedTestData();

        var query = container.GetItemQueryIterator<dynamic>(
            "SELECT c.city, COUNT(1) AS cnt FROM c GROUP BY c.city");

        var results = await Drain(query);
        results.Count.ShouldBe(2); // Seattle (1), Portland (2)
    }

    [Fact(Timeout = 10000)]
    public async Task CoalesceOperator_ShouldFallback()
    {
        var container = await CreateTempContainer();

        await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk1", name = "Alice", nickname = "Ali" },
            new PartitionKey("pk1"));
        await container.CreateItemAsync(
            new { id = "2", partitionKey = "pk1", name = "Bob" },
            new PartitionKey("pk1"));

        var query = container.GetItemQueryIterator<dynamic>(
            "SELECT c.nickname ?? c.name AS displayName FROM c ORDER BY c.name");

        var results = await Drain(query);
        results.Count.ShouldBe(2);
        ((string)results[0].displayName).ShouldBe("Ali");   // has nickname
        ((string)results[1].displayName).ShouldBe("Bob");   // falls back to name
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
