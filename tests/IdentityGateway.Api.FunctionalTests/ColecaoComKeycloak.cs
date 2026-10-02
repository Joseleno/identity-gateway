namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// As classes de teste que usam a Api contra o Keycloak real: uma factory para todas, uma classe de cada vez.
/// </summary>
[CollectionDefinition(Nome)]
public sealed class ColecaoComKeycloak : ICollectionFixture<ApiComKeycloakFactory>
{
    public const string Nome = "Api com Keycloak real";
}
