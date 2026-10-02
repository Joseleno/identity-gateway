using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace IdentityGateway.Api.FunctionalTests.Oidc;

/// <summary>
/// Um provedor OIDC mínimo em loopback: serve o discovery e o JWKS da chave de teste, e nada mais.
/// </summary>
/// <remarks>
/// <para>
/// <b>É o que deixa a Api de verdade validar tokens sem Keycloak</b>, só por configuração: o <c>BaseUrl</c> aponta
/// para cá, e o JwtBearer busca os metadados e as chaves como faria em produção. Nenhum esquema de autenticação é
/// trocado, nenhuma chave é injetada nas opções.
/// </para>
/// <para>
/// <b>O discovery anuncia um emissor diferente do que a Api aceita</b> (<see cref="EmissorAnunciado"/> é o endereço
/// deste servidor; a Api é configurada com outro, o "público"). É de propósito: a biblioteca aceitaria o emissor do
/// discovery mesmo com <c>ValidIssuer</c> configurado, e só com os dois diferentes um teste distingue o
/// <c>IssuerValidator</c> estrito da validação padrão.
/// </para>
/// <para>
/// A tudo o que não é discovery nem JWKS, responde <c>404</c> — inclusive ao token endpoint, e por isso o
/// <c>/health/ready</c> da Api fica <c>Unhealthy</c> nos testes funcionais, como já ficava.
/// </para>
/// </remarks>
internal sealed class OidcFalso : IAsyncDisposable
{
    public const string Realm = "identity-gateway";

    private readonly WebApplication _servidor;

    private OidcFalso(WebApplication servidor, string baseUrl, X509Certificate2? certificado)
    {
        _servidor = servidor;
        BaseUrl = baseUrl;
        Certificado = certificado;
    }

    /// <summary>O endereço deste servidor, sem barra final: o <c>Keycloak:Admin:BaseUrl</c> da Api sob teste.</summary>
    public string BaseUrl { get; }

    /// <summary>O emissor que o discovery anuncia. <b>Não</b> é o que a Api aceita.</summary>
    public string EmissorAnunciado => $"{BaseUrl}/realms/{Realm}";

    /// <summary>O certificado autoassinado, quando o servidor é HTTPS.</summary>
    public X509Certificate2? Certificado { get; }

    public static async Task<OidcFalso> IniciarAsync(EmissorDeTeste emissor, bool https = false)
    {
        ArgumentNullException.ThrowIfNull(emissor);

        X509Certificate2? certificado = https ? CertificadoAutoassinado() : null;

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port: 0, escuta =>
        {
            if (certificado is not null)
            {
                escuta.UseHttps(certificado);
            }
        }));

        WebApplication servidor = builder.Build();
        string esquema = https ? "https" : "http";
        string? baseUrl = null;

        servidor.MapGet($"/realms/{Realm}/.well-known/openid-configuration", () => Results.Json(new
        {
            issuer = $"{baseUrl}/realms/{Realm}",
            jwks_uri = $"{baseUrl}/realms/{Realm}/protocol/openid-connect/certs",
            token_endpoint = $"{baseUrl}/realms/{Realm}/protocol/openid-connect/token",
            authorization_endpoint = $"{baseUrl}/realms/{Realm}/protocol/openid-connect/auth",
            id_token_signing_alg_values_supported = AlgoritmosAnunciados,
            response_types_supported = TiposDeRespostaAnunciados,
            subject_types_supported = TiposDeSujeitoAnunciados,
        }));

        servidor.MapGet(
            $"/realms/{Realm}/protocol/openid-connect/certs", () => Results.Text(emissor.Jwks, "application/json"));

        await servidor.StartAsync();

        // A porta é escolhida pelo sistema; depois de subir, o Kestrel diz qual foi.
        int porta = new Uri(servidor.Urls.Single()).Port;
        baseUrl = $"{esquema}://127.0.0.1:{porta}";

        return new OidcFalso(servidor, baseUrl, certificado);
    }

    public async ValueTask DisposeAsync()
    {
        await _servidor.DisposeAsync();
        Certificado?.Dispose();
    }

    private static readonly string[] AlgoritmosAnunciados = ["RS256"];

    private static readonly string[] TiposDeRespostaAnunciados = ["code"];

    private static readonly string[] TiposDeSujeitoAnunciados = ["public"];

    /// <summary>Certificado autoassinado para o loopback, gerado em memória.</summary>
    /// <remarks>
    /// Exportado e reimportado como PKCS#12: no Windows, o Kestrel não consegue usar a chave efêmera que o
    /// <c>CreateSelfSigned</c> devolve ("No credentials are available in the security package").
    /// </remarks>
    private static X509Certificate2 CertificadoAutoassinado()
    {
        using var rsa = RSA.Create(2048);
        CertificateRequest pedido = new("CN=oidc-falso", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        SubjectAlternativeNameBuilder nomes = new();
        nomes.AddIpAddress(IPAddress.Loopback);
        pedido.CertificateExtensions.Add(nomes.Build());

        using X509Certificate2 efemero = pedido.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        return X509CertificateLoader.LoadPkcs12(efemero.Export(X509ContentType.Pfx), password: null);
    }
}
