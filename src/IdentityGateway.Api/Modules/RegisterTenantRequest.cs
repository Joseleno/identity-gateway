namespace IdentityGateway.Api.Modules;

/// <summary>
/// Corpo do <c>POST /api/v1/tenants</c>.
/// </summary>
/// <remarks>
/// Espelha <c>RegisterTenantCommand</c>, e a duplicação é deliberada: contrato de fio e contrato interno mudam
/// por razões diferentes. Colapsar os dois faria renomear um campo do command quebrar clientes HTTP.
/// </remarks>
/// <param name="Name">Nome de exibição.</param>
/// <param name="Slug">Identificador legível, único e imutável.</param>
/// <param name="PlanCode">Código do plano no catálogo.</param>
/// <param name="InitialAdminEmail">
/// E-mail do primeiro administrador. Obrigatório: sem ele o tenant nasce trancado, porque convidar membros
/// exige um <c>tenant-admin</c> que ainda não existiria (§9.1).
/// </param>
public sealed record RegisterTenantRequest(
    string Name,
    string Slug,
    string PlanCode,
    string InitialAdminEmail);
