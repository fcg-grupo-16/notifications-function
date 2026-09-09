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
