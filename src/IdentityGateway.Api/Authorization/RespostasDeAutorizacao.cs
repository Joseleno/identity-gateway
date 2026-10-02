using IdentityGateway.Application.Common.Abstractions;

namespace IdentityGateway.Api.Authorization;

/// <summary>
/// As duas respostas de quem não entra: <c>401</c> e <c>403</c>, como Problem Details (RFC 9457).
/// </summary>
/// <remarks>
/// <para>
/// <b>Um texto só para cada status, sem nada que distinga o motivo.</b> O <c>403</c> de "papel errado", o de "tenant
/// alheio" e o de "tenant que não existe" são a mesma resposta, byte a byte no que é fixo: uma diferença entre eles
/// diria a quem chama o que existe e o que não existe. O motivo fica no log, do lado de cá.
/// </para>
/// <para>
/// <b>Uma função só escreve cada um.</b> O handler de resultado da autorização e os módulos que precisam negar por
/// conta própria chamam o mesmo método — não há segundo lugar onde o texto possa divergir.
/// </para>
/// </remarks>
internal static class RespostasDeAutorizacao
{
    internal const string TipoDoProibido = "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.4";

    internal const string TipoDoNaoAutenticado = "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.2";

    internal const string TituloDoProibido = "Acesso negado";

    internal const string TituloDoNaoAutenticado = "Não autenticado";

    internal const string DetalheDoProibido = "A identidade apresentada não tem permissão para esta operação.";

    internal const string DetalheDoNaoAutenticado = "A requisição não traz um access token válido.";

    /// <summary>O <c>403</c> único da Gateway.</summary>
    public static IResult Proibido(HttpContext contexto) => Problema(
        contexto, StatusCodes.Status403Forbidden, TipoDoProibido, TituloDoProibido, DetalheDoProibido);

    /// <summary>O <c>401</c> único da Gateway. O <c>WWW-Authenticate</c> é de quem desafia, não daqui.</summary>
    public static IResult NaoAutenticado(HttpContext contexto) => Problema(
        contexto, StatusCodes.Status401Unauthorized, TipoDoNaoAutenticado, TituloDoNaoAutenticado,
        DetalheDoNaoAutenticado);

    private static IResult Problema(HttpContext contexto, int status, string tipo, string titulo, string detalhe)
    {
        ArgumentNullException.ThrowIfNull(contexto);

        return Results.Problem(
            statusCode: status,
            type: tipo,
            title: titulo,
            detail: detalhe,
            extensions: new Dictionary<string, object?>
            {
                ["correlationId"] = contexto.RequestServices.GetRequiredService<ICorrelationIdProvider>().CorrelationId,
            });
    }
}
