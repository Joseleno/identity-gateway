#:project ../tests/IdentityGateway.Testing.Keycloak
// Ferramenta de CI, nunca publicada: sem o AOT padrão dos apps de arquivo único, que transformaria o JSON por reflexão
// em erro (TreatWarningsAsErrors).
#:property PublishAot=false

// A jornada do compose, de ponta a ponta, como o job `Compose` da CI a roda — e como qualquer pessoa pode rodar com o
// compose de pé:  dotnet run tools/jornada-compose.cs -- <fase>
//
// Fases, uma por invocação (o estado entre elas vai num arquivo, nunca em memória):
//   convites --esperado N   conta os convites do platform-admin no mailpit: exatamente N
//   jornada                 link do platform-admin → device flow → HS256 antigo recusado → POST /tenants → Active →
//                           o admin do tenant conclui o convite, entra e lê o próprio tenant (200); outro tenant e o
//                           platform-admin recebem 403
//   antes-de-parar          renova o token e faz um GET autenticado (a api guarda as chaves do Keycloak)
//   com-keycloak-parado     com o Keycloak parado: POST /tenants → 202, e o tenant fica Pending
//   depois-de-voltar        com o Keycloak de volta: renova o token e espera o tenant ficar Active
//
// NUNCA imprime access token, refresh token, código de dispositivo, link de ação, senha nem HTML. As senhas são geradas
// em memória. O arquivo de estado guarda credenciais de um Keycloak descartável (refresh e access token) e é apagado no
// passo de limpeza do job.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IdentityGateway.Testing.Keycloak;

const string Realm = "identity-gateway";
const string ClientDeDemonstracao = "identity-gateway-demo";

// O contrato do 200 da leitura de tenant: exatamente estas chaves. Uma a mais (o e-mail do admin, por exemplo) reprova.
string[] chavesDoTenant = ["tenantId", "name", "slug", "status", "plan", "occupiedSeats", "registeredAt"];
string[] chavesDoPlano = ["tier", "maxUsers", "maxClients"];

Uri api = new(Ambiente("IG_API", "http://127.0.0.1:8080"));
Uri keycloakPublico = new(Ambiente("IG_KEYCLOAK_PUBLICO", "http://localhost:8081"));

// Na CI, sempre 127.0.0.1 no que é discado: `localhost` pode tentar ::1 primeiro, onde ninguém escuta.
Uri keycloakTransporte = new(Ambiente("IG_KEYCLOAK_TRANSPORTE", "http://127.0.0.1:8081"));
Uri mailpitUrl = new(Ambiente("IG_MAILPIT", "http://127.0.0.1:8025"));
string platformAdmin = Ambiente("PLATFORM_ADMIN_EMAIL", "platform-admin@identity-gateway.local").ToLowerInvariant();
string arquivoDeEstado = Ambiente("IG_ESTADO", Path.Combine(Path.GetTempPath(), "ig-jornada-estado.json"));
bool noGitHub = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";

List<(string Etapa, double Segundos)> etapas = [];
string etapaAtual = "início";
var relogio = Stopwatch.StartNew();
string fase = args.Length > 0 ? args[0] : string.Empty;

// O prazo de cada fase fica ABAIXO do timeout-minutes do passo que a roda no job. Se estourar, quem encerra é o app,
// dizendo a etapa e gravando a tabela do resumo — e não o runner, que mataria o processo sem dizer onde parou.
using CancellationTokenSource prazo = new(fase switch
{
    "convites" => TimeSpan.FromSeconds(50),
    "jornada" => TimeSpan.FromMinutes(4),
    "depois-de-voltar" => TimeSpan.FromMinutes(3),
    _ => TimeSpan.FromMinutes(1),
});
CancellationToken ct = prazo.Token;

using HttpClient http = new() { BaseAddress = api, Timeout = TimeSpan.FromSeconds(10) };
using ClienteDoMailpit mailpit = new(mailpitUrl);

