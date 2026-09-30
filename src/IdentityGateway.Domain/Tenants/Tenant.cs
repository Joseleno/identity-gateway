using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants.Events;
using IdentityGateway.Domain.ValueObjects;

namespace IdentityGateway.Domain.Tenants;

/// <summary>
/// Aggregate root do tenant. Guarda somente dados de governança; identidade e credenciais vivem no
/// Keycloak — com uma exceção declarada e temporária, o e-mail do admin inicial (<see cref="InitialAdminEmail"/>).
/// </summary>
public sealed class Tenant : AggregateRoot<TenantId>
{
    /// <summary>
    /// Construtor exclusivo do EF Core. <b>Nunca chamado por código de domínio.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Existe porque o EF Core não vincula value object de múltiplos campos a parâmetro de
    /// construtor.</b> Ele materializa por construtor parametrizado casando parâmetros com propriedades
    /// mapeadas, mas recusa o <c>Plan</c>: tanto <c>OwnsOne</c> (navegação) quanto <c>ComplexProperty</c>
    /// falham com "No suitable constructor was found ... Cannot bind 'plan'" (dotnet/efcore#31621, em
    /// aberto na 10.0.12). <c>TenantId</c> e <c>TenantSlug</c> são conversões de valor único e vinculam
    /// normalmente.
    /// </para>
    /// <para>
    /// <b>Por que dois construtores, e não um só sem o plano:</b> com o plano fora do construtor único, o
    /// compilador deixaria de exigi-lo, e um caminho de criação novo poderia esquecê-lo — o erro só
    /// apareceria em uso. Mantendo o construtor de domínio abaixo com o plano obrigatório, a garantia
    /// volta a ser do compilador, e o <c>null!</c> fica confinado a este construtor, que só o ORM chama.
    /// O EF escolhe este por ser o único cujos parâmetros ele consegue vincular; a duplicidade não gera
    /// ambiguidade.
    /// </para>
    /// <para>
    /// A alternativa descartada era o <c>private Tenant() { }</c> com quatro <c>null!</c> — um agregado
    /// inteiro momentaneamente inválido, em vez de um único campo que o EF preenche logo em seguida.
    /// </para>
    /// </remarks>
    private Tenant(TenantId id, string name, TenantSlug slug)
        : base(id)
    {
        Name = name;
        Slug = slug;

        // Preenchido pelo EF logo após a construção, ao materializar o complex type. Inalcançável por
        // código de domínio: este construtor não tem chamador fora do ORM.
        Plan = null!;

        Status = TenantStatus.Pending;
    }

    /// <summary>
    /// Construtor do domínio: exige o plano, como toda criação legítima de tenant.
    /// </summary>
    /// <remarks>
    /// É o que <see cref="Register"/> usa. Ter o plano como parâmetro obrigatório é o que faz o compilador
    /// recusar um caminho de criação que o esqueça.
    /// </remarks>
    private Tenant(
        TenantId id, string name, TenantSlug slug, Plan plan, Email initialAdminEmail, DateTimeOffset registeredAt)
        : base(id)
    {
        Name = name;
        Slug = slug;
        Plan = plan;
        Status = TenantStatus.Pending;
        RegisteredAt = registeredAt;
        InitialAdminEmail = initialAdminEmail;
    }

    /// <summary>Nome de exibição.</summary>
    public string Name { get; private set; }

    /// <summary>Slug único e imutável; vira o alias da Organization no Keycloak.</summary>
    public TenantSlug Slug { get; private set; }

    /// <summary>Limites contratados.</summary>
    public Plan Plan { get; private set; }

    /// <summary>Estado no ciclo de vida.</summary>
    public TenantStatus Status { get; private set; }

    /// <summary>Quando o tenant foi registrado, em UTC.</summary>
    /// <remarks>
    /// É daqui que o provisionamento conta a janela de retry: a regra é "pendente há tempo demais", e isso é fato do
    /// tenant, não da mensagem que o transporta.
    /// </remarks>
    public DateTimeOffset RegisteredAt { get; private set; }

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

    /// <summary>E-mail do primeiro administrador, só enquanto o tenant está em <see cref="TenantStatus.Pending"/>.</summary>
    /// <remarks>
    /// <para>
    /// <b>Exceção declarada à regra "dados pessoais só no Keycloak" (D1).</b> O convite acontece depois do
    /// <c>POST</c>, no consumidor do Outbox, e o endereço precisa esperar em algum lugar. Fora do evento
    /// <c>TenantRegistered</c>, que o levaria ao Outbox e, com o broker, ao RabbitMQ.
    /// </para>
    /// <para>
    /// Apagado na mesma operação que ativa (<see cref="CompleteProvisioning"/>) <b>ou</b> que marca a falha
    /// (<see cref="MarkProvisioningFailed"/>, D13). Nulo também em tenant registrado antes da fatia C — o handler trata
    /// esse caso como falha permanente.
    /// </para>
    /// </remarks>
    public Email? InitialAdminEmail { get; private set; }

