using Fcg.Contracts.Events;
using Fcg.Notifications.Function.Messaging;

namespace Fcg.Notifications.Function.UnitTests;

/// <summary>
/// Testes do parser do envelope do MassTransit — o componente onde mora o risco desta issue.
/// </summary>
public sealed class MassTransitEnvelopeParserTests
{
    private const string TipoUserCreated = "Fcg.Contracts.Events:UserCreatedEvent";
    private const string TipoPaymentProcessed = "Fcg.Contracts.Events:PaymentProcessedEvent";

    /// <summary>
    /// Envelope CAPTURADO do broker em orchestration#28 (`rabbitmqadmin get`), depois de um cadastro
    /// real pela API. É a especificação viva da integração: se este teste quebrar após um upgrade do
    /// MassTransit, o formato do envelope mudou e a Function vai parar de processar em produção.
    /// </summary>
    private const string EnvelopeReal = """
    {
      "messageId": "01000000-b43e-86fd-bb6f-08df0e1c3f3e",
      "requestId": null,
      "correlationId": null,
      "conversationId": "01000000-b43e-86fd-9d0f-08df0e1c3f3f",
      "initiatorId": null,
      "sourceAddress": "rabbitmq://rabbitmq/35860430ccf7_FcgUsersApi_bus_yryyyyfw84dx4pkjbdxoh8b7ng?temporary=true",
      "destinationAddress": "rabbitmq://rabbitmq/Fcg.Contracts.Events:UserCreatedEvent",
      "responseAddress": null,
      "faultAddress": null,
      "messageType": [
        "urn:message:Fcg.Contracts.Events:UserCreatedEvent"
      ],
      "message": {
        "userId": "6aa0c8039056346385d903d0",
        "nome": "Topologia",
        "email": "topologia-16801@fcg.com"
      },
      "expirationTime": null,
      "sentTime": "2026-09-09T02:44:19.2848751Z",
      "headers": {},
      "host": {
        "machineName": "35860430ccf7",
        "processName": "Fcg.Users.Api",
        "processId": 1,
        "assembly": "Fcg.Users.Api",
        "assemblyVersion": "1.0.0.0",
        "frameworkVersion": "10.0.12",
        "massTransitVersion": "8.5.10.0",
        "operatingSystemVersion": "Unix 7.0.14.380"
      }
    }
    """;

    [Fact(DisplayName = "Envelope real capturado do broker: extrai o evento com todos os campos")]
    public void TryParse_EnvelopeReal_ExtraiOEvento()
    {
        var envelope = MassTransitEnvelopeParser.TryParse<UserCreatedEvent>(EnvelopeReal, TipoUserCreated);

        Assert.NotNull(envelope);
        Assert.NotNull(envelope!.Message);
        Assert.Equal("6aa0c8039056346385d903d0", envelope.Message!.UserId);
        Assert.Equal("Topologia", envelope.Message.Nome);
        Assert.Equal("topologia-16801@fcg.com", envelope.Message.Email);
        Assert.Equal("01000000-b43e-86fd-9d0f-08df0e1c3f3f", envelope.ConversationId);
        Assert.Equal("01000000-b43e-86fd-bb6f-08df0e1c3f3e", envelope.MessageId);
    }

    [Fact(DisplayName = "Envelope real: headers vem VAZIO (sem Diagnostic-Id) enquanto não houver OpenTelemetry")]
    public void TryParse_EnvelopeReal_HeadersVazio()
    {
        // Documenta o estado atual medido no broker. O MassTransit só propaga contexto de trace
        // quando existe uma Activity ativa no publisher, o que passa a acontecer depois de
        // users-api#19. A issue #6 (correlação de traces) NÃO pode presumir que o header existe.
        var envelope = MassTransitEnvelopeParser.TryParse<UserCreatedEvent>(EnvelopeReal, TipoUserCreated);

        Assert.NotNull(envelope);
        Assert.NotNull(envelope!.Headers);
        Assert.Empty(envelope.Headers!);
    }

