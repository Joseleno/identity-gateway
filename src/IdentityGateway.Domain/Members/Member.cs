using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Domain.Members;

/// <summary>
/// Aggregate root do membro: o vínculo de um usuário do Keycloak com um tenant.
/// </summary>
/// <remarks>
/// <para>
/// <b>O mínimo que o convite do admin inicial exige</b> (spec da fatia C, §4.2): tenant, <c>sub</c>, status e
/// <c>InvitedAt</c>. O papel vive no Keycloak (ADR-005) e não é duplicado aqui nesta fatia; os papéis do catálogo e os
/// Permission Sets da §6.1 chegam no M2.
/// </para>
/// <para>
/// <b>Só o <see cref="Tenant"/> cria um membro</b>, por <see cref="Invite"/>, que é <c>internal</c>: é assim que
/// nenhum membro nasce sem a vaga reservada. Um teste de arquitetura garante que não há construtor nem fábrica
/// públicos.
/// </para>
/// <para>
/// Nenhum evento de domínio: <c>MemberInvited</c> ainda não teria consumidor.
/// </para>
/// </remarks>
public sealed class Member : AggregateRoot<MemberId>, IAuditable
{
    // Um construtor só, usado pela fábrica e pelo EF: todos os parâmetros são conversões de valor único, que o EF
    // vincula por nome — diferente do Tenant, cujo Plan é complex type.
    private Member(
        MemberId id,
        TenantId tenantId,
        ExternalUserId externalUserId,
        MemberStatus status,
        DateTimeOffset invitedAt)
        : base(id)
    {
        TenantId = tenantId;
        ExternalUserId = externalUserId;
        Status = status;
        InvitedAt = invitedAt;
    }

    /// <summary>Tenant ao qual o membro pertence.</summary>
    public TenantId TenantId { get; private set; }

    /// <summary>O <c>sub</c> do usuário no Keycloak.</summary>
    public ExternalUserId ExternalUserId { get; private set; }

    /// <summary>Estado no ciclo de vida.</summary>
    public MemberStatus Status { get; private set; }

    /// <summary>Quando o convite foi enviado, em UTC.</summary>
    public DateTimeOffset InvitedAt { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <inheritdoc />
    public Guid? CreatedBy { get; private set; }

    /// <inheritdoc />
    public Guid? UpdatedBy { get; private set; }

    /// <summary>
    /// Cria o membro convidado. Chamado só pelo <see cref="Tenant"/>, depois de reservar a vaga.
    /// </summary>
    /// <param name="tenantId">Tenant do membro; não pode ser vazio.</param>
    /// <param name="externalUserId">O <c>sub</c> devolvido pelo provedor.</param>
    /// <param name="invitedAt">Instante do convite; normalizado para UTC.</param>
    /// <exception cref="ArgumentException">Se o tenant for vazio.</exception>
    /// <exception cref="ArgumentNullException">Se o <c>sub</c> for nulo.</exception>
    internal static Member Invite(TenantId tenantId, ExternalUserId externalUserId, DateTimeOffset invitedAt)
    {
        if (tenantId.Value == Guid.Empty)
        {
            throw new ArgumentException("O membro precisa de um tenant.", nameof(tenantId));
        }

        ArgumentNullException.ThrowIfNull(externalUserId);

        return new Member(
            MemberId.New(), tenantId, externalUserId, MemberStatus.Invited, invitedAt.ToUniversalTime());
    }
}
