# Better Cosmos Emulator

A lightweight Azure Cosmos DB emulator backed by SQLite, implementing the REST API so the official `Microsoft.Azure.Cosmos` .NET SDK can connect in Gateway mode.

## Build & Test

```bash
nuke Compile              # or: dotnet build
dotnet test tests/Cosmos.Emulator.Tests.Integration
```

## Running

```bash
dotnet run --project src/Cosmos.Emulator.Api
```

Starts on `https://localhost:8081` with self-signed cert. Connection string:
```
AccountEndpoint=https://localhost:8081;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==
```

Port configurable via `CosmosEmulator:Port` in appsettings.json.

## Architecture

- **Database = SQLite file** (`data/{dbname}.db`), **Container = table** within that file
- **Hybrid columns**: scalar properties (including nested) are flattened into SQLite columns (`address.city` → `[address__city]`). Arrays stay in the `body` JSON column and use `json_extract()` at query time.
- **Catalog**: `data/_catalog.db` tracks database-level metadata
- **Query engine**: hand-rolled lexer → parser → AST → SQLite SQL translator. Supports JSON object/array literals natively — `{}` → `json_object()`, `[]` → `json_array()`, with `json(body)` for sub-object embedding
- **SQLite indexes**: single-column indexes created per column; composite indexes created from container indexing policy

## Supported Features

### CRUD
- Databases: create, list, get, delete
- Containers: create, list, get, replace (with indexing policy updates), delete
- Documents: create, read, replace, upsert, delete, etag concurrency (If-Match)
- Patch: Add, Set, Replace, Remove, Increment — nested paths, multiple ops, etag precondition
- Same document ID in different partitions

### Queries
- SELECT *, projections, VALUE, DISTINCT
- WHERE with =, !=, <, >, <=, >=, AND, OR, NOT
- ORDER BY (ASC/DESC), TOP, OFFSET/LIMIT (including parameterized `@param`)
- GROUP BY with aggregates
- COUNT, SUM, AVG, MIN, MAX
- IN, BETWEEN
- CONTAINS, STARTSWITH, ENDSWITH, IS_DEFINED, IS_NULL, ARRAY_CONTAINS
- `= null` / `!= null` → translated to IS [NOT] NULL (Cosmos SQL syntactic sugar)
- String functions (UPPER, LOWER, CONCAT, SUBSTRING, REPLACE, etc.)
- Coalesce (??) operator
- JSON object literals: `SELECT {"name": c.name, "age": c.age} FROM c`
- JSON array literals: `SELECT [c.name, c.age] FROM c`
- JOIN (intra-document arrays): `SELECT t FROM c JOIN t IN c.tags`
- Subquery array iteration: `FROM x IN c.items` (also over a parameter array)
- EXISTS subqueries: `EXISTS (SELECT VALUE 1 FROM o IN c.items WHERE o.x = c.y)` — translated to a correlated `EXISTS (SELECT 1 FROM json_each(...) ...)`
- Nested bracket notation: `c["Owner"]["Name"]` → `$.Owner.Name` (chained EF Core bracket access)
- Array-valued IN parameters: `c.x IN (@arr)` where `@arr` is a JSON array — expanded to individual bound params
- Parameterized queries (@param)
- Cross-partition queries
- Bracket notation: `c["PropertyName"]` (EF Core Cosmos provider syntax)
- `FROM root c` / `FROM root AS c` syntax

### Change Feed
- LatestVersion mode (A-IM: Incremental Feed)
- AllVersionsAndDeletes mode (A-IM: Full-Fidelity Feed)
- Continuation tokens, 304 Not Modified when caught up

### Indexing
- Indexing policy storage and round-trip (includedPaths, excludedPaths, compositeIndexes)
- Composite indexes on container create/replace pre-create SQLite columns and indexes
- Single-column SQLite indexes for composite index paths

## Key SDK Compatibility Details

These are critical for the .NET Cosmos SDK to work correctly:

### Resource IDs (_rid)
The SDK validates `_rid` binary format strictly:
- Database: 4 bytes big-endian, base64 with padding (`AABxYQ==`)
- Collection: 8 bytes (4 db + 4 coll), byte[4] has 0x80 bit set
- Document: 16 bytes (4 db + 4 coll + 8 doc), byte[15] upper nibble = 0x0
- PKRange: 16 bytes (4 db + 4 coll + 8), byte[8] = 0x02, byte[15] = 0x50
- See `ResourceIdGenerator.cs` for exact format

