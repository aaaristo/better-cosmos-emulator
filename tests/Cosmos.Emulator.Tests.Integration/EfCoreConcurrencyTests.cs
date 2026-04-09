using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Cosmos.Emulator.Tests.Integration;

[Collection("Emulator")]
public class EfCoreConcurrencyTests
{
    private readonly EmulatorFixture _fixture;

    public EfCoreConcurrencyTests(EmulatorFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EfCore_IsETagConcurrency_ShouldWork()
    {
        var dbName = $"test-db-{Guid.NewGuid():N}";
        await _fixture.Client.CreateDatabaseAsync(dbName);

        var endpoint = _fixture.Client.Endpoint.ToString();
        var options = new DbContextOptionsBuilder<TestConcurrencyContext>()
            .UseCosmos(endpoint,
                "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==",
                dbName,
                cosmosOptions =>
                {
                    cosmosOptions.ConnectionMode(ConnectionMode.Gateway);
                    cosmosOptions.LimitToEndpoint();
                    cosmosOptions.HttpClientFactory(() => new HttpClient(new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
                    }));
                })
            .Options;

        // Create the container and seed a document
        await using (var ctx = new TestConcurrencyContext(options))
        {
            await ctx.Database.EnsureCreatedAsync();
            ctx.Nodes.Add(new TestNode { Id = "node-1", Repo = "test", Value = "original" });
            await ctx.SaveChangesAsync();
        }

        // Read the document in two separate contexts (simulating two concurrent readers)
        string etag1;
        await using (var ctx1 = new TestConcurrencyContext(options))
        {
            var node1 = await ctx1.Nodes.FirstAsync(n => n.Id == "node-1");
            node1.Value.ShouldBe("original");
            etag1 = node1.Etag!;
            etag1.ShouldNotBeNullOrEmpty();

            // Update from context 1
            node1.Value = "updated-by-ctx1";
            await ctx1.SaveChangesAsync();

            // Etag should have changed after save
            node1.Etag.ShouldNotBe(etag1);
        }

        // Now try to update from a "stale" context that has the old etag
        await using (var ctx2 = new TestConcurrencyContext(options))
        {
            var node2 = await ctx2.Nodes.FirstAsync(n => n.Id == "node-1");
            node2.Value.ShouldBe("updated-by-ctx1");

            // Manually set the etag to the stale value to simulate a concurrent edit
            ctx2.Entry(node2).Property(n => n.Etag).OriginalValue = etag1;

            node2.Value = "updated-by-ctx2-stale";

            // Should throw DbUpdateConcurrencyException (412 Precondition Failed)
            var ex = await Should.ThrowAsync<DbUpdateConcurrencyException>(
                () => ctx2.SaveChangesAsync());
            ex.ShouldNotBeNull();
        }

        // Verify the document still has the ctx1 value
        await using (var ctx3 = new TestConcurrencyContext(options))
        {
            var node3 = await ctx3.Nodes.FirstAsync(n => n.Id == "node-1");
            node3.Value.ShouldBe("updated-by-ctx1");
        }
    }

    [Fact]
    public async Task EfCore_IsETagConcurrency_SuccessfulUpdate_ShouldWork()
    {
        var dbName = $"test-db-{Guid.NewGuid():N}";
        await _fixture.Client.CreateDatabaseAsync(dbName);

        var endpoint = _fixture.Client.Endpoint.ToString();
        var options = new DbContextOptionsBuilder<TestConcurrencyContext>()
            .UseCosmos(endpoint,
                "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==",
                dbName,
                cosmosOptions =>
                {
                    cosmosOptions.ConnectionMode(ConnectionMode.Gateway);
                    cosmosOptions.LimitToEndpoint();
                    cosmosOptions.HttpClientFactory(() => new HttpClient(new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
                    }));
                })
            .Options;

        // Create
        await using (var ctx = new TestConcurrencyContext(options))
        {
            await ctx.Database.EnsureCreatedAsync();
            ctx.Nodes.Add(new TestNode { Id = "node-2", Repo = "test", Value = "v1" });
            await ctx.SaveChangesAsync();
        }

        // Read, update, save — should succeed with correct etag
        await using (var ctx = new TestConcurrencyContext(options))
        {
            var node = await ctx.Nodes.FirstAsync(n => n.Id == "node-2");
            node.Value = "v2";
            await ctx.SaveChangesAsync(); // should not throw

            // Update again in same context — etag should have been refreshed
            node.Value = "v3";
            await ctx.SaveChangesAsync(); // should not throw
        }

        // Verify final value
        await using (var ctx = new TestConcurrencyContext(options))
        {
            var node = await ctx.Nodes.FirstAsync(n => n.Id == "node-2");
            node.Value.ShouldBe("v3");
        }
    }

    [Fact(Timeout = 15000)]
    public async Task EfCore_TakeWithManyItems_ShouldNotLoopForever()
    {
        // Regression: EF Core's Take(1000) generates OFFSET 0 LIMIT @p.
        // The emulator was returning continuation tokens causing ToListAsync() to loop.
        var dbName = $"test-db-{Guid.NewGuid():N}";
        await _fixture.Client.CreateDatabaseAsync(dbName);

        var endpoint = _fixture.Client.Endpoint.ToString();
        var options = new DbContextOptionsBuilder<TestBatchContext>()
            .UseCosmos(endpoint,
                "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==",
                dbName,
                cosmosOptions =>
                {
                    cosmosOptions.ConnectionMode(ConnectionMode.Gateway);
                    cosmosOptions.LimitToEndpoint();
                    cosmosOptions.HttpClientFactory(() => new HttpClient(new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
                    }));
                })
            .Options;

        // Create container and seed 150 items
        await using (var ctx = new TestBatchContext(options))
        {
            await ctx.Database.EnsureCreatedAsync();
            for (int i = 0; i < 150; i++)
            {
                ctx.Items.Add(new TestBatchItem
                {
                    Id = $"item-{i:D4}",
                    Partition = "pk1",
                    Name = $"Item {i}"
                });
            }
            await ctx.SaveChangesAsync();
        }

        // This is the exact pattern from PromoteBatchActivity:
        // _batchContext.SyncObjects.Where(o => o.Repo == partition).Take(1000).ToListAsync()
        await using (var ctx = new TestBatchContext(options))
        {
            var items = await ctx.Items
                .Where(i => i.Partition == "pk1")
                .Take(1000)
                .ToListAsync();

            items.Count.ShouldBe(150);
        }
    }
}

public class TestBatchItem
{
    public string Id { get; set; } = null!;
    public string Partition { get; set; } = null!;
    public string Name { get; set; } = null!;
}

public class TestBatchContext : DbContext
{
    public TestBatchContext(DbContextOptions<TestBatchContext> options) : base(options) { }
    public DbSet<TestBatchItem> Items => Set<TestBatchItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<TestBatchItem>();
        entity.ToContainer("TestBatchItems");
        entity.HasPartitionKey(i => i.Partition);
        entity.HasKey(i => i.Id);
    }
}

public class TestNode
{
    public string Id { get; set; } = null!;
    public string Repo { get; set; } = null!;
    public string Value { get; set; } = null!;
    public string? Etag { get; set; }
}

public class TestConcurrencyContext : DbContext
{
    public TestConcurrencyContext(DbContextOptions<TestConcurrencyContext> options) : base(options) { }

    public DbSet<TestNode> Nodes => Set<TestNode>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<TestNode>();
        entity.ToContainer("TestNodes");
        entity.HasPartitionKey(n => n.Repo);
        entity.HasKey(n => n.Id);
        entity.Property(n => n.Etag).IsETagConcurrency();
    }
}
