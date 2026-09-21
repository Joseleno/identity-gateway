using IdentityGateway.Domain.Common;

namespace IdentityGateway.Domain.UnitTests.Common;

public sealed class DomainInvariantViolationTests
{
    [Fact]
    public void ComMensagem_PreservaAMensagem()
    {
        DomainInvariantViolation excecao = new("Tenant X: liberação de vaga sem vaga ocupada.");

        excecao.Message.Should().Be("Tenant X: liberação de vaga sem vaga ocupada.");
    }
}
