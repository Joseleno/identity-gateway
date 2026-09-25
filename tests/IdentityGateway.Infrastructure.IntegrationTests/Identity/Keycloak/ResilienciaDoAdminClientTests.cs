using System.Net;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// A resiliência repete o que é seguro repetir, e só isso — medido contando requisições, não contando Organizations.
/// </summary>
/// <remarks>
/// Contar Organizations seria vacuoso: se o POST fosse repetido, o Keycloak responderia 409 (name e alias são únicos
/// no realm), o adaptador reconsultaria, e continuaria existindo uma só. Quem garantiria o resultado seria o Keycloak,
/// não o código sob teste.
/// </remarks>
public sealed class ResilienciaDoAdminClientTests(KeycloakFixture keycloak)
{
    [Fact]
    public async Task PostComRespostaPerdida_NaoERepetido()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Interceptacao interceptacao = new()
        {
            PerderResposta = (pedido, ordinal) => pedido.Method == HttpMethod.Post && ordinal == 1,
        };

        await using ServiceProvider provider = keycloak.CriarProvider(services => services.Interceptar(interceptacao));
        IIdentityProvider identidade = provider.GetRequiredService<IIdentityProvider>();
        var tenant = TenantId.New();
        TenantSlug slug = KeycloakFixture.SlugUnico();

        Func<Task> primeira = () => identidade.EnsureOrganizationAsync(tenant, slug, "Acme", ct);

        // A exceção transiente sobe para quem chamou decidir — e o POST saiu UMA vez.
        await primeira.Should().ThrowAsync<HttpRequestException>();
        interceptacao.Contar(HttpMethod.Post).Should().Be(1);

        // A Organization foi criada; a próxima tentativa a encontra pelo atributo em vez de criar outra.
        string id = await identidade.EnsureOrganizationAsync(tenant, slug, "Acme", ct);

        id.Should().NotBeNullOrEmpty();
        interceptacao.Contar(HttpMethod.Post).Should().Be(1);
        (await keycloak.ContarPorAliasAsync(slug.Value, ct)).Should().Be(1);
    }

    [Fact]
    public async Task GetCom503_ERepetido()
    {
        // Controle positivo: sem ele, "a resiliência nem foi registrada" também passaria no teste acima.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Interceptacao interceptacao = new()
        {
            ResponderSemEnviar = (pedido, ordinal) =>
                pedido.Method == HttpMethod.Get && ordinal == 1 ? HttpStatusCode.ServiceUnavailable : null,
        };

        await using ServiceProvider provider = keycloak.CriarProvider(services => services.Interceptar(interceptacao));

        string id = await provider.GetRequiredService<IIdentityProvider>()
            .EnsureOrganizationAsync(TenantId.New(), KeycloakFixture.SlugUnico(), "Acme", ct);

        id.Should().NotBeNullOrEmpty();
        interceptacao.Status(HttpMethod.Get).Should().StartWith(
            new HttpStatusCode?[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK });
    }

    [Fact]
    public async Task PostCom401_ReenviaOCorpoComTokenNovo()
    {
        // O 401 acontece no POST — e não no GET que vem antes —, que é o caso em que o corpo precisa ser relido.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Interceptacao interceptacao = new()
        {
            TrocarTokenPorLixo = (pedido, ordinal) => pedido.Method == HttpMethod.Post && ordinal == 1,
        };

        int[] tokensPedidos = [0];

        await using ServiceProvider provider = keycloak.CriarProvider(services =>
        {
            services.Interceptar(interceptacao);

            // Conta os pedidos de token sem trocar o que o cache faz com eles.
            services.AddSingleton<ITokenEndpoint>(sp => new EndpointContador(
                ActivatorUtilities.CreateInstance<KeycloakTokenClient>(sp), tokensPedidos));
        });

        TenantSlug slug = KeycloakFixture.SlugUnico();

        string id = await provider.GetRequiredService<IIdentityProvider>()
            .EnsureOrganizationAsync(TenantId.New(), slug, "Acme", ct);

        id.Should().NotBeNullOrEmpty();
        interceptacao.Status(HttpMethod.Post).Should().Equal(HttpStatusCode.Unauthorized, HttpStatusCode.Created);
        tokensPedidos[0].Should().Be(2);
        (await keycloak.ContarPorAliasAsync(slug.Value, ct)).Should().Be(1);
    }

    private sealed class EndpointContador(ITokenEndpoint real, int[] contador) : ITokenEndpoint
    {
        public Task<TokenObtido> ObterAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref contador[0]);
            return real.ObterAsync(cancellationToken);
        }
    }
}
