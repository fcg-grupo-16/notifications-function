using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fcg.Notifications.Function.Messaging;

/// <summary>
/// Envelope de mensagem do MassTransit.
/// </summary>
/// <remarks>
/// <para>
/// O MassTransit não publica o evento cru no corpo da mensagem: ele embrulha o payload num envelope
/// com metadados (<c>messageId</c>, <c>conversationId</c>, <c>messageType</c>, <c>headers</c>) e
/// coloca o evento em <c>message</c>. O <c>content_type</c> da mensagem AMQP é
/// <c>application/vnd.masstransit+json</c>.
/// </para>
/// <para>
/// O binding <c>RabbitMQTrigger</c> entrega o corpo cru — no modelo isolated a extensão suporta
/// apenas <c>string</c> e tipos serializáveis — então desembalar é responsabilidade nossa.
/// </para>
/// </remarks>
/// <typeparam name="T">
/// Tipo do evento de domínio contido em <c>message</c>. Restrito a tipo referência porque
/// <c>Message</c> é anulável (<c>T?</c>) — todos os eventos de <c>Fcg.Contracts.Events</c> são
/// <c>record</c>, então a restrição não custa nada.
/// </typeparam>
public sealed record MassTransitEnvelope<T> where T : class
{
    [JsonPropertyName("messageId")]
    public string? MessageId { get; init; }

    /// <summary>
    /// Identificador que o MassTransit propaga por TODO o fluxo de negócio. É ele que permite
    /// correlacionar a notificação com o request HTTP original nos logs dos outros serviços.
    /// </summary>
    [JsonPropertyName("conversationId")]
    public string? ConversationId { get; init; }

    /// <summary>
    /// URNs do tipo da mensagem, ex.: <c>urn:message:Fcg.Contracts.Events:UserCreatedEvent</c>.
    /// Usado para VALIDAR que a mensagem que chegou nesta fila é a esperada.
    /// </summary>
    /// <remarks>
    /// ANULÁVEL de propósito, e o inicializador <c>= []</c> não bastaria: quando o JSON traz
    /// <c>"messageType": null</c>, o System.Text.Json SOBRESCREVE o valor default com <c>null</c>.
    /// Declarar não-anulável aqui foi o que fez o parser lançar <c>ArgumentNullException</c> num
    /// corpo trivial — e, como o desenho todo depende de o parser nunca lançar, a mensagem era
    /// retentada 20 vezes e ia parar na dead-letter.
    /// </remarks>
    [JsonPropertyName("messageType")]
    public string[]? MessageType { get; init; }

    /// <summary>O evento de domínio propriamente dito.</summary>
    [JsonPropertyName("message")]
    public T? Message { get; init; }

    [JsonPropertyName("sentTime")]
    public DateTimeOffset? SentTime { get; init; }

    /// <summary>
    /// Headers da mensagem.
    /// </summary>
    /// <remarks>
    /// O MassTransit coloca aqui o contexto de trace W3C (<c>Diagnostic-Id</c>) — mas SÓ quando há
    /// uma <see cref="System.Diagnostics.Activity"/> ativa no publisher. Hoje o envelope real chega
    /// com <c>headers</c> VAZIO, porque os serviços ainda não têm instrumentação OpenTelemetry
    /// (users-api#19). Não presuma que o header existe; ver a issue #6.
    /// </remarks>
    [JsonPropertyName("headers")]
    public Dictionary<string, JsonElement>? Headers { get; init; }
}

