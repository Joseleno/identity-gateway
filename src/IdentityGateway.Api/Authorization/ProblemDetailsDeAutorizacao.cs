using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace IdentityGateway.Api.Authorization;

/// <summary>
/// Dá corpo ao <c>401</c> e ao <c>403</c> da autorização: sem isto, o primeiro sai só com o <c>WWW-Authenticate</c> e o
/// segundo sem nada.
/// </summary>
/// <remarks>
/// <para>
/// <b>No <c>401</c>, o desafio padrão roda primeiro.</b> É ele que escreve o <c>WWW-Authenticate</c>, que o cliente
/// OAuth espera; o corpo vem depois, enquanto a resposta ainda não começou.
/// </para>
/// <para>
/// <b>É também onde a auditoria de negação vai nascer.</b> Os handlers de autorização param no primeiro que falha
/// (<c>InvokeHandlersAfterFailure</c> desligado em todas as policies), e por isso não servem para registrar negação;
/// este ponto vê todas.
/// </para>
/// </remarks>
internal sealed class ProblemDetailsDeAutorizacao : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _padrao = new();

    public async Task HandleAsync(
        RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        if (authorizeResult.Forbidden)
        {
            await RespostasDeAutorizacao.Proibido(context).ExecuteAsync(context);
            return;
        }

        if (authorizeResult.Challenged)
        {
            await _padrao.HandleAsync(next, context, policy, authorizeResult);

            if (!context.Response.HasStarted)
            {
                await RespostasDeAutorizacao.NaoAutenticado(context).ExecuteAsync(context);
            }

            return;
        }

        await _padrao.HandleAsync(next, context, policy, authorizeResult);
    }
}
