using IdentityGateway.Api.Authorization;
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
    public void AsRotasDeTenant_TemAPolicyEsperada()
    {
        // Controle do teste acima: cada rota de tenant tem a policy nomeada certa — e não, por engano, AllowAnonymous,
        // nem a policy de outra rota.
        IReadOnlyList<Endpoint> endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        var policyPorRota = endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/api/v1/tenants", StringComparison.Ordinal))
            .ToDictionary(
                endpoint => Rotulo(endpoint),
                endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Single().Policy!);

        policyPorRota.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["POST /api/v1/tenants"] = Policies.PlatformAdmin,
            ["GET /api/v1/tenants/{tenantId:guid}/provisioning"] = Policies.PlatformAdmin,
            ["GET /api/v1/tenants/{tenantId:guid}"] = Policies.TenantAdmin,
        });
    }

    private static string Rotulo(RouteEndpoint endpoint) =>
        $"{endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods[0]} {endpoint.RoutePattern.RawText}";

    [Fact]
    public void TodaRotaComPolicyDeTenant_TemTenantIdNoTemplate()
    {
        // O SameTenantRequirement lê o tenant do parâmetro {tenantId}. Uma rota com policy de tenant e sem o parâmetro
        // nega sempre — e alguém, para "consertar", afrouxaria o requirement. O erro aparece aqui, na subida.
        IReadOnlyList<Endpoint> endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        RouteEndpoint[] comPolicyDeTenant =
        [
            .. endpoints.OfType<RouteEndpoint>()
                .Where(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                    .Any(dado => dado.Policy is not null && Policies.DeTenant.Contains(dado.Policy))),
        ];

        comPolicyDeTenant.Should().NotBeEmpty("sem rota com policy de tenant, a regra passaria vazia");
        comPolicyDeTenant
            .Where(endpoint => endpoint.RoutePattern.GetParameter(SameTenantRequirement.ParametroDaRota) is null)
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Should().BeEmpty("rota com policy de tenant precisa do parâmetro tenantId no template");
    }
}
