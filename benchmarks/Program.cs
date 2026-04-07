using System.Diagnostics;
using System.Net;
using Microsoft.Azure.Cosmos;
using Spectre.Console;
using Spectre.Console.Rendering;

const string MasterKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

var endpoints = new Dictionary<string, string>
{
    ["Better Emulator"] = "https://localhost:8081",
    ["Official Emulator"] = "https://localhost:8082",
    ["VNext Emulator"] = "https://localhost:8083",
};

// Parse CLI args: --endpoint "Name=url" or --only "Name" or --count N or --parallelism N
var customEndpoints = new Dictionary<string, string>();
string? onlyTarget = null;
int docCount = 100;
int parallelism = 20;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--endpoint" && i + 1 < args.Length)
    {
        var parts = args[++i].Split('=', 2);
        if (parts.Length == 2) customEndpoints[parts[0]] = parts[1];
    }
    else if (args[i] == "--only" && i + 1 < args.Length)
    {
        onlyTarget = args[++i];
    }
    else if (args[i] == "--count" && i + 1 < args.Length)
    {
        docCount = int.Parse(args[++i]);
    }
    else if (args[i] == "--parallelism" && i + 1 < args.Length)
    {
        parallelism = int.Parse(args[++i]);
    }
}

if (customEndpoints.Count > 0) endpoints = customEndpoints;

AnsiConsole.MarkupLine($"[bold blue]Cosmos DB Emulator Benchmark[/] ({docCount} docs, parallelism={parallelism})");
AnsiConsole.WriteLine();

var results = new List<BenchmarkResult>();

foreach (var (name, endpoint) in endpoints)
{
    if (onlyTarget is not null && !name.Contains(onlyTarget, StringComparison.OrdinalIgnoreCase))
        continue;

    AnsiConsole.MarkupLine($"[bold yellow]Testing: {name}[/] ({endpoint})");

    if (!await IsReachable(endpoint))
    {
        AnsiConsole.MarkupLine($"  [red]Not reachable — skipping[/]");
        AnsiConsole.WriteLine();
        continue;
    }

    var client = CreateClient(endpoint);
    var result = new BenchmarkResult { Name = name };

    try
    {
        await RunBenchmarks(client, result);
        results.Add(result);
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"  [red]Error: {Markup.Escape(ex.Message)}[/]");
    }
    finally
    {
        client.Dispose();
    }

    AnsiConsole.WriteLine();
}

PrintResults(results);

// --- Benchmark methods ---

