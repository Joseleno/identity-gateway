using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// Pede o token do service account ao Keycloak por <c>client_credentials</c> com <c>private_key_jwt</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sem retry, de propósito.</b> O <c>jti</c> do assertion é de uso único: uma política que reenviasse o mesmo
/// corpo seria recusada com "Token reuse detected". Quem precisa de outra tentativa chama de novo, e ganha um
/// assertion novo. O cliente HTTP nomeado <see cref="NomeDoCliente"/> é registrado sem resiliência, e um teste
/// garante que continue assim.
/// </para>
/// <para>
/// Singleton, e por isso pede o <see cref="HttpClient"/> à fábrica a cada chamada em vez de guardá-lo: um cliente
/// capturado por um singleton nunca troca de handler, e para de enxergar mudança de DNS.
/// </para>
/// </remarks>
internal sealed class KeycloakTokenClient(
    IHttpClientFactory fabrica,
    IOptions<KeycloakAdminOptions> options,
    ClientAssertionFactory assertions,
    ILogger<KeycloakTokenClient> logger) : ITokenEndpoint
{
    /// <summary>Nome do cliente HTTP do token endpoint.</summary>
    internal const string NomeDoCliente = "keycloak-token";

    private const int TamanhoMaximoDoErro = 200;

    public async Task<TokenObtido> ObterAsync(CancellationToken cancellationToken)
    {
        KeycloakAdminOptions opcoes = options.Value;
        using HttpClient http = fabrica.CreateClient(NomeDoCliente);

        using FormUrlEncodedContent corpo = new(
        [
            new("grant_type", "client_credentials"),
            new("client_id", opcoes.ClientId),
            new("client_assertion_type", "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"),
            new("client_assertion", assertions.Criar()),
        ]);

        using HttpResponseMessage resposta = await http.PostAsync(
            new Uri($"{opcoes.Issuer}/protocol/openid-connect/token"), corpo, cancellationToken);

        if (!resposta.IsSuccessStatusCode)
        {
            string erro = await LerErroAsync(resposta, cancellationToken);
            KeycloakLogs.TokenRecusado(logger, (int)resposta.StatusCode, erro);

            throw new HttpRequestException(
                $"O Keycloak recusou o token do service account: {(int)resposta.StatusCode} {erro}",
                inner: null,
                resposta.StatusCode);
        }

        RespostaDeToken? token = await resposta.Content.ReadFromJsonAsync<RespostaDeToken>(cancellationToken);

        if (token is null || string.IsNullOrEmpty(token.AccessToken))
        {
            throw new HttpRequestException("O Keycloak respondeu sucesso sem access_token.");
        }

        return new TokenObtido(token.AccessToken, TimeSpan.FromSeconds(token.ExpiresIn));
    }

    /// <summary>
    /// <c>error</c> e <c>error_description</c> do Keycloak, truncados.
    /// </summary>
    /// <remarks>
    /// O <c>error_description</c> do token endpoint é genérico ("Invalid client or Invalid client credentials") e
    /// ajuda a diagnosticar sem expor nada. O corpo inteiro nunca: se um dia o Keycloak ecoasse o que recebeu, o
    /// assertion iria junto para o log.
    /// </remarks>
    private static async Task<string> LerErroAsync(HttpResponseMessage resposta, CancellationToken cancellationToken)
    {
        try
        {
            RespostaDeErro? erro = await resposta.Content.ReadFromJsonAsync<RespostaDeErro>(cancellationToken);
            string texto = $"{erro?.Error}: {erro?.ErrorDescription}";

            return texto.Length <= TamanhoMaximoDoErro ? texto : texto[..TamanhoMaximoDoErro];
        }
        catch (JsonException)
        {
            return "(corpo de erro fora do formato OAuth)";
        }
    }

    private sealed record RespostaDeToken(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record RespostaDeErro(
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);
}
