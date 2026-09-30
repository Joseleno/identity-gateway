using System.Globalization;
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

    private string Realm => Uri.EscapeDataString(options.Value.Realm);

    private string Organizations => $"admin/realms/{Realm}/organizations";

    private string Users => $"admin/realms/{Realm}/users";

    /// <summary>Cria a Organization e devolve o id que o Keycloak atribuiu.</summary>
    /// <exception cref="KeycloakConflictException"><c>name</c> ou <c>alias</c> já em uso (409).</exception>
    public async Task<string> CreateOrganizationAsync(
        OrganizationRepresentation organizacao, CancellationToken cancellationToken)
    {
        // Num 401 o handler do token reenvia o MESMO request, e o corpo precisa poder ser serializado de novo.
        // StringContent garante isso por contrato (guarda os bytes prontos); JsonContent também reenviaria aqui,
        // por reserializar o objeto a cada envio, mas isso é comportamento observado, não garantia documentada —
        // por isso StringContent continua sendo a escolha explícita.
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

    /// <summary>Os usuários com o e-mail exato, com atributos e ações obrigatórias.</summary>
    /// <remarks>
    /// <c>exact=true</c>: sem ele a busca vira <c>LIKE %x%</c>. <c>briefRepresentation=false</c>: explícito, porque
    /// a forma resumida não traz atributos — na 26.7.4 o padrão de <c>/users</c> já é a completa, e o parâmetro protege
    /// contra a mudança desse padrão. O e-mail vai escapado: o <c>+</c> cru viraria espaço.
    /// </remarks>
    public Task<IReadOnlyList<UserRepresentation>> FindUsersByEmailAsync(string email, CancellationToken cancellationToken) =>
        BuscarUsuariosAsync("email", email, cancellationToken);

    /// <summary>Os usuários com o username exato, com atributos e ações obrigatórias.</summary>
    public Task<IReadOnlyList<UserRepresentation>> FindUsersByUsernameAsync(
        string username, CancellationToken cancellationToken) =>
        BuscarUsuariosAsync("username", username, cancellationToken);

    /// <summary>Cria o usuário e devolve o id que o Keycloak atribuiu.</summary>
    /// <exception cref="KeycloakConflictException">E-mail ou username já em uso (409).</exception>
    public async Task<string> CreateUserAsync(UserRepresentation usuario, CancellationToken cancellationToken)
    {
        // StringContent pelo mesmo motivo de CreateOrganizationAsync: o corpo precisa sobreviver ao reenvio do 401.
        using StringContent corpo = new(JsonSerializer.Serialize(usuario, Json), Encoding.UTF8, "application/json");

        using HttpResponseMessage resposta = await http.PostAsync(
            new Uri(Users, UriKind.Relative), corpo, cancellationToken);

        if (resposta.StatusCode == HttpStatusCode.Conflict)
        {
            throw new KeycloakConflictException("O Keycloak respondeu 409 ao criar o usuário.");
        }

        resposta.EnsureSuccessStatusCode();

        Uri local = resposta.Headers.Location
                    ?? throw new HttpRequestException("O Keycloak respondeu 201 sem o cabeçalho Location.");

        return local.Segments[^1].TrimEnd('/');
    }

    /// <summary>Vincula o usuário à Organization. Já vinculado (409) conta como sucesso.</summary>
    /// <remarks>Exige <c>manage-organizations</c> e <c>manage-users</c> (<c>OrganizationMemberResource</c>).</remarks>
    public async Task AddOrganizationMemberAsync(
        string organizationId, string userId, CancellationToken cancellationToken)
    {
        // O corpo é o id como string JSON (entre aspas); o Keycloak aceita com ou sem aspas.
        using StringContent corpo = new(JsonSerializer.Serialize(userId), Encoding.UTF8, "application/json");

        using HttpResponseMessage resposta = await http.PostAsync(
            new Uri($"{Organizations}/{Uri.EscapeDataString(organizationId)}/members", UriKind.Relative),
            corpo,
            cancellationToken);

        if (resposta.StatusCode == HttpStatusCode.Conflict)
        {
            return;
        }

        resposta.EnsureSuccessStatusCode();
    }

    /// <summary>Se o usuário é membro da Organization.</summary>
    /// <remarks>
    /// <c>GET /organizations/{id}/members/{memberId}</c> (<c>OrganizationMemberResource.get</c>, 26.7.4): 200 para
    /// membro; 404 para não membro quando quem pergunta pode consultar usuários (<c>manage-users</c> pode), 403
    /// quando não pode — e o 403, como qualquer outro status, sobe como erro.
    /// </remarks>
    public async Task<bool> IsOrganizationMemberAsync(
        string organizationId, string userId, CancellationToken cancellationToken)
    {
        using HttpResponseMessage resposta = await http.GetAsync(
            new Uri(
                $"{Organizations}/{Uri.EscapeDataString(organizationId)}/members/{Uri.EscapeDataString(userId)}",
                UriKind.Relative),
            cancellationToken);

        if (resposta.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        resposta.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>Os papéis de realm atribuídos diretamente ao usuário.</summary>
    /// <remarks>
    /// Pelos endpoints do próprio usuário, que exigem só a visão de usuários: ler o papel por <c>GET /roles/{nome}</c>
    /// exigiria <c>view-realm</c>, que o service account não tem.
    /// </remarks>
    public Task<IReadOnlyList<RoleRepresentation>> GetUserRealmRolesAsync(
        string userId, CancellationToken cancellationToken) =>
        LerPapeisAsync($"{Users}/{Uri.EscapeDataString(userId)}/role-mappings/realm", cancellationToken);

    /// <summary>Os papéis de realm que ainda podem ser atribuídos ao usuário, com o id de cada um.</summary>
    public Task<IReadOnlyList<RoleRepresentation>> GetAvailableUserRealmRolesAsync(
        string userId, CancellationToken cancellationToken) =>
        LerPapeisAsync($"{Users}/{Uri.EscapeDataString(userId)}/role-mappings/realm/available", cancellationToken);

    /// <summary>Atribui papéis de realm ao usuário. Reatribuir é inofensivo.</summary>
    public async Task AddUserRealmRolesAsync(
        string userId, IReadOnlyList<RoleRepresentation> papeis, CancellationToken cancellationToken)
    {
        using StringContent corpo = new(JsonSerializer.Serialize(papeis, Json), Encoding.UTF8, "application/json");

        using HttpResponseMessage resposta = await http.PostAsync(
            new Uri($"{Users}/{Uri.EscapeDataString(userId)}/role-mappings/realm", UriKind.Relative),
            corpo,
            cancellationToken);

        resposta.EnsureSuccessStatusCode();
    }

    /// <summary>Envia o e-mail com o link de ações, que vale <paramref name="prazoEmSegundos"/>.</summary>
    /// <remarks>
    /// O Keycloak envia dentro da requisição: timeout aqui não significa "não enviado". <c>PUT</c> fica fora do retry
    /// automático (<c>DisableForUnsafeHttpMethods</c>), como o <c>POST</c>.
    /// </remarks>
    /// <exception cref="KeycloakBadRequestException">400: usuário desabilitado, sem e-mail ou ação inválida.</exception>
    public async Task ExecuteActionsEmailAsync(
        string userId, IReadOnlyList<string> acoes, int prazoEmSegundos, CancellationToken cancellationToken)
    {
        using StringContent corpo = new(JsonSerializer.Serialize(acoes, Json), Encoding.UTF8, "application/json");
        string prazo = prazoEmSegundos.ToString(CultureInfo.InvariantCulture);

        using HttpResponseMessage resposta = await http.PutAsync(
            new Uri($"{Users}/{Uri.EscapeDataString(userId)}/execute-actions-email?lifespan={prazo}", UriKind.Relative),
            corpo,
            cancellationToken);

        // Decide pelo status, nunca pelo texto do corpo ("User is disabled" é detalhe do Keycloak).
        if (resposta.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new KeycloakBadRequestException("O Keycloak respondeu 400 ao enviar o e-mail de ações.");
        }

        resposta.EnsureSuccessStatusCode();
    }

    private async Task<IReadOnlyList<UserRepresentation>> BuscarUsuariosAsync(
        string campo, string valor, CancellationToken cancellationToken)
    {
        List<UserRepresentation>? achados = await http.GetFromJsonAsync<List<UserRepresentation>>(
            new Uri($"{Users}?{campo}={Uri.EscapeDataString(valor)}&exact=true&briefRepresentation=false", UriKind.Relative),
            Json,
            cancellationToken);

        return achados ?? [];
    }

    private async Task<IReadOnlyList<RoleRepresentation>> LerPapeisAsync(
        string caminho, CancellationToken cancellationToken)
    {
        List<RoleRepresentation>? papeis = await http.GetFromJsonAsync<List<RoleRepresentation>>(
            new Uri(caminho, UriKind.Relative), Json, cancellationToken);

        return papeis ?? [];
    }
}
