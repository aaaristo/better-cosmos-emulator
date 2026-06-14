using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Cosmos.Emulator.Tests.Integration;

/// <summary>
/// Regression tests for the cloud "SearchCreateCustomer" scenario:
///   ocids.Contains(c.Ocid.Value)  // client-side HashSet over a nested owned property
/// EF Core 10's Cosmos provider translates a parameterized-collection .Contains() over a
/// nested property into either "c[\"Ocid\"][\"Value\"] IN (@p)" or an
/// "EXISTS (SELECT VALUE 1 FROM o IN @p WHERE o = c[\"Ocid\"][\"Value\"])" subquery —
/// both of which previously failed to parse/translate.
/// </summary>
[Collection("Emulator")]
public class EfCoreContainsTests
{
    private readonly EmulatorFixture _fixture;

    public EfCoreContainsTests(EmulatorFixture fixture)
    {
        _fixture = fixture;
    }

    private DbContextOptions<CustomerContext> BuildOptions(string dbName)
    {
        var endpoint = _fixture.Client.Endpoint.ToString();
        return new DbContextOptionsBuilder<CustomerContext>()
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
    }

    [Fact]
    public async Task EfCore_ContainsOverNestedOwnedProperty_ShouldMatch()
    {
        var dbName = $"test-db-{Guid.NewGuid():N}";
        await _fixture.Client.CreateDatabaseAsync(dbName);
        var options = BuildOptions(dbName);

        var ocid1 = Guid.NewGuid();
        var ocid2 = Guid.NewGuid();
        var ocid3 = Guid.NewGuid();

        await using (var ctx = new CustomerContext(options))
        {
            await ctx.Database.EnsureCreatedAsync();
            ctx.Customers.AddRange(
                new Customer { Id = "c1", Chain = "chainA", Ocid = new Ocid { Value = ocid1 } },
                new Customer { Id = "c2", Chain = "chainA", Ocid = new Ocid { Value = ocid2 } },
                new Customer { Id = "c3", Chain = "chainA", Ocid = new Ocid { Value = ocid3 } });
            await ctx.SaveChangesAsync();
        }

        // The exact shape from SearchCreateCustomer.
        var ocids = new[] { ocid1, ocid3 }.ToHashSet();

        await using (var ctx = new CustomerContext(options))
        {
            var matches = await ctx.Customers
                .AsNoTracking()
                .Where(c => ocids.Contains(c.Ocid.Value))
                .Select(c => new { c.Id, Ocid = c.Ocid.Value, c.Chain })
                .ToListAsync();

            matches.Select(m => m.Id).OrderBy(x => x).ShouldBe(new[] { "c1", "c3" });
            matches.ShouldAllBe(m => ocids.Contains(m.Ocid));
            matches.ShouldAllBe(m => m.Chain == "chainA");
        }
    }

    [Fact]
    public async Task EfCore_ContainsOverNestedOwnedProperty_EmptySet_ShouldReturnNothing()
    {
        var dbName = $"test-db-{Guid.NewGuid():N}";
        await _fixture.Client.CreateDatabaseAsync(dbName);
        var options = BuildOptions(dbName);

        await using (var ctx = new CustomerContext(options))
        {
            await ctx.Database.EnsureCreatedAsync();
            ctx.Customers.Add(new Customer { Id = "c1", Chain = "chainA", Ocid = new Ocid { Value = Guid.NewGuid() } });
            await ctx.SaveChangesAsync();
        }

        var ocids = new HashSet<Guid>();

        await using (var ctx = new CustomerContext(options))
        {
            var matches = await ctx.Customers
                .AsNoTracking()
                .Where(c => ocids.Contains(c.Ocid.Value))
                .ToListAsync();

            matches.ShouldBeEmpty();
        }
    }
}

public class Customer
{
    public string Id { get; set; } = null!;
    public string Chain { get; set; } = null!;
    public Ocid Ocid { get; set; } = null!;
}

public class Ocid
{
    public Guid Value { get; set; }
}

public class CustomerContext : DbContext
{
    public CustomerContext(DbContextOptions<CustomerContext> options) : base(options) { }

    public DbSet<Customer> Customers => Set<Customer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Customer>();
        entity.ToContainer("Customers");
        entity.HasPartitionKey(c => c.Chain);
        entity.HasKey(c => c.Id);
        entity.OwnsOne(c => c.Ocid);
    }
}
