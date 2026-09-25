using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O cliente do token endpoint pede token com um assertion novo a cada tentativa, e nunca repete sozinho.
/// </summary>
public sealed class KeycloakTokenClientTests
{
    private static HttpResponseMessage TokenOk(string token = "t1", int expiraEm = 300) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"access_token":"{{token}}","expires_in":{{expiraEm}},"token_type":"Bearer"}""",
                Encoding.UTF8,
                "application/json"),
        };

    private static KeycloakTokenClient Criar(HandlerFalso handler)
    {
        IOptions<KeycloakAdminOptions> opcoes = OpcoesDeTeste.Keycloak();
        IDateTimeProvider relogio = Substitute.For<IDateTimeProvider>();
        relogio.UtcNow.Returns(DateTimeOffset.UtcNow);

        IHttpClientFactory fabrica = Substitute.For<IHttpClientFactory>();
        fabrica.CreateClient(KeycloakTokenClient.NomeDoCliente)
            .Returns(_ => new HttpClient(handler, disposeHandler: false));

        return new KeycloakTokenClient(
            fabrica,
            opcoes,
            new ClientAssertionFactory(new GatewaySigningKey(opcoes), opcoes, relogio),
            NullLogger<KeycloakTokenClient>.Instance);
    }

    [Fact]
    public async Task ObterAsync_EnviaClientCredentialsComAssertionParaOTokenEndpointDoRealm()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Uri? destino = null;
        Dictionary<string, string> formulario = [];

        HandlerFalso handler = new(async (pedido, token) =>
        {
            destino = pedido.RequestUri;
            string corpo = await pedido.Content!.ReadAsStringAsync(token);
            formulario = corpo.Split('&')
                .Select(par => par.Split('=', 2))
                .ToDictionary(par => Uri.UnescapeDataString(par[0]), par => Uri.UnescapeDataString(par[1]));
            return TokenOk();
        });

        TokenObtido obtido = await Criar(handler).ObterAsync(ct);

        obtido.AccessToken.Should().Be("t1");
        obtido.ExpiraEm.Should().Be(TimeSpan.FromSeconds(300));
        destino.Should().Be(new Uri("http://keycloak.test:8080/realms/identity-gateway/protocol/openid-connect/token"));
        formulario["grant_type"].Should().Be("client_credentials");
        formulario["client_assertion_type"].Should().Be("urn:ietf:params:oauth:client-assertion-type:jwt-bearer");
        formulario.Should().ContainKey("client_assertion");
    }

    [Fact]
    public async Task ObterAsync_DuasTentativas_GeramAssertionsComJtiDiferente()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ConcurrentQueue<string> jtis = new();

        HandlerFalso handler = new(async (pedido, token) =>
        {
            string corpo = await pedido.Content!.ReadAsStringAsync(token);
            string assertion = Uri.UnescapeDataString(
                corpo.Split('&').Single(par => par.StartsWith("client_assertion=", StringComparison.Ordinal))
                    .Split('=', 2)[1]);
            JsonWebToken jwt = new(assertion);
            using var payload = JsonDocument.Parse(Base64UrlEncoder.Decode(jwt.EncodedPayload));
            jtis.Enqueue(payload.RootElement.GetProperty("jti").GetString()!);
            return TokenOk();
        });

        KeycloakTokenClient cliente = Criar(handler);
        await cliente.ObterAsync(ct);
        await cliente.ObterAsync(ct);

        // Reenviar o mesmo assertion seria "Token reuse detected": cada tentativa precisa de um jti próprio.
        jtis.Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task ObterAsync_Recusado_LancaComStatusECodigoDeErroSemOAssertion()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        HandlerFalso handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                """{"error":"invalid_client","error_description":"Invalid client or Invalid client credentials"}""",
                Encoding.UTF8,
                "application/json"),
        }));

        Func<Task> obter = () => Criar(handler).ObterAsync(ct);

        (await obter.Should().ThrowAsync<HttpRequestException>())
            .Which.Should().Match<HttpRequestException>(excecao =>
                excecao.StatusCode == HttpStatusCode.Unauthorized
                && excecao.Message.Contains("invalid_client", StringComparison.Ordinal)
                && !excecao.Message.Contains("eyJ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClienteRegistrado_NaoRepeteONoTokenEndpoint()
    {
        // Composição real: se alguém pendurar resiliência no cliente do token, o mesmo corpo seria reenviado — e
        // recusado por reuso de jti. Um 503 aqui precisa sair na primeira tentativa.
        CancellationToken ct = TestContext.Current.CancellationToken;
        HandlerFalso handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        IConfiguration configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Keycloak:Admin:BaseUrl"] = "http://keycloak.test:8080",
                ["Keycloak:Admin:Realm"] = "identity-gateway",
                ["Keycloak:Admin:ClientId"] = "identity-gateway",
                ["Keycloak:Admin:PrivateKeyPem"] = ChavesDeTeste.Gerar().PemPrivado,
                ["Keycloak:Admin:AllowInsecureHttp"] = "true",
            })
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDateTimeProvider>());
        services.AddOptions<IdentityGateway.Infrastructure.Configuration.HttpResilienceOptions>();
        services.AddKeycloakIdentity(configuracao);
        services.AddHttpClient(KeycloakTokenClient.NomeDoCliente).ConfigurePrimaryHttpMessageHandler(() => handler);

        await using ServiceProvider provider = services.BuildServiceProvider();
        ITokenEndpoint endpoint = provider.GetRequiredService<ITokenEndpoint>();

        Func<Task> obter = () => endpoint.ObterAsync(ct);

        await obter.Should().ThrowAsync<HttpRequestException>();
        handler.Chamadas.Should().Be(1);
    }
}
