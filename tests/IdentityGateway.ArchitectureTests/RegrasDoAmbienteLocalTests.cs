using System.Text.Json;
using System.Text.RegularExpressions;

namespace IdentityGateway.ArchitectureTests;

/// <summary>
/// O ambiente de desenvolvimento (IDE e compose) não expõe o e-mail do admin, e o compose é coerente consigo mesmo.
/// </summary>
/// <remarks>
/// Lê os arquivos, como <c>RegrasDoRealmTests</c> lê o realm: são configuração versionada, e um valor errado neles
/// não quebra build nem teste de comportamento — só aparece num log de desenvolvimento, que é público na prática.
/// </remarks>
public sealed partial class RegrasDoAmbienteLocalTests
{
    [Fact]
    public void Development_NaoRegistraDadosSensiveisDoEf()
    {
        // D15, "inclusive em Development": com o log de dados sensíveis ligado, o EF registra o parâmetro do INSERT do
        // e-mail e, no DetectChanges, o valor antigo da coluna ao apagá-la.
        using var desenvolvimento = JsonDocument.Parse(File.ReadAllText(
            RaizDoRepositorio.Caminho("src", "IdentityGateway.Api", "appsettings.Development.json")));
        JsonElement raiz = desenvolvimento.RootElement;

        raiz.GetProperty("Database").GetProperty("EnableSensitiveDataLogging").GetBoolean().Should().BeFalse();
        raiz.GetProperty("Serilog").GetProperty("MinimumLevel").GetProperty("Override")
            .GetProperty("Microsoft.EntityFrameworkCore").GetString().Should().Be("Warning");
    }

    [Fact]
    public void ComposeTemKcHostnameIgualAoPublicBaseUrl()
    {
        // A porta 8081 aparece em três lugares: a publicada, o KC_HOSTNAME e o PublicBaseUrl. Os dois últimos precisam
        // ser o mesmo texto — o aud é comparado por igualdade exata com o emissor que o KC_HOSTNAME define. Sem o
        // KC_HOSTNAME, o link do e-mail sai com o host interno (keycloak:8080).
        string? hostname = ValorNoCompose("KC_HOSTNAME");
        string? publico = ValorNoCompose("Keycloak__Admin__PublicBaseUrl");

        hostname.Should().NotBeNull("sem KC_HOSTNAME o link do convite sai com http://keycloak:8080");
        publico.Should().Be(hostname);
        ValorNoCompose("KC_HOSTNAME_BACKCHANNEL_DYNAMIC").Should().Be("true",
            "sem o backchannel dinâmico, a api não conseguiria falar com o Keycloak pelo nome do serviço");
    }

    [Fact]
    public void ComposeEFixtureUsamAMesmaTagDoMailpit()
    {
        Match imagem = ImagemDoMailpit().Match(Compose());

        imagem.Success.Should().BeTrue("o compose precisa do serviço mailpit com a versão fixada");
        Fixture().Should().Contain($"ImagemDoMailpit = \"{imagem.Groups["imagem"].Value}\"",
            "o teste de integração precisa provar o mesmo mailpit que o compose sobe");
    }

    [Fact]
    public void ComposeEFixtureUsamAMesmaTagDoKeycloak()
    {
        // Os fatos do Keycloak que o projeto usa foram verificados numa tag: testar contra uma e subir outra deixaria
        // o compose sem prova. Todos os serviços do compose que usam a imagem precisam da mesma tag que o fixture.
        string[] imagens =
        [
            .. ImagensDoKeycloak().Matches(Compose()).Select(achado => achado.Groups["imagem"].Value).Distinct(),
        ];

        imagens.Should().ContainSingle("o compose usa uma tag só do Keycloak");
        Fixture().Should().Contain($"ImagemDoKeycloak = \"{imagens[0]}\"",
            "o teste de integração precisa provar o mesmo Keycloak que o compose sobe");
    }

    [Fact]
    public void DependenciasComDadoPessoalPublicamSoEmLocalhost()
    {
        // Agora há dado pessoal no banco (o e-mail do admin até a ativação) e, em falha, nos logs; e os e-mails do
        // mailpit trazem links que trocam senha. Nada disso fica exposto à rede local.
        string compose = Compose();

        compose.Should().Contain("\"127.0.0.1:5432:5432\"")
            .And.Contain("\"127.0.0.1:6379:6379\"")
            .And.Contain("\"127.0.0.1:5341:80\"")
            .And.Contain("\"127.0.0.1:8025:8025\"");
        compose.Should().NotContain("\"5432:5432\"").And.NotContain("\"6379:6379\"").And.NotContain("\"5341:80\"");
        compose.Should().NotContain(":1025\"", "o SMTP do mailpit só existe na rede do compose");
    }

    private static string Compose() => File.ReadAllText(RaizDoRepositorio.Caminho("docker-compose.yml"));

    private static string Fixture() => File.ReadAllText(
        RaizDoRepositorio.Caminho("tests", "IdentityGateway.Testing.Keycloak", "KeycloakFixture.cs"));

    /// <summary>
    /// Valor de uma variável de ambiente do compose (<c>CHAVE: valor</c> ou <c>CHAVE: "valor"</c>, numa linha só).
    /// </summary>
    /// <remarks>
    /// <c>SingleOrDefault</c>, e não o primeiro achado: a mesma chave em dois serviços deixaria a regra ambígua, e o
    /// teste precisa dizer isso em vez de conferir um dos dois por acaso.
    /// </remarks>
    private static string? ValorNoCompose(string chave) =>
        VariavelDoCompose().Matches(Compose())
            .Where(achado => achado.Groups["chave"].Value == chave)
            .Select(achado => achado.Groups["valor"].Value)
            .SingleOrDefault();

    [GeneratedRegex(@"^\s*(?<chave>[A-Za-z_][A-Za-z0-9_]*):\s*""?(?<valor>[^""\s]+)""?\s*$", RegexOptions.Multiline)]
    private static partial Regex VariavelDoCompose();

    [GeneratedRegex(@"^\s*image:\s*(?<imagem>axllent/mailpit:\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex ImagemDoMailpit();

    [GeneratedRegex(@"^\s*image:\s*(?<imagem>quay\.io/keycloak/keycloak:\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex ImagensDoKeycloak();
}
