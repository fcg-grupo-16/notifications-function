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
        _logger.LogInformation(
            "[E-mail] Para: {To} | Assunto: {Subject} | {Body}",
            message.To, message.Subject, message.Body);
        return Task.CompletedTask;
    }
}
