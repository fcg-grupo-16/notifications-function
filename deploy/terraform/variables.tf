variable "location" {
  type    = string
  default = "brazilsouth"
}

variable "function_app_name" {
  type        = string
  description = "Nome global do Function App (precisa ser único no Azure)."
  default     = "fcg-notifications-func"
}

variable "storage_account_name" {
  type        = string
  description = "Nome global da Storage Account (3-24 caracteres, minúsculas e dígitos)."
  default     = "stfcgnotifications"
}

variable "rabbitmq_connection_string" {
  type        = string
  description = "URI AMQP completa do broker, ex.: amqp://user:pass@host:5672/"
  sensitive   = true
}

variable "redis_connection_string" {
  type      = string
  sensitive = true
}

variable "mongodb_connection_string" {
  type      = string
  sensitive = true
}