try
{
    switch (fase)
    {
        case "convites":
            await ConvitesAsync(LerEsperado(args));
            break;
        case "jornada":
            await JornadaAsync();
            break;
        case "antes-de-parar":
            await AntesDePararAsync();
            break;
        case "com-keycloak-parado":
            await ComKeycloakParadoAsync();
            break;
        case "depois-de-voltar":
            await DepoisDeVoltarAsync();
            break;
        default:
            Console.Error.WriteLine(
                "uso: jornada-compose <convites --esperado N | jornada | antes-de-parar | com-keycloak-parado | depois-de-voltar>");
            return 2;
    }

    Resumir(falha: null);
    return 0;
}
catch (FalhaDoHarnessException falha)
{
    return Falhar(falha.Message, (int)falha.Familia);
}
catch (OperationCanceledException) when (prazo.IsCancellationRequested)
{
    return Falhar($"{etapaAtual}: o prazo da fase esgotou.", (int)FamiliaDeFalha.Prazo);
}
catch (OperationCanceledException)
{
    // Não foi o prazo da fase: foi uma chamada HTTP que não respondeu em 10 s — o HttpClient cancela a própria chamada.
    return Falhar($"{etapaAtual}: uma chamada HTTP ficou sem resposta por 10 s.", (int)FamiliaDeFalha.Prazo);
}
catch (HttpRequestException falha)
{
    // O que sobra aqui é o Keycloak: o mailpit e a API já embrulham as próprias falhas de conexão. A mensagem de uma
    // falha HTTP pode carregar a URL inteira; a query string (onde viajam chaves e códigos) é cortada.
    return Falhar(
        $"{etapaAtual}: o Keycloak não respondeu: {SemQueryStrings().Replace(falha.Message, "?…")}",
        (int)FamiliaDeFalha.DeviceFlow);
}

// ───────────────────────────── as fases ─────────────────────────────

async Task ConvitesAsync(int esperado)
{
    Etapa($"convites do platform-admin no mailpit: exatamente {esperado}");
    int achados = 0;

    // Espera até chegar ao número; depois, um intervalo curto antes de afirmar "exatamente" — obrigatório para zero,
    // e é o que pega um segundo e-mail que ainda estivesse a caminho.
    for (int tentativa = 0; tentativa < 30; tentativa++)
    {
        achados = (await mailpit.MensagensParaAsync(platformAdmin, ct)).Count;

        if (achados >= esperado)
        {
            break;
        }

        await Task.Delay(TimeSpan.FromSeconds(1), ct);
    }

    await Task.Delay(TimeSpan.FromSeconds(3), ct);
    achados = (await mailpit.MensagensParaAsync(platformAdmin, ct)).Count;

    if (achados != esperado)
    {
        throw new FalhaDoHarnessException(
            FamiliaDeFalha.Mailpit, etapaAtual, $"esperados {esperado} convites, achados {achados}.");
    }

    if (esperado > 0)
    {
        // Lança se a mensagem não trouxer o link no endereço público. O link em si nunca é impresso.
        await mailpit.LinkDeAcoesAsync(platformAdmin, keycloakPublico, Realm, ct);
    }
}

