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
    /// <remarks>
    /// <para>
    /// <b>Qualquer exceção que não seja <see cref="IdentityProviderInconsistencyException"/> é falha de
    /// infraestrutura</b>, e o consumidor deve tratá-la assim: retry por <i>exclusão</i> — ignora só o tipo de
    /// inconsistência e trata o resto como transiente — nunca por lista branca de tipo. Uma lista branca baseada só
    /// em <c>HttpRequestException</c> erra dos dois lados (spec §4.1): um timeout de tentativa ou um circuito
    /// aberto na Admin API chegam aqui como <c>TaskCanceledException</c> ou como o tipo de timeout/circuito da
    /// resiliência (Polly por baixo do <c>Microsoft.Extensions.Http.Resilience</c>), não como
    /// <c>HttpRequestException</c>; e nem todo <c>HttpRequestException</c> é transiente — um 403, um
    /// <c>invalid_client</c> ou um 404 de "Organizations not enabled" são erro de configuração, que repetir não
    /// corrige, mas que também não deve virar falha de negócio silenciosa.
    /// </para>
    /// </remarks>
    /// <param name="tenantId">Correlaciona a Organization ao tenant; é a chave da idempotência.</param>
    /// <param name="slug">Identificador único e imutável do tenant.</param>
    /// <param name="name">Nome de exibição.</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    /// <exception cref="IdentityProviderInconsistencyException">
    /// Único erro <b>permanente</b>: o slug está em uso por outra Organization, ou há mais de uma correlacionada ao
    /// mesmo tenant. Repetir a mensagem não resolve (spec §4.3).
    /// </exception>
    Task<string> EnsureOrganizationAsync(
        TenantId tenantId, TenantSlug slug, string name, CancellationToken cancellationToken);
}
