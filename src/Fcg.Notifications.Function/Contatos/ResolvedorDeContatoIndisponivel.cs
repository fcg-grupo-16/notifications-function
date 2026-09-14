namespace Fcg.Notifications.Function.Contatos;

/// <summary>
/// Implementação usada quando a consulta de contato NÃO está configurada.
/// </summary>
/// <remarks>
/// <para>
/// Existe para que a Function sempre INDEXE. Se <c>IResolvedorDeContato</c> pudesse faltar na DI, o
/// host falharia ao construir a <c>PaymentProcessedFunction</c> e o smoke test do CI acusaria
/// "função não indexada" — o container do CI sobe sem <c>UsersApi__BaseUrl</c>, então esse caminho é
/// o NORMAL lá, não uma exceção.
/// </para>
/// <para>
/// Mesmo princípio adotado no <c>users-api</c> para o esquema de autenticação de serviço: falhar no
/// USO, nunca na inicialização. Configuração ausente indisponibiliza um caminho; não derruba o
/// componente inteiro.
/// </para>
/// </remarks>
public sealed class ResolvedorDeContatoIndisponivel : IResolvedorDeContato
{
    /// <inheritdoc />
    public Task<string> ResolverEmailAsync(string userId, CancellationToken ct = default) =>
        throw new ContatoIndisponivelException(
            "Consulta de contato não configurada: defina UsersApi:BaseUrl e ServiceAuth:SecretKey. "
            + "Sem elas a confirmação de compra não tem como descobrir o e-mail do comprador.");
}
