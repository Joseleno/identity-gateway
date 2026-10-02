using System.Text.RegularExpressions;

namespace IdentityGateway.ArchitectureTests;

/// <summary>
/// Os apps de arquivo único de <c>tools/</c> seguem as regras de pacote do repositório, e o que eles assumem do
/// ambiente é o que o compose e o README dizem.
/// </summary>
public sealed partial class RegrasDeFerramentasTests
{
    private static string[] Ferramentas()
    {
        string pasta = RaizDoRepositorio.Caminho("tools");

        return Directory.Exists(pasta) ? Directory.GetFiles(pasta, "*.cs") : [];
    }

    [Fact]
    public void FerramentasNaoDeclaramPacotes()
    {
        // Um #:package num app de arquivo único é uma dependência fora do Directory.Packages.props: versão solta, sem
        // a revisão de licença que todo pacote do repositório passa. O que a ferramenta precisa vem pelo #:project.
        Ferramentas().Should().NotBeEmpty("sem ferramentas, a regra passaria vazia");

        foreach (string ferramenta in Ferramentas())
        {
            File.ReadAllText(ferramenta).Should().NotContain("#:package", $"{Path.GetFileName(ferramenta)}");
        }
    }

    [Fact]
    public void AppDaJornadaUsaABibliotecaDoHarnessESemAot()
    {
        // #:project para a biblioteca (um projeto de teste daria dois Main); sem o AOT padrão dos apps de arquivo único,
        // que transformaria o JSON por reflexão em erro de build.
        string app = File.ReadAllText(RaizDoRepositorio.Caminho("tools", "jornada-compose.cs"));

        app.Should().Contain("#:project ../tests/IdentityGateway.Testing.Keycloak");
        app.Should().Contain("#:property PublishAot=false");
    }

    [Fact]
    public void PadraoDoEmailDoPlatformAdminEOMesmoNoComposeNoAppENoReadme()
    {
        // Três lugares escrevem o mesmo padrão. Diferentes, o app procuraria no mailpit um e-mail que o compose não
        // usou — e o README mandaria abrir um convite que não existe.
        string compose = File.ReadAllText(RaizDoRepositorio.Caminho("docker-compose.yml"));
        string app = File.ReadAllText(RaizDoRepositorio.Caminho("tools", "jornada-compose.cs"));
        string readme = File.ReadAllText(RaizDoRepositorio.Caminho("README.md"));

        Match noCompose = PadraoNoCompose().Match(compose);
        Match noApp = PadraoNoApp().Match(app);

        noCompose.Success.Should().BeTrue();
        noApp.Success.Should().BeTrue();

        string email = noCompose.Groups["email"].Value;
        noApp.Groups["email"].Value.Should().Be(email);
        readme.Should().Contain(email);
        email.Should().Be(email.ToLowerInvariant());
    }

    [GeneratedRegex(@"PLATFORM_ADMIN_EMAIL:\s*\$\{PLATFORM_ADMIN_EMAIL:-(?<email>[^}]+)\}")]
    private static partial Regex PadraoNoCompose();

    [GeneratedRegex(@"Ambiente\(""PLATFORM_ADMIN_EMAIL"",\s*""(?<email>[^""]+)""\)")]
    private static partial Regex PadraoNoApp();
}
