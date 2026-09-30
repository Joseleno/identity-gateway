namespace IdentityGateway.Domain.Members;

/// <summary>
/// Estados do ciclo de vida de um membro (§6.1).
/// </summary>
/// <remarks>
/// Declara os seis estados da especificação, embora esta fatia só crie <see cref="Invited"/> — pelo mesmo motivo do
/// <c>TenantStatus</c>: uma fatia posterior não inventa nome diferente para um estado já nomeado. Persistido como
/// texto: a ordem dos membros não é dado de schema.
/// </remarks>
public enum MemberStatus
{
    /// <summary>Convidado; já ocupa vaga.</summary>
    Invited,

    /// <summary>Aceitou o convite (chega pela sincronização do ADR-007).</summary>
    Active,

    /// <summary>Desativado; a vaga foi liberada.</summary>
    Deactivated,

    /// <summary>O convite expirou sem aceite; a vaga foi liberada.</summary>
    Expired,

    /// <summary>O convite foi cancelado; a vaga foi liberada.</summary>
    Revoked,

    /// <summary>Apagado a pedido do titular. Terminal, sem dado que identifique a pessoa.</summary>
    Erased,
}
