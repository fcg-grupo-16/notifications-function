// Contratos de eventos compartilhados entre os microsserviços FCG.
// IMPORTANTE: o namespace e os nomes dos tipos DEVEM ser idênticos em todos os
// serviços. O MassTransit identifica a mensagem pela URN derivada de
// "namespace:NomeDoTipo" (ex.: urn:message:Fcg.Contracts.Events:UserCreatedEvent),
// portanto qualquer divergência quebra a interoperabilidade entre os serviços.
namespace Fcg.Contracts.Events;

/// <summary>
/// Publicado pelo UsersAPI quando um novo usuário é cadastrado.
/// Consumido pelo NotificationsAPI (e-mail de boas-vindas).
/// </summary>
public record UserCreatedEvent
{
    public string UserId { get; init; } = string.Empty;
    public string Nome { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
}

/// <summary>
/// Publicado pelo CatalogAPI ao iniciar a compra de um jogo.
/// Consumido pelo PaymentsAPI.
/// </summary>
public record OrderPlacedEvent
{
    public Guid OrderId { get; init; }
    public string UserId { get; init; } = string.Empty;
    public string GameId { get; init; } = string.Empty;
    public decimal Price { get; init; }
}

/// <summary>
/// Publicado pelo PaymentsAPI após processar (simular) o pagamento.
/// Consumido pelo CatalogAPI (adiciona à biblioteca se aprovado) e pelo
/// NotificationsAPI (e-mail de confirmação se aprovado).
/// Status: "Approved" ou "Rejected".
/// </summary>
public record PaymentProcessedEvent
{
    public Guid OrderId { get; init; }
    public string UserId { get; init; } = string.Empty;
    public string GameId { get; init; } = string.Empty;
    public decimal Price { get; init; }
    public string Status { get; init; } = string.Empty;
}
