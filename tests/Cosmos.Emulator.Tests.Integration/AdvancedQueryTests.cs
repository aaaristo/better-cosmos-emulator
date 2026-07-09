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

    [Fact]
    public async Task UpperLower_ShouldFoldAccentedCharacters_Invariant()
    {
        var container = await CreateTempContainer();
        await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk1", name = "Grütter", city = "café" },
            new PartitionKey("pk1"));

        // UPPER must fold accented letters (Unicode, culture-invariant), matching real Cosmos.
        var upper = await Drain(container.GetItemQueryIterator<dynamic>(
            "SELECT VALUE UPPER(c.name) FROM c"));
        upper.Count.ShouldBe(1);
        ((string)upper[0]).ShouldBe("GRÜTTER");

        // LOWER must fold accented letters back down.
        var lower = await Drain(container.GetItemQueryIterator<dynamic>(
            "SELECT VALUE LOWER('GRÜTTER') FROM c"));
        lower.Count.ShouldBe(1);
        ((string)lower[0]).ShouldBe("grütter");

        // Plain ASCII must still work.
        var ascii = await Drain(container.GetItemQueryIterator<dynamic>(
            "SELECT VALUE UPPER('abc') FROM c"));
        ((string)ascii[0]).ShouldBe("ABC");

        // A REPLACE-after-UPPER accent fold must now match (the real-world query pattern).
        var replaced = await Drain(container.GetItemQueryIterator<dynamic>(
            "SELECT VALUE REPLACE(UPPER(c.name), 'Ü', 'U') FROM c"));
        ((string)replaced[0]).ShouldBe("GRUTTER");
    }

    [Fact]
    public async Task StringSearch_ShouldBeCaseSensitiveAndLiteral()
    {
        var container = await CreateTempContainer();
        await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk1", name = "Hello", text = "a50b", accent = "Grütter" },
            new PartitionKey("pk1"));

        // CONTAINS is case-SENSITIVE by default (SQLite LIKE would wrongly match 'hello').
        (await Scalar<bool>(container, "SELECT VALUE CONTAINS(c.name, 'Hello') FROM c")).ShouldBeTrue();
        (await Scalar<bool>(container, "SELECT VALUE CONTAINS(c.name, 'hello') FROM c")).ShouldBeFalse();
        // Optional case-insensitive flag.
        (await Scalar<bool>(container, "SELECT VALUE CONTAINS(c.name, 'hello', true) FROM c")).ShouldBeTrue();
        // ...and it folds Unicode/accents correctly (relies on the invariant UPPER override).
        (await Scalar<bool>(container, "SELECT VALUE CONTAINS(c.accent, 'grÜtter', true) FROM c")).ShouldBeTrue();
        // Needle wildcards are treated literally, NOT as LIKE wildcards.
        (await Scalar<bool>(container, "SELECT VALUE CONTAINS(c.text, '5_') FROM c")).ShouldBeFalse();
        (await Scalar<bool>(container, "SELECT VALUE CONTAINS(c.text, '50') FROM c")).ShouldBeTrue();
        // Empty needle -> true (matches Cosmos).
        (await Scalar<bool>(container, "SELECT VALUE CONTAINS(c.name, '') FROM c")).ShouldBeTrue();

        // STARTSWITH / ENDSWITH: case-sensitive, with optional flag and empty-affix handling.
        (await Scalar<bool>(container, "SELECT VALUE STARTSWITH(c.name, 'He') FROM c")).ShouldBeTrue();
        (await Scalar<bool>(container, "SELECT VALUE STARTSWITH(c.name, 'he') FROM c")).ShouldBeFalse();
        (await Scalar<bool>(container, "SELECT VALUE STARTSWITH(c.name, 'he', true) FROM c")).ShouldBeTrue();
        (await Scalar<bool>(container, "SELECT VALUE STARTSWITH(c.name, '') FROM c")).ShouldBeTrue();
        (await Scalar<bool>(container, "SELECT VALUE ENDSWITH(c.name, 'lo') FROM c")).ShouldBeTrue();
        (await Scalar<bool>(container, "SELECT VALUE ENDSWITH(c.name, 'LO') FROM c")).ShouldBeFalse();
        (await Scalar<bool>(container, "SELECT VALUE ENDSWITH(c.name, 'LO', true) FROM c")).ShouldBeTrue();
        (await Scalar<bool>(container, "SELECT VALUE ENDSWITH(c.name, '') FROM c")).ShouldBeTrue();
    }

    [Fact]
    public async Task StringTrimAndMath_ShouldMatchCosmosSemantics()
    {
        var container = await CreateTempContainer();
        await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk1" },
            new PartitionKey("pk1"));

        // LTRIM must trim ONLY the left; RTRIM only the right; TRIM both.
        (await Scalar<string>(container, "SELECT VALUE LTRIM('  ab  ') FROM c")).ShouldBe("ab  ");
        (await Scalar<string>(container, "SELECT VALUE RTRIM('  ab  ') FROM c")).ShouldBe("  ab");
        (await Scalar<string>(container, "SELECT VALUE TRIM('  ab  ') FROM c")).ShouldBe("ab");

        // FLOOR / CEILING must round the correct direction for negatives.
        (await Scalar<double>(container, "SELECT VALUE FLOOR(-2.5) FROM c")).ShouldBe(-3);
        (await Scalar<double>(container, "SELECT VALUE FLOOR(2.5) FROM c")).ShouldBe(2);
        (await Scalar<double>(container, "SELECT VALUE CEILING(-2.5) FROM c")).ShouldBe(-2);
        (await Scalar<double>(container, "SELECT VALUE CEILING(2.5) FROM c")).ShouldBe(3);
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

    private async Task<T> Scalar<T>(Container container, string sql)
    {
        var results = new List<T>();
        var it = container.GetItemQueryIterator<T>(sql);
        while (it.HasMoreResults)
            results.AddRange(await it.ReadNextAsync());
        results.Count.ShouldBe(1, $"expected exactly one scalar result for: {sql}");
        return results[0];
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
