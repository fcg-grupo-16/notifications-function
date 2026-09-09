using Fcg.Contracts.Events;
using Fcg.Notifications.Function.Email;

namespace Fcg.Notifications.Function.UnitTests;

/// <summary>
/// Testes dos templates de e-mail, portados do <c>notifications-api</c>.
/// </summary>
/// <remarks>
/// Cobertura idêntica à do repositório de origem, reescrita com <c>Assert</c> do xUnit em vez de
/// FluentAssertions: os demais testes deste repositório já usam esse estilo, e evitamos trazer uma
/// dependência cuja versão 8 exige licença comercial para uso não-OSS.
/// </remarks>
public sealed class EmailTemplatesTests
{
    [Fact(DisplayName = "Welcome endereça o e-mail do usuário e cita o nome no corpo")]
    public void Welcome_EnderecaOEmailDoUsuarioEContemONome()
    {
        var evento = new UserCreatedEvent
        {
            UserId = "u-1",
            Nome = "Maria",
            Email = "maria@exemplo.com"
        };

        var email = EmailTemplates.Welcome(evento);

        Assert.Equal("maria@exemplo.com", email.To);
        Assert.False(string.IsNullOrWhiteSpace(email.Subject));
        Assert.Contains("Maria", email.Body);
    }

    [Theory(DisplayName = "PurchaseConfirmation gera o e-mail quando o pagamento foi aprovado")]
    [InlineData("Approved")]
    [InlineData("approved")]
    [InlineData("APPROVED")]
    public void PurchaseConfirmation_QuandoAprovado_GeraEmail(string status)
    {
        var orderId = Guid.NewGuid();
        var evento = new PaymentProcessedEvent
        {
            OrderId = orderId,
            UserId = "u-1",
            GameId = "game-42",
            Price = 99.90m,
            Status = status
        };

        var email = EmailTemplates.PurchaseConfirmation(evento);

        Assert.NotNull(email);
        Assert.Equal("u-1", email!.To);
        Assert.Contains("game-42", email.Body);
        Assert.Contains(orderId.ToString(), email.Body);
    }

    [Fact(DisplayName = "PurchaseConfirmation formata o preço em pt-BR (pega regressão de InvariantGlobalization)")]
    public void PurchaseConfirmation_FormataPrecoEmPtBr()
    {
        // EmailTemplates usa `new CultureInfo("pt-BR")`. Imagens de container .NET podem rodar em
        // invariant globalization, onde essa cultura não existe — e aí o preço sai como "¤99.90" ou
        // a chamada lança. É por isso que o csproj declara InvariantGlobalization=false, e este
        // teste é o que impede a regressão passar despercebida no CI ou no Dockerfile da #5.
        var evento = new PaymentProcessedEvent
        {
            OrderId = Guid.NewGuid(),
            UserId = "u-1",
            GameId = "game-42",
            Price = 1234.56m,
            Status = "Approved"
        };

        var email = EmailTemplates.PurchaseConfirmation(evento);

        Assert.NotNull(email);
        // Separador de milhar ponto e decimal vírgula: a marca da cultura pt-BR.
        Assert.Contains("1.234,56", email!.Body);
    }

    [Theory(DisplayName = "PurchaseConfirmation devolve null quando o pagamento NÃO foi aprovado")]
    [InlineData("Rejected")]
    [InlineData("")]
    [InlineData("Pending")]
    public void PurchaseConfirmation_QuandoNaoAprovado_RetornaNull(string status)
    {
        // É esta a regra de negócio: a decisão de "manda ou não manda e-mail" mora no template, e a
        // Function apenas respeita o null. Duplicar a verificação na Function criaria dois lugares
        // para a mesma regra.
        var evento = new PaymentProcessedEvent
        {
            OrderId = Guid.NewGuid(),
            UserId = "u-1",
            GameId = "game-42",
            Price = 99.90m,
            Status = status
        };

        Assert.Null(EmailTemplates.PurchaseConfirmation(evento));
    }

    [Theory(DisplayName = "IsApproved reconhece o status aprovado sem depender de caixa")]
    [InlineData("Approved", true)]
    [InlineData("approved", true)]
    [InlineData("APPROVED", true)]
    [InlineData("Rejected", false)]
    [InlineData("", false)]
    public void IsApproved_ReconheceOStatusAprovado(string status, bool esperado)
    {
        Assert.Equal(esperado, EmailTemplates.IsApproved(status));
    }
}
