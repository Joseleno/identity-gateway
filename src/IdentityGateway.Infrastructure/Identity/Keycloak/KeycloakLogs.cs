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
        EventId = 2200,
        Level = LogLevel.Debug,
        Message = "Keycloak: Organization do tenant {TenantId} já existia")]
    public static partial void OrganizacaoJaExistia(ILogger logger, Guid tenantId);

    [LoggerMessage(
        EventId = 2201,
        Level = LogLevel.Information,
        Message = "Keycloak: Organization do tenant {TenantId} criada")]
    public static partial void OrganizacaoCriada(ILogger logger, Guid tenantId);

    [LoggerMessage(
        EventId = 2202,
        Level = LogLevel.Information,
        Message = "Keycloak: corrida na criação da Organization do tenant {TenantId} resolvida pela reconsulta")]
    public static partial void CorridaResolvida(ILogger logger, Guid tenantId);

    [LoggerMessage(
        EventId = 2203,
        Level = LogLevel.Error,
        Message = "Keycloak: o slug do tenant {TenantId} está em uso por Organization não correlacionada")]
    public static partial void ConflitoNaoCorrelacionado(ILogger logger, Guid tenantId);

    [LoggerMessage(
        EventId = 2204,
        Level = LogLevel.Warning,
        Message = "Keycloak: token do service account recusado com {Status} — {Erro}")]
    public static partial void TokenRecusado(ILogger logger, int status, string erro);
}
