# Better Cosmos Emulator

A lightweight, SQLite-backed Azure Cosmos DB emulator for local development and testing. Drop-in compatible with the official `Microsoft.Azure.Cosmos` .NET SDK via Gateway mode.

## Why?

The official Cosmos DB emulator is heavy, Windows-only (Linux Docker image is limited), and lacks features like the AllVersionsAndDeletes change feed mode. This emulator is:

- **Fast** — starts in milliseconds, runs 74 tests in under 5 seconds
- **Lightweight** — single .NET process, SQLite storage, no Docker required
- **Feature-rich** — indexing, change feed (both modes), PATCH, JOIN, TTL, full SQL query engine
- **SDK-compatible** — tested against the official `Microsoft.Azure.Cosmos` SDK

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

Supported: SELECT, FROM, WHERE, ORDER BY, TOP, OFFSET/LIMIT, DISTINCT, GROUP BY, JOIN, VALUE, AS, aggregates (COUNT, SUM, AVG, MIN, MAX), IN, BETWEEN, CONTAINS, STARTSWITH, ENDSWITH, IS_DEFINED, IS_NULL, ARRAY_CONTAINS, UPPER, LOWER, LENGTH, CONCAT, SUBSTRING, REPLACE, ABS, FLOOR, CEILING, ROUND, coalesce (??), JSON object/array literals, parameterized queries.

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

74 integration tests covering all features using the official `Microsoft.Azure.Cosmos` SDK.

## Limitations

- **Auth** — accepts any valid-looking Authorization header (no HMAC-SHA256 validation)
- **Indexing enforcement** — stores indexing policies but doesn't reject queries on excluded paths
- **Single partition** — all data in one partition key range per container (no physical partitioning)
- **No stored procedures, triggers, or UDFs**
- **No geo-replication or consistency levels** (always session consistency)

## License

MIT
