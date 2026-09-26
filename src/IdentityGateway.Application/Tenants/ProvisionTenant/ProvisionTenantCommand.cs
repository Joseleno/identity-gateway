using IdentityGateway.Application.Common.Messaging;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Application.Tenants.ProvisionTenant;

/// <summary>
/// Garante que o tenant registrado existe no provedor de identidade e o coloca em operação.
/// </summary>
/// <remarks>
/// Command interno: nenhum endpoint o expõe. Quem o envia é o despacho do Outbox, ao ler <c>tenant-registered</c> —
/// e, quando o broker chegar, o consumidor dele. Carrega só o id: o estado vem do banco, que é a fonte da verdade,
/// e não de um payload que pode estar velho.
/// </remarks>
/// <param name="TenantId">Tenant a provisionar.</param>
public sealed record ProvisionTenantCommand(TenantId TenantId) : ICommand;
