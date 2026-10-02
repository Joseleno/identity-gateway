using IdentityGateway.Api.Authentication;
using IdentityGateway.Api.FunctionalTests.Oidc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// As três checagens de forma, fora do HTTP: o que cada uma aceita, o que recusa, e o que o motivo nunca revela.
/// </summary>
/// <remarks>
/// A suíte negativa prova as mesmas regras por HTTP, onde o que se vê é só o <c>401</c>. Aqui se vê o motivo — e que
/// ele é texto fixo: vai para o log, e não pode carregar um valor que veio do token.
/// </remarks>
public sealed class FormaDoAccessTokenTests : IDisposable
{
    private static readonly string[] Permitidos = ["identity-gateway-demo", "console-administrativo"];

    private static readonly string[] TypEmArray = ["Bearer"];

    private readonly EmissorDeTeste _emissor = new("https://sso.exemplo.test/realms/identity-gateway");

    public void Dispose() => _emissor.Dispose();

    private string? Recusar(Action<Dictionary<string, object?>>? ajustar = null, IReadOnlyList<string>? permitidos = null) =>
        FormaDoAccessToken.Recusar(new JsonWebToken(_emissor.Emitir(ajustar: ajustar)), permitidos ?? Permitidos);

    [Fact]
    public void TokenComAFormaDoProvedor_EAceito()
    {
        Recusar().Should().BeNull();
    }

    [Fact]
    public void QualquerClientDaLista_EAceito()
    {
        Recusar(payload => payload["azp"] = "console-administrativo").Should().BeNull();
    }

    [Fact]
    public void ListaVazia_RecusaTodoToken()
    {
        // Fail-closed: lista vazia não é "qualquer client serve".
        Recusar(permitidos: []).Should().Contain("azp");
    }

    [Fact]
    public void AzpComOutraCaixa_ERecusado()
    {
        Recusar(payload => payload["azp"] = "Identity-Gateway-Demo").Should().Contain("azp");
    }

    [Fact]
    public void AzpAusenteOuVazio_ERecusado()
    {
        Recusar(payload => payload.Remove("azp")).Should().Contain("azp");
        Recusar(payload => payload["azp"] = string.Empty).Should().Contain("azp");
    }

    [Fact]
    public void TypDeIdToken_ERecusado()
    {
        Recusar(payload => payload["typ"] = "ID").Should().Contain("typ");
    }

    [Fact]
    public void TypEmArrayDeUmElemento_ERecusado()
    {
        // É o caso que só o JSON distingue: como claim, ["Bearer"] e "Bearer" são iguais. (Para azp e sub em array, a
        // biblioteca nem chega a montar o token — por isso eles não têm teste aqui, só na suíte por HTTP.)
        Recusar(payload => payload["typ"] = TypEmArray).Should().Contain("typ");
    }

    [Fact]
    public void TypAusente_ERecusado()
    {
        Recusar(payload => payload.Remove("typ")).Should().Contain("typ");
    }

    [Theory]
    [InlineData("joao")]
    [InlineData("0199a00000007000800000000000000a")]             // formato N
    [InlineData("{0199a000-0000-7000-8000-00000000000a}")]       // formato B
    [InlineData(" 0199a000-0000-7000-8000-00000000000a")]        // espaço
    [InlineData("0x99a000-0000-7000-8000-00000000000a")]         // prefixo 0x, 36 caracteres
    [InlineData("+199a000-0000-7000-8000-00000000000a")]         // sinal, 36 caracteres
    [InlineData("0199a000-0x00-7000-8000-00000000000a")]         // prefixo 0x num componente do meio
    [InlineData("")]
    public void SubForaDoFormatoD_ERecusado(string sub)
    {
        Recusar(payload => payload["sub"] = sub).Should().Contain("sub");
    }

    [Fact]
    public void SubEmMaiusculasNoFormatoD_EAceito()
    {
        // O controle da ida e volta: a caixa não faz parte do formato D.
        Recusar(payload => payload["sub"] = "0199A000-0000-7000-8000-00000000000A").Should().BeNull();
    }

    [Fact]
    public void OMotivo_NuncaCarregaOValorRecusado()
    {
        // O motivo vai para o log. Um azp, um typ ou um sub forjados podem ser qualquer texto — inclusive um e-mail.
        string?[] motivos =
        [
            Recusar(payload => payload["azp"] = "segredo@acme.test"),
            Recusar(payload => payload["typ"] = "segredo@acme.test"),
            Recusar(payload => payload["sub"] = "segredo@acme.test"),
        ];

        motivos.Should().OnlyContain(motivo => motivo != null && !motivo.Contains("segredo", StringComparison.Ordinal));
    }
}
