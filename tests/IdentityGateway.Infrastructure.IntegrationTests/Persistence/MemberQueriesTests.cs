using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.Persistence;
using IdentityGateway.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;

namespace IdentityGateway.Infrastructure.IntegrationTests.Persistence;

/// <summary>
/// A leitura da pertença, contra o PostgreSQL: pelo tenant <b>e</b> pelo <c>sub</c>, nunca só por um deles.
/// </summary>
/// <remarks>
/// É a consulta de que a policy <c>TenantAdmin</c> depende (ADR-011). O erro que estes testes existem para pegar é o
/// filtro que esquece o tenant: ele acharia o membro de outro tenant e diria "é membro".
/// </remarks>
public sealed class MemberQueriesTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static TenantSlug SlugUnico() => TenantSlug.Create($"mq-{Guid.NewGuid():N}"[..18]).Value;

    private static ExternalUserId SubUnico() => ExternalUserId.From(Guid.NewGuid().ToString());

    /// <summary>Registra um tenant e o ativa pelo caminho de domínio, com o admin dado como membro.</summary>
    private async Task<TenantId> TenantComAdminAsync(ExternalUserId sub, CancellationToken ct)
    {
        var tenant = Tenant.Register(
            "Consultas", SlugUnico(), new Plan(PlanTier.Free, 5, 1), PostgresFixture.EmailDoAdmin(), PostgresFixture.Agora);

        await using AppDbContext contexto = postgres.CriarContexto();
        contexto.Tenants.Add(tenant);
        await contexto.SaveChangesAsync(ct);

        Member admin = tenant.CompleteProvisioning($"org-{Guid.NewGuid():N}", sub, PostgresFixture.Agora);
        contexto.Members.Add(admin);
        await contexto.SaveChangesAsync(ct);

        return tenant.Id;
    }

    private async Task<MemberStatus?> ConsultarAsync(TenantId tenant, ExternalUserId sub, CancellationToken ct)
    {
        // Contexto novo: a consulta lê do banco, e não do que o contexto de escrita ainda tem rastreado.
        await using AppDbContext contexto = postgres.CriarContexto();

        return await new MemberQueries(contexto).GetStatusAsync(tenant, sub, ct);
    }

    [Fact]
    public async Task MembroDoTenant_DevolveOStatusGravado()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ExternalUserId sub = SubUnico();
        TenantId tenant = await TenantComAdminAsync(sub, ct);

        (await ConsultarAsync(tenant, sub, ct)).Should().Be(MemberStatus.Invited);

        // O status vem da coluna, e não de um valor fixo: muda no banco, muda na resposta.
        await using (AppDbContext contexto = postgres.CriarContexto())
        {
            await contexto.Database.ExecuteSqlAsync(
                $"UPDATE members SET status = 'Deactivated' WHERE tenant_id = {tenant.Value}", ct);
        }

        (await ConsultarAsync(tenant, sub, ct)).Should().Be(MemberStatus.Deactivated);
    }

    [Fact]
    public async Task MembroDeOutroTenant_NaoEAchado()
    {
        // O mesmo sub é membro do tenant A. Perguntar por ele no tenant B responde nulo — e não o status dele em A.
        CancellationToken ct = TestContext.Current.CancellationToken;
        ExternalUserId sub = SubUnico();
        TenantId tenantA = await TenantComAdminAsync(sub, ct);
        TenantId tenantB = await TenantComAdminAsync(SubUnico(), ct);

        (await ConsultarAsync(tenantB, sub, ct)).Should().BeNull();
        (await ConsultarAsync(tenantA, sub, ct)).Should().Be(MemberStatus.Invited, "controle: no tenant dele, é achado");
    }

    [Fact]
    public async Task OutroSubNoMesmoTenant_NaoEAchado()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        TenantId tenant = await TenantComAdminAsync(SubUnico(), ct);

        (await ConsultarAsync(tenant, SubUnico(), ct)).Should().BeNull();
    }

    [Fact]
    public async Task TenantQueNaoExiste_DevolveNulo()
    {
        // Não há Member de um tenant que não existe: é por isso que a rota responde 403, e não 404, sem consultar o
        // tenant antes de autorizar.
        CancellationToken ct = TestContext.Current.CancellationToken;

        (await ConsultarAsync(TenantId.New(), SubUnico(), ct)).Should().BeNull();
    }
}
