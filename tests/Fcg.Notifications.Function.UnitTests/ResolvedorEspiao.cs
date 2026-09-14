using Fcg.Notifications.Function.Contatos;

namespace Fcg.Notifications.Function.UnitTests;

/// <summary>
/// Resolvedor de contato de teste. Devolve um e-mail fixo, ou lança, conforme configurado.
/// </summary>
internal sealed class ResolvedorEspiao(string email = "comprador@fcg.com", Exception? falha = null)
    : IResolvedorDeContato
{
    public List<string> Consultados { get; } = [];

    public Task<string> ResolverEmailAsync(string userId, CancellationToken ct = default)
    {
        Consultados.Add(userId);

        return falha is not null ? Task.FromException<string>(falha) : Task.FromResult(email);
    }
}
