using Fcg.Contracts.Events;
using Fcg.Notifications.Function.Email;
using Fcg.Notifications.Function.Messaging;
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

    public PaymentProcessedFunction(ILogger<PaymentProcessedFunction> logger, IEmailSender emailSender)
    {
        _logger = logger;
        _emailSender = emailSender;
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
        if (evento.OrderId == Guid.Empty || string.IsNullOrWhiteSpace(evento.UserId))
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

        // TODO(#3): idempotência por OrderId AQUI — e a chave só pode ser consumida neste caminho
        //           aprovado: um evento "Rejected" que gastasse a chave bloquearia a confirmação
        //           legítima de um reprocessamento posterior.
        await _emailSender.SendAsync(confirmacao, cancellationToken);

        // TODO(#4): persistir o histórico.
    }
}
