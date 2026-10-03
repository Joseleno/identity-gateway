using System.Net;
using System.Security.Cryptography;
using System.Text;
using IdentityGateway.Api.FunctionalTests.Oidc;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// A suíte negativa da autenticação: cada token que a Api precisa recusar, nas rotas protegidas que existem.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tokens forjados com a chave de teste</b>, que a Api conhece pelo JWKS do OIDC falso: a assinatura confere, e o
/// que reprova é exatamente o defeito do caso. Os casos de assinatura usam outra chave, de propósito.
/// </para>
/// <para>
/// <b>O discovery do OIDC falso anuncia um emissor diferente do configurado.</b> É o que faz o caso "iss igual ao do
/// discovery" distinguir o <c>IssuerValidator</c> estrito da validação padrão da biblioteca, que aceitaria esse emissor.
/// </para>
/// <para>
/// <b>Todo <c>401</c> é igual por fora:</b> <c>WWW-Authenticate: Bearer</c>, sem <c>error</c> nem
/// <c>error_description</c>, e o mesmo Problem Details. Quem chama não aprende por que foi recusado.
/// </para>
/// <para>
/// O limitador de requisições não entra na conta: ele roda depois da autorização, e um <c>401</c> não consome cota.
/// </para>
/// </remarks>
public sealed class AutenticacaoNegativaTests(IdentityGatewayApiFactory factory)
    : IClassFixture<IdentityGatewayApiFactory>
{
    private const string Emissor = IdentityGatewayApiFactory.EmissorPublico;

    private static readonly string[] PlatformAdmin = ["platform-admin"];

    private static readonly string[] AudienciaDaGatewayEOutra = ["identity-gateway-api", "account"];

    private static readonly string[] AzpEmArrayDeUm = ["identity-gateway-demo"];

    private static readonly string[] AzpEmArrayDeDois = ["identity-gateway-demo", "outro-client"];

    private static readonly string[] TypEmArray = ["Bearer"];

    // Outra chave RSA, que a Api não conhece. Estática: gerar 2048 bits por caso custaria mais que o teste.
    private static readonly RSA ChaveForasteira = RSA.Create(2048);

    private static long Agora => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static TheoryDataRow<Func<IdentityGatewayApiFactory, string>> Caso(
        string rotulo, Func<IdentityGatewayApiFactory, string> token) => new(token) { Label = rotulo };

    /// <summary>Um token de platform-admin válido em tudo, menos no que <paramref name="defeito"/> estragar.</summary>
    private static TheoryDataRow<Func<IdentityGatewayApiFactory, string>> Com(
        string rotulo, Action<Dictionary<string, object?>> defeito) =>
        Caso(rotulo, alvo => alvo.Emissor.Emitir(roles: PlatformAdmin, ajustar: defeito));

    /// <summary>A receita do README e da CI de antes desta fatia: HS256 com a chave de desenvolvimento publicada.</summary>
    private static string ReceitaHs256Antiga()
    {
        Dictionary<string, object?> payload = new()
        {
            ["sub"] = "0199a000-0000-7000-8000-000000000001",
            ["roles"] = "platform-admin",
            ["iss"] = "identitygateway",
            ["aud"] = "identitygateway-api",
            ["nbf"] = Agora,
            ["exp"] = Agora + 3600,
        };

        return EmissorDeTeste.AssinarComHs256(
            payload, Encoding.UTF8.GetBytes("chave-de-desenvolvimento-nao-use-em-producao"));
    }

    public static TheoryData<Func<IdentityGatewayApiFactory, string>> RecusadosPelaValidacao => new()
    {
        Caso("token malformado", _ => "nao-e-um-jwt"),
        Caso("Bearer vazio", _ => string.Empty),

        // 2 min: acima dos 30 s de tolerância, abaixo dos 5 min do padrão da biblioteca.
        Com("vencido há 2 min", payload =>
        {
            payload["exp"] = Agora - 120;
            payload["iat"] = Agora - 420;
        }),
        Com("nbf 2 min no futuro", payload => payload["nbf"] = Agora + 120),
        Com("sem exp", payload => payload.Remove("exp")),

        Com("aud = account", payload => payload["aud"] = "account"),
        Com("sem aud", payload => payload.Remove("aud")),

        Com("iss forasteiro", payload => payload["iss"] = "https://atacante.test/realms/identity-gateway"),
        Caso("iss igual ao do discovery, diferente do configurado", alvo => alvo.Emissor.Emitir(
            roles: PlatformAdmin, ajustar: payload => payload["iss"] = alvo.Oidc.EmissorAnunciado)),
        Com("iss com barra final", payload => payload["iss"] = Emissor + "/"),
        Com("iss com sufixo", payload => payload["iss"] = Emissor + "-outro"),
        Com("iss em maiúsculas", payload => payload["iss"] = Emissor.ToUpperInvariant()),

        Caso("outra chave RSA com o mesmo kid", alvo => alvo.Emissor.Assinar(
            alvo.Emissor.Payload(roles: PlatformAdmin), ChaveForasteira)),
        Caso("alg = none", alvo => EmissorDeTeste.SemAssinatura(alvo.Emissor.Payload(roles: PlatformAdmin))),
        Caso("HS256 com a chave pública como segredo", alvo => EmissorDeTeste.AssinarComHs256(
            alvo.Emissor.Payload(roles: PlatformAdmin), alvo.Emissor.ChavePublicaEmPem(), EmissorDeTeste.Kid)),
        Caso("a receita HS256 antiga", _ => ReceitaHs256Antiga()),
    };

    public static TheoryData<Func<IdentityGatewayApiFactory, string>> RecusadosPelaForma => new()
    {
        // O cabeçalho de um ID token também diz JWT; quem distingue é o claim typ.
        Com("typ = ID com a audiência certa", payload => payload["typ"] = "ID"),
        Com("typ em array", payload => payload["typ"] = TypEmArray),
        Com("sem typ", payload => payload.Remove("typ")),

        Com("azp fora da lista", payload => payload["azp"] = "outro-client"),
        Com("azp ausente", payload => payload.Remove("azp")),
        Com("azp vazio", payload => payload["azp"] = string.Empty),
        Com("azp em array de um", payload => payload["azp"] = AzpEmArrayDeUm),
        Com("azp em array de dois", payload => payload["azp"] = AzpEmArrayDeDois),
        Com("azp numérico", payload => payload["azp"] = 42),

        Com("sem sub", payload => payload.Remove("sub")),
        Com("sub que não é GUID", payload => payload["sub"] = "joao"),
        Com("sub no formato N", payload => payload["sub"] = Guid.NewGuid().ToString("N")),

        // 36 caracteres e aceitos pelo parse do formato D, que tolera prefixo 0x e sinal em cada componente.
        Com("sub com prefixo 0x", payload => payload["sub"] = "0x99a000-0000-7000-8000-00000000000a"),
        Com("sub com sinal", payload => payload["sub"] = "+199a000-0000-7000-8000-00000000000a"),
        Com("sub em array", payload => payload["sub"] = new[] { Guid.NewGuid().ToString() }),

        // Foco de revisão 4: com o detalhe do erro ligado, o iss iria para o WWW-Authenticate, o Kestrel recusaria o
        // cabeçalho e o 401 viraria 500.
        Com("iss com caractere de controle", payload => payload["iss"] = Emissor + "\r\nX-Injetado: 1"),
    };

    public static TheoryData<Func<IdentityGatewayApiFactory, string>> Aceitos => new()
    {
        Com("token padrão do platform-admin", _ => { }),
        Com("aud em array com a da Gateway e outra", payload => payload["aud"] = AudienciaDaGatewayEOutra),
        Com("vencido há 10 s (dentro da tolerância de 30 s)", payload => payload["exp"] = Agora - 10),
    };

    private async Task<HttpResponseMessage> EnviarAsync(HttpMethod metodo, string rota, string token, CancellationToken ct)
    {
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage pedido = new(metodo, rota);

        // Sem validação do cabeçalho: os casos incluem valores que o HttpClient recusaria formatar.
        pedido.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

        return await client.SendAsync(pedido, ct);
    }

    private static string RotaDeConsulta() => $"/api/v1/tenants/{Guid.NewGuid()}/provisioning";

    private async Task ConferirRecusaAsync(Func<IdentityGatewayApiFactory, string> token, CancellationToken ct)
    {
        (HttpMethod Metodo, string Rota)[] rotas =
        [
            (HttpMethod.Post, "/api/v1/tenants"),
            (HttpMethod.Get, RotaDeConsulta()),
            (HttpMethod.Get, $"/api/v1/tenants/{Guid.NewGuid()}"),
        ];

        foreach ((HttpMethod metodo, string rota) in rotas)
        {
            using HttpResponseMessage resposta = await EnviarAsync(metodo, rota, token(factory), ct);

            // Numa variável: sem corpo o ContentType é nulo, e um `?.` encadeado até o Should() pularia a asserção.
            string? tipoDoCorpo = resposta.Content.Headers.ContentType?.MediaType;

            resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{metodo} {rota}");
            resposta.Headers.WwwAuthenticate.ToString().Should().Be("Bearer", "o motivo da recusa fica no log, não na resposta");
            tipoDoCorpo.Should().Be("application/problem+json");
        }
    }

    [Theory]
    [MemberData(nameof(RecusadosPelaValidacao))]
    public async Task TokenQueAValidacaoRecusa_Responde401SemDetalheNasRotasProtegidas(Func<IdentityGatewayApiFactory, string> token)
    {
        ArgumentNullException.ThrowIfNull(token);

        await ConferirRecusaAsync(token, TestContext.Current.CancellationToken);
    }

    [Theory]
    [MemberData(nameof(RecusadosPelaForma))]
    public async Task TokenComAFormaErrada_Responde401SemDetalheNasRotasProtegidas(Func<IdentityGatewayApiFactory, string> token)
    {
        ArgumentNullException.ThrowIfNull(token);

        await ConferirRecusaAsync(token, TestContext.Current.CancellationToken);
    }

    [Theory]
    [MemberData(nameof(Aceitos))]
    public async Task TokenAceito_PassaDaAutenticacaoNasRotasProtegidas(Func<IdentityGatewayApiFactory, string> token)
    {
        // Os controles: sem eles, "tudo responde 401" também deixaria as duas theories acima verdes. Passar da
        // autenticação e da policy é chegar ao endpoint: o POST sem corpo responde 400, e a consulta de um tenant que
        // não existe, 404.
        ArgumentNullException.ThrowIfNull(token);
        CancellationToken ct = TestContext.Current.CancellationToken;

        using HttpResponseMessage registro = await EnviarAsync(HttpMethod.Post, "/api/v1/tenants", token(factory), ct);
        using HttpResponseMessage consulta = await EnviarAsync(HttpMethod.Get, RotaDeConsulta(), token(factory), ct);
        using HttpResponseMessage leitura = await EnviarAsync(
            HttpMethod.Get, $"/api/v1/tenants/{Guid.NewGuid()}", token(factory), ct);

        registro.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        consulta.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Na leitura de tenant, o platform-admin passa da autenticação e para na autorização: 403, e não 401.
        leitura.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task EsquemaBasic_Responde401()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage pedido = new(HttpMethod.Post, "/api/v1/tenants");
        pedido.Headers.TryAddWithoutValidation("Authorization", "Basic dXN1YXJpbzpzZW5oYQ==");

        using HttpResponseMessage resposta = await client.SendAsync(pedido, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
