using Fcg.Contracts.Events;
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

    /// <summary>Status que representa pagamento aprovado, conforme o contrato do evento.</summary>
    private const string StatusAprovado = "Approved";

    private readonly ILogger<PaymentProcessedFunction> _logger;

    public PaymentProcessedFunction(ILogger<PaymentProcessedFunction> logger) => _logger = logger;

    /// <inheritdoc cref="UserCreatedFunction.RunAsync"/>
    [Function(nameof(PaymentProcessedFunction))]
    public Task RunAsync(
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
            return Task.CompletedTask;
        }

        var evento = envelope.Message!;

        // Ver UserCreatedFunction: o contrato declara não-anulável, mas o JSON pode trazer null.
        if (evento.OrderId == Guid.Empty || string.IsNullOrWhiteSpace(evento.UserId))
        {
            _logger.LogWarning(
                "PaymentProcessedEvent sem campo obrigatório (OrderId ou UserId); descartado. ConversationId={ConversationId}",
                envelope.ConversationId);
            return Task.CompletedTask;
        }

        // Regra de negócio portada do PaymentProcessedConsumer: só pagamento APROVADO gera e-mail.
        // Na issue #2 esta verificação passa a ser feita por EmailTemplates.PurchaseConfirmation,
        // que devolve null quando o status não é "Approved" — a regra fica num lugar só.
        if (!string.Equals(evento.Status, StatusAprovado, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Pagamento {Status} para o pedido {OrderId} (usuário {UserId}): nenhum e-mail de confirmação será enviado.",
                evento.Status, evento.OrderId, evento.UserId);
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "PaymentProcessedEvent aprovado. OrderId={OrderId} UserId={UserId} GameId={GameId} ConversationId={ConversationId}",
            evento.OrderId, evento.UserId, evento.GameId, envelope.ConversationId);

        // TODO(#3): idempotência por OrderId em Redis — ATENÇÃO: a chave só pode ser consumida no
        //           caminho aprovado, senão um evento "Rejected" bloquearia a confirmação legítima
        //           de um reprocessamento posterior.
        // TODO(#2): enviar a confirmação via IEmailSender.
        // TODO(#4): persistir o histórico.
        return Task.CompletedTask;
    }
}
