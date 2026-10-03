using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Domain.ValueObjects;
using IdentityGateway.Infrastructure.Persistence;
using IdentityGateway.Testing.Keycloak;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// A leitura de tenant com tudo de verdade: o Keycloak emite o token, o Outbox provisiona, e o banco diz quem é membro.
/// </summary>
/// <remarks>
/// <para>
/// <b>O que os testes com o OIDC falso não provam:</b> que o admin convidado pelo provisionamento real recebe do
/// Keycloak um token com <c>tenant-admin</c> e o <c>tenant_id</c> do tenant dele, e que o <c>sub</c> desse token é o
/// que o provisionamento gravou como <c>Member</c>. Se uma das duas pontas divergir, a rota responde <c>403</c> ao
/// dono do tenant — e só aqui isso aparece.
/// </para>
/// <para>
/// <b>Cada <c>403</c> vem com a premissa afirmada antes:</b> o que o token traz, lido do próprio token. Um
/// <c>403</c> sozinho pode ser de qualquer camada.
/// </para>
/// </remarks>
[Collection(ColecaoComKeycloak.Nome)]
public sealed class LeituraDeTenantComKeycloakTests(ApiComKeycloakFactory api)
{
    private static readonly string[] SoTenantAdmin = ["tenant-admin"];

    private static readonly string[] ChavesDoTenant =
        ["tenantId", "name", "slug", "status", "plan", "occupiedSeats", "registeredAt"];

    private static readonly string[] ChavesDoPlano = ["tier", "maxUsers", "maxClients"];

    private static readonly TimeSpan PrazoDoProvisionamento = TimeSpan.FromSeconds(90);

    private static string Rota(Guid tenant) => $"/api/v1/tenants/{tenant}";

    private static async Task<(Guid Tenant, Uri Acompanhamento)> RegistrarAsync(
        HttpClient comoPlatformAdmin, string emailDoAdmin, CancellationToken ct)
    {
        using HttpResponseMessage resposta = await comoPlatformAdmin.PostAsJsonAsync(
            "/api/v1/tenants",
            new
            {
                name = "Acme Corp",
                slug = $"kc-{Guid.NewGuid():N}"[..20],
                planCode = "free",
                initialAdminEmail = emailDoAdmin,
            },
            ct);
        resposta.StatusCode.Should().Be(HttpStatusCode.Accepted);
        JsonElement corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>(ct);

        return (corpo.GetProperty("tenantId").GetGuid(), resposta.Headers.Location!);
    }

