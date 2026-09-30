using System.Text.Json;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Domain.Tenants.Events;
using IdentityGateway.Domain.ValueObjects;

namespace IdentityGateway.Domain.UnitTests.Tenants;

public sealed class TenantTests
{
    private static readonly DateTimeOffset Instante = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Convite = new(2026, 9, 25, 12, 5, 0, TimeSpan.Zero);
    private static readonly ExternalUserId Sub = ExternalUserId.From("sub-admin");

    private static Email EmailDoAdmin() => Email.Of("admin@acme.test").Value;

    private static Tenant TenantRegistrado(int maxUsers = 10) =>
        Tenant.Register(
            "Acme", TenantSlug.Create("acme").Value, new Plan(PlanTier.Standard, maxUsers, 5), EmailDoAdmin(), Instante);

    // Ativo pelo caminho de produção: a ativação ocupa a vaga do admin inicial.
    private static Tenant TenantAtivo(int maxUsers = 10)
    {
        Tenant tenant = TenantRegistrado(maxUsers);
        tenant.CompleteProvisioning("org-externa-1", Sub, Convite);
        return tenant;
    }

    [Fact]
    public void Register_NasceEmPending()
    {
        Tenant tenant = TenantRegistrado();

        tenant.Status.Should().Be(TenantStatus.Pending);
        tenant.Name.Should().Be("Acme");
        tenant.Slug.Value.Should().Be("acme");
        tenant.OccupiedSeats.Should().Be(0);
        tenant.ExternalOrganizationId.Should().BeNull();
        tenant.OverSubscribed.Should().BeFalse();
    }

    [Fact]
    public void Register_LevantaTenantRegistered()
    {
        Tenant tenant = TenantRegistrado();

        tenant.DomainEvents.Should().ContainSingle()
            .Which.Should().BeOfType<TenantRegistered>()
            .Which.Slug.Should().Be("acme");
    }

    [Fact]
    public void Register_GeraIdentidadesDistintas()
    {
        TenantRegistrado().Id.Should().NotBe(TenantRegistrado().Id);
    }

    [Fact]
    public void Register_GuardaOEmailNormalizado()
    {
        // O que vai para a coluna é o endereço normalizado, o mesmo que a busca exata do Keycloak
        // compara.
        var tenant = Tenant.Register(
            "Acme", SlugValido(), PlanoPadrao(), Email.Of("  Admin@Acme.COM ").Value, Instante);

        tenant.InitialAdminEmail!.Value.Should().Be("admin@acme.com");
    }