    [Fact(DisplayName = "Corpo em camelCase preenche as propriedades (o bug mais provável do componente)")]
    public void TryParse_CamelCase_PreencheAsPropriedades()
    {
        // Sem PropertyNameCaseInsensitive isto NÃO lança: devolve o objeto com tudo nulo, e a
        // função "processa com sucesso" mandando e-mail para destinatário vazio.
        const string json = """
        {"messageType":["urn:message:Fcg.Contracts.Events:UserCreatedEvent"],
         "message":{"userId":"u-1","nome":"Maria","email":"maria@fcg.com"}}
        """;

        var envelope = MassTransitEnvelopeParser.TryParse<UserCreatedEvent>(json, TipoUserCreated);

        Assert.NotNull(envelope);
        Assert.Equal("u-1", envelope!.Message!.UserId);
        Assert.Equal("Maria", envelope.Message.Nome);
        Assert.Equal("maria@fcg.com", envelope.Message.Email);
    }

    [Fact(DisplayName = "Corpo em PascalCase também funciona (robustez às duas convenções)")]
    public void TryParse_PascalCase_PreencheAsPropriedades()
    {
        const string json = """
        {"MessageType":["urn:message:Fcg.Contracts.Events:UserCreatedEvent"],
         "Message":{"UserId":"u-2","Nome":"João","Email":"joao@fcg.com"}}
        """;

        var envelope = MassTransitEnvelopeParser.TryParse<UserCreatedEvent>(json, TipoUserCreated);

        Assert.NotNull(envelope);
        Assert.Equal("u-2", envelope!.Message!.UserId);
        Assert.Equal("joao@fcg.com", envelope.Message.Email);
    }

    [Theory(DisplayName = "Corpo inválido devolve null em vez de lançar")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("isto não é json")]
    [InlineData("{\"messageType\": [")]
    [InlineData("[]")]
    public void TryParse_CorpoInvalido_RetornaNull(string? body)
    {
        // Lançar faria o host retentar até a dead-letter uma mensagem que nunca será processável.
        var envelope = MassTransitEnvelopeParser.TryParse<UserCreatedEvent>(body, TipoUserCreated);

        Assert.Null(envelope);
    }

    [Fact(DisplayName = "Envelope de OUTRO tipo de evento é rejeitado")]
    public void TryParse_TipoInesperado_RetornaNull()
    {
        // Cenário real: binding errado no RabbitMQ entregando PaymentProcessedEvent na fila de
        // cadastro. Sem esta validação, o parser preencheria um UserCreatedEvent com campos vazios
        // e a função mandaria e-mail para destinatário em branco.
        const string json = """
        {"messageType":["urn:message:Fcg.Contracts.Events:PaymentProcessedEvent"],
         "message":{"orderId":"00000000-0000-0000-0000-000000000001","userId":"u-1","gameId":"g-1","price":10,"status":"Approved"}}
        """;

        Assert.Null(MassTransitEnvelopeParser.TryParse<UserCreatedEvent>(json, TipoUserCreated));
        Assert.NotNull(MassTransitEnvelopeParser.TryParse<PaymentProcessedEvent>(json, TipoPaymentProcessed));
    }

    [Fact(DisplayName = "Envelope sem o campo 'message' devolve null")]
    public void TryParse_MessageAusente_RetornaNull()
    {
        const string json = """
        {"messageId":"1","messageType":["urn:message:Fcg.Contracts.Events:UserCreatedEvent"]}
        """;

        Assert.Null(MassTransitEnvelopeParser.TryParse<UserCreatedEvent>(json, TipoUserCreated));
    }

    [Fact(DisplayName = "Envelope sem 'messageType' devolve null (não dá para confirmar o tipo)")]
    public void TryParse_MessageTypeAusente_RetornaNull()
    {
        const string json = """{"message":{"userId":"u-1","nome":"N","email":"e@x.com"}}""";

        Assert.Null(MassTransitEnvelopeParser.TryParse<UserCreatedEvent>(json, TipoUserCreated));
    }

