namespace IdentityGateway.Application.Tenants.GetTenantProvisioning;

/// <summary>
/// Resposta da consulta de provisionamento.
/// </summary>
/// <remarks>
/// Sem o id da Organization nem o motivo de falha, de propósito: o primeiro é detalhe interno do Keycloak que
/// ninguém consome; o segundo, guardado como texto de exceção, arriscaria expor detalhe interno pela API — ele fica no
/// log de erro, com o <c>tenantId</c>.
/// </remarks>
/// <param name="TenantId">Identidade do tenant.</param>
/// <param name="Status">Nome do <c>TenantStatus</c>.</param>
/// <param name="RegisteredAt">Quando foi registrado, em UTC.</param>
public sealed record TenantProvisioningResponse(Guid TenantId, string Status, DateTimeOffset RegisteredAt);
