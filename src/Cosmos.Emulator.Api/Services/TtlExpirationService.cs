using Cosmos.Emulator.Storage;
using Cosmos.Emulator.Storage.Schema;
using Microsoft.Data.Sqlite;

namespace Cosmos.Emulator.Api.Services;

/// <summary>
/// Background service that periodically scans for and deletes TTL-expired documents.
///
/// Cosmos DB TTL rules:
/// - Container defaultTtl absent/null: TTL disabled, no expiration
/// - Container defaultTtl = -1: TTL enabled, but documents don't expire unless they have a ttl field
/// - Container defaultTtl > 0: documents expire after defaultTtl seconds unless overridden
/// - Document ttl = -1: never expires (overrides container default)
/// - Document ttl > 0: expires after ttl seconds (overrides container default)
/// - Document ttl absent: uses container defaultTtl
/// </summary>
public class TtlExpirationService : BackgroundService
{
    private readonly SqliteStorageProvider _storage;
    private readonly ILogger<TtlExpirationService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(5);

    public TtlExpirationService(SqliteStorageProvider storage, ILogger<TtlExpirationService> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CleanExpiredDocuments();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during TTL cleanup");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private void CleanExpiredDocuments()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Get all databases
        using var catalogConn = _storage.GetCatalogConnection();
        using var dbCmd = catalogConn.CreateCommand();
        dbCmd.CommandText = "SELECT id FROM databases";

        var dbIds = new List<string>();
        using (var reader = dbCmd.ExecuteReader())
        {
            while (reader.Read())
                dbIds.Add(reader.GetString(0));
        }

        foreach (var dbId in dbIds)
        {
            try
            {
                CleanDatabase(dbId, now);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error cleaning TTL for database {DbId}", dbId);
            }
        }
    }

    private void CleanDatabase(string dbId, long now)
    {
        using var conn = _storage.GetDatabaseConnection(dbId);

        // Get containers with TTL enabled (defaultTtl is not null)
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, default_ttl FROM _containers WHERE default_ttl IS NOT NULL";

        var containers = new List<(string id, int defaultTtl)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                containers.Add((reader.GetString(0), reader.GetInt32(1)));
        }

        foreach (var (containerId, defaultTtl) in containers)
        {
            CleanContainer(conn, containerId, defaultTtl, now);
        }
    }

    private void CleanContainer(SqliteConnection conn, string containerId, int defaultTtl, long now)
    {
        var table = SchemaInitializer.QuoteName(containerId);

        // Delete documents where:
        // 1. Document has ttl > 0 and _ts + ttl <= now
        // 2. Container has defaultTtl > 0, document has no ttl field, and _ts + defaultTtl <= now
        // Documents with ttl = -1 never expire
        using var cmd = conn.CreateCommand();

        if (defaultTtl > 0)
        {
            // Container has a positive default TTL
            // Delete docs where: (doc ttl > 0 AND expired by doc ttl) OR (doc has no ttl AND expired by default ttl)
            // But NOT docs where doc ttl = -1
            cmd.CommandText = $"""
                DELETE FROM {table}
                WHERE is_deleted = 0 AND (
                    (json_extract(body, '$.ttl') > 0 AND ts + json_extract(body, '$.ttl') <= @now)
                    OR
                    (json_extract(body, '$.ttl') IS NULL AND ts + @defaultTtl <= @now)
                )
                AND COALESCE(json_extract(body, '$.ttl'), 0) != -1
                """;
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@defaultTtl", defaultTtl);
        }
        else
        {
            // Container defaultTtl = -1: only expire docs that have their own ttl > 0
            cmd.CommandText = $"""
                DELETE FROM {table}
                WHERE is_deleted = 0
                AND json_extract(body, '$.ttl') > 0
                AND ts + json_extract(body, '$.ttl') <= @now
                """;
            cmd.Parameters.AddWithValue("@now", now);
        }

        var deleted = cmd.ExecuteNonQuery();
        if (deleted > 0)
        {
            _logger.LogInformation("TTL: deleted {Count} expired documents from {Container}", deleted, containerId);
        }
    }
}