    [Fact(DisplayName = "PaymentProcessedEvent: preço decimal e Guid do pedido são desserializados")]
    public void TryParse_PaymentProcessed_DesserializaPrecoEGuid()
    {
        const string json = """
        {"conversationId":"c-1",
         "messageType":["urn:message:Fcg.Contracts.Events:PaymentProcessedEvent"],
         "message":{"orderId":"3f2504e0-4f89-11d3-9a0c-0305e82c3301","userId":"u-9","gameId":"g-9","price":49.90,"status":"Approved"}}
        """;

        var envelope = MassTransitEnvelopeParser.TryParse<PaymentProcessedEvent>(json, TipoPaymentProcessed);

        Assert.NotNull(envelope);
        Assert.Equal(Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301"), envelope!.Message!.OrderId);
        Assert.Equal(49.90m, envelope.Message.Price);
        Assert.Equal("Approved", envelope.Message.Status);
    }

    /// <summary>
    /// Envelope de PaymentProcessedEvent CAPTURADO do broker depois de uma compra real.
    /// </summary>
    /// <remarks>
    /// <b>Repare no <c>"price": "29.90"</c> — com ASPAS.</b> O MassTransit serializa `decimal` como
    /// string no JSON. O System.Text.Json rejeita isso por padrão e lança <c>JsonException</c>, o que
    /// fazia a mensagem cair no caminho de poison message e a confirmação de compra nunca ser
    /// enviada — em silêncio, porque a função registrava execução bem-sucedida. O teste anterior
    /// passava porque usava `49.90` sem aspas, ou seja, testava uma suposição em vez da realidade.
    /// Só apareceu rodando o fluxo real. NÃO troque esta string por um número.
    /// </remarks>
    private const string EnvelopePagamentoReal = """
    {
      "messageId": "01000000-543f-e1af-2b40-08df0ea04298",
      "conversationId": "01000000-3b28-8751-e23e-08df0ea04240",
      "sourceAddress": "rabbitmq://rabbitmq/payments-order-placed",
      "destinationAddress": "rabbitmq://rabbitmq/Fcg.Contracts.Events:PaymentProcessedEvent",
      "messageType": [
        "urn:message:Fcg.Contracts.Events:PaymentProcessedEvent"
      ],
      "message": {
        "orderId": "a3abceb1-c6d5-4efc-b1a3-d79819af9940",
        "userId": "6aa0d1c10c0877de36606c1e",
        "gameId": "6aa0d1c0456e199191439970",
        "price": "29.90",
        "status": "Approved"
      },
      "sentTime": "2026-09-09T18:29:18.47248Z",
      "headers": {}
    }
    """;

    [Fact(DisplayName = "Envelope real de pagamento: decimal vem como STRING e precisa ser aceito")]
    public void TryParse_EnvelopePagamentoReal_AceitaDecimalComoString()
    {
        var envelope = MassTransitEnvelopeParser.TryParse<PaymentProcessedEvent>(
            EnvelopePagamentoReal, TipoPaymentProcessed);

        Assert.NotNull(envelope);
        Assert.Equal(29.90m, envelope!.Message!.Price);
        Assert.Equal(Guid.Parse("a3abceb1-c6d5-4efc-b1a3-d79819af9940"), envelope.Message.OrderId);
        Assert.Equal("Approved", envelope.Message.Status);
        Assert.Equal("6aa0d1c10c0877de36606c1e", envelope.Message.UserId);
    }

    [Theory(DisplayName = "Decimal é aceito tanto como string quanto como número")]
    [InlineData("\"29.90\"", 29.90)]
    [InlineData("29.90", 29.90)]
    [InlineData("\"0\"", 0)]
    [InlineData("1234.56", 1234.56)]
    public void TryParse_Decimal_AceitaAsDuasFormas(string precoJson, double esperado)
    {
        // Concatenação em vez de raw string interpolada: o JSON tem `}}` no fim, que colidiria
        // com os delimitadores de interpolação.
        var json =
            "{\"messageType\":[\"urn:message:Fcg.Contracts.Events:PaymentProcessedEvent\"],"
            + "\"message\":{\"orderId\":\"3f2504e0-4f89-11d3-9a0c-0305e82c3301\",\"userId\":\"u\","
            + "\"gameId\":\"g\",\"price\":" + precoJson + ",\"status\":\"Approved\"}}";

        var envelope = MassTransitEnvelopeParser.TryParse<PaymentProcessedEvent>(json, TipoPaymentProcessed);

        Assert.NotNull(envelope);
        Assert.Equal((decimal)esperado, envelope!.Message!.Price);
    }
}
