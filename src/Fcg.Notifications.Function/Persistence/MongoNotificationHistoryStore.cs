using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Fcg.Notifications.Function.Persistence;

/// <summary>
/// Histórico de notificações na collection <c>notifications</c> do database <c>notificationsdb</c> —
/// a mesma alimentada pelo <c>notifications-api</c> na Fase 2.
/// </summary>
public sealed class MongoNotificationHistoryStore : INotificationHistoryStore
{
    public const string CollectionName = "notifications";

    private const int DefaultLimit = 50;
    private const int MaxLimit = 500;

    private static int _indicesGarantidos;

    private readonly IMongoCollection<NotificationRecord> _notifications;
    private readonly ILogger<MongoNotificationHistoryStore> _logger;

    public MongoNotificationHistoryStore(
        IMongoDatabase database,
        ILogger<MongoNotificationHistoryStore> logger)
    {
        _notifications = database.GetCollection<NotificationRecord>(CollectionName);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task SaveAsync(NotificationRecord record, CancellationToken ct = default)
    {
        await GarantirIndicesUmaVezAsync(ct);
        await _notifications.InsertOneAsync(record, cancellationToken: ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<NotificationRecord>> GetRecentAsync(
        int limit, CancellationToken ct = default)
    {
        var safeLimit = limit is > 0 and <= MaxLimit ? limit : DefaultLimit;

        return await _notifications
            .Find(FilterDefinition<NotificationRecord>.Empty)
            .SortByDescending(n => n.SentAtUtc)
            .Limit(safeLimit)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public Task GarantirIndicesAsync(CancellationToken ct = default)
    {
        var indice = new CreateIndexModel<NotificationRecord>(
            Builders<NotificationRecord>.IndexKeys.Descending(n => n.SentAtUtc),
            new CreateIndexOptions { Name = "ix_sentAtUtc" });

        return _notifications.Indexes.CreateOneAsync(indice, cancellationToken: ct);
    }

    /// <summary>
    /// Garante os índices uma vez por processo, de forma preguiçosa e best-effort. Em caso de falha
    /// o flag é revertido para uma nova tentativa.
    /// </summary>
    private async Task GarantirIndicesUmaVezAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _indicesGarantidos, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await GarantirIndicesAsync(ct);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _indicesGarantidos, 0);
            _logger.LogWarning(ex, "Falha ao garantir os índices do histórico de notificações.");
        }
    }
}
