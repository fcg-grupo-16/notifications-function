using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Fcg.Notifications.Function.Idempotency;

/// <summary>
/// Store de idempotência durável em Redis, usando <c>SET NX EX</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Atomicidade.</b> <see cref="IDatabaseAsync.StringSetAsync"/> com
/// <see cref="When.NotExists"/> emite <c>SET chave valor NX EX ttl</c>: uma única operação atômica
/// no servidor, que grava só se a chave não existir e já aplica o TTL. É o equivalente distribuído
/// do <c>ConcurrentDictionary.TryAdd</c> que o <c>notifications-api</c> usava. Fazer
/// <c>KeyExists</c> seguido de <c>StringSet</c> seria uma corrida: dois pods passariam os dois pelo
/// teste e enviariam dois e-mails.
/// </para>
/// <para>
/// <b>Fail-CLOSED — ao contrário do cache dos outros serviços.</b> Se o Redis estiver
/// indisponível, este método LANÇA, a Function deixa a exceção subir, o host reentrega a mensagem
/// e, esgotadas as tentativas, ela vai para a dead-letter. É o oposto do <c>RedisCacheService</c>
/// de <c>users-api</c>/<c>catalog-api</c>, que é fail-open de propósito. A diferença é o custo do
/// erro: lá, cache fora significa consulta mais lenta; aqui, idempotência fora significa
/// <b>e-mail duplicado para o cliente</b> — efeito colateral externo e irreversível. Preferimos
/// não processar a processar duas vezes.
/// </para>
/// </remarks>
public sealed class RedisProcessedMessageStore : IProcessedMessageStore
{
    /// <summary>
    /// Janela de deduplicação.
    /// </summary>
    /// <remarks>
    /// Cobre com folga a janela realista de reentrega: retentativa do host (segundos), requeue da
    /// extensão do RabbitMQ (minutos) e reprocessamento manual da dead-letter (horas ou dias). Ao
    /// mesmo tempo impede o keyspace de crescer sem limite — sem TTL, o Redis acumularia uma chave
    /// por usuário e por pedido para sempre, e viraria banco em vez de cache.
    /// <para>
    /// <b>Trade-off explícito:</b> reprocessar a dead-letter DEPOIS de 7 dias reenvia o e-mail.
    /// Para boas-vindas e confirmação de compra isso é aceitável.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(7);

    private readonly IConnectionMultiplexer _multiplexer;
    private readonly ILogger<RedisProcessedMessageStore> _logger;
    private readonly string _prefixoDeChave;

    public RedisProcessedMessageStore(
        IConnectionMultiplexer multiplexer,
        ILogger<RedisProcessedMessageStore> logger,
        string prefixoDeChave = "fcg:notifications:")
    {
        _multiplexer = multiplexer;
        _logger = logger;
        _prefixoDeChave = prefixoDeChave;
    }

    /// <inheritdoc />
    public async Task<bool> TryMarkAsProcessedAsync(
        string messageType, string naturalKey, CancellationToken ct = default)
    {
        // O TIPO faz parte da chave: o mesmo identificador natural pode aparecer em eventos
        // diferentes (um OrderId em PaymentProcessed e, no futuro, num OrderRefunded), e eles
        // precisam ser deduplicados de forma independente.
        var chave = $"{_prefixoDeChave}processed:{messageType}:{naturalKey}";

        // Deliberadamente SEM try/catch: fail-closed (ver <remarks> da classe). Engolir a falha e
        // devolver true faria a Function enviar e-mail sem nenhuma garantia de unicidade.
        var inedito = await _multiplexer.GetDatabase()
            .StringSetAsync(chave, DateTimeOffset.UtcNow.ToString("O"), Ttl, When.NotExists);

        if (!inedito)
        {
            _logger.LogInformation(
                "Evento {MessageType} com chave {NaturalKey} já processado; ignorando (idempotência).",
                messageType, naturalKey);
        }

        return inedito;
    }
}
