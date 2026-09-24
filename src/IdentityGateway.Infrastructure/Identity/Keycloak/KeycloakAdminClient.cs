using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IdentityGateway.Application.Common.Abstractions;
using Microsoft.Extensions.Options;

namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// Cliente tipado da Admin API do Keycloak — só os endpoints usados (ADR-008).
/// </summary>
/// <remarks>
/// O token e a resiliência não aparecem aqui: são handlers no pipeline do <see cref="HttpClient"/> injetado
/// (<c>AddKeycloakIdentity</c>). Este cliente só sabe montar a requisição e ler a resposta.
/// </remarks>
internal sealed class KeycloakAdminClient(HttpClient http, IOptions<KeycloakAdminOptions> options)
{
    // Web = camelCase, que é o que o Jackson do Keycloak usa. Nulos fora: o Keycloak interpreta campo presente como
    // intenção.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private string Organizations => $"admin/realms/{Uri.EscapeDataString(options.Value.Realm)}/organizations";

    /// <summary>Cria a Organization e devolve o id que o Keycloak atribuiu.</summary>
    /// <exception cref="KeycloakConflictException"><c>name</c> ou <c>alias</c> já em uso (409).</exception>
    public async Task<string> CreateOrganizationAsync(
        OrganizationRepresentation organizacao, CancellationToken cancellationToken)
    {
        // StringContent, e não JsonContent: num 401 o handler do token reenvia o MESMO request, e o corpo precisa
        // poder ser lido de novo.
        using StringContent corpo = new(
            JsonSerializer.Serialize(organizacao, Json), Encoding.UTF8, "application/json");

        using HttpResponseMessage resposta = await http.PostAsync(
            new Uri(Organizations, UriKind.Relative), corpo, cancellationToken);

        // Decide pelo status, nunca pelo texto: a mensagem do 409 é detalhe do Keycloak e muda entre versões.
        if (resposta.StatusCode == HttpStatusCode.Conflict)
        {
            throw new KeycloakConflictException("O Keycloak respondeu 409 ao criar a Organization.");
        }

        resposta.EnsureSuccessStatusCode();

        // 201 sem corpo; o id vem no fim do Location.
        Uri local = resposta.Headers.Location
                    ?? throw new HttpRequestException("O Keycloak respondeu 201 sem o cabeçalho Location.");

        return local.Segments[^1].TrimEnd('/');
    }

    /// <summary>A Organization com o atributo informado, ou <c>null</c>.</summary>
    /// <remarks>
    /// O parâmetro é <c>q</c> — <c>searchQuery</c> é só o nome da variável Java, e <c>exact</c> vale para
    /// <c>search</c>, não para <c>q</c>, que já compara por igualdade. <c>briefRepresentation=false</c> é o que faz os
    /// atributos voltarem. <c>max=2</c> basta para detectar duplicidade sem trazer o realm inteiro.
    /// </remarks>
    /// <exception cref="IdentityProviderInconsistencyException">Mais de uma Organization com o atributo.</exception>
    public async Task<OrganizationRepresentation?> FindOrganizationByAttributeAsync(
        string chave, string valor, CancellationToken cancellationToken)
    {
        string q = Uri.EscapeDataString($"{chave}:{valor}");

        List<OrganizationRepresentation>? achadas = await http.GetFromJsonAsync<List<OrganizationRepresentation>>(
            new Uri($"{Organizations}?q={q}&briefRepresentation=false&max=2", UriKind.Relative),
            Json,
            cancellationToken);

        // Mais de uma é correlação corrompida: falhar alto em vez de escolher uma e provisionar sobre a errada.
        if (achadas is { Count: > 1 })
        {
            throw new IdentityProviderInconsistencyException($"Mais de uma Organization com {chave}={valor}.");
        }

        return achadas?.SingleOrDefault();
    }
}
