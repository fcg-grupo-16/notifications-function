using System.Diagnostics;
using Fcg.Notifications.Function.Messaging;
using Microsoft.Extensions.Logging;

namespace Fcg.Notifications.Function.Observability;

/// <summary>
/// Escopos de log que correlacionam as linhas da Function com o fluxo de negócio que as originou.
/// </summary>
public static class LoggingScopes
{
    /// <summary>
    /// Abre um escopo com <c>ConversationId</c>, <c>MessageId</c>, <c>MessageType</c> e <c>TraceId</c>,
    /// para toda linha de log emitida dentro dele carregar esses campos.
    /// </summary>
    public static IDisposable? BeginEventScope<T>(this ILogger logger, MassTransitEnvelope<T> envelope)
        where T : class =>
        logger.BeginScope(new Dictionary<string, object?>
        {
            ["ConversationId"] = envelope.ConversationId,
            ["MessageId"] = envelope.MessageId,
            ["MessageType"] = envelope.MessageType?.FirstOrDefault(),
            ["TraceId"] = Activity.Current?.TraceId.ToString()
        });
}
