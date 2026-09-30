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

    private static KeycloakAdminOptions Resolver(Dictionary<string, string?> valores, string ambiente = "Development")
    {
        IConfiguration configuracao = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();

        ServiceCollection services = new();
        services.ComAmbiente(ambiente);
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
    public void ConfiguracaoCompleta_SemPublicBaseUrl_AudEOTransporteSaemDoBaseUrl()
    {
        // Omitido o PublicBaseUrl, nada muda para quem roda a API pela IDE com BaseUrl=http://localhost:8081.
        KeycloakAdminOptions opcoes = Resolver(Validos());

        opcoes.AssertionAudience.Should().Be("https://sso.exemplo.test/realms/identity-gateway");
        opcoes.TokenEndpoint.Should().Be("https://sso.exemplo.test/realms/identity-gateway/protocol/openid-connect/token");
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

        opcoes.AssertionAudience.Should().Be("https://sso.exemplo.test/auth/realms/identity-gateway");
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
    public void PemDeChavePublica_FalhaAoValidarSemExporOValor()
    {
        // RSA.ImportFromPem aceita "PUBLIC KEY" sem reclamar — sem a checagem de partes privadas, isto passaria
        // aqui e só quebraria na primeira assinatura, com o ValidateOnStart já tendo deixado a aplicação subir.
        string pemPublico = ChavesDeTeste.Gerar().Rsa.ExportSubjectPublicKeyInfoPem();
        Dictionary<string, string?> valores = Validos();
        valores["Keycloak:Admin:PrivateKeyPem"] = pemPublico;

        Action resolver = () => Resolver(valores);

        resolver.Should().Throw<OptionsValidationException>()
            .WithMessage("*chave privada*")
            .Which.Message.Should().NotContain(pemPublico);
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

        opcoes.AssertionAudience.Should().Be("http://keycloak:8080/realms/identity-gateway");
    }

    [Fact]
    public void ComPublicBaseUrl_AudUsaOPublicoEOTransporteContinuaNoBaseUrl()
    {
        // D8: com KC_HOSTNAME, o emissor é o endereço público, e o aud é comparado por texto com ele. O token endpoint
        // e a Admin API continuam no endereço interno — de dentro do container, localhost:8081 não é o Keycloak.
        Dictionary<string, string?> valores = Validos();
        valores["Keycloak:Admin:BaseUrl"] = "http://keycloak:8080";
        valores["Keycloak:Admin:AllowInsecureHttp"] = "true";
        valores["Keycloak:Admin:PublicBaseUrl"] = "http://localhost:8081/";

        KeycloakAdminOptions opcoes = Resolver(valores);

        opcoes.AssertionAudience.Should().Be("http://localhost:8081/realms/identity-gateway");
        opcoes.TokenEndpoint.Should().Be("http://keycloak:8080/realms/identity-gateway/protocol/openid-connect/token");
        opcoes.AdminBaseAddress.Should().Be(new Uri("http://keycloak:8080/"));
    }

    [Theory]
    [InlineData("localhost:8081")]
    [InlineData("http://localhost:8081/?x=1")]
    [InlineData("http://localhost:8081/#frag")]
    [InlineData("ftp://localhost:8081")]
    public void PublicBaseUrlMalFormado_FalhaAoValidar(string publico)
    {
        Dictionary<string, string?> valores = Validos();
        valores["Keycloak:Admin:PublicBaseUrl"] = publico;

        Action resolver = () => Resolver(valores);

        resolver.Should().Throw<OptionsValidationException>().WithMessage("*PublicBaseUrl*");
    }

    [Fact]
    public void AllowInsecureHttpForaDeDevelopment_FalhaAoValidar()
    {
        // "Transporte interno" não pode virar convite a http em produção com um bearer de manage-users.
        Dictionary<string, string?> valores = Validos();
        valores["Keycloak:Admin:BaseUrl"] = "http://keycloak:8080";
        valores["Keycloak:Admin:AllowInsecureHttp"] = "true";

        Action resolver = () => Resolver(valores, ambiente: "Production");

        resolver.Should().Throw<OptionsValidationException>().WithMessage("*Development*");
    }

    [Fact]
    public void PublicBaseUrlHttpForaDeDevelopment_FalhaAoValidar()
    {
        Dictionary<string, string?> valores = Validos();
        valores["Keycloak:Admin:PublicBaseUrl"] = "http://sso.exemplo.test";

        Action resolver = () => Resolver(valores, ambiente: "Production");

        resolver.Should().Throw<OptionsValidationException>().WithMessage("*Development*");
    }

    [Fact]
    public void HttpsEmProducao_Aceita()
    {
        // Controle positivo das duas regras acima: sem ele, "Production recusa tudo" também passaria.
        Dictionary<string, string?> valores = Validos();
        valores["Keycloak:Admin:PublicBaseUrl"] = "https://sso.exemplo.test";

        KeycloakAdminOptions opcoes = Resolver(valores, ambiente: "Production");

        opcoes.AssertionAudience.Should().Be("https://sso.exemplo.test/realms/identity-gateway");
    }
}
