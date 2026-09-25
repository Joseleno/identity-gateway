using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.Keycloak;

[assembly: AssemblyFixture(typeof(KeycloakFixture))]

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// Um Keycloak 26.7.4 de verdade para o assembly inteiro, importando o MESMO realm que o compose usa.
/// </summary>
/// <remarks>
/// <para>
/// <b>Um container por assembly</b>, e não por classe: o Keycloak leva dezenas de segundos para subir, e o xUnit v3
/// rodaria as classes em paralelo, cada uma com o seu.
/// </para>
/// <para>
/// <b>O realm é o arquivo do repositório</b>, não uma cópia de teste: o teste prova o arquivo que o compose importa.
/// O certificado entra pelo mesmo placeholder, por variável de ambiente.
/// </para>
/// <para>
/// <b>Isolamento por slug único.</b> Os testes compartilham o realm; cada um cria as próprias Organizations com slug
/// aleatório e nunca afirma sobre a contagem global.
/// </para>
/// </remarks>
public sealed class KeycloakFixture : IAsyncLifetime
{
    public const string Realm = "identity-gateway";

    private readonly KeycloakContainer _container;

    static KeycloakFixture()
    {
        // Sem isso, dois testes que assinam com a MESMA chave (o Pem padrão de ChavesDeTeste) colidem no cache
        // estático de SignatureProvider do Microsoft.IdentityModel.Tokens: a chave não tem KeyId (exigência da
        // Task 2), então o cache identifica pelo material da chave, não pelo objeto RSA. Quando o primeiro teste
        // termina e descarta o próprio ServiceProvider — e com ele o RSA que assinou —, o cache continua
        // apontando para aquele RSA já descartado, e o próximo teste com a mesma chave recebe
        // ObjectDisposedException ao assinar. Em produção isso nunca ocorre: há um único ServiceProvider vivo
        // por todo o processo, então a chave cacheada nunca é descartada enquanto em uso.
        CryptoProviderFactory.Default.CacheSignatureProviders = false;
    }

    public KeycloakFixture()
    {
        Chaves = ChavesDeTeste.Gerar();

        _container = new KeycloakBuilder("quay.io/keycloak/keycloak:26.7.4")
            .WithRealm(CaminhoDoRealm())
            .WithEnvironment("GATEWAY_CLIENT_CERT", Chaves.CertificadoBase64)
            .Build();
    }

    // Internal, não public: ParDeChaves é internal (ChavesDeTeste.cs), e uma propriedade não pode ser mais
    // acessível que o próprio tipo. Nenhum teste precisa da chave por fora — só CriarProvider a usa como padrão.
    internal ParDeChaves Chaves { get; }

    public string BaseUrl => _container.GetBaseAddress().TrimEnd('/');

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    /// <summary>Slug aleatório, válido e curto — o isolamento entre testes.</summary>
    public static TenantSlug SlugUnico() => TenantSlug.Create($"t-{Guid.NewGuid():N}"[..18]).Value;

    /// <summary>
    /// A composição real (<c>AddInfrastructure</c>) apontada para este Keycloak, com handlers de teste opcionais
    /// acrescentados antes de construir.
    /// </summary>
    public ServiceProvider CriarProvider(Action<IServiceCollection>? ajustar = null, string? pem = null)
    {
        ServiceCollection services = KeycloakHealthCheckTests.ColecaoDaComposicao(BaseUrl, pem ?? Chaves.PemPrivado);
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

    private static string CaminhoDoRealm()
    {
        DirectoryInfo? pasta = new(AppContext.BaseDirectory);

        while (pasta is not null && !File.Exists(Path.Combine(pasta.FullName, "IdentityGateway.slnx")))
        {
            pasta = pasta.Parent;
        }

        return Path.Combine(
            pasta?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada."),
            "keycloak", "bootstrap", "realm-identity-gateway.json");
    }
}
