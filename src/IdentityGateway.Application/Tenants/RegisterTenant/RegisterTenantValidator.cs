using FluentValidation;
using IdentityGateway.Domain.ValueObjects;

namespace IdentityGateway.Application.Tenants.RegisterTenant;

/// <summary>
/// Exige que os campos obrigatórios estejam presentes e bem formados.
/// </summary>
/// <remarks>
/// <para>
/// Cobre <b>presença</b>; a <b>forma</b> do slug é de <c>TenantSlug.Create</c>. A divisão evita a mesma regra
/// em dois lugares — e o validator recusa antes de o handler abrir transação, porque o
/// <c>ValidationBehavior</c> roda mais cedo no pipeline.
/// </para>
/// <para>
/// <b>A forma do e-mail é a do <see cref="Email.Of"/>, chamada por <see cref="Email.IsValid"/>.</b> O
/// <c>EmailAddress()</c> do FluentValidation aceitava <c>a@b</c>, que o <c>Email.Of</c> recusa: o <c>POST</c>
/// respondia 202 e o tenant falharia depois. A mensagem não ecoa o valor (D15).
/// </para>
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
            .Must(Email.IsValid)
            .WithMessage("O e-mail do administrador inicial não é válido.");
    }
}
