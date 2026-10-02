namespace IdentityGateway.Testing.Keycloak;

/// <summary>Um usuário criado para um teste, com a senha que o teste definiu pelo link de ações.</summary>
/// <param name="Id">O <c>sub</c> no Keycloak.</param>
public sealed record UsuarioDeTeste(string Id, string Email, string Senha)
{
    /// <summary>Sem a senha e sem o e-mail: o <c>ToString</c> do record imprimiria os dois em qualquer log.</summary>
    public override string ToString() => $"UsuarioDeTeste({Id})";
}
