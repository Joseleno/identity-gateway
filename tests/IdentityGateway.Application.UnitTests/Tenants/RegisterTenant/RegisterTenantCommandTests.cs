using IdentityGateway.Application.Tenants.RegisterTenant;

namespace IdentityGateway.Application.UnitTests.Tenants.RegisterTenant;

public sealed class RegisterTenantCommandTests
{
    [Fact]
    public void ToString_NaoContemOEmailNemONome()
    {
        // O ToString gerado do record imprimiria todas as propriedades num log que formatasse o command (D15). O nome
        // do tenant fica de fora pela regra que já vale para os logs (ProvisioningLogs).
        RegisterTenantCommand comando = new("Acme Segredo", "acme", "free", "segredo@acme.test");

        string texto = comando.ToString();

        texto.Should().NotContain("segredo@acme.test")
            .And.NotContain("Acme Segredo")
            .And.Contain("acme");
    }
}
