using System.Net;
using System.Text;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using Microsoft.Extensions.Logging.Abstractions;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O que o adaptador faz com cada resposta da Admin API — sem Keycloak. O comportamento do Keycloak de verdade é
/// provado em <c>EnsureOrganizationContraKeycloakTests</c>.
/// </summary>
public sealed class KeycloakIdentityProviderTests
{
    private static readonly TenantId Tenant = new(Guid.Parse("0199a1b2-0000-7000-8000-000000000001"));
    private static readonly TenantSlug Slug = TenantSlug.Create("acme").Value;

    private static HttpResponseMessage Lista(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Criada(string id)
    {
        HttpResponseMessage resposta = new(HttpStatusCode.Created);
        resposta.Headers.Location = new Uri($"http://keycloak.test/admin/realms/identity-gateway/organizations/{id}");
        return resposta;
    }

    private static (KeycloakIdentityProvider Adaptador, List<HttpMethod> Metodos) Montar(
        params Func<HttpRequestMessage, HttpResponseMessage>[] respostas)
    {
        List<HttpMethod> metodos = [];
        int indice = 0;

        HandlerFalso handler = new((pedido, _) =>
        {
            metodos.Add(pedido.Method);
            return Task.FromResult(respostas[indice++](pedido));
        });

        KeycloakAdminClient admin = new(
            new HttpClient(handler) { BaseAddress = new Uri("http://keycloak.test/") },
            OpcoesDeTeste.Keycloak());

        return (new KeycloakIdentityProvider(admin, NullLogger<KeycloakIdentityProvider>.Instance), metodos);
    }

    [Fact]
    public async Task JaExiste_DevolveOIdSemCriar()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (KeycloakIdentityProvider adaptador, List<HttpMethod> metodos) = Montar(_ => Lista("""[{"id":"org-1","name":"acme","alias":"acme","enabled":true}]"""));

        string id = await adaptador.EnsureOrganizationAsync(Tenant, Slug, "Acme", ct);

        id.Should().Be("org-1");
        metodos.Should().Equal(HttpMethod.Get);
    }

    [Fact]
    public async Task NaoExiste_CriaComNameAliasDescriptionEAtributo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string? corpo = null;

        (KeycloakIdentityProvider adaptador, List<HttpMethod> _) = Montar(
            _ => Lista("[]"),
            pedido =>
            {
                corpo = pedido.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
                return Criada("org-2");
            });

        string id = await adaptador.EnsureOrganizationAsync(Tenant, Slug, "Acme Ltda", ct);

        id.Should().Be("org-2");
        corpo.Should().Contain("\"name\":\"acme\"")
            .And.Contain("\"alias\":\"acme\"")
            .And.Contain("\"description\":\"Acme Ltda\"")
            .And.Contain($"\"gateway_tenant_id\":[\"{Tenant.Value}\"]");
    }

    [Fact]
    public async Task BuscaUsaQComBriefRepresentationFalse()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Uri? consultada = null;

        (KeycloakIdentityProvider adaptador, List<HttpMethod> _) = Montar(pedido =>
        {
            consultada = pedido.RequestUri;
            return Lista("""[{"id":"org-1","name":"acme","alias":"acme","enabled":true}]""");
        });

        await adaptador.EnsureOrganizationAsync(Tenant, Slug, "Acme", ct);

        // Comparado já decodificado: se o Uri normaliza ou não o %3A é detalhe do .NET, e não o que se quer provar.
        Uri.UnescapeDataString(consultada!.Query).Should().Be(
            $"?q=gateway_tenant_id:{Tenant.Value}&briefRepresentation=false&max=2");
    }

    [Fact]
    public async Task ConflitoEReconsultaAcha_DevolveOIdDaVencedora()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (KeycloakIdentityProvider adaptador, List<HttpMethod> metodos) = Montar(
            _ => Lista("[]"),
            _ => new HttpResponseMessage(HttpStatusCode.Conflict),
            _ => Lista("""[{"id":"org-3","name":"acme","alias":"acme","enabled":true}]"""));

        string id = await adaptador.EnsureOrganizationAsync(Tenant, Slug, "Acme", ct);

        id.Should().Be("org-3");
        metodos.Should().Equal(HttpMethod.Get, HttpMethod.Post, HttpMethod.Get);
    }

    [Fact]
    public async Task ConflitoEReconsultaVazia_LancaInconsistencia()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (KeycloakIdentityProvider adaptador, List<HttpMethod> _) = Montar(
            _ => Lista("[]"),
            _ => new HttpResponseMessage(HttpStatusCode.Conflict),
            _ => Lista("[]"));

        Func<Task> garantir = () => adaptador.EnsureOrganizationAsync(Tenant, Slug, "Acme", ct);

        await garantir.Should().ThrowAsync<IdentityProviderInconsistencyException>().WithMessage("*acme*");
    }

    [Fact]
    public async Task MaisDeUmaComOAtributo_LancaInconsistencia()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (KeycloakIdentityProvider adaptador, List<HttpMethod> _) = Montar(_ => Lista(
            """[{"id":"a","name":"x","alias":"x","enabled":true},{"id":"b","name":"y","alias":"y","enabled":true}]"""));

        Func<Task> garantir = () => adaptador.EnsureOrganizationAsync(Tenant, Slug, "Acme", ct);

        await garantir.Should().ThrowAsync<IdentityProviderInconsistencyException>();
    }

    [Fact]
    public async Task ErroDoServidor_SobeComoHttpRequestException()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (KeycloakIdentityProvider adaptador, List<HttpMethod> _) = Montar(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        Func<Task> garantir = () => adaptador.EnsureOrganizationAsync(Tenant, Slug, "Acme", ct);

        // Transiente: é o consumidor quem decide repetir. O adaptador não engole nada.
        await garantir.Should().ThrowAsync<HttpRequestException>();
    }
}