### Partition Key Ranges (pkranges) — Change Feed Protocol
**The SDK reads pkranges via change feed**, not a simple GET:
- Sends `A-IM: Incremental Feed` header
- Loops until it gets `304 Not Modified`
- First call (no `If-None-Match` or `*`): return ranges with `etag` header
- Subsequent calls (with `If-None-Match: <etag>`): return `304` with NEW etag (same etag = stale cache loop)
- **If you always return 200, the SDK loops forever** — this was the main blocker

### SDK Query Rewriting
The SDK rewrites user queries before sending to the server, using JSON object/array literals:
- **ORDER BY**: `SELECT c._rid, [{"item": c.field}] AS orderByItems, c AS payload FROM c ORDER BY ...`
- **ORDER BY with projections**: `... {"displayName": (c.nickname ?? c.name)} AS payload ...`
- **GROUP BY**: `SELECT [{"item": c.city}] AS groupByItems, {"city": c.city, "cnt": {"item": COUNT(1)}} AS payload FROM c GROUP BY c.city`
- **Aggregates**: `SELECT VALUE [{"item": COUNT(1)}]` or `SELECT VALUE [{"item": {"sum": SUM(c.age), "count": COUNT(c.age)}}]`
- These are handled natively by the parser (ObjectExpression/ArrayExpression) — no regex rewriting needed
- `json(body)` wrapping in SQLite ensures full documents embed as sub-objects in `json_object()`, not strings

### Change Feed Modes
- LatestVersion: `A-IM: Incremental Feed`
- AllVersionsAndDeletes: `A-IM: Full-Fidelity Feed` (NOT `Incremental Feed`)

### _rid-based URL routing
The SDK uses `_rid` values in URL paths for internal calls (e.g., `/dbs/AABxYQ==/colls/AABxYYAAcWI=/pkranges`). The `-` in URLs represents `/` in base64 rids. Repositories resolve both user-facing IDs and _rid values.

### JSON serialization
- Use `PropertyNamingPolicy = null` to preserve mixed casing (`PartitionKeyRanges`, `_rid`, `_count`)
- Use `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` so `_etag` quotes serialize as `\"` not `\u0022`
- The SDK uses Newtonsoft.Json internally for response deserialization

### Session tokens
Format: `{pkRangeId}:-1#{lsn}` (e.g., `0:-1#1`). The old format `0:0` causes SDK parsing errors.

### Auth
Currently disabled (accepts any valid-looking Authorization header). Full HMAC-SHA256 validation can be added later — the tricky part is that the SDK uses _rid-based paths where base64 is case-sensitive while the spec says to lowercase.

