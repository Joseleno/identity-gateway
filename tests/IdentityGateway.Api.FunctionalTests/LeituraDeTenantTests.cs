using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IdentityGateway.Api.Authorization;
using IdentityGateway.Api.Middlewares;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// <c>GET /api/v1/tenants/{tenantId}</c>: o administrador lê o próprio tenant, e mais ninguém lê nada.
/// </summary>
/// <remarks>
/// <para>
/// <b>A regra de isolamento número um, por HTTP.</b> Cada caso de <c>403</c> tem tudo certo menos uma coisa — o papel,
/// a conta de plataforma, o tenant do token, a pertença no banco —, e todos respondem o mesmo Problem Details: quem
/// chama não aprende por que foi negado, nem se o tenant existe.
/// </para>
/// <para>
/// <b>Não há <c>404</c> nesta rota.</b> Tenant que não existe é <c>403</c>: a policy nega antes de qualquer consulta ao
/// tenant, porque não há membro de um tenant que não existe.
/// </para>
/// <para>
/// Os tokens levam <c>sub</c> único, e o limitador de requisições particiona por <c>sub</c>: a classe fica longe do
/// limite.
/// </para>
/// </remarks>
public sealed class LeituraDeTenantTests(IdentityGatewayApiFactory factory) : IClassFixture<IdentityGatewayApiFactory>
{
    private const string OutroTenant = "0199a000-0000-7000-8000-0000000000ff";

    private static readonly string[] SoTenantAdmin = ["tenant-admin"];

    private static readonly string[] SoPlatformAdmin = ["platform-admin"];

    private static readonly string[] PlatformAdminETenantAdmin = ["platform-admin", "tenant-admin"];

    private static readonly string[] SoReader = ["reader"];

    private static readonly string[] ChavesDoTenant =
        ["tenantId", "name", "slug", "status", "plan", "occupiedSeats", "registeredAt"];

    private static readonly string[] ChavesDoPlano = ["tier", "maxUsers", "maxClients"];

    private static string Rota(Guid tenant) => $"/api/v1/tenants/{tenant}";

    /// <summary>Grava um tenant ativo com um admin membro, pelo caminho de domínio, e devolve os dois ids.</summary>
    private async Task<(Guid Tenant, Guid Admin, string Slug)> TenantComAdminAsync(CancellationToken ct)
    {
        var admin = Guid.NewGuid();
        string slug = $"lt-{Guid.NewGuid():N}"[..20];
        var tenant = Tenant.Register(
            "Acme Corp",
            TenantSlug.Create(slug).Value,
            new Plan(PlanTier.Standard, 25, 3),
            Email.Of($"admin+{Guid.NewGuid():N}@acme.test").Value,
            DateTimeOffset.UtcNow);

        await factory.ComEscopoAsync(async contexto =>
        {
            contexto.Tenants.Add(tenant);
            await contexto.SaveChangesAsync(ct);

            // O sub como o Keycloak o emite: o GUID em minúsculas, formato D.
            Member membro = tenant.CompleteProvisioning(
                $"org-{Guid.NewGuid():N}", ExternalUserId.From(admin.ToString()), DateTimeOffset.UtcNow);
            contexto.Members.Add(membro);
            await contexto.SaveChangesAsync(ct);
        });

        return (tenant.Id.Value, admin, slug);
    }

    private async Task<HttpResponseMessage> LerAsync(string rota, string? token, CancellationToken ct)
    {
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage pedido = new(HttpMethod.Get, rota);

        if (token is not null)
        {
            pedido.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(pedido, ct);
    }

    /// <summary>O <c>403</c> único da Gateway: os mesmos quatro campos fixos, qualquer que seja o motivo.</summary>
    private static async Task DeveSerOProibidoPadraoAsync(HttpResponseMessage resposta, string caso, CancellationToken ct)
    {
        // Numa variável: sem corpo o ContentType é nulo, e um `?.` encadeado até o Should() pularia a asserção.
        string? tipoDoCorpo = resposta.Content.Headers.ContentType?.MediaType;

        resposta.StatusCode.Should().Be(HttpStatusCode.Forbidden, caso);
        tipoDoCorpo.Should().Be("application/problem+json", caso);

        JsonElement corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>(ct);
        corpo.GetProperty("status").GetInt32().Should().Be(403, caso);
        corpo.GetProperty("type").GetString().Should().Be(RespostasDeAutorizacao.TipoDoProibido, caso);
        corpo.GetProperty("title").GetString().Should().Be(RespostasDeAutorizacao.TituloDoProibido, caso);
        corpo.GetProperty("detail").GetString().Should().Be(RespostasDeAutorizacao.DetalheDoProibido, caso);
    }

    [Fact]
    public async Task AdminDoTenant_LeOProprioTenantComExatamenteAsChavesDoContrato()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Guid tenant, Guid admin, string slug) = await TenantComAdminAsync(ct);
        string token = factory.Emissor.Emitir(admin, SoTenantAdmin, tenant.ToString());

