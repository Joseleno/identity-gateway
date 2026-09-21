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
    public void PlanosDeTiersDiferentes_NaoSaoIguais()
    {
        var gratuito = new Plan(PlanTier.Free, maxUsers: 5, maxClients: 1);
        var pago = new Plan(PlanTier.Standard, maxUsers: 5, maxClients: 1);

        gratuito.Should().NotBe(pago);
    }
}
