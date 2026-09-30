using IdentityGateway.Api.Modules;

namespace IdentityGateway.Api.FunctionalTests;

public sealed class RegisterTenantRequestTests
{
    [Fact]
    public void ToString_NaoContemOEmail()
    {
        RegisterTenantRequest corpo = new("Acme", "acme", "free", "segredo@acme.test");

        corpo.ToString().Should().NotContain("segredo@acme.test");
    }
}
