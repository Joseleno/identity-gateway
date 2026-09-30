using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O service account autentica por <c>private_key_jwt</c> com <c>aud</c> = issuer, e tem só o papel que precisa.
/// </summary>
public sealed class KeycloakRealTests(KeycloakFixture keycloak)
{
    private static readonly string[] PapeisEsperados = ["manage-organizations", "manage-users"];

    private static readonly string[] PapeisProibidos =
        ["impersonation", "realm-admin", "manage-realm", "manage-clients", "manage-identity-providers"];

    private static readonly string[] PapeisDeRealmAceitos =
        ["default-roles-identity-gateway", "offline_access", "uma_authorization"];

    [Fact]
    public async Task ComAChaveRegistrada_ObtemToken()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();

        TokenObtido token = await provider.GetRequiredService<ITokenEndpoint>().ObterAsync(ct);

        token.AccessToken.Should().NotBeNullOrEmpty();
        token.ExpiraEm.Should().BePositive();
    }

    [Fact]
    public async Task ComOutraChave_RecebeInvalidClient()
    {
        // A metade que prova que a primeira não passa por acaso: assinatura com chave que o realm não conhece.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider(pem: ChavesDeTeste.Gerar().PemPrivado);

        Func<Task> obter = () => provider.GetRequiredService<ITokenEndpoint>().ObterAsync(ct);

        await obter.Should().ThrowAsync<HttpRequestException>().WithMessage("*invalid_client*");
    }

    [Fact]
    public async Task ServiceAccount_RecebeProibidoEmClientsENaConfiguracaoDoRealm()
    {
        // Com manage-users (fatia C), GET /users deixou de ser o negativo. O que o service account continua sem poder:
        // ler clients (view-clients) e alterar o realm (manage-realm).
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();
        string token = await provider.GetRequiredService<ServiceAccountTokenCache>().ObterAsync(ct);

        using HttpClient http = new() { BaseAddress = new Uri($"{keycloak.BaseUrl}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage clients = await http.GetAsync(
            new Uri($"admin/realms/{KeycloakFixture.Realm}/clients", UriKind.Relative), ct);
        using StringContent corpo = new("""{"realm":"identity-gateway"}""", Encoding.UTF8, "application/json");
        using HttpResponseMessage realm = await http.PutAsync(
            new Uri($"admin/realms/{KeycloakFixture.Realm}", UriKind.Relative), corpo, ct);

        clients.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        realm.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ServiceAccount_PapeisEfetivosSaoExatamenteOsDois()
    {
        // D6: os papéis EFETIVOS, com compostos expandidos (role-mappings/.../composite). Um composto como realm-admin
        // passaria num teste que olhasse só os papéis atribuídos diretamente.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient master = await keycloak.CriarClienteMasterAsync(ct);
        string realm = KeycloakFixture.Realm;

        using var usuarios = JsonDocument.Parse(await master.GetStringAsync(
            new Uri($"admin/realms/{realm}/users?username=service-account-identity-gateway&exact=true",
                UriKind.Relative), ct));
        string usuarioId = usuarios.RootElement[0].GetProperty("id").GetString()!;

        using var clientes = JsonDocument.Parse(await master.GetStringAsync(
            new Uri($"admin/realms/{realm}/clients?clientId=realm-management", UriKind.Relative), ct));
        string realmManagementId = clientes.RootElement[0].GetProperty("id").GetString()!;

        using var efetivosDoCliente = JsonDocument.Parse(await master.GetStringAsync(
            new Uri($"admin/realms/{realm}/users/{usuarioId}/role-mappings/clients/{realmManagementId}/composite",
                UriKind.Relative), ct));
        using var efetivosDoRealm = JsonDocument.Parse(await master.GetStringAsync(
            new Uri($"admin/realms/{realm}/users/{usuarioId}/role-mappings/realm/composite", UriKind.Relative), ct));

        List<string> papeisDoCliente = [.. efetivosDoCliente.RootElement.EnumerateArray()
            .Select(papel => papel.GetProperty("name").GetString()!)];
        List<string> papeisDoRealm = [.. efetivosDoRealm.RootElement.EnumerateArray()
            .Select(papel => papel.GetProperty("name").GetString()!)];

        papeisDoCliente.Should().BeEquivalentTo(PapeisEsperados);
        papeisDoCliente.Should().NotContain(
            PapeisProibidos);
        papeisDoRealm.Should().BeSubsetOf(
            PapeisDeRealmAceitos,
            "nenhum papel de negócio (tenant-admin, platform-admin) no service account");
    }

    [Fact]
    public async Task EndpointDeOrganizations_NaoResponde404()
    {
        // O smoke do realm: sem organizationsEnabled, este endpoint responde 404 e o import passa sem erro.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();
        string token = await provider.GetRequiredService<ServiceAccountTokenCache>().ObterAsync(ct);

        using HttpClient http = new() { BaseAddress = new Uri($"{keycloak.BaseUrl}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage resposta = await http.GetAsync(
            new Uri($"admin/realms/{KeycloakFixture.Realm}/organizations?max=1", UriKind.Relative), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HealthCheck_ComKeycloakDePe_Healthy()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();

        HealthReport relatorio = await provider.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(registro => registro.Name == "keycloak", ct);

        relatorio.Entries["keycloak"].Status.Should().Be(HealthStatus.Healthy);
    }
}
