namespace IdentityGateway.Domain.Tenants;

/// <summary>
/// Nível comercial do plano.
/// </summary>
/// <remarks>
/// O nível não determina os limites: quem os informa é o catálogo de planos, para que uma mudança
/// comercial não exija recompilar o domínio. O <c>Tier</c> serve para distinguir planos que compartilham
/// limites e para a leitura humana.
/// </remarks>
public enum PlanTier
{
    /// <summary>Plano gratuito.</summary>
    Free,

    /// <summary>Plano pago padrão.</summary>
    Standard,

    /// <summary>Plano negociado.</summary>
    Enterprise,
}
