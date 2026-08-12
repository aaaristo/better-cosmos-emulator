using Microsoft.Azure.Cosmos;
using Shouldly;

namespace Cosmos.Emulator.Tests.Integration;

/// <summary>
/// Hierarchical (sub-partitioned) containers: multiple partition key paths, where
/// reads may be scoped either by the full key or by a prefix of its components.
/// </summary>
[Collection("Emulator")]
public class HierarchicalPartitionKeyTests
{
    private readonly CosmosClient _client;

    public HierarchicalPartitionKeyTests(EmulatorFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task CreateContainer_WithMultiplePaths_ShouldRoundTripDefinition()
    {
        var db = await CreateTempDatabase();

        var response = await db.CreateContainerAsync(
            new ContainerProperties("hpk-container", new List<string> { "/tenantId", "/userId" }));

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);

        var read = await db.GetContainer("hpk-container").ReadContainerAsync();
        read.Resource.PartitionKeyPaths.ShouldBe(new List<string> { "/tenantId", "/userId" });
    }

    [Fact]
    public async Task PointRead_WithFullKey_ShouldReturnDocument()
    {
        var container = await CreateHierarchicalContainer();

        await container.CreateItemAsync(
            new { id = "doc1", tenantId = "t1", userId = "u1", name = "Alice" },
            FullKey("t1", "u1"));

        var read = await container.ReadItemAsync<TestDoc>("doc1", FullKey("t1", "u1"));

        read.Resource.name.ShouldBe("Alice");
    }

