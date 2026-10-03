using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Tenants.GetTenant;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;
using NSubstitute;

namespace IdentityGateway.Application.UnitTests.Tenants.GetTenant;

public sealed class GetTenantHandlerTests
{
    private readonly ITenantQueries _consultas = Substitute.For<ITenantQueries>();

    [Fact]
    public async Task TenantExistente_DevolveOsCamposComStatusETierEmTexto()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var tenant = TenantId.New();
        DateTimeOffset registro = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        _consultas.GetDetailsAsync(tenant, Arg.Any<CancellationToken>())
            .Returns(new TenantDetailsView(
                tenant,
                "Acme Corp",
                TenantSlug.Create("acme").Value,
                TenantStatus.Suspended,
                new Plan(PlanTier.Enterprise, 500, 20),
                7,
                registro));

        Result<TenantDetailsResponse> resultado = await new GetTenantHandler(_consultas)
            .Handle(new GetTenantQuery(tenant), ct);

        resultado.Value.Should().Be(new TenantDetailsResponse(
            tenant.Value, "Acme Corp", "acme", "Suspended", new TenantPlanResponse("Enterprise", 500, 20), 7, registro));
    }

    [Fact]
    public async Task TenantInexistente_DevolveNotFound()
    {
        // O handler segue o padrão e diz "não encontrado". Quem decide que isso vira 403, e não 404, é o módulo da Api.
        CancellationToken ct = TestContext.Current.CancellationToken;

        Result<TenantDetailsResponse> resultado = await new GetTenantHandler(_consultas)
            .Handle(new GetTenantQuery(TenantId.New()), ct);

        resultado.IsFailure.Should().BeTrue();
        resultado.Error.Code.Should().Be("Tenant.NaoEncontrado");
    }
}
