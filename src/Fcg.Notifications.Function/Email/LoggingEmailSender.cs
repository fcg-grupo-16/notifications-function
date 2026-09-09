using Fcg.Notifications.Function.Messaging;
using Microsoft.Extensions.Logging;

namespace Fcg.Notifications.Function.Email;

/// <summary>
/// Implementação padrão do <see cref="IEmailSender"/> que "envia" o e-mail
/// registrando-o no log (simulação, sem provedor externo — o serviço não tem
/// dependência de e-mail real). Trocável por uma implementação SMTP/provedor
/// real via DI, sem tocar nos consumers.
/// </summary>
public sealed class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _logger;

    public LoggingEmailSender(ILogger<LoggingEmailSender> logger) => _logger = logger;

    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        // Destinatário MASCARADO e corpo TRUNCADO. As Functions já mascaram o e-mail antes de
        // logar; entregar o endereço cru aqui anulava esse cuidado — as duas linhas apareciam
        // adjacentes no stdout, uma com `ma***@fcg.com` e a outra com o endereço completo.
        // E o corpo carrega o nome do usuário, sem limite de tamanho: um `Nome` de 10 MB virava
        // uma única linha de log de 10 MB (medido).
        _logger.LogInformation(
            "[E-mail] Para: {To} | Assunto: {Subject} | {Body}",
            LogSanitizer.MascararEmail(message.To),
            message.Subject,
            LogSanitizer.TruncarCorpo(message.Body));

        // Conteúdo completo só em Debug, para quem estiver depurando de propósito.
        _logger.LogDebug("[E-mail] destinatário completo: {To}", message.To);

        return Task.CompletedTask;
    }
}
