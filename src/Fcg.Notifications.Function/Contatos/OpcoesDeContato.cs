namespace Fcg.Notifications.Function.Contatos;

/// <summary>
/// Credencial de serviço usada para consultar o contato no <c>users-api</c>.
/// </summary>
/// <remarks>
/// Existe para o <see cref="ResolvedorDeContatoHttp"/> poder ser ativado pelo cliente tipado do
/// <c>IHttpClientFactory</c>: com três <c>string</c> soltas no construtor, a ativação por DI não
/// teria como resolvê-las.
/// </remarks>
public sealed record OpcoesDeContato(string SecretKey, string Issuer, string Audience);
