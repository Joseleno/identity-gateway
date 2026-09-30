using IdentityGateway.Domain.Members;

namespace IdentityGateway.Domain.UnitTests.Members;

public sealed class MemberIdTests
{
    [Fact]
    public void New_GeraIdentidadesDistintasEmVersao7()
    {
        var um = MemberId.New();
        var outro = MemberId.New();

        um.Should().NotBe(outro);
        um.Value.Version.Should().Be(7);
    }
}
