using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.Keycloak;
using Xunit;

namespace IdentityGateway.Testing.Keycloak;

/// <summary>
/// Um Keycloak 26.7.4 de verdade para o assembly inteiro, importando o MESMO realm que o compose usa, com um mailpit
/// na mesma rede recebendo o SMTP dele.
/// </summary>
/// <remarks>
/// <para>
/// <b>Um container por assembly de teste</b>, e não por classe: o Keycloak leva dezenas de segundos para subir, e o
/// xUnit v3 rodaria as classes em paralelo, cada uma com o seu. O <c>[assembly: AssemblyFixture]</c> fica em cada
/// projeto de teste que o usa — esta biblioteca não é um projeto de teste.
/// </para>
/// <para>
/// <b>O realm é o arquivo do repositório</b>, não uma cópia de teste: o teste prova o arquivo que o compose importa.
/// O certificado e o SMTP entram pelos mesmos placeholders, por variável de ambiente.
/// </para>
/// <para>
/// <b><c>KC_HOSTNAME</c> fixo e diferente do endereço discado</b> (<see cref="HostnamePublico"/>). Assim todo teste
/// de Keycloak exercita a separação do D8: o <c>aud</c> sai do <c>PublicBaseUrl</c>, e o transporte vai pela porta
/// mapeada. Um <c>aud</c> vindo do <c>BaseUrl</c> quebra todos eles com "Invalid token audience".
/// </para>
/// <para>
/// <b>Isolamento por dado único.</b> Os testes compartilham o realm e o mailpit; cada um cria as próprias
/// Organizations com slug aleatório e usa e-mails únicos com <c>+</c>, e nunca afirma sobre contagem global. O
/// mailpit é sempre filtrado pelo destinatário exato.
/// </para>
/// <para>
/// <b>Sem ROPC no realm da aplicação (ADR-003).</b> Token de usuário, aqui, só pelo harness de device flow. O
/// <c>grant_type=password</c> que resta é o do <c>admin-cli</c> do realm <b>master</b>, em
/// <see cref="CriarClienteMasterAsync"/>: é a infraestrutura do Testcontainers, fora do realm da aplicação.
/// </para>
/// </remarks>
public sealed class KeycloakFixture : IAsyncLifetime
{
    public const string Realm = "identity-gateway";

    /// <summary>O <c>KC_HOSTNAME</c> do container. Não resolve na máquina do teste — e não precisa: nunca é discado.</summary>
    public const string HostnamePublico = "http://keycloak.test:8081";

    /// <summary>A mesma tag do <c>docker-compose.yml</c> — um teste de arquitetura confere.</summary>
    public const string ImagemDoMailpit = "axllent/mailpit:v1.31.3";

    /// <summary>A mesma tag do <c>docker-compose.yml</c> — um teste de arquitetura confere.</summary>
    public const string ImagemDoKeycloak = "quay.io/keycloak/keycloak:26.7.4";

    /// <summary>
    /// O e-mail do platform-admin do bootstrap — o <c>${PLATFORM_ADMIN_EMAIL}</c> do realm.
    /// </summary>
    /// <remarks>
    /// Em minúsculas, como o import o grava. Este usuário serve à prova viva do realm importado; os testes que
    /// precisam de um platform-admin logado criam o seu, porque o link de ações é de uso único.
    /// </remarks>
    public const string EmailDoPlatformAdmin = "platform-admin@identity-gateway.test";

    /// <summary>O client público de demonstração do realm: só device flow, com os scopes da Gateway.</summary>
    public const string ClientDeDemonstracao = "identity-gateway-demo";

    /// <summary>
    /// Client público criado pelo fixture, em runtime, só com device flow, <b>herdando os scopes default do realm</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Existe para o teste da Account REST API, que exige <c>manage-account</c> e <c>aud=account</c> (do scope
    /// <c>roles</c>, um default do realm) — o que o client de demonstração não emite. Nunca é declarado no JSON.
    /// </para>
    /// <para>
    /// <b>Sem <c>defaultClientScopes</c> de propósito:</b> ele recebe exatamente o que um client qualquer criado pela
    /// Admin API receberia. O token dele NÃO traz a audiência da Gateway — e é isso que prova, no realm vivo, que
    /// <c>gateway-api</c> não é scope default. Com os scopes listados aqui, a prova seria vacuosa.
    /// </para>
    /// </remarks>
    public const string ClientDeConta = "fixture-conta-dispositivo";

