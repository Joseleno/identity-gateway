using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using IdentityGateway.Api.FunctionalTests.Oidc;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog.Events;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// O host com o ambiente <c>Production</c>: o que a subida recusa, e o que os pedidos recebem.
/// </summary>
/// <remarks>
/// <para>
/// <b>Com pedidos, e não só a subida.</b> "Sobe em Production" não diz se um token é aceito ou recusado ali. Cada
/// ramo de produção tem um pedido que o atravessa.
/// </para>
/// <para>
/// O OIDC falso serve HTTPS com um certificado autoassinado gerado em memória, porque fora de Development os
/// metadados só vêm por <c>https</c>; a Api confia nele pela impressão digital, e em mais nada.
/// </para>
/// </remarks>
public sealed class HostEmProducaoTests
{
    private const string EnderecoPublico = "https://sso.exemplo.test";

    private const string Emissor = EnderecoPublico + "/realms/identity-gateway";

    private const string ClientAdministrativo = "console-administrativo";

    private const string ListaComOClientAdministrativo = "Keycloak:Auth:AllowedClients:0";

    private static readonly string[] PlatformAdmin = ["platform-admin"];

    /// <summary>O emissor de teste, o OIDC falso em HTTPS e a Api em Production apontada para ele.</summary>
    private sealed class Cenario(EmissorDeTeste emissor, OidcFalso oidc, ApiEmProducaoFactory api) : IAsyncDisposable
    {
        public EmissorDeTeste Emissor { get; } = emissor;

        public ApiEmProducaoFactory Api { get; } = api;

        /// <summary>Um token de platform-admin obtido pelo client administrativo, com os ajustes do caso.</summary>
        public string Token(Action<Dictionary<string, object?>>? ajustar = null) => Emissor.Emitir(
            roles: PlatformAdmin,
            ajustar: payload =>
            {
                payload["azp"] = ClientAdministrativo;
                ajustar?.Invoke(payload);
            });

        public async ValueTask DisposeAsync()
        {
            await Api.DisposeAsync();
            await oidc.DisposeAsync();
            Emissor.Dispose();
        }
    }

    private static async Task<Cenario> SubirAsync(params (string Chave, string? Valor)[] extras)
    {
        EmissorDeTeste emissor = new(Emissor);
        OidcFalso oidc = await OidcFalso.IniciarAsync(emissor, https: true);
        ApiEmProducaoFactory api = new(
            ApiEmProducaoFactory.Configuracao(oidc.BaseUrl, EnderecoPublico, extras), QueConfiaSoEm(oidc.Certificado!));

        return new Cenario(emissor, oidc, api);
    }

    private static HttpClientHandler QueConfiaSoEm(X509Certificate2 certificado) => new()
    {
        ServerCertificateCustomValidationCallback = (_, apresentado, _, _) =>
            apresentado is not null
            && string.Equals(apresentado.Thumbprint, certificado.Thumbprint, StringComparison.Ordinal),
    };

    private static async Task<HttpResponseMessage> RegistrarSemCorpoAsync(
        HttpClient client, string token, CancellationToken ct)
    {
        using HttpRequestMessage pedido = new(HttpMethod.Post, "/api/v1/tenants");
        pedido.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

        return await client.SendAsync(pedido, ct);
    }

    private static IEnumerable<Exception> Cadeia(Exception excecao)
    {
        Queue<Exception> fila = new();
        fila.Enqueue(excecao);

        while (fila.TryDequeue(out Exception? atual))
        {
            yield return atual;

            if (atual is AggregateException agregada)
            {
                foreach (Exception interna in agregada.InnerExceptions)
                {
                    fila.Enqueue(interna);
                }
            }
            else if (atual.InnerException is not null)
            {
                fila.Enqueue(atual.InnerException);
            }
        }
    }

