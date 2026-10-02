using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Application.Common.Abstractions;

/// <summary>
/// Leituras de tenant que não precisam do agregado.
/// </summary>
/// <remarks>
/// Separado do <see cref="ITenantRepository"/>: o repositório devolve o agregado rastreado, para ser alterado; aqui
/// são projeções sem rastreamento, para responder consulta. Ler um tenant não pede o agregado montado.
/// </remarks>
public interface ITenantQueries
{
    /// <summary>Estado do provisionamento, ou nulo se o tenant não existir.</summary>
    Task<TenantProvisioningView?> GetProvisioningAsync(TenantId tenantId, CancellationToken cancellationToken = default);

    /// <summary>O tenant, sem o e-mail do admin inicial, ou nulo se ele não existir.</summary>
    Task<TenantDetailsView?> GetDetailsAsync(TenantId tenantId, CancellationToken cancellationToken = default);
}

/// <summary>Projeção do estado de provisionamento de um tenant.</summary>
/// <param name="TenantId">Identidade do tenant.</param>
/// <param name="Status">Estado no ciclo de vida.</param>
/// <param name="RegisteredAt">Quando foi registrado, em UTC.</param>
public sealed record TenantProvisioningView(TenantId TenantId, TenantStatus Status, DateTimeOffset RegisteredAt);

/// <summary>Projeção de um tenant para leitura.</summary>
/// <remarks>
/// <b>Sem o e-mail do admin inicial.</b> A projeção não o seleciona, e por isso ele nem sai do banco. Serve à leitura
/// de um tenant e, depois, à listagem.
/// </remarks>
/// <param name="TenantId">Identidade do tenant.</param>
/// <param name="Name">Nome de exibição.</param>
/// <param name="Slug">Slug.</param>
/// <param name="Status">Estado no ciclo de vida.</param>
/// <param name="Plan">O plano contratado.</param>
/// <param name="OccupiedSeats">Vagas ocupadas.</param>
/// <param name="RegisteredAt">Quando foi registrado, em UTC.</param>
public sealed record TenantDetailsView(
    TenantId TenantId,
    string Name,
    TenantSlug Slug,
    TenantStatus Status,
    Plan Plan,
    int OccupiedSeats,
    DateTimeOffset RegisteredAt);
