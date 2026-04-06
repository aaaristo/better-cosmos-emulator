# Better Cosmos Emulator

A lightweight Azure Cosmos DB emulator backed by SQLite, implementing the REST API so the official `Microsoft.Azure.Cosmos` .NET SDK can connect in Gateway mode.

## Build & Test

```bash
nuke Compile              # or: dotnet build
dotnet test tests/Cosmos.Emulator.Tests.Integration
```

## Architecture

- **Database = SQLite file** (`data/{dbname}.db`), **Container = table** within that file
- **Hybrid columns**: top-level and nested scalar properties are flattened into SQLite columns (`address.city` → `[address__city]`). Arrays stay in the `body` JSON column and use `json_extract()` at query time.
- **Catalog**: `data/_catalog.db` tracks database-level metadata
- **Query engine**: hand-rolled lexer → parser → AST → SQLite SQL translator

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
- Subsequent calls (with `If-None-Match: <etag>`): return `304`
- **If you always return 200, the SDK loops forever** — this was the main blocker

### _rid-based URL routing
The SDK uses `_rid` values in URL paths for internal calls (e.g., `/dbs/AABxYQ==/colls/AABxYYAAcWI=/pkranges`). The `-` in URLs represents `/` in base64 rids. Repositories must resolve both user-facing IDs and _rid values.

### JSON serialization
- Use `PropertyNamingPolicy = null` to preserve mixed casing (`PartitionKeyRanges`, `_rid`, `_count`)
- Use `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` so `_etag` quotes serialize as `\"` not `\u0022`
- The SDK uses Newtonsoft.Json internally for response deserialization

### Session tokens
Format: `{pkRangeId}:-1#{lsn}` (e.g., `0:-1#1`). The old format `0:0` causes SDK parsing errors.

### Auth
Currently disabled (accepts any valid-looking Authorization header). The SDK computes HMAC-SHA256 signatures over lowercased resource links, but uses _rid-based paths where base64 is case-sensitive. Full auth validation can be added later.

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
  Cosmos.Emulator.Api/          # ASP.NET Core host (net10.0)
  Cosmos.Emulator.Core/         # Models, helpers, auth
  Cosmos.Emulator.Storage/      # SQLite repositories
  Cosmos.Emulator.QueryEngine/  # Cosmos SQL → SQLite translator
tests/
  Cosmos.Emulator.Tests.Integration/  # SDK-based e2e tests
```

## Current Test Status

28 passing, 6 failing (query TOP/ORDER BY and change feed tests need work).
