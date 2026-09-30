using IdentityGateway.Domain.Members;

namespace IdentityGateway.Domain.UnitTests.Members;

public sealed class ExternalUserIdTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void From_Vazio_Lanca(string? valor)
    {
        // O sub vem do Keycloak, não de quem chama a API: vazio é defeito do adaptador, e defeito lança.
        Action criar = () => ExternalUserId.From(valor!);

        criar.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void From_GuardaOValorAparado()
    {
        ExternalUserId.From(" 9f1c-sub ").Value.Should().Be("9f1c-sub");
    }

    [Fact]
    public void DoisComOMesmoValor_SaoIguais()
    {
        ExternalUserId.From("abc").Should().Be(ExternalUserId.From("abc"));
    }
}
