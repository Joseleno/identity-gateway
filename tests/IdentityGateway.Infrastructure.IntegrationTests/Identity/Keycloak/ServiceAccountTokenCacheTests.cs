using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using NSubstitute;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O cache pede um token por vez, reusa enquanto ele vale com folga, e não guarda falha.
/// </summary>
public sealed class ServiceAccountTokenCacheTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly IDateTimeProvider _relogio = Substitute.For<IDateTimeProvider>();

    public ServiceAccountTokenCacheTests() => _relogio.UtcNow.Returns(T0);

    /// <summary>Token endpoint falso: devolve t1, t2, t3... e conta as chamadas.</summary>
    private sealed class EndpointFalso(Func<int, CancellationToken, Task<TokenObtido>>? comportamento = null)
        : ITokenEndpoint
    {
        private int _chamadas;

        public int Chamadas => Volatile.Read(ref _chamadas);

        public Task<TokenObtido> ObterAsync(CancellationToken cancellationToken)
        {
            int numero = Interlocked.Increment(ref _chamadas);

            return comportamento?.Invoke(numero, cancellationToken)
                   ?? Task.FromResult(new TokenObtido($"t{numero}", TimeSpan.FromSeconds(300)));
        }
    }

    [Fact]
    public async Task VinteChamadasSimultaneas_FazemUmaUnicaRequisicao()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        TaskCompletionSource primeiraChegou = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource liberar = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // A resposta fica retida até todas as chamadas terem entrado: se ela voltasse na hora, uma implementação
        // SEM trava também faria uma requisição só, e o teste passaria sem provar nada.
        EndpointFalso endpoint = new(async (numero, _) =>
        {
            primeiraChegou.TrySetResult();
            await liberar.Task;
            return new TokenObtido($"t{numero}", TimeSpan.FromSeconds(300));
        });

        using ServiceAccountTokenCache cache = new(endpoint, _relogio);

        Task<string>[] chamadas =
        [
            .. Enumerable.Range(0, 20).Select(_ => Task.Run(() => cache.ObterAsync(ct).AsTask(), ct)),
        ];

        await primeiraChegou.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await Task.Delay(200, ct);
        liberar.SetResult();

        string[] tokens = await Task.WhenAll(chamadas);

        endpoint.Chamadas.Should().Be(1);
        tokens.Should().AllBe("t1");
    }

    [Fact]
    public async Task TrintaEUmSegundosRestantes_Reusa()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        EndpointFalso endpoint = new();
        using ServiceAccountTokenCache cache = new(endpoint, _relogio);

        await cache.ObterAsync(ct);
        _relogio.UtcNow.Returns(T0.AddSeconds(300 - 31));
        string segundo = await cache.ObterAsync(ct);

        segundo.Should().Be("t1");
        endpoint.Chamadas.Should().Be(1);
    }

    [Fact]
    public async Task VinteENoveSegundosRestantes_BuscaDeNovo()
    {
        // A margem existe porque o token pode expirar no caminho até o Keycloak: um token com 5s de vida sai daqui
        // válido e chega vencido, e a chamada volta 401 por nada.
        CancellationToken ct = TestContext.Current.CancellationToken;
        EndpointFalso endpoint = new();
        using ServiceAccountTokenCache cache = new(endpoint, _relogio);

        await cache.ObterAsync(ct);
        _relogio.UtcNow.Returns(T0.AddSeconds(300 - 29));
        string segundo = await cache.ObterAsync(ct);

        segundo.Should().Be("t2");
        endpoint.Chamadas.Should().Be(2);
    }

    [Fact]
    public async Task TokenComVidaMenorQueAMargem_EUsadoUmaVezEDepoisRenovado()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        EndpointFalso endpoint = new((numero, _) =>
            Task.FromResult(new TokenObtido($"t{numero}", TimeSpan.FromSeconds(10))));
        using ServiceAccountTokenCache cache = new(endpoint, _relogio);

        string primeiro = await cache.ObterAsync(ct);
        string segundo = await cache.ObterAsync(ct);

        // Sem laço e sem erro: quem pediu usa o que veio; a próxima chamada busca outro.
        primeiro.Should().Be("t1");
        segundo.Should().Be("t2");
    }

    [Fact]
    public async Task FalhaNaObtencao_NaoFicaEmCache()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        EndpointFalso endpoint = new((numero, _) => numero == 1
            ? Task.FromException<TokenObtido>(new HttpRequestException("fora do ar"))
            : Task.FromResult(new TokenObtido($"t{numero}", TimeSpan.FromSeconds(300))));
        using ServiceAccountTokenCache cache = new(endpoint, _relogio);

        Func<Task> primeira = () => cache.ObterAsync(ct).AsTask();
        await primeira.Should().ThrowAsync<HttpRequestException>();

        string segunda = await cache.ObterAsync(ct);

        segunda.Should().Be("t2");
    }

    [Fact]
    public async Task CancelarQuemDisparouABusca_NaoCancelaQuemEspera()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        TaskCompletionSource primeiraChegou = new(TaskCreationOptions.RunContinuationsAsynchronously);

        EndpointFalso endpoint = new(async (numero, token) =>
        {
            if (numero == 1)
            {
                primeiraChegou.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }

            return new TokenObtido($"t{numero}", TimeSpan.FromSeconds(300));
        });

        using ServiceAccountTokenCache cache = new(endpoint, _relogio);
        using CancellationTokenSource cancelaPrimeiro = new();

        Task<string> primeiro = cache.ObterAsync(cancelaPrimeiro.Token).AsTask();
        await primeiraChegou.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Task<string> segundo = cache.ObterAsync(ct).AsTask();

        await cancelaPrimeiro.CancelAsync();

        Func<Task> esperarPrimeiro = () => primeiro;
        await esperarPrimeiro.Should().ThrowAsync<OperationCanceledException>();
        (await segundo.WaitAsync(TimeSpan.FromSeconds(10), ct)).Should().Be("t2");
    }

    [Fact]
    public async Task Invalidar_SoDescartaOTokenRecusado()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        EndpointFalso endpoint = new();
        using ServiceAccountTokenCache cache = new(endpoint, _relogio);

        await cache.ObterAsync(ct);

        // Um 401 atrasado de um token antigo não pode derrubar o token novo que outra chamada acabou de obter.
        cache.Invalidar("um-token-antigo");
        (await cache.ObterAsync(ct)).Should().Be("t1");

        cache.Invalidar("t1");
        (await cache.ObterAsync(ct)).Should().Be("t2");
    }
}
