using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Fcg.Notifications.Function.Persistence;

/// <summary>
/// Registro persistido de uma notificação enviada. Formato idêntico ao gravado pelo
/// <c>notifications-api</c> na Fase 2 — renomear campos torna os documentos antigos ilegíveis.
/// </summary>
public sealed class NotificationRecord
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    /// <summary>Tipo do evento que originou a notificação (ex.: <c>UserCreatedEvent</c>).</summary>
    public string Type { get; set; } = string.Empty;

    public string Recipient { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    /// <summary>Chave natural do evento: <c>UserId</c> no cadastro, <c>OrderId</c> na compra.</summary>
    public string NaturalKey { get; set; } = string.Empty;

    public DateTime SentAtUtc { get; set; }
}
