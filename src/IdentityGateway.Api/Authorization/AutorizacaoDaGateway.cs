using Microsoft.AspNetCore.Authorization;

namespace IdentityGateway.Api.Authorization;

/// <summary>
/// O registro da autorização da Gateway: as policies, a policy de fallback e a ordem dos handlers.
/// </summary>
/// <remarks>
/// <b>Um método só, usado pela produção e pelos testes unitários.</b> A ordem em que os handlers são registrados é
/// parte da segurança (abaixo), e um teste que montasse a autorização por conta própria provaria outra ordem.
/// </remarks>
internal static class AutorizacaoDaGateway
{
    public static IServiceCollection AddAutorizacaoDaGateway(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthorization(options =>
        {
            // Todo endpoint exige usuário autenticado, a menos que declare AllowAnonymous ou outra policy.
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

            // Depois do primeiro Fail(), nenhum outro handler roda. É global: vale para todas as policies. Por isso a
            // negação não pode ser auditada por um handler — a auditoria nasce no ProblemDetailsDeAutorizacao, que vê
            // todas.
            options.InvokeHandlersAfterFailure = false;

            // Claim plano, não RequireRole: o papel chega no claim "roles", com esse nome (MapInboundClaims desligado).
            options.AddPolicy(Policies.PlatformAdmin, policy => policy.RequireClaim("roles", "platform-admin"));

            // A ordem dos requirements é a ordem em que rodam: os que só leem o token primeiro.
            options.AddPolicy(Policies.TenantAdmin, policy => policy.AddRequirements(
                new RoleRequirement("tenant-admin"),
                new NotPlatformAdminRequirement(),
                new SameTenantRequirement()));
        });

        return services;
    }
}
