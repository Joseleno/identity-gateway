using IdentityGateway.Api.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// As opções do JwtBearer como o host as resolveu — não como o código parece configurá-las.
/// </summary>
/// <remarks>
/// <b>Em execução, e não por regra estática.</b> <c>IssuerSigningKey</c>, <c>SignatureValidator</c> e os
/// <c>Validate*</c> são propriedades de <c>TokenValidationParameters</c>: o teste de arquitetura enxerga tipos, não
/// membros, e uma regra sobre elas ficaria verde sem conferir nada. Aqui o valor é lido depois de todos os
/// <c>Configure</c> e <c>PostConfigure</c>, que é o que a validação de fato usa.
/// </remarks>
public sealed class OpcoesDoJwtBearerTests(IdentityGatewayApiFactory factory) : IClassFixture<IdentityGatewayApiFactory>
{
    private JwtBearerOptions Resolvidas() => factory.Services
        .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);

    [Fact]
    public void AsChaves_VemSoDosMetadados()
    {
        // Uma chave fixada aqui valeria junto com as do provedor — ou no lugar delas.
        TokenValidationParameters parametros = Resolvidas().TokenValidationParameters;

        parametros.IssuerSigningKey.Should().BeNull();
        (parametros.IssuerSigningKeys ?? []).Should().BeEmpty();
        parametros.SignatureValidator.Should().BeNull();
        parametros.IssuerSigningKeyResolver.Should().BeNull();
        Resolvidas().ConfigurationManager.Should().NotBeNull("as chaves vêm do JWKS anunciado pelos metadados");
    }

    [Fact]
    public void AValidacao_EstaLigadaPorInteiroESoAceitaRs256()
    {
        TokenValidationParameters parametros = Resolvidas().TokenValidationParameters;

        parametros.ValidateIssuer.Should().BeTrue();
        parametros.IssuerValidator.Should().NotBeNull("o ValidIssuer sozinho não restringe");
        parametros.ValidateAudience.Should().BeTrue();
        parametros.ValidAudience.Should().Be("identity-gateway-api");
        parametros.ValidateLifetime.Should().BeTrue();
        parametros.RequireExpirationTime.Should().BeTrue();
        parametros.ValidateIssuerSigningKey.Should().BeTrue();
        parametros.ValidAlgorithms.Should().Equal(SecurityAlgorithms.RsaSha256);
        parametros.ClockSkew.Should().Be(TimeSpan.FromSeconds(30));
        parametros.NameClaimType.Should().Be("sub");
        parametros.RoleClaimType.Should().Be("roles");
    }

    [Fact]
    public void OsMetadados_VemPeloEnderecoDeTransporteComPrazoCurto()
    {
        JwtBearerOptions jwt = Resolvidas();

        jwt.Authority.Should().BeNull("Authority amarraria o endereço dos metadados ao emissor aceito");
        jwt.MetadataAddress.Should().Be(
            $"{factory.Oidc.BaseUrl}/realms/identity-gateway/.well-known/openid-configuration");
        jwt.BackchannelTimeout.Should().Be(ValidacaoDoAccessToken.PrazoDosMetadados).And.Be(TimeSpan.FromSeconds(5));
        jwt.RefreshInterval.Should().Be(TimeSpan.FromSeconds(30));

        // Em Development, com AllowInsecureHttp: é o único caso em que os metadados vêm por http.
        jwt.RequireHttpsMetadata.Should().BeFalse();
    }

    [Fact]
    public void AResposta_NaoDetalhaOErroEOsClaimsChegamComoOEmissorOsEscreveu()
    {
        JwtBearerOptions jwt = Resolvidas();

        jwt.IncludeErrorDetails.Should().BeFalse("o WWW-Authenticate ecoaria o iss e o aud recusados");
        jwt.MapInboundClaims.Should().BeFalse("senão o claim roles vira a URI longa e a policy nunca casa");
    }

    [Fact]
    public async Task HaUmEsquemaDeAutenticacaoSo()
    {
        // Um segundo esquema — cookie, chave de API, outro bearer — seria um segundo caminho para dentro, com outra
        // validação. Se um dia existir, entra por decisão registrada, e este teste é quem pergunta.
        IAuthenticationSchemeProvider esquemas = factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        IEnumerable<AuthenticationScheme> todos = await esquemas.GetAllSchemesAsync();

        todos.Select(esquema => esquema.Name).Should().Equal(JwtBearerDefaults.AuthenticationScheme);
    }

    [Fact]
    public void NinguemTransformaClaimsDepoisDaValidacao()
    {
        // A regra de arquitetura só vê as classes da Api. Esta vê o que o contêiner resolve, venha de onde vier: a
        // implementação que não faz nada, que é a que o AddAuthentication registra.
        using IServiceScope escopo = factory.Services.CreateScope();

        escopo.ServiceProvider.GetRequiredService<IClaimsTransformation>()
            .Should().BeOfType<NoopClaimsTransformation>("os claims vêm só do token (ADR-004)");
    }
}
