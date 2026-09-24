using IdentityGateway.Application.Common.Abstractions;

namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// Guarda o token do service account e garante que só uma chamada por vez vá buscá-lo.
/// </summary>
/// <remarks>
/// <para>
/// <b>Singleton, e não dentro do handler.</b> O <c>IHttpClientFactory</c> recicla os handlers a cada ~2 minutos; um
/// cache guardado neles morreria junto, sem erro — e cada ciclo pediria um token novo.
/// </para>
/// <para>
/// <b>Single-flight.</b> Sem a trava, N chamadas simultâneas com o cache vazio pediriam N tokens — e cada pedido
/// assina um assertion e ocupa o Keycloak. Com ela, uma busca e as demais esperam o resultado.
/// </para>
/// <para>
/// <b>Falha não fica em cache.</b> A trava é liberada no <c>finally</c> sem gravar nada, e a próxima chamada tenta de
/// novo. Pelo mesmo motivo, cancelar quem disparou a busca não afeta quem espera: a exceção é só dele, e o próximo
/// da fila busca outra vez.
/// </para>
/// </remarks>
internal sealed class ServiceAccountTokenCache(ITokenEndpoint endpoint, IDateTimeProvider relogio) : IDisposable
{
    /// <summary>Folga antes da expiração em que o token deixa de ser usado.</summary>
    internal static readonly TimeSpan Margem = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _trava = new(1, 1);
    private TokenEmCache? _atual;

    /// <summary>Um token válido, do cache ou recém-obtido.</summary>
    public async ValueTask<string> ObterAsync(CancellationToken cancellationToken)
    {
        if (ValorSeValido(Volatile.Read(ref _atual)) is { } rapido)
        {
            return rapido;
        }

        await _trava.WaitAsync(cancellationToken);

        try
        {
            // Quem esperou na trava encontra o token que a chamada da frente acabou de obter.
            if (ValorSeValido(_atual) is { } jaObtido)
            {
                return jaObtido;
            }

            // A validade conta a partir do PEDIDO, não da resposta: o Keycloak começou a contar antes de responder.
            DateTimeOffset pedidoEm = relogio.UtcNow;
            TokenObtido obtido = await endpoint.ObterAsync(cancellationToken);

            Volatile.Write(ref _atual, new TokenEmCache(obtido.AccessToken, pedidoEm + obtido.ExpiraEm));

            // Devolve o que veio mesmo que já esteja dentro da margem: quem pediu usa, a próxima chamada renova.
            return obtido.AccessToken;
        }
        finally
        {
            _trava.Release();
        }
    }

    /// <summary>Descarta o token, se ainda for o que foi recusado.</summary>
    /// <remarks>
    /// Compara antes de descartar: um 401 atrasado de um token antigo não pode derrubar o token novo que outra
    /// chamada acabou de obter.
    /// </remarks>
    public void Invalidar(string tokenRecusado)
    {
        TokenEmCache? atual = Volatile.Read(ref _atual);

        if (atual is not null && string.Equals(atual.Valor, tokenRecusado, StringComparison.Ordinal))
        {
            Interlocked.CompareExchange(ref _atual, null, atual);
        }
    }

    public void Dispose() => _trava.Dispose();

    private string? ValorSeValido(TokenEmCache? token) =>
        token is not null && relogio.UtcNow < token.ExpiraEm - Margem ? token.Valor : null;

    private sealed record TokenEmCache(string Valor, DateTimeOffset ExpiraEm);
}
