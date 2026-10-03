using IdentityGateway.Application.Common.Messaging;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Application.Tenants.GetTenant;

/// <summary>O tenant, para quem o administra.</summary>
/// <remarks>
/// A query não carrega quem pergunta: a autorização — papel, tenant do token e pertença no banco — acontece antes, na
/// policy da rota. Quem enviar esta query por outro caminho precisa autorizar antes.
/// </remarks>
/// <param name="TenantId">Tenant consultado.</param>
public sealed record GetTenantQuery(TenantId TenantId) : IQuery<TenantDetailsResponse>;