async Task JornadaAsync()
{
    using HarnessDeLogin harness = NovoHarness();
    string senha = Mascarar(SenhasDeTeste.Gerar());

    Etapa("o platform-admin conclui o convite pelo link do e-mail");
    Uri link = await mailpit.LinkDeAcoesAsync(platformAdmin, keycloakPublico, Realm, ct);
    await harness.ConcluirLinkDeAcoesAsync(link, senha, ct);

    Etapa("o platform-admin obtém o token pelo device flow");
    TokensDeUsuario tokens = await harness.TokenPorDispositivoAsync(platformAdmin, senha, ct);
    Mascarar(tokens.AccessToken);
    Mascarar(tokens.RefreshToken);

    // Antes de existir qualquer Organization, o Keycloak pede usuário e senha numa página só. Dois passos aqui
    // querem dizer que a jornada não começou de um realm limpo.
    if (harness.PassosDoUltimoLogin != 1)
    {
        throw new FalhaDoHarnessException(
            FamiliaDeFalha.Formulario, etapaAtual,
            $"o login levou {harness.PassosDoUltimoLogin} passos, esperado 1 (nenhuma Organization no realm).");
    }

    Etapa("a receita HS256 antiga é recusada com 401");
    await ExigirAsync(HttpStatusCode.Unauthorized, HttpMethod.Post, "/api/v1/tenants", ReceitaHs256Antiga(), corpo: null);

    Etapa("POST /api/v1/tenants com o token do Keycloak responde 202");
    string sufixo = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
    string emailDoAdmin = $"admin+{sufixo}@acme.test";
    Uri localizacao = await RegistrarTenantAsync(tokens.AccessToken, $"acme-ci-{sufixo}", emailDoAdmin);

    Etapa("o tenant chega a Active (até 90 s)");
    await EsperarStatusAsync(localizacao, tokens.AccessToken, "Active", TimeSpan.FromSeconds(90));

    Etapa("o convite do admin do tenant está no mailpit, com o link no endereço público");

    // Ao menos uma mensagem, nunca exatamente uma: a entrega "pelo menos uma vez" pode mandar duas.
    if ((await mailpit.EsperarMensagensAsync(emailDoAdmin, 1, ct)).Count < 1)
    {
        throw new FalhaDoHarnessException(
            FamiliaDeFalha.Mailpit, etapaAtual, "nenhum convite para o admin do tenant no mailpit.");
    }

    Uri linkDoAdmin = await mailpit.LinkDeAcoesAsync(emailDoAdmin, keycloakPublico, Realm, ct);

    // Um harness novo é uma janela anônima: sem o cookie de sessão do platform-admin, que faria o device flow seguinte
    // sair com a conta dele — e o 403 pareceria defeito.
    using HarnessDeLogin navegadorDoAdmin = NovoHarness();
    string senhaDoAdmin = Mascarar(SenhasDeTeste.Gerar());

    Etapa("o admin do tenant conclui o convite pelo link e obtém o token pelo device flow");
    await navegadorDoAdmin.ConcluirLinkDeAcoesAsync(linkDoAdmin, senhaDoAdmin, ct);
    TokensDeUsuario tokensDoAdmin = await navegadorDoAdmin.TokenPorDispositivoAsync(emailDoAdmin, senhaDoAdmin, ct);
    Mascarar(tokensDoAdmin.AccessToken);
    Mascarar(tokensDoAdmin.RefreshToken);

    // O Location do 202 é /api/v1/tenants/{id}/provisioning; a leitura do tenant é o mesmo caminho sem o sufixo.
    string rotaDoTenant = localizacao.ToString().Replace("/provisioning", string.Empty, StringComparison.Ordinal);

    Etapa("o admin lê o próprio tenant: 200, Active, com exatamente as chaves do contrato");
    using (HttpResponseMessage leitura = await ExigirAsync(
               HttpStatusCode.OK, HttpMethod.Get, rotaDoTenant, tokensDoAdmin.AccessToken, corpo: null))
    {
        JsonElement tenant = await leitura.Content.ReadFromJsonAsync<JsonElement>(ct);
        ExigirChaves(tenant, chavesDoTenant, "tenant");
        ExigirChaves(tenant.GetProperty("plan"), chavesDoPlano, "plan");

        if (tenant.GetProperty("status").GetString() != "Active")
        {
            throw new FalhaDoHarnessException(FamiliaDeFalha.Api, etapaAtual, "o tenant lido pelo admin não está Active.");
        }
    }

    Etapa("o admin recebe 403 ao ler outro tenant");
    await ExigirAsync(
        HttpStatusCode.Forbidden, HttpMethod.Get, $"/api/v1/tenants/{Guid.NewGuid()}", tokensDoAdmin.AccessToken, corpo: null);

    // O platform-admin registra e acompanha o provisionamento, mas não lê o tenant: sem auditoria, seria o único acesso
    // entre tenants sem trilha. O token dele é do começo da jornada, e de lá para cá houve a espera do Active e o
    // convite do admin: é renovado antes deste uso tardio. O refresh token novo é o que a fase grava no fim.
    Etapa("o platform-admin renova o token e recebe 403 ao ler o tenant");
    tokens = await harness.RenovarAsync(tokens.RefreshToken, ct);
    Mascarar(tokens.AccessToken);
    Mascarar(tokens.RefreshToken);
    await ExigirAsync(HttpStatusCode.Forbidden, HttpMethod.Get, rotaDoTenant, tokens.AccessToken, corpo: null);

    GravarEstado(new Estado(tokens.RefreshToken, AccessToken: null, localizacao.ToString(), LocalizacaoPendente: null));
}

