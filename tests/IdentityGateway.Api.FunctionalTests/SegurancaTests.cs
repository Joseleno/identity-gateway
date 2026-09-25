using System.Net;
using System.Net.Http.Headers;

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
/// <b>Os testes usam <c>POST</c>, e não <c>GET</c>.</b> A rota protegida que existe é o registro de tenant; a
/// autorização roda antes do model binding, então o <c>401</c> acontece sem o corpo importar. Com <c>GET</c>, o
/// roteamento responderia <c>404</c> antes de a autorização ser consultada, e o teste passaria a não provar
/// nada — que foi exatamente o motivo de eles terem nascido em <c>Skip</c>, antes de existir endpoint.
/// </para>
/// </remarks>
public sealed class SegurancaTests(IdentityGatewayApiFactory factory) : IClassFixture<IdentityGatewayApiFactory>
{
    private const string RotaProtegida = "/api/v1/tenants";

    [Fact]
    public async Task SemToken_Retorna401()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage resposta = await client.PostAsync(RotaProtegida, content: null, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ComTokenInvalido_Retorna401()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClient();

        // Assinado com outra chave — a forma é de um JWT, o conteúdo não confere. É o caso que prova que a
        // validação de assinatura está ligada: sem ela, qualquer um emitiria o próprio token.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJmYWxzbyJ9.assinatura-invalida");

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
}
