using System.Net;
using System.Text;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O que o harness faz com cada resposta, sem Keycloak: transporte e espera são injetados.
/// </summary>
/// <remarks>
/// O comportamento contra o Keycloak real está em <c>HarnessContraKeycloakTests</c>. Aqui ficam os ramos que o
/// Keycloak real não produz por encomenda — consentimento negado, código expirado, <c>slow_down</c>, página
/// desconhecida — e as duas garantias que nenhum teste de integração enxerga: nada de segredo na exceção, e nenhuma
/// renovação repetida.
/// </remarks>
public sealed class HarnessDeLoginTests
{
    private const string Publico = "http://keycloak.test:8081";

    private const string PaginaFinal =
        "<html><head><title>Sign in to identity-gateway</title></head><body><div id=\"kc-info-message\">ok</div></body></html>";

    private const string PedidoDeDispositivo =
        """
        {"device_code":"CODIGO-DO-DISPOSITIVO","user_code":"ABCD-EFGH",
         "verification_uri":"http://keycloak.test:8081/realms/identity-gateway/device",
         "verification_uri_complete":"http://keycloak.test:8081/realms/identity-gateway/device?user_code=ABCD-EFGH",
         "expires_in":300,"interval":5}
        """;

    private const string TokensEmitidos =
        """{"access_token":"ACCESS-SECRETO","refresh_token":"REFRESH-SECRETO","expires_in":300}""";

    private static HarnessDeLogin Criar(HandlerFalso transporte, List<TimeSpan>? esperas = null) => new(
        new Uri(Publico),
        new Uri("http://127.0.0.1:18081"),
        "identity-gateway-demo",
        transporte,
        (duracao, _) =>
        {
            esperas?.Add(duracao);
            return Task.CompletedTask;
        });

    private static HttpResponseMessage Json(HttpStatusCode status, string corpo) =>
        new(status) { Content = new StringContent(corpo, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Html(string corpo) =>
        new(HttpStatusCode.OK) { Content = new StringContent(corpo, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Redirecionar(string destino, string? cookie = null)
    {
        HttpResponseMessage resposta = new(HttpStatusCode.Found);
        resposta.Headers.Location = new Uri(destino);

        if (cookie is not null)
        {
            resposta.Headers.TryAddWithoutValidation("Set-Cookie", cookie);
        }

        return resposta;
    }

    private static bool EhOTokenEndpoint(HttpRequestMessage pedido) =>
        pedido.RequestUri!.AbsolutePath.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal);

    private static bool EhOPedidoDeDispositivo(HttpRequestMessage pedido) =>
        pedido.RequestUri!.AbsolutePath.EndsWith("/auth/device", StringComparison.Ordinal);

    /// <summary>Um device flow cuja autorização já foi concedida: só o token endpoint varia.</summary>
    private static HandlerFalso DeviceFlowCom(Func<int, HttpResponseMessage> tokenNaTentativa)
    {
        int tentativas = 0;

        return new HandlerFalso((pedido, _) =>
        {
            if (EhOPedidoDeDispositivo(pedido))
            {
                return Task.FromResult(Json(HttpStatusCode.OK, PedidoDeDispositivo));
            }

            return Task.FromResult(EhOTokenEndpoint(pedido)
                ? tokenNaTentativa(Interlocked.Increment(ref tentativas))
                : Html(PaginaFinal));
        });
    }

    [Fact]
    public async Task PaginaDesconhecida_LancaComTituloFormularioENomesDosCamposSemValores()
    {
        // O que vai para o log da CI quando o Keycloak mostra uma página que o harness não conhece: o bastante para
        // diagnosticar (título, id do formulário, NOMES dos campos) e nada que seja segredo.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HandlerFalso transporte = new((_, _) => Task.FromResult(Html(
            """
            <html><head><title>Página estranha</title></head><body>
            <form id="kc-otp-login-form" action="http://keycloak.test:8081/realms/identity-gateway/login-actions/x">
              <input type="hidden" name="credentialId" value="VALOR-SECRETO">
              <input type="text" name="otp">
            </form></body></html>
            """)));
        using HarnessDeLogin harness = Criar(transporte);

        Func<Task> concluir = () => harness.ConcluirLinkDeAcoesAsync(
            new Uri($"{Publico}/realms/identity-gateway/login-actions/action-token?key=CHAVE-SECRETA"),
            "SENHA-SECRETA",
            ct);

        FalhaDoHarnessException falha = (await concluir.Should().ThrowAsync<FalhaDoHarnessException>()).Which;
        falha.Familia.Should().Be(FamiliaDeFalha.Formulario);
        falha.Etapa.Should().Be("link de ações");
        falha.Message.Should().Contain("Página estranha").And.Contain("kc-otp-login-form")
            .And.Contain("credentialId").And.Contain("otp");
        falha.Message.Should().NotContain("VALOR-SECRETO").And.NotContain("CHAVE-SECRETA")
            .And.NotContain("SENHA-SECRETA").And.NotContain("<form");
    }

