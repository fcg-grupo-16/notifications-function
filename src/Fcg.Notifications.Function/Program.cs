using Fcg.Notifications.Function.Email;
using Fcg.Notifications.Function.Idempotency;
using Fcg.Notifications.Function.Persistence;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using StackExchange.Redis;

var builder = FunctionsApplication.CreateBuilder(args);

// SEM ConfigureFunctionsWebApplication(): esse método é a integração com ASP.NET Core e exige o
// pacote ...Extensions.Http.AspNetCore. O HttpTrigger da consulta de auditoria (#4) usa
// HttpRequestData/HttpResponseData, o modelo nativo do isolated worker, que precisa só do
// ...Extensions.Http.

// Envio de e-mail PLUGÁVEL: hoje um sender que apenas registra no log (simulação, sem provedor
// externo), trocável por SMTP/HTTP real via DI sem tocar nas Functions, que dependem só da
// interface. Comportamento idêntico ao do notifications-api.
builder.Services.AddSingleton<IEmailSender, LoggingEmailSender>();
// Redis — store de idempotência DURÁVEL. Diferente dos outros serviços da plataforma, aqui o
// Redis NÃO é opcional: sem ele não existe garantia de "e-mail enviado uma vez só", e a Function
// escala a zero (o dicionário em memória do notifications-api morreria a cada ciclo). Por isso a
// ausência da connection string é ERRO EXPLÍCITO de configuração no startup, e não um fallback
// silencioso para um store em memória — que reintroduziria exatamente o bug.
// Só `Redis:ConnectionString`: o provider de variáveis de ambiente do .NET já normaliza `__` em
// `:`, então a app setting `Redis__ConnectionString` chega aqui com este nome. Testar as duas
// formas era código morto (verificado: a segunda chave sempre vem null).
var redisConnectionString =
    builder.Configuration["Redis:ConnectionString"]
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

// FACTORY, e não instância pronta: registrado como instância, o container NÃO descarta o
// multiplexer (verificado — IsConnected continua true após Dispose do provider), vazando conexão a
// cada reciclagem do host. E com factory a conexão passa a ser PREGUIÇOSA: um Connect ansioso no
// startup bloqueia o boot enquanto tenta alcançar o Redis — medido em 6s quando o pacote é
// descartado em silêncio (NetworkPolicy, pod não-ready), o que entraria no cold start de TODA
// réplica que sobe do zero. Relevante para o scale-to-zero da #5/orchestration#29.
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisOptions));
builder.Services.AddSingleton<IProcessedMessageStore, RedisProcessedMessageStore>();

// MongoDB (notificationsdb) — histórico das notificações. MESMO database e MESMA collection do
// notifications-api, para os registros da Fase 2 continuarem legíveis. Ao contrário do Redis, aqui há
// fallback em vez de erro fatal: o histórico é auditoria (best-effort), não garantia.
var mongoConnectionString =
    builder.Configuration["MongoDbSettings:ConnectionString"]
    ?? "mongodb://localhost:27017/?replicaSet=rs0";

var mongoDatabaseName = builder.Configuration["MongoDbSettings:DatabaseName"] ?? "notificationsdb";

builder.Services.AddSingleton<IMongoClient>(_ =>
    new MongoClient(MongoClientSettingsFactory.Criar(mongoConnectionString)));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(mongoDatabaseName));
builder.Services.AddSingleton<INotificationHistoryStore, MongoNotificationHistoryStore>();
// TODO(#6): OpenTelemetry (traces OTLP) e log estruturado em JSON.

builder.Build().Run();
