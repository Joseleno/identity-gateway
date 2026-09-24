using System.Diagnostics.CodeAnalysis;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Infrastructure.Configuration;

/// <summary>
/// Um plano do catálogo, como vem da configuração.
/// </summary>
public sealed class PlanDefinition
{
    /// <summary>Nível comercial.</summary>
    public PlanTier Tier { get; set; }

    /// <summary>Teto de vagas de membro.</summary>
    public int MaxUsers { get; set; }

    /// <summary>Teto de clients OIDC ativos.</summary>
    public int MaxClients { get; set; }
}

/// <summary>
/// O catálogo de planos, indexado pelo código.
/// </summary>
/// <remarks>
/// Herda de <c>Dictionary</c> porque a seção <c>Plans</c> do appsettings é um mapa de código para limites, e o
/// binder da configuração o preenche diretamente. Acrescentar um plano é editar o JSON.
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1010:Generic interface should also be implemented",
    Justification = "É um alvo de binding da configuração, não uma coleção de domínio.")]
public sealed class PlanOptions : Dictionary<string, PlanDefinition>
{
    /// <summary>Nome da seção no appsettings.</summary>
    public const string SectionName = "Plans";

    /// <summary>
    /// Compara códigos sem diferenciar caixa.
    /// </summary>
    /// <remarks>
    /// O código chega no corpo da requisição: exigir a caixa exata transformaria <c>"Free"</c> num <c>400</c>
    /// sem razão de negócio.
    /// </remarks>
    public PlanOptions()
        : base(StringComparer.OrdinalIgnoreCase)
    {
    }
}
