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
/// <b>Nesta versão o campo é validado e descartado</b>: o handler não o repassa ao <c>Tenant</c> nem ao evento
/// <c>TenantRegistered</c>, e nada o persiste. Onde ele deve viver entre o <c>POST</c> e o convite é decisão pendente
/// da fatia C (spec v2.4, §9.1): pô-lo no evento o levaria ao Outbox e ao RabbitMQ, contra a regra de dados pessoais
/// só no Keycloak. A versão anterior deste comentário afirmava que o campo era "carregado" — não era.
/// </para>
/// </remarks>
public sealed record RegisterTenantCommand(
    string Name,
    string Slug,
    string PlanCode,
    string InitialAdminEmail) : ICommand<TenantId>;
