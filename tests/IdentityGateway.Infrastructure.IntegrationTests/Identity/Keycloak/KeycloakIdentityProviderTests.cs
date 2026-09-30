using System.Net;
using System.Text;
using System.Text.Json;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Domain.ValueObjects;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using Microsoft.Extensions.Logging.Abstractions;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O que o adaptador faz com cada resposta da Admin API — sem Keycloak. O comportamento do Keycloak de verdade é
/// provado em <c>EnsureOrganizationContraKeycloakTests</c> e <c>EnsureInvitedUserContraKeycloakTests</c>.
/// </summary>
public sealed class KeycloakIdentityProviderTests
{
    private static readonly TenantId Tenant = new(Guid.Parse("0199a1b2-0000-7000-8000-000000000001"));
    private static readonly TenantSlug Slug = TenantSlug.Create("acme").Value;

    private static readonly InviteData Convite =
        new(Email.Of("admin+tag@acme.test").Value, RoleName.TenantAdmin, TimeSpan.FromDays(7));

    private static HttpResponseMessage Lista(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Criada(string id)
    {
        HttpResponseMessage resposta = new(HttpStatusCode.Created);
        resposta.Headers.Location = new Uri($"http://keycloak.test/admin/realms/identity-gateway/organizations/{id}");
        return resposta;
    }

    private static HttpResponseMessage UsuarioCriado(string id)
    {
        HttpResponseMessage resposta = new(HttpStatusCode.Created);
        resposta.Headers.Location = new Uri($"http://keycloak.test/admin/realms/identity-gateway/users/{id}");
        return resposta;
    }

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status);

    private static string UsuarioDoTenant(string id, params string[] acoes) =>
        $$$"""[{"id":"{{{id}}}","username":"admin+tag@acme.test","email":"admin+tag@acme.test","enabled":true,"requiredActions":[{{{string.Join(",", acoes.Select(acao => $"\"{acao}\""))}}}],"attributes":{"tenant_id":["{{{Tenant.Value}}}"]}}]""";

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

    [Fact]
    public async Task Convite_BuscaComEmailEscapadoExactEBriefRepresentationFalse()
    {
        // Forma da busca, comparada sem decodificar: o + sem escape vira espaço, e a busca exata não acha ninguém.
        // É também o único teste que pega a remoção do briefRepresentation=false — o Keycloak 26.7.4 devolve a
        // representação completa por padrão, e o teste de integração não vê diferença.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Uri? consultada = null;

        (KeycloakIdentityProvider adaptador, List<HttpMethod> _) = Montar(
            pedido =>
            {
                consultada = pedido.RequestUri;
                return Lista(UsuarioDoTenant("u-1"));
            },
            _ => Status(HttpStatusCode.Created),
            _ => Lista("""[{"id":"r-1","name":"tenant-admin"}]"""));

        await adaptador.EnsureInvitedUserAsync("org-1", Tenant, Convite, ct);

        consultada!.AbsolutePath.Should().EndWith("/admin/realms/identity-gateway/users");
        consultada.Query.Should().Be("?email=admin%2Btag%40acme.test&exact=true&briefRepresentation=false");
    }

    [Fact]
    public async Task UsuarioQueJaAceitou_NaoEnviaEmail()
    {
        // D12: sem UPDATE_PASSWORD pendente, o convite já foi aceito; um link agora trocaria a senha de um admin ativo.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (KeycloakIdentityProvider adaptador, List<HttpMethod> metodos) = Montar(
            _ => Lista(UsuarioDoTenant("u-1")),
            _ => Status(HttpStatusCode.Conflict),
            _ => Lista("""[{"id":"r-1","name":"tenant-admin"}]"""));

        ExternalUserId sub = await adaptador.EnsureInvitedUserAsync("org-1", Tenant, Convite, ct);