    [Fact]
    public void Register_ComEmailNulo_Lanca()
    {
        // Sem o e-mail o tenant nasceria trancado: ninguém seria convidado como tenant-admin (§9.1).
        Action registrar = () => Tenant.Register("Acme", SlugValido(), PlanoPadrao(), null!, Instante);

        registrar.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Register_TenantRegisteredNaoCarregaOEmail()
    {
        // D1: o evento vira mensagem no Outbox e, com o broker, no RabbitMQ. O e-mail fica só na coluna.
        Tenant tenant = TenantRegistrado();
        IDomainEvent evento = tenant.DomainEvents.Single();

        string json = JsonSerializer.Serialize(evento, evento.GetType());

        json.Should().NotContain("admin@acme.test");
    }

    [Fact]
    public void HasSeatAvailable_ComVaga_Verdadeiro()
    {
        TenantRegistrado(maxUsers: 1).HasSeatAvailable.Should().BeTrue();
    }

    [Fact]
    public void HasSeatAvailable_ComPlanoSemVagas_Falso()
    {
        // O catálogo recusa maxUsers < 1 na subida, mas o Plan gravado no tenant pode ser antigo (D14).
        TenantRegistrado(maxUsers: 0).HasSeatAvailable.Should().BeFalse();
    }

    [Fact]
    public void CompleteProvisioning_AtivaOcupaUmaVagaCriaOMemberEApagaOEmail()
    {
        Tenant tenant = TenantRegistrado();

        Member admin = tenant.CompleteProvisioning("org-externa-1", Sub, Convite);

        tenant.Status.Should().Be(TenantStatus.Active);
        tenant.ExternalOrganizationId.Should().Be("org-externa-1");
        tenant.OccupiedSeats.Should().Be(1, "a vaga do admin é reservada na ativação, exatamente uma (D2)");
        tenant.InitialAdminEmail.Should().BeNull("o e-mail só existe em tenant Pending (D1)");

        admin.TenantId.Should().Be(tenant.Id);
        admin.ExternalUserId.Should().Be(Sub);
        admin.Status.Should().Be(MemberStatus.Invited);
        admin.InvitedAt.Should().Be(Convite);
    }

    [Fact]
    public void CompleteProvisioning_LevantaTenantActivatedUmaVez()
    {
        Tenant tenant = TenantRegistrado();

        tenant.CompleteProvisioning("org-externa-1", Sub, Convite);

        tenant.DomainEvents.OfType<TenantActivated>().Should().ContainSingle();
    }

    [Fact]
    public void CompleteProvisioning_NormalizaInvitedAtParaUtc()
    {
        Tenant tenant = TenantRegistrado();
        DateTimeOffset emBrasilia = new(2026, 9, 25, 9, 5, 0, TimeSpan.FromHours(-3));

        Member admin = tenant.CompleteProvisioning("org-externa-1", Sub, emBrasilia);

        admin.InvitedAt.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void CompleteProvisioning_ComTenantAtivo_Lanca()
    {
        // Inversão deliberada em relação ao MarkProvisioned, que aceitava Active: uma segunda ativação criaria um
        // segundo Member e ocuparia uma segunda vaga. Quem entrega a mensagem repetida é o handler, que só age em
        // Pending.
        Tenant tenant = TenantAtivo();

        Action ativar = () => tenant.CompleteProvisioning("org-externa-1", Sub, Convite);

        ativar.Should().Throw<DomainInvariantViolation>();
        tenant.OccupiedSeats.Should().Be(1);
    }

    [Fact]
    public void CompleteProvisioning_ComTenantEmProvisioningFailed_Lanca()
    {
        // O MarkProvisioned aceitava sair de ProvisioningFailed; o retry manual, quando existir, devolve o tenant a
        // Pending antes (§6.2).
        Tenant tenant = TenantRegistrado();
        tenant.MarkProvisioningFailed();

        Action ativar = () => tenant.CompleteProvisioning("org-externa-1", Sub, Convite);

        ativar.Should().Throw<DomainInvariantViolation>();
    }

    [Fact]
    public void CompleteProvisioning_SemVaga_LancaSemTerMudadoNada()
    {
        // O handler verifica a vaga antes (D14); chegar aqui sem vaga é defeito. E o defeito não pode deixar o
        // agregado pela metade: nada muda antes de tudo ser validado.
        Tenant tenant = TenantRegistrado(maxUsers: 0);

        Action ativar = () => tenant.CompleteProvisioning("org-externa-1", Sub, Convite);

        ativar.Should().Throw<DomainInvariantViolation>();
        tenant.Status.Should().Be(TenantStatus.Pending);
        tenant.ExternalOrganizationId.Should().BeNull();
        tenant.OccupiedSeats.Should().Be(0);
        tenant.InitialAdminEmail.Should().NotBeNull();
        tenant.DomainEvents.OfType<TenantActivated>().Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CompleteProvisioning_ComIdExternoVazio_LancaSemMudarNada(string idExterno)
    {
        // Sem esta guarda o tenant iria a Active com id externo em branco — indistinguível de "não provisionado" para
        // o job de reconciliação.
        Tenant tenant = TenantRegistrado();

        Action ativar = () => tenant.CompleteProvisioning(idExterno, Sub, Convite);

        ativar.Should().Throw<ArgumentException>();
        tenant.Status.Should().Be(TenantStatus.Pending);
        tenant.OccupiedSeats.Should().Be(0);
    }

    [Fact]
    public void CompleteProvisioning_SemSub_LancaSemMudarNada()
    {
        Tenant tenant = TenantRegistrado();

        Action ativar = () => tenant.CompleteProvisioning("org-externa-1", null!, Convite);

        ativar.Should().Throw<ArgumentNullException>();
        tenant.Status.Should().Be(TenantStatus.Pending);
        tenant.OccupiedSeats.Should().Be(0);
    }

    [Fact]
    public void MarkProvisioningFailed_APartirDePending_MarcaFalha()
    {
        Tenant tenant = TenantRegistrado();

        tenant.MarkProvisioningFailed();

        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
    }

    [Fact]
    public void MarkProvisioningFailed_ApagaOEmail()
    {
        // D13: ProvisioningFailed não tem saída automática, e guardar o e-mail ali seria retenção sem prazo. O retry
        // manual recebe o e-mail de novo.
        Tenant tenant = TenantRegistrado();

        tenant.MarkProvisioningFailed();

        tenant.InitialAdminEmail.Should().BeNull();
    }

    [Fact]
    public void MarkProvisioningFailed_Repetido_NaoLanca()
    {
        Tenant tenant = TenantRegistrado();
        tenant.MarkProvisioningFailed();

        Action denovo = tenant.MarkProvisioningFailed;

        denovo.Should().NotThrow();
    }

    [Fact]
    public void MarkProvisioningFailed_ComTenantAtivo_Lanca()
    {
        Tenant tenant = TenantAtivo();

        Action falhar = tenant.MarkProvisioningFailed;

        falhar.Should().Throw<DomainInvariantViolation>();
    }

    [Fact]
    public void ReserveSeat_ComTenantAtivo_OcupaVaga()
    {
        Tenant tenant = TenantAtivo();

        Result resultado = tenant.ReserveSeat();

        resultado.IsSuccess.Should().BeTrue();
        tenant.OccupiedSeats.Should().Be(2, "a primeira vaga é do admin inicial");
    }

    [Fact]
    public void ReserveSeat_ComTenantNaoAtivo_Recusa()
    {
        Tenant tenant = TenantRegistrado();

        Result resultado = tenant.ReserveSeat();

        resultado.IsFailure.Should().BeTrue();
        resultado.Error.Code.Should().Be("Tenant.NaoAtivo");
        tenant.OccupiedSeats.Should().Be(0);
    }

    [Fact]
    public void ReserveSeat_NoLimiteDoPlano_RecusaSemUltrapassar()
    {
        Tenant tenant = TenantAtivo(maxUsers: 2);
        tenant.ReserveSeat();

        Result resultado = tenant.ReserveSeat();

        resultado.IsFailure.Should().BeTrue();
        resultado.Error.Code.Should().Be("Tenant.LimiteDeVagasAtingido");
        tenant.OccupiedSeats.Should().Be(2);
    }

    [Fact]
    public void ReleaseSeat_ComVagaOcupada_Libera()
    {
        Tenant tenant = TenantAtivo();
        tenant.ReserveSeat();

        tenant.ReleaseSeat();

        tenant.OccupiedSeats.Should().Be(1);
    }

    [Fact]
    public void ReleaseSeat_SemVagaOcupada_Lanca()
    {
        // NÃO é idempotente por desenho: um clamp em zero esconderia dupla liberação. Ver o XML doc do método.
        Tenant tenant = TenantAtivo();
        tenant.ReleaseSeat();

        Action liberar = tenant.ReleaseSeat;

        liberar.Should().Throw<DomainInvariantViolation>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_ComNomeVazio_Lanca(string nome)
    {
        Action registrar = () => Tenant.Register(nome, SlugValido(), PlanoPadrao(), EmailDoAdmin(), Instante);

        registrar.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Register_ComSlugNulo_Lanca()
    {
        Action registrar = () => Tenant.Register("Acme", null!, PlanoPadrao(), EmailDoAdmin(), Instante);

        registrar.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Register_ComPlanoNulo_Lanca()
    {
        Action registrar = () => Tenant.Register("Acme", SlugValido(), null!, EmailDoAdmin(), Instante);

        registrar.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Register_ComNomeCercadoDeEspacos_Apara()
    {
        // O nome é aparado mas mantém a caixa: é texto de exibição, não identificador. O slug, que é
        // identificador, normaliza a caixa — a diferença entre os dois é deliberada.
        var tenant = Tenant.Register("  Acme Corp  ", SlugValido(), PlanoPadrao(), EmailDoAdmin(), Instante);

        tenant.Name.Should().Be("Acme Corp");
    }

    [Fact]
    public void Register_GuardaOInstanteDoRegistro()
    {
        var tenant = Tenant.Register("Acme", SlugValido(), PlanoPadrao(), EmailDoAdmin(), Instante);

        tenant.RegisteredAt.Should().Be(Instante);
    }

    [Fact]
    public void Register_NormalizaOInstanteParaUtc()
    {
        // O Npgsql recusa gravar DateTimeOffset com offset diferente de zero numa coluna timestamptz. Normalizar
        // aqui tira do chamador a obrigação de lembrar disso.
        DateTimeOffset emBrasilia = new(2026, 9, 25, 9, 0, 0, TimeSpan.FromHours(-3));

        var tenant = Tenant.Register("Acme", SlugValido(), PlanoPadrao(), EmailDoAdmin(), emBrasilia);

        tenant.RegisteredAt.Offset.Should().Be(TimeSpan.Zero);
        tenant.RegisteredAt.Should().Be(emBrasilia);
    }

    private static TenantSlug SlugValido() => TenantSlug.Create("acme").Value;

    private static Plan PlanoPadrao() => new(PlanTier.Standard, maxUsers: 10, maxClients: 5);
}
