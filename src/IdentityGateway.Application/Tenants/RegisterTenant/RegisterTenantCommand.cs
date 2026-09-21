#pragma warning disable MSG0005

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
/// Nesta fatia o campo é validado e carregado, mas nada cria o convite — isso é trabalho do consumidor do
/// provisionamento. Sem carregá-lo, o endpoint aceitaria um campo obrigatório e o descartaria em silêncio.
/// </para>
/// </remarks>
public sealed record RegisterTenantCommand(
    string Name,
    string Slug,
    string PlanCode,
    string InitialAdminEmail) : ICommand<TenantId>;

#pragma warning restore MSG0005
