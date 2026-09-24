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

    /// <summary>Um assertion novo, com <c>jti</c> próprio.</summary>
    public string Criar()
    {
        KeycloakAdminOptions opcoes = options.Value;
        DateTime agora = relogio.UtcNow.UtcDateTime;

        SecurityTokenDescriptor descritor = new()
        {
            Issuer = opcoes.ClientId,

            // Só aqui, e nunca também em Claims["aud"]: as duas fontes juntas viram um array.
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
                new RsaSecurityKey(chave.Rsa), SecurityAlgorithms.RsaSsaPssSha256),
        };

        return Emissor.CreateToken(descritor);
    }
}
