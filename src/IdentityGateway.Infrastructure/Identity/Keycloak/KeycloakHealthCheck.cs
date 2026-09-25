using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// O Keycloak está pronto para a Gateway se ela consegue obter o token do service account.
/// </summary>
/// <remarks>
/// <para>
/// <b>Obter o token, e não só ler a metadata OIDC.</b> A metadata responderia 200 com a chave errada, o realm sem o
/// client ou o certificado dessincronizado. Obter o token prova chave, realm e <c>private_key_jwt</c> de uma vez — e
/// custa zero por sonda enquanto o token do cache vale.
/// </para>
/// <para>
/// Devolve o <c>FailureStatus</c> do registro (<c>Unhealthy</c>): o <c>MapHealthChecks</c> responde 200 para
/// <c>Degraded</c>, e um check degradado nunca tiraria a instância do balanceador.
/// </para>
/// </remarks>
internal sealed class KeycloakHealthCheck(ServiceAccountTokenCache cache) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            _ = await cache.ObterAsync(cancellationToken);
            return HealthCheckResult.Healthy("Token do service account obtido.");
        }
        catch (HttpRequestException excecao)
        {
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                "Não foi possível obter o token do service account no Keycloak.",
                excecao);
        }
    }
}
