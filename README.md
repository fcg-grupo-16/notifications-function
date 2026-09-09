# FCG — notifications-function

Função **serverless** de notificações da plataforma **FIAP Cloud Games**, criada na **Fase 3** do
Tech Challenge. Substitui o microsserviço
[`notifications-api`](https://github.com/fcg-grupo-16/notifications-api) (deprecado), que mantinha um
container rodando 24/7 para uma tarefa esporádica.

> **Grupo 16** — org GitHub [`fcg-grupo-16`](https://github.com/fcg-grupo-16)

> **Estado atual:** issues #1 (bootstrap) e #2 (envio de e-mail) prontas — as funções recebem o
> evento e **enviam o e-mail** (hoje simulado por log, como no `notifications-api`). Faltam
> [#3 a #6](https://github.com/fcg-grupo-16/notifications-function/issues): idempotência em Redis,
> histórico em MongoDB, empacotamento/IaC e observabilidade.
>
> ⚠️ **Sem a #3 não há garantia de "e-mail enviado uma vez só".** Uma reentrega da mesma mensagem
> hoje gera e-mail duplicado.

## O que esta função faz

| Evento consumido | Fila RabbitMQ | Ação |
|---|---|---|
| `UserCreatedEvent` | `notifications-user-created` | envia e-mail de boas-vindas |
| `PaymentProcessedEvent` | `notifications-payment-processed` | envia confirmação de compra (**só** se `Status == "Approved"`) |

**Stack (alvo):** Azure Functions v4 · isolated worker · .NET 8 · binding `RabbitMQTrigger` ·
Redis (idempotência, #3) · MongoDB (`notificationsdb`, histórico, #4) · KEDA (scale-to-zero, #5 e
`orchestration#29`). **Entregue até aqui:** Functions v4 + isolated worker + `RabbitMQTrigger`.

## Decisões de arquitetura

**1. Azure Functions com trigger RabbitMQ — sem migrar o backbone de mensageria.**
O binding oficial `RabbitMQTrigger` consome **direto da fila que a plataforma já usa**, então
`users-api` e `payments-api` não mudaram nada. A alternativa (AWS Lambda + SQS) exigiria migrar a
mensageria e mexer em dois outros serviços.

**2. KEDA no Kubernetes, não o plano Consumption da Azure.**
A [documentação oficial](https://learn.microsoft.com/azure/azure-functions/functions-bindings-rabbitmq-trigger)
é explícita: o binding RabbitMQ só é suportado nos planos **Elastic Premium** e **Dedicated** — os
planos *Consumption* e *Flex Consumption* **não** o suportam. Ou seja, o modelo "serverless puro" da
Azure não serve para este trigger, e os planos que servem são de instância reservada (pagos, sem
escala a zero real).

Rodar o **host de Functions em container no Kubernetes com KEDA** é o caminho suportado *e* o único
com **scale-to-zero de verdade** (`minReplicaCount: 0`): com a fila vazia, **zero** réplicas e zero
consumo — que é exatamente o problema que a Fase 3 pede para resolver. Continua sendo uma Azure
Function (mesmo runtime, mesmo binding, mesmo `host.json`).

**3. `net8.0`, e não `net10.0`** (o resto da plataforma é .NET 10). O suporte a .NET 10 no host de
Functions ainda é **preview**, com problemas conhecidos de carregamento de assembly no Core Tools
local. Esta é a peça mais crítica da entrega e precisa funcionar na gravação — ficamos no alvo estável.

**4. Idempotência em Redis, não em memória.** O `notifications-api` usava um `ConcurrentDictionary`.
Numa função que escala a zero (processo morre a cada ciclo) e sobe até 5 réplicas, isso **perderia
silenciosamente** a garantia de "e-mail enviado uma vez só". Ver
[#3](https://github.com/fcg-grupo-16/notifications-function/issues/3).

**5. A topologia do RabbitMQ é declarativa, no repo `orchestration`.** O `RabbitMQTrigger` **não cria**
fila, exchange nem binding — só consome. A topologia (com dead-letter queue) vive em
`docker/rabbitmq/definitions.json` no
[`orchestration`](https://github.com/fcg-grupo-16/orchestration) —
ver [`orchestration#28`](https://github.com/fcg-grupo-16/orchestration/issues/28).

## Como a mensagem chega aqui

O corpo da mensagem **não** é o evento cru: é o **envelope do MassTransit**
(`Content-Type: application/vnd.masstransit+json`), com o evento sob `message`:

```json
{
  "messageId": "...",
  "conversationId": "...",
  "messageType": ["urn:message:Fcg.Contracts.Events:UserCreatedEvent"],
  "message": { "userId": "...", "nome": "...", "email": "..." },
  "sentTime": "2026-09-08T12:00:00Z",
  "headers": {}
}
```

Desembalar isso é responsabilidade da função (`Messaging/MassTransitEnvelopeParser`).

### Duas armadilhas do formato — as duas descobertas rodando o fluxo real

**1. O corpo do evento vem em `camelCase`** (`userId`, `nome`, `email`), enquanto os records de
`Fcg.Contracts.Events` são `PascalCase`. Sem `PropertyNameCaseInsensitive` a desserialização **não
falha**: devolve o objeto com todas as propriedades nulas, e a função "processa com sucesso"
mandando e-mail para destinatário vazio.

**2. O MassTransit serializa `decimal` como STRING.** O envelope real de `PaymentProcessedEvent`
traz `"price": "29.90"` — com aspas. O `System.Text.Json` rejeita string para `decimal` por padrão
e lança `JsonException`, o que jogava a mensagem no caminho de *poison message* e fazia a
confirmação de compra **nunca ser enviada**, em silêncio, com o host registrando execução
bem-sucedida. Resolvido com `NumberHandling = JsonNumberHandling.AllowReadingFromString`.

Ambas têm teste dedicado usando **envelopes capturados do broker**, não inventados — o teste que
usava `49.90` sem aspas passava e escondia o bug nº 2.

### Nome do parâmetro do trigger

O parâmetro do `[RabbitMQTrigger]` **não pode se chamar `body`** — colide com o binding data do
próprio trigger e o host recusa a função no startup:

```
Microsoft.Azure.WebJobs.Host: Error indexing method 'Functions.UserCreatedFunction'.
Can't bind parameter 'body' to type 'System.String'.
Function 'Functions.UserCreatedFunction' failed indexing and will be disabled.
```

O sintoma é traiçoeiro: o `func start` **lista** as duas funções em `Functions:` e parece saudável,
mas nenhuma consome nada. Usamos `mensagem`.

## Configuração

| App setting | Origem no k8s | Exemplo |
|---|---|---|
| `RabbitMqConnection` | SealedSecret | `amqp://guest:guest@rabbitmq:5672/` |
| `Redis__ConnectionString` | SealedSecret | `redis:6379` |
| `MongoDbSettings__ConnectionString` | SealedSecret | `mongodb://mongodb:27017/?replicaSet=rs0` |
| `MongoDbSettings__DatabaseName` | ConfigMap | `notificationsdb` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | ConfigMap | `http://jaeger:4317` |

`RabbitMqConnection` é o **nome da app setting** referenciada pelo atributo
`[RabbitMQTrigger(..., ConnectionStringSetting = "RabbitMqConnection")]` — desde a v2 da extensão não
existem mais host/usuário/senha separados, só a URI AMQP completa, e ela **tem** de vir de uma app
setting (o binding não aceita valor inline).

Para dev local, copie `local.settings.example.json` para `local.settings.json` (gitignored).

## Rodar localmente

**Pré-requisito:** Azure Functions Core Tools v4. O pacote npm quebra no Node 26
(`TypeError: chalk.red is not a function` no instalador), então baixe o binário oficial do
[GitHub releases](https://github.com/Azure/azure-functions-core-tools/releases) e extraia num
diretório do seu PATH.

```bash
# 1) Infra (a partir do repo orchestration, clonado como irmão deste)
cd ../orchestration && docker compose up -d rabbitmq mongodb redis users-api catalog-api payments-api
```

```bash
# 2) A fila precisa existir ANTES — o trigger não a cria (ver orchestration#28)
docker compose exec rabbitmq rabbitmqctl list_queues name | grep notifications
```

```bash
# 3) ⚠️ Pare o notifications-api enquanto testa: os dois consomem a MESMA fila e viram
#    competing consumers, entregando cada mensagem a um deles de forma imprevisível.
docker compose stop notifications-api
```

```bash
# 4) A função
cd ../notifications-function/src/Fcg.Notifications.Function
cp local.settings.example.json local.settings.json   # gitignored
func start
```

No startup as duas funções devem aparecer como `rabbitMQTrigger`, e
`rabbitmqctl list_queues name consumers` deve mostrar **1 consumidor** em cada fila. Se as funções
aparecem listadas mas os consumidores continuam em 0, procure `failed indexing` no log.

### Testes

```bash
dotnet test
```

## Repositórios da plataforma

[orchestration](https://github.com/fcg-grupo-16/orchestration) ·
[users-api](https://github.com/fcg-grupo-16/users-api) ·
[catalog-api](https://github.com/fcg-grupo-16/catalog-api) ·
[payments-api](https://github.com/fcg-grupo-16/payments-api) ·
**notifications-function** (este) ·
[notifications-api](https://github.com/fcg-grupo-16/notifications-api) *(deprecado)*
