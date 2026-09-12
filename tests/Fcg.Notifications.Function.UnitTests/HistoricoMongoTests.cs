using Fcg.Notifications.Function.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace Fcg.Notifications.Function.UnitTests;

/// <summary>
/// Container MongoDB compartilhado por toda a classe de teste — um container, não um por método.
/// </summary>
public sealed class MongoFixture : IAsyncLifetime
{
    private readonly MongoDbContainer _container = new MongoDbBuilder("mongo:7.0").Build();

    public IMongoDatabase Database { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Database = new MongoClient(_container.GetConnectionString()).GetDatabase("notificationsdb");
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

/// <summary>
/// Testes do histórico contra um MongoDB de verdade, em container.
/// </summary>
public sealed class HistoricoMongoTests : IClassFixture<MongoFixture>
{
    private readonly IMongoDatabase _database;
    private readonly MongoNotificationHistoryStore _store;

    public HistoricoMongoTests(MongoFixture fixture)
    {
        _database = fixture.Database;
        _store = new MongoNotificationHistoryStore(
            _database, NullLogger<MongoNotificationHistoryStore>.Instance);
    }

    private IMongoCollection<BsonDocument> Collection =>
        _database.GetCollection<BsonDocument>(MongoNotificationHistoryStore.CollectionName);

    private static NotificationRecord Registro(string naturalKey, DateTime? sentAtUtc = null) =>
        new()
        {
            Type = "UserCreatedEvent",
            Recipient = $"{naturalKey}@fcg.com",
            Subject = "Bem-vindo(a)",
            Body = $"Olá, {naturalKey}!",
            NaturalKey = naturalKey,
            SentAtUtc = sentAtUtc ?? DateTime.UtcNow
        };

    [Fact(DisplayName = "Save grava o registro na collection notifications")]
    public async Task Save_PersisteORegistro()
    {
        await _store.SaveAsync(Registro("round-trip"));

        var documento = await Collection
            .Find(Builders<BsonDocument>.Filter.Eq("NaturalKey", "round-trip"))
            .SingleAsync();

        Assert.Equal("UserCreatedEvent", documento["Type"].AsString);
        Assert.Equal("round-trip@fcg.com", documento["Recipient"].AsString);
        Assert.Equal("Bem-vindo(a)", documento["Subject"].AsString);
    }

    [Fact(DisplayName = "GetRecent devolve do mais novo para o mais antigo")]
    public async Task GetRecent_OrdenaPorDataDesc()
    {
        var agora = DateTime.UtcNow;
        await _store.SaveAsync(Registro("ordem-antigo", agora.AddHours(-2)));
        await _store.SaveAsync(Registro("ordem-novo", agora.AddHours(-1)));

        var recentes = await _store.GetRecentAsync(500);
        var apenasDesteTeste = recentes.Where(r => r.NaturalKey.StartsWith("ordem-")).ToList();

        Assert.Equal("ordem-novo", apenasDesteTeste[0].NaturalKey);
        Assert.Equal("ordem-antigo", apenasDesteTeste[1].NaturalKey);
    }

    [Fact(DisplayName = "GetRecent respeita o limite pedido")]
    public async Task GetRecent_RespeitaOLimite()
    {
        for (var i = 0; i < 5; i++)
        {
            await _store.SaveAsync(Registro($"limite-{i}"));
        }

        Assert.Equal(3, (await _store.GetRecentAsync(3)).Count);
    }

    [Fact(DisplayName = "GarantirIndices pode ser chamado mais de uma vez")]
    public async Task GarantirIndices_EhIdempotente()
    {
        await _store.GarantirIndicesAsync();

        var excecao = await Record.ExceptionAsync(() => _store.GarantirIndicesAsync());

        Assert.Null(excecao);
    }

    [Fact(DisplayName = "O índice descendente de SentAtUtc é criado")]
    public async Task GarantirIndices_CriaOIndiceDeSentAtUtc()
    {
        await _store.GarantirIndicesAsync();

        var indices = await (await Collection.Indexes.ListAsync()).ToListAsync();

        var indice = Assert.Single(indices, i => i["name"].AsString == "ix_sentAtUtc");
        Assert.Equal(-1, indice["key"]["SentAtUtc"].ToInt32());
    }

    [Fact(DisplayName = "Documento gravado pela Fase 2 continua legível pela Function")]
    public async Task NotificationRecord_FormatoCompativelComAFase2()
    {
        var legado = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["Type"] = "UserCreatedEvent",
            ["Recipient"] = "legado@fcg.com",
            ["Subject"] = "Bem-vindo(a) à FIAP Cloud Games",
            ["Body"] = "Olá, Legado!",
            ["NaturalKey"] = "usuario-da-fase-2",
            ["SentAtUtc"] = DateTime.UtcNow.AddDays(-30)
        };

        await Collection.InsertOneAsync(legado);

        var recuperado = Assert.Single(
            await _store.GetRecentAsync(500), r => r.NaturalKey == "usuario-da-fase-2");

        Assert.Equal("legado@fcg.com", recuperado.Recipient);
        Assert.Equal("UserCreatedEvent", recuperado.Type);
        Assert.Equal("Olá, Legado!", recuperado.Body);
    }
}
