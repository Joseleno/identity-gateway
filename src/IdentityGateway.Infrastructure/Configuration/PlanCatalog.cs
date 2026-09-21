using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Tenants;
using Microsoft.Extensions.Options;

namespace IdentityGateway.Infrastructure.Configuration;

/// <summary>
/// Catálogo de planos em memória, lido da configuração.
/// </summary>
/// <remarks>
/// <para>
/// Em memória e não em tabela: plano é dado de catálogo, não entidade — a §6.1 o trata como value object dentro
/// do <c>Tenant</c>, sem ciclo de vida nem histórico. Uma tabela acrescentaria entidade que nada no M0 pede.
/// </para>
/// <para>
/// <c>IOptions</c> e não <c>IOptionsMonitor</c>: o catálogo é lido na subida e validado ali. Recarga a quente
/// mudaria os limites no meio de uma requisição, e o ganho não paga a pergunta "qual plano estava valendo
/// quando este tenant foi registrado?".
/// </para>
/// </remarks>
internal sealed class PlanCatalog(IOptions<PlanOptions> opcoes) : IPlanCatalog
{
    private readonly PlanOptions _planos = opcoes.Value;

    /// <inheritdoc />
    public Plan? Find(string planCode)
    {
        if (string.IsNullOrWhiteSpace(planCode))
        {
            return null;
        }

        return _planos.TryGetValue(planCode, out PlanDefinition? definicao)
            ? new Plan(definicao.Tier, definicao.MaxUsers, definicao.MaxClients)
            : null;
    }
}
