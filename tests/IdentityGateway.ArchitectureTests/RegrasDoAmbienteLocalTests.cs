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
    private static readonly string[] PortasEmLocalhost =
    [
        "127.0.0.1:8080:8080", "127.0.0.1:8081:8080", "127.0.0.1:8025:8025", "127.0.0.1:5432:5432",
        "127.0.0.1:6379:6379", "127.0.0.1:5341:80", "127.0.0.1:16686:16686", "127.0.0.1:4317:4317",
    ];

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
        // Há dado pessoal no banco (o e-mail do admin até a ativação) e, em falha, nos logs; os e-mails do mailpit
        // trazem links que trocam senha; e, desde os tokens do Keycloak, circulam access tokens de verdade pela api e
        // URLs de chamadas pelos traces do Jaeger. Nada disso fica exposto à rede local.
        string compose = Compose();

        // O conjunto EXATO das portas publicadas, lido de toda lista `ports:`. Uma porta nova reprova, e uma destas
        // sem o 127.0.0.1 também — com aspas, sem aspas ou em qualquer outra forma. Publicar uma porta a mais passa a
        // ser uma decisão que mexe neste teste.
        PortasPublicadas(compose).Should().BeEquivalentTo(PortasEmLocalhost);
        compose.Should().NotContain(":1025\"", "o SMTP do mailpit só existe na rede do compose");
    }

    [Fact]
    public void ListaDeClientsPermitidosSoNoAppsettingsDeDevelopment()
    {
        // DT4: o IConfiguration mescla arrays POR ÍNDICE. Uma lista no appsettings.json base sobreviveria, do segundo
        // item em diante, à configuração de produção. A lista de azp vive só no arquivo de Development.
        using var baseDaApi = JsonDocument.Parse(File.ReadAllText(
            RaizDoRepositorio.Caminho("src", "IdentityGateway.Api", "appsettings.json")));
        using var desenvolvimento = JsonDocument.Parse(File.ReadAllText(
            RaizDoRepositorio.Caminho("src", "IdentityGateway.Api", "appsettings.Development.json")));

        baseDaApi.RootElement.GetProperty("Keycloak").TryGetProperty("Auth", out _).Should().BeFalse(
            "nem a lista, nem a seção: fora de Development a lista vem do ambiente");
        desenvolvimento.RootElement.GetProperty("Keycloak").GetProperty("Auth").GetProperty("AllowedClients")
            .EnumerateArray().Select(client => client.GetString())
            .Should().Equal("identity-gateway-demo");
    }

    [Fact]
    public void ComposeNaoTemMaisAChaveJwt()
    {
        // A chave simétrica de desenvolvimento era publicada aqui, no README e na CI. Com os tokens do Keycloak, ela
        // deixa de existir — e uma variável Jwt__* de volta seria o primeiro sinal de que o HS256 voltou.
        Compose().Should().NotContain("Jwt__");
    }

    [Fact]
    public void ApiSoSobeDepoisDoConviteDoPlatformAdmin()
    {
        // O platform-admin-invite também confere que o realm é o desta versão. Sem a dependência, um volume antigo
        // deixaria a api subir e responder 401 a todo token, sem dizer por quê.
        DependenciaDaApiNoConvite().IsMatch(Compose()).Should().BeTrue(
            "a api depende de platform-admin-invite com service_completed_successfully");
    }

    [Fact]
    public void EntrypointDoKeycloakRecusaEmailVazioOuComMaiusculas()
    {
        // Foco de revisão 3. Vazio, o import gravaria o placeholder literal como e-mail. Com maiúsculas, o Keycloak
        // gravaria o e-mail em minúsculas — outro texto, que o platform-admin-invite procuraria em vão.
        string compose = Compose();

        compose.Should().Contain("test -n \"$$PLATFORM_ADMIN_EMAIL\"");
        compose.Should().Contain(
            "test \"$$PLATFORM_ADMIN_EMAIL\" = \"$$(printf '%s' \"$$PLATFORM_ADMIN_EMAIL\" | tr '[:upper:]' '[:lower:]')\"");
    }

    [Fact]
    public void PadraoDoEmailDoPlatformAdminEMinusculo()
    {
        Match padrao = PadraoDoPlatformAdmin().Match(Compose());

        padrao.Success.Should().BeTrue("o compose define PLATFORM_ADMIN_EMAIL com um padrão");
        string email = padrao.Groups["email"].Value;
        email.Should().Be(email.ToLowerInvariant()).And.Contain("@");
    }

    [Fact]
    public void ConviteDoPlatformAdminNaoRecebeSenhaPorVariavelNemAtribuiPapel()
    {
        // A senha do master é lida do arquivo dentro do sh: declarada como variável do serviço, ficaria no docker
        // inspect. E o one-shot só envia e-mail — quem "garantia o papel" em runtime promoveu um tenant-admin a
        // platform-admin (visto ao vivo no design): atribuir papel não é trabalho dele.
        string compose = Compose();

        compose.Should().NotContain("KC_CLI_PASSWORD:").And.NotContain("KC_BOOTSTRAP_ADMIN_PASSWORD:");
        compose.Should().NotContain("add-roles").And.NotContain("role-mappings/realm\" -");
        compose.Should().Contain("lifespan=14400", "o link do platform-admin vale 4 h");
        CamposComAttributes().IsMatch(compose).Should().BeFalse(
            "com --fields (attributes sozinho ou numa lista, como id,attributes), o objeto vem vazio e as travas ficam vacuosas");
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

    /// <summary>
    /// Todo item de toda lista <c>ports:</c> do compose, sem as aspas e sem o comentário do fim da linha.
    /// </summary>
    /// <remarks>
    /// Lê as listas, e não um padrão de porta: <c>- "8080"</c>, <c>- '8080:8080'</c>, <c>- "[::]:8080:8080"</c>, a forma
    /// longa (<c>- target: 80</c>) e a lista numa linha só (<c>ports: [...]</c>) também são itens — e nenhum deles é
    /// igual a uma das portas esperadas.
    /// </remarks>
    private static List<string> PortasPublicadas(string compose)
    {
        List<string> portas = [];
        bool emPortas = false;

        foreach (string linha in compose.Split('\n'))
        {
            string texto = linha.Trim();

            if (texto.StartsWith("ports:", StringComparison.Ordinal))
            {
                emPortas = true;
                string naMesmaLinha = texto["ports:".Length..].Trim();

                if (naMesmaLinha.Length > 0 && !naMesmaLinha.StartsWith('#'))
                {
                    portas.Add(naMesmaLinha);
                }
            }
            else if (emPortas && texto.StartsWith("- ", StringComparison.Ordinal))
            {
                portas.Add(ItemDeLista().Match(texto).Groups["valor"].Value);
            }
            else if (emPortas && texto.Length > 0 && !texto.StartsWith('#'))
            {
                emPortas = false;
            }
        }

        return portas;
    }

    [GeneratedRegex(@"^-\s*[""']?(?<valor>[^""'#\s]*)")]
    private static partial Regex ItemDeLista();

    // Dentro do serviço api, e dentro do depends_on dele: os dois trechos "qualquer coisa" param na primeira linha
    // que abre outro serviço (dois espaços) ou outra chave do serviço (quatro). Sem isso, a dependência declarada num
    // serviço que viesse depois da api satisfaria a regra.
    [GeneratedRegex(
        @"^  api:\s*$(?:(?!^  \S).)*?^    depends_on:\s*$(?:(?!^    \S).)*?^      platform-admin-invite:\s*\n\s+condition:\s*service_completed_successfully",
        RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex DependenciaDaApiNoConvite();

    [GeneratedRegex(@"--fields\s+\S*attributes")]
    private static partial Regex CamposComAttributes();

    [GeneratedRegex(@"PLATFORM_ADMIN_EMAIL:\s*\$\{PLATFORM_ADMIN_EMAIL:-(?<email>[^}]+)\}")]
    private static partial Regex PadraoDoPlatformAdmin();

    [GeneratedRegex(@"^\s*image:\s*(?<imagem>axllent/mailpit:\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex ImagemDoMailpit();

    [GeneratedRegex(@"^\s*image:\s*(?<imagem>quay\.io/keycloak/keycloak:\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex ImagensDoKeycloak();
}
