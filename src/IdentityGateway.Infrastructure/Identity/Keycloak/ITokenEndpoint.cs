namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// Obtém um token do service account. Existe como interface para o cache poder ser testado sem HTTP.
/// </summary>
internal interface ITokenEndpoint
{
    /// <summary>Pede um token novo ao Keycloak. Cada chamada é uma tentativa, com assertion próprio.</summary>
    /// <exception cref="HttpRequestException">O Keycloak recusou (4xx/5xx) ou a conexão falhou.</exception>
    /// <exception cref="TaskCanceledException">
    /// O timeout do cliente do token (<c>AttemptTimeoutSeconds</c>, sem resiliência aqui — ver
    /// <c>AddKeycloakIdentity</c>) estourou antes de o Keycloak responder. Não é
    /// <see cref="HttpRequestException"/>: desde o .NET 5, o cancelamento por <c>HttpClient.Timeout</c> chega como
    /// <see cref="TaskCanceledException"/> com <see cref="TimeoutException"/> como <c>InnerException</c>.
    /// </exception>
    Task<TokenObtido> ObterAsync(CancellationToken cancellationToken);
}

/// <summary>O access token e por quanto tempo ele vale, a partir do pedido.</summary>
internal sealed record TokenObtido(string AccessToken, TimeSpan ExpiraEm);
