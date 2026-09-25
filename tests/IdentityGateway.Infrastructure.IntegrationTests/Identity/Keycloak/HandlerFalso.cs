namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// Handler HTTP que responde o que o teste mandar, contando as chamadas.
/// </summary>
/// <remarks>
/// Para testar as peças sem Keycloak. O comportamento real do Keycloak é provado nos testes contra o container
/// (<c>KeycloakFixture</c>); aqui se prova o que o NOSSO código faz com cada resposta.
/// </remarks>
internal sealed class HandlerFalso(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    private int _chamadas;

    public int Chamadas => Volatile.Read(ref _chamadas);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _chamadas);
        return responder(request, cancellationToken);
    }
}
