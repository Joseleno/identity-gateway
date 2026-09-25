using System.Net;
using System.Text.Json;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// <c>EnsureOrganizationAsync</c> contra o Keycloak real: cria, reencontra, resolve corrida e recusa o que não é seu.
/// </summary>
public sealed class EnsureOrganizationContraKeycloakTests(KeycloakFixture keycloak)
{
    [Fact]
    public async Task Cria_ELeituraCruaMostraNameSlugDescriptionEAtributo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();
        var tenant = TenantId.New();
        TenantSlug slug = KeycloakFixture.SlugUnico();

        // Acento, aspas e & no nome de exibição: precisam chegar intactos na description.
        const string nome = "Café & Cia \"Ltda\"";

        string id = await provider.GetRequiredService<IIdentityProvider>()
            .EnsureOrganizationAsync(tenant, slug, nome, ct);

        JsonElement crua = await keycloak.LerOrganizacaoCruaAsync(id, ct);

        crua.GetProperty("name").GetString().Should().Be(slug.Value);
        crua.GetProperty("alias").GetString().Should().Be(slug.Value);
        crua.GetProperty("description").GetString().Should().Be(nome);
        crua.GetProperty("attributes").GetProperty("gateway_tenant_id")[0].GetString()
            .Should().Be(tenant.Value.ToString());
    }

    [Fact]
    public async Task BuscaComDistrator_DevolveSoAOrganizacaoDoTenant()
    {
        // Se o q fosse ignorado, a busca devolveria as Organizations do realm; com UMA só no realm, o teste de
        // idempotência passaria sem provar nada. Aqui há duas, e a busca precisa separar.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();
        IIdentityProvider identidade = provider.GetRequiredService<IIdentityProvider>();

        var x = TenantId.New();
        var y = TenantId.New();
        await identidade.EnsureOrganizationAsync(x, KeycloakFixture.SlugUnico(), "X", ct);
        string idY = await identidade.EnsureOrganizationAsync(y, KeycloakFixture.SlugUnico(), "Y", ct);

        OrganizationRepresentation? achada = await provider.GetRequiredService<KeycloakAdminClient>()
            .FindOrganizationByAttributeAsync(KeycloakIdentityProvider.AtributoDoTenant, y.Value.ToString(), ct);

        achada!.Id.Should().Be(idY);
    }

    [Fact]
    public async Task DuasChamadasEmSequencia_MesmoIdEUmaOrganizacao()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();
        IIdentityProvider identidade = provider.GetRequiredService<IIdentityProvider>();
        var tenant = TenantId.New();
        TenantSlug slug = KeycloakFixture.SlugUnico();

        string primeiro = await identidade.EnsureOrganizationAsync(tenant, slug, "Acme", ct);
        string segundo = await identidade.EnsureOrganizationAsync(tenant, slug, "Acme", ct);

        segundo.Should().Be(primeiro);
        (await keycloak.ContarPorAliasAsync(slug.Value, ct)).Should().Be(1);
    }

    [Fact]
    public async Task DuasChamadasEmParalelo_MesmoIdUmaOrganizacaoEUm409()
    {
        // Sem a barreira as duas tendem a serializar, e o 409 nunca acontece: o teste passaria sem exercitar a
        // reconsulta. A barreira só libera os dois GETs iniciais juntos — as duas chamadas veem "não existe" e
        // disputam o POST.
        CancellationToken ct = TestContext.Current.CancellationToken;
        TaskCompletionSource ambos = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int[] chegaram = [0];

        Interceptacao interceptacao = new()
        {
            AntesDeEnviar = async (pedido, ordinal) =>
            {
                if (pedido.Method == HttpMethod.Get && ordinal <= 2)
                {
                    if (Interlocked.Increment(ref chegaram[0]) == 2)
                    {
                        ambos.SetResult();
                    }

                    await ambos.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
                }
            },
        };

        await using ServiceProvider provider = keycloak.CriarProvider(services => services.Interceptar(interceptacao));
        var tenant = TenantId.New();
        TenantSlug slug = KeycloakFixture.SlugUnico();

        string[] ids = await Task.WhenAll(
            provider.GetRequiredService<IIdentityProvider>().EnsureOrganizationAsync(tenant, slug, "Acme", ct),
            provider.GetRequiredService<IIdentityProvider>().EnsureOrganizationAsync(tenant, slug, "Acme", ct));

        ids[1].Should().Be(ids[0]);
        (await keycloak.ContarPorAliasAsync(slug.Value, ct)).Should().Be(1);
        interceptacao.Status(HttpMethod.Post).Should().Contain(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task SlugTomadoPorOrganizacaoDeFora_LancaInconsistencia()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        TenantSlug slug = KeycloakFixture.SlugUnico();
        await keycloak.CriarOrganizacaoComoMasterAsync(slug.Value, tenantId: null, ct);

        await using ServiceProvider provider = keycloak.CriarProvider();

        Func<Task> garantir = () => provider.GetRequiredService<IIdentityProvider>()
            .EnsureOrganizationAsync(TenantId.New(), slug, "Acme", ct);

        await garantir.Should().ThrowAsync<IdentityProviderInconsistencyException>();
    }

    [Fact]
    public async Task DuasOrganizacoesComOMesmoTenant_LancaInconsistencia()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var tenant = TenantId.New();
        await keycloak.CriarOrganizacaoComoMasterAsync(KeycloakFixture.SlugUnico().Value, tenant.Value.ToString(), ct);
        await keycloak.CriarOrganizacaoComoMasterAsync(KeycloakFixture.SlugUnico().Value, tenant.Value.ToString(), ct);

        await using ServiceProvider provider = keycloak.CriarProvider();

        Func<Task> garantir = () => provider.GetRequiredService<IIdentityProvider>()
            .EnsureOrganizationAsync(tenant, KeycloakFixture.SlugUnico(), "Acme", ct);

        await garantir.Should().ThrowAsync<IdentityProviderInconsistencyException>();
    }
}
