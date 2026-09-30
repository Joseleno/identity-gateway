using IdentityGateway.Application.Common.Messaging;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Application.Tenants.RegisterTenant;

/// <summary>
/// Registra um tenant novo, ainda por provisionar.
/// </summary>
/// <remarks>
/// <para>
/// <b>O <c>InitialAdminEmail</c> é acréscimo deliberado ao que a §11.4 mostra.</b> A §9.1 e a §8 o exigem no
/// <c>POST /tenants</c>, e a §9.1 explica por quê: sem ele o tenant nasce trancado, já que convidar membros
/// exige <c>tenant-admin</c> daquele tenant, que ainda não existiria.
/// </para>
/// <para>
/// <b>O e-mail fica no tenant até a ativação (fatia C, D1)</b>, fora do evento <c>TenantRegistered</c>: o evento vai
/// para o Outbox e, com o broker, para o RabbitMQ, contra a regra de dados pessoais só no Keycloak. A coluna é
/// apagada na transação que ativa o tenant ou que o marca <c>ProvisioningFailed</c>.
/// </para>
/// </remarks>
public sealed record RegisterTenantCommand(
    string Name,
    string Slug,
    string PlanCode,
    string InitialAdminEmail) : ICommand<TenantId>
{
    /// <summary>Sem o e-mail nem o nome: o <c>ToString</c> gerado do record os imprimiria em qualquer log (D15).</summary>
    public override string ToString() => $"RegisterTenantCommand {{ Slug = {Slug}, PlanCode = {PlanCode} }}";
}
