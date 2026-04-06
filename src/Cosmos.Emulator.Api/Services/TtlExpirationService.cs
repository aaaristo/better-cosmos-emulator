using Cosmos.Emulator.Storage;
using Cosmos.Emulator.Storage.Repositories;

namespace Cosmos.Emulator.Api.Services;

/// <summary>
/// Background service that periodically scans for and deletes TTL-expired documents.
/// Expired documents are deleted through DocumentRepository.Delete() so they
/// appear in the change feed like any other deletion.
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
        var docRepo = new DocumentRepository(_storage);

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
                CleanDatabase(dbId, now, docRepo);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error cleaning TTL for database {DbId}", dbId);
            }
        }
    }

    private void CleanDatabase(string dbId, long now, DocumentRepository docRepo)
    {
        using var conn = _storage.GetDatabaseConnection(dbId);
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
            var expired = docRepo.FindExpiredDocuments(dbId, containerId, defaultTtl, now);
            foreach (var (docId, pk) in expired)
            {
                try
                {
                    docRepo.Delete(dbId, containerId, docId, pk, ttlExpired: true);
                    _logger.LogDebug("TTL: expired {DocId} from {Container}", docId, containerId);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "TTL: failed to delete {DocId} from {Container}", docId, containerId);
                }
            }
        }
    }
}
