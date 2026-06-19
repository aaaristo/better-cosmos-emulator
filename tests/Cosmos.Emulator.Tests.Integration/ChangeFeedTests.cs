using Microsoft.Azure.Cosmos;
using Shouldly;

namespace Cosmos.Emulator.Tests.Integration;

[Collection("Emulator")]
public class ChangeFeedTests
{
    private readonly CosmosClient _client;

    public ChangeFeedTests(EmulatorFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task LatestVersionChangeFeed_ShouldReturnChanges()
    {
        var container = await CreateTempContainer();

        // Insert some items
        await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk1", name = "Item1" },
            new PartitionKey("pk1"));
        await container.CreateItemAsync(
            new { id = "2", partitionKey = "pk1", name = "Item2" },
            new PartitionKey("pk1"));

        // Read change feed from beginning
        var feedIterator = container.GetChangeFeedIterator<dynamic>(
            ChangeFeedStartFrom.Beginning(),
            ChangeFeedMode.LatestVersion);

        var allChanges = new List<dynamic>();
        while (feedIterator.HasMoreResults)
        {
            var response = await feedIterator.ReadNextAsync();
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                break;
            allChanges.AddRange(response);
        }

        allChanges.Count.ShouldBe(2);
    }

    [Fact]
    public async Task LatestVersionChangeFeed_ShouldReturnNotModifiedWhenCaughtUp()
    {
        var container = await CreateTempContainer();

        await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk1", name = "Item1" },
            new PartitionKey("pk1"));

        var feedIterator = container.GetChangeFeedIterator<dynamic>(
            ChangeFeedStartFrom.Beginning(),
            ChangeFeedMode.LatestVersion);

        // Read first batch
        var firstBatch = await feedIterator.ReadNextAsync();
        firstBatch.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        firstBatch.Count.ShouldBe(1);

        // Reading again should return 304
        var secondBatch = await feedIterator.ReadNextAsync();
        secondBatch.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task LatestVersionChangeFeed_ShouldPickUpNewChanges()
    {
        var container = await CreateTempContainer();

        await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk1", name = "Initial" },
            new PartitionKey("pk1"));

        var feedIterator = container.GetChangeFeedIterator<dynamic>(
            ChangeFeedStartFrom.Beginning(),
            ChangeFeedMode.LatestVersion);

