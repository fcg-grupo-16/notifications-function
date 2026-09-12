using Fcg.Notifications.Function.Functions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fcg.Notifications.Function.UnitTests;

/// <summary>
/// Testes de como as Functions usam o histórico: o que é gravado e o que acontece quando a
/// gravação falha.
/// </summary>
public sealed class HistoricoNasFunctionsTests
{
    private const string UrnUserCreated = "urn:message:Fcg.Contracts.Events:UserCreatedEvent";
    private const string UrnPaymentProcessed = "urn:message:Fcg.Contracts.Events:PaymentProcessedEvent";
    private const string OrderId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

    private static string EnvelopeUserCreated() =>
        "{\"messageType\":[\"" + UrnUserCreated + "\"],\"conversationId\":\"c-1\","
        + "\"message\":{\"userId\":\"u-1\",\"nome\":\"Maria\",\"email\":\"maria@fcg.com\"}}";

    private static string EnvelopePagamento(string status) =>
        "{\"messageType\":[\"" + UrnPaymentProcessed + "\"],\"conversationId\":\"c-2\","
        + "\"message\":{\"orderId\":\"" + OrderId + "\",\"userId\":\"u-1\","
        + "\"gameId\":\"g-1\",\"price\":\"29.90\",\"status\":\"" + status + "\"}}";

    private static UserCreatedFunction NovaUserCreated(
        EmailSenderEspiao sender, HistoricoEspiao historico) =>
        new(NullLogger<UserCreatedFunction>.Instance, sender, new StoreEspiao(), historico);

    private static PaymentProcessedFunction NovaPaymentProcessed(
        EmailSenderEspiao sender, HistoricoEspiao historico) =>
        new(NullLogger<PaymentProcessedFunction>.Instance, sender, new StoreEspiao(), historico);

    [Fact(DisplayName = "Boas-vindas grava o histórico no formato da Fase 2")]
    public async Task UserCreated_GravaHistoricoNoFormatoDaFase2()
    {
        var sender = new EmailSenderEspiao();
        var historico = new HistoricoEspiao();

        await NovaUserCreated(sender, historico)
            .RunAsync(EnvelopeUserCreated(), CancellationToken.None);

        var email = Assert.Single(sender.Enviados);
        var registro = Assert.Single(historico.Salvos);
        Assert.Equal("UserCreatedEvent", registro.Type);
        Assert.Equal("maria@fcg.com", registro.Recipient);
        Assert.Equal(email.Subject, registro.Subject);
        Assert.Equal(email.Body, registro.Body);
        Assert.Equal("u-1", registro.NaturalKey);
    }

    [Fact(DisplayName = "Compra aprovada grava o histórico com o OrderId como chave natural")]
    public async Task PaymentProcessed_Aprovado_GravaHistorico()
    {
        var sender = new EmailSenderEspiao();
        var historico = new HistoricoEspiao();

        await NovaPaymentProcessed(sender, historico)
            .RunAsync(EnvelopePagamento("Approved"), CancellationToken.None);

        var registro = Assert.Single(historico.Salvos);
        Assert.Equal("PaymentProcessedEvent", registro.Type);
        Assert.Equal(OrderId, registro.NaturalKey);
        Assert.Single(sender.Enviados);
    }

    [Fact(DisplayName = "Compra rejeitada não envia e-mail nem grava histórico")]
    public async Task PaymentProcessed_Rejeitado_NaoGravaHistorico()
    {
        var sender = new EmailSenderEspiao();
        var historico = new HistoricoEspiao();

        await NovaPaymentProcessed(sender, historico)
            .RunAsync(EnvelopePagamento("Rejected"), CancellationToken.None);

        Assert.Empty(historico.Salvos);
        Assert.Empty(sender.Enviados);
    }

    [Fact(DisplayName = "Falha ao persistir o histórico não impede o envio nem propaga exceção")]
    public async Task Function_FalhaAoPersistir_NaoImpedeOEnvio()
    {
        var sender = new EmailSenderEspiao();
        var historico = new HistoricoEspiao(new TimeoutException("mongo fora"));

        var excecao = await Record.ExceptionAsync(() =>
            NovaUserCreated(sender, historico).RunAsync(EnvelopeUserCreated(), CancellationToken.None));

        Assert.Null(excecao);
        Assert.Single(sender.Enviados);
    }

    [Fact(DisplayName = "Falha ao persistir não propaga na confirmação de compra")]
    public async Task PaymentProcessed_FalhaAoPersistir_NaoPropaga()
    {
        var sender = new EmailSenderEspiao();
        var historico = new HistoricoEspiao(new TimeoutException("mongo fora"));

        var excecao = await Record.ExceptionAsync(() =>
            NovaPaymentProcessed(sender, historico)
                .RunAsync(EnvelopePagamento("Approved"), CancellationToken.None));

        Assert.Null(excecao);
        Assert.Single(sender.Enviados);
    }

    [Theory(DisplayName = "O limite da consulta de auditoria é aplicado no servidor")]
    [InlineData("?limit=99999", 200)]
    [InlineData("?limit=10", 10)]
    [InlineData("?limit=0", 1)]
    [InlineData("?limit=-5", 1)]
    [InlineData("?limit=abc", 50)]
    [InlineData("", 50)]
    public void HistoryFunction_LimitAcimaDoMaximo_EhLimitado(string query, int esperado) =>
        Assert.Equal(esperado, NotificationHistoryFunction.LerLimite(query));
}
