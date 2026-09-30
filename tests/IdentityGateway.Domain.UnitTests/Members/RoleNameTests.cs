using IdentityGateway.Domain.Members;

namespace IdentityGateway.Domain.UnitTests.Members;

public sealed class RoleNameTests
{
    [Fact]
    public void TenantAdmin_EONomeDoPapelDeRealm()
    {
        // O nome tem de bater com o papel declarado no realm (keycloak/bootstrap): é por ele que o adaptador o acha.
        RoleName.TenantAdmin.Value.Should().Be("tenant-admin");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Tenant-Admin")]
    [InlineData("-admin")]
    [InlineData("admin--x")]
    [InlineData("admin x")]
    public void From_ForaDoFormato_Lanca(string valor)
    {
        Action criar = () => RoleName.From(valor);

        criar.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void From_ComOMesmoNome_IgualAoCatalogado()
    {
        RoleName.From("tenant-admin").Should().Be(RoleName.TenantAdmin);
    }
}
