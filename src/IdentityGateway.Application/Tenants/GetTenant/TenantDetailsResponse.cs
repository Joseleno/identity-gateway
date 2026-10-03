namespace IdentityGateway.Application.Tenants.GetTenant;

/// <summary>
/// O que a leitura de um tenant devolve.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sem o e-mail do admin inicial, de propósito.</b> Ele é dado pessoal, só existe enquanto o tenant está por
/// provisionar e some na ativação; não é atributo do tenant para quem o lê. Um teste trava o conjunto de chaves da
/// resposta, e outro, por reflexão, recusa qualquer propriedade de e-mail aqui.
/// </para>
/// <para>
/// Sem o id da Organization: é detalhe interno do Keycloak, como na resposta do provisionamento.
/// </para>
/// </remarks>
/// <param name="TenantId">Identidade do tenant.</param>
/// <param name="Name">Nome de exibição.</param>
/// <param name="Slug">Slug, único e imutável.</param>
/// <param name="Status">Nome do <c>TenantStatus</c>.</param>
/// <param name="Plan">O plano contratado.</param>
/// <param name="OccupiedSeats">Vagas ocupadas, contando convites pendentes.</param>
/// <param name="RegisteredAt">Quando foi registrado, em UTC.</param>
public sealed record TenantDetailsResponse(
    Guid TenantId,
    string Name,
    string Slug,
    string Status,
    TenantPlanResponse Plan,
    int OccupiedSeats,
    DateTimeOffset RegisteredAt);

/// <summary>O plano do tenant, na leitura.</summary>
/// <param name="Tier">Nome do <c>PlanTier</c>.</param>
/// <param name="MaxUsers">Limite de membros.</param>
/// <param name="MaxClients">Limite de clients M2M.</param>
public sealed record TenantPlanResponse(string Tier, int MaxUsers, int MaxClients);
