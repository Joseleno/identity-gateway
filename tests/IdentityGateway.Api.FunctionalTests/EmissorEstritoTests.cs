using IdentityGateway.Api.Authentication;
using Microsoft.IdentityModel.Tokens;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// O emissor aceito é um texto só, comparado por igualdade ordinal.
/// </summary>
/// <remarks>
/// A função é a única barreira de emissor que existe: com metadados, a biblioteca aceitaria o emissor que o discovery
/// anuncia, com ou sem <c>ValidIssuer</c>. Por isso ela tem teste próprio, fora do HTTP.
/// </remarks>
public sealed class EmissorEstritoTests
{
    private const string Esperado = "https://sso.exemplo.test/realms/identity-gateway";

    private static string Validar(string emissor) =>
        ValidacaoDoAccessToken.EmissorEstrito(Esperado)(emissor, securityToken: null!, validationParameters: null!);

    [Fact]
    public void EmissorIgualAoConfigurado_EAceito()
    {
        Validar(Esperado).Should().Be(Esperado);
    }

    [Theory]
    [InlineData("https://sso.exemplo.test/realms/identity-gateway/")]       // barra final
    [InlineData("https://sso.exemplo.test/realms/identity-gateway-outro")]  // o esperado é prefixo
    [InlineData("https://sso.exemplo.test/realms/identity")]                // é prefixo do esperado
    [InlineData("HTTPS://SSO.EXEMPLO.TEST/REALMS/IDENTITY-GATEWAY")]        // só a caixa muda
    [InlineData("http://sso.exemplo.test/realms/identity-gateway")]         // outro esquema
    [InlineData("https://keycloak.interno.test/realms/identity-gateway")]   // o endereço de transporte
    [InlineData("")]
    public void QualquerOutroTexto_ERecusadoComOEmissorNaExcecao(string emissor)
    {
        Action validar = () => Validar(emissor);

        // SecurityTokenInvalidIssuerException, e não outra: é a que a biblioteca trata como recuperável.
        validar.Should().Throw<SecurityTokenInvalidIssuerException>()
            .Which.InvalidIssuer.Should().Be(emissor);
    }
}
