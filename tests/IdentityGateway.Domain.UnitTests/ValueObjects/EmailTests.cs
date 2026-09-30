using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.ValueObjects;

namespace IdentityGateway.Domain.UnitTests.ValueObjects;

/// <summary>
/// Criação de <see cref="Email"/>: o que a regra aceita, o que ela recusa, e o que ela nunca revela.
/// </summary>
public sealed class EmailTests
{
    // Parte local de 64 (o máximo) + "@" + domínio de 189 em rótulos de até 63: o maior endereço que a regra aceita.
    private static string Endereco254() =>
        $"{new string('a', 64)}@{new string('b', 63)}.{new string('c', 63)}.{new string('d', 56)}.test";

    private static string Endereco255() =>
        $"{new string('a', 64)}@{new string('b', 63)}.{new string('c', 63)}.{new string('d', 57)}.test";

    [Theory]
    [InlineData("joao@example.com")]
    [InlineData("joao.silva@example.com")]
    [InlineData("joao+tag@example.com")]
    [InlineData("joao_silva-2@example.com")]
    [InlineData("joao@sub.example.com.br")]
    [InlineData("a@b.co")]
    public void Of_ComEnderecoValido_RetornaSucesso(string entrada)
    {
        Email.Of(entrada).IsSuccess.Should().BeTrue($"'{entrada}' tem forma de endereço");
    }

    [Theory]
    [InlineData("sem-arroba.com")]
    [InlineData("@example.com")]          // sem parte local
    [InlineData("joao@")]                 // sem domínio
    [InlineData("joao@com")]              // domínio sem ponto
    [InlineData("a@b")]                   // o que o EmailAddress() do FluentValidation aceitava
    [InlineData("joao@example.")]         // termina em ponto
    [InlineData("joao@@example.com")]     // dois arrobas
    [InlineData("joao@exa..mple.com")]    // pontos consecutivos no domínio
    [InlineData("jo..ao@example.com")]    // pontos consecutivos na parte local
    [InlineData(".joao@example.com")]     // parte local começa em ponto
    [InlineData("joao.@example.com")]     // parte local termina em ponto
    [InlineData("joao!@example.com")]     // o Keycloak recusa ! no username
    [InlineData("joão@example.com")]      // fora do recorte ASCII
    [InlineData("joao@-example.com")]     // rótulo começa em hífen
    [InlineData("joao@example-.com")]     // rótulo termina em hífen
    [InlineData("joao@exam_ple.com")]     // sublinhado não é DNS
    [InlineData("joao silva@example.com")] // com espaço
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Of_ComEnderecoInvalido_RetornaFalha(string? entrada)
    {
        Email.Of(entrada!).IsFailure.Should().BeTrue($"'{entrada}' não tem forma de endereço aceita");
    }

    [Theory]
    [InlineData("JOAO@EXAMPLE.COM", "joao@example.com")]
    [InlineData("  Joao@Example.com  ", "joao@example.com")]
    public void Of_NormalizaParaMinusculas(string entrada, string esperado)
    {
        Result<Email> resultado = Email.Of(entrada);

        // Sem normalizar, Joao@x.com e joao@x.com seriam endereços distintos — e o mesmo usuário
        // conseguiria se cadastrar duas vezes.
        resultado.Value.Value.Should().Be(esperado);
    }

    [Fact]
    public void Of_ComCaixaEEspacos_Normaliza()
    {
        // Foco de revisão 1: é o valor que vai para a coluna e para a busca exata do Keycloak, que compara em
        // minúsculas. Guardado como veio, o retry não reencontraria o usuário criado na primeira tentativa.
        Email.Of("  Admin@Acme.COM ").Value.Value.Should().Be("admin@acme.com");
    }

    [Fact]
    public void Of_Com254Caracteres_Aceita()
    {
        // Foco de revisão 5: 254 é o tamanho da coluna initial_admin_email e o limite da RFC 5321.
        string endereco = Endereco254();
        endereco.Length.Should().Be(254);

        Email.Of(endereco).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Of_Com255Caracteres_Recusa()
    {
        string endereco = Endereco255();
        endereco.Length.Should().Be(255);

        Email.Of(endereco).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Of_ParteLocalDe64_AceitaEDe65_Recusa()
    {
        // 64 é o limite da RFC 5321 e o do Keycloak (EmailValidationUtil, MAX_LOCAL_PART_LENGTH). Aceitar 65 faria o
        // POST /users responder 400 a cada tentativa do provisionamento, pela janela inteira.
        Email.Of($"{new string('a', 64)}@example.com").IsSuccess.Should().BeTrue();
        Email.Of($"{new string('a', 65)}@example.com").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void ToString_NaoContemOEndereco()
    {
        // D15: um log com {email}, uma interpolação numa mensagem de exceção — o ToString é o caminho acidental.
        Email email = Email.Of("segredo+tag@acme.test").Value;

        email.ToString().Should().NotContain("segredo").And.NotContain("acme");
    }

    [Fact]
    public void ErroDeEnderecoInvalido_NaoEcoaOValor()
    {
        // A mensagem do erro vai para log e para o corpo da resposta 400.
        Result<Email> resultado = Email.Of("segredo@@acme.test");

        resultado.Error.Message.Should().NotContain("segredo");
        resultado.Error.Code.Should().Be("Email.Invalido");
    }

    [Theory]
    [InlineData("joao@example.com", true)]
    [InlineData("a@b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid_ConcordaComOf(string? entrada, bool esperado)
    {
        Email.IsValid(entrada).Should().Be(esperado);
    }

    [Fact]
    public void Equals_IgnorandoCaixa_SaoIguais()
    {
        Email um = Email.Of("joao@example.com").Value;
        Email outro = Email.Of("JOAO@example.com").Value;

        um.Should().Be(outro);
    }
}
