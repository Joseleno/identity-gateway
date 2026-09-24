namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// Obtém um token do service account. Existe como interface para o cache poder ser testado sem HTTP.
/// </summary>
internal interface ITokenEndpoint
{
    /// <summary>Pede um token novo ao Keycloak. Cada chamada é uma tentativa, com assertion próprio.</summary>
    /// <exception cref="HttpRequestException">O Keycloak recusou ou não respondeu.</exception>
    Task<TokenObtido> ObterAsync(CancellationToken cancellationToken);
}

/// <summary>O access token e por quanto tempo ele vale, a partir do pedido.</summary>
internal sealed record TokenObtido(string AccessToken, TimeSpan ExpiraEm);
