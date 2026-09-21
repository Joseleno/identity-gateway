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
public sealed class Plan : ValueObject
{
    /// <summary>
    /// Cria o plano com os limites contratados.
    /// </summary>
    /// <remarks>
    /// Limite negativo é recusado aqui, e não mais adiante: um <c>MaxUsers</c> negativo faria
    /// <c>ReserveSeat</c> recusar toda reserva com "o plano não admite mais de -5 membros" — uma mensagem
    /// que descreve o sintoma e esconde a causa, que é catálogo mal configurado. Zero é permitido: um
    /// plano sem vagas é estranho, mas é uma decisão comercial possível, e não uma impossibilidade.
    /// </remarks>
    /// <param name="tier">Nível comercial.</param>
    /// <param name="maxUsers">Teto de vagas de membro; não pode ser negativo.</param>
    /// <param name="maxClients">Teto de clients OIDC ativos; não pode ser negativo.</param>
    /// <exception cref="ArgumentOutOfRangeException">Se algum dos tetos for negativo.</exception>
    public Plan(PlanTier tier, int maxUsers, int maxClients)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxUsers);
        ArgumentOutOfRangeException.ThrowIfNegative(maxClients);

        Tier = tier;
        MaxUsers = maxUsers;
        MaxClients = maxClients;
    }

    /// <summary>Nível comercial do plano.</summary>
    public PlanTier Tier { get; }

    /// <summary>Teto de vagas de membro.</summary>
    public int MaxUsers { get; }

    /// <summary>Teto de clients OIDC ativos.</summary>
    public int MaxClients { get; }

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Tier;
        yield return MaxUsers;
        yield return MaxClients;
    }
}
