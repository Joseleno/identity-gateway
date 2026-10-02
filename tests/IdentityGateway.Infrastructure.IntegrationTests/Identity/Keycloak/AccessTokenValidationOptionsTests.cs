using IdentityGateway.Infrastructure.Configuration;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// A option que a Api lê para validar o access token: derivada do adaptador do Keycloak, validada na subida.
/// </summary>
/// <remarks>
/// Sem Keycloak e sem host: resolve as options pelo mesmo <c>AddKeycloakIdentity</c> da produção, em Development e em
/// Production. Os mesmos ramos são provados com o host de pé, e com pedidos, nos testes funcionais da Api.
/// </remarks>
public sealed class AccessTokenValidationOptionsTests
{
    private static readonly string PemValido = ChavesDeTeste.Gerar().PemPrivado;

    private static ServiceProvider Compor(Dictionary<string, string?> valores, string ambiente)
    {
        IConfiguration configuracao = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();

        ServiceCollection services = new();
        services.ComAmbiente(ambiente);
        services.AddKeycloakIdentity(configuracao);

        return services.BuildServiceProvider();
    }

    private static AccessTokenValidationOptions Resolver(
        Dictionary<string, string?> valores, string ambiente = "Development")
    {
        using ServiceProvider provider = Compor(valores, ambiente);
        return provider.GetRequiredService<IOptions<AccessTokenValidationOptions>>().Value;
    }

    private static Dictionary<string, string?> EmProducao() => new()
    {
        ["Keycloak:Admin:BaseUrl"] = "https://keycloak.interno.test",
        ["Keycloak:Admin:PublicBaseUrl"] = "https://sso.exemplo.test",
        ["Keycloak:Admin:Realm"] = "identity-gateway",
        ["Keycloak:Admin:ClientId"] = "identity-gateway",
        ["Keycloak:Admin:PrivateKeyPem"] = PemValido,
    };

    private static Dictionary<string, string?> DoCompose() => new()
    {
        ["Keycloak:Admin:BaseUrl"] = "http://keycloak:8080",
        ["Keycloak:Admin:PublicBaseUrl"] = "http://localhost:8081",
        ["Keycloak:Admin:Realm"] = "identity-gateway",
        ["Keycloak:Admin:ClientId"] = "identity-gateway",
        ["Keycloak:Admin:PrivateKeyPem"] = PemValido,
        ["Keycloak:Admin:AllowInsecureHttp"] = "true",
        ["Keycloak:Auth:AllowedClients:0"] = "identity-gateway-demo",
    };

    [Fact]
    public void Issuer_ComPublicBaseUrl_EOMesmoTextoDoAudDoAssertion()
    {
        // DT1: uma propriedade só alimenta os dois. Se a Api refizesse a conta, um dia as duas divergiriam — e o
        // sintoma seria "o service account autentica, e todo token de usuário leva 401".
        Dictionary<string, string?> valores = EmProducao();
        using ServiceProvider provider = Compor(valores, "Production");

        AccessTokenValidationOptions validacao = provider.GetRequiredService<IOptions<AccessTokenValidationOptions>>().Value;
        KeycloakAdminOptions admin = provider.GetRequiredService<IOptions<KeycloakAdminOptions>>().Value;

        validacao.Issuer.Should().Be("https://sso.exemplo.test/realms/identity-gateway");
        validacao.Issuer.Should().Be(admin.AssertionAudience);
    }

    [Fact]
    public void Issuer_SemPublicBaseUrl_SaiDoBaseUrlEContinuaIgualAoAud()
    {
        Dictionary<string, string?> valores = EmProducao();
        valores.Remove("Keycloak:Admin:PublicBaseUrl");
        using ServiceProvider provider = Compor(valores, "Production");

        AccessTokenValidationOptions validacao = provider.GetRequiredService<IOptions<AccessTokenValidationOptions>>().Value;
        KeycloakAdminOptions admin = provider.GetRequiredService<IOptions<KeycloakAdminOptions>>().Value;

        validacao.Issuer.Should().Be("https://keycloak.interno.test/realms/identity-gateway");
        validacao.Issuer.Should().Be(admin.AssertionAudience);
    }

    [Fact]
    public void MetadataAddress_SaiDoBaseUrlInternoENuncaDoPublico()
    {
        // O endereço público pode nem resolver de dentro da rede (localhost:8081 visto de dentro do container da api).
        AccessTokenValidationOptions validacao = Resolver(DoCompose());

        validacao.MetadataAddress.Should().Be(
            "http://keycloak:8080/realms/identity-gateway/.well-known/openid-configuration");
        validacao.Issuer.Should().Be("http://localhost:8081/realms/identity-gateway");
    }

