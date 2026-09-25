namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// A <c>OrganizationRepresentation</c> da Admin API, só com os campos usados.
/// </summary>
/// <remarks>
/// <see cref="Name"/> recebe o <b>slug</b>, não o nome de exibição: <c>name</c> é único no realm, e o nome do tenant
/// não é. O nome de exibição vai em <see cref="Description"/> (até 4000 caracteres, devolvida em toda leitura).
/// </remarks>
internal sealed record OrganizationRepresentation(
    string? Id,
    string Name,
    string Alias,
    string? Description,
    bool Enabled,
    Dictionary<string, List<string>>? Attributes);

/// <summary>O Keycloak respondeu 409 ao criar: <c>name</c> ou <c>alias</c> já em uso.</summary>
/// <remarks>Interna: nunca sai do adaptador. Ou vira corrida resolvida, ou vira inconsistência.</remarks>
internal sealed class KeycloakConflictException : Exception
{
    public KeycloakConflictException()
    {
    }

    public KeycloakConflictException(string message)
        : base(message)
    {
    }

    public KeycloakConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
