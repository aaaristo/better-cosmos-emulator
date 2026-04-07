# Better Cosmos Emulator

A lightweight, SQLite-backed Azure Cosmos DB emulator for local development and testing. Drop-in compatible with the official `Microsoft.Azure.Cosmos` .NET SDK via Gateway mode.

## Why?

The official Cosmos DB emulator is heavy, Windows-only (Linux Docker image is limited), and lacks features like the AllVersionsAndDeletes change feed mode. This emulator is:

- **Fast** — starts in milliseconds, runs 90 tests in under 5 seconds
- **Lightweight** — single .NET process, SQLite storage, no Docker required
- **Feature-rich** — indexing, change feed (both modes), PATCH, JOIN, TTL, full SQL query engine
- **SDK-compatible** — tested against the official `Microsoft.Azure.Cosmos` SDK and EF Core Cosmos provider

## Quick Start

```bash
dotnet run --project src/Cosmos.Emulator.Api
```

With custom data directory and port:

```bash
dotnet run --project src/Cosmos.Emulator.Api -- --data /path/to/data --port 9999
```

### Docker

```bash
docker build -t better-cosmos-emulator .
docker run -p 8081:8081 -v cosmos-data:/data better-cosmos-emulator
```

Connect with the standard emulator connection string:

```csharp
var client = new CosmosClient(
    "https://localhost:8081",
    "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==",
    new CosmosClientOptions
    {
        ConnectionMode = ConnectionMode.Gateway,
        LimitToEndpoint = true,
        HttpClientFactory = () => new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        })
    });
```

Or use a connection string:

```
AccountEndpoint=https://localhost:8081;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==
```

> **Note:** `LimitToEndpoint = true` is required. The self-signed certificate requires disabling certificate validation.

## Features

### CRUD Operations
- Databases: create, list, get, delete
- Containers: create, list, get, replace, delete
- Documents: create, read, replace, upsert, delete
- Patch: Add, Set, Replace, Remove, Increment (nested paths supported)
- ETag-based optimistic concurrency (If-Match)
- Same document ID across different partition keys

### SQL Queries
Full Cosmos DB SQL query support:

```sql
SELECT c.name, c.age FROM c WHERE c.age > 25 ORDER BY c.age DESC
SELECT VALUE COUNT(1) FROM c
SELECT c.city, COUNT(1) AS cnt FROM c GROUP BY c.city
SELECT DISTINCT VALUE c.city FROM c
SELECT * FROM c WHERE c.name IN ('Alice', 'Bob')
SELECT * FROM c WHERE CONTAINS(c.name, 'li')
SELECT t FROM c JOIN t IN c.tags WHERE t.key = 'admin'
SELECT {"name": c.name, "score": c.age * 2} FROM c
SELECT VALUE c.nickname ?? c.name FROM c
```

Supported: SELECT, FROM, WHERE, ORDER BY, TOP, OFFSET/LIMIT (parameterized), DISTINCT, GROUP BY, JOIN, VALUE, AS, aggregates (COUNT, SUM, AVG, MIN, MAX), IN, BETWEEN, CONTAINS, STARTSWITH, ENDSWITH, IS_DEFINED, IS_NULL, ARRAY_CONTAINS, UPPER, LOWER, LENGTH, CONCAT, SUBSTRING, REPLACE, ABS, FLOOR, CEILING, ROUND, coalesce (??), `= null`/`!= null`, JSON object/array literals, parameterized queries, bracket notation (`c["prop"]`), `FROM root [AS] alias`.

### Change Feed
- **LatestVersion** mode — track inserts and updates
- **AllVersionsAndDeletes** mode — track all changes including deletes
- Continuation tokens for resumable iteration
- Per-partition key filtering

### Indexing
- Indexing policy storage and round-trip
- Composite indexes: pre-creates SQLite columns and indexes on container create/replace
- Queries use SQLite column indexes for efficient filtering

### Other
- **TTL** — automatic document expiration with container `defaultTtl` and per-document `ttl` override
- **Pagination** — `x-ms-max-item-count` and `x-ms-continuation` for all list/query endpoints
- **HTTPS** — self-signed certificate on port 8081 (configurable)
- **Partition keys** — full partition key support including hierarchical

## Architecture

```
Database  →  SQLite file (data/mydb.db)
Container →  Table within that file ([orders], [orders__cf])
Document  →  Row with hybrid columns:
              - Scalar properties → SQLite columns (indexed)
              - Nested scalars → flattened columns (address.city → [address__city])
              - Arrays → stay in body JSON column (use json_extract at query time)
```