async Task RunBenchmarks(CosmosClient client, BenchmarkResult result)
{
    var dbName = $"bench-{Guid.NewGuid():N}"[..20];

    // 1. Create database
    result.CreateDatabase = await TimeSafe("Create database", async () =>
    {
        await client.CreateDatabaseAsync(dbName);
    });

    var db = client.GetDatabase(dbName);

    // 2. Create container
    result.CreateContainer = await TimeSafe("Create container", async () =>
    {
        await db.CreateContainerAsync("bench-items", "/pk");
    });

    var container = db.GetContainer("bench-items");

    // 3. Insert documents (parallel)
    result.Insert100 = await TimeSafe($"Insert {docCount} docs (p={parallelism})", async () =>
    {
        var semaphore = new SemaphoreSlim(parallelism);
        var tasks = new List<Task>();
        for (int i = 0; i < docCount; i++)
        {
            var idx = i;
            await semaphore.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await container.CreateItemAsync(new
                    {
                        id = $"item-{idx:D6}",
                        pk = "partition-1",
                        name = $"Document {idx}",
                        amount = idx * 1.5,
                        tags = new[] { "bench", $"group-{idx % 10}" },
                        nested = new { city = idx % 2 == 0 ? "Zurich" : "Bern", code = idx }
                    }, new PartitionKey("partition-1"));
                }
                finally { semaphore.Release(); }
            }));
        }
        await Task.WhenAll(tasks);
    });

    // 4. Point read
    result.PointRead = await TimeAvgSafe("Point read (avg 10)", 10, async () =>
    {
        await container.ReadItemAsync<dynamic>($"item-{docCount / 2:D6}", new PartitionKey("partition-1"));
    });

    // 5. Upsert
    result.Upsert = await TimeAvgSafe("Upsert (avg 10)", 10, async () =>
    {
        await container.UpsertItemAsync(new
        {
            id = "item-000001",
            pk = "partition-1",
            name = "Updated",
            amount = 999.0,
            tags = new[] { "updated" },
            nested = new { city = "Geneva", code = 1 }
        }, new PartitionKey("partition-1"));
    });

    // 6. Query: SELECT * with filter
    result.QueryFilter = await TimeAvgSafe("Query: WHERE filter (avg 5)", 5, async () =>
    {
        var q = container.GetItemQueryIterator<dynamic>(
            "SELECT * FROM c WHERE c.amount > 50");
        while (q.HasMoreResults) await q.ReadNextAsync();
    });

    // 7. Query: ORDER BY with OFFSET/LIMIT
    result.QueryOrderByLimit = await TimeAvgSafe("Query: ORDER BY + LIMIT (avg 5)", 5, async () =>
    {
        var q = container.GetItemQueryIterator<dynamic>(
            "SELECT * FROM c ORDER BY c.amount DESC OFFSET 0 LIMIT 10");
        while (q.HasMoreResults) await q.ReadNextAsync();
    });

    // 8. Query: aggregation
    result.QueryAggregate = await TimeAvgSafe("Query: COUNT aggregate (avg 5)", 5, async () =>
    {
        var q = container.GetItemQueryIterator<dynamic>(
            "SELECT VALUE COUNT(1) FROM c WHERE c.nested.city = 'Zurich'");
        while (q.HasMoreResults) await q.ReadNextAsync();
    });

    // 9. Query: GROUP BY
    result.QueryGroupBy = await TimeAvgSafe("Query: GROUP BY (avg 5)", 5, async () =>
    {
        var q = container.GetItemQueryIterator<dynamic>(
            "SELECT c.nested.city, COUNT(1) AS cnt FROM c GROUP BY c.nested.city");
        while (q.HasMoreResults) await q.ReadNextAsync();
    });

    // 10. Change feed: read all
    result.ChangeFeedDrain = await TimeSafe($"Change feed: drain {docCount} docs", async () =>
    {
        var feed = container.GetChangeFeedIterator<dynamic>(
            ChangeFeedStartFrom.Beginning(),
            ChangeFeedMode.LatestVersion,
            new ChangeFeedRequestOptions { PageSizeHint = 25 });
        while (feed.HasMoreResults)
        {
            var resp = await feed.ReadNextAsync();
            if (resp.StatusCode == HttpStatusCode.NotModified) break;
        }
    });

    // 11. Delete documents (parallel)
    result.Delete100 = await TimeSafe($"Delete {docCount} docs (p={parallelism})", async () =>
    {
        var semaphore = new SemaphoreSlim(parallelism);
        var tasks = new List<Task>();
        for (int i = 0; i < docCount; i++)
        {
            var idx = i;
            await semaphore.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await container.DeleteItemAsync<dynamic>($"item-{idx:D6}", new PartitionKey("partition-1"));
                }
                finally { semaphore.Release(); }
            }));
        }
        await Task.WhenAll(tasks);
    });

    // Cleanup
    await db.DeleteAsync();
}

// --- Helpers ---

CosmosClient CreateClient(string endpoint) => new(
    endpoint, MasterKey,
    new CosmosClientOptions
    {
        ConnectionMode = ConnectionMode.Gateway,
        LimitToEndpoint = true,
        HttpClientFactory = () => new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        })
    });

async Task<bool> IsReachable(string endpoint)
{
    try
    {
        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        });
        http.Timeout = TimeSpan.FromSeconds(3);
        var resp = await http.GetAsync(endpoint);
        return true;
    }
    catch { return false; }
}

async Task<double> Time(Func<Task> action)
{
    var sw = Stopwatch.StartNew();
    await action();
    return sw.Elapsed.TotalMilliseconds;
}

async Task<double> TimeAvg(int iterations, Func<Task> action)
{
    // Warmup
    await action();

    var sw = Stopwatch.StartNew();
    for (int i = 0; i < iterations; i++)
        await action();
    return sw.Elapsed.TotalMilliseconds / iterations;
}

