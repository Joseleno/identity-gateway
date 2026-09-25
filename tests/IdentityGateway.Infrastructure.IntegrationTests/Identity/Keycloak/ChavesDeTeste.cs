using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// Par de chaves da Gateway gerado em memória para o teste.
/// </summary>
/// <remarks>
/// <b>Nunca um <c>.pem</c> versionado.</b> O repositório é público, e o secret scanning do GitHub dispara em chave
/// privada commitada — mesmo de teste. Gerar a cada execução custa milissegundos.
/// </remarks>
internal static class ChavesDeTeste
{
    public static ParDeChaves Gerar()
    {
        var rsa = RSA.Create(2048);

        CertificateRequest pedido = new(
            "CN=identity-gateway", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        using X509Certificate2 certificado = pedido.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        // DER em base64 numa linha só: é o formato que o placeholder do realm espera — o import substitui o texto
        // antes do parse do JSON, e uma quebra de linha no valor quebraria o JSON.
        return new ParDeChaves(
            rsa,
            rsa.ExportPkcs8PrivateKeyPem(),
            Convert.ToBase64String(certificado.Export(X509ContentType.Cert)));
    }
}

/// <summary>A chave, sua forma PEM (o que a Gateway lê) e o certificado (o que o Keycloak registra).</summary>
internal sealed record ParDeChaves(RSA Rsa, string PemPrivado, string CertificadoBase64);
