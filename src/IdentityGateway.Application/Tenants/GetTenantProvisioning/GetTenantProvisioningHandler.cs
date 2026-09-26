using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Common.Messaging;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Application.Tenants.GetTenantProvisioning;

/// <summary>Responde <see cref="GetTenantProvisioningQuery"/>.</summary>
public sealed class GetTenantProvisioningHandler(ITenantQueries consultas)
    : IQueryHandler<GetTenantProvisioningQuery, TenantProvisioningResponse>
{
    public async ValueTask<Result<TenantProvisioningResponse>> Handle(
        GetTenantProvisioningQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        TenantProvisioningView? estado = await consultas.GetProvisioningAsync(query.TenantId, cancellationToken);

        if (estado is null)
        {
            return Result.Failure<TenantProvisioningResponse>(TenantErrors.NotFound(query.TenantId));
        }

        return new TenantProvisioningResponse(estado.TenantId.Value, estado.Status.ToString(), estado.RegisteredAt);
    }
}
