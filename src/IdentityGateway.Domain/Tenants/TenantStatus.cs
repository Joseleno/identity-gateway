namespace IdentityGateway.Domain.Tenants;

/// <summary>
/// Estados do ciclo de vida de um tenant.
/// </summary>
/// <remarks>
/// <para>
/// O enum declara os oito estados da máquina documentada na especificação, embora o agregado ainda só
/// implemente as transições do caminho de registro. Declarar todos evita que uma fatia posterior invente
/// um nome diferente para um estado que a especificação já nomeou.
/// </para>
/// <para>
/// <see cref="Suspending"/> e <see cref="Terminating"/> são estados de passagem: a API responde <c>202</c>
/// e o efeito no Keycloak acontece depois. Sem eles não haveria como responder "aceito, ainda
/// processando" numa consulta feita nessa janela.
/// </para>
/// </remarks>
public enum TenantStatus
{
    /// <summary>Registrado, aguardando provisionamento no Keycloak.</summary>
    Pending,

    /// <summary>Provisionado e em operação.</summary>
    Active,

    /// <summary>Suspensão aceita, ainda sendo aplicada.</summary>
    Suspending,

    /// <summary>Suspenso: não aceita operação de gestão.</summary>
    Suspended,

    /// <summary>Encerramento aceito, ainda sendo aplicado.</summary>
    Terminating,

    /// <summary>Encerrado. Estado terminal.</summary>
    Terminated,

    /// <summary>Provisionamento falhou depois de esgotados os retries; aguarda retry manual.</summary>
    ProvisioningFailed,
}
