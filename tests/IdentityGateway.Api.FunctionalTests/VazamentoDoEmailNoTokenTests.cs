using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using IdentityGateway.Api.FunctionalTests.Logs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Serilog.Events;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// Um token que carrega e-mail não o deixa em corpo, cabeçalho, log nem trace — no sucesso e em cada recusa.
/// </summary>
/// <remarks>
/// <para>
/// <b>Com o log no nível mais falador que a Api teria</b>: <c>Debug</c> em tudo, inclusive em
/// <c>Microsoft.AspNetCore</c>, onde o handler do JwtBearer registra a falha de validação com a exceção. É o cenário
/// de quem está diagnosticando um problema de autenticação em produção — o pior para vazar.
/// </para>
/// <para>
/// <b>Procura quatro formas</b>: o e-mail em texto; o e-mail em base64url, nos três alinhamentos possíveis (ele viaja
/// dentro do payload); o token inteiro; e o segmento do payload.
/// </para>
/// <para>
/// <b>Nenhuma asserção de ausência sem a de presença.</b> Cada caso confere antes que o log e o trace daquele pedido
/// foram capturados (pela rota, que leva um GUID único): sem isso, "não achei o e-mail" passaria com o coletor vazio.
/// E confere que o log veio no nível prometido, por uma linha em <c>Debug</c> do <c>Microsoft.AspNetCore</c> com a
/// mesma rota: sem isso, o teste passaria no nível de Development, onde o handler do JwtBearer fala menos.
/// </para>
/// <para>
/// <b>A ausência é afirmada por booleano, com o nome da forma.</b> Um <c>NotContain</c> imprimiria, ao falhar, o valor
/// procurado e o texto onde procurou — o token, justamente quando ele vazou.
/// </para>
/// <para>
/// Duas exceções declaradas à regra "só configuração" da factory: o processador de spans em memória, registrado por
/// <c>ConfigureTestServices</c>, e o nível de log.
/// </para>
/// </remarks>
public sealed class VazamentoDoEmailNoTokenTests : IClassFixture<IdentityGatewayApiFactory>, IDisposable
{
    private static readonly string[] PlatformAdmin = ["platform-admin"];

    private static readonly RSA ChaveForasteira = RSA.Create(2048);

    private readonly IdentityGatewayApiFactory _factory;
    private readonly WebApplicationFactory<Program> _comCaptura;
    private readonly ExportadorDeSpansEmMemoria _spans = new();
    private readonly string _canal = Guid.NewGuid().ToString("N");

