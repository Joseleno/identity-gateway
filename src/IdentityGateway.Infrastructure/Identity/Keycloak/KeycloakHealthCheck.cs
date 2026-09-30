using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// O Keycloak está pronto para a Gateway se ela consegue obter o token do service account — com os papéis que o
/// provisionamento usa.
/// </summary>
/// <remarks>
/// <para>
/// <b>Obter o token, e não só ler a metadata OIDC.</b> A metadata responderia 200 com a chave errada, o realm sem o
/// client ou o certificado dessincronizado. Obter o token prova chave, realm e <c>private_key_jwt</c> de uma vez — e
/// custa zero por sonda enquanto o token do cache vale.
/// </para>
/// <para>
/// <b>E exigir <c>manage-users</c> no token (fatia C).</b> O import do realm só roda na primeira subida: num volume
/// antigo, o token sai, mas sem o papel que o convite do admin exige, e cada tenant passaria a janela inteira em retry.
/// Aqui isso vira <c>Unhealthy</c> com a instrução do conserto.
/// </para>
/// <para>
/// Devolve o <c>FailureStatus</c> do registro (<c>Unhealthy</c>): o <c>MapHealthChecks</c> responde 200 para
/// <c>Degraded</c>, e um check degradado nunca tiraria a instância do balanceador.
/// </para>
/// </remarks>
internal sealed class KeycloakHealthCheck(ServiceAccountTokenCache cache) : IHealthCheck
{
    /// <summary>Papel de <c>realm-management</c> que o convite exige além de <c>manage-organizations</c>.</summary>
    internal const string PapelExigido = "manage-users";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        string token;

        try
        {
            token = await cache.ObterAsync(cancellationToken);
        }
        catch (HttpRequestException excecao)
        {
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                "Não foi possível obter o token do service account no Keycloak.",
                excecao);
        }

        if (!TemPapelDeRealmManagement(token, PapelExigido))
        {
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                $"O service account do Keycloak não tem {PapelExigido}: o realm foi importado antes da fatia C, e o "
                + "import só roda na primeira subida. Rode `docker compose down -v` e suba de novo com "
                + "`docker compose up -d --build`.");
        }

        return HealthCheckResult.Healthy("Token do service account obtido, com manage-users.");
    }

    // Lê resource_access.realm-management.roles sem validar a assinatura: o token acabou de vir do token endpoint por
    // um canal autenticado, e a pergunta é só "que papéis o realm deu".
    private static bool TemPapelDeRealmManagement(string token, string papel)
    {
        try
        {
            JsonWebToken jwt = new(token);
            using var corpo = JsonDocument.Parse(Base64UrlEncoder.Decode(jwt.EncodedPayload));

            return corpo.RootElement.TryGetProperty("resource_access", out JsonElement acessos)
                   && acessos.TryGetProperty("realm-management", out JsonElement cliente)
                   && cliente.TryGetProperty("roles", out JsonElement papeis)
                   && papeis.EnumerateArray().Any(item => item.GetString() == papel);
        }
        catch (Exception excecao) when (excecao is ArgumentException or JsonException or FormatException)
        {
            return false;
        }
    }
}
