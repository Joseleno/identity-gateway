using IdentityGateway.Api.Authentication;
using Microsoft.IdentityModel.Tokens;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// Quando a falha de autenticação vale um aviso, e quantas vezes.
/// </summary>
/// <remarks>
/// O aviso existe para o provedor de identidade fora do ar não virar <c>401</c> em silêncio. Dois erros o estragariam:
/// tratar qualquer recusa como "chaves indisponíveis" (o alerta deixaria de querer dizer alguma coisa), e avisar a cada
/// pedido (quem manda um token com <c>kid</c> inventado encheria o log sem se autenticar).
/// </remarks>
public sealed class AvisoDeChavesIndisponiveisTests
{
    [Theory]
    [InlineData(typeof(SecurityTokenSignatureKeyNotFoundException), true)]
    [InlineData(typeof(SecurityTokenExpiredException), false)]
    [InlineData(typeof(SecurityTokenInvalidAudienceException), false)]
    [InlineData(typeof(SecurityTokenInvalidIssuerException), false)]
    [InlineData(typeof(SecurityTokenInvalidSignatureException), false)]
    [InlineData(typeof(SecurityTokenMalformedException), false)]
    [InlineData(typeof(InvalidOperationException), false)]
    [InlineData(typeof(HttpRequestException), false)]
    [InlineData(typeof(TaskCanceledException), false)]
    public void SoAFaltaDeChave_EFalhaDeChave(Type tipoDaExcecao, bool esperado)
    {
        ArgumentNullException.ThrowIfNull(tipoDaExcecao);
        var excecao = (Exception)Activator.CreateInstance(tipoDaExcecao)!;

        ValidacaoDoAccessToken.EhFalhaDeChaveOuDeMetadados(excecao).Should().Be(esperado);
    }

    [Fact]
    public void PodeAvisar_UmaVezPorIntervalo()
    {
        AvisoDeChavesIndisponiveis aviso = new();
        long intervalo = (long)ValidacaoDoAccessToken.IntervaloDeRefresh.TotalMilliseconds;

        aviso.PodeAvisar(agoraEmMs: 1_000).Should().BeTrue("o primeiro avisa");
        aviso.PodeAvisar(agoraEmMs: 1_001).Should().BeFalse("o segundo, logo em seguida, não");
        aviso.PodeAvisar(agoraEmMs: 1_000 + intervalo - 1).Should().BeFalse("nem o último do intervalo");
        aviso.PodeAvisar(agoraEmMs: 1_000 + intervalo).Should().BeTrue("passado o intervalo, avisa de novo");
    }
}