- **Catalog**: `data/_catalog.db` for database metadata
- **Query engine**: hand-rolled lexer → recursive descent parser → AST → SQLite SQL translator
- **Change feed**: per-container `__cf` table with monotonic LSN tracking

## Configuration

`appsettings.json`:

```json
{
  "CosmosEmulator": {
    "MasterKey": "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==",
    "DataPath": "data",
    "Port": 8081
  }
}
```

## Testing

```bash
dotnet test tests/Cosmos.Emulator.Tests.Integration
```

90 integration tests covering all features using the official `Microsoft.Azure.Cosmos` SDK and EF Core Cosmos provider (`IsETagConcurrency`, bracket notation, `= null` syntax).

## Benchmarks

Compare performance against the official Cosmos DB emulators:

```bash
# Start the emulators (better-cosmos-emulator on 8081, official on 8082, vnext on 8083)
dotnet run --project src/Cosmos.Emulator.Api
docker run -d -p 8082:8081 mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:latest
docker run -d -p 8083:8081 mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview

# Run benchmark against all reachable endpoints
dotnet run --project benchmarks/Cosmos.Emulator.Benchmarks.csproj

# Run against specific endpoints
dotnet run --project benchmarks/Cosmos.Emulator.Benchmarks.csproj -- \
  --endpoint "Better=https://localhost:8081" \
  --endpoint "Official=https://localhost:8082"

# Run against a single target
dotnet run --project benchmarks/Cosmos.Emulator.Benchmarks.csproj -- --only "Better"
```

Measures: database/container creation, 100-doc insert, point read, upsert, queries (filter, ORDER BY, COUNT, GROUP BY), change feed drain, and 100-doc delete.

### Results (Docker, Windows 11, same host)

| Operation | Better Emulator | VNext Preview | Official Emulator |
|---|--:|--:|--:|
| Create database | 140 ms | 103 ms | 882 ms |
| Create container | 65 ms | 59 ms | 701 ms |
| Insert 100 docs | **668 ms** | 613 ms | 1363 ms |
| Point read | **2.2 ms** | 3.5 ms | 4.8 ms |
| Upsert | **4.5 ms** | 5.7 ms | 12.0 ms |
| Query: WHERE | **7.5 ms** | 9.8 ms | 11.2 ms |
| Query: ORDER BY + LIMIT | **4.4 ms** | 6.0 ms | 8.8 ms |
| Query: COUNT | **2.5 ms** | 6.0 ms | 6.3 ms |
| Query: GROUP BY | **3.1 ms** | 5.7 ms | 6.5 ms |
| Change feed drain | 49 ms | **48 ms** | 70 ms |
| Delete 100 docs | 413 ms | 511 ms | 1141 ms |
| **TOTAL** | **1360 ms** | 1370 ms | 4206 ms |

### At scale (10,000 docs, parallelism=50)

| Operation | Better Emulator | VNext Preview | Official Emulator |
|---|--:|--:|--:|
| Insert 10k docs | 27.8 s | **16.7 s** | 20.6 s |
| Point read | **2.9 ms** | 2.7 ms | 4.7 ms |
| Upsert | **4.7 ms** | 5.2 ms | 9.8 ms |
| Query: WHERE (5k hits) | 296 ms | 386 ms | **291 ms** |
| Query: ORDER BY + LIMIT 10 | 24 ms | 22 ms | **6.9 ms** |
| Query: COUNT | **3.9 ms** | 17 ms | 5.9 ms |
| Query: GROUP BY | **5.9 ms** | 21 ms | 6.1 ms |
| Change feed drain 10k | **1.1 s** | 2.7 s | 2.1 s |
| Delete 10k docs | 29.8 s | **11.5 s** | 20.2 s |
| **TOTAL** | 59.2 s | **31.5 s** | 44.7 s |

At 100 docs the better emulator is **3x faster** than the official emulator and tied with VNext. At 10k docs, VNext pulls ahead on bulk writes/deletes (Rust gateway), but the better emulator has the fastest change feed drain and aggregate queries. The official emulator is slowest overall at both scales.

SQLite's single-writer lock is the bottleneck for parallel inserts/deletes — sequential writes are fast but don't benefit from parallelism as much as the other emulators.

## Limitations

- **Auth** — accepts any valid-looking Authorization header (no HMAC-SHA256 validation)
- **Indexing enforcement** — stores indexing policies but doesn't reject queries on excluded paths
- **Single partition** — all data in one partition key range per container (no physical partitioning)
- **No stored procedures, triggers, or UDFs**
- **No geo-replication or consistency levels** (always session consistency)

## License

Apache-2.0
