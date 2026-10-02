using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdentityGateway.Api.FunctionalTests.Oidc;

/// <summary>
/// Emite tokens com a forma dos do Keycloak, assinados com uma chave RSA de teste — e também os malformados.
/// </summary>
/// <remarks>
/// <para>
/// <b>O JWT é montado à mão</b> (cabeçalho, payload, assinatura), e não por uma biblioteca de emissão: os casos
/// negativos precisam de coisas que nenhuma biblioteca deixa escrever — <c>alg: none</c>, <c>azp</c> em array, token
/// sem <c>exp</c>, HS256 com a chave pública como segredo.
/// </para>
/// <para>
/// <b>O payload padrão imita o token real</b> do client de demonstração (os tipos de cada claim são conferidos contra
/// um token de verdade na coleção com Keycloak): <c>aud</c> texto, <c>sub</c> GUID, <c>typ</c> <c>Bearer</c>,
/// <c>acr</c> texto, <c>roles</c> array, <c>tenant_id</c> texto, 5 minutos.
/// </para>
/// <para>
/// A chave nasce em memória, a cada execução. Nunca um <c>.pem</c> versionado.
/// </para>
/// </remarks>
internal sealed class EmissorDeTeste : IDisposable
{
    /// <summary>O <c>kid</c> fixo da chave de teste, publicado no JWKS do OIDC falso.</summary>
    public const string Kid = "chave-de-teste";

    public const string AudienciaDaGateway = "identity-gateway-api";

    public const string ClientDeDemonstracao = "identity-gateway-demo";

    private readonly RSA _rsa = RSA.Create(2048);

    /// <param name="emissor">O <c>iss</c> dos tokens: o emissor público que a Api está configurada para aceitar.</param>
    public EmissorDeTeste(string emissor)
    {
        Emissor = emissor;

        RSAParameters publica = _rsa.ExportParameters(includePrivateParameters: false);
        Jwks = JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = "RS256",
                    kid = Kid,
                    n = Base64Url.EncodeToString(publica.Modulus),
                    e = Base64Url.EncodeToString(publica.Exponent),
                },
            },
        });
    }

    public string Emissor { get; }

    /// <summary>O documento JWKS com a chave pública, como o endpoint de certificados o serve.</summary>
    public string Jwks { get; }

    /// <summary>A chave pública em PEM — para o ataque clássico de usá-la como segredo HS256.</summary>
    public byte[] ChavePublicaEmPem() => Encoding.ASCII.GetBytes(_rsa.ExportSubjectPublicKeyInfoPem());

    public void Dispose() => _rsa.Dispose();

    /// <summary>O payload de um token válido. O teste o altera antes de assinar.</summary>
    public Dictionary<string, object?> Payload(Guid? sub = null, IReadOnlyCollection<string>? roles = null, string? tenantId = null)
    {
        long agora = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Dictionary<string, object?> payload = new()
        {
            ["exp"] = agora + 300,
            ["iat"] = agora,
            ["auth_time"] = agora,
            ["jti"] = Guid.NewGuid().ToString(),
            ["iss"] = Emissor,
            ["aud"] = AudienciaDaGateway,
            ["sub"] = (sub ?? Guid.NewGuid()).ToString("D"),
            ["typ"] = "Bearer",
            ["azp"] = ClientDeDemonstracao,
            ["sid"] = Guid.NewGuid().ToString("N"),
            ["acr"] = "1",
            ["scope"] = "openid",
        };

        if (tenantId is not null)
        {
            payload["tenant_id"] = tenantId;
        }

        // Como o Keycloak: usuário sem papel do catálogo recebe o token SEM o claim.
        if (roles is { Count: > 0 })
        {
            payload["roles"] = roles;
        }

        return payload;
    }

    /// <summary>Um token válido, com as alterações que o teste pedir no payload.</summary>
    public string Emitir(
        Guid? sub = null, IReadOnlyCollection<string>? roles = null, string? tenantId = null,
        Action<Dictionary<string, object?>>? ajustar = null)
    {
        Dictionary<string, object?> payload = Payload(sub, roles, tenantId);
        ajustar?.Invoke(payload);

        return Assinar(payload);
    }

    /// <summary>Assina com RS256. Sem <paramref name="chave"/>, usa a chave de teste, que a Api conhece pelo JWKS.</summary>
    public string Assinar(Dictionary<string, object?> payload, RSA? chave = null, string kid = Kid)
    {
        string conteudo = $"{Codificar(new { alg = "RS256", typ = "JWT", kid })}.{Codificar(payload)}";
        byte[] assinatura = (chave ?? _rsa).SignData(
            Encoding.ASCII.GetBytes(conteudo), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{conteudo}.{Base64Url.EncodeToString(assinatura)}";
    }

    /// <summary>Um token HS256, assinado com o segredo dado.</summary>
    public static string AssinarComHs256(Dictionary<string, object?> payload, byte[] segredo, string? kid = null)
    {
        object cabecalho = kid is null ? new { alg = "HS256", typ = "JWT" } : new { alg = "HS256", typ = "JWT", kid };
        string conteudo = $"{Codificar(cabecalho)}.{Codificar(payload)}";
        byte[] assinatura = HMACSHA256.HashData(segredo, Encoding.ASCII.GetBytes(conteudo));

        return $"{conteudo}.{Base64Url.EncodeToString(assinatura)}";
    }

    /// <summary>Um token <c>alg: none</c>, sem assinatura.</summary>
    public static string SemAssinatura(Dictionary<string, object?> payload) =>
        $"{Codificar(new { alg = "none", typ = "JWT" })}.{Codificar(payload)}.";

    private static string Codificar(object valor) =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(valor));
}
