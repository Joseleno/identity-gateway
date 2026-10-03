using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace IdentityGateway.Api.Authorization;

/// <summary>
/// Garante que o tenant do token é o tenant da rota.
/// </summary>
/// <remarks>
/// <para>
/// Sem esta verificação, qualquer administrador de tenant operaria sobre qualquer tenant: bastaria trocar o id na URL
/// (BOLA/IDOR).
/// </para>
/// <para>
/// <b>Todo caminho que não é sucesso chama <c>Fail()</c>.</b> Um requirement sem <c>Succeed</c> e sem <c>Fail</c> fica
/// só "ainda não satisfeito", e outro handler poderia satisfazê-lo. É por isso que o acesso do platform-admin à
/// leitura de tenant <b>não</b> é um segundo handler deste requirement: o <c>Fail()</c> daqui o vetaria. Ele será
/// uma policy própria.
/// </para>
/// </remarks>
internal sealed class SameTenantRequirement : AuthorizationHandler<SameTenantRequirement>, IAuthorizationRequirement
{
    /// <summary>O nome do parâmetro de rota que toda rota com policy de tenant precisa ter. Há teste de subida.</summary>
    internal const string ParametroDaRota = "tenantId";

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, SameTenantRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (MesmoTenant(context))
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(new AuthorizationFailureReason(this, "O tenant da rota e o do token não conferem."));
        }

        return Task.CompletedTask;
    }

    private static bool MesmoTenant(AuthorizationHandlerContext context)
    {
        // Com endpoint routing, o Resource é o próprio HttpContext.
        if (context.Resource is not HttpContext http)
        {
            return false;
        }

        // GetRouteValue devolve o texto cru da URL, mesmo com a restrição :guid — que aceita os mesmos formatos do
        // Guid.TryParse. Rota sem {tenantId} é erro de configuração, e o teste de subida impede que chegue aqui.
        if (!Guid.TryParse(http.GetRouteValue(ParametroDaRota)?.ToString(), out Guid daRota))
        {
            return false;
        }

        // Exatamente UM claim. O Keycloak nunca emite dois (o mapper não é multivalorado); aceitar "o primeiro", "o
        // último" ou "algum" aceitaria um token forjado com o tenant da vítima numa das posições.
        if (context.User.FindAll("tenant_id").Take(2).ToArray() is not [Claim claim])
        {
            return false;
        }

        // O claim, só no formato D, que é como o Keycloak o emite — conferido pela ida e volta, porque o
        // Guid.TryParseExact tolera espaço nas pontas e, em cada componente, o prefixo 0x e o sinal de mais:
        // "0x99a000-…" viraria o GUID 0099a000-…. A comparação com a rota é por Guid, e não por texto: a rota com o
        // GUID em maiúsculas é o mesmo tenant.
        return Guid.TryParseExact(claim.Value, "D", out Guid doToken)
            && string.Equals(doToken.ToString("D"), claim.Value, StringComparison.OrdinalIgnoreCase)
            && doToken == daRota;
    }
}
