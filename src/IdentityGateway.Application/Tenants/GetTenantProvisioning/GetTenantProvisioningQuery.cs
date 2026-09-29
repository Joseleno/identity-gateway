using IdentityGateway.Application.Common.Messaging;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Application.Tenants.GetTenantProvisioning;

/// <summary>Estado do provisionamento de um tenant — o destino do <c>Location</c> do <c>202</c> do registro.</summary>
/// <param name="TenantId">Tenant consultado.</param>
public sealed record GetTenantProvisioningQuery(TenantId TenantId) : IQuery<TenantProvisioningResponse>;
