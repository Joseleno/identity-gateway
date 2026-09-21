using IdentityGateway.Domain.Common;

namespace IdentityGateway.Domain.Tenants.Events;

/// <summary>
/// Um tenant foi registrado e aguarda provisionamento.
/// </summary>
/// <remarks>
/// É este evento que dispara o provisionamento: gravado no Outbox na mesma transação do <c>INSERT</c>, ele
/// garante que ou os dois acontecem ou nenhum. Registrar o tenant chamando o Keycloak direto deixaria um
/// órfão de cada lado sempre que o outro falhasse.
/// </remarks>
/// <param name="TenantId">Identidade do tenant registrado.</param>
/// <param name="Slug">Slug que virará o alias da Organization.</param>
public sealed record TenantRegistered(TenantId TenantId, TenantSlug Slug) : IDomainEvent
{
    /// <inheritdoc />
    public DateTimeOffset OccurredOn { get; } = DateTimeOffset.UtcNow;
}
