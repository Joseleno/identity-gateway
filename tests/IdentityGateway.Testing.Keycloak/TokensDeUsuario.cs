namespace IdentityGateway.Testing.Keycloak;

/// <summary>O par de tokens de um login. O <c>ToString</c> não os revela.</summary>
public sealed record TokensDeUsuario(string AccessToken, string RefreshToken, TimeSpan ExpiraEm)
{
    public override string ToString() => $"TokensDeUsuario(expira em {ExpiraEm.TotalSeconds:0}s)";
}
