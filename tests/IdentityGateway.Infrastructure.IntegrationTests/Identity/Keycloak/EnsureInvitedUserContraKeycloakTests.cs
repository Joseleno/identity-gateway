using System.Net;
using System.Text;
using System.Text.Json;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// <c>EnsureInvitedUserAsync</c> contra o Keycloak real e o mailpit: os cinco passos, a idempotência, as
/// inconsistências, as falhas no meio e o link.
/// </summary>
/// <remarks>
/// Toda conferência de estado é leitura crua pelo master, nunca pelo DTO do adaptador sob teste: o Keycloak ignora em
/// silêncio ação obrigatória desconhecida e atributo não declarado, e um DTO que os ecoasse aprovaria o defeito.
/// </remarks>
public sealed class EnsureInvitedUserContraKeycloakTests(KeycloakFixture keycloak)
{
    private static readonly TimeSpan Prazo = TimeSpan.FromHours(2);

    private static readonly string[] AcoesDoConvite = ["UPDATE_PASSWORD", "VERIFY_EMAIL"];

    private static readonly string[] SoAtualizarSenha = ["UPDATE_PASSWORD"];

    private static InviteData Convite(string email, RoleName? papel = null) =>
        new(Email.Of(email).Value, papel ?? RoleName.TenantAdmin, Prazo);

