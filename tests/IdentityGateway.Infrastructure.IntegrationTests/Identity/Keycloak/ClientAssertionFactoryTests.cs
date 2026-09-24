using System.Security.Cryptography;
using System.Text.Json;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O assertion do <c>private_key_jwt</c> tem exatamente a forma que o Keycloak 26.7 aceita — e nada que ele recuse
/// em silêncio.
/// </summary>
public sealed class ClientAssertionFactoryTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    // Issuer LITERAL, e não recalculado pela fórmula do código: um teste que reusa a fórmula aprova a fórmula
    // errada. A BaseUrl termina em barra de propósito — é o caso que produziria "//realms".
    private const string IssuerEsperado = "http://keycloak.test:8080/realms/identity-gateway";

    private readonly ParDeChaves _chaves = ChavesDeTeste.Gerar();

    private ClientAssertionFactory CriarFabrica()
    {
        IOptions<KeycloakAdminOptions> opcoes =
            OpcoesDeTeste.Keycloak("http://keycloak.test:8080/", _chaves.PemPrivado);
        IDateTimeProvider relogio = Substitute.For<IDateTimeProvider>();
        relogio.UtcNow.Returns(Agora);

        return new ClientAssertionFactory(new GatewaySigningKey(opcoes), opcoes, relogio);
    }

    private static JsonElement Payload(string assertion)
    {
        JsonWebToken token = new(assertion);
        return JsonDocument.Parse(Base64UrlEncoder.Decode(token.EncodedPayload)).RootElement.Clone();
    }

    [Fact]
    public void Criar_AudEStringUnicaIgualAoIssuer()
    {
        JsonElement payload = Payload(CriarFabrica().Criar());

        // String, e não array: o Keycloak 26.2+ recusa aud com mais de um valor, e um array de um elemento só está
        // a uma linha de virar dois.
        payload.GetProperty("aud").ValueKind.Should().Be(JsonValueKind.String);
        payload.GetProperty("aud").GetString().Should().Be(IssuerEsperado);
    }

    [Fact]
    public void Criar_IssESubSaoOClientId()
    {
        JsonElement payload = Payload(CriarFabrica().Criar());

        payload.GetProperty("iss").GetString().Should().Be("identity-gateway");
        payload.GetProperty("sub").GetString().Should().Be("identity-gateway");
    }

    [Fact]
    public void Criar_VidaDeSessentaSegundosAPartirDoRelogio()
    {
        // O padrão do JsonWebTokenHandler é 60 MINUTOS. O Keycloak aceitaria — ele só confere a idade pelo iat —, e
        // um assertion vazado valeria uma hora contra qualquer outro verificador.
        JsonElement payload = Payload(CriarFabrica().Criar());

        long iat = payload.GetProperty("iat").GetInt64();
        payload.GetProperty("exp").GetInt64().Should().Be(iat + 60);
        payload.GetProperty("nbf").GetInt64().Should().Be(iat);
        iat.Should().Be(Agora.ToUnixTimeSeconds());
    }

    [Fact]
    public void Criar_JtiNovoACadaChamada()
    {
        ClientAssertionFactory fabrica = CriarFabrica();

        string primeiro = Payload(fabrica.Criar()).GetProperty("jti").GetString()!;
        string segundo = Payload(fabrica.Criar()).GetProperty("jti").GetString()!;

        // O Keycloak guarda o jti num cache de uso único: repetir é "Token reuse detected".
        primeiro.Should().NotBe(segundo);
    }

    [Fact]
    public void Criar_HeaderSemKidEComPs256()
    {
        JsonWebToken token = new(CriarFabrica().Criar());

        // Com kid no header, o Keycloak exige que ele bata com o SHA-256 da chave pública; o .NET, com
        // X509SecurityKey, poria o thumbprint SHA-1. Sem kid, o Keycloak usa o certificado padrão do client.
        token.TryGetHeaderValue("kid", out string _).Should().BeFalse();
        token.Alg.Should().Be("PS256");
    }

    [Fact]
    public async Task Criar_AssinaturaConfereComAChavePublica()
    {
        using var publica = RSA.Create();
        publica.ImportParameters(_chaves.Rsa.ExportParameters(includePrivateParameters: false));

        TokenValidationResult resultado = await new JsonWebTokenHandler().ValidateTokenAsync(
            CriarFabrica().Criar(),
            new TokenValidationParameters
            {
                IssuerSigningKey = new RsaSecurityKey(publica),
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = false,
            });

        resultado.IsValid.Should().BeTrue(resultado.Exception?.Message);
    }
}
