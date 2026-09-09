using Fcg.Notifications.Function.Email;
using Fcg.Notifications.Function.Idempotency;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

var builder = FunctionsApplication.CreateBuilder(args);

// SEM ConfigureFunctionsWebApplication(): esse método é a integração com ASP.NET Core e exige o
// pacote ...Extensions.Http.AspNetCore. Esta função é acionada por FILA, não por HTTP, então o
// pacote seria peso morto. A issue #4, se optar por expor o histórico via HttpTrigger, adiciona
// o pacote e a chamada junto.

// Envio de e-mail PLUGÁVEL: hoje um sender que apenas registra no log (simulação, sem provedor
// externo), trocável por SMTP/HTTP real via DI sem tocar nas Functions, que dependem só da
// interface. Comportamento idêntico ao do notifications-api.
builder.Services.AddSingleton<IEmailSender, LoggingEmailSender>();
// Redis — store de idempotência DURÁVEL. Diferente dos outros serviços da plataforma, aqui o
// Redis NÃO é opcional: sem ele não existe garantia de "e-mail enviado uma vez só", e a Function
// escala a zero (o dicionário em memória do notifications-api morreria a cada ciclo). Por isso a
// ausência da connection string é ERRO EXPLÍCITO de configuração no startup, e não um fallback
// silencioso para um store em memória — que reintroduziria exatamente o bug.
var redisConnectionString =
    builder.Configuration["Redis:ConnectionString"]
    ?? builder.Configuration["Redis__ConnectionString"]
    ?? throw new InvalidOperationException(
        "Redis:ConnectionString não configurada. O store de idempotência é obrigatório: sem ele a "
        + "Function reenvia e-mails a cada reentrega ou ciclo de escala. Configure a app setting "
        + "Redis__ConnectionString (provisionada no SealedSecret notifications-function-secret).");

var redisOptions = ConfigurationOptions.Parse(redisConnectionString);

// AbortOnConnectFail=false: o HOST sobe mesmo com o Redis momentaneamente fora, e o multiplexer
// reconecta sozinho. Isso NÃO é fail-open: a primeira mensagem processada sem Redis vai lançar,
// ser reentregue e, se preciso, ir para a dead-letter — nunca gerar e-mail duplicado.
redisOptions.AbortOnConnectFail = false;
redisOptions.ConnectTimeout = 3000;
redisOptions.SyncTimeout = 3000;

builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisOptions));
builder.Services.AddSingleton<IProcessedMessageStore, RedisProcessedMessageStore>();
// TODO(#4): MongoDB (notificationsdb) para o histórico de notificações.
// TODO(#6): OpenTelemetry (traces OTLP) e log estruturado em JSON.

builder.Build().Run();
