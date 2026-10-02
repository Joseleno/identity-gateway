using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace IdentityGateway.Testing.Keycloak;

/// <summary>
/// Par de chaves da Gateway gerado em memória para o teste.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nunca um <c>.pem</c> versionado.</b> O repositório é público, e o secret scanning do GitHub dispara em chave
/// privada commitada — mesmo de teste. Gerar a cada execução custa milissegundos.
/// </para>
/// <para>
/// Públicos: o fixture usa a chave no <c>GATEWAY_CLIENT_CERT</c>, e a coleção de testes da Api precisa do PEM para
/// configurar a Gateway contra o mesmo Keycloak.
/// </para>
/// </remarks>
public static class ChavesDeTeste
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
public sealed record ParDeChaves(RSA Rsa, string PemPrivado, string CertificadoBase64);
