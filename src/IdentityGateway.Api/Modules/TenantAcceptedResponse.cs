namespace IdentityGateway.Api.Modules;

/// <summary>
/// Corpo do <c>202</c> de registro aceito.
/// </summary>
/// <remarks>
/// Carrega o id porque um <c>202</c> de corpo vazio obrigaria o cliente a uma consulta só para descobri-lo. O
/// <c>Status</c> é sempre <c>Pending</c> nesta fatia — o tenant ainda não foi provisionado no Keycloak.
/// </remarks>
/// <param name="TenantId">Identidade do tenant registrado.</param>
/// <param name="Status">Estado no ciclo de vida.</param>
public sealed record TenantAcceptedResponse(Guid TenantId, string Status);