    /// <summary>Se ainda cabe um membro no plano.</summary>
    /// <remarks>
    /// Leitura, para o provisionamento recusar antes de tocar o Keycloak um tenant cujo plano não comporta o admin
    /// (D14) — o <c>Plan</c> gravado pode ser anterior à regra do catálogo que exige <c>maxUsers</c> de pelo menos 1.
    /// </remarks>
    public bool HasSeatAvailable => OccupiedSeats < Plan.MaxUsers;

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
    /// <param name="initialAdminEmail">E-mail de quem será convidado como <c>tenant-admin</c>.</param>
    /// <param name="registeredAt">Instante do registro; normalizado para UTC.</param>
    /// <returns>O tenant em <see cref="TenantStatus.Pending"/>.</returns>
    public static Tenant Register(
        string name, TenantSlug slug, Plan plan, Email initialAdminEmail, DateTimeOffset registeredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(initialAdminEmail);

        // O nome é aparado, mas não tem a caixa alterada, e a diferença em relação ao slug é deliberada:
        // o slug é identificador — `Acme` e `acme` seriam o mesmo tenant e precisam colidir —, enquanto o
        // nome é texto de exibição, e "IBM" não pode virar "ibm". Aparar o entorno resolve o espaço colado
        // sem tocar no que o cliente escolheu se chamar.
        Tenant tenant = new(
            TenantId.New(), name.Trim(), slug, plan, initialAdminEmail, registeredAt.ToUniversalTime());

        // O evento carrega só o id e o slug: o e-mail fica na coluna (D1).
        tenant.RaiseDomainEvent(new TenantRegistered(tenant.Id, slug.Value));

        return tenant;
    }

    /// <summary>
    /// Conclui o provisionamento: ativa o tenant, ocupa a vaga do admin inicial e devolve o <see cref="Member"/> dele.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Uma operação de domínio, num commit só (D2).</b> No Keycloak o convite já aconteceu; aqui, <c>Active</c>, a
    /// vaga, o membro em <c>Invited</c> e o e-mail apagado acontecem juntos ou não acontecem.
    /// </para>
    /// <para>
    /// <b>Valida tudo antes de mudar qualquer coisa.</b> Exige <see cref="TenantStatus.Pending"/> e vaga livre, e cria o
    /// membro — que valida os próprios argumentos — antes da primeira atribuição. Uma exceção nunca deixa o agregado
    /// pela metade.
    /// </para>
    /// <para>
    /// <b>Recusa <c>Active</c> e <c>ProvisioningFailed</c></b>, ao contrário do <c>MarkProvisioned</c> que substituiu
    /// (removido na fatia C): uma segunda ativação criaria um segundo membro. A mensagem repetida é tratada pelo
    /// handler, que só age em <c>Pending</c>.
    /// </para>
    /// </remarks>
    /// <param name="externalOrganizationId">Id da Organization no Keycloak.</param>
    /// <param name="adminUserId">O <c>sub</c> do admin convidado.</param>
    /// <param name="invitedAt">Instante do convite; normalizado para UTC.</param>
    /// <returns>O membro do admin, para o handler entregar ao repositório.</returns>
    /// <exception cref="DomainInvariantViolation">
    /// Fora de <c>Pending</c>, ou sem vaga — o handler verifica a vaga antes (D14), então chegar aqui sem ela é defeito.
    /// </exception>
    public Member CompleteProvisioning(
        string externalOrganizationId, ExternalUserId adminUserId, DateTimeOffset invitedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalOrganizationId);
        ArgumentNullException.ThrowIfNull(adminUserId);

        EnsureStatusIn(TenantStatus.Pending);

        if (!HasSeatAvailable)
        {
            throw new DomainInvariantViolation(
                $"Tenant {Id.Value}: ativação sem vaga para o admin inicial; o provisionamento verifica antes.");
        }

        var admin = Member.Invite(Id, adminUserId, invitedAt);

        ExternalOrganizationId = externalOrganizationId;
        Status = TenantStatus.Active;
        OcuparVaga();
        InitialAdminEmail = null;

        RaiseDomainEvent(new TenantActivated(Id));

        return admin;
    }

    /// <summary>
    /// Marca que o provisionamento falhou: erro permanente, ou janela de retry esgotada.
    /// </summary>
    /// <remarks>
    /// O tenant fica aguardando o retry manual, que o devolverá a <c>Pending</c> antes de reenfileirar (§6.2). <b>Apaga
    /// o e-mail do admin (D13):</b> este estado não tem saída automática, e guardá-lo seria retenção sem prazo; o retry
    /// manual recebe o e-mail de novo, o que também permite corrigir um endereço digitado errado. Não levanta evento —
    /// nada reage à falha hoje. Idempotente, porque a mesma mensagem pode ser reentregue.
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
        InitialAdminEmail = null;
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

    /// <summary>
    /// Ocupa uma vaga sem exigir <c>Active</c>.
    /// </summary>
    /// <remarks>
    /// Existe para <see cref="CompleteProvisioning"/>, que ocupa a vaga do admin no mesmo passo em que ativa.
    /// <see cref="ReserveSeat"/> continua exigindo <c>Active</c> para todo outro chamador (D2): afrouxá-lo enfraqueceria
    /// a invariante para quem vier depois.
    /// </remarks>
    private void OcuparVaga() => OccupiedSeats++;

    /// <summary>
    /// Exige que o tenant esteja em um dos estados informados.
    /// </summary>
    /// <remarks>
    /// <c>params ReadOnlySpan</c> e não <c>params TenantStatus[]</c>: o array seria alocado a cada
    /// chamada, inclusive quando a transição é válida e nada falha. Com o span, os valores ficam na pilha
    /// e o caminho feliz não aloca. Os chamadores não mudam.
    /// </remarks>
    private void EnsureStatusIn(params ReadOnlySpan<TenantStatus> permitidos)
    {
        if (permitidos.Contains(Status))
        {
            return;
        }

        throw new DomainInvariantViolation(
            $"Tenant {Id.Value}: operação exige o estado {string.Join(" ou ", permitidos.ToArray())}, "
            + $"mas o tenant está em {Status}.");
    }
}
