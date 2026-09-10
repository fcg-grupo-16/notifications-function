namespace Fcg.Notifications.Function.Messaging;

/// <summary>
/// Helpers para não vazar dado pessoal nem conteúdo não confiável no stdout.
/// </summary>
/// <remarks>
/// Os logs desta função vão para o stdout do container e, no cluster, para o backend de logs
/// compartilhado. Tudo o que entra aqui vem de uma mensagem publicada por outro sistema — é
/// conteúdo NÃO CONFIÁVEL e frequentemente contém dado pessoal (e-mail do usuário).
/// </remarks>
public static class LogSanitizer
{
    private const int TamanhoMaximoCorpo = 500;

    /// <summary>
    /// Indica se o valor é um destinatário de e-mail aceitável para prosseguir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Não é validação de RFC 5322 — é uma barreira contra os dois abusos concretos que o endereço
    /// permite hoje, vindo de um campo que qualquer publisher controla:
    /// </para>
    /// <list type="number">
    ///   <item>
    ///     <b>CR/LF.</b> <c>IsNullOrWhiteSpace</c> não barra quebra de linha INTERNA. Um
    ///     <c>vitima@fcg.com\r\nBcc: atacante@evil.com</c> passa hoje como injeção de LOG (forja
    ///     linhas inteiras no stdout) e, no dia em que plugarem um SMTP de verdade, vira injeção de
    ///     CABEÇALHO — exatamente o cenário que o contrato de <c>IEmailSender</c> antecipa.
    ///   </item>
    ///   <item>
    ///     <b>Tamanho.</b> Endereço absurdamente longo só serve para inflar log e payload.
    ///   </item>
    /// </list>
    /// </remarks>
    /// <summary>
    /// Indica se o identificador é aceitável para virar chave de idempotência no Redis.
    /// </summary>
    /// <remarks>
    /// O Redis aceita chave de até 512 MB, e o <c>UserId</c>/<c>OrderId</c> vem de um campo que
    /// qualquer publisher controla. Medido: um id de 200.000 caracteres é aceito sem reclamação.
    /// Como o Redis é COMPARTILHADO com os caches de <c>users-api</c> e <c>catalog-api</c> e tem
    /// teto de 256 MB com evicção LRU, um id absurdo acelera o despejo de chaves alheias e degrada
    /// o cache de toda a plataforma. Mesmo raciocínio do limite aplicado ao destinatário de e-mail.
    /// </remarks>
    public static bool IdentificadorEhAceitavel(string? identificador) =>
        !string.IsNullOrWhiteSpace(identificador)
        && identificador.Length <= 128
        && identificador.AsSpan().IndexOfAny('\r', '\n') < 0;

    public static bool DestinatarioEhAceitavel(string? destinatario) =>
        !string.IsNullOrWhiteSpace(destinatario)
        && destinatario.Length <= 320                      // limite prático de endereço de e-mail
        && destinatario.AsSpan().IndexOfAny('\r', '\n') < 0;

    /// <summary>
    /// Mascara um e-mail preservando o suficiente para investigar (<c>ma***@fcg.com</c>).
    /// </summary>
    /// <remarks>
    /// O objetivo é permitir correlacionar "o e-mail do fulano falhou" sem despejar o endereço
    /// completo em todo log de nível Information. Para achar o registro exato existe o
    /// <c>ConversationId</c>, que não é dado pessoal.
    /// </remarks>
    public static string MascararEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return "(vazio)";
        }

        var arroba = email.IndexOf('@');

        // Sem "@" não dá para saber o que é local-part; mascara quase tudo por segurança.
        if (arroba <= 0)
        {
            return email.Length <= 2 ? "***" : email[..2] + "***";
        }

        var local = email[..arroba];
        var dominio = email[arroba..];
        var prefixo = local.Length <= 2 ? local : local[..2];

        return $"{prefixo}***{dominio}";
    }

    /// <summary>
    /// Corta o corpo da mensagem para log, sem partir um par surrogate no meio.
    /// </summary>
    /// <remarks>
    /// Um corte cego com <c>body[..500]</c> pode terminar num surrogate alto solitário quando há
    /// emoji na fronteira, e o log sai com <c>�</c>. <see cref="StringInfo"/> não resolve sozinho o
    /// caso, então recuamos um caractere quando o corte cai no meio do par.
    /// </remarks>
    public static string TruncarCorpo(string? corpo)
    {
        if (corpo is null)
        {
            return "(vazio)";
        }

        if (corpo.Length <= TamanhoMaximoCorpo)
        {
            return corpo;
        }

        var fim = TamanhoMaximoCorpo;

        if (char.IsHighSurrogate(corpo[fim - 1]))
        {
            fim--;
        }

        return corpo[..fim] + "…";
    }
}