    public VazamentoDoEmailNoTokenTests(IdentityGatewayApiFactory factory)
    {
        _factory = factory;
        _comCaptura = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Serilog:MinimumLevel:Default", "Debug");
            builder.UseSetting("Serilog:MinimumLevel:Override:Microsoft.AspNetCore", "Debug");
            builder.UseSetting("Serilog:WriteTo:9:Args:canal", _canal);
            builder.ConfigureTestServices(services => services.ConfigureOpenTelemetryTracerProvider(
                tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(_spans))));
        });
    }

    public void Dispose()
    {
        _comCaptura.Dispose();
        _spans.Dispose();
    }

    private static long Agora => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static TheoryDataRow<Func<IdentityGatewayApiFactory, Dictionary<string, object?>, string>, HttpStatusCode> Caso(
        string rotulo, HttpStatusCode esperado, Func<IdentityGatewayApiFactory, Dictionary<string, object?>, string> assinar) =>
        new(assinar, esperado) { Label = rotulo };

    /// <summary>
    /// Cada caso recebe o payload já com o e-mail e decide como estragá-lo e assiná-lo.
    /// </summary>
    /// <remarks>
    /// O primeiro parâmetro é a factory, e não o emissor: o emissor de teste é <c>internal</c>, e um membro público de
    /// uma classe de teste pública não pode expor tipo interno na assinatura.
    /// </remarks>
    public static TheoryData<Func<IdentityGatewayApiFactory, Dictionary<string, object?>, string>, HttpStatusCode> Casos => new()
    {
        // 404: autenticado e autorizado; o tenant da rota não existe.
        Caso("sucesso", HttpStatusCode.NotFound, (alvo, payload) => alvo.Emissor.Assinar(payload)),
        Caso("sem o papel (403)", HttpStatusCode.Forbidden, (alvo, payload) =>
        {
            payload.Remove("roles");
            return alvo.Emissor.Assinar(payload);
        }),
        Caso("vencido", HttpStatusCode.Unauthorized, (alvo, payload) =>
        {
            payload["exp"] = Agora - 120;
            return alvo.Emissor.Assinar(payload);
        }),
        Caso("audiência errada", HttpStatusCode.Unauthorized, (alvo, payload) =>
        {
            payload["aud"] = "account";
            return alvo.Emissor.Assinar(payload);
        }),
        Caso("assinatura inválida", HttpStatusCode.Unauthorized, (alvo, payload) =>
            alvo.Emissor.Assinar(payload, ChaveForasteira)),
        Caso("azp fora da lista", HttpStatusCode.Unauthorized, (alvo, payload) =>
        {
            payload["azp"] = "outro-client";
            return alvo.Emissor.Assinar(payload);
        }),
    };

    [Theory]
    [MemberData(nameof(Casos))]
    public async Task TokenComEmail_NaoDeixaOEmailEmRespostaLogNemTrace(
        Func<IdentityGatewayApiFactory, Dictionary<string, object?>, string> assinar, HttpStatusCode esperado)
    {
        ArgumentNullException.ThrowIfNull(assinar);
        CancellationToken ct = TestContext.Current.CancellationToken;
        string email = $"segredo{Guid.NewGuid():N}@acme.test";
        string rota = $"/api/v1/tenants/{Guid.NewGuid()}/provisioning";

        Dictionary<string, object?> payload = _factory.Emissor.Payload(roles: PlatformAdmin);
        payload["email"] = email;
        payload["preferred_username"] = email;
        payload["name"] = email;
        string token = assinar(_factory, payload);

        using HttpClient client = _comCaptura.CreateClient();
        using HttpRequestMessage pedido = new(HttpMethod.Get, rota);
        pedido.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

        using HttpResponseMessage resposta = await client.SendAsync(pedido, ct);
        string corpo = await resposta.Content.ReadAsStringAsync(ct);
        string cabecalhos = resposta.Headers.ToString();

        resposta.StatusCode.Should().Be(esperado);

        // O span da requisição é exportado quando ela termina de verdade, um instante depois de a resposta chegar.
        for (int tentativa = 0; tentativa < 40 && !DoPedido(_spans.Textos, rota); tentativa++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        }

        var coletor = ColetorDeLogsDaApi.DoCanal(_canal);
        IReadOnlyList<string> logs = coletor.Textos;
        IReadOnlyList<string> spans = _spans.Textos;

        // Presença primeiro: o log e o trace DESTE pedido foram capturados — e o log, no nível que o teste promete. Em
        // Development o Microsoft.AspNetCore fica em Information; uma linha dele em Debug com a rota só existe se o
        // nível pedido no construtor chegou ao Serilog.
        DoPedido(logs, rota).Should().BeTrue("o log do pedido precisa ter sido capturado");
        DoPedido(spans, rota).Should().BeTrue("o span do pedido precisa ter sido capturado");
        coletor.Eventos.Any(evento => EmDebugDoAspNetCore(evento, rota))
            .Should().BeTrue("o log do pedido precisa ter sido capturado com o Microsoft.AspNetCore em Debug");

        (string Nome, string Valor)[] segredos =
        [
            .. FormasDeUmSegredo.De(email).Select((forma, indice) =>
                (indice == 0 ? "o e-mail em texto" : $"o e-mail em base64url (alinhamento {indice - 1})", forma)),
            ("o token inteiro", token),
            ("o payload do token", token.Split('.')[1]),
        ];

        // Booleano com o nome da forma, e não NotContain: ao falhar, o NotContain imprime o que procurou e o texto
        // inteiro onde procurou — e, num vazamento, é ali que o token está.
        foreach ((string nome, string valor) in segredos)
        {
            corpo.Contains(valor, StringComparison.Ordinal)
                .Should().BeFalse($"{nome} não pode aparecer no corpo da resposta");
            cabecalhos.Contains(valor, StringComparison.Ordinal)
                .Should().BeFalse($"{nome} não pode aparecer nos cabeçalhos da resposta");
            logs.Any(texto => texto.Contains(valor, StringComparison.Ordinal))
                .Should().BeFalse($"{nome} não pode aparecer no log");
            spans.Any(texto => texto.Contains(valor, StringComparison.Ordinal))
                .Should().BeFalse($"{nome} não pode aparecer nos spans");
        }
    }

    [Theory]
    [InlineData("a@b.co")]
    [InlineData("segredo0123456789@acme.test")]
    [InlineData("segredo+0123456789ab@acme.test")]
    public void FormasEmBase64_SaoAchadasEmQualquerPosicaoDoPayload(string email)
    {
        // O detector do teste acima, provado: com o e-mail em qualquer deslocamento dentro de um JSON, ao menos uma das
        // três formas aparece no base64url. Sem isto, "não achei o e-mail em base64" poderia ser defeito do detector.
        IReadOnlyList<string> formas = FormasDeUmSegredo.De(email);

        formas.Should().HaveCount(4).And.Contain(email);

        for (int prefixo = 0; prefixo < 7; prefixo++)
        {
            for (int sufixo = 0; sufixo < 4; sufixo++)
            {
                string json = "{\"x\":\"" + new string('p', prefixo) + "\",\"email\":\"" + email + "\""
                              + new string(' ', sufixo) + ",\"y\":1}";
                string codificado = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));

                formas.Skip(1).Any(forma => codificado.Contains(forma, StringComparison.Ordinal))
                    .Should().BeTrue($"prefixo de {prefixo}, sufixo de {sufixo}");
            }
        }
    }

    private static bool DoPedido(IReadOnlyList<string> textos, string rota) =>
        textos.Any(texto => texto.Contains(rota, StringComparison.Ordinal));

    private static bool EmDebugDoAspNetCore(LogEvent evento, string rota) =>
        evento.Level == LogEventLevel.Debug
        && evento.Properties.TryGetValue("SourceContext", out LogEventPropertyValue? origem)
        && origem.ToString().Contains("Microsoft.AspNetCore", StringComparison.Ordinal)
        && evento.RenderMessage(CultureInfo.InvariantCulture).Contains(rota, StringComparison.Ordinal);
}
