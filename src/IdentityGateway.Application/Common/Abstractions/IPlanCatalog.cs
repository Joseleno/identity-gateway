using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Application.Common.Abstractions;

/// <summary>
/// Catálogo dos planos comerciais disponíveis.
/// </summary>
/// <remarks>
/// Plano é dado de catálogo, não entidade: a §6.1 o trata como value object dentro do <c>Tenant</c>, sem ciclo
/// de vida nem histórico próprio. A implementação lê da configuração, de modo que mudar um limite comercial
/// seja editar o appsettings — não uma migration.
/// </remarks>
public interface IPlanCatalog
{
    /// <summary>Resolve o plano pelo código, ou <c>null</c> se não existir.</summary>
    Plan? Find(string planCode);
}
