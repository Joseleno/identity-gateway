namespace IdentityGateway.Api.Authentication;

/// <summary>
/// Mensagens de log da autenticação, na faixa 2100–2199.
/// </summary>
/// <remarks>
/// <b>Nenhuma registra o token, um claim dele nem a mensagem da exceção de validação.</b> O que entra é o tipo da
/// falha e, onde cabe, o motivo em texto fixo.
/// </remarks>
internal static partial class AutenticacaoLogs
{
    [LoggerMessage(
        EventId = 2100,
        Level = LogLevel.Warning,
        Message = "Autenticação: token recusado por chave de assinatura não encontrada ({Tipo}). É o sintoma de chaves "
                  + "ou metadados do provedor de identidade indisponíveis, ou de uma troca de chaves; um token com kid "
                  + "desconhecido também o provoca. No máximo um aviso destes a cada 30 s; os demais saem em Debug")]
    public static partial void ChavesIndisponiveis(ILogger logger, string tipo);

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Debug,
        Message = "Autenticação: token recusado pela validação ({Tipo})")]
    public static partial void TokenRecusado(ILogger logger, string tipo);

    [LoggerMessage(
        EventId = 2102,
        Level = LogLevel.Debug,
        Message = "Autenticação: token recusado pela forma ({Motivo})")]
    public static partial void FormaRecusada(ILogger logger, string motivo);

    [LoggerMessage(
        EventId = 2103,
        Level = LogLevel.Warning,
        Message = "Autenticação: Keycloak:Auth:AllowedClients está vazia; todo token de usuário será recusado com 401")]
    public static partial void NenhumClientPermitido(ILogger logger);
}
