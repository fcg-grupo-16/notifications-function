namespace Fcg.Notifications.Function.Persistence;

/// <summary>
/// Persistência do histórico de notificações enviadas. É best-effort: falha aqui não impede o envio
/// do e-mail.
/// </summary>
public interface INotificationHistoryStore
{
    /// <summary>Grava um registro no histórico.</summary>
    Task SaveAsync(NotificationRecord record, CancellationToken ct = default);

    /// <summary>Devolve os registros mais recentes, do mais novo para o mais antigo.</summary>
    Task<IReadOnlyList<NotificationRecord>> GetRecentAsync(int limit, CancellationToken ct = default);

    /// <summary>Cria os índices da collection. Idempotente.</summary>
    Task GarantirIndicesAsync(CancellationToken ct = default);
}