async Task AntesDePararAsync()
{
    Estado estado = LerEstado();
    using HarnessDeLogin harness = NovoHarness();

    Etapa("renovação do token antes de parar o Keycloak");
    TokensDeUsuario tokens = await harness.RenovarAsync(estado.RefreshToken, ct);
    Mascarar(tokens.AccessToken);

    // O refresh token novo é gravado ANTES de qualquer outro passo: com a rotação, o antigo já não vale, e repetir a
    // renovação com ele derrubaria a sessão.
    estado = estado with { RefreshToken = Mascarar(tokens.RefreshToken), AccessToken = tokens.AccessToken };
    GravarEstado(estado);

    Etapa("um GET autenticado, para a api guardar as chaves do Keycloak");
    await ExigirAsync(HttpStatusCode.OK, HttpMethod.Get, estado.Localizacao, tokens.AccessToken, corpo: null);
}

async Task ComKeycloakParadoAsync()
{
    Estado estado = LerEstado();
    string token = estado.AccessToken
        ?? throw new FalhaDoHarnessException(FamiliaDeFalha.Api, "estado", "falta o access token da fase antes-de-parar.");

    // A pré-condição é o próprio Keycloak não responder, e não o /health/ready: a api guarda o token do service
    // account em memória por alguns minutos, e o ready continua 200 logo depois do stop.
    Etapa("pré-condição: o Keycloak não responde");
    await ExigirKeycloakParadoAsync();

    Etapa("com o Keycloak parado, POST /api/v1/tenants responde 202");
    string sufixo = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
    Uri localizacao = await RegistrarTenantAsync(token, $"acme-parado-{sufixo}", $"admin+parado{sufixo}@acme.test");

    // Logo depois do 202 todo tenant está Pending, com o Keycloak de pé ou parado: uma leitura só não provaria nada.
    // Quinze segundos cobrem a varredura do Outbox (5 s) e uma tentativa inteira de provisionar; com o Keycloak de pé,
    // o tenant já teria virado Active.
    Etapa("o tenant continua Pending por 15 s, enquanto o Keycloak não volta");
    await ExigirStatusEstavelAsync(localizacao, token, "Pending", TimeSpan.FromSeconds(15));

    GravarEstado(estado with { LocalizacaoPendente = localizacao.ToString() });
}

async Task DepoisDeVoltarAsync()
{
    Estado estado = LerEstado();
    string pendente = estado.LocalizacaoPendente
        ?? throw new FalhaDoHarnessException(FamiliaDeFalha.Api, "estado", "falta o tenant da fase com-keycloak-parado.");
    using HarnessDeLogin harness = NovoHarness();

    Etapa("renovação do token depois que o Keycloak voltou");
    TokensDeUsuario tokens = await harness.RenovarAsync(estado.RefreshToken, ct);
    Mascarar(tokens.AccessToken);
    GravarEstado(estado with { RefreshToken = Mascarar(tokens.RefreshToken), AccessToken = tokens.AccessToken });

    // O prazo cobre o teto do backoff do Outbox (60 s) mais uma tentativa inteira contra o Keycloak (10 s), com folga.
    Etapa("o tenant registrado com o Keycloak parado chega a Active (até 150 s)");
    await EsperarStatusAsync(new Uri(pendente, UriKind.RelativeOrAbsolute), tokens.AccessToken, "Active", TimeSpan.FromSeconds(150));
}

async Task ExigirKeycloakParadoAsync()
{
    using HttpClient sonda = new() { Timeout = TimeSpan.FromSeconds(3) };

    try
    {
        using HttpResponseMessage resposta = await sonda.GetAsync(
            new Uri(keycloakTransporte, $"/realms/{Realm}/.well-known/openid-configuration"), ct);
    }
    catch (Exception falha) when (falha is HttpRequestException
                                  || (falha is TaskCanceledException && !ct.IsCancellationRequested))
    {
        // Conexão recusada ou sem resposta em 3 s: é o Keycloak parado, que é o que a fase precisa.
        return;
    }

    throw new FalhaDoHarnessException(
        FamiliaDeFalha.DeviceFlow, etapaAtual, "o Keycloak ainda responde; esta fase precisa dele parado.");
}

// ───────────────────────────── a API ─────────────────────────────

async Task<Uri> RegistrarTenantAsync(string token, string slug, string emailDoAdmin)
{
    using HttpResponseMessage resposta = await ExigirAsync(
        HttpStatusCode.Accepted, HttpMethod.Post, "/api/v1/tenants", token,
        JsonContent.Create(new { name = "Acme", slug, planCode = "free", initialAdminEmail = emailDoAdmin }));

    return resposta.Headers.Location
        ?? throw new FalhaDoHarnessException(FamiliaDeFalha.Api, etapaAtual, "o 202 veio sem o cabeçalho Location.");
}

