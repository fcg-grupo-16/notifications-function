using Fcg.Contracts.Events;
using Fcg.Notifications.Function.Email;
using Fcg.Notifications.Function.Idempotency;
using Fcg.Notifications.Function.Messaging;
using Fcg.Notifications.Function.Persistence;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Fcg.Notifications.Function.Functions;

/// <summary>
/// Acionada por mensagens na fila <c>notifications-payment-processed</c> do RabbitMQ e envia a
/// confirmação de compra. Substitui o <c>PaymentProcessedConsumer</c> do <c>notifications-api</c>.
/// </summary>
public sealed class PaymentProcessedFunction
{
    private const string TipoEsperado = "Fcg.Contracts.Events:PaymentProcessedEvent";

    private readonly ILogger<PaymentProcessedFunction> _logger;
    private readonly IEmailSender _emailSender;
    private readonly IProcessedMessageStore _store;
    private readonly INotificationHistoryStore _history;

    public PaymentProcessedFunction(
        ILogger<PaymentProcessedFunction> logger,
        IEmailSender emailSender,
        IProcessedMessageStore store,
        INotificationHistoryStore history)
    {
        _logger = logger;
        _emailSender = emailSender;
        _store = store;
        _history = history;
    }

    /// <inheritdoc cref="UserCreatedFunction.RunAsync"/>
    [Function(nameof(PaymentProcessedFunction))]
    public async Task RunAsync(
        [RabbitMQTrigger("notifications-payment-processed", ConnectionStringSetting = "RabbitMqConnection")]
        string mensagem,
        CancellationToken cancellationToken)
    {
        var envelope = MassTransitEnvelopeParser.TryParse<PaymentProcessedEvent>(mensagem, TipoEsperado);

        if (envelope is null)
        {
            _logger.LogWarning(
                "Mensagem inválida ou de tipo inesperado na fila notifications-payment-processed; descartada.");
            _logger.LogDebug("Corpo descartado: {Corpo}", LogSanitizer.TruncarCorpo(mensagem));
            return;
        }

        var evento = envelope.Message!;

        // Ver UserCreatedFunction: o contrato declara não-anulável, mas o JSON pode trazer null.
        if (evento.OrderId == Guid.Empty || !LogSanitizer.IdentificadorEhAceitavel(evento.UserId))
        {
            _logger.LogWarning(
                "PaymentProcessedEvent sem campo obrigatório (OrderId ou UserId); descartado. ConversationId={ConversationId}",
                envelope.ConversationId);
            return;
        }

        // A REGRA DE NEGÓCIO MORA NO TEMPLATE, não aqui: PurchaseConfirmation devolve null quando
        // o pagamento não foi aprovado. Duplicar a verificação com um `if (Status != "Approved")`
        // nesta função criaria dois lugares para a mesma regra, que um dia divergiriam.
        // Comportamento portado 1:1 do PaymentProcessedConsumer.
        var confirmacao = EmailTemplates.PurchaseConfirmation(evento);

        if (confirmacao is null)
        {
            _logger.LogInformation(
                "Pagamento {Status} para o pedido {OrderId} (usuário {UserId}): nenhum e-mail de confirmação será enviado.",
                evento.Status, evento.OrderId, evento.UserId);
            return;
        }

        _logger.LogInformation(
            "PaymentProcessedEvent aprovado. OrderId={OrderId} UserId={UserId} GameId={GameId} ConversationId={ConversationId}",
            evento.OrderId, evento.UserId, evento.GameId, envelope.ConversationId);

        // A CHAVE SÓ É CONSUMIDA NO CAMINHO APROVADO — repare que este bloco está DEPOIS do
        // `confirmacao is null`, não antes. Se um evento "Rejected" gastasse a chave do OrderId, a
        // confirmação LEGÍTIMA de um reprocessamento posterior do mesmo pedido (rejeitado e depois
        // aprovado) seria bloqueada para sempre. Detalhe sutil, portado 1:1 do
        // PaymentProcessedConsumer — não mova este guard para cima.
        var inedito = await _store.TryMarkAsProcessedAsync(
            nameof(PaymentProcessedEvent), evento.OrderId.ToString(), cancellationToken);

        if (!inedito)
        {
            return;
        }

        // COMPENSAÇÃO: se o envio falhar, a marcação é desfeita para a reentrega poder tentar de
        // novo. Sem isto, marcar-antes-de-enviar tornaria a perda PERMANENTE — a reentrega veria a
        // chave, sairia calada, o host daria ack, e o e-mail desapareceria sem log de erro e sem ir
        // para a dead-letter. É também o que mantém verdadeiro o contrato de IEmailSender ("falha
        // transitória deve lançar, a reentrega resolve").
        try
        {
            await _emailSender.SendAsync(confirmacao, cancellationToken);
        }
        catch
        {
            await _store.UnmarkAsync(nameof(PaymentProcessedEvent), evento.OrderId.ToString(), cancellationToken);
            throw;
        }

        await SalvarHistoricoAsync(evento, confirmacao, cancellationToken);
    }

    /// <summary>
    /// Grava a confirmação enviada no histórico de auditoria. Best-effort: a exceção é registrada e
    /// engolida.
    /// </summary>
    private async Task SalvarHistoricoAsync(
        PaymentProcessedEvent evento, EmailMessage confirmacao, CancellationToken ct)
    {
        try
        {
            await _history.SaveAsync(new NotificationRecord
            {
                Type = nameof(PaymentProcessedEvent),
                Recipient = confirmacao.To,
                Subject = confirmacao.Subject,
                Body = confirmacao.Body,
                NaturalKey = evento.OrderId.ToString(),
                SentAtUtc = DateTime.UtcNow
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Falha ao persistir o histórico da confirmação de compra para OrderId={OrderId}; e-mail já enviado.",
                evento.OrderId);
        }
    }
}
