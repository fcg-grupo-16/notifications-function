using System.Diagnostics;
using System.Text.Json;
using Fcg.Notifications.Function.Messaging;

namespace Fcg.Notifications.Function.Observability;

/// <summary>
/// Restaura o contexto de trace W3C propagado nos headers do envelope do MassTransit, para o span da
/// Function entrar no mesmo trace do fluxo que publicou o evento. Best-effort: sem header válido, a
/// função segue sem correlação.
/// </summary>
public static class TraceContextRestorer
{
    /// <summary>Nome do <see cref="Source"/>, para registrar no OpenTelemetry com <c>.AddSource(...)</c>.</summary>
    public const string SourceName = "Fcg.Notifications.Function";

    /// <summary>
    /// <c>ActivitySource</c> da Function. Sem <c>.AddSource(SourceName)</c> no OpenTelemetry, os spans
    /// são descartados.
    /// </summary>
    public static readonly ActivitySource Source = new(SourceName);

    /// <summary>
    /// Headers aceitos, em ordem de preferência. <c>MT-Activity-Id</c> é o que o MassTransit 8 grava
    /// (medido no envelope real publicado pelo users-api); <c>traceparent</c> é o nome padrão W3C.
    /// </summary>
    private static readonly string[] HeaderNames = ["MT-Activity-Id", "traceparent"];

    /// <summary>
    /// Cria um span <see cref="ActivityKind.Consumer"/> filho do contexto propagado. Devolve
    /// <c>null</c> sem contexto válido ou sem listener registrado.
    /// </summary>
    public static Activity? StartChildActivity<T>(MassTransitEnvelope<T> envelope, string operationName)
        where T : class
    {
        var traceparent = ExtrairTraceparent(envelope.Headers);

        if (traceparent is null || !ActivityContext.TryParse(traceparent, null, out var parentContext))
        {
            return null;
        }

        return Source.StartActivity(operationName, ActivityKind.Consumer, parentContext);
    }

    private static string? ExtrairTraceparent(Dictionary<string, JsonElement>? headers)
    {
        if (headers is null)
        {
            return null;
        }

        foreach (var nome in HeaderNames)
        {
            var par = headers.FirstOrDefault(h => string.Equals(h.Key, nome, StringComparison.OrdinalIgnoreCase));

            if (par.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(par.Value.GetString()))
            {
                return par.Value.GetString();
            }
        }

        return null;
    }
}
