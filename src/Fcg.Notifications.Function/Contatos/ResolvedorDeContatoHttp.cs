using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

namespace Fcg.Notifications.Function.Contatos;

/// <summary>
/// Resolve o contato consultando o endpoint interno do <c>users-api</c>, com cache no Redis.
/// </summary>
/// <remarks>
/// <para>
/// <b>Token de SERVIÇO, com chave própria.</b> A Function assina o próprio JWT com
/// <c>ServiceAuth:SecretKey</c> — que é DIFERENTE da <c>JwtSettings:SecretKey</c> da plataforma. A
/// chave dos usuários é compartilhada entre users-api, catalog-api e o Kong: quem a tem assina
/// qualquer token, inclusive de Administrador. Ver ADR 0007 no repositório orchestration.
/// </para>
/// <para>
/// <b>Cache no Redis de idempotência, não no de cache.</b> É o Redis dedicado e durável da ADR 0006,
/// com <c>noeviction</c>. Uma chave de contato com TTL não compete com as de idempotência por
/// memória de forma relevante, e evita uma segunda dependência. O prefixo é distinto
/// (<c>contato:</c>) para a inspeção manual continuar legível.
/// </para>
/// <para>
/// <b>Falha de dependência LANÇA, e isso é uma escolha.</b> O critério da issue pede "não derrubar o
/// processamento da mensagem". Lançar não derruba: devolve a mensagem ao host, que reentrega e, no
/// limite, manda para a dead-letter — de onde ela pode ser reprocessada quando o users-api voltar.
/// A alternativa (seguir sem enviar) daria ACK numa mensagem cuja confirmação de compra nunca
/// chegaria ao cliente, e sem registro recuperável. Entre adiar e perder em silêncio, adiamos.
/// </para>
/// <para>
/// <b>Com UMA exceção: o 404.</b> Usuário inexistente é falha determinística, tem tipo próprio
/// (<see cref="ContatoNaoEncontradoException"/>) e não deve ser reentregue — reentregar bate no mesmo
/// 404 até a dead-letter.
/// </para>
/// </remarks>
public sealed class ResolvedorDeContatoHttp : IResolvedorDeContato
{
    /// <summary>TTL do contato em cache.</summary>
    /// <remarks>
    /// Curto o bastante para uma troca de e-mail se refletir em minutos, longo o bastante para uma
    /// rajada de confirmações do mesmo usuário não gerar uma consulta por mensagem.
    /// </remarks>
    private static readonly TimeSpan TtlDoCache = TimeSpan.FromMinutes(30);

    /// <summary>Validade do token de serviço — só precisa cobrir a chamada.</summary>
    private static readonly TimeSpan ValidadeDoToken = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<ResolvedorDeContatoHttp> _logger;
    private readonly OpcoesDeContato _opcoes;
    private readonly string _prefixoDeChave;

    public ResolvedorDeContatoHttp(
        HttpClient http,
        IConnectionMultiplexer redis,
        ILogger<ResolvedorDeContatoHttp> logger,
        OpcoesDeContato opcoes,
        string prefixoDeChave = "fcg:notifications:")
    {
        _http = http;
        _redis = redis;
        _logger = logger;
        _opcoes = opcoes;
        _prefixoDeChave = prefixoDeChave;
    }

    /// <inheritdoc />
    public async Task<string> ResolverEmailAsync(string userId, CancellationToken ct = default)
    {
        var chave = $"{_prefixoDeChave}contato:{userId}";

        // O cache é fail-OPEN, ao contrário do store de idempotência: Redis fora aqui significa uma
        // consulta HTTP a mais, não e-mail duplicado.
        try
        {
            var emCache = await _redis.GetDatabase().StringGetAsync(chave);
            if (emCache.HasValue)
            {
                return emCache!;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache de contato indisponível; consultando o users-api direto.");
        }

        var email = await ConsultarAsync(userId, ct);

        try
        {
            await _redis.GetDatabase().StringSetAsync(chave, email, TtlDoCache);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao gravar o contato em cache; seguindo assim mesmo.");
        }

        return email;
    }

    private async Task<string> ConsultarAsync(string userId, CancellationToken ct)
    {
        using var requisicao = new HttpRequestMessage(HttpMethod.Get, $"api/v1/usuarios/{userId}/contato");
        requisicao.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GerarTokenDeServico());

        HttpResponseMessage resposta;

        try
        {
            resposta = await _http.SendAsync(requisicao, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ContatoIndisponivelException(
                $"Falha ao consultar o contato do usuário {userId} no users-api.", ex);
        }

        using (resposta)
        {
            // 404 é DETERMINÍSTICO e tem tipo PRÓPRIO. Medido no cluster: um usuário apagado entre
            // a compra e o processamento fez esta chamada bater cinco vezes no mesmo 404, rumo à
            // dead-letter. Reentregar não muda o resultado.
            if (resposta.StatusCode == HttpStatusCode.NotFound)
            {
                throw new ContatoNaoEncontradoException($"Usuário {userId} não encontrado no users-api.");
            }

            if (!resposta.IsSuccessStatusCode)
            {
                throw new ContatoIndisponivelException(
                    $"users-api respondeu {(int)resposta.StatusCode} ao consultar o contato do usuário {userId}.");
            }

            var contato = await resposta.Content.ReadFromJsonAsync<ContatoResponse>(ct);

            if (string.IsNullOrWhiteSpace(contato?.Email))
            {
                throw new ContatoIndisponivelException(
                    $"users-api devolveu contato vazio para o usuário {userId}.");
            }

            return contato.Email;
        }
    }

    private string GerarTokenDeServico()
    {
        var credenciais = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opcoes.SecretKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _opcoes.Issuer,
            audience: _opcoes.Audience,
            claims: [new Claim(ClaimTypes.Role, "Servico")],
            expires: DateTime.UtcNow.Add(ValidadeDoToken),
            signingCredentials: credenciais);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed record ContatoResponse(string Email);
}
