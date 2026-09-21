using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants.Events;

namespace IdentityGateway.Domain.Tenants;

/// <summary>
/// Aggregate root do tenant. Guarda somente dados de governança; identidade e credenciais vivem no
/// Keycloak.
/// </summary>
public sealed class Tenant : AggregateRoot<TenantId>
{
    private Tenant(TenantId id, string name, TenantSlug slug, Plan plan)
        : base(id)
    {
        Name = name;
        Slug = slug;
        Plan = plan;
        Status = TenantStatus.Pending;
    }

    /// <summary>Nome de exibição.</summary>
    public string Name { get; private set; }

    /// <summary>Slug único e imutável; vira o alias da Organization no Keycloak.</summary>
    public TenantSlug Slug { get; private set; }

    /// <summary>Limites contratados.</summary>
    public Plan Plan { get; private set; }

    /// <summary>Estado no ciclo de vida.</summary>
    public TenantStatus Status { get; private set; }

    /// <summary>Id da Organization no Keycloak; nulo até o provisionamento concluir.</summary>
    public string? ExternalOrganizationId { get; private set; }

    /// <summary>Vagas de membro ocupadas.</summary>
    /// <remarks>
    /// Protegido por concorrência otimista na persistência (<c>xmin</c>): duas reservas simultâneas geram
    /// conflito de versão, e o retry reprocessa com o valor atual.
    /// </remarks>
    public int OccupiedSeats { get; private set; }

    /// <summary>Se o tenant excedeu o teto do plano por absorção externa.</summary>
    /// <remarks>
    /// Nenhuma operação da API liga isto. A única via é a absorção de usuários criados fora da Gateway,
    /// e ela é auditada — daí a invariante de vagas valer para a API, não para o contador em absoluto.
    /// </remarks>
    public bool OverSubscribed { get; private set; }

    /// <summary>
    /// Registra um tenant novo, ainda por provisionar.
    /// </summary>
    /// <remarks>
    /// Nenhuma chamada ao Keycloak acontece aqui. O evento levantado vira mensagem no Outbox, gravada na
    /// mesma transação do <c>INSERT</c>: com o Keycloak fora do ar nada fica órfão, e o provisionamento
    /// apenas acontece mais tarde.
    /// </remarks>
    /// <param name="name">Nome de exibição.</param>
    /// <param name="slug">Slug já validado.</param>
    /// <param name="plan">Plano vindo do catálogo.</param>
    /// <returns>O tenant em <see cref="TenantStatus.Pending"/>.</returns>
    public static Tenant Register(string name, TenantSlug slug, Plan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(plan);

        Tenant tenant = new(TenantId.New(), name, slug, plan);

        tenant.RaiseDomainEvent(new TenantRegistered(tenant.Id, slug.Value));

        return tenant;
    }

    /// <summary>
    /// Conclui o provisionamento e coloca o tenant em operação.
    /// </summary>
    /// <remarks>
    /// <b>Idempotente na entrada:</b> a mesma mensagem do Outbox pode ser entregue mais de uma vez, e a
    /// segunda entrega precisa ser inofensiva. Já estando ativo com o mesmo id externo, retorna sem efeito
    /// — inclusive sem levantar o evento de novo.
    /// </remarks>
    /// <param name="externalOrganizationId">Id da Organization criada no Keycloak.</param>
    /// <exception cref="DomainInvariantViolation">
    /// Se o tenant não estiver em <see cref="TenantStatus.Pending"/> nem em
    /// <see cref="TenantStatus.ProvisioningFailed"/>. Transição inválida é erro de programação.
    /// </exception>
    public void MarkProvisioned(string externalOrganizationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalOrganizationId);

        if (Status == TenantStatus.Active && ExternalOrganizationId == externalOrganizationId)
        {
            return;
        }

        EnsureStatusIn(TenantStatus.Pending, TenantStatus.ProvisioningFailed);

        ExternalOrganizationId = externalOrganizationId;
        Status = TenantStatus.Active;

        RaiseDomainEvent(new TenantActivated(Id));
    }

    /// <summary>
    /// Marca que o provisionamento falhou depois de esgotados os retries.
    /// </summary>
    /// <remarks>
    /// O tenant fica aguardando retry manual, e o evento segue disponível para reprocessamento. Idempotente
    /// porque o consumidor de Fault também pode reentregar.
    /// </remarks>
    /// <exception cref="DomainInvariantViolation">Se o tenant não estiver em Pending nem já falhado.</exception>
    public void MarkProvisioningFailed()
    {
        if (Status == TenantStatus.ProvisioningFailed)
        {
            return;
        }

        EnsureStatusIn(TenantStatus.Pending);

        Status = TenantStatus.ProvisioningFailed;
    }

    /// <summary>
    /// Reserva uma vaga do plano.
    /// </summary>
    /// <remarks>
    /// Devolve <see cref="Result"/> e não lança: tanto "o tenant não está ativo" quanto "as vagas
    /// acabaram" são respostas de negócio legítimas, e quem chama decide o que fazer com elas.
    /// <para>
    /// Duas reservas concorrentes geram conflito de versão no <c>SaveChanges</c> (<c>xmin</c>); o retry que
    /// reprocessa com o valor atual vive no pipeline de comandos — o agregado não sabe de persistência.
    /// </para>
    /// </remarks>
    /// <returns>Sucesso, ou o erro que impediu a reserva.</returns>
    public Result ReserveSeat()
    {
        if (Status != TenantStatus.Active)
        {
            return Result.Failure(TenantErrors.NotActive(Id));
        }

        if (OccupiedSeats >= Plan.MaxUsers)
        {
            return Result.Failure(TenantErrors.SeatLimitReached(Plan.MaxUsers));
        }

        OccupiedSeats++;

        return Result.Success();
    }

    /// <summary>
    /// Libera uma vaga ocupada.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Não é idempotente, de propósito.</b> Deve ser chamado apenas como consequência de uma transição
    /// de estado que de fato ocorreu — quem chama é o handler, e só quando a desativação do membro reporta
    /// que a transição aconteceu.
    /// </para>
    /// <para>
    /// A versão anterior da especificação usava <c>Math.Max(0, OccupiedSeats - 1)</c>. O clamp não protegia
    /// nada: escondia a divergência, impedindo o valor negativo que denunciaria a dupla liberação. Um POST
    /// de desativação repetido (timeout mais retry do cliente) decrementava duas vezes, e o <c>xmin</c> não
    /// pega — concorrência otimista detecta escrita simultânea, não repetida. Repetido N vezes, o contador
    /// chegava a zero com o tenant cheio, e o limite do plano deixava de existir em silêncio.
    /// </para>
    /// </remarks>
    /// <exception cref="DomainInvariantViolation">Se não houver vaga ocupada para liberar.</exception>
    public void ReleaseSeat()
    {
        if (OccupiedSeats == 0)
        {
            throw new DomainInvariantViolation(
                $"Tenant {Id.Value}: liberação de vaga sem vaga ocupada.");
        }

        OccupiedSeats--;
    }

    private void EnsureStatusIn(params TenantStatus[] permitidos)
    {
        if (Array.IndexOf(permitidos, Status) >= 0)
        {
            return;
        }

        throw new DomainInvariantViolation(
            $"Tenant {Id.Value}: operação exige o estado {string.Join(" ou ", permitidos)}, "
            + $"mas o tenant está em {Status}.");
    }
}
