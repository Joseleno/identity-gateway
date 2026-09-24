using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Application.Common.Abstractions;

/// <summary>
/// Única forma de a Application falar com o provedor de identidade.
/// </summary>
/// <remarks>
/// <para>
/// Nenhum tipo do Keycloak atravessa esta interface (ADR-008), e toda operação é idempotente: "garanta que existe",
/// não "crie". A mesma mensagem do Outbox pode chegar duas vezes, e a segunda entrega precisa ser inofensiva.
/// </para>
/// <para>
/// <b>A interface cresce por fatia.</b> Cada operação entra quando o caso de uso que a chama entra. Declarar as
/// outras cinco da §11.3 agora seria contrato que mente: métodos que existem e lançam
/// <see cref="NotImplementedException"/>.
/// </para>
/// </remarks>
public interface IIdentityProvider
{
    /// <summary>
    /// Garante que existe a Organization do tenant, e devolve o id dela no provedor.
    /// </summary>
    /// <param name="tenantId">Correlaciona a Organization ao tenant; é a chave da idempotência.</param>
    /// <param name="slug">Identificador único e imutável do tenant.</param>
    /// <param name="name">Nome de exibição.</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    /// <exception cref="IdentityProviderInconsistencyException">
    /// Estado que repetir não resolve: o slug está em uso por outra Organization, ou há mais de uma correlacionada ao
    /// mesmo tenant.
    /// </exception>
    /// <exception cref="HttpRequestException">Falha transiente de comunicação com o provedor.</exception>
    Task<string> EnsureOrganizationAsync(
        TenantId tenantId, TenantSlug slug, string name, CancellationToken cancellationToken);
}
