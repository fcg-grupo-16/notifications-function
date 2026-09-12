# FCG — notifications-function

Função **serverless** de notificações da plataforma **FIAP Cloud Games**, criada na **Fase 3** do
Tech Challenge. Substitui o microsserviço
[`notifications-api`](https://github.com/fcg-grupo-16/notifications-api) (deprecado), que mantinha um
container rodando 24/7 para uma tarefa esporádica.

> **Grupo 16** — org GitHub [`fcg-grupo-16`](https://github.com/fcg-grupo-16)

> **Estado atual:** issues #1 (bootstrap), #2 (envio de e-mail), #3 (idempotência) e #4 (histórico
> em MongoDB) prontas — as funções recebem o evento, enviam o e-mail (hoje simulado por log, como no
> `notifications-api`), **garantem envio único** mesmo entre reinícios do processo e registram cada
> envio no histórico de auditoria. Faltam
> [#5 e #6](https://github.com/fcg-grupo-16/notifications-function/issues): empacotamento/IaC e
> observabilidade.

## O que esta função faz

| Evento consumido | Fila RabbitMQ | Ação |
|---|---|---|
| `UserCreatedEvent` | `notifications-user-created` | envia e-mail de boas-vindas |
| `PaymentProcessedEvent` | `notifications-payment-processed` | envia confirmação de compra (**só** se `Status == "Approved"`) |

**Stack (alvo):** Azure Functions v4 · isolated worker · .NET 8 · binding `RabbitMQTrigger` ·
Redis (idempotência, #3) · MongoDB (`notificationsdb`, histórico, #4) · KEDA (scale-to-zero, #5 e
`orchestration#29`). **Entregue até aqui:** Functions v4 + isolated worker + `RabbitMQTrigger` +
Redis + MongoDB.

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

## Envio de e-mail

O envio é **plugável**: as Functions dependem só de `IEmailSender`, e a implementação atual
(`LoggingEmailSender`) apenas registra a mensagem no log — simulação, sem provedor externo, igual
ao `notifications-api`. Trocar por SMTP/HTTP real é registrar outra implementação no DI, sem tocar
nas Functions.

`EmailTemplates` renderiza as mensagens em pt-BR e **guarda a regra de negócio**:
`PurchaseConfirmation` devolve `null` quando o pagamento não foi aprovado, e a Function apenas
respeita esse `null` — a decisão de "manda ou não manda" mora num lugar só.

### ⚠️ Requisito de globalização (ICU) — a #5 precisa respeitar isto no Dockerfile

`EmailTemplates` formata o preço com `CultureInfo("pt-BR")` (`R$ 1.234,56`), e o
`Directory.Build.props` declara `InvariantGlobalization=false` para todos os projetos.

**Consequência:** o processo **exige ICU**. Numa imagem base sem ICU (alpine sem `icu-libs`,
chiseled), o .NET **recusa iniciar** — `Couldn't find a valid ICU package installed on the
system`. É falha de **startup**, não de formatação, e some do log num container que reinicia em
loop.

A imagem oficial de Azure Functions traz ICU. Se alguém trocar a base, instale `icu-libs`.

> **Nenhum teste unitário pega isso.** O teste `PurchaseConfirmation_FormataPrecoEmPtBr` valida o
> **formato** (separador de milhar, decimal, símbolo) — verificado que ele continua verde mesmo
> apagando a propriedade do csproj, porque o host de teste sempre roda com ICU disponível. A única
> verificação real é um **smoke test da imagem**, escopo da issue #5.

### Sanitização de log

Tudo que sai no log vem de mensagem publicada por outro sistema — conteúdo **não confiável**, com
dado pessoal. Por isso:

| | |
|---|---|
| **E-mail** | mascarado (`ma***@fcg.com`) em `Information`; completo só em `Debug` |
| **Corpo** | truncado em 500 caracteres, sem partir par surrogate |
| **Destinatário** | rejeitado se tiver `CR`/`LF` ou passar de 320 caracteres |

O CR/LF importa: `IsNullOrWhiteSpace` não barra quebra de linha **interna**, e um
`vitima@fcg.com\r\nBcc: atacante@evil.com` é injeção de log hoje e injeção de **cabeçalho** no dia
em que houver SMTP de verdade.

### Limitações conhecidas

1. **A confirmação de compra é endereçada ao `UserId`, não a um e-mail.** Comportamento herdado do
   `notifications-api`: `PaymentProcessedEvent` não carrega o endereço. Corrigir exige enriquecer o
   contrato do evento (compartilhado com `payments-api` e `catalog-api`) ou consultar o `users-api`.
2. **A janela de deduplicação é de 7 dias.** Reprocessar a dead-letter depois disso reenvia o
   e-mail — ver a seção "Idempotência".
3. **A consulta de auditoria só responde quando há réplica no ar** — ou seja, quase nunca, por
   design. Ver "Histórico de notificações (NoSQL)".

## Idempotência

O RabbitMQ entrega **at-least-once**: a mesma mensagem chega mais de uma vez em retentativa do
host, requeue da extensão ou reprocessamento manual da dead-letter. Sem deduplicação, cada
reentrega vira um e-mail a mais para o cliente.

O `notifications-api` resolvia isso com um `ConcurrentDictionary` em memória. **Aqui isso não
serve**, e a razão é a própria migração para serverless:

1. **A Function escala a zero.** O KEDA termina o pod quando a fila esvazia; um dicionário em
   memória morre junto. Uma reentrega chega num processo novo, com memória vazia, e reenvia.
2. **`maxReplicaCount: 5`.** Sob rajada existem até cinco processos, cada um com seu próprio
   dicionário, sem enxergar o do outro.

Por isso o store é **compartilhado entre réplicas e sobrevive ao reinício do processo**, em
Redis, com `SET NX EX`:

```
SET fcg:notifications:processed:<TipoDoEvento>:<chave> <timestamp> NX EX 604800
```

| Decisão | Por quê |
|---|---|
| **`SET NX`**, não `EXISTS` + `SET` | uma operação **atômica** no servidor. "Consultar depois gravar" tem janela de corrida: dois pods passam pelo teste e os dois enviam |
| **TTL de 7 dias** | cobre retentativa (segundos), requeue (minutos) e reprocessamento manual da DLQ (horas/dias), sem o keyspace crescer para sempre |
| **Tipo do evento na chave** | o mesmo id pode aparecer em eventos diferentes e precisa ser deduplicado de forma independente |
| **Marcar ANTES de enviar, com COMPENSAÇÃO** | marcar antes evita duplicar; e se o envio falhar, a marcação é **desfeita** (`UnmarkAsync`) para a reentrega poder tentar de novo. Sem a compensação a perda seria permanente e silenciosa: a reentrega veria a chave, sairia calada, o host daria ack, e o e-mail desapareceria **sem log de erro e sem ir para a dead-letter** |
| **Rejeitado não consome a chave** | um `PaymentProcessedEvent` com status `Rejected` **não** marca o `OrderId`, senão a confirmação legítima de um reprocessamento posterior (rejeitado → aprovado) ficaria bloqueada para sempre |

### ⚠️ A janela de 7 dias é um TETO, não um piso

O Redis da plataforma é provisionado deliberadamente **como cache**, não como banco
([`orchestration/k8s/12-infra-redis.yaml`](https://github.com/fcg-grupo-16/orchestration/blob/main/k8s/12-infra-redis.yaml)):
`Deployment` sem `PersistentVolumeClaim`, `--save ""`, `--appendonly no` e
`--maxmemory-policy allkeys-lru`. Duas consequências medidas:

1. **Restart do pod apaga tudo.** Um restart do Redis zerou o keyspace inteiro, inclusive chaves de
   idempotência ativas.
2. **O LRU come justamente estas chaves.** Num container com as mesmas flags, 200 chaves de
   idempotência com TTL de 7 dias, nunca mais lidas, foram para **2** depois de tráfego normal de
   cache — 99% despejadas. Não é azar: a chave é **escrita uma vez e lida nunca** (a única leitura é
   a duplicata rara), então é sempre o dado mais frio de uma instância compartilhada com os caches
   quentes de `users-api` e `catalog-api` — exatamente o que `allkeys-lru` escolhe primeiro.

**Na prática, a deduplicação é best-effort.** Se o pod do Redis reiniciar (reschedule, drain de nó,
bump de imagem) ou houver pressão de memória entre o envio e um reprocessamento da dead-letter, **o
e-mail pode ser duplicado** — o próprio caso que esta seção existe para evitar.

> O comentário do manifesto do Redis justifica a ausência de persistência com *"todo dado é
> reconstruível a partir do MongoDB"*. Para as chaves de idempotência isso é **falso**: elas não são
> reconstruíveis de lugar nenhum. Rastreado em `orchestration#35`.

### ⚠️ Fail-CLOSED — ao contrário do cache dos outros serviços

Se o Redis estiver fora, o store **lança**, a mensagem é reentregue e, esgotadas as tentativas, vai
para a dead-letter. **O orçamento é curto: 5 tentativas em ~20 segundos** — limite fixo da extensão
do RabbitMQ, já que o `host.json` não tem bloco `retry` (deprecado para este trigger). Isso é menos
que um restart rotineiro do Redis, então indisponibilidade dele enche a DLQ rápido e reprocessá-la é
procedimento esperado. Com `prefetchCount: 20`, até 20 mensagens em voo queimam as tentativas na
mesma janela e vão juntas para a DLQ.

É o oposto do `RedisCacheService` de `users-api`/`catalog-api`, que é
fail-**open** de propósito.

A diferença é o custo do erro: lá, cache fora significa consulta mais lenta; aqui, idempotência
fora significa **e-mail duplicado para o cliente** — efeito colateral externo e irreversível.
Preferimos não processar a processar duas vezes.

Pelo mesmo motivo, a **falta da connection string é erro de startup**, não um fallback silencioso
para memória — que reintroduziria exatamente o bug.

## Histórico de notificações (NoSQL)

Cada e-mail enviado vira um documento na collection `notifications` do database `notificationsdb`
(MongoDB). É o requisito **NoSQL** da Fase 3 do lado da função: um *log de eventos* append-only, de
alta volumetria.

**Mesmo database, mesma collection e mesmo formato de documento do `notifications-api`** — os
registros da Fase 2 continuam legíveis:

| Campo | Conteúdo |
|---|---|
| `Type` | nome do evento: `UserCreatedEvent` · `PaymentProcessedEvent` |
| `Recipient`, `Subject`, `Body` | o e-mail como foi enviado |
| `NaturalKey` | `UserId` no cadastro · `OrderId` na compra |
| `SentAtUtc` | UTC, com índice descendente `ix_sentAtUtc` |

O teste `NotificationRecord_FormatoCompativelComAFase2` insere um documento cru nesse formato e exige
que a Function o leia — renomear qualquer campo quebra o teste.

**Best-effort, depois do envio.** A gravação fica num `try/catch` que registra e engole a exceção: se
ela subisse, o host retentaria a mensagem, a retentativa seria barrada pela idempotência e a
mensagem terminaria na dead-letter por um erro de auditoria. Pagamento não aprovado não gera e-mail
e, como na Fase 2, não gera registro.

**Índice criado uma vez por processo, preguiçosamente**, na primeira gravação — e não no startup,
como no `notifications-api`: com KEDA o processo sobe e desce o tempo todo, e criar no startup
somaria uma ida ao Mongo a todo cold start.

### Consulta de auditoria

```
GET /api/v1/notificacoes?limit=50     (x-functions-key: <chave>)
```

Porta o endpoint homônimo do `notifications-api`. `limit` tem teto de 200 no servidor.

> ⚠️ **Este endpoint fica indisponível a maior parte do tempo, e isso é esperado.** Com a fila vazia
> o KEDA mantém **0 réplicas**, e não existe "acordar por HTTP": o scaler observa a fila, não o
> tráfego HTTP. Quem precisa do histórico com a fila vazia consulta o Mongo direto
> (`mongosh notificationsdb`). **Não** resolva isso subindo `minReplicaCount` para 1 — seria abrir
> mão do scale-to-zero, o requisito central da fase.

Como a função não tem rota no Kong, a única proteção do endpoint é o `AuthorizationLevel.Function`
(header `x-functions-key`). Com `func start` local, a chave não é exigida.

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

```bash
# 5) Depois de disparar um cadastro pelo users-api, o histórico tem de estar lá
docker compose -f ../orchestration/docker-compose.yml exec mongodb mongosh notificationsdb --quiet --eval '
  printjson(db.notifications.find().sort({SentAtUtc:-1}).limit(1).toArray());
  printjson(db.notifications.getIndexes());
'
#    -> documento com Type/Recipient/Subject/Body/NaturalKey/SentAtUtc + índice ix_sentAtUtc
```

```bash
# 6) A consulta de auditoria (local, o func start não exige a chave da função)
curl -s 'http://localhost:7071/api/v1/notificacoes?limit=5'
```

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
