using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Domain.UnitTests.Tenants;

public sealed class TenantIdTests
{
    [Fact]
    public void New_GeraIdentidadesDistintas()
    {
        var um = TenantId.New();
        var outro = TenantId.New();

        um.Should().NotBe(outro);
    }

    [Fact]
    public void DoisIdsComOMesmoGuid_SaoIguais()
    {
        var guid = Guid.CreateVersion7();

        new TenantId(guid).Should().Be(new TenantId(guid));
    }
}
