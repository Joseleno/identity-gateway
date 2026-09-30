using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.Keycloak;

[assembly: AssemblyFixture(typeof(KeycloakFixture))]

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// Um Keycloak 26.7.4 de verdade para o assembly inteiro, importando o MESMO realm que o compose usa, com um mailpit
/// na mesma rede recebendo o SMTP dele.
/// </summary>
/// <remarks>
/// <para>
/// <b>Um container por assembly</b>, e não por classe: o Keycloak leva dezenas de segundos para subir, e o xUnit v3
/// rodaria as classes em paralelo, cada uma com o seu.
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
/// </remarks>
public sealed partial class KeycloakFixture : IAsyncLifetime
{
    public const string Realm = "identity-gateway";

    /// <summary>O <c>KC_HOSTNAME</c> do container. Não resolve na máquina do teste — e não precisa: nunca é discado.</summary>
    public const string HostnamePublico = "http://keycloak.test:8081";

    /// <summary>A mesma tag do <c>docker-compose.yml</c> — um teste de arquitetura confere.</summary>
    public const string ImagemDoMailpit = "axllent/mailpit:v1.31.3";

    private const int PortaDoMailpit = 8025;

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

        _container = new KeycloakBuilder("quay.io/keycloak/keycloak:26.7.4")
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
            .Build();
    }

    // Internal, não public: ParDeChaves é internal (ChavesDeTeste.cs), e uma propriedade não pode ser mais
    // acessível que o próprio tipo. Nenhum teste precisa da chave por fora — só CriarProvider a usa como padrão.
    internal ParDeChaves Chaves { get; }

    public string BaseUrl => _container.GetBaseAddress().TrimEnd('/');

    /// <summary>Interface e API do mailpit, pela porta mapeada.</summary>
    public string MailpitUrl => $"http://{_mailpit.Hostname}:{_mailpit.GetMappedPublicPort(PortaDoMailpit)}";

    public async ValueTask InitializeAsync()
    {
        await _rede.CreateAsync();
        await Task.WhenAll(_mailpit.StartAsync(), _container.StartAsync());
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        await _mailpit.DisposeAsync();
        await _rede.DisposeAsync();
    }

    /// <summary>Slug aleatório, válido e curto — o isolamento entre testes.</summary>
    public static TenantSlug SlugUnico() => TenantSlug.Create($"t-{Guid.NewGuid():N}"[..18]).Value;

    /// <summary>E-mail único com <c>+</c>: exercita o escape da query em todo teste e evita o conflito do D5.</summary>
    public static string EmailUnico() => $"admin+{Guid.NewGuid():N}@acme.test";

    /// <summary>E-mail único de exatamente 254 caracteres: parte local de 64, domínio em rótulos de até 63.</summary>
    public static string EmailUnicoDe254Caracteres() =>
        $"admin+{Guid.NewGuid():N}{new string('a', 26)}@{new string('b', 63)}.{new string('c', 63)}."
        + $"{new string('d', 56)}.test";

    /// <summary>
    /// A composição real (<c>AddInfrastructure</c>) apontada para este Keycloak, com handlers de teste opcionais
    /// acrescentados antes de construir.
    /// </summary>
    public ServiceProvider CriarProvider(Action<IServiceCollection>? ajustar = null, string? pem = null)
    {
        ServiceCollection services = KeycloakHealthCheckTests.ColecaoDaComposicao(
            BaseUrl, pem ?? Chaves.PemPrivado, HostnamePublico);
        ajustar?.Invoke(services);

        return services.BuildServiceProvider(validateScopes: true);
    }

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

    /// <summary>
    /// Token de um usuário comum do realm, por senha, num client de teste criado para isso.
    /// </summary>
    /// <remarks>
    /// Um client próprio, público e com direct grant: o <c>admin-cli</c> do realm não tem <c>fullScopeAllowed</c>, e o
    /// token dele não traria os papéis do client <c>account</c> que a Account REST API exige.
    /// </remarks>
    public async Task<string> TokenDeUsuarioComumAsync(string username, string senha, CancellationToken cancellationToken)
    {
        string clientId = $"teste-conta-{Guid.NewGuid():N}"[..24];

        using (HttpClient master = await CriarClienteMasterAsync(cancellationToken))
        {
            using HttpResponseMessage criado = await master.PostAsJsonAsync(
                $"admin/realms/{Realm}/clients",
                new
                {
                    clientId,
                    publicClient = true,
                    directAccessGrantsEnabled = true,
                    standardFlowEnabled = false,
                    fullScopeAllowed = true,
                },
                cancellationToken);
            criado.EnsureSuccessStatusCode();
        }

        using HttpClient http = new() { BaseAddress = new Uri($"{BaseUrl}/") };
        using FormUrlEncodedContent corpo = new(
        [
            new("grant_type", "password"),
            new("client_id", clientId),
            new("username", username),
            new("password", senha),
        ]);

        using HttpResponseMessage resposta = await http.PostAsync(
            new Uri($"realms/{Realm}/protocol/openid-connect/token", UriKind.Relative), corpo, cancellationToken);
        resposta.EnsureSuccessStatusCode();

        using var token = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync(cancellationToken));
        return token.RootElement.GetProperty("access_token").GetString()!;
    }

    /// <summary>Os ids das mensagens do mailpit cujo destinatário é exatamente o informado.</summary>
    /// <remarks>
    /// A busca <c>to:</c> do mailpit casa por trecho — <c>x@acme.test</c> acharia <c>pre.x@acme.test</c>. O filtro
    /// exato é feito aqui, sobre <c>To[].Address</c>.
    /// </remarks>
    public async Task<IReadOnlyList<string>> MensagensParaAsync(string destinatario, CancellationToken cancellationToken)
    {
        using HttpClient http = new() { BaseAddress = new Uri($"{MailpitUrl}/") };
        string consulta = Uri.EscapeDataString($"to:\"{destinatario}\"");

        using var busca = JsonDocument.Parse(await http.GetStringAsync(
            new Uri($"api/v1/search?query={consulta}", UriKind.Relative), cancellationToken));

        return
        [
            .. busca.RootElement.GetProperty("messages").EnumerateArray()
                .Where(mensagem => mensagem.GetProperty("To").EnumerateArray().Any(para =>
                    string.Equals(para.GetProperty("Address").GetString(), destinatario, StringComparison.OrdinalIgnoreCase)))
                .Select(mensagem => mensagem.GetProperty("ID").GetString()!),
        ];
    }

    /// <summary>Espera até haver ao menos <paramref name="quantidade"/> mensagens para o destinatário (até 5 s).</summary>
    /// <remarks>
    /// O Keycloak envia dentro da requisição de <c>execute-actions-email</c>, então o e-mail já deveria estar lá quando
    /// a chamada volta; a espera curta só absorve a gravação do mailpit.
    /// </remarks>
    public async Task<IReadOnlyList<string>> EsperarMensagensAsync(
        string destinatario, int quantidade, CancellationToken cancellationToken)
    {
        for (int tentativa = 0; tentativa < 20; tentativa++)
        {
            IReadOnlyList<string> ids = await MensagensParaAsync(destinatario, cancellationToken);

            if (ids.Count >= quantidade)
            {
                return ids;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        return await MensagensParaAsync(destinatario, cancellationToken);
    }

    /// <summary>O corpo em texto da mensagem (o HTML traz o <c>&amp;</c> do link escapado).</summary>
    public async Task<string> TextoDaMensagemAsync(string id, CancellationToken cancellationToken)
    {
        using HttpClient http = new() { BaseAddress = new Uri($"{MailpitUrl}/") };

        using var mensagem = JsonDocument.Parse(await http.GetStringAsync(
            new Uri($"api/v1/message/{id}", UriKind.Relative), cancellationToken));

        return mensagem.RootElement.GetProperty("Text").GetString()!;
    }

    /// <summary>O link de ações do primeiro convite para o destinatário, com o host público.</summary>
    public async Task<Uri> LinkDoConviteAsync(string destinatario, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> ids = await EsperarMensagensAsync(destinatario, 1, cancellationToken);
        ids.Should().NotBeEmpty("o convite precisa estar no mailpit");

        Match link = LinkDeAcoes().Match(await TextoDaMensagemAsync(ids[0], cancellationToken));
        link.Success.Should().BeTrue("o e-mail precisa trazer o link com o endereço público (KC_HOSTNAME)");

        return new Uri(link.Value);
    }

    [GeneratedRegex(@"http://keycloak\.test:8081/realms/identity-gateway/login-actions/action-token\?key=\S+")]
    private static partial Regex LinkDeAcoes();

    private static string CaminhoDoRealm() =>
        RaizDoRepositorio.Caminho("keycloak", "bootstrap", "realm-identity-gateway.json");
}