    /// <summary>Espera o Outbox provisionar o tenant no Keycloak. Com prazo: sem o Outbox ligado, ele nunca chega.</summary>
    private static async Task EsperarAtivoAsync(HttpClient comoPlatformAdmin, Uri acompanhamento, CancellationToken ct)
    {
        DateTimeOffset fim = DateTimeOffset.UtcNow + PrazoDoProvisionamento;
        string? status = null;

        while (DateTimeOffset.UtcNow < fim)
        {
            using HttpResponseMessage resposta = await comoPlatformAdmin.GetAsync(acompanhamento, ct);
            resposta.StatusCode.Should().Be(HttpStatusCode.OK);
            status = (await resposta.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("status").GetString();

            if (status is "Active")
            {
                return;
            }

            status.Should().NotBe("ProvisioningFailed", "o provisionamento não pode desistir neste cenário");
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        status.Should().Be("Active", $"o tenant precisa ser provisionado em {PrazoDoProvisionamento.TotalSeconds:0} s");
    }

    private async Task<HttpResponseMessage> LerComAsync(string accessToken, Guid tenant, CancellationToken ct)
    {
        using HttpClient client = api.CreateClient();
        using HttpRequestMessage pedido = new(HttpMethod.Get, Rota(tenant));
        pedido.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await client.SendAsync(pedido, ct);
    }

    private static string[] Roles(JsonElement payload) =>
        payload.TryGetProperty("roles", out JsonElement roles)
            ? [.. roles.EnumerateArray().Select(role => role.GetString()!)]
            : [];

    [Fact]
    public async Task AdminConvidado_LeOProprioTenant_ENaoLeOutro_EOPlatformAdminNaoLeNenhum()
    {
        // Uma jornada só, porque o cenário é caro (dois convites, três device flows e um provisionamento). Cada
        // asserção diz qual das três afirmações falhou.
        CancellationToken ct = TestContext.Current.CancellationToken;
        UsuarioDeTeste operador = await api.Keycloak.NovoPlatformAdminAsync(ct);
        using HttpClient comoOperador = await api.CriarClienteComoAsync(operador, ct);
        string emailDoAdmin = KeycloakFixture.EmailUnico();

        (Guid tenant, Uri acompanhamento) = await RegistrarAsync(comoOperador, emailDoAdmin, ct);
        (Guid outroTenant, _) = await RegistrarAsync(comoOperador, KeycloakFixture.EmailUnico(), ct);
        await EsperarAtivoAsync(comoOperador, acompanhamento, ct);

        // O admin conclui o convite pelo link do e-mail, como faria no navegador, e entra pelo device flow.
        string senha = SenhasDeTeste.Gerar();

        using (HarnessDeLogin navegador = api.Keycloak.CriarHarness())
        {
            await navegador.ConcluirLinkDeAcoesAsync(await api.Keycloak.LinkDoConviteAsync(emailDoAdmin, ct), senha, ct);
        }

        using HarnessDeLogin harness = api.Keycloak.CriarHarness();
        TokensDeUsuario doAdmin = await harness.TokenPorDispositivoAsync(emailDoAdmin, senha, ct);
        JsonElement payload = PayloadDoJwt.Ler(doAdmin.AccessToken);

        // Premissas, lidas do token que o Keycloak emitiu: o papel e o tenant vêm do provisionamento.
        Roles(payload).Should().BeEquivalentTo(SoTenantAdmin);
        payload.GetProperty("tenant_id").GetString().Should().Be(tenant.ToString());
        payload.GetProperty("tenant_id").GetString().Should().NotBe(outroTenant.ToString());

        using HttpResponseMessage doProprio = await LerComAsync(doAdmin.AccessToken, tenant, ct);
        using HttpResponseMessage doOutro = await LerComAsync(doAdmin.AccessToken, outroTenant, ct);
        using HttpResponseMessage peloOperador = await comoOperador.GetAsync(new Uri(Rota(tenant), UriKind.Relative), ct);

        doProprio.StatusCode.Should().Be(HttpStatusCode.OK, "o admin convidado lê o próprio tenant");
        JsonElement corpo = await doProprio.Content.ReadFromJsonAsync<JsonElement>(ct);
        corpo.EnumerateObject().Select(chave => chave.Name).Should().BeEquivalentTo(ChavesDoTenant);
        corpo.GetProperty("plan").EnumerateObject().Select(chave => chave.Name).Should().BeEquivalentTo(ChavesDoPlano);
        corpo.GetProperty("tenantId").GetGuid().Should().Be(tenant);
        corpo.GetProperty("status").GetString().Should().Be("Active");
        corpo.GetProperty("occupiedSeats").GetInt32().Should().Be(1);

        doOutro.StatusCode.Should().Be(HttpStatusCode.Forbidden, "o tenant de outro id existe, e não é dele");
        peloOperador.StatusCode.Should().Be(HttpStatusCode.Forbidden, "o platform-admin registra, mas não lê o tenant");
    }

    [Fact]
    public async Task TenantIdHerdadoDeUmGrupo_NaoBasta_SemSerMembroNoBanco()
    {
        // O ataque que a pertença fecha (ADR-011), reproduzido como quem tem a chave da Gateway o faria: um usuário
        // SEM o atributo tenant_id, com o papel tenant-admin, posto num grupo que tem o tenant_id de um tenant que
        // existe. O mapper do Keycloak recua para o atributo do grupo, e o token sai com o tenant da vítima.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Guid vitima = await TenantAtivoNoBancoAsync(ct);
        UsuarioDeTeste atacante = await api.Keycloak.NovoUsuarioAsync(SoTenantAdmin, tenantId: null, ct);
        string grupo = await api.Keycloak.CriarGrupoComoMasterAsync(
            $"ataque-{Guid.NewGuid():N}",
            new Dictionary<string, string[]> { ["tenant_id"] = [vitima.ToString()] },
            ct);

        try
        {
            await api.Keycloak.PorNoGrupoComoMasterAsync(atacante.Id, grupo, ct);
            using HarnessDeLogin harness = api.Keycloak.CriarHarness();
            TokensDeUsuario tokens = await harness.TokenPorDispositivoAsync(atacante.Email, atacante.Senha, ct);
            JsonElement payload = PayloadDoJwt.Ler(tokens.AccessToken);

            // Premissas: o token passa nas três camadas do token. Sem elas afirmadas, o 403 poderia vir de qualquer uma.
            Roles(payload).Should().BeEquivalentTo(SoTenantAdmin);
            payload.GetProperty("tenant_id").GetString().Should().Be(
                vitima.ToString(), "o token precisa trazer o tenant_id herdado do grupo, ou o teste não prova a pertença");

            using HttpResponseMessage resposta = await LerComAsync(tokens.AccessToken, vitima, ct);

            resposta.StatusCode.Should().Be(HttpStatusCode.Forbidden, "o sub do atacante não é Member do tenant");
        }
        finally
        {
            // O realm não tem grupos, e outros testes afirmam isso.
            await api.Keycloak.ApagarGrupoComoMasterAsync(grupo, CancellationToken.None);
        }
    }

    /// <summary>
    /// Um tenant já ativo, gravado direto no banco, com outro membro como admin.
    /// </summary>
    /// <remarks>
    /// Registrado e ativado em memória e gravado num commit só: o tenant já nasce <c>Active</c>, e o consumidor do
    /// provisionamento — que aqui está ligado — ignora a mensagem de registro em vez de tentar provisioná-lo.
    /// </remarks>
    private async Task<Guid> TenantAtivoNoBancoAsync(CancellationToken ct)
    {
        var tenant = Tenant.Register(
            "Vítima",
            TenantSlug.Create($"vt-{Guid.NewGuid():N}"[..20]).Value,
            new Plan(PlanTier.Free, 5, 1),
            Email.Of(KeycloakFixture.EmailUnico()).Value,
            DateTimeOffset.UtcNow);
        Member admin = tenant.CompleteProvisioning(
            $"org-{Guid.NewGuid():N}", ExternalUserId.From(Guid.NewGuid().ToString()), DateTimeOffset.UtcNow);

        using IServiceScope escopo = api.Services.CreateScope();
        AppDbContext contexto = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
        contexto.Tenants.Add(tenant);
        contexto.Members.Add(admin);
        await contexto.SaveChangesAsync(ct);

        return tenant.Id.Value;
    }
}
