using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IdentityGateway.Testing.Keycloak;

/// <summary>
/// Um navegador mínimo para o Keycloak: conclui o link de ações e faz login pelo Device Authorization Grant.
/// </summary>
/// <remarks>
/// <para>
/// <b>Uma instância é uma sessão de navegador</b>: o jar de cookies vive nela. Um segundo login na mesma instância
/// encontra o cookie de SSO e pula a página de login; para entrar como outro usuário, use outra instância.
/// </para>
/// <para>
/// <b>Só o device flow, e a senha só no formulário do Keycloak.</b> O harness submete a página de login com uma senha
/// que o próprio teste definiu; a credencial nunca passa pela Gateway, e por isso não é ROPC (ADR-003).
/// </para>
/// <para>
/// <b>Endereço público e de transporte.</b> O Keycloak escreve os links com o <c>KC_HOSTNAME</c>, que nos testes não
/// resolve (<c>keycloak.test:8081</c>). O harness disca o endereço de transporte e mantém o <c>Host</c> público.
/// </para>
/// <para>
/// <b>Nada do que ele registra ou lança carrega segredo</b>: nem token, nem senha, nem código de dispositivo, nem link,
/// nem HTML. Uma página desconhecida vira exceção com o título, o id do formulário e os NOMES dos campos.
/// </para>
/// </remarks>
public sealed partial class HarnessDeLogin : IDisposable
{
    private const int LimiteDePaginas = 12;
    private const int LimiteDeRedirecionamentos = 10;

    private readonly HttpClient _http;
    private readonly Dictionary<string, Biscoito> _cookies = new(StringComparer.Ordinal);
    private readonly Uri _publico;
    private readonly string _realm;
    private readonly string _clientId;
    private readonly Action<string> _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _esperar;

