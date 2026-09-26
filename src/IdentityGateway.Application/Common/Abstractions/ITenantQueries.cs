using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Application.Common.Abstractions;

/// <summary>
/// Leituras de tenant que não precisam do agregado.
/// </summary>
/// <remarks>
/// Separado do <see cref="ITenantRepository"/>: o repositório devolve o agregado rastreado, para ser alterado; aqui
/// são projeções sem rastreamento, para responder consulta. Três campos não pedem o <c>Tenant</c> montado.
/// </remarks>
public interface ITenantQueries
{
    /// <summary>Estado do provisionamento, ou nulo se o tenant não existir.</summary>
    Task<TenantProvisioningView?> GetProvisioningAsync(TenantId tenantId, CancellationToken cancellationToken = default);
}

/// <summary>Projeção do estado de provisionamento de um tenant.</summary>
/// <param name="TenantId">Identidade do tenant.</param>
/// <param name="Status">Estado no ciclo de vida.</param>
/// <param name="RegisteredAt">Quando foi registrado, em UTC.</param>
public sealed record TenantProvisioningView(TenantId TenantId, TenantStatus Status, DateTimeOffset RegisteredAt);
