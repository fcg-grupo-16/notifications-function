# ⚠️ ALTERNATIVA AVALIADA E NÃO IMPLANTADA — NÃO é o caminho de execução desta Function.
#
# Este Terraform provisiona um Azure Function App. A plataforma NÃO o usa: pela ADR 0003, o host de
# Functions roda como container no Kubernetes com KEDA, porque os planos Consumption e Flex
# Consumption da Azure não suportam o trigger de RabbitMQ, e os que suportam são de instância
# reservada — sem escala a zero, que é o requisito.
#
# Pela ADR 0008 (Fase 4), a plataforma vai para AWS/EKS, e a Function vai junto, como container. Este
# arquivo fica como registro de que a alternativa foi estudada, e é validado pelo CI apenas para não
# apodrecer.
#
#   ADR 0003: https://github.com/fcg-grupo-16/orchestration/blob/main/docs/adr/0003-serverless-azure-functions-keda.md
#   ADR 0008: https://github.com/fcg-grupo-16/orchestration/blob/main/docs/adr/0008-provedor-de-nuvem-aws.md
#
# SKU EP1 (Elastic Premium), não Y1 (Consumption): o binding RabbitMQTrigger não é suportado nos
# planos Consumption e Flex Consumption.

terraform {
  required_version = ">= 1.9"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
  }
}

provider "azurerm" {
  features {}
}

resource "azurerm_resource_group" "fcg" {
  name     = "rg-fcg-notifications"
  location = var.location
}

# Exigida pelo host de Functions para estado interno, mesmo sem trigger de Storage.
resource "azurerm_storage_account" "fn" {
  name                     = var.storage_account_name
  resource_group_name      = azurerm_resource_group.fcg.name
  location                 = azurerm_resource_group.fcg.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
  min_tls_version          = "TLS1_2"
}

resource "azurerm_service_plan" "fn" {
  name                = "asp-fcg-notifications"
  resource_group_name = azurerm_resource_group.fcg.name
  location            = azurerm_resource_group.fcg.location
  os_type             = "Linux"
  sku_name            = "EP1"
}

resource "azurerm_linux_function_app" "notifications" {
  name                       = var.function_app_name
  resource_group_name        = azurerm_resource_group.fcg.name
  location                   = azurerm_resource_group.fcg.location
  service_plan_id            = azurerm_service_plan.fn.id
  storage_account_name       = azurerm_storage_account.fn.name
  storage_account_access_key = azurerm_storage_account.fn.primary_access_key

  site_config {
    application_stack {
      dotnet_version              = "8.0"
      use_dotnet_isolated_runtime = true
    }
    # Sem Runtime Scale Monitoring o Elastic Premium não observa a fila e fica preso em 1 instância.
    runtime_scale_monitoring_enabled = true
  }

  app_settings = {
    FUNCTIONS_WORKER_RUNTIME            = "dotnet-isolated"
    RabbitMqConnection                  = var.rabbitmq_connection_string
    "Redis__ConnectionString"           = var.redis_connection_string
    "MongoDbSettings__ConnectionString" = var.mongodb_connection_string
    "MongoDbSettings__DatabaseName"     = "notificationsdb"
  }
}
