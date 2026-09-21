using IdentityGateway.Domain.Common;

namespace IdentityGateway.Domain.Tenants;

/// <summary>
/// Limites contratados pelo tenant.
/// </summary>
/// <remarks>
/// <para>
/// Value object e não entidade: dois tenants no mesmo plano têm planos <i>iguais</i>, não o <i>mesmo</i>
/// plano. Trocar de plano é substituir o objeto inteiro, o que evita a pergunta "alterar este plano afeta
/// quem mais?".
/// </para>
/// <para>
/// Quem constrói é o catálogo de planos, a partir de um código (<c>PlanCode</c> na §11.4). Não há
/// validação dos limites aqui de propósito: um plano com limites absurdos é erro de catálogo, e validar
/// no value object esconderia isso de quem o mantém.
/// </para>
/// </remarks>
/// <param name="tier">Nível comercial.</param>
/// <param name="maxUsers">Teto de vagas de membro.</param>
/// <param name="maxClients">Teto de clients OIDC ativos.</param>
public sealed class Plan(PlanTier tier, int maxUsers, int maxClients) : ValueObject
{
    /// <summary>Nível comercial do plano.</summary>
    public PlanTier Tier { get; } = tier;

    /// <summary>Teto de vagas de membro.</summary>
    public int MaxUsers { get; } = maxUsers;

    /// <summary>Teto de clients OIDC ativos.</summary>
    public int MaxClients { get; } = maxClients;

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Tier;
        yield return MaxUsers;
        yield return MaxClients;
    }
}
