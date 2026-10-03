using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IdentityGateway.Api.FunctionalTests.Oidc;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using IdentityGateway.Testing.Keycloak;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// Tokens emitidos pelo Keycloak de verdade, atravessando a Api de verdade.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cada <c>401</c> daqui vem acompanhado da forma do token que o recebeu.</b> "A API respondeu 401" sozinho é
/// sobredeterminado — pode ser a audiência, o <c>azp</c>, o emissor, a assinatura. O teste afirma antes o que o token
/// tem e o que não tem, para o <c>401</c> ser o esperado pelo motivo esperado.
/// </para>
/// <para>
/// <b>Um platform-admin por teste</b> (<c>NovoPlatformAdminAsync</c>): o link de ações é de uso único.
/// </para>
/// </remarks>
[Collection(ColecaoComKeycloak.Nome)]
public sealed class TokensDoKeycloakNaApiTests(ApiComKeycloakFactory api)
{
    private const string AudienciaDaGateway = "identity-gateway-api";

    private static readonly string[] SoTenantAdmin = ["tenant-admin"];

    private static readonly string[] SemPapeis = [];

    private static object CorpoDoRegistro() => new
    {
        name = "Acme Corp",
        slug = $"kc-{Guid.NewGuid():N}"[..20],
        planCode = "free",
        initialAdminEmail = KeycloakFixture.EmailUnico(),
    };

    private async Task<HttpResponseMessage> RegistrarComAsync(string accessToken, CancellationToken ct)
    {
        using HttpClient client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await client.PostAsJsonAsync("/api/v1/tenants", CorpoDoRegistro(), ct);
    }

    [Fact]
    public async Task PlatformAdminDoKeycloak_RegistraUmTenant()
    {
        // O critério do M0: um token do Keycloak, obtido pelo device flow, numa rota protegida.
        CancellationToken ct = TestContext.Current.CancellationToken;
        UsuarioDeTeste admin = await api.Keycloak.NovoPlatformAdminAsync(ct);
        using HttpClient client = await api.CriarClienteComoAsync(admin, ct);

        using HttpResponseMessage resposta = await client.PostAsJsonAsync("/api/v1/tenants", CorpoDoRegistro(), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Accepted);
        resposta.Headers.Location.Should().NotBeNull();
    }

