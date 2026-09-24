using System.Net;
using System.Net.Http.Headers;

namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// Autentica cada chamada à Admin API com o token do service account.
/// </summary>
/// <remarks>
/// <para>
/// <b>Num 401, invalida o token e repete uma vez.</b> O token pode ter sido revogado ou expirado antes da hora que o
/// cache calculou. Repetir é seguro até num POST: 401 significa que o Keycloak não executou nada. Uma vez só — um
/// token recém-emitido recusado é erro de configuração, e insistir viraria laço.
/// </para>
/// <para>
/// <b>Fica DENTRO da resiliência</b> (registrado depois dela). Cada tentativa da resiliência passa por aqui e renova o
/// token se preciso, e a repetição após 401 acontece dentro de uma tentativa, contida no timeout total.
/// </para>
/// <para>
/// O mesmo <see cref="HttpRequestMessage"/> é reenviado: por isso o <c>KeycloakAdminClient</c> usa corpo em
/// <c>StringContent</c>, que pode ser lido de novo.
/// </para>
/// </remarks>
internal sealed class ServiceAccountTokenHandler(ServiceAccountTokenCache cache) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string token = await cache.ObterAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage resposta = await base.SendAsync(request, cancellationToken);

        if (resposta.StatusCode != HttpStatusCode.Unauthorized)
        {
            return resposta;
        }

        resposta.Dispose();
        cache.Invalidar(token);

        string novo = await cache.ObterAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", novo);

        return await base.SendAsync(request, cancellationToken);
    }
}