### EF Core Cosmos Provider Compatibility
EF Core's Cosmos provider generates queries differently from direct SDK usage:
- **Bracket notation**: `c["PropertyName"]` instead of `c.PropertyName` — the lexer tokenizes `"PropertyName"` as a string literal, and the translator handles `ArrayIndexAccess` with string keys as property access
- **`FROM root AS c`**: EF Core uses the `root` keyword with explicit `AS` alias — the parser strips `root` and uses the alias
- **`= null` syntax**: EF Core generates `c.Deleted = null` instead of `IS_NULL(c.Deleted)` — the translator converts `= NULL` to `IS NULL` and `!= NULL` to `IS NOT NULL`
- **`IsETagConcurrency()`**: Maps the entity's `Etag` property to `_etag` in the document body, uses `If-Match` header on replace — fully supported
- **Parameterized OFFSET/LIMIT**: `OFFSET 0 LIMIT @p` — parameters resolved at translation time
- **`ARRAY_CONTAINS(@param, c["Path"])`**: Array parameter expanded to `IN (@p0, @p1, ...)` because SQLite's `json_each()` doesn't work with bound parameters
- **`SELECT VALUE c`**: Returns `body` directly (not `json_quote(body)`) to avoid wrapping objects as escaped strings
- **Parameterized `.Contains()` over a nested owned property** (e.g. `ocids.Contains(c.Ocid.Value)` where `ocids` is a client-side `HashSet`): EF Core 10 generates one of two shapes — `c["Ocid"]["Value"] IN (@p)` with the whole collection as a single array-valued parameter, or `EXISTS (SELECT VALUE 1 FROM o IN @p WHERE o = c["Ocid"]["Value"])`. Both are supported: the array parameter is expanded to individual bound params (SQLite can't bind an array to `IN`/`json_each`), nested brackets collapse to a dotted JSON path, and the EXISTS subquery becomes a correlated `EXISTS (SELECT 1 FROM json_each(...) ...)`.

### SQLite Quirks
- `json_each()` does NOT work with bound parameters — silently returns 0 rows. Array parameters must be expanded to individual values at translation time.
- `json_type(json_extract(body, path))` returns NULL (not `'null'`) for JSON null values because `json_extract` converts JSON null to SQL NULL. Use `json_type(body, path)` two-arg form instead.
- `column = NULL` is always false in SQLite — must use `IS NULL`. The translator auto-converts `= null` / `!= null` comparisons.
- Column names are case-insensitive — user properties like `ETag` collide with system column `etag`. The `FlattenObject` method skips properties matching system column names.
- `ALTER TABLE ADD COLUMN` fails on duplicate names — wrapped with try/catch as safety net.

### Query Plan Endpoint
Older SDKs (3.41.0 via EF Core) send `x-ms-cosmos-is-query-plan-request: True` before executing queries. The emulator returns a query plan that:
- For ORDER BY queries: rewrites the query to `orderByItems`/`payload` format so the SDK's `StreamingOrderByCrossPartitionQueryPipelineStage` works correctly
- For non-ORDER BY queries: returns a passthrough plan with the original query as `rewrittenQuery`
- Newer SDKs (3.46+) rewrite queries themselves without requesting a plan

### SDK client configuration for tests
```csharp
new CosmosClientOptions
{
    ConnectionMode = ConnectionMode.Gateway,
    LimitToEndpoint = true,  // Required — prevents SDK from discovering other endpoints
    HttpClientFactory = () => new HttpClient(handler, disposeHandler: false)
}
```

## Project Structure

```
src/
  Cosmos.Emulator.Api/          # ASP.NET Core host (net10.0), HTTPS on port 8081
  Cosmos.Emulator.Core/         # Models, helpers, auth
  Cosmos.Emulator.Storage/      # SQLite repositories, schema, hybrid columns
  Cosmos.Emulator.QueryEngine/  # Cosmos SQL → SQLite translator (lexer, parser, AST)
tests/
  Cosmos.Emulator.Tests.Integration/  # SDK-based e2e tests + EF Core tests
  Cosmos.Emulator.Tests.Unit/         # Unit tests (SQLite JSON behavior)
```

## Current Test Status

100 integration tests + 7 unit tests. 0 skipped.

## Debugging Failing Tests

### SDK hangs or retries forever
- The SDK reads pkranges via **change feed** (`A-IM: Incremental Feed`). It loops until it gets `304 Not Modified`. If our pkranges endpoint always returns 200, the SDK loops forever. Check `PartitionKeyRangeEndpoints.cs`.
- The 304 response must include a **new etag** each time. Same etag = SDK thinks cache is stale and retries.
- `LimitToEndpoint = true` is required in `CosmosClientOptions` or the SDK tries to discover other endpoints.

### SDK query returns wrong type (e.g., string instead of object)
- The SDK rewrites queries with JSON literals (`orderByItems`, `groupByItems`, `payload`). These go through our parser natively.
- If `body` (full document) appears as a value inside `json_object()`, it must be wrapped as `json(body)` — otherwise SQLite stringifies it instead of embedding as sub-object.
- Check `SqliteQueryTranslator.cs` — the `TranslateObject` and SELECT clause builder wrap `body` with `json()`.

### Intermittent pkranges "stale cache" failures
- Happens when many tests share one `CosmosClient` and create many containers. The SDK's `PartitionKeyRangeCache` can get confused.
- Usually 1-2 intermittent failures out of 66. Tests pass individually.
- Not a real bug — it's a test isolation issue from shared SDK client state.

### Seeing what the SDK sends
- Add `LoggingHandler` as a `DelegatingHandler` in the test fixture's `HttpClientFactory` to log all HTTP requests/responses to a file.
- Log to a file (not Console.WriteLine) — test runner buffers stdout and you won't see it until the test completes.
- Check `EmulatorFixture.cs` for the `LoggingHandler` class.

### Seeing what SQL is generated
- Add `File.AppendAllText(logPath, $"[SQL] {sql}\n")` before `docRepo.ExecuteQuery()` in `DocumentEndpoints.HandleQuery`.
- Check `sdk-debug.log` in the project root after running tests.

### Comparing with official emulator
- `docker run -d -p 18081:8081 mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:latest`
- Use node.js probe scripts to send the same requests and compare response bodies/headers.
- The official emulator uses the well-known key: `C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==`

## Not Yet Implemented

- Indexing policy enforcement (reject queries on excluded paths)
- HMAC-SHA256 auth validation
