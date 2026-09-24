using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace IdentityGateway.Infrastructure.Persistence.Repositories;

/// <summary>
/// Implementa <see cref="ITenantRepository"/> sobre o <see cref="AppDbContext"/>.
/// </summary>
internal sealed class TenantRepository(AppDbContext context) : ITenantRepository
{
    /// <inheritdoc />
    public void Add(Tenant tenant) => context.Tenants.Add(tenant);

    /// <inheritdoc />
    public Task<bool> SlugExistsAsync(TenantSlug slug, CancellationToken cancellationToken) =>
        context.Tenants.AnyAsync(tenant => tenant.Slug == slug, cancellationToken);
}