async Task<double> TimeSafe(string label, Func<Task> action)
{
    try
    {
        var ms = await Time(action);
        Print(label, ms);
        return ms;
    }
    catch (Exception ex)
    {
        var msg = ex.ToString();
        AnsiConsole.MarkupLine($"  [red]  FAILED[/]  {label}");
        AnsiConsole.WriteLine($"         {msg[..Math.Min(msg.Length, 300)]}");
        return -1;
    }
}

async Task<double> TimeAvgSafe(string label, int iterations, Func<Task> action)
{
    try
    {
        var ms = await TimeAvg(iterations, action);
        Print(label, ms);
        return ms;
    }
    catch (Exception ex)
    {
        var msg = ex.ToString();
        AnsiConsole.MarkupLine($"  [red]  FAILED[/]  {label}");
        AnsiConsole.WriteLine($"         {msg[..Math.Min(msg.Length, 300)]}");
        return -1;
    }
}

void Print(string label, double ms)
{
    var color = ms < 50 ? "green" : ms < 200 ? "yellow" : "red";
    AnsiConsole.MarkupLine($"  [{color}]{ms,8:F1} ms[/]  {label}");
}

void PrintResults(List<BenchmarkResult> results)
{
    if (results.Count == 0) return;

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[bold blue]Summary[/]");

    var table = new Table();
    table.AddColumn("Operation");
    foreach (var r in results)
        table.AddColumn(new TableColumn(r.Name).Centered());

    AddRow(table, "Create database", results, r => r.CreateDatabase);
    AddRow(table, "Create container", results, r => r.CreateContainer);
    AddRow(table, $"Insert {docCount} docs", results, r => r.Insert100);
    AddRow(table, "Point read", results, r => r.PointRead);
    AddRow(table, "Upsert", results, r => r.Upsert);
    AddRow(table, "Query: WHERE", results, r => r.QueryFilter);
    AddRow(table, "Query: ORDER BY + LIMIT", results, r => r.QueryOrderByLimit);
    AddRow(table, "Query: COUNT", results, r => r.QueryAggregate);
    AddRow(table, "Query: GROUP BY", results, r => r.QueryGroupBy);
    AddRow(table, "Change feed drain", results, r => r.ChangeFeedDrain);
    AddRow(table, $"Delete {docCount} docs", results, r => r.Delete100);

    // Total row
    var totals = results.Select(r =>
        r.CreateDatabase + r.CreateContainer + r.Insert100 + r.PointRead +
        r.Upsert + r.QueryFilter + r.QueryOrderByLimit + r.QueryAggregate +
        r.QueryGroupBy + r.ChangeFeedDrain + r.Delete100).ToList();
    var minTotal = totals.Min();
    table.AddRow(
        new[] { new Text("TOTAL", new Style(decoration: Decoration.Bold)) }
        .Concat(totals.Select(t =>
        {
            var color = t == minTotal ? Color.Green : Color.Default;
            return new Text($"{t:F0} ms", new Style(foreground: color, decoration: Decoration.Bold));
        }))
        .ToArray<IRenderable>());

    AnsiConsole.Write(table);
}

void AddRow(Table table, string label, List<BenchmarkResult> results, Func<BenchmarkResult, double> getValue)
{
    var values = results.Select(getValue).ToList();
    var min = values.Min();
    table.AddRow(
        new[] { new Text(label) }
        .Concat(values.Select(v =>
        {
            var color = v == min ? Color.Green : Color.Default;
            return new Text($"{v:F1} ms", new Style(foreground: color));
        }))
        .ToArray<IRenderable>());
}

class BenchmarkResult
{
    public string Name { get; set; } = "";
    public double CreateDatabase { get; set; }
    public double CreateContainer { get; set; }
    public double Insert100 { get; set; }
    public double PointRead { get; set; }
    public double Upsert { get; set; }
    public double QueryFilter { get; set; }
    public double QueryOrderByLimit { get; set; }
    public double QueryAggregate { get; set; }
    public double QueryGroupBy { get; set; }
    public double ChangeFeedDrain { get; set; }
    public double Delete100 { get; set; }
}
