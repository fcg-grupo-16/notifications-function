using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

// SEM ConfigureFunctionsWebApplication(): esse método é a integração com ASP.NET Core e exige o
// pacote ...Extensions.Http.AspNetCore. Esta função é acionada por FILA, não por HTTP, então o
// pacote seria peso morto. A issue #4, se optar por expor o histórico via HttpTrigger, adiciona
// o pacote e a chamada junto.

// TODO(#2): builder.Services.AddSingleton<IEmailSender, LoggingEmailSender>();
// TODO(#3): Redis (IConnectionMultiplexer + IProcessedMessageStore) — obrigatório, sem fallback
//           em memória: a Function escala a zero e perderia a garantia de idempotência.
// TODO(#4): MongoDB (notificationsdb) para o histórico de notificações.
// TODO(#6): OpenTelemetry (traces OTLP) e log estruturado em JSON.

builder.Build().Run();
