using System.Globalization;
using Fcg.Contracts.Events;

namespace Fcg.Notifications.Function.Email;

/// <summary>
/// Renderiza as mensagens de e-mail (pt-BR) a partir dos eventos de domínio.
/// Puro/estático para facilitar os testes. Separa o CONTEÚDO (template) do
/// ENVIO (<see cref="IEmailSender"/>).
/// </summary>
public static class EmailTemplates
{
    private static readonly CultureInfo PtBr = new("pt-BR");

    /// <summary>
    /// E-mail de boas-vindas para um novo usuário.
    /// </summary>
    public static EmailMessage Welcome(UserCreatedEvent message) => new(
        To: message.Email,
        Subject: "Bem-vindo(a) à FIAP Cloud Games",
        Body: $"Olá, {message.Nome}! Sua conta foi criada com sucesso. Boas-vindas à FIAP Cloud Games.");

    /// <summary>
    /// Confirmação de compra — apenas quando o pagamento foi aprovado;
    /// retorna <c>null</c> quando o status não é "Approved" (nenhum e-mail é enviado).
    /// </summary>
    public static EmailMessage? PurchaseConfirmation(PaymentProcessedEvent message)
    {
        if (!IsApproved(message.Status))
        {
            return null;
        }

        var price = message.Price.ToString("C", PtBr);
        return new EmailMessage(
            To: message.UserId,
            Subject: "Confirmação de compra",
            Body: $"Sua compra foi aprovada: jogo {message.GameId} adquirido por {price} (pedido {message.OrderId}).");
    }

    /// <summary>
    /// Indica se o status do pagamento representa aprovação.
    /// </summary>
    public static bool IsApproved(string status) =>
        string.Equals(status, "Approved", StringComparison.OrdinalIgnoreCase);
}
