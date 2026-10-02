using Microsoft.AspNetCore.Authorization;

namespace IdentityGateway.Api.Authorization;

/// <summary>
/// Separação de funções: quem traz <c>platform-admin</c> não age como administrador de tenant.
/// </summary>
/// <remarks>
/// <para>
/// A hierarquia de papéis é teto de atribuição, não herança de acesso: estar acima de <c>tenant-admin</c> limita o
/// que o platform-admin pode conceder, e não lhe dá o que o <c>tenant-admin</c> acessa.
/// </para>
/// <para>
/// <b>Por que negar quem acumula os dois papéis.</b> O service account da Gateway atribui <c>platform-admin</c>
/// (o <c>manage-users</c> o permite). Sem esta negação, o "platform-admin não lê tenant sem auditoria" só valeria
/// para a conta que não acumula papéis — bastaria somar <c>tenant-admin</c> e um <c>tenant_id</c> para contorná-lo.
/// </para>
/// </remarks>
internal sealed class NotPlatformAdminRequirement
    : AuthorizationHandler<NotPlatformAdminRequirement>, IAuthorizationRequirement
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, NotPlatformAdminRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (context.User.HasClaim("roles", "platform-admin"))
        {
            context.Fail(new AuthorizationFailureReason(this, "Conta de plataforma não age como administrador de tenant."));
        }
        else
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
