using IdentityGateway.Domain.Tenants;
using Microsoft.Extensions.Logging;

namespace IdentityGateway.Application.Tenants.ProvisionTenant;

/// <summary>
/// Mensagens de log do provisionamento. EventIds 1100–1199.
/// </summary>
/// <remarks>
/// Só o id do tenant, o status e a exceção — nunca o nome do tenant: log é indexado e lido por muita gente (§14).
/// Os dois <c>Error</c> são o único registro do motivo de um <c>ProvisioningFailed</c>; o banco guarda só o estado.
/// </remarks>
internal static partial class ProvisioningLogs
{
    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Warning,
        Message = "Provisionamento: tenant {TenantId} não existe; mensagem descartada")]
    public static partial void TenantInexistente(ILogger logger, Guid tenantId);

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Information,
        Message = "Provisionamento: tenant {TenantId} está em {Status}, não em Pending; nada a fazer")]
    public static partial void ForaDePending(ILogger logger, Guid tenantId, TenantStatus status);

    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Information,
        Message = "Provisionamento: tenant {TenantId} ativo")]
    public static partial void Provisionado(ILogger logger, Guid tenantId);

    [LoggerMessage(
        EventId = 1103,
        Level = LogLevel.Error,
        Message = "Provisionamento: tenant {TenantId} em ProvisioningFailed por inconsistência no provedor de identidade")]
    public static partial void FalhaPermanente(ILogger logger, Guid tenantId, Exception excecao);

    [LoggerMessage(
        EventId = 1104,
        Level = LogLevel.Error,
        Message = "Provisionamento: tenant {TenantId} em ProvisioningFailed; janela de {Horas}h esgotada")]
    public static partial void JanelaEsgotada(ILogger logger, Guid tenantId, double horas, Exception excecao);
}
