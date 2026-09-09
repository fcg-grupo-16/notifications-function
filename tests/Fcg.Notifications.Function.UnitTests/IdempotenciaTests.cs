using Fcg.Notifications.Function.Email;
using Fcg.Notifications.Function.Functions;
using Fcg.Notifications.Function.Idempotency;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Fcg.Notifications.Function.UnitTests;

/// <summary>
/// Testes do store de idempotência contra um Redis DE VERDADE, em container.
/// </summary>
/// <remarks>
/// Um duplo em memória não provaria nada aqui: o que está sendo testado é justamente a
/// atomicidade do <c>SET NX EX</c> no servidor e a durabilidade da chave — as duas propriedades
/// que o dicionário do <c>notifications-api</c> não tinha.
/// </remarks>
public sealed class RedisProcessedMessageStoreTests : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7.4.1-alpine")
        .Build();

    private IConnectionMultiplexer _multiplexer = null!;
    private RedisProcessedMessageStore _store = null!;

    public async Task InitializeAsync()
    {
        await _redis.StartAsync();
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        _store = new RedisProcessedMessageStore(
            _multiplexer, NullLogger<RedisProcessedMessageStore>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _multiplexer.DisposeAsync();
        await _redis.DisposeAsync();
    }

    [Fact(DisplayName = "Primeira vez marca a chave; segunda vez é reconhecida como duplicata")]
    public async Task TryMark_PrimeiraVezTrue_SegundaVezFalse()
    {
        const string chave = "usuario-novo";

        Assert.True(await _store.TryMarkAsProcessedAsync("UserCreatedEvent", chave));
        Assert.False(await _store.TryMarkAsProcessedAsync("UserCreatedEvent", chave));
        Assert.False(await _store.TryMarkAsProcessedAsync("UserCreatedEvent", chave));
    }

    [Fact(DisplayName = "O tipo do evento faz parte da chave: mesmo id em eventos diferentes é independente")]
    public async Task TryMark_TiposDiferentesMesmaChave_SaoIndependentes()
    {
        // O mesmo OrderId pode aparecer num PaymentProcessedEvent e, no futuro, num
        // OrderRefundedEvent — deduplicá-los juntos suprimiria a segunda notificação.
        const string id = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

        Assert.True(await _store.TryMarkAsProcessedAsync("PaymentProcessedEvent", id));
        Assert.True(await _store.TryMarkAsProcessedAsync("OrderRefundedEvent", id));
    }

    [Fact(DisplayName = "50 chamadas CONCORRENTES: exatamente UMA marca a chave (SET NX é atômico)")]
    public async Task TryMark_ChamadasConcorrentes_ApenasUmaRetornaTrue()
    {
        // Simula o cenário real de maxReplicaCount: 5 — a mesma mensagem chegando a vários pods.
        // Com um "EXISTS depois SET", várias destas chamadas retornariam true e o cliente receberia
        // vários e-mails. Com SET NX no servidor, exatamente uma.
        var resultados = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ =>
            _store.TryMarkAsProcessedAsync("UserCreatedEvent", "usuario-concorrente")));

        Assert.Equal(1, resultados.Count(r => r));
    }

    [Fact(DisplayName = "A chave nasce com TTL — não pode ser eterna")]
    public async Task TryMark_DefineTtl()
    {
        // Sem TTL o Redis acumularia uma chave por usuário e por pedido para sempre, virando banco
        // em vez de cache.
        await _store.TryMarkAsProcessedAsync("UserCreatedEvent", "usuario-ttl");

        var ttl = await _multiplexer.GetDatabase()
            .KeyTimeToLiveAsync("fcg:notifications:processed:UserCreatedEvent:usuario-ttl");

        Assert.NotNull(ttl);
        Assert.InRange(ttl!.Value, TimeSpan.FromDays(6.9), TimeSpan.FromDays(7));
    }

    [Fact(DisplayName = "A chave usa o prefixo do serviço (isolamento de keyspace no Redis compartilhado)")]
    public async Task TryMark_UsaOPrefixoDoServico()
    {
        await _store.TryMarkAsProcessedAsync("UserCreatedEvent", "usuario-prefixo");

        var existe = await _multiplexer.GetDatabase()
            .KeyExistsAsync("fcg:notifications:processed:UserCreatedEvent:usuario-prefixo");

        Assert.True(existe);
    }

    [Fact(DisplayName = "A marcação SOBREVIVE ao reinício do processo (é o que o store em memória não fazia)")]
    public async Task TryMark_SobreviveAoReinicioDoProcesso()
    {
        // Este é o teste que justifica a issue: a Function escala a zero, o processo morre e uma
        // reentrega chega num processo NOVO. Com o ConcurrentDictionary do notifications-api, a
        // segunda chamada abaixo retornaria true e o e-mail sairia de novo.
        const string chave = "usuario-sobrevivente";

        Assert.True(await _store.TryMarkAsProcessedAsync("UserCreatedEvent", chave));

        // Simula processo novo: multiplexer e store completamente novos, mesmo Redis.
        await using var outroMultiplexer = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var outroStore = new RedisProcessedMessageStore(
            outroMultiplexer, NullLogger<RedisProcessedMessageStore>.Instance);

        Assert.False(await outroStore.TryMarkAsProcessedAsync("UserCreatedEvent", chave));
    }

    [Fact(DisplayName = "Redis indisponível: o store LANÇA (fail-closed), não libera o envio")]
    public async Task TryMark_RedisIndisponivel_Lanca()
    {
        // Contrato de teste, não de implementação: se alguém acrescentar um try/catch aqui achando
        // que está "deixando mais robusto", reintroduz o risco de e-mail duplicado.
        var opcoes = ConfigurationOptions.Parse("127.0.0.1:6399");   // porta morta
        opcoes.AbortOnConnectFail = false;
        opcoes.ConnectTimeout = 500;
        opcoes.SyncTimeout = 500;
        opcoes.ConnectRetry = 1;

        await using var semRedis = await ConnectionMultiplexer.ConnectAsync(opcoes);
        var store = new RedisProcessedMessageStore(
            semRedis, NullLogger<RedisProcessedMessageStore>.Instance);

        await Assert.ThrowsAnyAsync<Exception>(
            () => store.TryMarkAsProcessedAsync("UserCreatedEvent", "qualquer"));
    }
}