        using HttpResponseMessage resposta = await LerAsync(Rota(tenant), token, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>(ct);

        // O conjunto EXATO de chaves: uma a mais reprova, mesmo nula. É o que impede o e-mail do admin inicial (ou o
        // id da Organization) de aparecer aqui por uma propriedade nova no DTO.
        corpo.EnumerateObject().Select(chave => chave.Name).Should().BeEquivalentTo(ChavesDoTenant);
        corpo.GetProperty("plan").EnumerateObject().Select(chave => chave.Name).Should().BeEquivalentTo(ChavesDoPlano);

        corpo.GetProperty("tenantId").GetGuid().Should().Be(tenant);
        corpo.GetProperty("name").GetString().Should().Be("Acme Corp");
        corpo.GetProperty("slug").GetString().Should().Be(slug);
        corpo.GetProperty("status").GetString().Should().Be("Active");
        corpo.GetProperty("plan").GetProperty("tier").GetString().Should().Be("Standard");
        corpo.GetProperty("plan").GetProperty("maxUsers").GetInt32().Should().Be(25);
        corpo.GetProperty("plan").GetProperty("maxClients").GetInt32().Should().Be(3);
        corpo.GetProperty("occupiedSeats").GetInt32().Should().Be(1, "o admin convidado ocupa uma vaga");
        corpo.GetProperty("registeredAt").GetDateTimeOffset()
            .Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
    }

    [Theory]
    [InlineData("D")]
    [InlineData("N")]
    [InlineData("B")]
    [InlineData("P")]
    public async Task ProprioTenantComOGuidDaRotaEscritoDeOutroJeito_Responde200(string formato)
    {
        // Controles: a comparação entre a rota e o token é por Guid, e não por texto. Em maiúsculas, sem hifens, entre
        // chaves ou entre parênteses, é o mesmo tenant.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Guid tenant, Guid admin, _) = await TenantComAdminAsync(ct);
        string token = factory.Emissor.Emitir(admin, SoTenantAdmin, tenant.ToString());
        string rota = $"/api/v1/tenants/{tenant.ToString(formato).ToUpperInvariant()}";

        using HttpResponseMessage resposta = await LerAsync(rota, token, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.OK, rota);
    }

    [Fact]
    public async Task ProprioTenantComSinalNoGuidDaRota_LeOTenantDoGuidENaoDoTexto()
    {
        // O parse de GUID tolera o sinal de mais em cada componente: "+199a000-…" é o GUID 0199a000-…. A restrição
        // :guid, o requirement e o parâmetro do endpoint usam esse mesmo parse, e por isso o segmento passa. O que
        // torna isso seguro é tudo depois operar sobre o Guid vinculado, nunca sobre o texto do segmento: a leitura é a
        // do tenant desse Guid, com o id na forma canônica.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Guid tenant, Guid admin, _) = await TenantComAdminAsync(ct);
        string token = factory.Emissor.Emitir(admin, SoTenantAdmin, tenant.ToString());
        string canonico = tenant.ToString();
        string rota = $"/api/v1/tenants/+{canonico[1..]}";

        using HttpResponseMessage resposta = await LerAsync(rota, token, ct);

        canonico.Should().StartWith("0", "controle: o sinal só substitui um zero à esquerda sem mudar o GUID");
        resposta.StatusCode.Should().Be(HttpStatusCode.OK, rota);
        JsonElement corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>(ct);
        corpo.GetProperty("tenantId").GetString().Should().Be(canonico);
    }

    private static TheoryDataRow<Func<IdentityGatewayApiFactory, Guid, Guid, string>> Caso(
        string rotulo, Func<IdentityGatewayApiFactory, Guid, Guid, string> token) => new(token) { Label = rotulo };

    /// <summary>O token do admin membro do tenant, com o claim <c>tenant_id</c> trocado pelo que o caso disser.</summary>
    private static TheoryDataRow<Func<IdentityGatewayApiFactory, Guid, Guid, string>> ComTenantId(
        string rotulo, Func<Guid, object?> tenantId) =>
        Caso(rotulo, (alvo, tenant, admin) => alvo.Emissor.Emitir(admin, SoTenantAdmin, ajustar: payload =>
        {
            if (tenantId(tenant) is { } valor)
            {
                payload["tenant_id"] = valor;
            }
        }));

