using FluentValidation;

namespace IdentityGateway.Application.Tenants.RegisterTenant;

/// <summary>
/// Exige que os campos obrigatórios estejam presentes e bem formados.
/// </summary>
/// <remarks>
/// Cobre <b>presença</b>; a <b>forma</b> do slug é de <c>TenantSlug.Create</c>. A divisão evita a mesma regra
/// em dois lugares — e o validator recusa antes de o handler abrir transação, porque o
/// <c>ValidationBehavior</c> roda mais cedo no pipeline.
/// </remarks>
public sealed class RegisterTenantValidator : AbstractValidator<RegisterTenantCommand>
{
    public RegisterTenantValidator()
    {
        RuleFor(comando => comando.Name)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(comando => comando.Slug)
            .NotEmpty();

        RuleFor(comando => comando.PlanCode)
            .NotEmpty();

        RuleFor(comando => comando.InitialAdminEmail)
            .NotEmpty()
            .EmailAddress();
    }
}
