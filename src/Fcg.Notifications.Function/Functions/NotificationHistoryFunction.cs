using System.Net;
using Fcg.Notifications.Function.Persistence;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Fcg.Notifications.Function.Functions;

/// <summary>
/// Consulta do histórico de notificações enviadas: <c>GET /api/v1/notificacoes?limit=50</c>. Porta
/// o endpoint homônimo do <c>notifications-api</c>. Exige a chave da função no header
/// <c>x-functions-key</c>.
/// </summary>
public sealed class NotificationHistoryFunction
{
    private const int LimiteMaximo = 200;
    private const int LimitePadrao = 50;

    private readonly INotificationHistoryStore _history;

    public NotificationHistoryFunction(INotificationHistoryStore history) => _history = history;

    [Function(nameof(NotificationHistoryFunction))]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "v1/notificacoes")]
        HttpRequestData request,
        CancellationToken ct)
    {
        var registros = await _history.GetRecentAsync(LerLimite(request.Url.Query), ct);

        var response = request.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(registros, ct);
        return response;
    }

    /// <summary>
    /// Lê <c>?limit=</c> da query string, limitado a <see cref="LimiteMaximo"/>. Ausente ou
    /// inválido devolve <see cref="LimitePadrao"/>.
    /// </summary>
    internal static int LerLimite(string query)
    {
        var bruto = System.Web.HttpUtility.ParseQueryString(query)["limit"];

        return int.TryParse(bruto, out var informado)
            ? Math.Clamp(informado, 1, LimiteMaximo)
            : LimitePadrao;
    }
}