    public HarnessDeLogin(
        Uri enderecoPublico, Uri enderecoDeTransporte, string clientId, Action<string>? log = null,
        string realm = "identity-gateway")
        : this(
            enderecoPublico,
            enderecoDeTransporte,
            clientId,
            new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false },
            Task.Delay,
            log,
            realm)
    {
    }

    /// <summary>Para os testes do próprio harness: o transporte e a espera são injetados.</summary>
    internal HarnessDeLogin(
        Uri enderecoPublico, Uri enderecoDeTransporte, string clientId, HttpMessageHandler transporte,
        Func<TimeSpan, CancellationToken, Task> esperar, Action<string>? log = null,
        string realm = "identity-gateway")
    {
        ArgumentNullException.ThrowIfNull(enderecoPublico);
        ArgumentNullException.ThrowIfNull(enderecoDeTransporte);

        _publico = enderecoPublico;
        _realm = realm;
        _clientId = clientId;
        _log = log ?? (_ => { });
        _esperar = esperar;
        _http = new HttpClient(new ReescritaDeAutoridade(enderecoPublico, enderecoDeTransporte, transporte))
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    /// <summary>Quantas vezes o formulário de login foi enviado no último login (1 ou 2; 0 com sessão viva).</summary>
    public int PassosDoUltimoLogin { get; private set; }

    public void Dispose() => _http.Dispose();

    /// <summary>Segue o link do e-mail de ações: define a senha e preenche o perfil.</summary>
    public async Task ConcluirLinkDeAcoesAsync(Uri link, string novaSenha, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);

        Pagina pagina = await NavegarAsync(HttpMethod.Get, link, corpo: null, cancellationToken);
        await PercorrerAsync("link de ações", pagina, usuario: null, novaSenha, cancellationToken);
    }

    /// <summary>
    /// Abre o link do e-mail de ações <b>sem concluí-lo</b>: verdadeiro se o Keycloak mostrou a página de ações.
    /// </summary>
    /// <remarks>
    /// A primeira página do link só informa o que será pedido e oferece o "prosseguir"; abri-la não consome o link. É
    /// como se confere que um convite chegou com um link que funciona, deixando-o para o convidado.
    /// </remarks>
    public async Task<bool> LinkDeAcoesAbreAsync(Uri link, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);

        Pagina pagina = await NavegarAsync(HttpMethod.Get, link, corpo: null, cancellationToken);

        return pagina.Status == 200 && pagina.TemInformacao && !pagina.TemErro;
    }

    /// <summary>Login pelo device flow, com o consentimento. Devolve o access token e o refresh token.</summary>
    public async Task<TokensDeUsuario> TokenPorDispositivoAsync(
        string usuario, string senha, CancellationToken cancellationToken)
    {
        const string etapa = "device flow";

        using JsonDocument pedido = await PostarFormularioAsync(
            etapa,
            Rota("protocol/openid-connect/auth/device"),
            [new("client_id", _clientId), new("scope", "openid")],
            aceitarErro: false,
            cancellationToken);

        string deviceCode = pedido.RootElement.GetProperty("device_code").GetString()!;
        Uri verificacao = new(pedido.RootElement.GetProperty("verification_uri_complete").GetString()!);
        int intervalo = pedido.RootElement.GetProperty("interval").GetInt32();
        DateTimeOffset prazo = DateTimeOffset.UtcNow.AddSeconds(pedido.RootElement.GetProperty("expires_in").GetInt32());

        // O login e o consentimento acontecem ANTES do primeiro poll: um poll antes do intervalo leva slow_down mesmo
        // com a autorização já concedida.
        PassosDoUltimoLogin = 0;
        Pagina pagina = await NavegarAsync(HttpMethod.Get, verificacao, corpo: null, cancellationToken);
        await PercorrerAsync(etapa, pagina, usuario, senha, cancellationToken);

        while (true)
        {
            await _esperar(TimeSpan.FromSeconds(intervalo), cancellationToken);

            if (DateTimeOffset.UtcNow > prazo)
            {
                throw new FalhaDoHarnessException(FamiliaDeFalha.Prazo, etapa, "o device code expirou antes do token.");
            }

            using JsonDocument resposta = await PostarFormularioAsync(
                etapa,
                Rota("protocol/openid-connect/token"),
                [
                    new("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                    new("client_id", _clientId),
                    new("device_code", deviceCode),
                ],
                aceitarErro: true,
                cancellationToken);

            if (resposta.RootElement.TryGetProperty("access_token", out _))
            {
                return LerTokens(resposta.RootElement);
            }

            string erro = resposta.RootElement.TryGetProperty("error", out JsonElement codigo)
                ? codigo.GetString() ?? "desconhecido"
                : "desconhecido";

            switch (erro)
            {
                case "authorization_pending":
                    break;
                case "slow_down":
                    intervalo += 5;
                    _log($"{etapa}: slow_down, intervalo agora {intervalo}s");
                    break;
                default:
                    // expired_token, access_denied e o resto: repetir não conserta.
                    throw new FalhaDoHarnessException(FamiliaDeFalha.DeviceFlow, etapa, $"o token endpoint respondeu {erro}.");
            }
        }
    }

    /// <summary>
    /// Renova pelo refresh token. Com a rotação ligada, o refresh token usado deixa de valer: quem chama guarda o novo
    /// antes de qualquer outro passo, e NUNCA repete a chamada — o reuso derruba a sessão inteira do client.
    /// </summary>
    public async Task<TokensDeUsuario> RenovarAsync(string refreshToken, CancellationToken cancellationToken)
    {
        const string etapa = "renovação";

        using JsonDocument resposta = await PostarFormularioAsync(
            etapa,
            Rota("protocol/openid-connect/token"),
            [new("grant_type", "refresh_token"), new("client_id", _clientId), new("refresh_token", refreshToken)],
            aceitarErro: true,
            cancellationToken);

        if (!resposta.RootElement.TryGetProperty("access_token", out _))
        {
            string erro = resposta.RootElement.TryGetProperty("error", out JsonElement codigo)
                ? codigo.GetString() ?? "desconhecido"
                : "desconhecido";

            throw new FalhaDoHarnessException(
                FamiliaDeFalha.DeviceFlow, etapa, $"o token endpoint respondeu {erro}; o caminho é um device flow novo.");
        }

        return LerTokens(resposta.RootElement);
    }

    private static TokensDeUsuario LerTokens(JsonElement raiz) => new(
        raiz.GetProperty("access_token").GetString()!,
        raiz.GetProperty("refresh_token").GetString()!,
        TimeSpan.FromSeconds(raiz.GetProperty("expires_in").GetInt32()));

    private Uri Rota(string caminho) => new($"{_publico.ToString().TrimEnd('/')}/realms/{_realm}/{caminho}");

    // ───────────────────────────── as páginas ─────────────────────────────

    private async Task PercorrerAsync(
        string etapa, Pagina pagina, string? usuario, string senha, CancellationToken cancellationToken)
    {
        bool usuarioEnviado = false;
        bool senhaEnviada = false;

        for (int passo = 0; passo < LimiteDePaginas; passo++)
        {
            _log($"{etapa}: página \"{pagina.Titulo}\" ({pagina.Descricao()})");

            if (pagina.TemErro)
            {
                throw Desconhecida(etapa, pagina, "o Keycloak mostrou a página de erro");
            }

            if (pagina.Formulario("kc-form-login") is { } login)
            {
                bool pedeSenha = login.Tem("password");

                // O mesmo formulário de volta quer dizer credencial recusada. Insistir não conserta, e cada envio
                // conta como tentativa para a proteção contra força bruta do realm, que bloquearia a conta.
                if (pedeSenha ? senhaEnviada : usuarioEnviado)
                {
                    throw new FalhaDoHarnessException(
                        FamiliaDeFalha.Formulario, etapa,
                        "o Keycloak devolveu o formulário de login: usuário ou senha recusados.");
                }

                Dictionary<string, string> campos = login.Ocultos();

                if (login.Tem("username") && !login.EhOculto("username"))
                {
                    campos["username"] = usuario
                        ?? throw Desconhecida(etapa, pagina, "o fluxo pediu login, e nenhum usuário foi informado");
                }

                if (pedeSenha)
                {
                    campos["password"] = senha;
                }

                usuarioEnviado = true;
                senhaEnviada |= pedeSenha;
                PassosDoUltimoLogin++;
                pagina = await NavegarAsync(HttpMethod.Post, login.Acao, campos, cancellationToken);
            }
            else if (pagina.Formulario("kc-passwd-update-form") is { } novaSenha)
            {
                Dictionary<string, string> campos = novaSenha.Ocultos();
                campos["password-new"] = senha;
                campos["password-confirm"] = senha;
                pagina = await NavegarAsync(HttpMethod.Post, novaSenha.Acao, campos, cancellationToken);
            }
            else if (pagina.Formulario("kc-update-profile-form") is { } perfil)
            {
                Dictionary<string, string> campos = perfil.Valores();
                campos["firstName"] = "Teste";
                campos["lastName"] = "Harness";
                pagina = await NavegarAsync(HttpMethod.Post, perfil.Acao, campos, cancellationToken);
            }
            else if (pagina.FormularioCom("accept") is { } consentimento)
            {
                Dictionary<string, string> campos = consentimento.Ocultos();
                campos["accept"] = "Yes";
                pagina = await NavegarAsync(HttpMethod.Post, consentimento.Acao, campos, cancellationToken);
            }
            else if (pagina.LinkDeProsseguir is { } prosseguir)
            {
                pagina = await NavegarAsync(HttpMethod.Get, prosseguir, corpo: null, cancellationToken);
            }
            else if (pagina.TemInformacao)
            {
                // A página final: "Your account has been updated" ou "Device Login Successful".
                return;
            }
            else
            {
                throw Desconhecida(etapa, pagina, "página que o harness não conhece");
            }
        }

        throw new FalhaDoHarnessException(FamiliaDeFalha.Formulario, etapa, $"mais de {LimiteDePaginas} páginas sem chegar ao fim.");
    }

    private static FalhaDoHarnessException Desconhecida(string etapa, Pagina pagina, string motivo) =>
        new(FamiliaDeFalha.Formulario, etapa, $"{motivo}. Título: \"{pagina.Titulo}\"; {pagina.Descricao()}.");

    // ───────────────────────────── o transporte ─────────────────────────────

    private async Task<Pagina> NavegarAsync(
        HttpMethod metodo, Uri destino, Dictionary<string, string>? corpo, CancellationToken cancellationToken)
    {
        for (int salto = 0; salto < LimiteDeRedirecionamentos; salto++)
        {
            using HttpRequestMessage pedido = new(metodo, destino);

            if (corpo is not null)
            {
                pedido.Content = new FormUrlEncodedContent(corpo);
            }

            string cabecalho = CookiesPara(destino);

            if (cabecalho.Length > 0)
            {
                pedido.Headers.TryAddWithoutValidation("Cookie", cabecalho);
            }

            using HttpResponseMessage resposta = await _http.SendAsync(pedido, cancellationToken);
            GuardarCookies(resposta);

            if ((int)resposta.StatusCode is >= 300 and < 400 && resposta.Headers.Location is { } proximo)
            {
                destino = proximo.IsAbsoluteUri ? proximo : new Uri(destino, proximo);
                metodo = HttpMethod.Get;
                corpo = null;
                continue;
            }

            string html = await resposta.Content.ReadAsStringAsync(cancellationToken);
            return new Pagina(destino, (int)resposta.StatusCode, html);
        }

        throw new FalhaDoHarnessException(
            FamiliaDeFalha.Formulario, "navegação", $"mais de {LimiteDeRedirecionamentos} redirecionamentos.");
    }

    private async Task<JsonDocument> PostarFormularioAsync(
        string etapa, Uri destino, KeyValuePair<string, string>[] campos, bool aceitarErro,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage pedido = new(HttpMethod.Post, destino) { Content = new FormUrlEncodedContent(campos) };
        using HttpResponseMessage resposta = await _http.SendAsync(pedido, cancellationToken);
        string corpo = await resposta.Content.ReadAsStringAsync(cancellationToken);

        if (!resposta.IsSuccessStatusCode && !aceitarErro)
        {
            throw new FalhaDoHarnessException(
                FamiliaDeFalha.DeviceFlow, etapa, $"{destino.AbsolutePath} respondeu {(int)resposta.StatusCode}.");
        }

        try
        {
            return JsonDocument.Parse(corpo);
        }
        catch (JsonException)
        {
            throw new FalhaDoHarnessException(
                FamiliaDeFalha.DeviceFlow, etapa,
                $"{destino.AbsolutePath} respondeu {(int)resposta.StatusCode} com um corpo que não é JSON.");
        }
    }

    // ───────────────────────────── os cookies ─────────────────────────────

    // Jar manual: o Keycloak marca os cookies como Secure, e o CookieContainer não os devolveria por http.
    private sealed record Biscoito(string Nome, string Valor, string Caminho);

    private void GuardarCookies(HttpResponseMessage resposta)
    {
        if (!resposta.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cabecalhos))
        {
            return;
        }

        foreach (string cabecalho in cabecalhos)
        {
            string[] partes = cabecalho.Split(';', StringSplitOptions.TrimEntries);
            int igual = partes[0].IndexOf('=', StringComparison.Ordinal);

            if (igual <= 0)
            {
                continue;
            }

            string nome = partes[0][..igual];
            string valor = partes[0][(igual + 1)..];
            string caminho = "/";
            bool vencido = valor.Length == 0;

            foreach (string atributo in partes.Skip(1))
            {
                if (atributo.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
                {
                    caminho = atributo[5..];
                }
                else if (atributo.StartsWith("Max-Age=", StringComparison.OrdinalIgnoreCase))
                {
                    vencido |= int.TryParse(atributo[8..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int idade)
                               && idade <= 0;
                }
                else if (atributo.StartsWith("Expires=", StringComparison.OrdinalIgnoreCase))
                {
                    vencido |= DateTimeOffset.TryParse(
                                   atributo[8..], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
                                   out DateTimeOffset quando)
                               && quando <= DateTimeOffset.UtcNow;
                }
            }

            string chave = $"{nome}|{caminho}";

            if (vencido)
            {
                _cookies.Remove(chave);
            }
            else
            {
                _cookies[chave] = new Biscoito(nome, valor, caminho);
            }
        }
    }

    private string CookiesPara(Uri destino) => string.Join(
        "; ",
        _cookies.Values
            .Where(cookie => destino.AbsolutePath.StartsWith(cookie.Caminho, StringComparison.Ordinal))
            .Select(cookie => $"{cookie.Nome}={cookie.Valor}"));

    /// <summary>Troca o endereço público pelo de transporte, mantendo o <c>Host</c> público.</summary>
    private sealed class ReescritaDeAutoridade(Uri publico, Uri transporte, HttpMessageHandler interno)
        : DelegatingHandler(interno)
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri original = request.RequestUri!;

            if (string.Equals(original.Authority, publico.Authority, StringComparison.OrdinalIgnoreCase))
            {
                request.RequestUri = new UriBuilder(original)
                {
                    Scheme = transporte.Scheme,
                    Host = transporte.Host,
                    Port = transporte.Port,
                }.Uri;
                request.Headers.Host = publico.Authority;
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    // ───────────────────────────── o HTML ─────────────────────────────

    private sealed partial class Pagina(Uri endereco, int status, string html)
    {
        private readonly List<Formulario> _formularios =
            [.. Formularios().Matches(html).Select(achado => new Formulario(endereco, achado.Value))];

        public int Status { get; } = status;

        public string Titulo { get; } = Titulos().Match(html) is { Success: true } titulo
            ? WebUtility.HtmlDecode(titulo.Groups[1].Value).Trim()
            : string.Empty;

        public bool TemErro { get; } = html.Contains("id=\"kc-error-message\"", StringComparison.Ordinal);

        public bool TemInformacao { get; } = html.Contains("id=\"kc-info-message\"", StringComparison.Ordinal);

        /// <summary>O link "clique para prosseguir" da página de informação — só o que continua o fluxo de login.</summary>
        public Uri? LinkDeProsseguir { get; } = Links().Matches(html)
            .Select(achado => WebUtility.HtmlDecode(achado.Groups[1].Value))
            .Where(href => href.Contains("/login-actions/", StringComparison.Ordinal))
            .Select(href => Uri.TryCreate(href, UriKind.Absolute, out Uri? absoluto) ? absoluto : new Uri(endereco, href))
            .FirstOrDefault();

        public Formulario? Formulario(string id) => _formularios.FirstOrDefault(formulario => formulario.Id == id);

        public Formulario? FormularioCom(string campo) => _formularios.FirstOrDefault(formulario => formulario.Tem(campo));

        /// <summary>Só nomes: id dos formulários e nomes dos campos, nunca valores nem HTML.</summary>
        public string Descricao() => _formularios.Count == 0
            ? $"HTTP {Status}, sem formulário"
            : $"HTTP {Status}, " + string.Join(
                "; ", _formularios.Select(f => $"formulário \"{f.Id}\" com os campos [{string.Join(", ", f.Nomes)}]"));

        [GeneratedRegex("<form\\b.*?</form>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex Formularios();

        [GeneratedRegex("<title>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex Titulos();

        [GeneratedRegex("<a\\b[^>]*\\bhref=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
        private static partial Regex Links();
    }

    private sealed partial class Formulario
    {
        private readonly List<(string Nome, string Tipo, string Valor)> _campos;

        public Formulario(Uri pagina, string html)
        {
            string abertura = Abertura().Match(html).Value;
            Id = Atributo(abertura, "id");

            string acao = WebUtility.HtmlDecode(Atributo(abertura, "action"));
            Acao = Uri.TryCreate(acao, UriKind.Absolute, out Uri? absoluta) ? absoluta : new Uri(pagina, acao);

            _campos =
            [
                .. Campos().Matches(html)
                    .Select(achado => achado.Value)
                    .Where(campo => Atributo(campo, "name").Length > 0)
                    .Select(campo => (
                        Atributo(campo, "name"),
                        Atributo(campo, "type").ToLowerInvariant(),
                        WebUtility.HtmlDecode(Atributo(campo, "value")))),
            ];
        }

        public string Id { get; }

        public Uri Acao { get; }

        public IEnumerable<string> Nomes => _campos.Select(campo => campo.Nome).Distinct(StringComparer.Ordinal);

        public bool Tem(string nome) => _campos.Any(campo => campo.Nome == nome);

        public bool EhOculto(string nome) => _campos.Any(campo => campo.Nome == nome && campo.Tipo == "hidden");

        /// <summary>Os campos ocultos, com o valor que a página trouxe.</summary>
        public Dictionary<string, string> Ocultos() => _campos
            .Where(campo => campo.Tipo == "hidden")
            .GroupBy(campo => campo.Nome, StringComparer.Ordinal)
            .ToDictionary(grupo => grupo.Key, grupo => grupo.First().Valor, StringComparer.Ordinal);

        /// <summary>Todos os campos de valor (sem os botões), com o que a página trouxe.</summary>
        public Dictionary<string, string> Valores() => _campos
            .Where(campo => campo.Tipo is not ("submit" or "button"))
            .GroupBy(campo => campo.Nome, StringComparer.Ordinal)
            .ToDictionary(grupo => grupo.Key, grupo => grupo.First().Valor, StringComparer.Ordinal);

        private static string Atributo(string elemento, string nome)
        {
            Match achado = Regex.Match(
                elemento, $"\\b{Regex.Escape(nome)}\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);

            return achado.Success ? achado.Groups[1].Value : string.Empty;
        }

        [GeneratedRegex("<form\\b[^>]*>", RegexOptions.IgnoreCase)]
        private static partial Regex Abertura();

        [GeneratedRegex("<(?:input|button)\\b[^>]*>", RegexOptions.IgnoreCase)]
        private static partial Regex Campos();
    }
}
