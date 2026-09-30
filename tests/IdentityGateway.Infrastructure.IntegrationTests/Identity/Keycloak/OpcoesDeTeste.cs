using IdentityGateway.Infrastructure.Identity.Keycloak;
using Microsoft.Extensions.Options;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>Options do Keycloak para testes que montam as peças à mão, sem contêiner de DI.</summary>
internal static class OpcoesDeTeste
{
    public static IOptions<KeycloakAdminOptions> Keycloak(
        string baseUrl = "http://keycloak.test:8080", string? pem = null, string? publicBaseUrl = null) =>
        Options.Create(new KeycloakAdminOptions
        {
            BaseUrl = baseUrl,
            PublicBaseUrl = publicBaseUrl,
            Realm = "identity-gateway",
            ClientId = "identity-gateway",
            PrivateKeyPem = pem ?? ChavesDeTeste.Gerar().PemPrivado,
            AllowInsecureHttp = true,
        });
}
