using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Domain.UnitTests.Tenants;

public sealed class TenantSlugTests
{
    [Theory]
    [InlineData("acme", "acme")]
    [InlineData("acme-corp", "acme-corp")]
    [InlineData("Acme-Corp", "acme-corp")]     // normaliza a caixa
    [InlineData("  acme  ", "acme")]           // apara o entorno
    [InlineData("a1b2c3", "a1b2c3")]
    public void Create_ComValorValido_NormalizaEAceita(string entrada, string esperado)
    {
        Result<TenantSlug> resultado = TenantSlug.Create(entrada);

        resultado.IsSuccess.Should().BeTrue();
        resultado.Value.Value.Should().Be(esperado);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ac")]                  // curto demais
    [InlineData("-acme")]               // hífen na ponta
    [InlineData("acme-")]               // hífen na ponta
    [InlineData("acme--corp")]          // hífen consecutivo
    [InlineData("acme_corp")]           // underscore não é válido em DNS
    [InlineData("acmé")]                // fora de [a-z0-9-]
    [InlineData("acme corp")]           // espaço interno
    [InlineData("---")]                 // só hífens, tamanho válido — recusa tem que vir da forma
    [InlineData("a--")]                 // hífen consecutivo na borda direita
    [InlineData("--a")]                 // hífen consecutivo na borda esquerda
    [InlineData("a---b")]               // três hífens entre blocos válidos
    public void Create_ComValorInvalido_Recusa(string entrada)
    {
        Result<TenantSlug> resultado = TenantSlug.Create(entrada);

        resultado.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_ComMaisDe63Caracteres_Recusa()
    {
        string longo = new('a', 64);

        TenantSlug.Create(longo).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_ComExatamente63Caracteres_Aceita()
    {
        string limite = new('a', 63);

        TenantSlug.Create(limite).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Create_ComEspacosAoRedorDe63Caracteres_Aceita()
    {
        // O tamanho é medido depois do trim: os espaços não contam para o limite.
        string comEspacos = "  " + new string('a', 63) + "  ";

        TenantSlug.Create(comEspacos).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void DoisSlugsComOMesmoValor_SaoIguais()
    {
        TenantSlug um = TenantSlug.Create("acme").Value;
        TenantSlug outro = TenantSlug.Create("ACME").Value;

        um.Should().Be(outro);
    }
}
