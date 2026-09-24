using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// A chave privada da Gateway, importada uma única vez.
/// </summary>
/// <remarks>
/// Singleton: ler e decodificar o PEM a cada assertion seria trabalho repetido, e manter a string PEM em memória
/// por toda a vida do processo seria uma cópia a mais do segredo. Aqui a string é lida, importada e descartada;
/// fica só o objeto <see cref="RSA"/>.
/// </remarks>
internal sealed class GatewaySigningKey(IOptions<KeycloakAdminOptions> options) : IDisposable
{
    /// <summary>A chave, pronta para assinar.</summary>
    public RSA Rsa { get; } = Carregar(options.Value);

    /// <summary>
    /// Lê a chave do arquivo ou do PEM inline e a importa.
    /// </summary>
    /// <exception cref="IOException">Arquivo ilegível.</exception>
    /// <exception cref="ArgumentException">Conteúdo sem bloco PEM reconhecível.</exception>
    /// <exception cref="CryptographicException">Bloco PEM que não é uma chave RSA válida.</exception>
    public static RSA Carregar(KeycloakAdminOptions opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);

        string pem = string.IsNullOrWhiteSpace(opcoes.PrivateKeyPath)
            ? opcoes.PrivateKeyPem ?? string.Empty
            : File.ReadAllText(opcoes.PrivateKeyPath);

        var rsa = RSA.Create();

        try
        {
            rsa.ImportFromPem(pem);
            return rsa;
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Se a chave configurada pode ser carregada — usado pela validação na subida.
    /// </summary>
    /// <remarks>
    /// Quando a regra "exatamente uma fonte" já está violada, devolve <c>true</c> e deixa a mensagem para ela: duas
    /// mensagens para o mesmo erro confundem mais do que ajudam.
    /// </remarks>
    public static bool EhLegivel(KeycloakAdminOptions opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);

        if (!TemExatamenteUmaFonte(opcoes))
        {
            return true;
        }

        try
        {
            using RSA _ = Carregar(opcoes);
            return true;
        }
        catch (Exception excecao) when (excecao is IOException or UnauthorizedAccessException
                                            or ArgumentException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Exatamente uma entre <c>PrivateKeyPath</c> e <c>PrivateKeyPem</c>.</summary>
    public static bool TemExatamenteUmaFonte(KeycloakAdminOptions opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);

        return string.IsNullOrWhiteSpace(opcoes.PrivateKeyPath) != string.IsNullOrWhiteSpace(opcoes.PrivateKeyPem);
    }

    public void Dispose() => Rsa.Dispose();
}
