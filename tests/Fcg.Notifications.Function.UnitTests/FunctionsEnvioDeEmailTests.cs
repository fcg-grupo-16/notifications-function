using Fcg.Notifications.Function.Email;
using Fcg.Notifications.Function.Functions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fcg.Notifications.Function.UnitTests;

/// <summary>
/// Sender de teste que apenas guarda o que foi "enviado", ou lança quando pedido.
/// </summary>
/// <remarks>
/// Feito à mão em vez de com biblioteca de mock: a interface tem um método só, e um duplo explícito
/// deixa o teste mais legível do que o setup/verify de um framework.
/// </remarks>
internal sealed class EmailSenderEspiao : IEmailSender
{
    private readonly Exception? _erroParaLancar;

    public EmailSenderEspiao(Exception? erroParaLancar = null) => _erroParaLancar = erroParaLancar;

    public List<EmailMessage> Enviados { get; } = [];

    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (_erroParaLancar is not null)
        {
            throw _erroParaLancar;
        }

        Enviados.Add(message);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Testes das Functions em si — o comportamento observável de ponta a ponta dentro do processo:
/// dado um corpo de mensagem, sai (ou não sai) e-mail.
/// </summary>
public sealed class FunctionsEnvioDeEmailTests
{
    private const string UrnUserCreated = "urn:message:Fcg.Contracts.Events:UserCreatedEvent";
    private const string UrnPaymentProcessed = "urn:message:Fcg.Contracts.Events:PaymentProcessedEvent";

    // Concatenação em vez de raw string interpolada: o JSON termina em `}}`, que colide com os
    // delimitadores de interpolação.
    private static string EnvelopeUserCreated(string userId, string nome, string email) =>
        "{\"messageType\":[\"" + UrnUserCreated + "\"],\"conversationId\":\"c-1\","
        + "\"message\":{\"userId\":\"" + userId + "\",\"nome\":\"" + nome + "\","
        + "\"email\":\"" + email + "\"}}";

    private static string EnvelopePagamento(string status, string price = "\"29.90\"") =>
        "{\"messageType\":[\"" + UrnPaymentProcessed + "\"],\"conversationId\":\"c-2\","
        + "\"message\":{\"orderId\":\"3f2504e0-4f89-11d3-9a0c-0305e82c3301\",\"userId\":\"u-1\","
        + "\"gameId\":\"game-42\",\"price\":" + price + ",\"status\":\"" + status + "\"}}";

    private static UserCreatedFunction NovaUserCreated(EmailSenderEspiao sender) =>
        new(NullLogger<UserCreatedFunction>.Instance, sender);

    private static PaymentProcessedFunction NovaPaymentProcessed(EmailSenderEspiao sender) =>
        new(NullLogger<PaymentProcessedFunction>.Instance, sender);

    // ---------------------------------------------------------------- cadastro

    [Fact(DisplayName = "Cadastro válido envia o e-mail de boas-vindas para o endereço do usuário")]
    public async Task UserCreated_EnvelopeValido_EnviaBoasVindas()
    {
        var sender = new EmailSenderEspiao();

        await NovaUserCreated(sender).RunAsync(
            EnvelopeUserCreated("u-1", "Maria", "maria@fcg.com"), CancellationToken.None);

        var email = Assert.Single(sender.Enviados);
        Assert.Equal("maria@fcg.com", email.To);
        Assert.Contains("Maria", email.Body);
    }

    [Theory(DisplayName = "Corpo inválido NÃO envia e-mail e NÃO lança (poison message é descartada)")]
    [InlineData("{\"lixo\":true}")]
    [InlineData("não é json")]
    [InlineData("")]
    // O caso que fazia o parser lançar e a mensagem ser reentregue 20 vezes.
    [InlineData("{\"messageType\":null,\"message\":{\"userId\":\"u\",\"nome\":\"n\",\"email\":\"e@x.com\"}}")]
    // Namespace vizinho terminado igual: com EndsWith isto era ACEITO e virava e-mail forjado.
    [InlineData("{\"messageType\":[\"urn:message:Atacante.Fcg.Contracts.Events:UserCreatedEvent\"],\"message\":{\"userId\":\"u\",\"nome\":\"n\",\"email\":\"atacante@evil.com\"}}")]
    public async Task UserCreated_CorpoInvalido_NaoEnviaENaoLanca(string corpo)
    {
        var sender = new EmailSenderEspiao();

        var excecao = await Record.ExceptionAsync(
            () => NovaUserCreated(sender).RunAsync(corpo, CancellationToken.None));

        Assert.Null(excecao);
        Assert.Empty(sender.Enviados);
    }

