using IdentityGateway.Domain.Members;

namespace IdentityGateway.Application.Common.Abstractions;

/// <summary>
/// Persistência do agregado <see cref="Member"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Toda assinatura exige o tenant (§6.4).</b> Nesta fatia há só <see cref="Add"/>, e o <c>Member</c> já carrega o
/// <c>TenantId</c>. <c>GetAsync(TenantId, MemberId)</c>, <c>ListAsync(TenantId)</c> e o teste que proíbe a
/// sobrecarga só por id chegam no M2, com as rotas de membro.
/// </para>
/// <para>
/// Como o <see cref="ITenantRepository"/>, não expõe <c>SaveChanges</c>: o membro e a ativação do tenant saem no
/// mesmo commit do <c>TransactionBehavior</c>.
/// </para>
/// </remarks>
public interface IMemberRepository
{
    /// <summary>Marca o membro para inserção. Não grava.</summary>
    void Add(Member member);
}
