using System.Text.Json;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O access token que o client de demonstração emite, claim a claim — o que a Api vai validar.
/// </summary>
/// <remarks>
/// Cada asserção aqui é a segunda testemunha de uma regra do realm: <c>fullScopeAllowed</c> falso (só o catálogo no
/// <c>roles</c>), <c>basic</c> nos defaults (o <c>sub</c>), sem <c>profile</c> nem <c>email</c> (nenhum dado pessoal
/// no token), <c>gateway-api</c> (a audiência) e a rotação do refresh token.
/// </remarks>
public sealed class FormaDoTokenContraKeycloakTests(KeycloakFixture keycloak)
{
    private static readonly string[] Catalogo = ["platform-admin", "tenant-admin", "financial-manager", "reader"];

    private static readonly string[] SoTenantAdmin = ["tenant-admin"];

    private static readonly string[] SemPapeis = [];

    private static readonly string[] ClaimsQueNaoPodemSair =
    [
        "email", "email_verified", "preferred_username", "name", "given_name", "family_name", "realm_access",
        "resource_access",
    ];

    private async Task<JsonElement> PayloadDeAsync(UsuarioDeTeste usuario, CancellationToken ct)
    {
        using HarnessDeLogin harness = keycloak.CriarHarness();
        TokensDeUsuario tokens = await harness.TokenPorDispositivoAsync(usuario.Email, usuario.Senha, ct);

        return PayloadDoJwt.Ler(tokens.AccessToken);
    }

    private static string[] Roles(JsonElement payload) =>
        [.. payload.GetProperty("roles").EnumerateArray().Select(papel => papel.GetString()!)];

    [Fact]
    public async Task TokenDoTenantAdmin_TemAFormaQueAGatewayValida()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string tenant = TenantId.New().Value.ToString();
        UsuarioDeTeste usuario = await keycloak.NovoUsuarioAsync(SoTenantAdmin, tenant, ct);

        JsonElement payload = await PayloadDeAsync(usuario, ct);

        payload.GetProperty("iss").GetString().Should().Be($"{KeycloakFixture.HostnamePublico}/realms/{KeycloakFixture.Realm}");

        // aud como TEXTO, e só a Gateway: com um valor só, o audience mapper não emite array.
        payload.GetProperty("aud").ValueKind.Should().Be(JsonValueKind.String);
        payload.GetProperty("aud").GetString().Should().Be("identity-gateway-api");
        payload.GetProperty("azp").GetString().Should().Be(KeycloakFixture.ClientDeDemonstracao);
        payload.GetProperty("typ").GetString().Should().Be("Bearer");
        payload.GetProperty("acr").ValueKind.Should().Be(JsonValueKind.String);

        // sub: o id do usuário, GUID no formato D — vem do scope basic.
        payload.GetProperty("sub").GetString().Should().Be(usuario.Id);
        Guid.TryParseExact(usuario.Id, "D", out _).Should().BeTrue();

        payload.GetProperty("tenant_id").ValueKind.Should().Be(JsonValueKind.String);
        payload.GetProperty("tenant_id").GetString().Should().Be(tenant);
        payload.GetProperty("roles").ValueKind.Should().Be(JsonValueKind.Array);
        Roles(payload).Should().Equal("tenant-admin");

        // ADR-005: 5 minutos, configurados no realm.
        (payload.GetProperty("exp").GetInt64() - payload.GetProperty("iat").GetInt64()).Should().Be(300);