/// <summary>
/// Desembala envelopes do MassTransit entregues pelo binding <c>RabbitMQTrigger</c>.
/// </summary>
public static class MassTransitEnvelopeParser
{
    /// <summary>
    /// Opções de desserialização compartilhadas.
    /// </summary>
    /// <remarks>
    /// <b><see cref="JsonSerializerOptions.PropertyNameCaseInsensitive"/> é obrigatório, não
    /// cosmético.</b> O MassTransit serializa o corpo do evento em <c>camelCase</c>
    /// (<c>userId</c>, <c>nome</c>, <c>email</c> — confirmado capturando o envelope real do broker),
    /// enquanto os records de <c>Fcg.Contracts.Events</c> são <c>PascalCase</c>. Sem esta opção a
    /// desserialização NÃO falha: ela devolve um objeto com todas as propriedades nulas, e a função
    /// "processa com sucesso" enviando e-mail para destinatário vazio. É o bug mais provável deste
    /// componente, e existe teste dedicado para ele.
    /// </remarks>
    /// <summary>Prefixo que o MassTransit usa nas URNs de tipo de mensagem.</summary>
    private const string UrnPrefixo = "urn:message:";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,

        // <b>AllowReadingFromString também é obrigatório.</b> O MassTransit serializa `decimal` como
        // STRING no JSON — o envelope real de PaymentProcessedEvent capturado do broker traz
        // `"price": "29.90"`, com aspas, e não `29.90`. O System.Text.Json rejeita string para
        // decimal por padrão e lança JsonException, o que fazia a mensagem cair no caminho de
        // poison message e o e-mail de confirmação de compra NUNCA ser enviado — silenciosamente,
        // já que a função registrava sucesso. Descoberto rodando o fluxo real, não em teste.
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>
    /// Extrai o envelope e valida que ele carrega o tipo de evento esperado.
    /// </summary>
    /// <param name="body">Corpo cru da mensagem, como entregue pelo trigger.</param>
    /// <param name="expectedTypeUrnSuffix">
    /// Sufixo esperado em <c>messageType</c>, ex.: <c>Fcg.Contracts.Events:UserCreatedEvent</c>.
    /// </param>
    /// <returns>O envelope, ou <c>null</c> se o corpo for inválido ou de outro tipo.</returns>
    /// <remarks>
    /// <para>
    /// Devolve <c>null</c> em vez de lançar quando a mensagem é inválida ou de tipo inesperado.
    /// Lançar faria o host RETENTAR a mensagem até esgotar a política e mandá-la para a
    /// dead-letter — mas uma mensagem malformada é falha DETERMINÍSTICA: nenhuma retentativa vai
    /// mudar o resultado. O chamador loga e descarta (ack), que é o tratamento correto de
    /// <i>poison message</i>.
    /// </para>
    /// <para>
    /// A validação de <c>messageType</c> existe para transformar um erro de topologia (binding
    /// errado no RabbitMQ mandando outro evento para esta fila) num log explícito, em vez de um
    /// e-mail com campos em branco.
    /// </para>
    /// </remarks>
    public static MassTransitEnvelope<T>? TryParse<T>(string? body, string expectedTypeUrnSuffix)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        // CATCH ABRANGENTE, e não só JsonException: este método é o guarda que impede que uma
        // mensagem malformada vire exceção, retentativa e dead-letter. Qualquer coisa que escape
        // daqui quebra esse contrato — foi o que aconteceu com ArgumentNullException vindo do LINQ
        // sobre um `messageType: null`. Preferimos engolir e devolver null a deixar o chamador
        // descobrir uma exceção nova em produção.
        try
        {
            var envelope = JsonSerializer.Deserialize<MassTransitEnvelope<T>>(body, Options);

            if (envelope?.Message is null)
            {
                return null;
            }

            var urnEsperada = UrnPrefixo + expectedTypeUrnSuffix;

            // Comparação EXATA e Ordinal, não EndsWith. Com EndsWith, um
            // `urn:message:Atacante.Fcg.Contracts.Events:UserCreatedEvent` passaria na validação —
            // verificado. E nomes de tipo CLR são case-sensitive, então OrdinalIgnoreCase estaria
            // errado mesmo sem o problema do sufixo.
            var tipoConfere = envelope.MessageType is { } tipos
                && tipos.Any(urn => string.Equals(urn, urnEsperada, StringComparison.Ordinal));

            return tipoConfere ? envelope : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
