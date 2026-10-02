using System.Text.Json;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O realm depois do import, lido pelo master: o que o JSON declara só vale se o Keycloak o gravou assim.
/// </summary>
/// <remarks>
/// As regras de <c>RegrasDoRealmTests</c> leem o arquivo. Três coisas elas não veem: o atributo
/// <c>CreateDefaultClientScopes</c> não persiste (só o efeito dele: os scopes embutidos existem), o papel padrão é
/// montado pelo import, e o token do service account só existe emitido.
/// </remarks>
public sealed class RealmVivoTests(KeycloakFixture keycloak)
{
    private static readonly string[] ScopesEmbutidos = ["basic", "roles", "acr", "profile", "email", "web-origins"];

    private static readonly string[] ScopesDaGateway = ["gateway-roles", "gateway-tenant", "gateway-api"];

    private static readonly string[] ScopesDoDemo = ["basic", "acr", "gateway-roles", "gateway-tenant", "gateway-api"];

    private static readonly string[] ScopesDoServiceAccount = ["basic", "roles"];

    private static readonly string[] PapeisDaConta = ["manage-account", "view-profile"];

    private static readonly string[] PapeisDoServiceAccount = ["manage-organizations", "manage-users"];

    private static readonly string[] AcoesDoBootstrap = ["UPDATE_PASSWORD", "VERIFY_EMAIL"];

    private static string[] Nomes(JsonElement lista) =>
        [.. lista.EnumerateArray().Select(item => item.GetProperty("name").GetString()!)];

