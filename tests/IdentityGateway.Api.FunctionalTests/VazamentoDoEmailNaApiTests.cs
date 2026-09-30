using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// D15 na borda HTTP: nenhuma resposta — nem os ProblemDetails, que em Development trazem detalhe — devolve o e-mail.
/// </summary>
public sealed class VazamentoDoEmailNaApiTests(IdentityGatewayApiFactory factory)
    : IClassFixture<IdentityGatewayApiFactory>
{
    private const string Rota = "/api/v1/tenants";

    private static object Corpo(string slug, string email) => new
    {
        name = "Acme Corp",
        slug,
        planCode = "free",
        initialAdminEmail = email,
    };

    private static string SlugUnico() => $"vaza-{Guid.NewGuid():N}"[..20];

    [Fact]
    public async Task EmailMalFormado_400SemOValor()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");
        string marca = $"segredo{Guid.NewGuid():N}";

        using HttpResponseMessage resposta = await client.PostAsJsonAsync(
            Rota, Corpo(SlugUnico(), $"{marca}@@acme.test"), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resposta.Content.ReadAsStringAsync(ct)).Should().NotContain(marca);
    }

    [Fact]
    public async Task RegistroAceitoEConsulta_NaoDevolvemOEmail()
    {
        // Nenhum read model expõe o InitialAdminEmail, inclusive o GET .../provisioning (§4.8).
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");
        string email = $"segredo+{Guid.NewGuid():N}@acme.test";

        using HttpResponseMessage aceito = await client.PostAsJsonAsync(Rota, Corpo(SlugUnico(), email), ct);
        string corpoDoAceito = await aceito.Content.ReadAsStringAsync(ct);
        string id = JsonDocument.Parse(corpoDoAceito).RootElement.GetProperty("tenantId").GetString()!;
        using HttpResponseMessage consulta = await client.GetAsync(
            new Uri($"{Rota}/{id}/provisioning", UriKind.Relative), ct);

        aceito.StatusCode.Should().Be(HttpStatusCode.Accepted);
        corpoDoAceito.Should().NotContain("segredo");
        (await consulta.Content.ReadAsStringAsync(ct)).Should().NotContain("segredo");
    }

    [Fact]
    public async Task SlugDuplicado_409SemOEmail()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");
        string slug = SlugUnico();
        using HttpResponseMessage primeira = await client.PostAsJsonAsync(
            Rota, Corpo(slug, $"primeiro+{Guid.NewGuid():N}@acme.test"), ct);
        string email = $"segredo+{Guid.NewGuid():N}@acme.test";

        using HttpResponseMessage segunda = await client.PostAsJsonAsync(Rota, Corpo(slug, email), ct);

        primeira.StatusCode.Should().Be(HttpStatusCode.Accepted);
        segunda.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await segunda.Content.ReadAsStringAsync(ct)).Should().NotContain("segredo");
    }
}
