using System.Collections.Concurrent;
using System.Net;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using NSubstitute;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O handler põe o token e, num 401, troca o token e tenta de novo — uma vez só.
/// </summary>
public sealed class ServiceAccountTokenHandlerTests
{
    private sealed class EndpointSequencial : ITokenEndpoint
    {
        private int _chamadas;

        public int Chamadas => Volatile.Read(ref _chamadas);

        public Task<TokenObtido> ObterAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new TokenObtido($"t{Interlocked.Increment(ref _chamadas)}", TimeSpan.FromMinutes(5)));
    }

    private static (HttpMessageInvoker Invocador, EndpointSequencial Endpoint, ConcurrentQueue<string> TokensVistos)
        Montar(Func<string, HttpStatusCode> statusPorToken)
    {
        IDateTimeProvider relogio = Substitute.For<IDateTimeProvider>();
        relogio.UtcNow.Returns(DateTimeOffset.UtcNow);

        EndpointSequencial endpoint = new();
        ConcurrentQueue<string> vistos = new();

        HandlerFalso keycloak = new((pedido, _) =>
        {
            string token = pedido.Headers.Authorization!.Parameter!;
            vistos.Enqueue(token);
            return Task.FromResult(new HttpResponseMessage(statusPorToken(token)));
        });

        ServiceAccountTokenHandler handler = new(new ServiceAccountTokenCache(endpoint, relogio))
        {
            InnerHandler = keycloak,
        };

        return (new HttpMessageInvoker(handler), endpoint, vistos);
    }

    [Fact]
    public async Task Com401_InvalidaBuscaTokenNovoERepeteUmaVez()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (HttpMessageInvoker invocador, EndpointSequencial endpoint, ConcurrentQueue<string> vistos) = Montar(
            token => token == "t1" ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);

        using HttpRequestMessage pedido = new(HttpMethod.Get, "http://keycloak.test/admin/realms/x/organizations");
        using HttpResponseMessage resposta = await invocador.SendAsync(pedido, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        vistos.Should().Equal("t1", "t2");
        endpoint.Chamadas.Should().Be(2);
    }

    [Fact]
    public async Task SegundoAinda401_DevolveO401SemLaco()
    {
        // Token recém-emitido recusado é problema de configuração (papel, realm, relógio), não de expiração.
        // Insistir transformaria um erro de configuração em laço contra o Keycloak.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (HttpMessageInvoker invocador, EndpointSequencial _, ConcurrentQueue<string> vistos) =
            Montar(_ => HttpStatusCode.Unauthorized);

        using HttpRequestMessage pedido = new(HttpMethod.Get, "http://keycloak.test/admin/realms/x/organizations");
        using HttpResponseMessage resposta = await invocador.SendAsync(pedido, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        vistos.Should().HaveCount(2);
    }

    [Fact]
    public async Task Sem401_UsaOTokenDoCacheSemBuscarOutro()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (HttpMessageInvoker invocador, EndpointSequencial endpoint, ConcurrentQueue<string> _) =
            Montar(_ => HttpStatusCode.OK);

        using HttpRequestMessage primeiro = new(HttpMethod.Get, "http://keycloak.test/a");
        using HttpRequestMessage segundo = new(HttpMethod.Get, "http://keycloak.test/b");
        (await invocador.SendAsync(primeiro, ct)).Dispose();
        (await invocador.SendAsync(segundo, ct)).Dispose();

        endpoint.Chamadas.Should().Be(1);
    }
}
