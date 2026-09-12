# IaC — Azure Function App (caminho de nuvem)

Este Terraform provisiona a `notifications-function` num **Azure Function App** real.

## ⚠️ Não é o caminho da demo — e por quê

1. O binding `RabbitMQTrigger` **não é suportado** nos planos Consumption e Flex Consumption da
   Azure, só em **Elastic Premium** e **Dedicated (App Service)** — instância reservada, paga e
   **sem scale-to-zero real**. O oposto do que a Fase 3 pede.
2. Um Function App na Azure precisaria alcançar o RabbitMQ pela internet (ou por VNet integration
   com broker gerenciado), e o broker da plataforma roda no minikube.

Por isso a demonstração roda no **Kubernetes com KEDA** (`deploy/k8s/` + o `ScaledObject` no repo
`orchestration`), onde o scale-to-zero é real e o custo é zero. Este Terraform fica como IaC de
referência para quando a plataforma for para a nuvem.

## Como aplicar

```bash
cd deploy/terraform
terraform init
terraform plan -var="rabbitmq_connection_string=amqp://user:pass@broker-publico:5672/" \
               -var="redis_connection_string=..." -var="mongodb_connection_string=..."
terraform apply
```

`terraform validate` e `terraform fmt -check` rodam no CI, então o código é verificado mesmo sem ser
aplicado.

> **Nunca commite `terraform.tfstate` nem `.tfvars` com valores reais** — o state guarda as
> connection strings em claro. Os dois estão no `.gitignore` deste diretório.
