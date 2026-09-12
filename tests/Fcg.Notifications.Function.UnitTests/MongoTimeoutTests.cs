using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Fcg.Notifications.Function.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace Fcg.Notifications.Function.UnitTests;

/// <summary>
/// Testes do tempo que o histórico leva para desistir quando o MongoDB está fora do ar.
/// </summary>
public sealed class MongoTimeoutTests
{
    [Fact(DisplayName = "O cliente do histórico usa timeouts curtos")]
    public void Criar_AplicaTimeoutsCurtos()
    {
        var settings = MongoClientSettingsFactory.Criar("mongodb://localhost:27017/?replicaSet=rs0");

        Assert.Equal(TimeSpan.FromSeconds(3), settings.ServerSelectionTimeout);
        Assert.Equal(TimeSpan.FromSeconds(3), settings.ConnectTimeout);
        Assert.Equal("rs0", settings.ReplicaSetName);
    }

    [Fact(DisplayName = "Com o Mongo fora do ar, gravar o histórico falha em segundos, não em um minuto")]
    public async Task Save_MongoIndisponivel_FalhaRapido()
    {
        var client = new MongoClient(
            MongoClientSettingsFactory.Criar($"mongodb://127.0.0.1:{ObterPortaLivre()}"));
        var store = new MongoNotificationHistoryStore(
            client.GetDatabase("notificationsdb"), NullLogger<MongoNotificationHistoryStore>.Instance);

        var cronometro = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(new NotificationRecord
        {
            Type = "UserCreatedEvent",
            NaturalKey = "u-timeout",
            SentAtUtc = DateTime.UtcNow
        }));
        cronometro.Stop();

        // Com o default do driver eram dois timeouts de 30 s (índice + insert).
        Assert.True(cronometro.Elapsed < TimeSpan.FromSeconds(15),
            $"levou {cronometro.Elapsed.TotalSeconds:F1} s");
    }

    private static int ObterPortaLivre()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
