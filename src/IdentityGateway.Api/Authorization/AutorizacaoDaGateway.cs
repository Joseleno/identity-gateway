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

            // Os três primeiros só leem o token e rodam nesta ordem. O da pertença lê o banco e roda por último — o que
            // não vem desta lista, e sim da ordem de registro dos handlers, logo abaixo.
            options.AddPolicy(Policies.TenantAdmin, policy => policy.AddRequirements(
                new RoleRequirement("tenant-admin"),
                new NotPlatformAdminRequirement(),
                new SameTenantRequirement(),
                new MemberRequirement()));
        });

        // A ORDEM IMPORTA. Os três primeiros requirements são o próprio handler, e rodam dentro do
        // PassThroughAuthorizationHandler, que o AddAuthorization acabou de registrar. O handler da pertença entra
        // DEPOIS dele: se entrasse antes, rodaria primeiro, com o contexto ainda sem falha, e consultaria o banco
        // para qualquer tenantId da URL — com qualquer token autenticado.
        services.AddScoped<IAuthorizationHandler, MemberRequirementHandler>();

        return services;
    }
}
