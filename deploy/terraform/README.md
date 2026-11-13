# IaC — Azure Function App (alternativa avaliada, **não implantada**)

> ⚠️ **Este não é o caminho de execução da Function, nem na Fase 3 nem na Fase 4.** A Function roda
> como container no Kubernetes com KEDA ([ADR 0003](https://github.com/fcg-grupo-16/orchestration/blob/main/docs/adr/0003-serverless-azure-functions-keda.md)), e na Fase 4 a plataforma vai para
> **AWS/EKS** ([ADR 0008](https://github.com/fcg-grupo-16/orchestration/blob/main/docs/adr/0008-provedor-de-nuvem-aws.md)). Nada da plataforma implanta em Azure. Este diretório fica como
> registro de que a alternativa foi estudada, e o CI o valida só para não apodrecer
> ([#18](https://github.com/fcg-grupo-16/notifications-function/issues/18)).

Este Terraform provisiona a `notifications-function` num **Azure Function App** real.

## ⚠️ Não é o caminho da demo — e por quê

1. O binding `RabbitMQTrigger` **não é suportado** nos planos Consumption e Flex Consumption da
   Azure, só em **Elastic Premium** e **Dedicated (App Service)** — instância reservada, paga e
   **sem scale-to-zero real**. O oposto do que a Fase 3 pede.
2. Um Function App na Azure precisaria alcançar o RabbitMQ pela internet (ou por VNet integration
   com broker gerenciado), e o broker da plataforma roda no minikube.

Por isso a demonstração roda no **Kubernetes com KEDA** (`deploy/k8s/` + o `ScaledObject` no repo
`orchestration`), onde o scale-to-zero é real e o custo é zero. Quando a plataforma foi para a
nuvem, na Fase 4, foi para o **EKS**, e a Function foi junto como container: este Terraform não é
usado lá.

## Como aplicar (só para avaliar a alternativa; não faz parte do deploy)

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
