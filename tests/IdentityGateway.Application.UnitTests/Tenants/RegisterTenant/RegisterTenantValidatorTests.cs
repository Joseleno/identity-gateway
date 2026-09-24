using FluentValidation.Results;
using IdentityGateway.Application.Tenants.RegisterTenant;

namespace IdentityGateway.Application.UnitTests.Tenants.RegisterTenant;

/// <summary>
/// Cobre a validação de <b>presença</b> dos campos do comando.
/// </summary>
/// <remarks>
/// A validação de <b>forma</b> do slug não está aqui de propósito: ela vive em <c>TenantSlug.Create</c>, que
/// devolve <c>Result</c>, e duplicá-la faria a regra existir em dois lugares que envelheceriam separados.
/// </remarks>
public sealed class RegisterTenantValidatorTests
{
    private readonly RegisterTenantValidator _validator = new();

    private static RegisterTenantCommand Valido() =>
        new("Acme", "acme", "free", "admin@acme.com");

    [Fact]
    public void ComandoCompleto_Passa()
    {
        ValidationResult resultado = _validator.Validate(Valido());

        resultado.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NomeEmBranco_Falha(string nome)
    {
        ValidationResult resultado = _validator.Validate(Valido() with { Name = nome });

        resultado.IsValid.Should().BeFalse();
    }

    [Fact]
    public void NomeAcimaDe200_Falha()
    {
        ValidationResult resultado = _validator.Validate(Valido() with { Name = new string('a', 201) });

        resultado.IsValid.Should().BeFalse();
    }

    [Fact]
    public void SlugEmBranco_Falha()
    {
        ValidationResult resultado = _validator.Validate(Valido() with { Slug = "" });

        resultado.IsValid.Should().BeFalse();
    }

    [Fact]
    public void PlanCodeEmBranco_Falha()
    {
        ValidationResult resultado = _validator.Validate(Valido() with { PlanCode = "" });

        resultado.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("sem-arroba")]
    public void EmailInvalido_Falha(string email)
    {
        ValidationResult resultado = _validator.Validate(Valido() with { InitialAdminEmail = email });

        resultado.IsValid.Should().BeFalse();
    }
}
