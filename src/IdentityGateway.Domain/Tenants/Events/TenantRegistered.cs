using IdentityGateway.Domain.Common;

namespace IdentityGateway.Domain.Tenants.Events;

/// <summary>
/// Um tenant foi registrado e aguarda provisionamento.
/// </summary>
/// <remarks>
/// <para>
/// É este evento que dispara o provisionamento: gravado no Outbox na mesma transação do <c>INSERT</c>, ele
/// garante que ou os dois acontecem ou nenhum. Registrar o tenant chamando o Keycloak direto deixaria um
/// órfão de cada lado sempre que o outro falhasse.
/// </para>
/// <para>
/// <b>O slug viaja como <c>string</c>, não como <see cref="TenantSlug"/>.</b> A mensagem do Outbox é
/// gravada em JSON e relida por <c>JsonSerializer.Deserialize(content, tipo, …)</c> — um value object com
/// construtor privado não tem como ser reconstruído ali, e a desserialização lança
/// <c>NotSupportedException</c>. O efeito seria pior que um erro visível: toda mensagem
/// <c>tenant-registered</c> esgotaria as tentativas e iria para dead-letter, e nenhum tenant jamais
/// completaria o provisionamento.
/// </para>
/// <para>
/// Vale também como regra geral: <b>payload de evento é contrato de fio</b>, e tipo de domínio com
/// invariante no construtor é péssimo contrato de fio. Quem consome reconstrói o value object se precisar
/// — a validação já aconteceu antes de o tenant existir.
/// </para>
/// </remarks>
/// <param name="TenantId">Identidade do tenant registrado.</param>
/// <param name="Slug">Slug que virará o alias da Organization, já normalizado e validado.</param>
public sealed record TenantRegistered(TenantId TenantId, string Slug) : IDomainEvent
{
    /// <inheritdoc />
    public DateTimeOffset OccurredOn { get; } = DateTimeOffset.UtcNow;
}
