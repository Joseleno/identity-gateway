using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// Todo endpoint diz quem pode chamá-lo: uma policy nomeada, ou o anonimato declarado.
/// </summary>
/// <remarks>
/// A policy de fallback (usuário autenticado) cobre o endpoint que alguém esquecer de proteger — mas "autenticado" é
/// qualquer papel, de qualquer tenant. Este teste faz do esquecimento um vermelho: endpoint novo sem
/// <c>RequireAuthorization(policy)</c> nem <c>AllowAnonymous()</c> reprova aqui. O que ele <b>não</b> prova é a própria
/// fallback: quem prova é o caminho não mapeado sem token, em <c>SegurancaTests</c>.
/// </remarks>
public sealed class EndpointsDeclaramAutorizacaoTests(IdentityGatewayApiFactory factory)
    : IClassFixture<IdentityGatewayApiFactory>
{
    [Fact]
    public void TodoEndpoint_TemPolicyNomeadaOuAnonimatoDeclarado()
    {
        IReadOnlyList<Endpoint> endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        string[] semDeclaracao =
        [
            .. endpoints
                .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null)
                .Where(endpoint => !endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                    .Any(dado => !string.IsNullOrEmpty(dado.Policy)))
                .Select(endpoint => endpoint.DisplayName ?? endpoint.ToString()!),
        ];

        endpoints.Should().NotBeEmpty("sem endpoints, a regra passaria vazia");
        semDeclaracao.Should().BeEmpty("endpoint sem policy nomeada nem AllowAnonymous fica só com a fallback");
    }

    [Fact]
    public void AsRotasDeTenant_ExigemPlatformAdmin()
    {
        // Controle do teste acima: as duas rotas que existem têm a policy nomeada — e não, por engano, AllowAnonymous.
        IReadOnlyList<Endpoint> endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        string[] policies =
        [
            .. endpoints.OfType<RouteEndpoint>()
                .Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/api/v1/tenants", StringComparison.Ordinal))
                .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>())
                .Select(dado => dado.Policy!),
        ];

        policies.Should().HaveCount(2).And.OnlyContain(policy => policy == "PlatformAdmin");
    }
}
