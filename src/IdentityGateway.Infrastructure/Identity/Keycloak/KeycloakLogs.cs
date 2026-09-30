using Microsoft.Extensions.Logging;

namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// Mensagens de log da integração com o Keycloak, na faixa 2200–2299.
/// </summary>
/// <remarks>
/// <b>Nenhuma registra token, assertion, chave ou corpo de requisição.</b> O formulário do token endpoint leva o
/// <c>client_assertion</c>, e o token dá <c>manage-organizations</c> e <c>manage-users</c> sobre o realm: qualquer
/// um dos dois no log seria credencial indexada e retida. <b>Nem o e-mail do convidado</b>: o que identifica o
/// convite nos logs é o tenant. O que se registra é o tenant, a operação e o status.
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

    [LoggerMessage(
        EventId = 2205,
        Level = LogLevel.Debug,
        Message = "Keycloak: usuário do convite do tenant {TenantId} já existia; reaproveitado")]
    public static partial void UsuarioJaExistia(ILogger logger, Guid tenantId);

    [LoggerMessage(
        EventId = 2206,
        Level = LogLevel.Information,
        Message = "Keycloak: usuário do convite do tenant {TenantId} criado")]
    public static partial void UsuarioCriado(ILogger logger, Guid tenantId);

    [LoggerMessage(
        EventId = 2207,
        Level = LogLevel.Information,
        Message = "Keycloak: corrida na criação do usuário do tenant {TenantId} resolvida pela reconsulta")]
    public static partial void CorridaDoUsuarioResolvida(ILogger logger, Guid tenantId);

    [LoggerMessage(
        EventId = 2208,
        Level = LogLevel.Error,
        Message = "Keycloak: o e-mail do convite do tenant {TenantId} pertence a usuário não correlacionado")]
    public static partial void UsuarioNaoCorrelacionado(ILogger logger, Guid tenantId);

    [LoggerMessage(
        EventId = 2209,
        Level = LogLevel.Information,
        Message = "Keycloak: papel {Papel} atribuído ao convidado do tenant {TenantId}")]
    public static partial void PapelAtribuido(ILogger logger, string papel, Guid tenantId);

    [LoggerMessage(
        EventId = 2210,
        Level = LogLevel.Error,
        Message = "Keycloak: o papel {Papel} do convite do tenant {TenantId} não existe no realm")]
    public static partial void PapelAusente(ILogger logger, string papel, Guid tenantId);

    [LoggerMessage(
        EventId = 2211,
        Level = LogLevel.Information,
        Message = "Keycloak: e-mail de convite do tenant {TenantId} enviado")]
    public static partial void ConviteEnviado(ILogger logger, Guid tenantId);

    [LoggerMessage(
        EventId = 2212,
        Level = LogLevel.Information,
        Message = "Keycloak: convite do tenant {TenantId} já aceito; e-mail não reenviado")]
    public static partial void ConviteJaAceito(ILogger logger, Guid tenantId);

    [LoggerMessage(
        EventId = 2213,
        Level = LogLevel.Error,
        Message = "Keycloak: envio do convite do tenant {TenantId} recusado com 400 (usuário desabilitado ou sem e-mail)")]
    public static partial void EnvioRecusado(ILogger logger, Guid tenantId);

    [LoggerMessage(
        EventId = 2214,
        Level = LogLevel.Information,
        Message = "Keycloak: corrida na atribuição do papel {Papel} ao convidado do tenant {TenantId} resolvida pela releitura")]
    public static partial void CorridaDoPapelResolvida(ILogger logger, string papel, Guid tenantId);
}
