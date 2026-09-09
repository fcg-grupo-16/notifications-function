using Fcg.Contracts.Events;
using Fcg.Notifications.Function.Messaging;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Fcg.Notifications.Function.Functions;

/// <summary>
/// Acionada por mensagens na fila <c>notifications-user-created</c> do RabbitMQ e envia o e-mail
/// de boas-vindas. Substitui o <c>UserCreatedConsumer</c> do microsserviço <c>notifications-api</c>,
/// deprecado na Fase 3.
/// </summary>
public sealed class UserCreatedFunction
{
    /// <summary>Sufixo da URN esperada em <c>messageType</c> nesta fila.</summary>
    private const string TipoEsperado = "Fcg.Contracts.Events:UserCreatedEvent";

    private readonly ILogger<UserCreatedFunction> _logger;

    public UserCreatedFunction(ILogger<UserCreatedFunction> logger) => _logger = logger;

    /// <summary>
    /// Processa um <see cref="UserCreatedEvent"/> entregue pela fila.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>O parâmetro é <c>string</c>, não um POCO.</b> A extensão de RabbitMQ no modelo isolated
    /// aceita <c>string</c> e tipos serializáveis, mas o corpo que chega é o ENVELOPE do
    /// MassTransit, não o evento: bindar direto em <c>UserCreatedEvent</c> preencheria tudo com
    /// nulo, porque as propriedades do evento estão aninhadas sob <c>message</c>.
    /// </para>
    /// <para>
    /// <b>A fila NÃO é criada aqui.</b> O binding apenas consome; a topologia (exchanges, filas,
    /// bindings e a dead-letter via policy) é declarativa em <c>docker/rabbitmq/definitions.json</c>
    /// no repositório <c>orchestration</c> — ver orchestration#28.
    /// </para>
    /// <para>
    /// <b><c>ConnectionStringSetting</c> é o NOME de uma app setting</b>, nunca o valor. Desde a v2
    /// da extensão não existem mais host/usuário/senha separados: é uma URI AMQP completa, lida de
    /// uma app setting (provisionada no SealedSecret <c>notifications-function-secret</c>).
    /// </para>
    /// </remarks>
    [Function(nameof(UserCreatedFunction))]
    public Task RunAsync(
        [RabbitMQTrigger("notifications-user-created", ConnectionStringSetting = "RabbitMqConnection")]
        string mensagem,
        CancellationToken cancellationToken)
    {
        var envelope = MassTransitEnvelopeParser.TryParse<UserCreatedEvent>(mensagem, TipoEsperado);

        if (envelope?.Message is null)
        {
            // Poison message: loga e DESCARTA (ack). Lançar aqui faria o host retentar até a
            // dead-letter uma mensagem que nunca será processável — ruído sem ganho.
            _logger.LogWarning(
                "Mensagem inválida ou de tipo inesperado na fila notifications-user-created; descartada. Corpo: {Body}",
                Truncar(mensagem));
            return Task.CompletedTask;
        }

        var evento = envelope.Message;

        _logger.LogInformation(
            "UserCreatedEvent recebido. UserId={UserId} Email={Email} ConversationId={ConversationId}",
            evento.UserId, evento.Email, envelope.ConversationId);

        // TODO(#3): idempotência por UserId em Redis, ANTES do envio.
        // TODO(#2): enviar o e-mail de boas-vindas via IEmailSender.
        // TODO(#4): persistir o histórico em notificationsdb (best-effort, depois do envio).
        return Task.CompletedTask;
    }

    /// <summary>Corta o corpo no log para uma mensagem gigante não inundar o stdout.</summary>
    private static string Truncar(string? body) =>
        body is null ? "(vazio)" : body.Length > 500 ? body[..500] + "…" : body;
}
