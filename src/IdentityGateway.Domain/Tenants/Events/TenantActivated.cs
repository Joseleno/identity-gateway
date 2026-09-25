using IdentityGateway.Domain.Common;

namespace IdentityGateway.Domain.Tenants.Events;

/// <summary>
/// O provisionamento concluiu e o tenant entrou em operação.
/// </summary>
/// <param name="TenantId">Identidade do tenant ativado.</param>
public sealed record TenantActivated(TenantId TenantId) : IDomainEvent
{
    /// <inheritdoc />
    /// <remarks>
    /// <c>init</c> e não só <c>get</c>: o Outbox relê o evento do JSON, e sem setter o System.Text.Json deixava o
    /// inicializador valer — o evento voltava com o instante da desserialização. O inicializador continua dando o
    /// valor na criação.
    /// </remarks>
    public DateTimeOffset OccurredOn { get; init; } = DateTimeOffset.UtcNow;
}
