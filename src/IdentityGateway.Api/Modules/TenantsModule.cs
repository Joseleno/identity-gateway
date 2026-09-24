using Carter;
using IdentityGateway.Api.Extensions;
using IdentityGateway.Application.Common.Abstractions;
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
            .RequireAuthorization("PlatformAdmin")
            .WithName("RegistrarTenant")
            .WithSummary("Registra um tenant novo, ainda por provisionar.")
            .Produces<TenantAcceptedResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    /// <remarks>
    /// Responde <c>202</c>, não <c>201</c>: o registro foi aceito e o provisionamento no Keycloak acontece
    /// depois, a partir da mensagem do Outbox. O <c>Location</c> aponta para o acompanhamento do processamento
    /// — rota que ainda não existe nesta fatia, e é limitação conhecida: num <c>202</c>, omitir o cabeçalho ou
    /// apontar para um recurso que minta sobre estar pronto seria pior.
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
}
