using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.Persistence;
using IdentityGateway.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IdentityGateway.Infrastructure.IntegrationTests.Persistence;

/// <summary>
/// O <see cref="Member"/> sobrevive à ida e à volta do banco, com auditoria, e o índice único vale.
/// </summary>
public sealed class MapeamentoDeMemberTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static TenantSlug SlugUnico() => TenantSlug.Create($"mem-{Guid.NewGuid():N}"[..18]).Value;

    /// <summary>Registra, ativa pelo caminho de domínio e grava o admin pelo repositório — como o handler fará.</summary>
    private async Task<Member> AtivarComAdminAsync(string sub, CancellationToken ct)
    {
        var tenant = Tenant.Register(
            "Membros", SlugUnico(), new Plan(PlanTier.Free, 5, 1), PostgresFixture.EmailDoAdmin(), PostgresFixture.Agora);

        await using (AppDbContext escrita = postgres.CriarContexto())
        {
            escrita.Tenants.Add(tenant);
            await escrita.SaveChangesAsync(ct);
        }

        await using AppDbContext contexto = postgres.CriarContexto();
        Tenant rastreado = await contexto.Tenants.SingleAsync(item => item.Id == tenant.Id, ct);
        Member admin = rastreado.CompleteProvisioning("org-mem", ExternalUserId.From(sub), PostgresFixture.Agora);

        var membros = new MemberRepository(contexto);
        membros.Add(admin);
        await contexto.SaveChangesAsync(ct);

        return admin;
    }

    [Fact]
    public async Task Member_SobreviveAoRoundTripComAuditoria()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Member admin = await AtivarComAdminAsync($"sub-{Guid.NewGuid():N}", ct);

        await using AppDbContext leitura = postgres.CriarContexto();
        Member lido = await leitura.Members.SingleAsync(item => item.Id == admin.Id, ct);

        lido.TenantId.Should().Be(admin.TenantId);
        lido.ExternalUserId.Should().Be(admin.ExternalUserId);
        lido.Status.Should().Be(MemberStatus.Invited);
        lido.InvitedAt.Should().Be(PostgresFixture.Agora);
        lido.CreatedAt.Should().Be(PostgresFixture.Agora, "o AuditableInterceptor preenche no insert");
        lido.CreatedBy.Should().Be(PostgresFixture.Usuario);
        lido.UpdatedAt.Should().BeNull();
    }

    [Fact]
    public async Task Status_EGravadoComoTexto()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Member admin = await AtivarComAdminAsync($"sub-{Guid.NewGuid():N}", ct);

        await using AppDbContext contexto = postgres.CriarContexto();
        List<string> status = await contexto.Database
            .SqlQuery<string>($"SELECT status AS \"Value\" FROM members WHERE id = {admin.Id.Value}")
            .ToListAsync(ct);

        status.Should().ContainSingle().Which.Should().Be("Invited");
    }

    [Fact]
    public async Task IndiceUnico_RecusaOMesmoSubNoMesmoTenant()
    {
        // É o índice que pega a entrega concorrente duplicada que escapasse do xmin do tenant.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string sub = $"sub-{Guid.NewGuid():N}";
        Member admin = await AtivarComAdminAsync(sub, ct);

        await using AppDbContext contexto = postgres.CriarContexto();
        Func<Task> duplicar = () => contexto.Database.ExecuteSqlAsync($"""
            INSERT INTO members (id, tenant_id, external_user_id, status, invited_at, created_at)
            VALUES ({Guid.CreateVersion7()}, {admin.TenantId.Value}, {sub}, 'Invited', now(), now())
            """, ct);

        await duplicar.Should().ThrowAsync<PostgresException>()
            .Where(excecao => excecao.SqlState == PostgresErrorCodes.UniqueViolation);
    }
}
