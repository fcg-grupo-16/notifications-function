# FCG — notifications-function

Função **serverless** de notificações da plataforma **FIAP Cloud Games**, criada na **Fase 3** do
Tech Challenge. Substitui o microsserviço
[`notifications-api`](https://github.com/fcg-grupo-16/notifications-api) (deprecado), que mantinha um
container rodando 24/7 para uma tarefa esporádica.

> **Grupo 16** — org GitHub [`fcg-grupo-16`](https://github.com/fcg-grupo-16)

> ⚠️ **Repositório recém-criado — a implementação ainda não começou.**
> O trabalho está quebrado nas issues [#1 a #6](https://github.com/fcg-grupo-16/notifications-function/issues),
> na ordem. Comece pela **[#1](https://github.com/fcg-grupo-16/notifications-function/issues/1)** e
> substitua este README pelo definitivo conforme as issues pedem.

## O que esta função faz

| Evento consumido | Fila RabbitMQ | Ação |
|---|---|---|
| `UserCreatedEvent` | `notifications-user-created` | envia e-mail de boas-vindas |
| `PaymentProcessedEvent` | `notifications-payment-processed` | envia confirmação de compra (**só** se `Status == "Approved"`) |

**Stack:** Azure Functions v4 · isolated worker · .NET 8 · binding `RabbitMQTrigger` ·
Redis (idempotência) · MongoDB (`notificationsdb`, histórico) · KEDA (scale-to-zero no Kubernetes).

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
  "headers": { "Diagnostic-Id": "00-<traceId>-<spanId>-01" }
}
```

Desembalar isso é responsabilidade da função (`Messaging/MassTransitEnvelopeParser`).

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

```bash
# 1) Infra (a partir do repo orchestration, clonado como irmão deste)
cd ../orchestration && docker compose up -d rabbitmq mongodb redis users-api catalog-api payments-api

# 2) A fila precisa existir ANTES (o trigger não a cria)
docker compose exec rabbitmq rabbitmqctl list_queues name | grep notifications

# 3) A função
cd ../notifications-function/src/Fcg.Notifications.Function && func start
```

## Repositórios da plataforma

[orchestration](https://github.com/fcg-grupo-16/orchestration) ·
[users-api](https://github.com/fcg-grupo-16/users-api) ·
[catalog-api](https://github.com/fcg-grupo-16/catalog-api) ·
[payments-api](https://github.com/fcg-grupo-16/payments-api) ·
**notifications-function** (este) ·
[notifications-api](https://github.com/fcg-grupo-16/notifications-api) *(deprecado)*