        sub.Value.Should().Be("u-1");
        metodos.Should().Equal(HttpMethod.Get, HttpMethod.Post, HttpMethod.Get);
    }

    [Fact]
    public async Task ConflitoSemNossoUsuarioNaReconsulta_LancaInconsistenciaSemLaco()
    {
        // Uma reconsulta só (por e-mail e por username), nunca em laço: o 409 também sai quando outro usuário tem
        // username igual ao nosso e-mail.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (KeycloakIdentityProvider adaptador, List<HttpMethod> metodos) = Montar(
            _ => Lista("[]"),
            _ => Status(HttpStatusCode.Conflict),
            _ => Lista("[]"),
            _ => Lista("""[{"id":"outro","username":"admin+tag@acme.test","email":"outro@acme.test","enabled":true}]"""));

        Func<Task> convidar = () => adaptador.EnsureInvitedUserAsync("org-1", Tenant, Convite, ct);

        (await convidar.Should().ThrowAsync<IdentityProviderInconsistencyException>())
            .Which.Message.Should().Contain(Tenant.Value.ToString()).And.NotContain("admin+tag");
        metodos.Should().Equal(HttpMethod.Get, HttpMethod.Post, HttpMethod.Get, HttpMethod.Get);
    }

    [Fact]
    public async Task UsuarioDeOutroTenant_LancaInconsistenciaSemOEmail()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (KeycloakIdentityProvider adaptador, List<HttpMethod> metodos) = Montar(_ => Lista(
            """[{"id":"u-9","email":"admin+tag@acme.test","enabled":true,"attributes":{"tenant_id":["0199a1b2-0000-7000-8000-000000000999"]}}]"""));

        Func<Task> convidar = () => adaptador.EnsureInvitedUserAsync("org-1", Tenant, Convite, ct);

        (await convidar.Should().ThrowAsync<IdentityProviderInconsistencyException>())
            .Which.Message.Should().NotContain("admin+tag");
        metodos.Should().Equal(HttpMethod.Get);
    }

    [Fact]
    public async Task EnvioCom400_LancaInconsistenciaSemOEmail()
    {
        // 400 no execute-actions-email: usuário desabilitado à mão ou sem e-mail. Repetir não corrige.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (KeycloakIdentityProvider adaptador, List<HttpMethod> _) = Montar(
            _ => Lista(UsuarioDoTenant("u-1", "UPDATE_PASSWORD", "VERIFY_EMAIL")),
            _ => Status(HttpStatusCode.Created),
            _ => Lista("""[{"id":"r-1","name":"tenant-admin"}]"""),
            _ => Status(HttpStatusCode.BadRequest));

        Func<Task> convidar = () => adaptador.EnsureInvitedUserAsync("org-1", Tenant, Convite, ct);

        (await convidar.Should().ThrowAsync<IdentityProviderInconsistencyException>())
            .Which.Message.Should().NotContain("admin+tag");
    }

    [Fact]
    public async Task EnvioCom500_SobeComoTransitorio()
    {
        // SMTP fora do ar vira 500 no Keycloak (UserResource.java L1073-1075): transitório, o Outbox repete.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (KeycloakIdentityProvider adaptador, List<HttpMethod> _) = Montar(
            _ => Lista(UsuarioDoTenant("u-1", "UPDATE_PASSWORD", "VERIFY_EMAIL")),
            _ => Status(HttpStatusCode.Created),
            _ => Lista("""[{"id":"r-1","name":"tenant-admin"}]"""),
            _ => Status(HttpStatusCode.InternalServerError));

        Func<Task> convidar = () => adaptador.EnsureInvitedUserAsync("org-1", Tenant, Convite, ct);

        await convidar.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task UsuarioNovo_CriaComUsernameIgualAoEmailHabilitadoComAcoesEAtributoEEnviaComOPrazo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string? corpoDaCriacao = null;
        string? corpoDoEnvio = null;
        Uri? envio = null;

        (KeycloakIdentityProvider adaptador, List<HttpMethod> metodos) = Montar(
            _ => Lista("[]"),
            pedido =>
            {
                corpoDaCriacao = pedido.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
                return UsuarioCriado("u-novo");
            },
            _ => Status(HttpStatusCode.Created),
            _ => Lista("[]"),
            _ => Lista("""[{"id":"r-1","name":"tenant-admin"},{"id":"r-2","name":"offline_access"}]"""),
            _ => Status(HttpStatusCode.NoContent),
            pedido =>
            {
                envio = pedido.RequestUri;
                corpoDoEnvio = pedido.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
                return Status(HttpStatusCode.NoContent);
            });

        ExternalUserId sub = await adaptador.EnsureInvitedUserAsync("org-1", Tenant, Convite, ct);

        sub.Value.Should().Be("u-novo");
        metodos.Should().Equal(
            HttpMethod.Get, HttpMethod.Post, HttpMethod.Post, HttpMethod.Get, HttpMethod.Get, HttpMethod.Post,
            HttpMethod.Put);

        // Comparado pelo valor desserializado, não pelo texto: o encoder padrão escreve o + como +.
        using var criacao = JsonDocument.Parse(corpoDaCriacao!);
        JsonElement usuario = criacao.RootElement;
        usuario.TryGetProperty("id", out _).Should().BeFalse("nulo fica fora do JSON");
        usuario.GetProperty("username").GetString().Should().Be("admin+tag@acme.test");
        usuario.GetProperty("email").GetString().Should().Be("admin+tag@acme.test");
        usuario.GetProperty("enabled").GetBoolean().Should().BeTrue();
        usuario.GetProperty("requiredActions").EnumerateArray().Select(acao => acao.GetString())
            .Should().Equal("UPDATE_PASSWORD", "VERIFY_EMAIL");
        usuario.GetProperty("attributes").GetProperty("tenant_id").EnumerateArray().Select(valor => valor.GetString())
            .Should().Equal(Tenant.Value.ToString());

        using var acoes = JsonDocument.Parse(corpoDoEnvio!);
        acoes.RootElement.EnumerateArray().Select(acao => acao.GetString())
            .Should().Equal("UPDATE_PASSWORD", "VERIFY_EMAIL");
        envio!.Query.Should().Be("?lifespan=604800", "7 dias em segundos inteiros (TotalSeconds, não Seconds)");
    }
}
