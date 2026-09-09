namespace Fcg.Notifications.Function.Email;

/// <summary>
/// Mensagem de e-mail já renderizada a partir de um template, pronta para envio.
/// Separa o CONTEÚDO (destinatário/assunto/corpo) do meio de ENVIO (<see cref="IEmailSender"/>).
/// </summary>
public sealed record EmailMessage(string To, string Subject, string Body);