async Task EsperarStatusAsync(Uri localizacao, string token, string esperado, TimeSpan limite)
{
    DateTimeOffset fim = DateTimeOffset.UtcNow + limite;
    string ultimo = "(nenhum)";

    while (DateTimeOffset.UtcNow < fim)
    {
        using HttpResponseMessage resposta = await ExigirAsync(
            HttpStatusCode.OK, HttpMethod.Get, localizacao.ToString(), token, corpo: null);
        JsonElement corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>(ct);
        ultimo = corpo.GetProperty("status").GetString() ?? "(nulo)";

        if (ultimo == esperado)
        {
            return;
        }

        if (ultimo == "ProvisioningFailed")
        {
            throw new FalhaDoHarnessException(FamiliaDeFalha.Api, etapaAtual, "o tenant caiu em ProvisioningFailed.");
        }

        await Task.Delay(TimeSpan.FromSeconds(3), ct);
    }

    throw new FalhaDoHarnessException(
        FamiliaDeFalha.Prazo, etapaAtual, $"o tenant não chegou a {esperado} em {limite.TotalSeconds:0} s; ficou em {ultimo}.");
}

// Ao contrário de EsperarStatusAsync, que sai na primeira leitura igual à esperada: aqui TODA leitura do período
// precisa ser a esperada.
async Task ExigirStatusEstavelAsync(Uri localizacao, string token, string esperado, TimeSpan periodo)
{
    DateTimeOffset fim = DateTimeOffset.UtcNow + periodo;

    do
    {
        using HttpResponseMessage resposta = await ExigirAsync(
            HttpStatusCode.OK, HttpMethod.Get, localizacao.ToString(), token, corpo: null);
        JsonElement corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>(ct);
        string status = corpo.GetProperty("status").GetString() ?? "(nulo)";

        if (status != esperado)
        {
            throw new FalhaDoHarnessException(
                FamiliaDeFalha.Api, etapaAtual,
                $"o tenant passou a {status}; esperado {esperado} durante {periodo.TotalSeconds:0} s.");
        }

        await Task.Delay(TimeSpan.FromSeconds(3), ct);
    }
    while (DateTimeOffset.UtcNow < fim);
}

void ExigirChaves(JsonElement objeto, string[] esperadas, string nome)
{
    string[] vieram = [.. objeto.EnumerateObject().Select(chave => chave.Name).Order(StringComparer.Ordinal)];

    if (!vieram.SequenceEqual(esperadas.Order(StringComparer.Ordinal), StringComparer.Ordinal))
    {
        throw new FalhaDoHarnessException(
            FamiliaDeFalha.Api, etapaAtual,
            $"{nome}: esperadas as chaves [{string.Join(", ", esperadas)}], vieram [{string.Join(", ", vieram)}].");
    }
}

// Toda asserção de status é EXATA. "Diferente de 200" deixaria um 401 por token vencido passar por um 403 esperado.
async Task<HttpResponseMessage> ExigirAsync(
    HttpStatusCode esperado, HttpMethod metodo, string rota, string? token, HttpContent? corpo)
{
    using HttpRequestMessage pedido = new(metodo, new Uri(rota, UriKind.RelativeOrAbsolute)) { Content = corpo };

    if (token is not null)
    {
        pedido.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    HttpResponseMessage resposta;

    try
    {
        resposta = await http.SendAsync(pedido, ct);
    }
    catch (HttpRequestException falha)
    {
        throw new FalhaDoHarnessException(
            FamiliaDeFalha.Api, etapaAtual, $"a API não respondeu em {metodo} {new Uri(api, rota).AbsolutePath}.", falha);
    }

    if (resposta.StatusCode != esperado)
    {
        int veio = (int)resposta.StatusCode;
        resposta.Dispose();

        throw new FalhaDoHarnessException(
            FamiliaDeFalha.Api, etapaAtual,
            $"{metodo} {new Uri(api, rota).AbsolutePath} respondeu {veio}, esperado {(int)esperado}.");
    }

    return resposta;
}

// O token que o README e a CI ensinavam a montar antes dos tokens do Keycloak: HS256 com a chave de desenvolvimento
// publicada. Precisa levar 401 para sempre.
static string ReceitaHs256Antiga()
{
    long agora = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    string cabecalho = Base64Url("""{"alg":"HS256","typ":"JWT"}""");
    string payload = Base64Url(
        $$"""{"sub":"0199a000-0000-7000-8000-000000000001","roles":"platform-admin","iss":"identitygateway","aud":"identitygateway-api","nbf":{{agora}},"exp":{{agora + 3600}}}""");
    byte[] assinatura = HMACSHA256.HashData(
        Encoding.UTF8.GetBytes("chave-de-desenvolvimento-nao-use-em-producao"), Encoding.ASCII.GetBytes($"{cabecalho}.{payload}"));

    return $"{cabecalho}.{payload}.{System.Buffers.Text.Base64Url.EncodeToString(assinatura)}";

    static string Base64Url(string texto) => System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(texto));
}