    /// <summary>Tenta subir o host e devolve a falha de validação das options — direta ou embrulhada pelo host.</summary>
    private static OptionsValidationException FalhaDeSubida(IReadOnlyDictionary<string, string?> configuracao)
    {
        using ApiEmProducaoFactory api = new(configuracao);

        Exception? falha = Record.Exception(() => api.CreateClient());

        falha.Should().NotBeNull("a subida precisava falhar com esta configuração");
        OptionsValidationException? validacao = Cadeia(falha!).OfType<OptionsValidationException>().FirstOrDefault();
        validacao.Should().NotBeNull($"a subida falhou com {falha!.GetType().Name}, e não por validação das options");

        return validacao!;
    }

    // ───────────────────────────── a subida ─────────────────────────────

    [Fact]
    public async Task ConfiguracaoDeProducaoValida_SobeEOLiveResponde()
    {
        // O controle: sem ele, "tudo falha ao subir em Production" deixaria os três testes de recusa verdes.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Cenario cenario = await SubirAsync((ListaComOClientAdministrativo, ClientAdministrativo));
        using HttpClient client = cenario.Api.CreateClient();

        HttpResponseMessage live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), ct);

        live.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void ClientDeDemonstracaoNaLista_ASubidaFalha()
    {
        // DT4. O client público de device flow é só do ambiente local.
        OptionsValidationException falha = FalhaDeSubida(ApiEmProducaoFactory.Configuracao(
            "https://keycloak.interno.test", EnderecoPublico,
            ("Keycloak:Auth:AllowedClients:0", ClientAdministrativo),
            ("Keycloak:Auth:AllowedClients:1", "identity-gateway-demo")));

        falha.Message.Should().Contain("AllowedClients").And.Contain("Development")
            .And.NotContain(ClientAdministrativo);
    }

    [Fact]
    public void TransporteEmHttpComAllowInsecureHttp_ASubidaFalha()
    {
        // A guarda HttpSoEmDesenvolvimento. Com a flag, o BaseUrl em http passa na regra "https ou a flag"; quem recusa
        // é a regra "a flag só em Development". É a prova que a fatia C deixou pendente.
        OptionsValidationException falha = FalhaDeSubida(ApiEmProducaoFactory.Configuracao(
            "http://keycloak:8080", EnderecoPublico, ("Keycloak:Admin:AllowInsecureHttp", "true")));

        falha.Message.Should().Contain("AllowInsecureHttp").And.Contain("Development");
    }

    [Fact]
    public void EnderecoPublicoEmHttp_ASubidaFalha()
    {
        // O outro ramo da mesma guarda: o emissor aceito em http diria que tokens emitidos em claro valem.
        OptionsValidationException falha = FalhaDeSubida(ApiEmProducaoFactory.Configuracao(
            "https://keycloak.interno.test", "http://sso.exemplo.test"));

        falha.Message.Should().Contain("PublicBaseUrl").And.Contain("Development");
    }

    [Fact]
    public void TransporteEmHttpSemAFlag_ASubidaFalha()
    {
        OptionsValidationException falha = FalhaDeSubida(ApiEmProducaoFactory.Configuracao(
            "http://keycloak:8080", EnderecoPublico));

        falha.Message.Should().Contain("BaseUrl").And.Contain("https");
    }

    // ───────────────────────────── os pedidos ─────────────────────────────

    [Fact]
    public async Task OpcoesResolvidas_ExigemHttpsNosMetadadosComPrazoDe5s()
    {
        await using Cenario cenario = await SubirAsync((ListaComOClientAdministrativo, ClientAdministrativo));

        JwtBearerOptions jwt = cenario.Api.Services
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);

        jwt.RequireHttpsMetadata.Should().BeTrue();
        jwt.BackchannelTimeout.Should().Be(TimeSpan.FromSeconds(5));
        jwt.IncludeErrorDetails.Should().BeFalse();
        jwt.MetadataAddress.Should().StartWith("https://127.0.0.1:");
    }

    [Fact]
    public async Task TokenDeClientDaLista_PassaDaAutenticacao()
    {
        // O controle dos pedidos: em Production, com HTTPS de ponta a ponta nos metadados, um token válido entra. O
        // POST sem corpo responde 400 — depois da autenticação e da policy.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Cenario cenario = await SubirAsync((ListaComOClientAdministrativo, ClientAdministrativo));
        using HttpClient client = cenario.Api.CreateClient();

        using HttpResponseMessage resposta = await RegistrarSemCorpoAsync(client, cenario.Token(), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AudienciaErrada_401SemDetalheDoErroTambemEmProducao()
    {
        // IncludeErrorDetails é falso em todo ambiente: não há ramo "em produção esconde, em desenvolvimento mostra".
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Cenario cenario = await SubirAsync((ListaComOClientAdministrativo, ClientAdministrativo));
        using HttpClient client = cenario.Api.CreateClient();

        using HttpResponseMessage resposta = await RegistrarSemCorpoAsync(
            client, cenario.Token(payload => payload["aud"] = "account"), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        resposta.Headers.WwwAuthenticate.ToString().Should().Be("Bearer").And.NotContain("error_description");
    }

    [Fact]
    public async Task TokenDoClientDeDemonstracao_EmProducaoLeva401()
    {
        // O demo não está na lista (e nem poderia). O token dele é válido em tudo — assinatura, emissor, audiência — e
        // é recusado só pelo azp.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Cenario cenario = await SubirAsync((ListaComOClientAdministrativo, ClientAdministrativo));
        using HttpClient client = cenario.Api.CreateClient();

        using HttpResponseMessage resposta = await RegistrarSemCorpoAsync(
            client, cenario.Emissor.Emitir(roles: PlatformAdmin), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListaDeClientsVazia_SobeAvisaERecusaTokenValido()
    {
        // Fail-closed: a API sobe (o provisionamento e os health checks não dependem de usuário), avisa na subida, e
        // nenhum token de usuário entra.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Cenario cenario = await SubirAsync();
        using HttpClient client = cenario.Api.CreateClient();

        using HttpResponseMessage resposta = await RegistrarSemCorpoAsync(client, cenario.Token(), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        cenario.Api.Logs.Eventos.Should().Contain(evento =>
            evento.Level == LogEventLevel.Warning
            && evento.MessageTemplate.Text.Contains("AllowedClients", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MetadadosFrios_401EmPoucosSegundosComOAvisoESemOTokenNoLog()
    {
        // DT5 e DT6. O provedor aceita a conexão e nunca responde, e a Api ainda não tem os metadados: o pedido leva
        // 401 (não 500), em cerca de 5 s (o prazo dos metadados; o padrão de 60 s reprovaria), e o log diz que o
        // problema é a busca das chaves — sem o token.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using TcpListener mudo = new(IPAddress.Loopback, 0);
        mudo.Start();
        int porta = ((IPEndPoint)mudo.LocalEndpoint).Port;

        using EmissorDeTeste emissor = new(Emissor);
        await using ApiEmProducaoFactory api = new(ApiEmProducaoFactory.Configuracao(
            $"https://127.0.0.1:{porta}", EnderecoPublico, (ListaComOClientAdministrativo, ClientAdministrativo)));
        using HttpClient client = api.CreateClient();
        string token = emissor.Emitir(roles: PlatformAdmin, ajustar: payload => payload["azp"] = ClientAdministrativo);

        var relogio = Stopwatch.StartNew();
        using HttpResponseMessage resposta = await RegistrarSemCorpoAsync(client, token, ct);
        relogio.Stop();

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        relogio.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(7));
        api.Logs.Eventos.Should().Contain(evento =>
            evento.Level == LogEventLevel.Warning
            && evento.MessageTemplate.Text.Contains("indisponíveis", StringComparison.Ordinal));
        api.Logs.Textos.Should().NotContain(texto =>
            texto.Contains(token, StringComparison.Ordinal)
            || texto.Contains(token.Split('.')[1], StringComparison.Ordinal));
    }
}