    [Fact]
    public async Task PointRead_WithWrongSecondComponent_ShouldNotFind()
    {
        var container = await CreateHierarchicalContainer();

        await container.CreateItemAsync(
            new { id = "doc1", tenantId = "t1", userId = "u1", name = "Alice" },
            FullKey("t1", "u1"));

        var ex = await Should.ThrowAsync<CosmosException>(
            () => container.ReadItemAsync<TestDoc>("doc1", FullKey("t1", "u2")));

        ex.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SameDocumentId_InDifferentSubPartitions_ShouldCoexist()
    {
        var container = await CreateHierarchicalContainer();

        await container.CreateItemAsync(
            new { id = "shared", tenantId = "t1", userId = "u1", name = "First" },
            FullKey("t1", "u1"));
        await container.CreateItemAsync(
            new { id = "shared", tenantId = "t1", userId = "u2", name = "Second" },
            FullKey("t1", "u2"));

        (await container.ReadItemAsync<TestDoc>("shared", FullKey("t1", "u1"))).Resource.name.ShouldBe("First");
        (await container.ReadItemAsync<TestDoc>("shared", FullKey("t1", "u2"))).Resource.name.ShouldBe("Second");
    }

    [Fact]
    public async Task Query_WithFullKey_ShouldReturnOnlyThatSubPartition()
    {
        var container = await SeedTenants();

        var results = await RunQuery(container, "SELECT * FROM c", FullKey("t1", "u1"));

        results.Select(d => d.id).OrderBy(x => x).ShouldBe(new[] { "t1-u1-a", "t1-u1-b" });
    }

    [Fact]
    public async Task Query_WithPrefixKey_ShouldReturnWholeTenant()
    {
        var container = await SeedTenants();

        var results = await RunQuery(container, "SELECT * FROM c", PrefixKey("t1"));

        results.Select(d => d.id).OrderBy(x => x)
            .ShouldBe(new[] { "t1-u1-a", "t1-u1-b", "t1-u2-a" });
    }

    [Fact]
    public async Task Query_WithPrefixKey_ShouldExcludeOtherTenants()
    {
        var container = await SeedTenants();

        var results = await RunQuery(container, "SELECT * FROM c", PrefixKey("t2"));

        results.Select(d => d.id).ShouldBe(new[] { "t2-u1-a" });
    }

    [Fact]
    public async Task Query_WithPrefixKey_ShouldNotMatchLongerTenantValue()
    {
        // Guards the range-bound arithmetic: "t1" must not match the tenant "t10".
        var container = await CreateHierarchicalContainer();

        await container.CreateItemAsync(
            new { id = "short", tenantId = "t1", userId = "u1" }, FullKey("t1", "u1"));
        await container.CreateItemAsync(
            new { id = "long", tenantId = "t10", userId = "u1" }, FullKey("t10", "u1"));

        var results = await RunQuery(container, "SELECT * FROM c", PrefixKey("t1"));

        results.Select(d => d.id).ShouldBe(new[] { "short" });
    }

    [Fact]
    public async Task Query_WithNumericPrefixKey_ShouldNotMatchLongerNumber()
    {
        // A numeric component serializes without quotes, so "1" is a textual prefix
        // of "12" — the delimiter check must still keep them apart.
        var db = await CreateTempDatabase();
        var container = (await db.CreateContainerAsync(
            new ContainerProperties($"num-{Guid.NewGuid():N}", new List<string> { "/tenantId", "/userId" }))).Container;

        await container.CreateItemAsync(
            new { id = "one", tenantId = 1, userId = "u1" },
            new PartitionKeyBuilder().Add(1).Add("u1").Build());
        await container.CreateItemAsync(
            new { id = "twelve", tenantId = 12, userId = "u1" },
            new PartitionKeyBuilder().Add(12).Add("u1").Build());

        var results = await RunQuery(container, "SELECT * FROM c",
            new PartitionKeyBuilder().Add(1).Build());

        results.Select(d => d.id).ShouldBe(new[] { "one" });
    }

    [Fact]
    public async Task Query_WithPrefixKey_AndWhereClause_ShouldCombine()
    {
        var container = await SeedTenants();

        var results = await RunQuery(container,
            "SELECT * FROM c WHERE c.userId = 'u1'", PrefixKey("t1"));

        results.Select(d => d.id).OrderBy(x => x).ShouldBe(new[] { "t1-u1-a", "t1-u1-b" });
    }

    [Fact]
    public async Task Query_ThreeLevelKey_ShouldSupportPartialPrefixAtEachDepth()
    {
        var db = await CreateTempDatabase();
        var container = (await db.CreateContainerAsync(new ContainerProperties(
            $"deep-{Guid.NewGuid():N}",
            new List<string> { "/tenantId", "/userId", "/sessionId" }))).Container;

        foreach (var (tenant, user, session) in new[]
                 {
                     ("t1", "u1", "s1"), ("t1", "u1", "s2"), ("t1", "u2", "s1"), ("t2", "u1", "s1")
                 })
        {
            await container.CreateItemAsync(
                new { id = $"{tenant}-{user}-{session}", tenantId = tenant, userId = user, sessionId = session },
                new PartitionKeyBuilder().Add(tenant).Add(user).Add(session).Build());
        }

        var oneLevel = await RunQuery(container, "SELECT * FROM c",
            new PartitionKeyBuilder().Add("t1").Build());
        oneLevel.Count.ShouldBe(3);

        var twoLevels = await RunQuery(container, "SELECT * FROM c",
            new PartitionKeyBuilder().Add("t1").Add("u1").Build());
        twoLevels.Select(d => d.id).OrderBy(x => x).ShouldBe(new[] { "t1-u1-s1", "t1-u1-s2" });

        var fullKey = await RunQuery(container, "SELECT * FROM c",
            new PartitionKeyBuilder().Add("t1").Add("u1").Add("s1").Build());
        fullKey.Select(d => d.id).ShouldBe(new[] { "t1-u1-s1" });
    }

    [Fact]
    public async Task ChangeFeed_ScopedToPrefixKey_ShouldReturnOnlyThatTenant()
    {
        var container = await SeedTenants();

        var iterator = container.GetChangeFeedIterator<TestDoc>(
            ChangeFeedStartFrom.Beginning(FeedRange.FromPartitionKey(PrefixKey("t1"))),
            ChangeFeedMode.LatestVersion);

        var changes = new List<TestDoc>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            if (page.StatusCode == System.Net.HttpStatusCode.NotModified)
                break;
            changes.AddRange(page);
        }

        changes.Select(d => d.id).OrderBy(x => x)
            .ShouldBe(new[] { "t1-u1-a", "t1-u1-b", "t1-u2-a" });
    }

    [Fact]
    public async Task Delete_WithFullKey_ShouldNotAffectSiblingSubPartition()
    {
        var container = await SeedTenants();

        await container.DeleteItemAsync<TestDoc>("t1-u1-a", FullKey("t1", "u1"));

        var remaining = await RunQuery(container, "SELECT * FROM c", PrefixKey("t1"));
        remaining.Select(d => d.id).OrderBy(x => x).ShouldBe(new[] { "t1-u1-b", "t1-u2-a" });
    }

    [Fact]
    public async Task AllVersionsChangeFeed_ScopedToPrefixKey_ShouldReturnOnlyThatTenant()
    {
        var container = await SeedTenants();

        var iterator = container.GetChangeFeedIterator<dynamic>(
            ChangeFeedStartFrom.Beginning(FeedRange.FromPartitionKey(PrefixKey("t1"))),
            ChangeFeedMode.AllVersionsAndDeletes);

        var count = 0;
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            if (page.StatusCode == System.Net.HttpStatusCode.NotModified)
                break;
            count += page.Count;
        }