    private const int PortaDoMailpit = 8025;

    private static readonly string[] AcoesDoConvite = ["UPDATE_PASSWORD", "VERIFY_EMAIL"];

    private static readonly string[] SoPlatformAdmin = ["platform-admin"];

    private ClienteDoMailpit? _mailpitCliente;

    private readonly INetwork _rede;
    private readonly IContainer _mailpit;
    private readonly KeycloakContainer _container;

    public KeycloakFixture()
    {
        Chaves = ChavesDeTeste.Gerar();

        _rede = new NetworkBuilder().Build();

        _mailpit = new ContainerBuilder(ImagemDoMailpit)
            .WithNetwork(_rede)
            .WithNetworkAliases("mailpit")
            .WithPortBinding(PortaDoMailpit, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(pedido =>
                pedido.ForPort(PortaDoMailpit).ForPath("/readyz")))
            .Build();

        _container = new KeycloakBuilder(ImagemDoKeycloak)
            .WithNetwork(_rede)
            .WithRealm(CaminhoDoRealm())
            .WithEnvironment("GATEWAY_CLIENT_CERT", Chaves.CertificadoBase64)
            .WithEnvironment("KC_HOSTNAME", HostnamePublico)
            .WithEnvironment("KC_HOSTNAME_BACKCHANNEL_DYNAMIC", "true")
            // O SMTP_* é o do mailpit acima, pelo alias na rede. Além de entregar o convite, ele precisa existir já no
            // import: o Keycloak recusa placeholder literal no remetente ("Invalid sender address").
            .WithEnvironment("SMTP_HOST", "mailpit")
            .WithEnvironment("SMTP_PORT", "1025")
            .WithEnvironment("SMTP_FROM", "convites@identity-gateway.test")
            .WithEnvironment("PLATFORM_ADMIN_EMAIL", EmailDoPlatformAdmin)
            .Build();
    }

    /// <summary>O par de chaves da Gateway registrado no realm. Quem compõe a Gateway contra este Keycloak usa o PEM.</summary>
    public ParDeChaves Chaves { get; }

    public string BaseUrl => _container.GetBaseAddress().TrimEnd('/');

    /// <summary>Interface e API do mailpit, pela porta mapeada.</summary>
    public string MailpitUrl => $"http://{_mailpit.Hostname}:{_mailpit.GetMappedPublicPort(PortaDoMailpit)}";

    /// <summary>O cliente da API do mailpit deste fixture.</summary>
    public ClienteDoMailpit Mailpit => _mailpitCliente ??= new ClienteDoMailpit(new Uri(MailpitUrl));

    public async ValueTask InitializeAsync()
    {
        await _rede.CreateAsync();
        await Task.WhenAll(_mailpit.StartAsync(), _container.StartAsync());
        await CriarClientDeContaAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _mailpitCliente?.Dispose();
        await _container.DisposeAsync();
        await _mailpit.DisposeAsync();
        await _rede.DisposeAsync();
    }

    /// <summary>E-mail único com <c>+</c>: exercita o escape da query em todo teste e evita o conflito do D5.</summary>
    public static string EmailUnico() => $"admin+{Guid.NewGuid():N}@acme.test";

    /// <summary>E-mail único de exatamente 254 caracteres: parte local de 64, domínio em rótulos de até 63.</summary>
    public static string EmailUnicoDe254Caracteres() =>
        $"admin+{Guid.NewGuid():N}{new string('a', 26)}@{new string('b', 63)}.{new string('c', 63)}."
        + $"{new string('d', 56)}.test";

