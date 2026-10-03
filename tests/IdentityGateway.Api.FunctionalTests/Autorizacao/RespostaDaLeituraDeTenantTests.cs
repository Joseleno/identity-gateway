using IdentityGateway.Api.Authorization;
using IdentityGateway.Api.Modules;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Tenants.GetTenant;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Api.FunctionalTests.Autorizacao;

/// <summary>
/// A tradução do resultado da leitura de tenant em resposta HTTP, sem HTTP.
/// </summary>
/// <remarks>
/// O ramo de falha é inalcançável por HTTP com a porta real — a policy nega antes, porque não há membro de um tenant
/// que não existe. Por isso é testado aqui, na função: se um dia for alcançado, "não encontrado" responde o mesmo
/// <c>403</c> das negações, e não o <c>404</c> que o <c>ParaOk</c> daria.
/// </remarks>
public sealed class RespostaDaLeituraDeTenantTests
{
    private static readonly string[] SoACorrelacao = ["correlationId"];

    private static DefaultHttpContext Contexto()
    {
        ServiceCollection services = new();
        services.AddSingleton<ICorrelationIdProvider>(new CorrelacaoFixa());

        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }

    [Fact]
    public void TenantNaoEncontrado_ViraOMesmo403DaAutorizacao()
    {
        var falha = Result.Failure<TenantDetailsResponse>(TenantErrors.NotFound(TenantId.New()));

        IResult resposta = TenantsModule.ParaRespostaDoTenant(falha, Contexto());

        ProblemHttpResult problema = resposta.Should().BeOfType<ProblemHttpResult>().Subject;
        problema.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        problema.ProblemDetails.Type.Should().Be(RespostasDeAutorizacao.TipoDoProibido);
        problema.ProblemDetails.Title.Should().Be(RespostasDeAutorizacao.TituloDoProibido);
        problema.ProblemDetails.Detail.Should().Be(RespostasDeAutorizacao.DetalheDoProibido);

        // Nem o código do erro de domínio, que diria "Tenant.NaoEncontrado".
        problema.ProblemDetails.Extensions.Keys.Should().BeEquivalentTo(SoACorrelacao);
    }

    [Fact]
    public void TenantEncontrado_Vira200ComOTenant()
    {
        TenantDetailsResponse tenant = new(
            Guid.NewGuid(), "Acme", "acme", "Active", new TenantPlanResponse("Free", 5, 1), 1, DateTimeOffset.UtcNow);

        IResult resposta = TenantsModule.ParaRespostaDoTenant(tenant, Contexto());

        resposta.Should().BeOfType<Ok<TenantDetailsResponse>>().Which.Value.Should().Be(tenant);
    }

    private sealed class CorrelacaoFixa : ICorrelationIdProvider
    {
        public string CorrelationId => "correlacao-de-teste";
    }
}