    private async Task<string> IdDoClientAsync(string clientId, CancellationToken ct)
    {
        JsonElement clients = await keycloak.LerComoMasterAsync($"clients?clientId={clientId}", ct);
        return clients[0].GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task ScopesEmbutidos_ExistemNoRealm()
    {
        // Sem o CreateDefaultClientScopes, declarar clientScopes no JSON apaga os embutidos sem erro — e o access token
        // sai sem sub, que vem do scope basic.
        CancellationToken ct = TestContext.Current.CancellationToken;

        string[] scopes = Nomes(await keycloak.LerComoMasterAsync("client-scopes", ct));

        scopes.Should().Contain(ScopesEmbutidos).And.Contain(ScopesDaGateway);
    }

    [Fact]
    public async Task ScopesDaGateway_NaoSaoDefaultNemOpcionalDoRealm()
    {
        // D-e: um client criado depois pela Admin API herda os defaults do realm. Com gateway-api entre eles, qualquer
        // client novo emitiria token aceito pela Gateway.
        CancellationToken ct = TestContext.Current.CancellationToken;

        string[] defaults = Nomes(await keycloak.LerComoMasterAsync("default-default-client-scopes", ct));
        string[] opcionais = Nomes(await keycloak.LerComoMasterAsync("default-optional-client-scopes", ct));

        defaults.Should().Contain("basic", "controle: a lista de defaults do realm foi lida de verdade");
        defaults.Concat(opcionais).Should().NotContain(ScopesDaGateway).And.NotContain("offline_access");
    }

    [Fact]
    public async Task PapelPadrao_SoComOsPapeisDaConta()
    {
        // offline_access e uma_authorization entrariam aqui se o JSON não declarasse os dois papéis e o scope — e, pelo
        // papel padrão, em todo usuário novo.
        CancellationToken ct = TestContext.Current.CancellationToken;
        JsonElement padrao = await keycloak.LerComoMasterAsync($"roles/default-roles-{KeycloakFixture.Realm}", ct);

        string[] compostos = Nomes(await keycloak.LerComoMasterAsync(
            $"roles-by-id/{padrao.GetProperty("id").GetString()}/composites", ct));

        compostos.Should().BeEquivalentTo(PapeisDaConta);
    }

    [Fact]
    public async Task Clients_FicaramComOsScopesDoJson()
    {
        // Os clients embutidos nascem antes dos scopes do JSON; os declarados no JSON precisam ter ficado exatamente
        // com os scopes que o JSON lista, sem herdar os defaults do realm.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string demo = await IdDoClientAsync("identity-gateway-demo", ct);
        string gateway = await IdDoClientAsync("identity-gateway", ct);

        Nomes(await keycloak.LerComoMasterAsync($"clients/{demo}/default-client-scopes", ct))
            .Should().BeEquivalentTo(ScopesDoDemo);
        Nomes(await keycloak.LerComoMasterAsync($"clients/{demo}/optional-client-scopes", ct)).Should().BeEmpty();
        Nomes(await keycloak.LerComoMasterAsync($"clients/{gateway}/default-client-scopes", ct))
            .Should().BeEquivalentTo(ScopesDoServiceAccount);
    }

    [Fact]
    public async Task Realm_GravouOsTemposEARotacaoDoRefresh()
    {
        // Chave que o import não reconhece é ignorada em silêncio: o que vale é o que o realm devolve.
        CancellationToken ct = TestContext.Current.CancellationToken;

        JsonElement realm = await keycloak.LerComoMasterAsync(string.Empty, ct);

        realm.GetProperty("accessTokenLifespan").GetInt32().Should().Be(300);
        realm.GetProperty("revokeRefreshToken").GetBoolean().Should().BeTrue();
        realm.GetProperty("refreshTokenMaxReuse").GetInt32().Should().Be(0);
        realm.GetProperty("registrationAllowed").GetBoolean().Should().BeFalse();
        realm.GetProperty("bruteForceProtected").GetBoolean().Should().BeTrue();
        realm.GetProperty("resetPasswordAllowed").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task PlatformAdminDoBootstrap_NasceSemSenhaSoComOPapelEComAsDuasAcoes()
    {
        // D-h. Leitura crua: o placeholder foi substituído, o e-mail ficou em minúsculas, não há credencial, e o único
        // papel de realm atribuído é platform-admin — sem o papel padrão, que o import não dá a usuário do JSON com
        // realmRoles.
        CancellationToken ct = TestContext.Current.CancellationToken;
        IReadOnlyList<JsonElement> achados = await keycloak.UsuariosPorEmailAsync(KeycloakFixture.EmailDoPlatformAdmin, ct);

        achados.Should().ContainSingle();
        JsonElement usuario = achados[0];
        string id = usuario.GetProperty("id").GetString()!;
        JsonElement papeis = await keycloak.LerComoMasterAsync($"users/{id}/role-mappings", ct);

        usuario.GetProperty("username").GetString().Should().Be(KeycloakFixture.EmailDoPlatformAdmin);
        usuario.GetProperty("email").GetString().Should().Be(KeycloakFixture.EmailDoPlatformAdmin);
        usuario.GetProperty("enabled").GetBoolean().Should().BeTrue();
        usuario.GetProperty("emailVerified").GetBoolean().Should().BeFalse();
        usuario.GetProperty("requiredActions").EnumerateArray().Select(acao => acao.GetString())
            .Should().BeEquivalentTo(AcoesDoBootstrap);
        usuario.TryGetProperty("attributes", out _).Should().BeFalse();

        Nomes(papeis.GetProperty("realmMappings")).Should().Equal("platform-admin");
        papeis.TryGetProperty("clientMappings", out _).Should().BeFalse();
        (await keycloak.LerComoMasterAsync($"users/{id}/credentials", ct)).GetArrayLength().Should().Be(0);
        (await keycloak.LerComoMasterAsync($"users/{id}/groups", ct)).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task TokenDoServiceAccount_TemOsPapeisDeAdministracaoENaoAAudienciaDaGateway()
    {
        // DT10 e D-e. O token do service account abre a Admin API (resource_access, do scope roles) e NÃO serve na
        // própria Gateway: sem gateway-api, a audiência identity-gateway-api não entra; sem gateway-roles, não há claim
        // roles; sem gateway-tenant, não há tenant_id.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();

        JsonElement payload = PayloadDoJwt.Ler(
            await provider.GetRequiredService<ServiceAccountTokenCache>().ObterAsync(ct));

        payload.GetProperty("resource_access").GetProperty("realm-management").GetProperty("roles")
            .EnumerateArray().Select(papel => papel.GetString())
            .Should().BeEquivalentTo(PapeisDoServiceAccount);
        PayloadDoJwt.Audiencias(payload).Should().NotContain("identity-gateway-api");
        payload.GetProperty("azp").GetString().Should().Be("identity-gateway");
        payload.TryGetProperty("roles", out _).Should().BeFalse();
        payload.TryGetProperty("tenant_id", out _).Should().BeFalse();
    }
}