        count.ShouldBe(3); // the three documents under t1; t2 is excluded
    }

    [Fact]
    public async Task Query_WithPartialPartitionKeyHeader_ShouldScopeToPrefix()
    {
        // The .NET SDK expresses a prefix as an EPK range, but the REST API also accepts a
        // shortened x-ms-documentdb-partitionkey. Exercised over raw HTTP because the SDK
        // will not produce that shape.
        var dbName = $"test-db-{Guid.NewGuid():N}";
        var collName = $"test-coll-{Guid.NewGuid():N}";
        var db = (await _client.CreateDatabaseAsync(dbName)).Database;
        var container = (await db.CreateContainerAsync(
            new ContainerProperties(collName, new List<string> { "/tenantId", "/userId" }))).Container;

        foreach (var (tenant, user) in new[] { ("t1", "u1"), ("t1", "u2"), ("t2", "u1") })
        {
            await container.CreateItemAsync(
                new { id = $"{tenant}-{user}", tenantId = tenant, userId = user },
                FullKey(tenant, user));
        }

        var body = await QueryOverRawHttp(dbName, collName, "SELECT * FROM c", "[\"t1\"]");

        body.ShouldContain("t1-u1");
        body.ShouldContain("t1-u2");
        body.ShouldNotContain("t2-u1");
    }

    private async Task<string> QueryOverRawHttp(
        string dbName, string collName, string query, string partitionKeyHeader)
    {
        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        })
        { BaseAddress = _client.Endpoint };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/dbs/{dbName}/colls/{collName}/docs")
        {
            Content = new StringContent(
                $"{{\"query\":\"{query}\"}}", System.Text.Encoding.UTF8, "application/query+json")
        };
        request.Headers.TryAddWithoutValidation("Authorization", "type=master&ver=1.0&sig=test");
        request.Headers.TryAddWithoutValidation("x-ms-date", DateTime.UtcNow.ToString("R"));
        request.Headers.TryAddWithoutValidation("x-ms-documentdb-isquery", "true");
        request.Headers.TryAddWithoutValidation("x-ms-documentdb-query-enablecrosspartition", "true");
        request.Headers.TryAddWithoutValidation("x-ms-documentdb-partitionkey", partitionKeyHeader);

        var response = await http.SendAsync(request);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }

    private static PartitionKey FullKey(string tenant, string user) =>
        new PartitionKeyBuilder().Add(tenant).Add(user).Build();

    private static PartitionKey PrefixKey(string tenant) =>
        new PartitionKeyBuilder().Add(tenant).Build();

    private static async Task<List<TestDoc>> RunQuery(Container container, string sql, PartitionKey partitionKey)
    {
        var iterator = container.GetItemQueryIterator<TestDoc>(
            new QueryDefinition(sql),
            requestOptions: new QueryRequestOptions { PartitionKey = partitionKey });

        var results = new List<TestDoc>();
        while (iterator.HasMoreResults)
            results.AddRange(await iterator.ReadNextAsync());
        return results;
    }

    /// <summary>t1 has two users (one with two docs), t2 has one — so prefix and full key differ.</summary>
    private async Task<Container> SeedTenants()
    {
        var container = await CreateHierarchicalContainer();

        foreach (var (tenant, user, suffix) in new[]
                 {
                     ("t1", "u1", "a"), ("t1", "u1", "b"), ("t1", "u2", "a"), ("t2", "u1", "a")
                 })
        {
            await container.CreateItemAsync(
                new { id = $"{tenant}-{user}-{suffix}", tenantId = tenant, userId = user },
                FullKey(tenant, user));
        }

        return container;
    }

    private async Task<Container> CreateHierarchicalContainer()
    {
        var db = await CreateTempDatabase();
        var properties = new ContainerProperties(
            $"hpk-{Guid.NewGuid():N}", new List<string> { "/tenantId", "/userId" });
        return (await db.CreateContainerAsync(properties)).Container;
    }

    private async Task<Database> CreateTempDatabase()
    {
        var dbName = $"test-db-{Guid.NewGuid():N}";
        return (await _client.CreateDatabaseAsync(dbName)).Database;
    }

    public class TestDoc
    {
        public string id { get; set; } = "";
        public string tenantId { get; set; } = "";
        public string userId { get; set; } = "";
        public string? sessionId { get; set; }
        public string? name { get; set; }
    }
}
