using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Common.Messaging;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Application.Tenants.RegisterTenant;

/// <summary>
/// Orquestra o registro de um tenant.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nenhuma chamada ao Keycloak acontece aqui.</b> O <c>INSERT</c> e o evento no Outbox saem no mesmo
/// <c>SaveChanges</c> — que é o que garante que ou os dois acontecem, ou nenhum. O provisionamento é trabalho
/// do consumidor da mensagem, mais tarde.
/// </para>
/// <para>
/// A checagem de unicidade não dispensa o índice único do banco: ela existe para dar a mensagem de negócio
/// nomeando o slug, e entre o <c>SELECT</c> e o <c>INSERT</c> há uma janela que só a constraint fecha.
/// </para>
/// </remarks>
public sealed class RegisterTenantHandler(
    ITenantRepository repositorio,
    IPlanCatalog catalogo,
    IDateTimeProvider relogio)
    : ICommandHandler<RegisterTenantCommand, TenantId>
{
    public async ValueTask<Result<TenantId>> Handle(
        RegisterTenantCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<TenantSlug> slug = TenantSlug.Create(command.Slug);

        if (slug.IsFailure)
        {
            return Result.Failure<TenantId>(slug.Error);
        }

        if (await repositorio.SlugExistsAsync(slug.Value, cancellationToken))
        {
            return Result.Failure<TenantId>(TenantErrors.SlugInUse(slug.Value));
        }

        Plan? plano = catalogo.Find(command.PlanCode);

        if (plano is null)
        {
            return Result.Failure<TenantId>(TenantErrors.UnknownPlan(command.PlanCode));
        }

        var tenant = Tenant.Register(command.Name, slug.Value, plano, relogio.UtcNow);

        repositorio.Add(tenant);

        return tenant.Id;
    }
}
