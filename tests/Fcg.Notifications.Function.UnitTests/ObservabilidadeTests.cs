using System.Diagnostics;
using System.Text.Json;
using Fcg.Contracts.Events;
using Fcg.Notifications.Function.Functions;
using Fcg.Notifications.Function.Messaging;
using Fcg.Notifications.Function.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fcg.Notifications.Function.UnitTests;

/// <summary>
/// Logger de teste que guarda os escopos abertos.
/// </summary>
internal sealed class LoggerComEscopos : ILogger
{
    public List<object> Escopos { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        Escopos.Add(state);
        return null;
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
    }
}

/// <summary>
/// Testes da correlação de logs e traces com o fluxo que publicou o evento.
/// </summary>
public sealed class ObservabilidadeTests : IDisposable
{
    private const string TraceIdReal = "f16db9c722cc9550b16acbf47e1250df";

    /// <summary>
    /// Envelope capturado do broker, publicado pelo users-api com OpenTelemetry ativo (nome e e-mail
    /// trocados). O contexto de trace chega em <c>MT-Activity-Id</c>.
    /// </summary>
    private const string EnvelopeRealComTrace = """
        {"messageId":"01000000-e113-d9ff-573b-08df111a399e","conversationId":"01000000-e113-d9ff-5a66-08df111a39a0","messageType":["urn:message:Fcg.Contracts.Events:UserCreatedEvent"],"message":{"userId":"6aa5cd1c196f7b7e223b038c","nome":"Maria","email":"maria@fcg.com"},"sentTime":"2026-09-12T22:07:24.3399995Z","headers":{"MT-Activity-Id":"00-f16db9c722cc9550b16acbf47e1250df-607ee5dc698b5e19-01"}}
        """;

    private readonly ActivityListener _listener = new()
    {
        ShouldListenTo = source => source.Name == TraceContextRestorer.SourceName,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
    };

    public ObservabilidadeTests() => ActivitySource.AddActivityListener(_listener);

    public void Dispose() => _listener.Dispose();

    private static MassTransitEnvelope<UserCreatedEvent> Envelope(Dictionary<string, JsonElement>? headers) =>
        new()
        {
            MessageId = "m-1",
            ConversationId = "c-1",
            MessageType = ["urn:message:Fcg.Contracts.Events:UserCreatedEvent"],
            Message = new UserCreatedEvent { UserId = "u-1", Nome = "Maria", Email = "maria@fcg.com" },
            Headers = headers
        };

    private static Dictionary<string, JsonElement> Header(string nome, string valor) =>
        new() { [nome] = JsonDocument.Parse(JsonSerializer.Serialize(valor)).RootElement };

    [Fact(DisplayName = "Envelope real do users-api: o span da Function fica no MESMO trace do cadastro")]
    public void StartChildActivity_EnvelopeReal_UsaMtActivityId()
    {
        var envelope = MassTransitEnvelopeParser.TryParse<UserCreatedEvent>(
            EnvelopeRealComTrace, "Fcg.Contracts.Events:UserCreatedEvent");

        using var atividade = TraceContextRestorer.StartChildActivity(envelope!, "teste");

        Assert.NotNull(atividade);
        Assert.Equal(TraceIdReal, atividade!.TraceId.ToString());
        Assert.Equal("607ee5dc698b5e19", atividade.ParentSpanId.ToString());
        Assert.Equal(ActivityKind.Consumer, atividade.Kind);
    }

    [Fact(DisplayName = "Header traceparent (nome W3C) também é aceito")]
    public void StartChildActivity_ComTraceparentValido_CriaSpanFilho()
    {
        var envelope = Envelope(Header("traceparent", $"00-{TraceIdReal}-b7ad6b7169203331-01"));

        using var atividade = TraceContextRestorer.StartChildActivity(envelope, "teste");

        Assert.NotNull(atividade);
        Assert.Equal(TraceIdReal, atividade!.TraceId.ToString());
    }

    [Fact(DisplayName = "Nome do header é comparado sem diferenciar maiúsculas")]
    public void StartChildActivity_HeaderMinusculo_Funciona()
    {
        var envelope = Envelope(Header("mt-activity-id", $"00-{TraceIdReal}-b7ad6b7169203331-01"));

        using var atividade = TraceContextRestorer.StartChildActivity(envelope, "teste");

        Assert.NotNull(atividade);
    }

    [Fact(DisplayName = "Sem headers, devolve null e não lança")]
    public void StartChildActivity_SemHeaders_RetornaNull()
    {
        Assert.Null(TraceContextRestorer.StartChildActivity(Envelope(null), "teste"));
        Assert.Null(TraceContextRestorer.StartChildActivity(Envelope([]), "teste"));
    }

    [Fact(DisplayName = "traceparent malformado devolve null e não lança")]
    public void StartChildActivity_TraceparentMalformado_RetornaNull()
    {
        var envelope = Envelope(Header("MT-Activity-Id", "isto-nao-e-um-traceparent"));

        Assert.Null(TraceContextRestorer.StartChildActivity(envelope, "teste"));
    }

    [Fact(DisplayName = "O escopo de log carrega ConversationId, MessageId, MessageType e TraceId")]
    public void BeginEventScope_IncluiIdentificadoresDeCorrelacao()
    {
        var logger = new LoggerComEscopos();
        var envelope = Envelope(Header("MT-Activity-Id", $"00-{TraceIdReal}-b7ad6b7169203331-01"));

        using var atividade = TraceContextRestorer.StartChildActivity(envelope, "teste");
        using var escopo = logger.BeginEventScope(envelope);

        var campos = Assert.IsType<Dictionary<string, object?>>(Assert.Single(logger.Escopos));
        Assert.Equal("c-1", campos["ConversationId"]);
        Assert.Equal("m-1", campos["MessageId"]);
        Assert.Equal("urn:message:Fcg.Contracts.Events:UserCreatedEvent", campos["MessageType"]);
        Assert.Equal(TraceIdReal, campos["TraceId"]);
    }

    [Fact(DisplayName = "Sem OpenTelemetry configurado, a Function processa e envia normalmente")]
    public async Task Function_SemOtlpConfigurado_ProcessaNormalmente()
    {
        // Sem listener, como numa Function sem OpenTelemetry.
        _listener.Dispose();
        var sender = new EmailSenderEspiao();
        var funcao = new UserCreatedFunction(
            NullLogger<UserCreatedFunction>.Instance, sender, new StoreEspiao(), new HistoricoEspiao());

        await funcao.RunAsync(EnvelopeRealComTrace, CancellationToken.None);

        Assert.Single(sender.Enviados);
    }
}
