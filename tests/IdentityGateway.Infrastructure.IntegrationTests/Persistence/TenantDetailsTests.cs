using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.Persistence;
using IdentityGateway.Infrastructure.Persistence.Queries;

namespace IdentityGateway.Infrastructure.IntegrationTests.Persistence;

/// <summary>
/// A projeção de leitura do tenant, contra o PostgreSQL.
/// </summary>
/// <remarks>
/// O que só o banco real prova: que o <c>Plan</c>, um tipo complexo achatado em três colunas, volta inteiro numa
/// projeção com <c>Select</c>, e que o slug e o status, gravados como texto, voltam como os tipos do domínio.
/// </remarks>
public sealed class TenantDetailsTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private async Task<Tenant> RegistrarAsync(Plan plano, CancellationToken ct)
    {
        var tenant = Tenant.Register(
            "  Leitura Ltda  ",
            TenantSlug.Create($"td-{Guid.NewGuid():N}"[..18]).Value,
            plano,
            PostgresFixture.EmailDoAdmin(),
            PostgresFixture.Agora);

        await using AppDbContext contexto = postgres.CriarContexto();
        contexto.Tenants.Add(tenant);
        await contexto.SaveChangesAsync(ct);

        return tenant;
    }

    private async Task<TenantDetailsView?> LerAsync(TenantId tenant, CancellationToken ct)
    {
        await using AppDbContext contexto = postgres.CriarContexto();

        return await new TenantQueries(contexto).GetDetailsAsync(tenant, ct);
    }

    [Fact]
    public async Task TenantPorProvisionar_VoltaComOPlanoInteiroEStatusPending()
    {
        // Em Pending, o e-mail do admin inicial ainda está na linha. A projeção não o traz: o tipo nem tem onde pôr.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = await RegistrarAsync(new Plan(PlanTier.Standard, 25, 3), ct);

        TenantDetailsView? lido = await LerAsync(tenant.Id, ct);

        lido.Should().Be(new TenantDetailsView(
            tenant.Id,
            "Leitura Ltda",
            tenant.Slug,
            TenantStatus.Pending,
            new Plan(PlanTier.Standard, 25, 3),
            OccupiedSeats: 0,
            PostgresFixture.Agora));
    }

    [Fact]
    public async Task TenantAtivado_VoltaActiveComAVagaDoAdmin()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = await RegistrarAsync(new Plan(PlanTier.Free, 5, 1), ct);

        await using (AppDbContext contexto = postgres.CriarContexto())
        {
            Tenant rastreado = contexto.Tenants.Single(item => item.Id == tenant.Id);
            Member admin = rastreado.CompleteProvisioning(
                $"org-{Guid.NewGuid():N}", ExternalUserId.From(Guid.NewGuid().ToString()), PostgresFixture.Agora);
            contexto.Members.Add(admin);
            await contexto.SaveChangesAsync(ct);
        }

        TenantDetailsView? lido = await LerAsync(tenant.Id, ct);

        lido!.Status.Should().Be(TenantStatus.Active);
        lido.OccupiedSeats.Should().Be(1);
        lido.Plan.Should().Be(new Plan(PlanTier.Free, 5, 1));
    }

    [Fact]
    public async Task TenantQueNaoExiste_DevolveNulo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        (await LerAsync(TenantId.New(), ct)).Should().BeNull();
    }
}
