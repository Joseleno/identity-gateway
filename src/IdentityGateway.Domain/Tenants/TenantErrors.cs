using IdentityGateway.Domain.Errors;

namespace IdentityGateway.Domain.Tenants;

/// <summary>
/// Erros de negócio do tenant.
/// </summary>
/// <remarks>
/// Fora do <c>DomainErrors</c> geral porque pertencem a um agregado só. O catálogo central guarda o que
/// atravessa agregados; misturar os dois faria o arquivo crescer com o domínio inteiro.
/// </remarks>
public static class TenantErrors
{
    /// <summary>Operação que exige tenant em operação, com o tenant em outro estado.</summary>
    public static Error NotActive(TenantId tenantId) => Error.Conflict(
        "Tenant.NaoAtivo",
        $"O tenant '{tenantId.Value}' não está ativo.");

    /// <summary>Não existe tenant com o id informado.</summary>
    public static Error NotFound(TenantId tenantId) => Error.NotFound(
        "Tenant.NaoEncontrado",
        $"O tenant '{tenantId.Value}' não existe.");

    /// <summary>Todas as vagas do plano já estão ocupadas.</summary>
    public static Error SeatLimitReached(int maxUsers) => Error.Conflict(
        "Tenant.LimiteDeVagasAtingido",
        $"O plano não admite mais de {maxUsers} membros.");

    /// <summary>Já existe tenant com o slug informado.</summary>
    /// <remarks>
    /// <see cref="ErrorType.Conflict"/> e não <c>Validation</c>: o slug é bem formado, o que impede é o
    /// estado do sistema. A Api traduz em 409.
    /// </remarks>
    public static Error SlugInUse(TenantSlug slug) => Error.Conflict(
        "Tenant.SlugEmUso",
        $"O slug '{slug.Value}' já pertence a outro tenant.");

    /// <summary>O código de plano informado não existe no catálogo.</summary>
    public static Error UnknownPlan(string planCode) => Error.Validation(
        "Tenant.PlanoDesconhecido",
        $"Não existe plano com o código '{planCode}'.");
}
