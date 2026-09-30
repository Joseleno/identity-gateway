using IdentityGateway.Domain.Members;
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
/// outras quatro da §11.3 agora seria contrato que mente: métodos que existem e lançam
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

    /// <summary>
    /// Garante que o convidado existe, pertence à Organization, tem o papel e — se ainda não aceitou — recebeu o
    /// e-mail com o link. Devolve o <c>sub</c> dele.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>O <paramref name="tenantId"/> é a chave da correlação</b> (atributo de usuário <c>tenant_id</c>, §12.2): um
    /// usuário com o mesmo e-mail e o mesmo tenant é tentativa anterior e é reaproveitado; com qualquer outro valor, ou
    /// sem valor, é conta alheia (D5). A assinatura acrescenta o tenant à da §11.3, como o I1 fez em
    /// <see cref="EnsureOrganizationAsync"/>.
    /// </para>
    /// <para>
    /// Mesmo contrato de erro de <see cref="EnsureOrganizationAsync"/>: só
    /// <see cref="IdentityProviderInconsistencyException"/> é permanente. <b>Nenhuma exceção carrega o e-mail</b>; elas
    /// levam o tenant (D15).
    /// </para>
    /// <para>
    /// <b>O e-mail pode sair mais de uma vez:</b> enquanto o convidado não aceitar, cada chamada reenvia, e qualquer
    /// falha entre esta chamada e o commit faz a mensagem voltar (§4.3 da spec da fatia C).
    /// </para>
    /// </remarks>
    /// <param name="organizationId">Organization do tenant, devolvida por <see cref="EnsureOrganizationAsync"/>.</param>
    /// <param name="tenantId">Tenant do convite; é gravado no usuário e correlaciona as tentativas.</param>
    /// <param name="invite">E-mail, papel e prazo do link.</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    /// <exception cref="IdentityProviderInconsistencyException">
    /// O e-mail ou o username pertence a um usuário que não é deste tenant, o nosso usuário foi desabilitado à mão, ou
    /// o papel não existe no realm. Repetir não resolve.
    /// </exception>
    Task<ExternalUserId> EnsureInvitedUserAsync(
        string organizationId, TenantId tenantId, InviteData invite, CancellationToken cancellationToken);
}
