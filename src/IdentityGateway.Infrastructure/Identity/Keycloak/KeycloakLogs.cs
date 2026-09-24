using Microsoft.Extensions.Logging;

namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// Mensagens de log da integração com o Keycloak, na faixa 2200–2299.
/// </summary>
/// <remarks>
/// <b>Nenhuma registra token, assertion, chave ou corpo de requisição.</b> O formulário do token endpoint leva o
/// <c>client_assertion</c>, e o token dá <c>manage-organizations</c> sobre o realm: qualquer um dos dois no log
/// seria credencial indexada e retida. O que se registra é o tenant, a operação e o status.
/// </remarks>
internal static partial class KeycloakLogs
{
    [LoggerMessage(
        EventId = 2204,
        Level = LogLevel.Warning,
        Message = "Keycloak: token do service account recusado com {Status} — {Erro}")]
    public static partial void TokenRecusado(ILogger logger, int status, string erro);
}
