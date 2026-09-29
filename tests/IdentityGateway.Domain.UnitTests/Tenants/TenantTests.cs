using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Domain.Tenants.Events;

namespace IdentityGateway.Domain.UnitTests.Tenants;

public sealed class TenantTests
{
    private static readonly DateTimeOffset Instante = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static Tenant TenantRegistrado(int maxUsers = 10) =>
        Tenant.Register("Acme", TenantSlug.Create("acme").Value, new Plan(PlanTier.Standard, maxUsers, 5), Instante);

    private static Tenant TenantAtivo(int maxUsers = 10)
    {
        Tenant tenant = TenantRegistrado(maxUsers);
        tenant.MarkProvisioned("org-externa-1");
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
    public void MarkProvisioned_AtivaEGuardaOIdExterno()
    {
        Tenant tenant = TenantRegistrado();

        tenant.MarkProvisioned("org-externa-1");

        tenant.Status.Should().Be(TenantStatus.Active);
        tenant.ExternalOrganizationId.Should().Be("org-externa-1");
        tenant.DomainEvents.OfType<TenantActivated>().Should().ContainSingle();
    }

    [Fact]
    public void MarkProvisioned_RepetidoComOMesmoId_NaoLevantaSegundoEvento()
    {
        // A mesma mensagem do Outbox pode ser entregue mais de uma vez: a segunda não pode duplicar efeito.
        Tenant tenant = TenantRegistrado();

        tenant.MarkProvisioned("org-externa-1");
        tenant.MarkProvisioned("org-externa-1");

        tenant.DomainEvents.OfType<TenantActivated>().Should().ContainSingle();
    }

    [Fact]
    public void MarkProvisioned_APartirDeProvisioningFailed_Ativa()
    {
        // É o retry manual: o provisionamento falhou, alguém reprocessou o evento, e agora deu certo.
        Tenant tenant = TenantRegistrado();
        tenant.MarkProvisioningFailed();

        tenant.MarkProvisioned("org-externa-1");

        tenant.Status.Should().Be(TenantStatus.Active);
    }

    [Fact]
    public void MarkProvisioned_ComOutroIdExternoEstandoAtivo_Lanca()
    {
        Tenant tenant = TenantAtivo();

        Action ativar = () => tenant.MarkProvisioned("org-externa-2");

        ativar.Should().Throw<DomainInvariantViolation>();
    }

    [Fact]
    public void MarkProvisioningFailed_APartirDePending_MarcaFalha()
    {
        Tenant tenant = TenantRegistrado();

        tenant.MarkProvisioningFailed();

        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
    }

    [Fact]
    public void MarkProvisioningFailed_Repetido_NaoLanca()
    {
        // O consumidor de Fault pode reentregar a mesma mensagem.
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
        tenant.OccupiedSeats.Should().Be(1);
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

        tenant.OccupiedSeats.Should().Be(0);
    }

    [Fact]
    public void ReleaseSeat_SemVagaOcupada_Lanca()
    {
        // NÃO é idempotente por desenho: um clamp em zero esconderia dupla liberação, e o contador chegaria
        // a zero com o tenant cheio. Ver o XML doc do método.
        Tenant tenant = TenantAtivo();

        Action liberar = tenant.ReleaseSeat;

        liberar.Should().Throw<DomainInvariantViolation>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_ComNomeVazio_Lanca(string nome)
    {
        Action registrar = () => Tenant.Register(nome, SlugValido(), PlanoPadrao(), Instante);

        registrar.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Register_ComSlugNulo_Lanca()
    {
        Action registrar = () => Tenant.Register("Acme", null!, PlanoPadrao(), Instante);

        registrar.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Register_ComPlanoNulo_Lanca()
    {
        Action registrar = () => Tenant.Register("Acme", SlugValido(), null!, Instante);

        registrar.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Register_ComNomeCercadoDeEspacos_Apara()
    {
        // O nome é aparado mas mantém a caixa: é texto de exibição, não identificador. O slug, que é
        // identificador, normaliza a caixa — a diferença entre os dois é deliberada.
        var tenant = Tenant.Register("  Acme Corp  ", SlugValido(), PlanoPadrao(), Instante);

        tenant.Name.Should().Be("Acme Corp");
    }

    [Fact]
    public void Register_GuardaOInstanteDoRegistro()
    {
        var tenant = Tenant.Register("Acme", SlugValido(), PlanoPadrao(), Instante);

        tenant.RegisteredAt.Should().Be(Instante);
    }

    [Fact]
    public void Register_NormalizaOInstanteParaUtc()
    {
        // O Npgsql recusa gravar DateTimeOffset com offset diferente de zero numa coluna timestamptz. Normalizar
        // aqui tira do chamador a obrigação de lembrar disso.
        DateTimeOffset emBrasilia = new(2026, 9, 25, 9, 0, 0, TimeSpan.FromHours(-3));

        var tenant = Tenant.Register("Acme", SlugValido(), PlanoPadrao(), emBrasilia);

        tenant.RegisteredAt.Offset.Should().Be(TimeSpan.Zero);
        tenant.RegisteredAt.Should().Be(emBrasilia);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void MarkProvisioned_ComIdExternoVazio_Lanca(string idExterno)
    {
        // Sem esta guarda o tenant iria a Active com id externo em branco — indistinguível de "não
        // provisionado" para o job de reconciliação, que compara tenants com as Organizations existentes.
        Tenant tenant = TenantRegistrado();

        Action provisionar = () => tenant.MarkProvisioned(idExterno);

        provisionar.Should().Throw<ArgumentException>();
        tenant.Status.Should().Be(TenantStatus.Pending);
    }

    private static TenantSlug SlugValido() => TenantSlug.Create("acme").Value;

    private static Plan PlanoPadrao() => new(PlanTier.Standard, maxUsers: 10, maxClients: 5);
}
