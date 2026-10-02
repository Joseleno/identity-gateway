using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace IdentityGateway.Infrastructure.Persistence.Queries;

/// <summary>
/// Implementa <see cref="IMemberQueries"/> com uma projeção sem rastreamento.
/// </summary>
internal sealed class MemberQueries(AppDbContext context) : IMemberQueries
{
    /// <inheritdoc />
    /// <remarks>
    /// O filtro leva o tenant <b>e</b> o <c>sub</c>, e é o índice único <c>(tenant_id, external_user_id)</c> que
    /// responde: no máximo uma linha. O cast para o tipo anulável é o que faz "nenhuma linha" virar nulo, e não o
    /// primeiro valor do enum.
    /// </remarks>
    public Task<MemberStatus?> GetStatusAsync(
        TenantId tenantId, ExternalUserId externalUserId, CancellationToken cancellationToken) =>
        context.Members
            .AsNoTracking()
            .Where(membro => membro.TenantId == tenantId && membro.ExternalUserId == externalUserId)
            .Select(membro => (MemberStatus?)membro.Status)
            .SingleOrDefaultAsync(cancellationToken);
}