    /// <summary>
    /// Tokens do <b>membro</b> do tenant (o <c>sub</c> está no banco) em que só uma coisa está errada. Recebem a
    /// factory, o tenant e o admin, e devolvem o token.
    /// </summary>
    public static TheoryData<Func<IdentityGatewayApiFactory, Guid, Guid, string>> TokensNegados => new()
    {
        Caso("platform-admin sem tenant_id",
            (alvo, _, admin) => alvo.Emissor.Emitir(admin, SoPlatformAdmin)),
        Caso("platform-admin com o tenant_id do tenant",
            (alvo, tenant, admin) => alvo.Emissor.Emitir(admin, SoPlatformAdmin, tenant.ToString())),
        Caso("platform-admin que também é tenant-admin do próprio tenant",
            (alvo, tenant, admin) => alvo.Emissor.Emitir(admin, PlatformAdminETenantAdmin, tenant.ToString())),
        Caso("papel de outro nível (reader)",
            (alvo, tenant, admin) => alvo.Emissor.Emitir(admin, SoReader, tenant.ToString())),
        Caso("sem o claim roles",
            (alvo, tenant, admin) => alvo.Emissor.Emitir(admin, tenantId: tenant.ToString())),
        ComTenantId("tenant_id ausente", _ => null),
        ComTenantId("tenant_id vazio", _ => string.Empty),
        ComTenantId("tenant_id que não é GUID", _ => "acme"),
        ComTenantId("tenant_id com espaço antes", tenant => $" {tenant}"),
        ComTenantId("tenant_id com espaço depois", tenant => $"{tenant} "),
        ComTenantId("tenant_id entre chaves", tenant => tenant.ToString("B")),
        ComTenantId("tenant_id no formato N", tenant => tenant.ToString("N")),
        ComTenantId("tenant_id de outro tenant", _ => OutroTenant),
        ComTenantId("tenant_id em array: o próprio e outro", tenant => new[] { tenant.ToString(), OutroTenant }),
        ComTenantId("tenant_id em array: outro e o próprio", tenant => new[] { OutroTenant, tenant.ToString() }),
        ComTenantId("tenant_id em array: o próprio, duas vezes", tenant => new[] { tenant.ToString(), tenant.ToString() }),
    };

    [Theory]
    [MemberData(nameof(TokensNegados))]
    public async Task MembroDoTenantComUmDefeitoNoToken_Responde403(Func<IdentityGatewayApiFactory, Guid, Guid, string> token)
    {
        ArgumentNullException.ThrowIfNull(token);
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Guid tenant, Guid admin, _) = await TenantComAdminAsync(ct);

        using HttpResponseMessage resposta = await LerAsync(Rota(tenant), token(factory, tenant, admin), ct);