        // Drain initial changes
        string? continuationToken = null;
        while (feedIterator.HasMoreResults)
        {
            var response = await feedIterator.ReadNextAsync();
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                continuationToken = response.ContinuationToken;
                break;
            }
        }

        // Add more items
        await container.CreateItemAsync(
            new { id = "2", partitionKey = "pk1", name = "New" },
            new PartitionKey("pk1"));

        // Resume from continuation
        var resumedIterator = container.GetChangeFeedIterator<dynamic>(
            ChangeFeedStartFrom.ContinuationToken(continuationToken!),
            ChangeFeedMode.LatestVersion);

        var newChanges = new List<dynamic>();
        while (resumedIterator.HasMoreResults)
        {
            var response = await resumedIterator.ReadNextAsync();
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                break;
            newChanges.AddRange(response);
        }

        newChanges.Count.ShouldBe(1);
        ((string)newChanges[0].name).ShouldBe("New");
    }

    [Fact(Timeout = 15000)]
    public async Task AllVersionsAndDeletesChangeFeed_ShouldCaptureDeletes()
    {
        var container = await CreateTempContainer();

        // Create, then delete an item
        await container.CreateItemAsync(
            new { id = "1", partitionKey = "pk1", name = "ToDelete" },
            new PartitionKey("pk1"));
        await container.DeleteItemAsync<dynamic>("1", new PartitionKey("pk1"));

        // Read change feed in AllVersionsAndDeletes mode
        var feedIterator = container.GetChangeFeedIterator<dynamic>(
            ChangeFeedStartFrom.Beginning(),
            ChangeFeedMode.AllVersionsAndDeletes);

        var allChanges = new List<dynamic>();
        while (feedIterator.HasMoreResults)
        {
            var response = await feedIterator.ReadNextAsync();
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                break;
            allChanges.AddRange(response);
        }

        // Should have at least create + delete
        allChanges.Count.ShouldBeGreaterThanOrEqualTo(2);

        // The delete event must carry the deleted document's id at the item level,
        // since it has no body for consumers to read the id from.
        var deleteEvent = allChanges.FirstOrDefault(
            c => (string?)c.metadata?.operationType == "delete");
        ((object?)deleteEvent).ShouldNotBeNull();
        ((string)deleteEvent.id).ShouldBe("1");
    }

    [Fact]
    public async Task LatestVersionChangeFeed_FullIterationPattern_ShouldWork()
    {
        // Simulates CosmosChangeNotifierFunction pattern:
        // 1. Pre-existing data in container (50 docs)
        // 2. Start change feed from Beginning with small page size (5)
        // 3. Iterate through ALL historical entries across many pages until 304
        // 4. New change arrives
        // 5. Resume iteration — should pick up the new change
        var container = await CreateTempContainer();

        // Pre-existing data (simulates historical documents)
        const int historicalCount = 50;
        for (int i = 0; i < historicalCount; i++)
        {
            await container.CreateItemAsync(
                new { id = $"historical-{i:D3}", partitionKey = "pk1", name = $"Old-{i}" },
                new PartitionKey("pk1"));
        }

        // Start from Beginning with small page size to force multiple iterations
        var feedIterator = container.GetChangeFeedIterator<dynamic>(
            ChangeFeedStartFrom.Beginning(),
            ChangeFeedMode.LatestVersion,
            new ChangeFeedRequestOptions { PageSizeHint = 5 });

        var historicalChanges = new List<dynamic>();
        string? continuationToken = null;
        int pageCount = 0;
        while (feedIterator.HasMoreResults)
        {
            var response = await feedIterator.ReadNextAsync();
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                continuationToken = response.ContinuationToken;
                break;
            }
            historicalChanges.AddRange(response);
            continuationToken = response.ContinuationToken;
            pageCount++;
        }

        historicalChanges.Count.ShouldBe(historicalCount);
        pageCount.ShouldBeGreaterThan(1); // must have paginated
        continuationToken.ShouldNotBeNull();

        // Now add a new document (simulates a dispatch)
        await container.CreateItemAsync(
            new { id = "new-change", partitionKey = "pk1", name = "NewItem", Repo = "Chain" },
            new PartitionKey("pk1"));

        // Resume from continuation — should pick up the new change immediately
        var resumedIterator = container.GetChangeFeedIterator<dynamic>(
            ChangeFeedStartFrom.ContinuationToken(continuationToken),
            ChangeFeedMode.LatestVersion,
            new ChangeFeedRequestOptions { PageSizeHint = 5 });

        var newChanges = new List<dynamic>();
        while (resumedIterator.HasMoreResults)
        {
            var response = await resumedIterator.ReadNextAsync();
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                break;
            newChanges.AddRange(response);
        }

        newChanges.Count.ShouldBe(1);
        ((string)newChanges[0].id).ShouldBe("new-change");
    }

    [Fact]
    public async Task LatestVersionChangeFeed_MultipleUpdates_ShouldReturnLatestState()
    {
        // Verify that LatestVersion returns the CURRENT state, not intermediate versions
        var container = await CreateTempContainer();

        await container.CreateItemAsync(
            new { id = "item-1", partitionKey = "pk1", name = "v1" },
            new PartitionKey("pk1"));

        // Update the same item multiple times
        await container.ReplaceItemAsync(
            new { id = "item-1", partitionKey = "pk1", name = "v2" },
            "item-1", new PartitionKey("pk1"));
        await container.ReplaceItemAsync(
            new { id = "item-1", partitionKey = "pk1", name = "v3" },
            "item-1", new PartitionKey("pk1"));

        // Change feed should return the latest version
        var feedIterator = container.GetChangeFeedIterator<dynamic>(
            ChangeFeedStartFrom.Beginning(),
            ChangeFeedMode.LatestVersion);

        var changes = new List<dynamic>();
        while (feedIterator.HasMoreResults)
        {
            var response = await feedIterator.ReadNextAsync();
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                break;
            changes.AddRange(response);
        }

        // LatestVersion should return 1 document with latest state
        changes.Count.ShouldBe(1);
        ((string)changes[0].name).ShouldBe("v3");
    }

    [Fact]
    public async Task LatestVersionChangeFeed_NewPartitionAfterDrain_ShouldBeVisible()
    {
        // Production scenario: CosmosChangeNotifierFunction drains change feed for
        // partition "Chain", then a write happens in partition "AMA001". The next
        // change feed poll must see the AMA001 change.
        var dbName = $"test-db-{Guid.NewGuid():N}";
        var db = (await _client.CreateDatabaseAsync(dbName)).Database;
        var container = (await db.CreateContainerAsync($"test-coll-{Guid.NewGuid():N}", "/Repo")).Container;

        // Write to partition "Chain"
        await container.CreateItemAsync(
            new { id = "sub-1", Repo = "Chain", Path = "/subscription/1" },
            new PartitionKey("Chain"));
        await container.CreateItemAsync(
            new { id = "sub-2", Repo = "Chain", Path = "/subscription/2" },
            new PartitionKey("Chain"));

        // Drain all changes
        var feedIterator = container.GetChangeFeedIterator<dynamic>(
            ChangeFeedStartFrom.Beginning(),
            ChangeFeedMode.LatestVersion,
            new ChangeFeedRequestOptions { PageSizeHint = 5 });

        string? continuationToken = null;
        var drained = new List<dynamic>();
        while (feedIterator.HasMoreResults)
        {
            var response = await feedIterator.ReadNextAsync();
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                continuationToken = response.ContinuationToken;
                break;
            }
            drained.AddRange(response);
            continuationToken = response.ContinuationToken;
        }

        drained.Count.ShouldBe(2);

        // Now write to a DIFFERENT partition "AMA001"
        await container.CreateItemAsync(
            new { id = "obj-1", Repo = "AMA001", Path = "/data/item-1", Hash = "abc123" },
            new PartitionKey("AMA001"));

        // Resume change feed — must see the AMA001 change
        var resumedIterator = container.GetChangeFeedIterator<dynamic>(
            ChangeFeedStartFrom.ContinuationToken(continuationToken!),
            ChangeFeedMode.LatestVersion,
            new ChangeFeedRequestOptions { PageSizeHint = 5 });

        var newChanges = new List<dynamic>();
        while (resumedIterator.HasMoreResults)
        {
            var response = await resumedIterator.ReadNextAsync();
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                break;
            newChanges.AddRange(response);
        }

        newChanges.Count.ShouldBe(1);
        ((string)newChanges[0].Repo).ShouldBe("AMA001");
        ((string)newChanges[0].Path).ShouldBe("/data/item-1");
    }

    [Fact(Timeout = 30000)]
    public async Task LatestVersionChangeFeed_ConcurrentWithWrites_ShouldNotHang()
    {
        var container = await CreateTempContainer();

        // Seed some initial data
        for (int i = 0; i < 10; i++)
            await container.CreateItemAsync(
                new { id = $"seed-{i}", partitionKey = "pk1", name = $"Seed-{i}" },
                new PartitionKey("pk1"));

        // Start 20 parallel writes
        var writeCts = new CancellationTokenSource();
        var writeCount = 0;
        var writeTask = Task.Run(async () =>
        {
            var idx = 0;
            while (!writeCts.Token.IsCancellationRequested)
            {
                try
                {
                    await container.UpsertItemAsync(
                        new { id = $"concurrent-{idx % 50}", partitionKey = "pk1", name = $"Write-{idx}" },
                        new PartitionKey("pk1"));
                    Interlocked.Increment(ref writeCount);
                    idx++;
                }
                catch { }
            }
        });

        // Simultaneously drain change feed — this must not hang
        var feedIterator = container.GetChangeFeedIterator<dynamic>(
            ChangeFeedStartFrom.Beginning(),
            ChangeFeedMode.LatestVersion,
            new ChangeFeedRequestOptions { PageSizeHint = 5 });

        var feedCount = 0;
        var pages = 0;
        while (feedIterator.HasMoreResults)
        {
            var response = await feedIterator.ReadNextAsync();
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                break;
            feedCount += response.Count;
            pages++;
        }

        writeCts.Cancel();
        await writeTask;

        feedCount.ShouldBeGreaterThanOrEqualTo(10); // at least the seeds
        pages.ShouldBeGreaterThan(1);
        writeCount.ShouldBeGreaterThan(0);
    }

    private async Task<Container> CreateTempContainer()
    {
        var dbName = $"test-db-{Guid.NewGuid():N}";
        var db = (await _client.CreateDatabaseAsync(dbName)).Database;
        return (await db.CreateContainerAsync($"test-coll-{Guid.NewGuid():N}", "/partitionKey")).Container;
    }
}
