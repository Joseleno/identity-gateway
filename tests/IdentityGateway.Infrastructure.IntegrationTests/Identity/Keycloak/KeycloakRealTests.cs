using System.Net;
using System.Net.Http.Headers;
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
    public async Task ServiceAccount_RecebeProibidoEmUsuarios()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();
        string token = await provider.GetRequiredService<ServiceAccountTokenCache>().ObterAsync(ct);

        using HttpClient http = new() { BaseAddress = new Uri($"{keycloak.BaseUrl}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage resposta = await http.GetAsync(
            new Uri($"admin/realms/{KeycloakFixture.Realm}/users", UriKind.Relative), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ServiceAccount_TemExatamenteManageOrganizations()
    {
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

        using var papeis = JsonDocument.Parse(await master.GetStringAsync(
            new Uri($"admin/realms/{realm}/users/{usuarioId}/role-mappings/clients/{realmManagementId}",
                UriKind.Relative), ct));

        papeis.RootElement.EnumerateArray().Select(papel => papel.GetProperty("name").GetString())
            .Should().Equal("manage-organizations");
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