    [Fact]
    public void MetadataAddress_PreservaOPrefixoDeCaminhoDoBaseUrl()
    {
        Dictionary<string, string?> valores = EmProducao();
        valores["Keycloak:Admin:BaseUrl"] = "https://keycloak.interno.test/auth/";

        AccessTokenValidationOptions validacao = Resolver(valores, "Production");

        validacao.MetadataAddress.Should().Be(
            "https://keycloak.interno.test/auth/realms/identity-gateway/.well-known/openid-configuration");
    }

    [Fact]
    public void RequireHttpsMetadata_SegueOAllowInsecureHttp()
    {
        // DT5: !IsDevelopment() solto faria a app subir e responder 500 a todo pedido — a checagem do JwtBearer roda
        // no primeiro pedido, não na subida. A regra que já é validada na subida é a do AllowInsecureHttp.
        Resolver(DoCompose()).RequireHttpsMetadata.Should().BeFalse();
        Resolver(EmProducao(), "Production").RequireHttpsMetadata.Should().BeTrue();
    }

    [Fact]
    public void SemSecaoAuth_AudiencePadraoEListaVazia()
    {
        // Fail-closed: sem lista, a API sobe e recusa todo token de usuário (o aviso da subida é da Api).
        AccessTokenValidationOptions validacao = Resolver(EmProducao(), "Production");

        validacao.Audience.Should().Be("identity-gateway-api");
        validacao.AllowedClients.Should().BeEmpty();
    }

    [Fact]
    public void AllowedClients_VemDaConfiguracao()
    {
        Dictionary<string, string?> valores = EmProducao();
        valores["Keycloak:Auth:AllowedClients:0"] = "console-administrativo";
        valores["Keycloak:Auth:AllowedClients:1"] = "portal";

        AccessTokenValidationOptions validacao = Resolver(valores, "Production");

        validacao.AllowedClients.Should().Equal("console-administrativo", "portal");
    }

    [Fact]
    public void ClientDeDemonstracaoEmDevelopment_Aceito()
    {
        Resolver(DoCompose()).AllowedClients.Should().Equal("identity-gateway-demo");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void ClientDeDemonstracaoForaDeDevelopment_FalhaAoValidarComMensagemNeutra(string ambiente)
    {
        // DT4: o client público de device flow é o vetor clássico de phishing de código de dispositivo. "Só no arquivo
        // de Development" não é garantia — toda a suíte roda em Development, e o IConfiguration mescla arrays por
        // índice. A garantia é a subida recusar.
        Dictionary<string, string?> valores = EmProducao();
        valores["Keycloak:Auth:AllowedClients:0"] = "console-administrativo";
        valores["Keycloak:Auth:AllowedClients:1"] = "identity-gateway-demo";

        Action validar = () => Resolver(valores, ambiente);

        validar.Should().Throw<OptionsValidationException>()
            .WithMessage("*AllowedClients*Development*")
            .Which.Message.Should().NotContain("console-administrativo");
    }

    [Fact]
    public void ItemVazioNaLista_FalhaAoValidar()
    {
        // Um item em branco casaria com um azp em branco.
        Dictionary<string, string?> valores = EmProducao();
        valores["Keycloak:Auth:AllowedClients:0"] = " ";

        Action validar = () => Resolver(valores, "Production");

        validar.Should().Throw<OptionsValidationException>().WithMessage("*AllowedClients*");
    }

    [Fact]
    public void AudienceVazia_FalhaAoValidar()
    {
        Dictionary<string, string?> valores = EmProducao();
        valores["Keycloak:Auth:Audience"] = "";

        Action validar = () => Resolver(valores, "Production");

        validar.Should().Throw<OptionsValidationException>().WithMessage("*Audience*");
    }

    [Fact]
    public void IssuerEMetadadosNaConfiguracao_SaoIgnorados()
    {
        // Os três são derivados, com setter interno: ninguém aponta a validação para outro emissor por configuração.
        Dictionary<string, string?> valores = EmProducao();
        valores["Keycloak:Auth:Issuer"] = "https://atacante.test/realms/x";
        valores["Keycloak:Auth:MetadataAddress"] = "https://atacante.test/.well-known/openid-configuration";
        valores["Keycloak:Auth:RequireHttpsMetadata"] = "false";

        AccessTokenValidationOptions validacao = Resolver(valores, "Production");

        validacao.Issuer.Should().Be("https://sso.exemplo.test/realms/identity-gateway");
        validacao.MetadataAddress.Should().StartWith("https://keycloak.interno.test/");
        validacao.RequireHttpsMetadata.Should().BeTrue();
    }
}
