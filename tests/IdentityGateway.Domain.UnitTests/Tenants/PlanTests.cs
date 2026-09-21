using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Domain.UnitTests.Tenants;

public sealed class PlanTests
{
    [Fact]
    public void GuardaOsLimitesInformados()
    {
        var plano = new Plan(PlanTier.Standard, maxUsers: 50, maxClients: 5);

        plano.Tier.Should().Be(PlanTier.Standard);
        plano.MaxUsers.Should().Be(50);
        plano.MaxClients.Should().Be(5);
    }

    [Fact]
    public void DoisPlanosComOsMesmosLimites_SaoIguais()
    {
        var um = new Plan(PlanTier.Free, maxUsers: 5, maxClients: 1);
        var outro = new Plan(PlanTier.Free, maxUsers: 5, maxClients: 1);

        um.Should().Be(outro);
    }

    [Fact]
    public void ComMaxUsersNegativo_Lanca()
    {
        // Recusar aqui, e não adiante: um MaxUsers negativo faria ReserveSeat recusar toda reserva com
        // "o plano não admite mais de -5 membros", mensagem que descreve o sintoma e esconde a causa.
        Action criar = () => _ = new Plan(PlanTier.Free, maxUsers: -5, maxClients: 1);

        criar.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ComMaxClientsNegativo_Lanca()
    {
        Action criar = () => _ = new Plan(PlanTier.Free, maxUsers: 5, maxClients: -1);

        criar.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ComLimitesZerados_Aceita()
    {
        // Zero é decisão comercial possível, não impossibilidade — diferente de negativo.
        Action criar = () => _ = new Plan(PlanTier.Free, maxUsers: 0, maxClients: 0);

        criar.Should().NotThrow();
    }

    [Fact]
    public void PlanosDeTiersDiferentes_NaoSaoIguais()
    {
        var gratuito = new Plan(PlanTier.Free, maxUsers: 5, maxClients: 1);
        var pago = new Plan(PlanTier.Standard, maxUsers: 5, maxClients: 1);

        gratuito.Should().NotBe(pago);
    }
}
