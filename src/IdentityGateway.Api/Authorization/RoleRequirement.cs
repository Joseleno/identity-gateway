using Microsoft.AspNetCore.Authorization;

namespace IdentityGateway.Api.Authorization;

/// <summary>
/// Exige um papel do catálogo no claim <c>roles</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Um handler próprio, e não <c>RequireClaim</c>.</b> O <c>RequireClaim</c> só deixa de dar <c>Succeed</c> quando o
/// claim falta — ele não chama <c>Fail()</c>. Em ASP.NET Core, um requirement sem <c>Succeed</c> e sem <c>Fail</c> está
/// só "ainda não satisfeito": qualquer outro handler que o aprove o satisfaz. <c>Fail()</c> veta, e nada o desfaz.
/// </para>
/// <para>
/// <b>O requirement é o próprio handler.</b> Não precisa de serviço nenhum, e assim roda dentro do
/// <c>PassThroughAuthorizationHandler</c>, na ordem em que a policy o declara (ver <see cref="AutorizacaoDaGateway"/>).
/// </para>
/// </remarks>
internal sealed class RoleRequirement(string role) : AuthorizationHandler<RoleRequirement>, IAuthorizationRequirement
{
    /// <summary>O papel exigido, como o realm o escreve.</summary>
    public string Role { get; } = role;

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RoleRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (context.User.HasClaim("roles", requirement.Role))
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(new AuthorizationFailureReason(this, "O papel exigido não está no token."));
        }

        return Task.CompletedTask;
    }
}