    private static HttpResponseMessage ListaVazia() =>
        new(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") };

    /// <summary>
    /// O arranjo comum: a composição real (com a interceptação, se houver), um tenant novo, a Organization dele e um
    /// e-mail único.
    /// </summary>
    private async Task<Cenario> PrepararAsync(
        CancellationToken ct, Interceptacao? interceptacao = null, string? endereco = null)
    {
        ServiceProvider provider = interceptacao is null
            ? keycloak.CriarProvider()
            : keycloak.CriarProvider(services => services.Interceptar(interceptacao));

        try
        {
            IIdentityProvider identidade = provider.GetRequiredService<IIdentityProvider>();
            var tenant = TenantId.New();
            string organizacao = await identidade.EnsureOrganizationAsync(
                tenant, KeycloakFixture.SlugUnico(), "Acme", ct);

            return new Cenario(provider, identidade, tenant, endereco ?? KeycloakFixture.EmailUnico(), organizacao);
        }
        catch
        {
            await provider.DisposeAsync();
            throw;
        }
    }

    [Fact]
    public async Task ConviteNovo_UsuarioHabilitadoComTenantIdAcoesVinculoPapelEUmEmail()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Cenario cenario = await PrepararAsync(ct);

        ExternalUserId sub = await cenario.ConvidarAsync(ct);

        JsonElement cru = await keycloak.LerUsuarioCruAsync(sub.Value, ct);
        cru.GetProperty("username").GetString().Should().Be(cenario.Endereco);
        cru.GetProperty("email").GetString().Should().Be(cenario.Endereco);
        cru.GetProperty("enabled").GetBoolean().Should().BeTrue("D4: desabilitado, o Keycloak recusa o envio e o clique");
        cru.GetProperty("requiredActions").EnumerateArray().Select(acao => acao.GetString())
            .Should().BeEquivalentTo(AcoesDoConvite);
        cru.GetProperty("attributes").GetProperty("tenant_id").EnumerateArray().Select(valor => valor.GetString())
            .Should().Equal(cenario.Tenant.Value.ToString());

        (await keycloak.MembrosDaOrganizacaoAsync(cenario.Organizacao, ct)).Should().Contain(sub.Value);
        (await keycloak.PapeisDeRealmDoUsuarioAsync(sub.Value, ct)).Should().Contain("tenant-admin");
        (await keycloak.EsperarMensagensAsync(cenario.Endereco, 1, ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task ChamadoDuasVezes_MesmoSubUmUsuarioEDoisEmails()
    {
        // Sem aceite, o usuário continua com UPDATE_PASSWORD, e a segunda chamada reenvia — é o duplo envio que a §4.3
        // declara. O que não pode duplicar é o usuário nem o vínculo.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Cenario cenario = await PrepararAsync(ct);

        ExternalUserId primeiro = await cenario.ConvidarAsync(ct);
        ExternalUserId segundo = await cenario.ConvidarAsync(ct);

        segundo.Should().Be(primeiro);
        (await keycloak.UsuariosPorEmailAsync(cenario.Endereco, ct)).Should().ContainSingle();
        (await keycloak.MembrosDaOrganizacaoAsync(cenario.Organizacao, ct)).Where(id => id == primeiro.Value)
            .Should().ContainSingle();
        (await keycloak.EsperarMensagensAsync(cenario.Endereco, 2, ct)).Should().HaveCount(2);
    }

    [Fact]
    public async Task UsuarioPreCriadoComNossoTenantSemVinculoNemPapel_CompletaTudo()
    {
        // Retomada parcial: uma tentativa anterior criou o usuário e caiu antes do vínculo. O reaproveitamento precisa
        // seguir para os passos 3, 4 e 5 — pular algum deixaria o admin sem Organization ou sem papel, sem erro.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Cenario cenario = await PrepararAsync(ct);
        string preCriado = await keycloak.CriarUsuarioComoMasterAsync(new
        {
            username = cenario.Endereco,
            email = cenario.Endereco,
            enabled = true,
            requiredActions = AcoesDoConvite,
            attributes = new Dictionary<string, string[]> { ["tenant_id"] = [cenario.Tenant.Value.ToString()] },
        }, ct);

        ExternalUserId sub = await cenario.ConvidarAsync(ct);

        sub.Value.Should().Be(preCriado);
        (await keycloak.MembrosDaOrganizacaoAsync(cenario.Organizacao, ct)).Should().Contain(preCriado);
        (await keycloak.PapeisDeRealmDoUsuarioAsync(preCriado, ct)).Should().Contain("tenant-admin");
        (await keycloak.EsperarMensagensAsync(cenario.Endereco, 1, ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task UsuarioQueJaAceitou_NenhumEmailEnviado()
    {
        // D12: sem UPDATE_PASSWORD pendente, um link trocaria a senha de um admin já ativo.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Cenario cenario = await PrepararAsync(ct);
        string aceito = await keycloak.CriarUsuarioComoMasterAsync(new
        {
            username = cenario.Endereco,
            email = cenario.Endereco,
            enabled = true,
            attributes = new Dictionary<string, string[]> { ["tenant_id"] = [cenario.Tenant.Value.ToString()] },
        }, ct);

        ExternalUserId sub = await cenario.ConvidarAsync(ct);

        sub.Value.Should().Be(aceito);
        (await keycloak.EsperarMensagensAsync(cenario.Endereco, 1, ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task UsuarioPreExistenteSemTenantId_LancaInconsistencia()
    {
        // D5: reaproveitar um usuário sem o nosso tenant_id poderia entregar o tenant ao platform-admin ou a uma conta
        // abandonada.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Cenario cenario = await PrepararAsync(ct);
        await keycloak.CriarUsuarioComoMasterAsync(
            new { username = cenario.Endereco, email = cenario.Endereco, enabled = true }, ct);

        Func<Task> convidar = () => cenario.ConvidarAsync(ct);

        await convidar.Should().ThrowAsync<IdentityProviderInconsistencyException>();
    }

    [Fact]
    public async Task UsuarioComTenantIdDeOutroTenant_LancaInconsistencia()
    {
        // "Igual", e não "existe": o atributo de outro tenant não é tentativa anterior deste.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Cenario cenario = await PrepararAsync(ct);
        await keycloak.CriarUsuarioComoMasterAsync(new
        {
            username = cenario.Endereco,
            email = cenario.Endereco,
            enabled = true,
            attributes = new Dictionary<string, string[]> { ["tenant_id"] = [TenantId.New().Value.ToString()] },
        }, ct);

        Func<Task> convidar = () => cenario.ConvidarAsync(ct);

        await convidar.Should().ThrowAsync<IdentityProviderInconsistencyException>();
    }

    [Fact]
    public async Task UsernameIgualAoEmailEmOutroUsuario_LancaInconsistenciaSemLaco()
    {
        // A busca por e-mail não acha ninguém, o POST dá 409 por causa do username, e a reconsulta acha um usuário sem o
        // nosso tenant_id. Um laço "409 → busca → POST" nunca terminaria aqui — e o projeto não tem timeout por teste:
        // um laço de volta penduraria o job em vez de falhar. Por isso o segundo POST /users responde 500 sem ir ao
        // Keycloak. Sem laço ele nunca acontece; com laço a chamada sobe como HttpRequestException, e o ThrowAsync
        // abaixo fica vermelho, com nome e em segundos.
        CancellationToken ct = TestContext.Current.CancellationToken;
        int postsDeUsuario = 0;
        Interceptacao interceptacao = new()
        {
            Responder = (pedido, _) =>
                pedido.Method == HttpMethod.Post
                && pedido.RequestUri!.AbsolutePath.EndsWith("/users", StringComparison.Ordinal)
                && Interlocked.Increment(ref postsDeUsuario) >= 2
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : null,
        };
        await using Cenario cenario = await PrepararAsync(ct, interceptacao);
        await keycloak.CriarUsuarioComoMasterAsync(
            new { username = cenario.Endereco, email = $"outro+{Guid.NewGuid():N}@acme.test", enabled = true }, ct);

        Func<Task> convidar = () => cenario.ConvidarAsync(ct);

        await convidar.Should().ThrowAsync<IdentityProviderInconsistencyException>();
        interceptacao.Contar(HttpMethod.Post, "/users").Should().Be(1);
    }

    [Fact]
    public async Task PreXExistente_ConviteParaXCriaOutroUsuario()
    {
        // exact=true: sem ele, a busca vira LIKE %x% e acharia pre.x — sem o nosso tenant_id, o convite de x viraria
        // inconsistência por causa de um vizinho.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Cenario cenario = await PrepararAsync(ct);
        string vizinho = $"pre.{cenario.Endereco}";
        string idDoVizinho = await keycloak.CriarUsuarioComoMasterAsync(
            new { username = vizinho, email = vizinho, enabled = true }, ct);

        ExternalUserId sub = await cenario.ConvidarAsync(ct);

        sub.Value.Should().NotBe(idDoVizinho);
        (await keycloak.LerUsuarioCruAsync(sub.Value, ct)).GetProperty("email").GetString()
            .Should().Be(cenario.Endereco);
    }

    [Fact]
    public async Task NossoUsuarioDesabilitado_LancaInconsistencia()
    {
        // Alguém desabilitou o nosso usuário à mão: o Keycloak recusa o envio com 400, e repetir não corrige.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Cenario cenario = await PrepararAsync(ct);
        ExternalUserId sub = await cenario.ConvidarAsync(ct);
        await keycloak.DesabilitarUsuarioComoMasterAsync(sub.Value, ct);

        Func<Task> convidar = () => cenario.ConvidarAsync(ct);

        (await convidar.Should().ThrowAsync<IdentityProviderInconsistencyException>())
            .Which.Message.Should().NotContain(cenario.Endereco);
    }

    [Fact]
    public async Task PapelAusenteNoRealm_LancaInconsistencia()
    {
        // O papel não aparece nem nos atribuídos nem nos disponíveis: o realm não é o que a Gateway espera.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Cenario cenario = await PrepararAsync(ct);
        var inexistente = RoleName.From($"papel-{Guid.NewGuid():N}");

        Func<Task> convidar = () => cenario.ConvidarAsync(ct, inexistente);

        await convidar.Should().ThrowAsync<IdentityProviderInconsistencyException>();
    }

    [Fact]
    public async Task CorridaNoPost_O409ReencontraONossoUsuario()
    {
        // Duas entregas concorrentes: a primeira busca desta chamada volta vazia (interceptada), o POST real dá 409 —
        // porque a outra entrega criou o usuário —, e a reconsulta o reencontra pelo tenant_id.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Cenario vencedora = await PrepararAsync(ct);
        ExternalUserId vencedor = await vencedora.ConvidarAsync(ct);

        Interceptacao interceptacao = new()
        {
            Responder = (pedido, ordinal) =>
                pedido.Method == HttpMethod.Get && ordinal == 1
                                                && pedido.RequestUri!.AbsolutePath.EndsWith("/users", StringComparison.Ordinal)
                    ? ListaVazia()
                    : null,
        };
        await using ServiceProvider segundo = keycloak.CriarProvider(services => services.Interceptar(interceptacao));

        ExternalUserId sub = await segundo.GetRequiredService<IIdentityProvider>()
            .EnsureInvitedUserAsync(vencedora.Organizacao, vencedora.Tenant, Convite(vencedora.Endereco), ct);

        sub.Should().Be(vencedor);
        interceptacao.Detalhes.Should().Contain(chamada =>
            chamada.Metodo == HttpMethod.Post && chamada.Caminho.EndsWith("/users", StringComparison.Ordinal)
            && chamada.Status == HttpStatusCode.Conflict);
        (await keycloak.UsuariosPorEmailAsync(vencedora.Endereco, ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task SmtpFora_RetryReaproveitaOUsuarioEEnviaNaVolta()
    {
        // O 500 do SMTP é injetado: derrubar o mailpit compartilhado quebraria os testes paralelos.
        // Que "SMTP fora do ar = 500" vem da leitura de código (UserResource.java L1073-1075).
        CancellationToken ct = TestContext.Current.CancellationToken;
        Interceptacao interceptacao = new()
        {
            ResponderSemEnviar = (pedido, ordinal) =>
                pedido.Method == HttpMethod.Put && ordinal == 1 ? HttpStatusCode.InternalServerError : null,
        };
        await using Cenario cenario = await PrepararAsync(ct, interceptacao);

        Func<Task> primeira = () => cenario.ConvidarAsync(ct);
        await primeira.Should().ThrowAsync<HttpRequestException>();
        (await keycloak.MensagensParaAsync(cenario.Endereco, ct)).Should().BeEmpty();

        ExternalUserId sub = await cenario.ConvidarAsync(ct);

        interceptacao.Contar(HttpMethod.Post, "/users").Should().Be(1, "o usuário da primeira tentativa é reaproveitado");
        (await keycloak.UsuariosPorEmailAsync(cenario.Endereco, ct)).Should().ContainSingle()
            .Which.GetProperty("id").GetString().Should().Be(sub.Value);
        (await keycloak.EsperarMensagensAsync(cenario.Endereco, 1, ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task QuedaNoVinculo_RetryCompletaSemDuplicar()
    {
        // Usuário criado, Keycloak cai no vínculo. POST não é repetido pela resiliência, então a
        // exceção sobe; a próxima entrega retoma do usuário existente.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Interceptacao interceptacao = new() { Responder = CairUmaVez(HttpMethod.Post, "/members") };
        await using Cenario cenario = await PrepararAsync(ct, interceptacao);

        Func<Task> primeira = () => cenario.ConvidarAsync(ct);
        await primeira.Should().ThrowAsync<HttpRequestException>();

        ExternalUserId sub = await cenario.ConvidarAsync(ct);

        (await keycloak.UsuariosPorEmailAsync(cenario.Endereco, ct)).Should().ContainSingle();
        (await keycloak.MembrosDaOrganizacaoAsync(cenario.Organizacao, ct)).Should().Contain(sub.Value);
        (await keycloak.PapeisDeRealmDoUsuarioAsync(sub.Value, ct)).Should().Contain("tenant-admin");
        (await keycloak.EsperarMensagensAsync(cenario.Endereco, 1, ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task VinculoCom400EUsuarioJaMembro_LeituraDaPertencaConfirmaESegue()
    {
        // A corrida de dois POST de vínculo dá 400 ao perdedor. O 400 é injetado; a leitura da pertença vai ao
        // Keycloak, com o service account — é ela que prova que manage-organizations e manage-users bastam (200).
        CancellationToken ct = TestContext.Current.CancellationToken;
        bool[] responder400 = [false];
        Interceptacao interceptacao = new()
        {
            ResponderSemEnviar = (pedido, _) => responder400[0] && EhPostDeVinculo(pedido) ? HttpStatusCode.BadRequest : null,
        };
        await using Cenario cenario = await PrepararAsync(ct, interceptacao);
        ExternalUserId primeiro = await cenario.ConvidarAsync(ct);

        responder400[0] = true;
        ExternalUserId segundo = await cenario.ConvidarAsync(ct);

        segundo.Should().Be(primeiro);
        interceptacao.Detalhes.Should().ContainSingle(chamada =>
            chamada.Metodo == HttpMethod.Get
            && chamada.Caminho.EndsWith($"/organizations/{cenario.Organizacao}/members/{primeiro.Value}", StringComparison.Ordinal))
            .Which.Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task VinculoCom400SemPertenca_PropagaOErroOriginal()
    {
        // Sem a pertença, o Keycloak responde 404 ao service account (e não 403: users().canQuery() vale), e o 400
        // original sobe como transitório.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Interceptacao interceptacao = new()
        {
            ResponderSemEnviar = (pedido, _) => EhPostDeVinculo(pedido) ? HttpStatusCode.BadRequest : null,
        };
        await using Cenario cenario = await PrepararAsync(ct, interceptacao);
        string preCriado = await keycloak.CriarUsuarioComoMasterAsync(new
        {
            username = cenario.Endereco,
            email = cenario.Endereco,
            enabled = true,
            requiredActions = AcoesDoConvite,
            attributes = new Dictionary<string, string[]> { ["tenant_id"] = [cenario.Tenant.Value.ToString()] },
        }, ct);

        Func<Task> convidar = () => cenario.ConvidarAsync(ct);

        (await convidar.Should().ThrowExactlyAsync<HttpRequestException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        interceptacao.Detalhes.Should().ContainSingle(chamada =>
            chamada.Metodo == HttpMethod.Get
            && chamada.Caminho.EndsWith($"/organizations/{cenario.Organizacao}/members/{preCriado}", StringComparison.Ordinal))
            .Which.Status.Should().Be(HttpStatusCode.NotFound);
        (await keycloak.MembrosDaOrganizacaoAsync(cenario.Organizacao, ct)).Should().NotContain(preCriado);
    }

    [Fact]
    public async Task QuedaNoPapel_RetryCompletaSemDuplicar()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Interceptacao interceptacao = new() { Responder = CairUmaVez(HttpMethod.Post, "/role-mappings/realm") };
        await using Cenario cenario = await PrepararAsync(ct, interceptacao);

        Func<Task> primeira = () => cenario.ConvidarAsync(ct);
        await primeira.Should().ThrowAsync<HttpRequestException>();

        ExternalUserId sub = await cenario.ConvidarAsync(ct);

        (await keycloak.UsuariosPorEmailAsync(cenario.Endereco, ct)).Should().ContainSingle();
        (await keycloak.PapeisDeRealmDoUsuarioAsync(sub.Value, ct)).Should().Contain("tenant-admin");
        (await keycloak.EsperarMensagensAsync(cenario.Endereco, 1, ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task LinkDoEmail_PrazoDaPoliticaSubDoUsuarioEAbreAPaginaDeAcoes()
    {
        // Prazo de 2 h, diferente do padrão do realm (12 h) e do da política (7 dias): só passa se o lifespan chegou.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Cenario cenario = await PrepararAsync(ct);

        ExternalUserId sub = await cenario.ConvidarAsync(ct);
        Uri link = await keycloak.LinkDoConviteAsync(cenario.Endereco, ct);

        string chave = Uri.UnescapeDataString(link.Query.TrimStart('?').Split('&')
            .Single(par => par.StartsWith("key=", StringComparison.Ordinal))[4..]);
        using var carga = JsonDocument.Parse(Base64UrlEncoder.Decode(new JsonWebToken(chave).EncodedPayload));
        long exp = carga.RootElement.GetProperty("exp").GetInt64();
        long iat = carga.RootElement.GetProperty("iat").GetInt64();
        (exp - iat).Should().BeCloseTo((long)Prazo.TotalSeconds, 2);
        carga.RootElement.GetProperty("sub").GetString().Should().Be(sub.Value);

        // Com o KC_HOSTNAME fixo, o clique é validado contra o emissor gravado no token, não contra o host da
        // requisição: trocar só a autoridade pela porta mapeada abre a mesma página que o navegador veria.
        Uri local = new($"{keycloak.BaseUrl}{link.PathAndQuery}");
        using HttpClient navegador = new();
        using HttpResponseMessage pagina = await navegador.GetAsync(local, ct);
        string html = await pagina.Content.ReadAsStringAsync(ct);

        pagina.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("id=\"kc-info-message\"").And.NotContain("id=\"kc-error-message\"");
    }

    [Fact]
    public async Task UsuarioCriadoPeloMasterComCaixaMista_EReaproveitado()
    {
        // O Keycloak grava em minúsculas, e o Email.Of também normaliza — a busca exata dos dois
        // lados concorda, e o usuário de uma tentativa anterior é reencontrado.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string marca = Guid.NewGuid().ToString("N").ToUpperInvariant();
        await using Cenario cenario = await PrepararAsync(ct, endereco: $"  ADMIN+{marca}@acme.TEST ");
        string preCriado = await keycloak.CriarUsuarioComoMasterAsync(new
        {
            username = $"Admin+{marca}@Acme.Test",
            email = $"Admin+{marca}@Acme.Test",
            enabled = true,
            requiredActions = SoAtualizarSenha,
            attributes = new Dictionary<string, string[]> { ["tenant_id"] = [cenario.Tenant.Value.ToString()] },
        }, ct);

        ExternalUserId sub = await cenario.ConvidarAsync(ct);

        sub.Value.Should().Be(preCriado);
        (await keycloak.EsperarMensagensAsync($"admin+{marca.ToLowerInvariant()}@acme.test", 1, ct))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task EmailDe254Caracteres_ViraUsernameNoKeycloak()
    {
        // Parte local de 64 (o limite do Keycloak) e username de 254 (o perfil aceita 255).
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Cenario cenario = await PrepararAsync(ct, endereco: KeycloakFixture.EmailUnicoDe254Caracteres());
        cenario.Endereco.Length.Should().Be(254);

        ExternalUserId sub = await cenario.ConvidarAsync(ct);

        (await keycloak.LerUsuarioCruAsync(sub.Value, ct)).GetProperty("username").GetString()
            .Should().Be(cenario.Endereco);
    }

    private static bool EhPostDeVinculo(HttpRequestMessage pedido) =>
        pedido.Method == HttpMethod.Post && pedido.RequestUri!.AbsolutePath.EndsWith("/members", StringComparison.Ordinal);

    /// <summary>Responde 503 à primeira requisição com o método e o sufixo de caminho; deixa passar o resto.</summary>
    private static Func<HttpRequestMessage, int, HttpResponseMessage?> CairUmaVez(HttpMethod metodo, string sufixo)
    {
        int jaCaiu = 0;

        return (pedido, _) =>
            pedido.Method == metodo
            && pedido.RequestUri!.AbsolutePath.EndsWith(sufixo, StringComparison.Ordinal)
            && Interlocked.Exchange(ref jaCaiu, 1) == 0
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : null;
    }

    /// <summary>O que <see cref="PrepararAsync"/> monta; descartar o cenário descarta o provider.</summary>
    /// <param name="Endereco">O e-mail do convite, como o teste o escreveu (o <c>Email.Of</c> normaliza).</param>
    private sealed record Cenario(
        ServiceProvider Provider,
        IIdentityProvider Identidade,
        TenantId Tenant,
        string Endereco,
        string Organizacao) : IAsyncDisposable
    {
        public Task<ExternalUserId> ConvidarAsync(CancellationToken ct, RoleName? papel = null) =>
            Identidade.EnsureInvitedUserAsync(Organizacao, Tenant, Convite(Endereco, papel), ct);

        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }
}
