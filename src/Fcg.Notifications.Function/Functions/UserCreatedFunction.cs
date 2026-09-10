using Fcg.Contracts.Events;
using Fcg.Notifications.Function.Email;
using Fcg.Notifications.Function.Idempotency;
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
    private readonly IEmailSender _emailSender;
    private readonly IProcessedMessageStore _store;

    public UserCreatedFunction(
        ILogger<UserCreatedFunction> logger,
        IEmailSender emailSender,
        IProcessedMessageStore store)
    {
        _logger = logger;
        _emailSender = emailSender;
        _store = store;
    }

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
    public async Task RunAsync(
        [RabbitMQTrigger("notifications-user-created", ConnectionStringSetting = "RabbitMqConnection")]
        string mensagem,
        CancellationToken cancellationToken)
    {
        var envelope = MassTransitEnvelopeParser.TryParse<UserCreatedEvent>(mensagem, TipoEsperado);

        if (envelope is null)
        {
            // Poison message: loga e DESCARTA (ack). Lançar aqui faria o host retentar até a
            // dead-letter uma mensagem que nunca será processável — ruído sem ganho.
            // O corpo cru vai em Debug e NÃO em Warning: ele contém dado pessoal e conteúdo
            // controlado por quem publicou, e este log vai para o stdout do container.
            _logger.LogWarning(
                "Mensagem inválida ou de tipo inesperado na fila notifications-user-created; descartada.");
            _logger.LogDebug("Corpo descartado: {Corpo}", LogSanitizer.TruncarCorpo(mensagem));
            return;
        }

        var evento = envelope.Message!;

        // O contrato declara UserId e Email como `string` não-anulável, mas o System.Text.Json
        // SOBRESCREVE o default com null quando o JSON traz `"email": null` — então a garantia do
        // tipo não vale para dado que veio da rede. Sem esta checagem, a issue #2 enviaria e-mail
        // para destinatário nulo ou estouraria NullReferenceException dentro do IEmailSender.
        if (!LogSanitizer.IdentificadorEhAceitavel(evento.UserId)
            || !LogSanitizer.DestinatarioEhAceitavel(evento.Email))
        {
            _logger.LogWarning(
                "UserCreatedEvent com UserId ausente ou destinatário inaceitável; descartado. ConversationId={ConversationId}",
                envelope.ConversationId);
            return;
        }

        _logger.LogInformation(
            "UserCreatedEvent recebido. UserId={UserId} Email={Email} ConversationId={ConversationId}",
            evento.UserId, LogSanitizer.MascararEmail(evento.Email), envelope.ConversationId);

        // IDEMPOTÊNCIA ANTES DO ENVIO: "reserva" a chave e só então manda o e-mail.
        //
        // O trade-off é consciente e é o mesmo que o notifications-api já fazia. Marcar ANTES
        // significa que uma falha ENTRE a marcação e o envio perde o e-mail (a reentrega vê a
        // chave e não reenvia). Marcar DEPOIS trocaria isso por "pode duplicar". Escolhemos
        // at-most-once porque e-mail duplicado é visível e irritante para o cliente, enquanto
        // perder um e-mail de boas-vindas é recuperável — e porque este caminho é o mesmo do
        // serviço que estamos substituindo, então a migração não muda comportamento.
        var inedito = await _store.TryMarkAsProcessedAsync(
            nameof(UserCreatedEvent), evento.UserId, cancellationToken);

        if (!inedito)
        {
            // O store já registrou o motivo no log.
            return;
        }

        // COMPENSAÇÃO: se o envio falhar, a marcação é desfeita para a reentrega poder tentar de
        // novo. Sem isto, marcar-antes-de-enviar tornaria a perda PERMANENTE — a reentrega veria a
        // chave, sairia calada, o host daria ack, e o e-mail desapareceria sem log de erro e sem ir
        // para a dead-letter. É também o que mantém verdadeiro o contrato de IEmailSender ("falha
        // transitória deve lançar, a reentrega resolve").
        try
        {
            var email = EmailTemplates.Welcome(evento);
            await _emailSender.SendAsync(email, cancellationToken);
        }
        catch
        {
            await _store.UnmarkAsync(nameof(UserCreatedEvent), evento.UserId, cancellationToken);
            throw;
        }

        // TODO(#4): persistir o histórico em notificationsdb (best-effort, depois do envio).
    }
}
