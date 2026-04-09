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

    [Fact]
    public async Task EqualsNull_ShouldMatchNullValues()
    {
        // Cosmos SQL allows "c.Deleted = null" as syntactic sugar for IS_NULL
        var container = await CreateTempContainer();

        await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk1", Deleted = (long?)null, name = "Active" },
            new PartitionKey("pk1"));
        await container.CreateItemAsync(
            new { id = "2", partitionKey = "pk1", Deleted = 1700000000L, name = "Deleted" },
            new PartitionKey("pk1"));
        await container.CreateItemAsync(
            new { id = "3", partitionKey = "pk1", Deleted = (long?)null, name = "AlsoActive" },
            new PartitionKey("pk1"));

        // = null
        var q1 = new QueryDefinition("SELECT * FROM c WHERE c.Deleted = null");
        var r1 = new List<dynamic>();
        using var it1 = container.GetItemQueryIterator<dynamic>(q1);
        while (it1.HasMoreResults)
        {
            var page = await it1.ReadNextAsync();
            r1.AddRange(page);
        }
        r1.Count.ShouldBe(2);

        // != null
        var q2 = new QueryDefinition("SELECT * FROM c WHERE c.Deleted != null");
        var r2 = new List<dynamic>();
        using var it2 = container.GetItemQueryIterator<dynamic>(q2);
        while (it2.HasMoreResults)
        {
            var page = await it2.ReadNextAsync();
            r2.AddRange(page);
        }
        r2.Count.ShouldBe(1);
        ((string)r2[0].name).ShouldBe("Deleted");
    }

    [Fact]
    public async Task SelectDistinctValue_ShouldReturnUniqueScalars()
    {
        var container = await SeedTestData();

        var query = new QueryDefinition("SELECT DISTINCT VALUE c.city FROM c");
        var results = new List<string>();
        using var iterator = container.GetItemQueryIterator<string>(query);
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            results.AddRange(page);
        }

        results.Count.ShouldBe(2);
        results.ShouldContain("Seattle");
        results.ShouldContain("Portland");
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

    [Fact]
    public async Task StartsWith_IsNull_OrderBy_WithParams_ShouldWork()
    {
        // Production query pattern from SyncEngine:
        // SELECT c.Path, c.Hash FROM c WHERE c.Repo = @repo AND STARTSWITH(c.Path, @prefix) AND IS_NULL(c.Deleted) ORDER BY c.Path OFFSET 0 LIMIT @pageSize
        var dbName = $"test-db-{Guid.NewGuid():N}";
        var db = (await _client.CreateDatabaseAsync(dbName)).Database;
        var container = (await db.CreateContainerAsync($"test-coll-{Guid.NewGuid():N}", "/Repo")).Container;

        // Seed: 3 non-deleted items matching prefix, 1 deleted, 1 different prefix
        await container.CreateItemAsync(new { id = "1", Repo = "Chain", Path = "/config/item-a", Hash = "aaa", Deleted = (long?)null }, new PartitionKey("Chain"));
        await container.CreateItemAsync(new { id = "2", Repo = "Chain", Path = "/config/item-b", Hash = "bbb", Deleted = (long?)null }, new PartitionKey("Chain"));
        await container.CreateItemAsync(new { id = "3", Repo = "Chain", Path = "/config/item-c", Hash = "ccc", Deleted = (long?)null }, new PartitionKey("Chain"));
        await container.CreateItemAsync(new { id = "4", Repo = "Chain", Path = "/config/item-d", Hash = "ddd", Deleted = 1700000000L }, new PartitionKey("Chain"));
        await container.CreateItemAsync(new { id = "5", Repo = "Chain", Path = "/other/item-e", Hash = "eee", Deleted = (long?)null }, new PartitionKey("Chain"));

        var query = new QueryDefinition(
            "SELECT c.Path, c.Hash FROM c WHERE c.Repo = @repo AND STARTSWITH(c.Path, @prefix) AND IS_NULL(c.Deleted) ORDER BY c.Path OFFSET 0 LIMIT @pageSize")
            .WithParameter("@repo", "Chain")
            .WithParameter("@prefix", "/config/")
            .WithParameter("@pageSize", 10);

        var results = new List<dynamic>();
        using var iterator = container.GetItemQueryIterator<dynamic>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey("Chain") });
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            results.AddRange(page);
        }

        // Should return 3 non-deleted items with /config/ prefix, sorted by Path
        results.Count.ShouldBe(3);
        ((string)results[0].Path).ShouldBe("/config/item-a");
        ((string)results[1].Path).ShouldBe("/config/item-b");
        ((string)results[2].Path).ShouldBe("/config/item-c");
    }

    [Fact]
    public async Task StartsWith_IsNull_OrderBy_Pagination_ShouldWork()
    {
        // Same query but with LIMIT 2 to verify pagination via OFFSET/LIMIT
        var dbName = $"test-db-{Guid.NewGuid():N}";
        var db = (await _client.CreateDatabaseAsync(dbName)).Database;
        var container = (await db.CreateContainerAsync($"test-coll-{Guid.NewGuid():N}", "/Repo")).Container;

        for (int i = 0; i < 5; i++)
        {
            await container.CreateItemAsync(new
            {
                id = $"item-{i}",
                Repo = "Chain",
                Path = $"/config/item-{(char)('a' + i)}",
                Hash = $"hash-{i}",
                Deleted = (long?)null
            }, new PartitionKey("Chain"));
        }

        // Page 1: OFFSET 0 LIMIT 2
        var page1Query = new QueryDefinition(
            "SELECT c.Path, c.Hash FROM c WHERE c.Repo = @repo AND STARTSWITH(c.Path, @prefix) AND IS_NULL(c.Deleted) ORDER BY c.Path OFFSET 0 LIMIT @pageSize")
            .WithParameter("@repo", "Chain")
            .WithParameter("@prefix", "/config/")
            .WithParameter("@pageSize", 2);

        var page1 = new List<dynamic>();
        using var it1 = container.GetItemQueryIterator<dynamic>(page1Query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey("Chain") });
        while (it1.HasMoreResults)
        {
            var resp = await it1.ReadNextAsync();
            page1.AddRange(resp);
        }

        page1.Count.ShouldBe(2);
        ((string)page1[0].Path).ShouldBe("/config/item-a");
        ((string)page1[1].Path).ShouldBe("/config/item-b");

        // Page 2: OFFSET 2 LIMIT 2
        var page2Query = new QueryDefinition(
            "SELECT c.Path, c.Hash FROM c WHERE c.Repo = @repo AND STARTSWITH(c.Path, @prefix) AND IS_NULL(c.Deleted) ORDER BY c.Path OFFSET @offset LIMIT @pageSize")
            .WithParameter("@repo", "Chain")
            .WithParameter("@prefix", "/config/")
            .WithParameter("@offset", 2)
            .WithParameter("@pageSize", 2);

        var page2 = new List<dynamic>();
        using var it2 = container.GetItemQueryIterator<dynamic>(page2Query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey("Chain") });
        while (it2.HasMoreResults)
        {
            var resp = await it2.ReadNextAsync();
            page2.AddRange(resp);
        }

        page2.Count.ShouldBe(2);
        ((string)page2[0].Path).ShouldBe("/config/item-c");
        ((string)page2[1].Path).ShouldBe("/config/item-d");

        // Page 3: OFFSET 4 LIMIT 2 — should return only 1
        var page3Query = new QueryDefinition(
            "SELECT c.Path, c.Hash FROM c WHERE c.Repo = @repo AND STARTSWITH(c.Path, @prefix) AND IS_NULL(c.Deleted) ORDER BY c.Path OFFSET @offset LIMIT @pageSize")
            .WithParameter("@repo", "Chain")
            .WithParameter("@prefix", "/config/")
            .WithParameter("@offset", 4)
            .WithParameter("@pageSize", 2);

        var page3 = new List<dynamic>();
        using var it3 = container.GetItemQueryIterator<dynamic>(page3Query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey("Chain") });
        while (it3.HasMoreResults)
        {
            var resp = await it3.ReadNextAsync();
            page3.AddRange(resp);
        }

        page3.Count.ShouldBe(1);
        ((string)page3[0].Path).ShouldBe("/config/item-e");
    }

    private async Task<Container> CreateTempContainer()
    {
        var dbName = $"test-db-{Guid.NewGuid():N}";
        var db = (await _client.CreateDatabaseAsync(dbName)).Database;
        return (await db.CreateContainerAsync($"test-coll-{Guid.NewGuid():N}", "/partitionKey")).Container;
    }
}
