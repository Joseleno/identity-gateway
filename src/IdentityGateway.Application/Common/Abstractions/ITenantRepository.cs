using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Application.Common.Abstractions;

/// <summary>
/// Persistência do agregado <see cref="Tenant"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Não expõe <c>SaveChanges</c>.</b> Quem fecha a unidade de trabalho é o <c>TransactionBehavior</c>, e é
/// isso que põe o <c>INSERT</c> do tenant e a mensagem do Outbox no mesmo <c>SaveChanges</c> — logo, na mesma
/// transação implícita do EF Core. Um repositório que salvasse sozinho quebraria essa garantia sem erro
/// nenhum aparecer.
/// </para>
/// </remarks>
public interface ITenantRepository
{
    /// <summary>Marca o tenant para inserção. Não grava.</summary>
    void Add(Tenant tenant);

    /// <summary>Se já existe tenant com o slug informado.</summary>
    /// <remarks>
    /// Dá a mensagem de negócio boa (<c>409</c> nomeando o slug). Não substitui o índice único do banco: entre
    /// esta consulta e o <c>INSERT</c> há uma janela em que outra requisição grava o mesmo slug.
    /// </remarks>
    Task<bool> SlugExistsAsync(TenantSlug slug, CancellationToken cancellationToken = default);

    /// <summary>Carrega o tenant para alteração, ou nulo se não existir.</summary>
    /// <remarks>
    /// Devolve o agregado <b>rastreado</b>: quem altera o estado não chama <c>Update</c> — o commit do
    /// <c>TransactionBehavior</c> grava o que mudou.
    /// </remarks>
    Task<Tenant?> GetAsync(TenantId tenantId, CancellationToken cancellationToken = default);
}
