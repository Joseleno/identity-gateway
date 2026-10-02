namespace IdentityGateway.Api.Authorization;

/// <summary>
/// Os nomes das policies de autorização. Constantes, para um nome digitado errado ser erro de compilação — com texto
/// solto, o endpoint subiria e responderia 500 no primeiro pedido ("policy não encontrada").
/// </summary>
internal static class Policies
{
    /// <summary>Operador da plataforma: o claim <c>roles</c> traz <c>platform-admin</c>.</summary>
    public const string PlatformAdmin = "PlatformAdmin";

    /// <summary>
    /// Quem administra o tenant da rota: <c>tenant-admin</c>, sem <c>platform-admin</c>, com o <c>tenant_id</c> do token
    /// igual ao da rota.
    /// </summary>
    public const string TenantAdmin = "TenantAdmin";

    /// <summary>
    /// As policies que decidem pelo tenant da rota. Toda rota que usa uma delas precisa ter <c>{tenantId}</c> no
    /// template: sem o parâmetro, o <see cref="SameTenantRequirement"/> nega sempre. Um teste de subida confere.
    /// </summary>
    public static IReadOnlyList<string> DeTenant { get; } = [TenantAdmin];
}
