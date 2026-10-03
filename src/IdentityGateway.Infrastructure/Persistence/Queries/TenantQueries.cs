using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace IdentityGateway.Infrastructure.Persistence.Queries;

/// <summary>
/// Implementa <see cref="ITenantQueries"/> com projeções sem rastreamento.
/// </summary>
internal sealed class TenantQueries(AppDbContext context) : ITenantQueries
{
    /// <inheritdoc />
    public Task<TenantProvisioningView?> GetProvisioningAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        context.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => new TenantProvisioningView(tenant.Id, tenant.Status, tenant.RegisteredAt))
            .SingleOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public Task<TenantDetailsView?> GetDetailsAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        context.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => new TenantDetailsView(
                tenant.Id,
                tenant.Name,
                tenant.Slug,
                tenant.Status,
                tenant.Plan,
                tenant.OccupiedSeats,
                tenant.RegisteredAt))
            .SingleOrDefaultAsync(cancellationToken);
}
