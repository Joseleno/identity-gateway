using System.Security.Cryptography;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// A configuração do Keycloak é barrada na subida quando está incompleta, ambígua ou insegura.
/// </summary>
public sealed class KeycloakAdminOptionsTests
{
    private static readonly string PemValido = ChavesDeTeste.Gerar().PemPrivado;

    private static KeycloakAdminOptions Resolver(Dictionary<string, string?> valores)
    {
        IConfiguration configuracao = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();

        ServiceCollection services = new();
        services.AddKeycloakIdentity(configuracao);

        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<KeycloakAdminOptions>>().Value;
    }

    private static Dictionary<string, string?> Validos() => new()
    {
        ["Keycloak:Admin:BaseUrl"] = "https://sso.exemplo.test",
        ["Keycloak:Admin:Realm"] = "identity-gateway",
        ["Keycloak:Admin:ClientId"] = "identity-gateway",
        ["Keycloak:Admin:PrivateKeyPem"] = PemValido,
    };

    [Fact]
    public void ConfiguracaoCompleta_ExpoeIssuerEEnderecoBase()
    {
        KeycloakAdminOptions opcoes = Resolver(Validos());

        opcoes.Issuer.Should().Be("https://sso.exemplo.test/realms/identity-gateway");
        opcoes.AdminBaseAddress.Should().Be(new Uri("https://sso.exemplo.test/"));
    }

    [Fact]
    public void BaseUrlComPrefixoDeCaminhoEBarraFinal_PreservaOPrefixo()
    {
        // Keycloak atrás de proxy reverso em /auth: perder o prefixo mandaria o assertion e a Admin API para a
        // raiz do host, e o resultado seria 404 — ou pior, o issuer errado e invalid_client.
        Dictionary<string, string?> valores = Validos();
        valores["Keycloak:Admin:BaseUrl"] = "https://sso.exemplo.test/auth/";

        KeycloakAdminOptions opcoes = Resolver(valores);

        opcoes.Issuer.Should().Be("https://sso.exemplo.test/auth/realms/identity-gateway");
        opcoes.AdminBaseAddress.Should().Be(new Uri("https://sso.exemplo.test/auth/"));
    }

    [Fact]
    public void SemASecao_FalhaAoValidar()
    {
        Action resolver = () => Resolver([]);

        resolver.Should().Throw<OptionsValidationException>().WithMessage("*BaseUrl*");
    }

    [Fact]
    public void SemNenhumaChave_FalhaAoValidar()
    {
        Dictionary<string, string?> valores = Validos();
        valores.Remove("Keycloak:Admin:PrivateKeyPem");

        Action resolver = () => Resolver(valores);

        resolver.Should().Throw<OptionsValidationException>().WithMessage("*exatamente um*");
    }

    [Fact]
    public void ComAsDuasChaves_FalhaAoValidar()
    {
        // Duas fontes para o mesmo segredo: qual vence seria detalhe de implementação, e quem trocou a chave num
        // lugar continuaria usando a do outro sem saber.
        Dictionary<string, string?> valores = Validos();
        valores["Keycloak:Admin:PrivateKeyPath"] = "/keys/private.pem";

        Action resolver = () => Resolver(valores);

        resolver.Should().Throw<OptionsValidationException>().WithMessage("*exatamente um*");
    }

    [Fact]
    public void ArquivoDeChaveInexistente_FalhaAoValidarSemExporOCaminhoNemOValor()
    {
        Dictionary<string, string?> valores = Validos();
        valores.Remove("Keycloak:Admin:PrivateKeyPem");
        valores["Keycloak:Admin:PrivateKeyPath"] = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pem");

        Action resolver = () => Resolver(valores);

        resolver.Should().Throw<OptionsValidationException>()
            .WithMessage("*chave privada*")
            .Which.Message.Should().NotContain(valores["Keycloak:Admin:PrivateKeyPath"]);
    }

    [Fact]
    public void PemInvalido_FalhaAoValidarSemExporOValor()
    {
        Dictionary<string, string?> valores = Validos();
        valores["Keycloak:Admin:PrivateKeyPem"] = "-----BEGIN PRIVATE KEY-----\nisto-nao-e-chave\n-----END PRIVATE KEY-----";

        Action resolver = () => Resolver(valores);

        resolver.Should().Throw<OptionsValidationException>()
            .WithMessage("*chave privada*")
            .Which.Message.Should().NotContain("isto-nao-e-chave");
    }

    [Fact]
    public void PemComQuebrasDeLinhaDoWindows_Carrega()
    {
        // Chave colada nos user-secrets no Windows chega com CRLF. Precisa carregar igual.
        Dictionary<string, string?> valores = Validos();
        valores["Keycloak:Admin:PrivateKeyPem"] = PemValido.ReplaceLineEndings("\r\n");

        KeycloakAdminOptions opcoes = Resolver(valores);

        using RSA chave = GatewaySigningKey.Carregar(opcoes);
        chave.KeySize.Should().Be(2048);
    }

    [Fact]
    public void HttpSemPermissao_FalhaAoValidar()
    {
        Dictionary<string, string?> valores = Validos();
        valores["Keycloak:Admin:BaseUrl"] = "http://keycloak:8080";

        Action resolver = () => Resolver(valores);

        resolver.Should().Throw<OptionsValidationException>().WithMessage("*https*");
    }

    [Fact]
    public void HttpComPermissaoDeDesenvolvimento_Aceita()
    {
        Dictionary<string, string?> valores = Validos();
        valores["Keycloak:Admin:BaseUrl"] = "http://keycloak:8080";
        valores["Keycloak:Admin:AllowInsecureHttp"] = "true";

        KeycloakAdminOptions opcoes = Resolver(valores);

        opcoes.Issuer.Should().Be("http://keycloak:8080/realms/identity-gateway");
    }
}