    [Theory(DisplayName = "Campo obrigatório vazio ou nulo NÃO envia e-mail")]
    [InlineData("u-1", "Maria", "")]
    [InlineData("", "Maria", "maria@fcg.com")]
    [InlineData("u-1", "Maria", "   ")]
    public async Task UserCreated_CampoObrigatorioAusente_NaoEnvia(string userId, string nome, string email)
    {
        // Sem esta guarda o IEmailSender receberia destinatário vazio — e, com `"email": null`, o
        // contrato declara `string` não-anulável mas o System.Text.Json entrega null mesmo assim.
        var sender = new EmailSenderEspiao();

        await NovaUserCreated(sender).RunAsync(
            EnvelopeUserCreated(userId, nome, email), CancellationToken.None);

        Assert.Empty(sender.Enviados);
    }

    [Fact(DisplayName = "Email nulo no JSON (não apenas vazio) também é barrado")]
    public async Task UserCreated_EmailNuloNoJson_NaoEnvia()
    {
        var sender = new EmailSenderEspiao();
        const string corpo = """
        {"messageType":["urn:message:Fcg.Contracts.Events:UserCreatedEvent"],
         "message":{"userId":"u-1","nome":"Maria","email":null}}
        """;

        var excecao = await Record.ExceptionAsync(
            () => NovaUserCreated(sender).RunAsync(corpo, CancellationToken.None));

        Assert.Null(excecao);
        Assert.Empty(sender.Enviados);
    }

    [Fact(DisplayName = "Falha do sender PROPAGA — é o que faz o host reentregar em falha transitória")]
    public async Task UserCreated_SenderFalha_PropagaExcecao()
    {
        // Contrato documentado em IEmailSender: falha transitória deve lançar, para a mensagem ser
        // reentregue. Engolir a exceção aqui perderia o e-mail em silêncio.
        var sender = new EmailSenderEspiao(new InvalidOperationException("SMTP fora do ar"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NovaUserCreated(sender).RunAsync(
                EnvelopeUserCreated("u-1", "Maria", "maria@fcg.com"), CancellationToken.None));
    }

    // ---------------------------------------------------------------- compra

    [Theory(DisplayName = "Pagamento aprovado envia a confirmação (independente da caixa do status)")]
    [InlineData("Approved")]
    [InlineData("approved")]
    [InlineData("APPROVED")]
    public async Task PaymentProcessed_Aprovado_EnviaConfirmacao(string status)
    {
        var sender = new EmailSenderEspiao();

        await NovaPaymentProcessed(sender).RunAsync(EnvelopePagamento(status), CancellationToken.None);

        var email = Assert.Single(sender.Enviados);
        Assert.Contains("game-42", email.Body);
        Assert.Contains("29,90", email.Body);   // pt-BR
    }

    [Theory(DisplayName = "Pagamento NÃO aprovado não envia nada — a regra mora no template")]
    [InlineData("Rejected")]
    [InlineData("Pending")]
    [InlineData("")]
    public async Task PaymentProcessed_NaoAprovado_NaoEnvia(string status)
    {
        var sender = new EmailSenderEspiao();

        await NovaPaymentProcessed(sender).RunAsync(EnvelopePagamento(status), CancellationToken.None);

        Assert.Empty(sender.Enviados);
    }

    [Fact(DisplayName = "Preço como STRING no JSON (formato real do MassTransit) gera a confirmação")]
    public async Task PaymentProcessed_PrecoComoString_EnviaConfirmacao()
    {
        // O bug que fazia a confirmação de compra nunca sair: o MassTransit publica
        // `"price": "29.90"` com aspas, e o System.Text.Json rejeitava por padrão.
        var sender = new EmailSenderEspiao();

        await NovaPaymentProcessed(sender).RunAsync(
            EnvelopePagamento("Approved", "\"1234.56\""), CancellationToken.None);

        var email = Assert.Single(sender.Enviados);
        Assert.Contains("1.234,56", email.Body);
    }

    [Fact(DisplayName = "OrderId vazio é barrado antes de virar e-mail")]
    public async Task PaymentProcessed_OrderIdVazio_NaoEnvia()
    {
        var sender = new EmailSenderEspiao();
        const string corpo = """
        {"messageType":["urn:message:Fcg.Contracts.Events:PaymentProcessedEvent"],
         "message":{"orderId":"00000000-0000-0000-0000-000000000000","userId":"u-1","gameId":"g","price":"1","status":"Approved"}}
        """;

        await NovaPaymentProcessed(sender).RunAsync(corpo, CancellationToken.None);

        Assert.Empty(sender.Enviados);
    }

    [Fact(DisplayName = "Envelope de UserCreated na fila de pagamento é rejeitado (erro de topologia)")]
    public async Task PaymentProcessed_TipoErrado_NaoEnvia()
    {
        var sender = new EmailSenderEspiao();

        await NovaPaymentProcessed(sender).RunAsync(
            EnvelopeUserCreated("u-1", "Maria", "maria@fcg.com"), CancellationToken.None);

        Assert.Empty(sender.Enviados);
    }
}
