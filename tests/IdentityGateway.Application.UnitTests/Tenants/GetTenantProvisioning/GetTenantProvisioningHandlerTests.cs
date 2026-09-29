using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Tenants.GetTenantProvisioning;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;
using NSubstitute;

namespace IdentityGateway.Application.UnitTests.Tenants.GetTenantProvisioning;

public sealed class GetTenantProvisioningHandlerTests
{
    private readonly ITenantQueries _consultas = Substitute.For<ITenantQueries>();

    [Fact]
    public async Task TenantExistente_DevolveStatusComoTexto()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var tenant = TenantId.New();
        DateTimeOffset registro = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        _consultas.GetProvisioningAsync(tenant, Arg.Any<CancellationToken>())
            .Returns(new TenantProvisioningView(tenant, TenantStatus.ProvisioningFailed, registro));

        Result<TenantProvisioningResponse> resultado =
            await new GetTenantProvisioningHandler(_consultas).Handle(new GetTenantProvisioningQuery(tenant), ct);

        resultado.Value.Should().Be(new TenantProvisioningResponse(tenant.Value, "ProvisioningFailed", registro));
    }

    [Fact]
    public async Task TenantInexistente_DevolveNotFound()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        Result<TenantProvisioningResponse> resultado = await new GetTenantProvisioningHandler(_consultas)
            .Handle(new GetTenantProvisioningQuery(TenantId.New()), ct);

        resultado.IsFailure.Should().BeTrue();
        resultado.Error.Code.Should().Be("Tenant.NaoEncontrado");
    }
}
