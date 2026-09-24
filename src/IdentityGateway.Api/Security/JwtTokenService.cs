using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace IdentityGateway.Api.Security;

/// <summary>
/// Emite tokens JWT assinados com a chave da configuração.
/// </summary>
/// <remarks>
/// <para>
/// <b>É um exemplo, e o kit não esconde isso.</b> Num sistema real, quem emite token é um provedor de identidade
/// — Entra ID, Keycloak, Auth0, Cognito — que cuida de senha, MFA, revogação e rotação de chave. Emitir o próprio
/// token significa assumir tudo isso, e quase nunca é o que se quer.
/// </para>
/// <para>
/// O que existe aqui serve a dois propósitos legítimos: permitir experimentar a API logo depois do clone, sem
/// montar um IdP, e dar aos testes funcionais um token de verdade, para que eles exercitem o mesmo caminho de
/// validação que produção usa.
/// </para>
/// <para>
/// <b>Para ligar um IdP de verdade</b>, apague este serviço e o endpoint que o expõe, e troque a validação por
/// autoridade e JWKS — <c>options.Authority</c> em vez de <c>IssuerSigningKey</c>. A configuração da validação
/// está em <c>AddAutenticacao</c>, e é o único outro lugar que muda.
/// </para>
/// </remarks>
internal sealed class JwtTokenService(IOptions<JwtOptions> options, IDateTimeProvider clock)
{
    private readonly JwtOptions _options = options.Value;

    /// <summary>
    /// Emite um token para o usuário informado.
    /// </summary>
    /// <remarks>
    /// O identificador vai em <c>sub</c>, que é o claim padrão do registro do JWT para "quem é o sujeito deste
    /// token". O <c>HttpCurrentUser</c> o lê <b>nessa mesma forma curta</b>, porque a validação roda com
    /// <c>MapInboundClaims = false</c>: o remapeamento automático do handler traduzia <c>roles</c> para a URI
    /// longa antes de a policy <c>PlatformAdmin</c> comparar, e o <c>403</c> vinha mesmo com o token correto.
    /// Desligá-lo conserta a policy e, de quebra, faz os claims chegarem como o emissor os escreveu — que é o
    /// que um IdP externo vai entregar. O <c>HttpCurrentUser</c> ainda aceita a URI longa como alternativa.
    /// <para>
    /// <b>O claim de papel é <c>roles</c> plano, não aninhado.</b> A §12.1 documenta que <c>RequireRole</c>
    /// falha com o Keycloak porque o papel chega dentro de <c>realm_access.roles</c> — e chama isso de "o ponto
    /// que mais gera erro nessa integração". A policy exige o claim plano, e é ele que este método emite: o
    /// token de teste tem a mesma forma que o do Keycloak terá, com o client scope configurado.
    /// </para>
    /// </remarks>
    public string Emitir(Guid usuarioId, string nome, params string[] roles)
    {
        DateTime agora = clock.UtcNow.UtcDateTime;

        List<Claim> claims =
        [
            new(JwtRegisteredClaimNames.Sub, usuarioId.ToString()),
            new(JwtRegisteredClaimNames.Name, nome),

            // Identificador único do token. Não é usado hoje, e é o que uma lista de revogação precisaria para
            // invalidar um token específico antes de ele expirar.
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
        ];

        // Um claim por papel, e não um único claim com lista separada por vírgula: é assim que o
        // RequireClaim da policy compara.
        foreach (string role in roles)
        {
            claims.Add(new Claim("roles", role));
        }

        SymmetricSecurityKey chave = new(Encoding.UTF8.GetBytes(_options.SigningKey));

        JwtSecurityToken token = new(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: agora,
            expires: agora.AddMinutes(_options.ExpirationMinutes),
            signingCredentials: new SigningCredentials(chave, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
