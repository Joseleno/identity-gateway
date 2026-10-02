namespace IdentityGateway.Api.Authorization;

/// <summary>
/// Os nomes das policies de autorização. Constantes, para um nome digitado errado ser erro de compilação — com texto
/// solto, o endpoint subiria e responderia 500 no primeiro pedido ("policy não encontrada").
/// </summary>
internal static class Policies
{
    /// <summary>Operador da plataforma: o claim <c>roles</c> traz <c>platform-admin</c>.</summary>
    public const string PlatformAdmin = "PlatformAdmin";
}