    [Fact]
    public async Task TenantAdminDoKeycloak_AutenticaMasNaoRegistraTenant()
    {
        // 403, e não 401: o token é aceito; o que falta é o papel.
        CancellationToken ct = TestContext.Current.CancellationToken;
        UsuarioDeTeste admin = await api.Keycloak.NovoUsuarioAsync(SoTenantAdmin, Guid.NewGuid().ToString(), ct);
        using HttpClient client = await api.CriarClienteComoAsync(admin, ct);

        using HttpResponseMessage resposta = await client.PostAsJsonAsync("/api/v1/tenants", CorpoDoRegistro(), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PonteDeContrato_OsClaimsDoTokenRealTemOsTiposDosDoEmissorDeTeste()
    {
        // Os testes funcionais confiam que o emissor de teste imita o token real. Aqui a imitação é conferida: os
        // mesmos claims, com os mesmos tipos JSON. Um claim a mais no token real (um mapper novo no realm) ou um tipo
        // diferente (aud virando array) reprova — antes de os testes funcionais passarem a provar outra coisa.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string tenant = Guid.NewGuid().ToString();
        UsuarioDeTeste admin = await api.Keycloak.NovoUsuarioAsync(SoTenantAdmin, tenant, ct);
        using HarnessDeLogin harness = api.Keycloak.CriarHarness();
        TokensDeUsuario tokens = await harness.TokenPorDispositivoAsync(admin.Email, admin.Senha, ct);
        using EmissorDeTeste emissor = new("https://irrelevante.test/realms/identity-gateway");

        JsonElement real = PayloadDoJwt.Ler(tokens.AccessToken);
        JsonElement forjado = JsonSerializer.SerializeToElement(emissor.Payload(roles: SoTenantAdmin, tenantId: tenant));

        var tiposDoReal = real.EnumerateObject()
            .ToDictionary(claim => claim.Name, claim => claim.Value.ValueKind);
        var tiposDoForjado = forjado.EnumerateObject()
            .ToDictionary(claim => claim.Name, claim => claim.Value.ValueKind);

        tiposDoForjado.Should().BeEquivalentTo(tiposDoReal);
    }

    [Fact]
    public async Task TokenDoClientDeDeviceFlowDoFixture_AzpAceitoSemAAudiencia_401()
    {
        // O client está na lista de azp desta factory e não tem o scope gateway-api: o 401 só pode vir da audiência.
        // É a testemunha, no Keycloak real, de que ValidateAudience está ligado e de que gateway-api não é default.
        CancellationToken ct = TestContext.Current.CancellationToken;
        UsuarioDeTeste usuario = await api.Keycloak.NovoPlatformAdminAsync(ct);
        using HarnessDeLogin harness = api.Keycloak.CriarHarness(KeycloakFixture.ClientDeConta);
        TokensDeUsuario tokens = await harness.TokenPorDispositivoAsync(usuario.Email, usuario.Senha, ct);
        JsonElement payload = PayloadDoJwt.Ler(tokens.AccessToken);

        payload.GetProperty("azp").GetString().Should().Be(KeycloakFixture.ClientDeConta);
        PayloadDoJwt.Audiencias(payload).Should().NotContain(AudienciaDaGateway);

        using HttpResponseMessage resposta = await RegistrarComAsync(tokens.AccessToken, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TokenDoServiceAccountDaGateway_401()
    {
        // A chave da Gateway abre a Admin API do Keycloak; não pode abrir a própria Gateway. O token do service account
        // não tem a audiência (sem gateway-api) nem azp aceito.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string token = await api.Services.GetRequiredService<ServiceAccountTokenCache>().ObterAsync(ct);
        JsonElement payload = PayloadDoJwt.Ler(token);

        payload.GetProperty("azp").GetString().Should().Be("identity-gateway");
        PayloadDoJwt.Audiencias(payload).Should().NotContain(AudienciaDaGateway);

        using HttpResponseMessage resposta = await RegistrarComAsync(token, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TokenPorSenhaDoAdminCliDoRealm_401()
    {
        // ADR-003: o admin-cli embutido do realm mantém o direct grant (não é declarado no JSON, e o Keycloak o cria
        // assim). O que a Gateway garante é que nenhum token obtido por senha é aceito por ela: o do admin-cli é um
        // token leve, sem audiência, e o azp não está na lista. O usuário aqui até tem o papel platform-admin.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string email = KeycloakFixture.EmailUnico();
        string senha = SenhasDeTeste.Gerar();
        string id = await api.Keycloak.CriarUsuarioComoMasterAsync(
            new
            {
                username = email,
                email,
                emailVerified = true,
                firstName = "Por",
                lastName = "Senha",
                enabled = true,
                credentials = new[] { new { type = "password", value = senha, temporary = false } },
            },
            ct);
        await api.Keycloak.AtribuirPapelDeRealmComoMasterAsync(id, "platform-admin", ct);

        using HttpClient keycloak = new() { BaseAddress = new Uri($"{api.Keycloak.BaseUrl}/") };
        using FormUrlEncodedContent corpo = new(
        [
            new("grant_type", "password"),
            new("client_id", "admin-cli"),
            new("username", email),
            new("password", senha),
        ]);
        using HttpResponseMessage emitido = await keycloak.PostAsync(
            new Uri($"realms/{KeycloakFixture.Realm}/protocol/openid-connect/token", UriKind.Relative), corpo, ct);
        emitido.StatusCode.Should().Be(HttpStatusCode.OK, "controle: o Keycloak emite o token por senha no admin-cli");
        JsonElement resposta = await emitido.Content.ReadFromJsonAsync<JsonElement>(ct);
        string token = resposta.GetProperty("access_token").GetString()!;
        JsonElement payload = PayloadDoJwt.Ler(token);

        payload.GetProperty("azp").GetString().Should().Be("admin-cli");
        PayloadDoJwt.Audiencias(payload).Should().NotContain(AudienciaDaGateway);

        using HttpResponseMessage naApi = await RegistrarComAsync(token, ct);

        naApi.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ApiComOEnderecoPublicoErrado_RecusaTokenRealDoKeycloak()
    {
        // O emissor aceito é o configurado, e não o que o discovery do Keycloak anuncia. Com o PublicBaseUrl errado, os
        // metadados continuam vindo (pelo BaseUrl), a assinatura confere — e o token é recusado pelo emissor. Sem o
        // IssuerValidator estrito, este teste ficaria 202: a biblioteca aceitaria o emissor do discovery.
        CancellationToken ct = TestContext.Current.CancellationToken;
        UsuarioDeTeste admin = await api.Keycloak.NovoPlatformAdminAsync(ct);
        using HarnessDeLogin harness = api.Keycloak.CriarHarness();
        TokensDeUsuario tokens = await harness.TokenPorDispositivoAsync(admin.Email, admin.Senha, ct);
        using WebApplicationFactory<Program> comEmissorErrado = api.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Keycloak:Admin:PublicBaseUrl", "http://outro-endereco.test:8081");
            builder.UseSetting("Outbox:Enabled", "false");
        });
        using HttpClient client = comEmissorErrado.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        using HttpResponseMessage resposta = await client.PostAsJsonAsync("/api/v1/tenants", CorpoDoRegistro(), ct);
        using HttpResponseMessage controle = await RegistrarComAsync(tokens.AccessToken, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        controle.StatusCode.Should().Be(HttpStatusCode.Accepted, "o mesmo token entra na Api com o endereço certo");
    }

    [Fact]
    public async Task UsuarioSemPapelDoCatalogo_AutenticaELeva403()
    {
        // O token sai sem o claim roles. A Api trata como "sem papel": 403, não erro.
        CancellationToken ct = TestContext.Current.CancellationToken;
        UsuarioDeTeste usuario = await api.Keycloak.NovoUsuarioAsync(SemPapeis, tenantId: null, ct);
        using HttpClient client = await api.CriarClienteComoAsync(usuario, ct);

        using HttpResponseMessage resposta = await client.PostAsJsonAsync("/api/v1/tenants", CorpoDoRegistro(), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Ready_ComKeycloakPostgresERedisDePe_Responde200()
    {
        // O /health/ready com tudo de verdade: o token do service account sai, com manage-users.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = api.CreateClient();

        using HttpResponseMessage resposta = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
