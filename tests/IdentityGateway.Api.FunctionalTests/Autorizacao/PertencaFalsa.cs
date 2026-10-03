using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Api.FunctionalTests.Autorizacao;

/// <summary>
/// Uma <see cref="IMemberQueries"/> de teste: responde o status combinado e conta quantas vezes foi consultada.
/// </summary>
/// <remarks>
/// A contagem é o que prova a ordem dos handlers: a pertença só pode ser consultada para quem já passou nas camadas
/// do token. Uma consulta com o papel ausente é uma consulta ao banco que qualquer token autenticado conseguiria
/// provocar, para qualquer tenant.
/// </remarks>
internal sealed class PertencaFalsa(MemberStatus? status) : IMemberQueries
{
    private int _consultas;

    /// <summary>Quantas vezes a porta foi consultada.</summary>
    public int Consultas => _consultas;

    /// <summary>O tenant e o <c>sub</c> da última consulta.</summary>
    public (TenantId TenantId, ExternalUserId ExternalUserId)? Ultima { get; private set; }

    public Task<MemberStatus?> GetStatusAsync(
        TenantId tenantId, ExternalUserId externalUserId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _consultas);
        Ultima = (tenantId, externalUserId);

        return Task.FromResult(status);
    }
}
