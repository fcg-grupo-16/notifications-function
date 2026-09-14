namespace Fcg.Notifications.Function.Contatos;

/// <summary>
/// Resolve o endereço de e-mail de um usuário a partir do seu identificador.
/// </summary>
/// <remarks>
/// Existe porque o <c>PaymentProcessedEvent</c> carrega apenas o <c>UserId</c> — a confirmação de
/// compra era endereçada a um ObjectId (issue #9).
/// </remarks>
public interface IResolvedorDeContato
{
    /// <summary>
    /// Devolve o e-mail do usuário.
    /// </summary>
    /// <exception cref="ContatoIndisponivelException">
    /// Quando o contato não pôde ser resolvido por falha de dependência. LANÇAR é deliberado — ver
    /// a implementação.
    /// </exception>
    Task<string> ResolverEmailAsync(string userId, CancellationToken ct = default);
}

/// <summary>
/// Falha TRANSITÓRIA ao resolver o contato: o users-api está fora, lento ou respondeu erro.
/// </summary>
/// <remarks>
/// Deve subir e deixar o host reentregar — a próxima tentativa pode ter sucesso.
/// </remarks>
public sealed class ContatoIndisponivelException(string mensagem, Exception? inner = null)
    : Exception(mensagem, inner);

/// <summary>
/// O usuário NÃO EXISTE no users-api (404).
/// </summary>
/// <remarks>
/// <para>
/// Falha DETERMINÍSTICA, e a distinção veio de uma medição no cluster: um usuário apagado entre a
/// compra e o processamento da mensagem fez a Function bater cinco vezes no mesmo 404 e seguir rumo
/// à dead-letter. Reentregar não muda o resultado — quem não existe nunca vai ter e-mail.
/// </para>
/// <para>
/// Quem trata deve registrar e seguir (ack), não reentregar.
/// </para>
/// </remarks>
public sealed class ContatoNaoEncontradoException(string mensagem) : Exception(mensagem);
