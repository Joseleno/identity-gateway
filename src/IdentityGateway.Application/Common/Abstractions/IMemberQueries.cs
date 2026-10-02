using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Application.Common.Abstractions;

/// <summary>
/// Leitura da pertença de um ator a um tenant.
/// </summary>
/// <remarks>
/// <para>
/// <b>O tenant está na assinatura (§6.4).</b> Não existe leitura de membro só pelo <c>sub</c>: quem pergunta diz de
/// qual tenant, e o membro de outro tenant não é achado. É a mesma regra do repositório de sub-recurso, aplicada à
/// consulta.
/// </para>
/// <para>
/// <b>Porta de consulta, e não método novo do <see cref="IMemberRepository"/>.</b> O repositório devolve o agregado
/// rastreado, para ser alterado; aqui é uma projeção sem rastreamento, de um campo, para a autorização decidir. O
/// repositório continua só com <c>Add</c>.
/// </para>
/// </remarks>
public interface IMemberQueries
{
    /// <summary>O status do membro <c>(tenant, sub)</c>, ou nulo se o ator não for membro do tenant.</summary>
    Task<MemberStatus?> GetStatusAsync(
        TenantId tenantId, ExternalUserId externalUserId, CancellationToken cancellationToken = default);
}