        // DT11: o token não carrega e-mail nem nome — ele vai a histórico de shell, a proxies e à demonstração.
        // Filtrado fora da asserção: um NotContain com lambda recebe árvore de expressão, que não aceita o descarte do out.
        ClaimsQueNaoPodemSair.Where(claim => payload.TryGetProperty(claim, out _)).Should().BeEmpty();
    }

    [Fact]
    public async Task TokenDoPlatformAdmin_TemOPapelENaoTemTenant()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        UsuarioDeTeste usuario = await keycloak.NovoPlatformAdminAsync(ct);

        JsonElement payload = await PayloadDeAsync(usuario, ct);

        Roles(payload).Should().Equal("platform-admin");
        payload.TryGetProperty("tenant_id", out _).Should().BeFalse();
        PayloadDoJwt.Audiencias(payload).Should().Equal("identity-gateway-api");
    }

    [Fact]
    public async Task Roles_SoTrazOCatalogo_MesmoComPapelForaDeleNoUsuario()
    {
        // Com fullScopeAllowed verdadeiro no demo, o claim traria default-roles-identity-gateway. A pré-condição é
        // conferida antes, pelo master: o usuário criado pela Admin API TEM um papel fora do catálogo. Sem ela o teste
        // seria vacuoso — o platform-admin do JSON, por exemplo, não tem o papel padrão.
        CancellationToken ct = TestContext.Current.CancellationToken;
        UsuarioDeTeste usuario = await keycloak.NovoUsuarioAsync(SoTenantAdmin, TenantId.New().Value.ToString(), ct);
        JsonElement efetivos = await keycloak.LerComoMasterAsync($"users/{usuario.Id}/role-mappings/realm/composite", ct);
        efetivos.EnumerateArray().Select(papel => papel.GetProperty("name").GetString())
            .Should().Contain($"default-roles-{KeycloakFixture.Realm}");

        JsonElement payload = await PayloadDeAsync(usuario, ct);

        Roles(payload).Should().BeSubsetOf(Catalogo).And.Equal("tenant-admin");
    }

    [Fact]
    public async Task UsuarioSemPapelDoCatalogo_TokenSaiSemOClaimRoles()
    {
        // Sem papel do catálogo, o mapper não emite o claim. A Api trata a ausência como "sem papel", não como erro.
        CancellationToken ct = TestContext.Current.CancellationToken;
        UsuarioDeTeste usuario = await keycloak.NovoUsuarioAsync(SemPapeis, tenantId: null, ct);

        JsonElement payload = await PayloadDeAsync(usuario, ct);

        payload.TryGetProperty("roles", out _).Should().BeFalse();
        payload.GetProperty("sub").GetString().Should().Be(usuario.Id);
    }

    [Fact]
    public async Task RefreshToken_RotacionaEOReusoDerrubaASessaoDoClient()
    {
        // D-n. A renovação devolve um refresh token NOVO, e o usado passa a ser recusado. E o reuso tem um efeito que
        // molda todo o resto: depois dele, até o refresh token novo é recusado — o Keycloak derruba a sessão daquele
        // client. Por isso ninguém neste repositório repete uma renovação. Sessão própria (harness e usuário só deste
        // teste), para o reuso não derrubar a sessão de ninguém.
        CancellationToken ct = TestContext.Current.CancellationToken;
        UsuarioDeTeste usuario = await keycloak.NovoPlatformAdminAsync(ct);
        using HarnessDeLogin harness = keycloak.CriarHarness();
        TokensDeUsuario primeiro = await harness.TokenPorDispositivoAsync(usuario.Email, usuario.Senha, ct);

        TokensDeUsuario renovado = await harness.RenovarAsync(primeiro.RefreshToken, ct);

        // Comparado fora da asserção: um NotBe que falhasse imprimiria os dois refresh tokens na mensagem.
        (renovado.RefreshToken == primeiro.RefreshToken).Should().BeFalse("a renovação devolve um refresh token novo");
        PayloadDoJwt.Ler(renovado.AccessToken).GetProperty("sub").GetString().Should().Be(usuario.Id);

        Func<Task> reuso = () => harness.RenovarAsync(primeiro.RefreshToken, ct);
        (await reuso.Should().ThrowAsync<FalhaDoHarnessException>()).Which.Message.Should().Contain("invalid_grant");

        Func<Task> depoisDoReuso = () => harness.RenovarAsync(renovado.RefreshToken, ct);
        (await depoisDoReuso.Should().ThrowAsync<FalhaDoHarnessException>("o reuso derrubou a sessão do client"))
            .Which.Message.Should().Contain("invalid_grant");
    }

    [Fact]
    public async Task GrupoComTenantId_DaOClaimAUsuarioSemOAtributo()
    {
        // CARACTERIZAÇÃO, não requisito: o mapper de atributo recua para o atributo de mesmo nome do grupo, e não há
        // configuração que desligue isso. É o motivo de a rota de tenant exigir a pertença no banco (ADR-011). Se um
        // upgrade do Keycloak mudar este comportamento, o teste avisa — e a decisão pode ser revista.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string forjado = TenantId.New().Value.ToString();
        UsuarioDeTeste usuario = await keycloak.NovoUsuarioAsync(SoTenantAdmin, tenantId: null, ct);
        string grupo = await keycloak.CriarGrupoComoMasterAsync(
            $"g-{Guid.NewGuid():N}", new Dictionary<string, string[]> { ["tenant_id"] = [forjado] }, ct);

        try
        {
            await keycloak.PorNoGrupoComoMasterAsync(usuario.Id, grupo, ct);

            JsonElement payload = await PayloadDeAsync(usuario, ct);

            payload.GetProperty("tenant_id").GetString().Should().Be(forjado);
            Roles(payload).Should().Equal("tenant-admin");
        }
        finally
        {
            await keycloak.ApagarGrupoComoMasterAsync(grupo, CancellationToken.None);
        }
    }
}
