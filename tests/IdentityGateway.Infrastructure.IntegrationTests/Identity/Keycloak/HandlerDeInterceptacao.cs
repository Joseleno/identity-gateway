using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O que o teste quer que aconteça com cada requisição à Admin API, e o registro do que aconteceu.
/// </summary>
/// <remarks>
/// O <c>ordinal</c> conta por método, a partir de 1: "o primeiro POST", "o segundo GET".
/// </remarks>
internal sealed class Interceptacao
{
    private readonly ConcurrentDictionary<HttpMethod, int> _ordinais = new();

    /// <summary>Espera antes de enviar (barreira de corrida).</summary>
    public Func<HttpRequestMessage, int, Task>? AntesDeEnviar { get; init; }

    /// <summary>Responde este status sem enviar ao Keycloak.</summary>
    public Func<HttpRequestMessage, int, HttpStatusCode?>? ResponderSemEnviar { get; init; }

    /// <summary>Troca o token por lixo antes de enviar.</summary>
    public Func<HttpRequestMessage, int, bool>? TrocarTokenPorLixo { get; init; }

    /// <summary>Envia, descarta a resposta e lança — a resposta "se perdeu na rede".</summary>
    public Func<HttpRequestMessage, int, bool>? PerderResposta { get; init; }

    /// <summary>Responde com esta resposta (com corpo) sem enviar ao Keycloak.</summary>
    /// <remarks>
    /// Para simular uma busca vazia (corrida do 409) ou um 500 do SMTP, que precisam de corpo ou de caminho — o
    /// <see cref="ResponderSemEnviar"/> só devolve o status.
    /// </remarks>
    public Func<HttpRequestMessage, int, HttpResponseMessage?>? Responder { get; init; }

    public ConcurrentQueue<(HttpMethod Metodo, HttpStatusCode? Status)> Registro { get; } = new();

    /// <summary>Método, caminho e status de cada chamada, na ordem.</summary>
    public ConcurrentQueue<(HttpMethod Metodo, string Caminho, HttpStatusCode? Status)> Detalhes { get; } = new();

    public int Contar(HttpMethod metodo) => Registro.Count(chamada => chamada.Metodo == metodo);

    public int Contar(HttpMethod metodo, string sufixoDoCaminho) =>
        Detalhes.Count(chamada => chamada.Metodo == metodo
                                  && chamada.Caminho.EndsWith(sufixoDoCaminho, StringComparison.Ordinal));

    public IEnumerable<HttpStatusCode?> Status(HttpMethod metodo) =>
        Registro.Where(chamada => chamada.Metodo == metodo).Select(chamada => chamada.Status);

    internal int ProximoOrdinal(HttpMethod metodo) => _ordinais.AddOrUpdate(metodo, 1, (_, atual) => atual + 1);
}

/// <summary>
/// Pendurado no FIM do pipeline do <c>KeycloakAdminClient</c> — dentro da resiliência e do handler do token —, é o
/// último a ver a requisição antes da rede.
/// </summary>
internal sealed class HandlerDeInterceptacao(Interceptacao estado) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int ordinal = estado.ProximoOrdinal(request.Method);

        if (estado.AntesDeEnviar is not null)
        {
            await estado.AntesDeEnviar(request, ordinal);
        }

        if (estado.ResponderSemEnviar?.Invoke(request, ordinal) is { } status)
        {
            estado.Registro.Enqueue((request.Method, status));
            estado.Detalhes.Enqueue((request.Method, request.RequestUri!.AbsolutePath, status));
            return new HttpResponseMessage(status);
        }

        if (estado.Responder?.Invoke(request, ordinal) is { } pronta)
        {
            estado.Registro.Enqueue((request.Method, pronta.StatusCode));
            estado.Detalhes.Enqueue((request.Method, request.RequestUri!.AbsolutePath, pronta.StatusCode));
            return pronta;
        }

        if (estado.TrocarTokenPorLixo?.Invoke(request, ordinal) == true)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "token-lixo");
        }

        HttpResponseMessage resposta = await base.SendAsync(request, cancellationToken);
        estado.Registro.Enqueue((request.Method, resposta.StatusCode));
        estado.Detalhes.Enqueue((request.Method, request.RequestUri!.AbsolutePath, resposta.StatusCode));

        if (estado.PerderResposta?.Invoke(request, ordinal) == true)
        {
            resposta.Dispose();
            throw new HttpRequestException("Resposta perdida (injetado pelo teste).");
        }

        return resposta;
    }
}

internal static class InterceptacaoExtensions
{
    /// <summary>Acrescenta a interceptação ao fim do pipeline do cliente da Admin API.</summary>
    /// <remarks>Uma instância nova de handler por pipeline, com o estado compartilhado.</remarks>
    public static void Interceptar(this IServiceCollection services, Interceptacao estado) =>
        services.AddHttpClient<KeycloakAdminClient>().AddHttpMessageHandler(() => new HandlerDeInterceptacao(estado));
}