        await DeveSerOProibidoPadraoAsync(resposta, "token com defeito", ct);
    }

    [Fact]
    public async Task AdminDeOutroTenantQueExiste_Responde403()
    {
        // Os dois tenants existem, e cada admin é membro do seu. O token de A, na rota de B.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Guid tenantA, Guid adminA, _) = await TenantComAdminAsync(ct);
        (Guid tenantB, _, _) = await TenantComAdminAsync(ct);
        string token = factory.Emissor.Emitir(adminA, SoTenantAdmin, tenantA.ToString());

        using HttpResponseMessage resposta = await LerAsync(Rota(tenantB), token, ct);

        await DeveSerOProibidoPadraoAsync(resposta, "tenant alheio", ct);
    }

    [Fact]
    public async Task TenantQueNaoExiste_Responde403ENao404()
    {
        // Duas formas de "não existe": o tenant da rota não existe e o token é de outro tenant; e o tenant da rota não
        // existe e o token diz que é o dele. Nas duas, 403 — a resposta não distingue "não existe" de "não é seu".
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Guid tenantA, Guid adminA, _) = await TenantComAdminAsync(ct);
        var inexistente = Guid.NewGuid();
        string tokenDeA = factory.Emissor.Emitir(adminA, SoTenantAdmin, tenantA.ToString());
        string tokenDoInexistente = factory.Emissor.Emitir(adminA, SoTenantAdmin, inexistente.ToString());

        using HttpResponseMessage deOutro = await LerAsync(Rota(inexistente), tokenDeA, ct);
        using HttpResponseMessage doProprio = await LerAsync(Rota(inexistente), tokenDoInexistente, ct);

        await DeveSerOProibidoPadraoAsync(deOutro, "tenant inexistente, token de outro tenant", ct);
        await DeveSerOProibidoPadraoAsync(doProprio, "tenant inexistente, token com o tenant_id dele", ct);
    }

    [Fact]
    public async Task TokenCertoDeQuemNaoEMembro_Responde403()
    {
        // O ataque que a pertença fecha: papel certo e tenant_id certo no token (forjável por um grupo no Keycloak),
        // de um sub que o banco da Gateway não conhece como membro do tenant.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Guid tenant, _, _) = await TenantComAdminAsync(ct);
        string token = factory.Emissor.Emitir(Guid.NewGuid(), SoTenantAdmin, tenant.ToString());

        using HttpResponseMessage resposta = await LerAsync(Rota(tenant), token, ct);

        await DeveSerOProibidoPadraoAsync(resposta, "sub sem Member", ct);
    }

    [Fact]
    public async Task MembroDesativado_PerdeOAcessoNoPedidoSeguinte()
    {
        // A pertença é lida a cada pedido: o token ainda vale 5 minutos, e o acesso acaba quando o status muda.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Guid tenant, Guid admin, _) = await TenantComAdminAsync(ct);
        string token = factory.Emissor.Emitir(admin, SoTenantAdmin, tenant.ToString());

        using HttpResponseMessage antes = await LerAsync(Rota(tenant), token, ct);
        await factory.ComEscopoAsync(contexto => contexto.Database.ExecuteSqlAsync(
            $"UPDATE members SET status = 'Deactivated' WHERE tenant_id = {tenant}", ct));
        using HttpResponseMessage depois = await LerAsync(Rota(tenant), token, ct);

        antes.StatusCode.Should().Be(HttpStatusCode.OK, "controle: antes da desativação, o mesmo token lê");
        await DeveSerOProibidoPadraoAsync(depois, "membro desativado", ct);
    }

    [Fact]
    public async Task Todo403DaRota_TemOMesmoCorpoEOsMesmosCabecalhos()
    {
        // Seis motivos diferentes — quatro num tenant que existe, dois num que não existe —, comparados campo a campo:
        // fora o correlationId e o traceId, que são do pedido, as respostas são idênticas. Quem chama não aprende por
        // que foi negado, nem se o tenant existe.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Guid tenant, Guid admin, _) = await TenantComAdminAsync(ct);
        var inexistente = Guid.NewGuid();

        (string Rota, string Token)[] pedidos =
        [
            (Rota(tenant), factory.Emissor.Emitir(admin, SoReader, tenant.ToString())),
            (Rota(tenant), factory.Emissor.Emitir(admin, PlatformAdminETenantAdmin, tenant.ToString())),
            (Rota(tenant), factory.Emissor.Emitir(admin, SoTenantAdmin, OutroTenant)),
            (Rota(tenant), factory.Emissor.Emitir(Guid.NewGuid(), SoTenantAdmin, tenant.ToString())),
            (Rota(inexistente), factory.Emissor.Emitir(admin, SoTenantAdmin, tenant.ToString())),
            (Rota(inexistente), factory.Emissor.Emitir(admin, SoTenantAdmin, inexistente.ToString())),
        ];

        List<string> corpos = [];
        List<string> cabecalhos = [];

        foreach ((string rota, string token) in pedidos)
        {
            using HttpResponseMessage resposta = await LerAsync(rota, token, ct);
            JsonElement corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>(ct);

            corpos.Add(string.Join(
                " | ",
                corpo.EnumerateObject()
                    .Where(campo => campo.Name is not ("correlationId" or "traceId"))
                    .Select(campo => $"{campo.Name}={campo.Value.GetRawText()}")));
            // O nome e o valor de cada cabeçalho: um cabeçalho presente em todas as respostas, com um valor por motivo,
            // diria tanto quanto um campo do corpo. Do X-Correlation-Id, que é do pedido, só o nome.
            cabecalhos.Add(string.Join(
                " | ",
                resposta.Headers.Concat(resposta.Content.Headers)
                    .OrderBy(cabecalho => cabecalho.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(cabecalho => EDaCorrelacao(cabecalho.Key)
                        ? cabecalho.Key
                        : $"{cabecalho.Key}: {string.Join(", ", cabecalho.Value)}")));
        }

        corpos.Distinct().Should().ContainSingle("o 403 não pode dizer por que negou");
        corpos[0].Should().Contain("status=403");
        cabecalhos.Distinct().Should().ContainSingle("nem pelos cabeçalhos");
        cabecalhos[0].Should().Contain("Content-Type: application/problem+json", "os valores entram na comparação");
    }

    private static bool EDaCorrelacao(string cabecalho) =>
        string.Equals(cabecalho, CorrelationIdMiddleware.HeaderName, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task SemToken_Responde401()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using HttpResponseMessage resposta = await LerAsync(Rota(Guid.NewGuid()), token: null, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TenantIdQueNaoEGuid_NaoCasaARota()
    {
        // A restrição :guid tira o pedido da rota antes da policy: é um caminho não mapeado, e responde 404 a quem está
        // autenticado. Não diz nada sobre tenant nenhum — um id malformado não pode existir.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string token = factory.Emissor.Emitir(Guid.NewGuid(), SoTenantAdmin, OutroTenant);

        using HttpResponseMessage resposta = await LerAsync("/api/v1/tenants/acme", token, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
