using IdentityGateway.Application.Common.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// Monta o client assertion do <c>private_key_jwt</c> (RFC 7523): a prova de posse da chave que a Gateway apresenta
/// ao token endpoint no lugar de um segredo.
/// </summary>
/// <remarks>
/// <para>Cada campo tem um motivo verificado no código do Keycloak 26.7.4 (spec v2.4, §10.2):</para>
/// <list type="bullet">
///   <item><b><c>aud</c> = issuer, string única.</b> Aceito sempre e recomendado desde a 26.2; <c>aud</c> com mais de
///   um valor é recusado.</item>
///   <item><b><c>jti</c> novo.</b> O Keycloak o exige e o guarda num cache de uso único.</item>
///   <item><b>Tempos explícitos, 60s.</b> Deixados à biblioteca, seriam 60 minutos.</item>
///   <item><b><see cref="RsaSecurityKey"/> sem <c>KeyId</c>.</b> Nenhum <c>kid</c> sai no header, e o Keycloak usa o
///   certificado padrão do client. Um <c>kid</c> calculado pelo .NET não bateria com o do Keycloak.</item>
///   <item><b>PS256</b>, igual ao fixado no client do realm.</item>
/// </list>
/// </remarks>
internal sealed class ClientAssertionFactory(
    GatewaySigningKey chave,
    IOptions<KeycloakAdminOptions> options,
    IDateTimeProvider relogio)
{
    /// <summary>Vida do assertion.</summary>
    internal static readonly TimeSpan Vida = TimeSpan.FromSeconds(60);

    // Thread-safe e sem estado por token: uma instância serve a todos.
    private static readonly JsonWebTokenHandler Emissor = new();

    // O CryptoProviderFactory.Default cacheia o SignatureProvider — e com ele o RSA — por processo, indexado pelo
    // material da chave. Esse cache sobrevive à vida do GatewaySigningKey que o DI descarta: uma segunda instância
    // da MESMA chave no mesmo processo (outro host, uma recarga, uma rotação) assinaria com um RSA já descartado,
    // e o sintoma é um ObjectDisposedException sem relação aparente com o request que falhou. Uma fábrica própria,
    // sem cache, custa um SignatureProvider por assertion — ou seja, por renovação de token, não por request — e
    // evita depender da vida do processo bater com a vida de cada instância de chave.
    private static readonly CryptoProviderFactory Assinadores = new() { CacheSignatureProviders = false };

    /// <summary>Um assertion novo, com <c>jti</c> próprio.</summary>
    public string Criar()
    {
        KeycloakAdminOptions opcoes = options.Value;
        DateTime agora = relogio.UtcNow.UtcDateTime;

        SecurityTokenDescriptor descritor = new()
        {
            Issuer = opcoes.ClientId,

            // Só aqui: o Keycloak 26.2+ recusa aud com mais de um valor. Repetir em Claims["aud"] seria, no
            // mínimo, redundante — nesta versão da biblioteca Audience prevalece em silêncio — e dependeria de
            // uma precedência não documentada que uma atualização poderia inverter.
            Audience = opcoes.Issuer,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = opcoes.ClientId,
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString(),
            },
            IssuedAt = agora,
            NotBefore = agora,
            Expires = agora + Vida,
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(chave.Rsa), SecurityAlgorithms.RsaSsaPssSha256)
            {
                CryptoProviderFactory = Assinadores,
            },
        };

        return Emissor.CreateToken(descritor);
    }
}