// ───────────────────────────── o estado entre as fases ─────────────────────────────

void GravarEstado(Estado estado)
{
    File.WriteAllText(arquivoDeEstado, JsonSerializer.Serialize(estado));

    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(arquivoDeEstado, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

Estado LerEstado()
{
    if (!File.Exists(arquivoDeEstado))
    {
        throw new FalhaDoHarnessException(
            FamiliaDeFalha.Api, "estado", "o arquivo de estado não existe: a fase `jornada` precisa rodar antes.");
    }

    return JsonSerializer.Deserialize<Estado>(File.ReadAllText(arquivoDeEstado))
        ?? throw new FalhaDoHarnessException(FamiliaDeFalha.Api, "estado", "o arquivo de estado está vazio.");
}

// ───────────────────────────── a saída ─────────────────────────────

HarnessDeLogin NovoHarness() => new(keycloakPublico, keycloakTransporte, ClientDeDemonstracao, realm: Realm);

void Etapa(string nome)
{
    FecharEtapa();
    etapaAtual = nome;
    relogio.Restart();
}

void FecharEtapa()
{
    if (etapaAtual != "início")
    {
        etapas.Add((etapaAtual, relogio.Elapsed.TotalSeconds));
        Console.WriteLine($"ok  {relogio.Elapsed.TotalSeconds,6:0.0}s  {etapaAtual}");
    }
}

int Falhar(string motivo, int codigo)
{
    Console.WriteLine($"FALHOU  {motivo}");

    if (noGitHub)
    {
        Console.WriteLine($"::error title={etapaAtual}::{motivo}");
    }

    Resumir(falha: motivo);
    return codigo;
}

void Resumir(string? falha)
{
    if (falha is null)
    {
        FecharEtapa();
    }

    if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is not { Length: > 0 } resumo)
    {
        return;
    }

    StringBuilder tabela = new();
    tabela.AppendLine(CultureInfo.InvariantCulture, $"### jornada-compose {string.Join(' ', args)}");
    tabela.AppendLine();
    tabela.AppendLine("| Etapa | Tempo |");
    tabela.AppendLine("|---|---|");

    foreach ((string etapa, double segundos) in etapas)
    {
        tabela.AppendLine(CultureInfo.InvariantCulture, $"| {etapa} | {segundos:0.0} s |");
    }

    if (falha is not null)
    {
        tabela.AppendLine(CultureInfo.InvariantCulture, $"| **falhou** — {falha} | |");
    }

    File.AppendAllText(resumo, tabela.ToString());
}

// Registra o segredo no mascaramento do GitHub Actions — defesa a mais: o app nunca o imprime.
string Mascarar(string segredo)
{
    if (noGitHub)
    {
        Console.WriteLine($"::add-mask::{segredo}");
    }

    return segredo;
}

static string Ambiente(string nome, string padrao) =>
    Environment.GetEnvironmentVariable(nome) is { Length: > 0 } valor ? valor : padrao;

static int LerEsperado(string[] argumentos)
{
    int indice = Array.IndexOf(argumentos, "--esperado");

    return indice >= 0
           && indice + 1 < argumentos.Length
           && int.TryParse(argumentos[indice + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int esperado)
        ? esperado
        : throw new FalhaDoHarnessException(FamiliaDeFalha.Mailpit, "convites", "informe --esperado N.");
}

/// <summary>O que uma fase deixa para a seguinte. Credenciais de um Keycloak descartável.</summary>
internal sealed record Estado(string RefreshToken, string? AccessToken, string Localizacao, string? LocalizacaoPendente);

internal partial class Program
{
    [GeneratedRegex(@"\?[^\s""']+")]
    private static partial Regex SemQueryStrings();
}
