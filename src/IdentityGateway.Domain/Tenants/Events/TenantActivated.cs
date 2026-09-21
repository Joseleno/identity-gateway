using IdentityGateway.Domain.Common;

namespace IdentityGateway.Domain.Tenants.Events;

/// <summary>
/// O provisionamento concluiu e o tenant entrou em operação.
/// </summary>
/// <param name="TenantId">Identidade do tenant ativado.</param>
public sealed record TenantActivated(TenantId TenantId) : IDomainEvent
{
    /// <inheritdoc />
    public DateTimeOffset OccurredOn { get; } = DateTimeOffset.UtcNow;
}