/// <summary>
/// Testes de como as Functions usam o store — inclusive o detalhe sutil do caminho rejeitado.
/// </summary>
public sealed class FunctionsIdempotenciaTests
{
    private const string UrnUserCreated = "urn:message:Fcg.Contracts.Events:UserCreatedEvent";
    private const string UrnPaymentProcessed = "urn:message:Fcg.Contracts.Events:PaymentProcessedEvent";

    private static string EnvelopeUserCreated(string userId) =>
        "{\"messageType\":[\"" + UrnUserCreated + "\"],"
        + "\"message\":{\"userId\":\"" + userId + "\",\"nome\":\"Maria\",\"email\":\"maria@fcg.com\"}}";

    private static string EnvelopePagamento(string status) =>
        "{\"messageType\":[\"" + UrnPaymentProcessed + "\"],"
        + "\"message\":{\"orderId\":\"3f2504e0-4f89-11d3-9a0c-0305e82c3301\",\"userId\":\"u-1\","
        + "\"gameId\":\"g\",\"price\":\"29.90\",\"status\":\"" + status + "\"}}";

    [Fact(DisplayName = "Evento duplicado NÃO gera segundo e-mail")]
    public async Task UserCreated_Duplicado_NaoEnviaSegundoEmail()
    {
        var sender = new EmailSenderEspiao();
        var store = new StoreEspiao();
        var funcao = new UserCreatedFunction(NullLogger<UserCreatedFunction>.Instance, sender, store);

        await funcao.RunAsync(EnvelopeUserCreated("u-1"), CancellationToken.None);
        await funcao.RunAsync(EnvelopeUserCreated("u-1"), CancellationToken.None);
        await funcao.RunAsync(EnvelopeUserCreated("u-1"), CancellationToken.None);

        Assert.Single(sender.Enviados);
    }

    [Fact(DisplayName = "A chave é marcada ANTES do envio (at-most-once)")]
    public async Task UserCreated_MarcaAntesDeEnviar()
    {
        // Com o sender lançando, o e-mail não sai — mas a chave já tem de estar marcada, que é
        // exatamente o trade-off documentado: preferimos perder um e-mail a duplicá-lo.
        var sender = new EmailSenderEspiao(new InvalidOperationException("falhou"));
        var store = new StoreEspiao();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new UserCreatedFunction(NullLogger<UserCreatedFunction>.Instance, sender, store)
                .RunAsync(EnvelopeUserCreated("u-1"), CancellationToken.None));

        Assert.Contains("UserCreatedEvent:u-1", store.Marcadas);
    }

    [Fact(DisplayName = "Falha do store PROPAGA — sem idempotência, não se envia (fail-closed)")]
    public async Task UserCreated_StoreFalha_NaoEnviaEPropaga()
    {
        var sender = new EmailSenderEspiao();
        var store = new StoreEspiao(erroParaLancar: new RedisConnectionException(
            ConnectionFailureType.UnableToConnect, "redis fora"));

        await Assert.ThrowsAsync<RedisConnectionException>(() =>
            new UserCreatedFunction(NullLogger<UserCreatedFunction>.Instance, sender, store)
                .RunAsync(EnvelopeUserCreated("u-1"), CancellationToken.None));

        Assert.Empty(sender.Enviados);
    }

    [Fact(DisplayName = "Pagamento REJEITADO não consome a chave do OrderId")]
    public async Task PaymentProcessed_Rejeitado_NaoConsomeAChave()
    {
        // O detalhe sutil: se o evento rejeitado gastasse a chave, a confirmação legítima de um
        // reprocessamento posterior (rejeitado -> aprovado, mesmo pedido) seria bloqueada.
        var sender = new EmailSenderEspiao();
        var store = new StoreEspiao();
        var funcao = new PaymentProcessedFunction(
            NullLogger<PaymentProcessedFunction>.Instance, sender, store);

        await funcao.RunAsync(EnvelopePagamento("Rejected"), CancellationToken.None);

        Assert.Empty(store.Marcadas);
        Assert.Empty(sender.Enviados);

        // O MESMO pedido, agora aprovado, ainda consegue notificar.
        await funcao.RunAsync(EnvelopePagamento("Approved"), CancellationToken.None);

        Assert.Single(sender.Enviados);
    }

    [Fact(DisplayName = "Confirmação de compra duplicada NÃO gera segundo e-mail")]
    public async Task PaymentProcessed_Duplicado_NaoEnviaSegundoEmail()
    {
        var sender = new EmailSenderEspiao();
        var funcao = new PaymentProcessedFunction(
            NullLogger<PaymentProcessedFunction>.Instance, sender, new StoreEspiao());

        await funcao.RunAsync(EnvelopePagamento("Approved"), CancellationToken.None);
        await funcao.RunAsync(EnvelopePagamento("Approved"), CancellationToken.None);

        Assert.Single(sender.Enviados);
    }
}
