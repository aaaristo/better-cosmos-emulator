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

    [Fact(Timeout = 15000)]
    public async Task SelectDistinctValue_DotNotation_ConcurrentWithWrites_ShouldNotHang()
    {
        // Exact production pattern: SELECT DISTINCT VALUE c.Repo FROM c
        // while concurrent writes are happening to the same container
        var dbName = $"test-db-{Guid.NewGuid():N}";
        var db = (await _client.CreateDatabaseAsync(dbName)).Database;
        var container = (await db.CreateContainerAsync($"test-coll-{Guid.NewGuid():N}", "/Repo")).Container;

        // Seed some data across partitions
        for (int i = 0; i < 20; i++)
        {
            var repo = i % 3 == 0 ? "Chain" : i % 3 == 1 ? "AMA001" : "AMA002";
            await container.CreateItemAsync(
                new { id = $"item-{i}", Repo = repo, Path = $"/data/{i}" },
                new PartitionKey(repo));
        }

        // Start concurrent writes
        var writeCts = new CancellationTokenSource();
        var writeTask = Task.Run(async () =>
        {
            int idx = 100;
            while (!writeCts.Token.IsCancellationRequested)
            {
                try
                {
                    await container.UpsertItemAsync(
                        new { id = $"write-{idx % 20}", Repo = "AMA001", Path = $"/write/{idx}" },
                        new PartitionKey("AMA001"));
                    idx++;
                }
                catch { }
            }
        });

        // Run the DISTINCT VALUE query multiple times while writes happen
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var query = new QueryDefinition("SELECT DISTINCT VALUE c.Repo FROM c");
            var results = new List<string>();
            using var iterator = container.GetItemQueryIterator<string>(query);
            while (iterator.HasMoreResults)
            {
                var page = await iterator.ReadNextAsync();
                results.AddRange(page);
            }

            results.Count.ShouldBeGreaterThanOrEqualTo(2);
            results.ShouldContain("Chain");
            results.ShouldContain("AMA001");
        }

        writeCts.Cancel();
        await writeTask;
    }

    [Fact(Timeout = 30000)]
    public async Task QueryWithOwnLimit_ShouldNotLoopForever()
    {
        // Regression: EF Core's Take(1000) generates OFFSET 0 LIMIT @p with @p=1000
        // AND sets x-ms-max-item-count to 1000.
        // The emulator saw results.Count == maxItems (1000 == 1000) and set a continuation
        // token. The SDK followed the continuation, but the query has its own OFFSET 0 so
        // the same 1000 results came back every time — infinite loop.
        var dbName = $"test-db-{Guid.NewGuid():N}";
        var db = (await _client.CreateDatabaseAsync(dbName)).Database;
        var container = (await db.CreateContainerAsync($"test-coll-{Guid.NewGuid():N}", "/partitionKey")).Container;

        // Insert exactly 1000 items — matches the LIMIT, triggering the bug
        for (int i = 0; i < 1000; i++)
        {
            await container.CreateItemAsync(
                new { id = $"item-{i:D4}", partitionKey = "pk1", name = $"Item {i}" },
                new PartitionKey("pk1"));
        }

        // EF Core pattern: OFFSET 0 LIMIT 1000, MaxItemCount=1000
        var query = new QueryDefinition("SELECT VALUE c FROM root c OFFSET 0 LIMIT @p")
            .WithParameter("@p", 1000);

        var results = new List<dynamic>();
        int pageCount = 0;
        using var iterator = container.GetItemQueryIterator<dynamic>(query,
            requestOptions: new QueryRequestOptions { MaxItemCount = 1000 });
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            results.AddRange(page);
            pageCount++;
            if (pageCount > 3)
                throw new Exception($"Infinite paging: {pageCount} pages, {results.Count} results");
        }

        results.Count.ShouldBe(1000);
        pageCount.ShouldBe(1);
    }

    [Fact]
    public async Task InvalidQuery_ShouldReturnDiagnosticErrorBody()
    {
        // Create a real container, then send the query over raw HTTP — the SDK's query
        // pipeline hides the response body, but the actual 400 body carries the diagnostics
        // (and the server logs the full stack trace via ILogger).
        var dbName = $"test-db-{Guid.NewGuid():N}";
        var collName = $"test-coll-{Guid.NewGuid():N}";
        var db = (await _client.CreateDatabaseAsync(dbName)).Database;
        await db.CreateContainerAsync(collName, "/partitionKey");

        const string badQuery = "SELECT * FROM c WHERE c.city IN"; // parser rejects this

        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        })
        { BaseAddress = _client.Endpoint };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/dbs/{dbName}/colls/{collName}/docs")
        {
            Content = new StringContent(
                $"{{\"query\":\"{badQuery}\"}}", System.Text.Encoding.UTF8, "application/query+json")
        };
        request.Headers.TryAddWithoutValidation("Authorization", "type=master&ver=1.0&sig=test");
        request.Headers.TryAddWithoutValidation("x-ms-date", DateTime.UtcNow.ToString("R"));

        var response = await http.SendAsync(request);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        // The diagnostic body echoes the original statement and how far we got, so the exact
        // failing query can be recovered without reproducing.
        body.ShouldContain(badQuery);
        body.ShouldContain("parse/translation");
        body.ShouldContain("\"stage\"");
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