    [Fact]
    public async Task PaginaDeErroDoKeycloak_LancaNaHora()
    {
        // Link já usado ou vencido: o Keycloak responde com kc-error-message. Sem este ramo, a página viraria
        // "desconhecida" — ou, pior, um laço até o limite de páginas.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HandlerFalso transporte = new((_, _) => Task.FromResult(Html(
            "<html><head><title>Sign in to identity-gateway</title></head><body>"
            + "<p id=\"kc-error-message\">Action expired.</p></body></html>")));
        using HarnessDeLogin harness = Criar(transporte);

        Func<Task> concluir = () => harness.ConcluirLinkDeAcoesAsync(
            new Uri($"{Publico}/realms/identity-gateway/login-actions/action-token?key=x"), "s", ct);

        (await concluir.Should().ThrowAsync<FalhaDoHarnessException>())
            .Which.Message.Should().Contain("página de erro");
        transporte.Chamadas.Should().Be(1);
    }

    [Fact]
    public async Task LoginRecusado_FalhaNaPrimeiraVoltaDoFormularioSemInsistir()
    {
        // Senha errada: o Keycloak devolve o mesmo formulário. O realm tem proteção contra força bruta — um harness que
        // reenviasse até o limite de páginas bloquearia a conta que está testando. (Visto ao vivo: sem este ramo, doze
        // envios seguidos.)
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HandlerFalso transporte = new((pedido, _) => Task.FromResult(EhOPedidoDeDispositivo(pedido)
            ? Json(HttpStatusCode.OK, PedidoDeDispositivo)
            : Html(
                """
                <html><head><title>Sign in to identity-gateway</title></head><body>
                <form id="kc-form-login" action="http://keycloak.test:8081/realms/identity-gateway/login-actions/authenticate?x=1" method="post">
                  <input type="text" name="username"><input type="password" name="password">
                  <input type="hidden" name="credentialId"><button name="login" type="submit">Sign In</button>
                </form></body></html>
                """)));
        using HarnessDeLogin harness = Criar(transporte);

        Func<Task> entrar = () => harness.TokenPorDispositivoAsync("alguem@acme.test", "SENHA-SECRETA", ct);

        FalhaDoHarnessException falha = (await entrar.Should().ThrowAsync<FalhaDoHarnessException>()).Which;
        falha.Familia.Should().Be(FamiliaDeFalha.Formulario);
        falha.Message.Should().Contain("recusados").And.NotContain("SENHA-SECRETA");
        harness.PassosDoUltimoLogin.Should().Be(1);

        // O pedido de dispositivo, a página de verificação e UM envio do formulário.
        transporte.Chamadas.Should().Be(3);
    }

    [Theory]
    [InlineData("access_denied")]
    [InlineData("expired_token")]
    public async Task ErroDefinitivoNoToken_FalhaNaHoraSemRepetir(string erro)
    {
        // Foco de revisão 2: quem nega o consentimento ou deixa o código expirar não pode ficar esperando o prazo
        // inteiro, e repetir o pedido não conserta nenhum dos dois.
        CancellationToken ct = TestContext.Current.CancellationToken;
        int pedidosDeToken = 0;
        List<TimeSpan> esperas = [];
        using HandlerFalso transporte = DeviceFlowCom(tentativa =>
        {
            // Só a primeira resposta é o erro: se o harness insistisse, a segunda entregaria os tokens, e o teste
            // falharia por não ver a exceção — em vez de girar até o prazo.
            pedidosDeToken = tentativa;
            return tentativa == 1
                ? Json(HttpStatusCode.BadRequest, $$"""{"error":"{{erro}}"}""")
                : Json(HttpStatusCode.OK, TokensEmitidos);
        });
        using HarnessDeLogin harness = Criar(transporte, esperas);

        Func<Task> entrar = () => harness.TokenPorDispositivoAsync("alguem@acme.test", "SENHA-SECRETA", ct);

        FalhaDoHarnessException falha = (await entrar.Should().ThrowAsync<FalhaDoHarnessException>()).Which;
        falha.Familia.Should().Be(FamiliaDeFalha.DeviceFlow);
        falha.Message.Should().Contain(erro).And.NotContain("CODIGO-DO-DISPOSITIVO").And.NotContain("ABCD-EFGH");
        pedidosDeToken.Should().Be(1);
        esperas.Should().Equal(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConsentimentoNegado_FalhaNaHora()
    {
        // O nome que o Foco de revisão cita; o caso é o access_denied da theory acima, afirmado pelo nome do erro.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HandlerFalso transporte = DeviceFlowCom(tentativa => tentativa == 1
            ? Json(HttpStatusCode.BadRequest, """{"error":"access_denied"}""")
            : Json(HttpStatusCode.OK, TokensEmitidos));
        using HarnessDeLogin harness = Criar(transporte);

        Func<Task> entrar = () => harness.TokenPorDispositivoAsync("alguem@acme.test", "s", ct);

        (await entrar.Should().ThrowAsync<FalhaDoHarnessException>()).Which.Message.Should().Contain("access_denied");
    }

    [Fact]
    public async Task DeviceCodeExpirado_FalhaNaHora()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HandlerFalso transporte = DeviceFlowCom(tentativa => tentativa == 1
            ? Json(HttpStatusCode.BadRequest, """{"error":"expired_token"}""")
            : Json(HttpStatusCode.OK, TokensEmitidos));
        using HarnessDeLogin harness = Criar(transporte);

        Func<Task> entrar = () => harness.TokenPorDispositivoAsync("alguem@acme.test", "s", ct);

        (await entrar.Should().ThrowAsync<FalhaDoHarnessException>()).Which.Message.Should().Contain("expired_token");
    }

    [Fact]
    public async Task SlowDown_SomaCincoSegundosAoIntervaloESegue()
    {
        // O Keycloak responde slow_down a um poll antes do intervalo, mesmo com a autorização concedida, e não aumenta
        // o intervalo sozinho: a RFC 8628 manda o client somar 5 s.
        CancellationToken ct = TestContext.Current.CancellationToken;
        List<TimeSpan> esperas = [];
        using HandlerFalso transporte = DeviceFlowCom(tentativa => tentativa switch
        {
            1 => Json(HttpStatusCode.BadRequest, """{"error":"slow_down"}"""),
            2 => Json(HttpStatusCode.BadRequest, """{"error":"authorization_pending"}"""),
            _ => Json(HttpStatusCode.OK, TokensEmitidos),
        });
        using HarnessDeLogin harness = Criar(transporte, esperas);

        TokensDeUsuario tokens = await harness.TokenPorDispositivoAsync("alguem@acme.test", "s", ct);

        tokens.AccessToken.Should().Be("ACCESS-SECRETO");
        tokens.RefreshToken.Should().Be("REFRESH-SECRETO");
        esperas.Should().Equal(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Renovacao_Recusada_LancaSemRepetir()
    {
        // Com a rotação ligada, repetir uma renovação reusa o refresh token — e o reuso derruba a sessão inteira do
        // client. O harness não tem retry: uma chamada, e a falha diz que o caminho é um device flow novo.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HandlerFalso transporte = new((_, _) => Task.FromResult(
            Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""")));
        using HarnessDeLogin harness = Criar(transporte);

        Func<Task> renovar = () => harness.RenovarAsync("REFRESH-SECRETO", ct);

        FalhaDoHarnessException falha = (await renovar.Should().ThrowAsync<FalhaDoHarnessException>()).Which;
        falha.Message.Should().Contain("invalid_grant").And.Contain("device flow novo").And.NotContain("REFRESH-SECRETO");
        transporte.Chamadas.Should().Be(1);
    }

    [Fact]
    public async Task Cookies_SecureVoltaPorHttpEVencidoSome()
    {
        // O Keycloak marca os cookies como Secure; o CookieContainer não os devolveria por http, e o login se perderia
        // entre uma página e a seguinte. E um cookie apagado pelo servidor (Max-Age=0) não pode voltar.
        CancellationToken ct = TestContext.Current.CancellationToken;
        List<string?> cookiesRecebidos = [];
        using HandlerFalso transporte = new((pedido, _) =>
        {
            cookiesRecebidos.Add(pedido.Headers.TryGetValues("Cookie", out IEnumerable<string>? valores)
                ? string.Join("; ", valores)
                : null);

            return Task.FromResult(cookiesRecebidos.Count switch
            {
                1 => Redirecionar(
                    $"{Publico}/realms/identity-gateway/passo-2",
                    "AUTH_SESSION_ID=abc; Path=/realms/identity-gateway/; Secure; HttpOnly"),
                2 => Redirecionar(
                    $"{Publico}/realms/identity-gateway/passo-3",
                    "AUTH_SESSION_ID=; Max-Age=0; Path=/realms/identity-gateway/; Secure; HttpOnly"),
                _ => Html(PaginaFinal),
            });
        });
        using HarnessDeLogin harness = Criar(transporte);

        await harness.ConcluirLinkDeAcoesAsync(new Uri($"{Publico}/realms/identity-gateway/passo-1"), "s", ct);

        cookiesRecebidos.Should().Equal(null, "AUTH_SESSION_ID=abc", null);
    }

    [Fact]
    public async Task EnderecoPublico_EDiscadoNoTransporteComOHostPublico()
    {
        // KC_HOSTNAME=keycloak.test:8081 não resolve na máquina do teste: o harness disca a porta mapeada e mantém o
        // Host que o Keycloak espera.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Uri? discado = null;
        string? host = null;
        using HandlerFalso transporte = new((pedido, _) =>
        {
            discado = pedido.RequestUri;
            host = pedido.Headers.Host;
            return Task.FromResult(Html(PaginaFinal));
        });
        using HarnessDeLogin harness = Criar(transporte);

        await harness.ConcluirLinkDeAcoesAsync(new Uri($"{Publico}/realms/identity-gateway/x?key=1&tab=2"), "s", ct);

        discado!.Authority.Should().Be("127.0.0.1:18081");
        discado.PathAndQuery.Should().Be("/realms/identity-gateway/x?key=1&tab=2");
        host.Should().Be("keycloak.test:8081");
    }

    [Fact]
    public void TokensDeUsuario_ToStringNaoRevelaOsTokens()
    {
        TokensDeUsuario tokens = new("ACCESS-SECRETO", "REFRESH-SECRETO", TimeSpan.FromMinutes(5));

        tokens.ToString().Should().NotContain("SECRETO");
    }
}
