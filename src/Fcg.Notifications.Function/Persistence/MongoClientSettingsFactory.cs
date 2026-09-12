using MongoDB.Driver;

namespace Fcg.Notifications.Function.Persistence;

/// <summary>
/// Configurações do cliente MongoDB do histórico, com timeouts curtos. O histórico é best-effort, e o
/// default de 30 s do driver seguraria cada mensagem enquanto o Mongo estiver fora do ar.
/// </summary>
public static class MongoClientSettingsFactory
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public static MongoClientSettings Criar(string connectionString)
    {
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        settings.ServerSelectionTimeout = Timeout;
        settings.ConnectTimeout = Timeout;
        return settings;
    }
}
