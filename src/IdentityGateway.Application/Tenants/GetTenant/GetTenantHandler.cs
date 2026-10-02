using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Common.Messaging;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Application.Tenants.GetTenant;

/// <summary>Responde <see cref="GetTenantQuery"/>.</summary>
public sealed class GetTenantHandler(ITenantQueries consultas) : IQueryHandler<GetTenantQuery, TenantDetailsResponse>
{
    public async ValueTask<Result<TenantDetailsResponse>> Handle(
        GetTenantQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        TenantDetailsView? tenant = await consultas.GetDetailsAsync(query.TenantId, cancellationToken);

        if (tenant is null)
        {
            return Result.Failure<TenantDetailsResponse>(TenantErrors.NotFound(query.TenantId));
        }

        return new TenantDetailsResponse(
            tenant.TenantId.Value,
            tenant.Name,
            tenant.Slug.Value,
            tenant.Status.ToString(),
            new TenantPlanResponse(tenant.Plan.Tier.ToString(), tenant.Plan.MaxUsers, tenant.Plan.MaxClients),
            tenant.OccupiedSeats,
            tenant.RegisteredAt);
    }
}
