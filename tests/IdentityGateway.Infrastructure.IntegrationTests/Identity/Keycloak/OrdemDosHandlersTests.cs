using IdentityGateway.Infrastructure.Identity.Keycloak;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// A ordem dos handlers do cliente da Admin API do Keycloak — resiliência por fora, token por dentro (§4.2 do
/// design) — não tinha nenhum teste automático; só a revisão de código a protegia (handoff da Task 11, mutação 2,
/// registrada como "verde, não corrigido").
/// </summary>
/// <remarks>
/// Sem container: monta a composição real (a mesma <c>AddInfrastructure</c> que <see cref="KeycloakHealthCheckTests"/>
/// usa) e inspeciona a cadeia de <see cref="DelegatingHandler"/> que o <see cref="IHttpMessageHandlerFactory"/>
/// constrói — nenhuma chamada HTTP é feita.
/// </remarks>
public sealed class OrdemDosHandlersTests
{
    [Fact]
    public void ClienteDaAdminApi_ResilienciaPorForaTokenPorDentro()
    {
        using ServiceProvider provider = KeycloakHealthCheckTests
            .ColecaoDaComposicao("https://sso.exemplo.test")
            .BuildServiceProvider();

        IHttpMessageHandlerFactory fabrica = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        // Nome do cliente tipado: o IHttpClientFactory registra AddHttpClient<TClient> sob o nome curto do tipo.
        HttpMessageHandler raiz = fabrica.CreateHandler(nameof(KeycloakAdminClient));

        // A fábrica pode envolver a cadeia com handlers de logging e um LifetimeTrackingHttpMessageHandler externo;
        // por isso a asserção é de ordem relativa entre os dois handlers que importam, não de posição exata.
        List<string> cadeia = [];
        for (HttpMessageHandler? atual = raiz; atual is DelegatingHandler delegador; atual = delegador.InnerHandler)
        {
            cadeia.Add(atual.GetType().Name);
        }

        int indiceResiliencia = cadeia.FindIndex(nome => nome.Contains("Resilience", StringComparison.Ordinal));
        int indiceToken = cadeia.IndexOf(nameof(ServiceAccountTokenHandler));
        string cadeiaDescrita = string.Join(" -> ", cadeia);

        indiceResiliencia.Should().BeGreaterThanOrEqualTo(
            0, $"o handler de resiliência precisa estar na cadeia: {cadeiaDescrita}");
        indiceToken.Should().BeGreaterThanOrEqualTo(
            0, $"o {nameof(ServiceAccountTokenHandler)} precisa estar na cadeia: {cadeiaDescrita}");
        indiceResiliencia.Should().BeLessThan(indiceToken,
            "resiliência é o handler mais externo (registrado primeiro): cada tentativa renova o token, e o " +
            $"401→repete fica contido no timeout total. Cadeia observada: {cadeiaDescrita}");
    }
}
