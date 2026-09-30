namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// A <c>UserRepresentation</c> da Admin API, só com os campos usados.
/// </summary>
/// <remarks>
/// Nulos ficam fora do JSON (ver <c>KeycloakAdminClient</c>): o Keycloak interpreta campo presente como intenção, e um
/// <c>"attributes": null</c> numa atualização apagaria os atributos.
/// </remarks>
internal sealed record UserRepresentation(
    string? Id,
    string? Username,
    string? Email,
    bool? Enabled,
    List<string>? RequiredActions,
    Dictionary<string, List<string>>? Attributes);

/// <summary>A <c>RoleRepresentation</c> da Admin API: atribuir papel exige o id, não só o nome.</summary>
internal sealed record RoleRepresentation(string Id, string Name);

/// <summary>O Keycloak respondeu 400: a requisição não serve para o estado atual do recurso.</summary>
/// <remarks>Interna: nunca sai do adaptador. O <c>KeycloakIdentityProvider</c> a traduz em inconsistência.</remarks>
internal sealed class KeycloakBadRequestException : Exception
{
    public KeycloakBadRequestException()
    {
    }

    public KeycloakBadRequestException(string message)
        : base(message)
    {
    }

    public KeycloakBadRequestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
