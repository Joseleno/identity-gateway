using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// Exercita a autenticação e os cabeçalhos de segurança.
/// </summary>
/// <remarks>
/// <para>
/// <b>Estes testes existem porque os outros passam.</b> Um teste de caso de uso usa cliente autenticado, e
/// continuaria passando se a autorização fosse removida dos endpoints — um token a mais no cabeçalho não
/// incomoda quem não o exige. O que prova que a proteção está ligada é a requisição <b>sem</b> token receber 401,
/// e é isso que está aqui.
/// </para>
/// <para>
/// Vale contra o token ausente e contra o token errado, que falham por motivos diferentes: o primeiro não chega a
/// ser validado, o segundo é rejeitado pela assinatura.
/// </para>
/// <para>
/// <b>Um caminho que não existe também responde <c>401</c> sem token.</b> A policy de fallback exige usuário
/// autenticado para tudo o que não declara outra coisa, inclusive para o que o roteamento não achou: quem não se
/// identificou não fica sabendo o que existe. Com token, o mesmo caminho responde <c>404</c>.
/// </para>
/// </remarks>
public sealed class SegurancaTests(IdentityGatewayApiFactory factory) : IClassFixture<IdentityGatewayApiFactory>
{
    private const string RotaProtegida = "/api/v1/tenants";

    private static readonly string[] PlatformAdmin = ["platform-admin"];

    [Fact]
    public async Task SemToken_Retorna401()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage resposta = await client.PostAsync(RotaProtegida, content: null, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ComTokenAssinadoPorOutraChave_Retorna401()
    {
        // RS256, com o mesmo kid que a Api conhece, assinado por outra chave: tudo confere menos a assinatura. É o caso
        // que prova que ela é verificada — um HS256 qualquer seria recusado já pelo algoritmo, e provaria menos.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClient();
        using var outraChave = RSA.Create(2048);
        string token = factory.Emissor.Assinar(factory.Emissor.Payload(roles: PlatformAdmin), outraChave);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage resposta = await client.PostAsync(RotaProtegida, content: null, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task OsHealthChecks_ContinuamAbertos()
    {
        // Exigir token no health check quebraria o orquestrador: o Kubernetes não se autentica, e a instância
        // saudável seria marcada como morta e reiniciada em laço.
        //
        // Só o live: o ready passou a exigir o Keycloak, que a suíte funcional não sobe. O ready é coberto pelos
        // testes de integração (Unhealthy com Keycloak fora, Healthy contra o container) e pelo job de compose da CI.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage live = await client.GetAsync("/health/live", ct);

        live.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TodaResposta_TrazOsCabecalhosDeSeguranca()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClient();

        // Numa resposta 401, de propósito: os cabeçalhos precisam valer também para o que o pipeline recusa,
        // e não só para o caminho feliz.
        HttpResponseMessage resposta = await client.PostAsync(RotaProtegida, content: null, ct);

        resposta.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        resposta.Headers.GetValues("X-Frame-Options").Should().Contain("DENY");
        resposta.Headers.GetValues("Referrer-Policy").Should().Contain("no-referrer");
        resposta.Headers.Should().Contain(cabecalho => cabecalho.Key == "Content-Security-Policy");
    }

    [Fact]
    public async Task SemToken_OCorpoEProblemDetailsEOCabecalhoNaoDizPorQue()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage resposta = await client.PostAsync(RotaProtegida, content: null, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        resposta.Headers.WwwAuthenticate.ToString().Should().Be("Bearer");
        (resposta.Content.Headers.ContentType?.MediaType).Should().Be("application/problem+json");

        JsonElement problema = await resposta.Content.ReadFromJsonAsync<JsonElement>(ct);
        problema.GetProperty("status").GetInt32().Should().Be(401);
        problema.GetProperty("title").GetString().Should().Be("Não autenticado");
        problema.GetProperty("type").GetString().Should().Contain("rfc9110");
        problema.GetProperty("correlationId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task TokenValidoSemOPapel_Retorna403ComOProblemDetailsUnico()
    {
        // Autenticado, mas sem platform-admin. O 403 não diz o que faltou.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado();

        HttpResponseMessage resposta = await client.PostAsync(RotaProtegida, content: null, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (resposta.Content.Headers.ContentType?.MediaType).Should().Be("application/problem+json");

        JsonElement problema = await resposta.Content.ReadFromJsonAsync<JsonElement>(ct);
        problema.GetProperty("status").GetInt32().Should().Be(403);
        problema.GetProperty("title").GetString().Should().Be("Acesso negado");
        problema.GetProperty("detail").GetString().Should().NotContain("platform-admin").And.NotContain("roles");
        problema.GetProperty("correlationId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CaminhoNaoMapeado_SemToken401EComToken404()
    {
        // A prova da policy de fallback: nenhum endpoint responde aqui, e mesmo assim quem não se identificou leva 401.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient anonimo = factory.CreateClient();
        using HttpClient autenticado = factory.CreateClientAutenticado();
        Uri caminho = new("/api/v1/nao-existe", UriKind.Relative);

        HttpResponseMessage semToken = await anonimo.GetAsync(caminho, ct);
        HttpResponseMessage comToken = await autenticado.GetAsync(caminho, ct);

        semToken.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        comToken.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