    /// <summary>Cliente HTTP autenticado como admin do realm master — para preparar e conferir estado.</summary>
    public async Task<HttpClient> CriarClienteMasterAsync(CancellationToken cancellationToken)
    {
        HttpClient http = new() { BaseAddress = new Uri($"{BaseUrl}/") };

        using FormUrlEncodedContent corpo = new(
        [
            new("grant_type", "password"),
            new("client_id", "admin-cli"),
            new("username", KeycloakBuilder.DefaultUsername),
            new("password", KeycloakBuilder.DefaultPassword),
        ]);

        using HttpResponseMessage resposta = await http.PostAsync(
            new Uri("realms/master/protocol/openid-connect/token", UriKind.Relative), corpo, cancellationToken);
        resposta.EnsureSuccessStatusCode();

        using var token = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync(cancellationToken));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", token.RootElement.GetProperty("access_token").GetString());

        return http;
    }

    /// <summary>
    /// Lê um recurso do realm pela Admin API, como admin do master — o JSON cru, nunca um DTO de quem está sob teste.
    /// </summary>
    /// <param name="caminho">Relativo a <c>admin/realms/identity-gateway/</c>; vazio lê o próprio realm.</param>
    public async Task<JsonElement> LerComoMasterAsync(string caminho, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        string json = await master.GetStringAsync(
            new Uri($"admin/realms/{Realm}/{caminho}".TrimEnd('/'), UriKind.Relative), cancellationToken);

        using var documento = JsonDocument.Parse(json);
        return documento.RootElement.Clone();
    }

    /// <summary>Cria uma Organization por fora da Gateway, como o master faria.</summary>
    public async Task<string> CriarOrganizacaoComoMasterAsync(
        string alias, string? tenantId, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);

        Dictionary<string, object> organizacao = new()
        {
            ["name"] = alias,
            ["alias"] = alias,
            ["enabled"] = true,
        };

        if (tenantId is not null)
        {
            organizacao["attributes"] = new Dictionary<string, string[]> { ["gateway_tenant_id"] = [tenantId] };
        }

        using HttpResponseMessage resposta = await master.PostAsJsonAsync(
            $"admin/realms/{Realm}/organizations", organizacao, cancellationToken);
        resposta.EnsureSuccessStatusCode();

        return resposta.Headers.Location!.Segments[^1];
    }

    /// <summary>A Organization em JSON cru, lida pelo master — nunca pelo DTO do adaptador sob teste.</summary>
    public async Task<JsonElement> LerOrganizacaoCruaAsync(string id, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        string json = await master.GetStringAsync(
            new Uri($"admin/realms/{Realm}/organizations/{id}", UriKind.Relative), cancellationToken);

        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>Quantas Organizations têm o alias — pela chave especial <c>alias</c> do <c>q</c>.</summary>
    public async Task<int> ContarPorAliasAsync(string alias, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        string json = await master.GetStringAsync(
            new Uri($"admin/realms/{Realm}/organizations?q={Uri.EscapeDataString($"alias:{alias}")}&max=10",
                UriKind.Relative),
            cancellationToken);

        using var lista = JsonDocument.Parse(json);
        return lista.RootElement.GetArrayLength();
    }

    /// <summary>O usuário em JSON cru, lido pelo master — nunca pelo DTO do adaptador sob teste.</summary>
    public async Task<JsonElement> LerUsuarioCruAsync(string id, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        string json = await master.GetStringAsync(
            new Uri($"admin/realms/{Realm}/users/{id}", UriKind.Relative), cancellationToken);

        using var usuario = JsonDocument.Parse(json);
        return usuario.RootElement.Clone();
    }

    /// <summary>Os usuários com o e-mail exato, lidos pelo master.</summary>
    public async Task<IReadOnlyList<JsonElement>> UsuariosPorEmailAsync(string email, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        string json = await master.GetStringAsync(
            new Uri($"admin/realms/{Realm}/users?email={Uri.EscapeDataString(email)}&exact=true", UriKind.Relative),
            cancellationToken);

        using var lista = JsonDocument.Parse(json);
        return [.. lista.RootElement.EnumerateArray().Select(usuario => usuario.Clone())];
    }

    /// <summary>Cria um usuário por fora da Gateway e devolve o id.</summary>
    public async Task<string> CriarUsuarioComoMasterAsync(object usuario, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        using HttpResponseMessage resposta = await master.PostAsJsonAsync(
            $"admin/realms/{Realm}/users", usuario, cancellationToken);
        resposta.EnsureSuccessStatusCode();

        return resposta.Headers.Location!.Segments[^1];
    }

    public async Task DesabilitarUsuarioComoMasterAsync(string id, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        using HttpResponseMessage resposta = await master.PutAsJsonAsync(
            $"admin/realms/{Realm}/users/{id}", new { enabled = false }, cancellationToken);
        resposta.EnsureSuccessStatusCode();
    }

    /// <summary>Os ids dos membros da Organization, lidos pelo master.</summary>
    public async Task<IReadOnlyList<string>> MembrosDaOrganizacaoAsync(string organizacao, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        string json = await master.GetStringAsync(
            new Uri($"admin/realms/{Realm}/organizations/{organizacao}/members", UriKind.Relative), cancellationToken);

        using var lista = JsonDocument.Parse(json);
        return [.. lista.RootElement.EnumerateArray().Select(membro => membro.GetProperty("id").GetString()!)];
    }

    /// <summary>Os papéis de realm atribuídos diretamente ao usuário, lidos pelo master.</summary>
    public async Task<IReadOnlyList<string>> PapeisDeRealmDoUsuarioAsync(string id, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        string json = await master.GetStringAsync(
            new Uri($"admin/realms/{Realm}/users/{id}/role-mappings/realm", UriKind.Relative), cancellationToken);

        using var lista = JsonDocument.Parse(json);
        return [.. lista.RootElement.EnumerateArray().Select(papel => papel.GetProperty("name").GetString()!)];
    }

    /// <summary>Um harness de login (uma sessão de navegador nova) apontado para este Keycloak.</summary>
    public HarnessDeLogin CriarHarness(string clientId = ClientDeDemonstracao, Action<string>? log = null) =>
        new(new Uri(HostnamePublico), new Uri(BaseUrl), clientId, log, Realm);

    /// <summary>
    /// Cria um usuário pelo master, com os papéis e o tenant pedidos, e conclui o convite dele pelo link do e-mail.
    /// </summary>
    /// <remarks>
    /// O caminho é o do convite real: o usuário nasce sem senha, com as duas ações obrigatórias; o Keycloak manda o
    /// e-mail; o harness segue o link e define uma senha gerada. Nenhuma senha é atribuída pela Admin API.
    /// </remarks>
    public async Task<UsuarioDeTeste> NovoUsuarioAsync(
        IReadOnlyList<string> papeis, string? tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(papeis);

        string email = EmailUnico();
        string senha = SenhasDeTeste.Gerar();

        Dictionary<string, object> usuario = new()
        {
            ["username"] = email,
            ["email"] = email,
            ["enabled"] = true,
            ["emailVerified"] = false,
            ["requiredActions"] = AcoesDoConvite,
        };

        if (tenantId is not null)
        {
            usuario["attributes"] = new Dictionary<string, string[]> { ["tenant_id"] = [tenantId] };
        }

        string id = await CriarUsuarioComoMasterAsync(usuario, cancellationToken);

        foreach (string papel in papeis)
        {
            await AtribuirPapelDeRealmComoMasterAsync(id, papel, cancellationToken);
        }

        await EnviarEmailDeAcoesComoMasterAsync(id, cancellationToken);

        using HarnessDeLogin harness = CriarHarness();
        await harness.ConcluirLinkDeAcoesAsync(await LinkDoConviteAsync(email, cancellationToken), senha, cancellationToken);

        return new UsuarioDeTeste(id, email, senha);
    }

    /// <summary>Um platform-admin próprio do teste, com o convite já concluído.</summary>
    /// <remarks>
    /// <b>Um por teste.</b> No Testcontainers não há one-shot, e o link de ações é de uso único: com um platform-admin
    /// só, dois testes paralelos disputariam o link, e o segundo receberia "Action expired". O platform-admin do JSON
    /// (<see cref="EmailDoPlatformAdmin"/>) fica para a prova do realm importado.
    /// </remarks>
    public Task<UsuarioDeTeste> NovoPlatformAdminAsync(CancellationToken cancellationToken) =>
        NovoUsuarioAsync(SoPlatformAdmin, tenantId: null, cancellationToken);

    /// <summary>Atribui um papel de realm ao usuário, como o master.</summary>
    public async Task AtribuirPapelDeRealmComoMasterAsync(
        string usuarioId, string papel, CancellationToken cancellationToken)
    {
        JsonElement representacao = await LerComoMasterAsync($"roles/{papel}", cancellationToken);

        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        using HttpResponseMessage resposta = await master.PostAsJsonAsync(
            $"admin/realms/{Realm}/users/{usuarioId}/role-mappings/realm",
            new[] { new { id = representacao.GetProperty("id").GetString(), name = papel } },
            cancellationToken);
        resposta.EnsureSuccessStatusCode();
    }

    /// <summary>Dispara o e-mail de ações do usuário, como o master, com um link de 10 minutos.</summary>
    public async Task EnviarEmailDeAcoesComoMasterAsync(string usuarioId, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        using HttpResponseMessage resposta = await master.PutAsJsonAsync(
            $"admin/realms/{Realm}/users/{usuarioId}/execute-actions-email?lifespan=600",
            AcoesDoConvite,
            cancellationToken);
        resposta.EnsureSuccessStatusCode();
    }

    // Uma vez por fixture: o ROPC que existia aqui criava um client por chamada, com direct grant, e não o removia.
    private async Task CriarClientDeContaAsync(CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        using HttpResponseMessage resposta = await master.PostAsJsonAsync(
            $"admin/realms/{Realm}/clients",
            new
            {
                clientId = ClientDeConta,
                publicClient = true,
                standardFlowEnabled = false,
                implicitFlowEnabled = false,
                directAccessGrantsEnabled = false,
                serviceAccountsEnabled = false,
                fullScopeAllowed = true,
                attributes = new Dictionary<string, string> { ["oauth2.device.authorization.grant.enabled"] = "true" },
            },
            cancellationToken);
        resposta.EnsureSuccessStatusCode();
    }

    /// <summary>Os ids das mensagens do mailpit cujo destinatário é exatamente o informado.</summary>
    /// <remarks>
    /// A busca <c>to:</c> do mailpit casa por trecho — <c>x@acme.test</c> acharia <c>pre.x@acme.test</c>. O filtro
    /// exato é feito no cliente do mailpit, sobre <c>To[].Address</c>.
    /// </remarks>
    public Task<IReadOnlyList<string>> MensagensParaAsync(string destinatario, CancellationToken cancellationToken) =>
        Mailpit.MensagensParaAsync(destinatario, cancellationToken);

    /// <summary>Espera até haver ao menos <paramref name="quantidade"/> mensagens para o destinatário (até 5 s).</summary>
    /// <remarks>
    /// O Keycloak envia dentro da requisição de <c>execute-actions-email</c>, então o e-mail já deveria estar lá quando
    /// a chamada volta; a espera curta só absorve a gravação do mailpit.
    /// </remarks>
    public Task<IReadOnlyList<string>> EsperarMensagensAsync(
        string destinatario, int quantidade, CancellationToken cancellationToken) =>
        Mailpit.EsperarMensagensAsync(destinatario, quantidade, cancellationToken);

    /// <summary>O corpo em texto da mensagem (o HTML traz o <c>&amp;</c> do link escapado).</summary>
    public Task<string> TextoDaMensagemAsync(string id, CancellationToken cancellationToken) =>
        Mailpit.TextoDaMensagemAsync(id, cancellationToken);

    /// <summary>O link de ações do convite mais recente para o destinatário, com o host público.</summary>
    /// <exception cref="FalhaDoHarnessException">Não há mensagem, ou ela não traz o link no endereço público.</exception>
    public Task<Uri> LinkDoConviteAsync(string destinatario, CancellationToken cancellationToken) =>
        Mailpit.LinkDeAcoesAsync(destinatario, new Uri(HostnamePublico), Realm, cancellationToken);

    private static string CaminhoDoRealm() =>
        RaizDoRepositorio.Caminho("keycloak", "bootstrap", "realm-identity-gateway.json");
}
