namespace Fcg.Notifications.Function.Email;

/// <summary>
/// Abstração de envio de e-mail. A implementação é PLUGÁVEL: hoje um sender que
/// apenas registra no log (simulação), amanhã um provedor SMTP/HTTP real — sem
/// mudar os consumers, que dependem só desta interface.
/// </summary>
public interface IEmailSender
{
    /// <summary>Envia a mensagem já renderizada.</summary>
    /// <remarks>
    /// <b>Contrato de erro — muda de sentido agora que rodamos como Azure Function.</b> Uma exceção
    /// que escape daqui faz o host devolver a mensagem ao broker e reentregá-la. Portanto:
    /// <list type="bullet">
    ///   <item>
    ///     falha TRANSITÓRIA (provedor fora do ar, timeout de rede) <b>deve</b> lançar — a
    ///     reentrega resolve;
    ///   </item>
    ///   <item>
    ///     falha DETERMINÍSTICA (destinatário inválido, template quebrado) <b>não deve</b> lançar —
    ///     nenhuma reentrega vai mudar o resultado, e insistir só consome a política de retentativa
    ///     até jogar a mensagem na dead-letter. Registre em Warning/Error e retorne.
    ///   </item>
    /// </list>
    /// O <see cref="LoggingEmailSender"/> nunca falha, então isso só passa a morder quando alguém
    /// plugar um provedor SMTP de verdade — que é exatamente quando ninguém vai lembrar.
    /// </remarks>
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}
