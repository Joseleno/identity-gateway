using Carter;
using IdentityGateway.Api.Authorization;
using IdentityGateway.Api.Extensions;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Tenants.GetTenant;
using IdentityGateway.Application.Tenants.GetTenantProvisioning;
using IdentityGateway.Application.Tenants.RegisterTenant;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;
using Mediator;

namespace IdentityGateway.Api.Modules;

/// <summary>
/// Endpoints de tenant.
/// </summary>
/// <remarks>
/// <para>
/// <b>A rota traz <c>/api/v1</c> literal.</b> A §8 determina que o roteamento use <c>Asp.Versioning.Http</c>,
/// e o pacote fica de fora enquanto houver uma versão só: a URL produzida é idêntica, e o que ele entrega — a
/// convivência entre <c>v1</c> e <c>v2</c>, com <c>Deprecation</c> e <c>Sunset</c> da RFC 8594 — ainda não tem
/// consumidor. A política da §8 permanece; adia-se o mecanismo.
/// </para>
/// <para>
/// <b>Público, não <c>internal</c>.</b> A varredura de módulos do Carter (<c>DependencyContextAssemblyCatalog</c>)
/// enumera só os tipos exportados do assembly — <c>internal</c> fica invisível a ela mesmo com
/// <c>InternalsVisibleTo</c>, que abrange chamada direta, não reflexão de terceiro. Um módulo <c>internal</c>
/// compila normalmente e nunca aparece em <c>MapCarter()</c>: toda rota responde <c>404</c> sem erro nenhum
/// no startup.
/// </para>
/// </remarks>
public sealed class TenantsModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/v1/tenants", RegistrarAsync)
            .RequireAuthorization(Policies.PlatformAdmin)
            .WithName("RegistrarTenant")
            .WithSummary("Registra um tenant novo, ainda por provisionar.")
            .Produces<TenantAcceptedResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        app.MapGet("/api/v1/tenants/{tenantId:guid}/provisioning", ConsultarProvisionamentoAsync)
            .RequireAuthorization(Policies.PlatformAdmin)
            .WithName("ConsultarProvisionamento")
            .WithSummary("Estado do provisionamento de um tenant.")
            .Produces<TenantProvisioningResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        app.MapGet("/api/v1/tenants/{tenantId:guid}", ConsultarAsync)
            .RequireAuthorization(Policies.TenantAdmin)
            .WithName("ConsultarTenant")
            .WithSummary("O tenant, para quem o administra.")
            .Produces<TenantDetailsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    /// <remarks>
    /// Responde <c>202</c>, não <c>201</c>: o registro foi aceito e o provisionamento no Keycloak acontece
    /// depois, a partir da mensagem do Outbox. O <c>Location</c> aponta para <c>ConsultarProvisionamentoAsync</c>,
    /// o acompanhamento do processamento: num <c>202</c>, omitir o cabeçalho ou apontar para um recurso que minta
    /// sobre estar pronto seria pior.
    /// </remarks>
    private static async Task<IResult> RegistrarAsync(
        RegisterTenantRequest request,
        ISender sender,
        ICorrelationIdProvider correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        RegisterTenantCommand comando = new(
            request.Name,
            request.Slug,
            request.PlanCode,
            request.InitialAdminEmail);

        Result<TenantId> resultado = await sender.Send(comando, cancellationToken);

        return resultado.ParaAccepted(
            localizacao: id => $"/api/v1/tenants/{id.Value}/provisioning",
            corpo: id => new TenantAcceptedResponse(id.Value, nameof(TenantStatus.Pending)),
            correlationId: correlationId.CorrelationId);
    }

    // Sempre 200 com o status, também quando Active, e não um 303 para o recurso do tenant: quem acompanha o
    // provisionamento é o platform-admin, e GET /tenants/{id} responde 403 a ele. A restrição :guid na rota faz um id
    // malformado responder 404 sem chegar ao handler.
    private static async Task<IResult> ConsultarProvisionamentoAsync(
        Guid tenantId,
        ISender sender,
        ICorrelationIdProvider correlationId,
        CancellationToken cancellationToken)
    {
        Result<TenantProvisioningResponse> resultado = await sender.Send(
            new GetTenantProvisioningQuery(new TenantId(tenantId)), cancellationToken);

        return resultado.ParaOk(correlationId.CorrelationId);
    }

    /// <remarks>
    /// Quem chega aqui já passou pela policy <c>TenantAdmin</c>: é administrador deste tenant, pelo token e pelo banco.
    /// A rota não tem <c>404</c>: para quem não é membro, um tenant que não existe e um tenant alheio são a mesma
    /// resposta, e a policy nega os dois antes de qualquer consulta ao tenant.
    /// </remarks>
    private static async Task<IResult> ConsultarAsync(
        Guid tenantId,
        ISender sender,
        HttpContext contexto,
        CancellationToken cancellationToken)
    {
        Result<TenantDetailsResponse> resultado = await sender.Send(
            new GetTenantQuery(new TenantId(tenantId)), cancellationToken);

        return ParaRespostaDoTenant(resultado, contexto);
    }

    /// <summary>
    /// Traduz o resultado da leitura do tenant: <c>200</c> com o tenant, ou o mesmo <c>403</c> da autorização.
    /// </summary>
    /// <remarks>
    /// <b>Não usa o <c>ParaOk</c>,</b> que traduziria "tenant não encontrado" em <c>404</c>. A policy torna esse
    /// caminho inalcançável — não há <c>Member</c> de um tenant que não existe —, mas se um dia ele for alcançado (uma
    /// corrida, um tenant removido à mão), a resposta não pode passar a distinguir "não existe" de "não é seu".
    /// </remarks>
    internal static IResult ParaRespostaDoTenant(Result<TenantDetailsResponse> resultado, HttpContext contexto)
    {
        ArgumentNullException.ThrowIfNull(resultado);

        return resultado.Match(
            onSuccess: tenant => Results.Ok(tenant),
            onFailure: _ => RespostasDeAutorizacao.Proibido(contexto));
    }
}
