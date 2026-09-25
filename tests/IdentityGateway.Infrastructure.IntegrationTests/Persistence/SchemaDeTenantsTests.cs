using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IdentityGateway.Infrastructure.IntegrationTests.Persistence;

/// <summary>
/// Prova que a migration cria a tabela e o índice único do slug.
/// </summary>
/// <remarks>
/// Espelha <c>SchemaDoOutboxTests</c> e existe pelo mesmo motivo: sem migration aplicada, <c>MigrateAsync</c> é
/// um no-op silencioso e a suíte passaria sobre um banco sem as tabelas que o código usa.
/// </remarks>
public sealed class SchemaDeTenantsTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task AMigration_CriaATabelaDeTenants()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using AppDbContext contexto = postgres.CriarContexto();

        int total = await contexto.Tenants.CountAsync(ct);

        total.Should().Be(0);
    }

    [Fact]
    public async Task OIndiceUnicoDoSlug_RecusaDuplicata()
    {
        // É esta constraint que fecha a janela entre o SELECT de unicidade do handler e o INSERT: duas
        // requisições concorrentes com o mesmo slug passariam as duas pela checagem em memória.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string valor = $"dup-{Guid.NewGuid():N}"[..18];
        TenantSlug slug = TenantSlug.Create(valor).Value;

        await using (AppDbContext primeiro = postgres.CriarContexto())
        {
            primeiro.Tenants.Add(Tenant.Register("Primeiro", slug, new Plan(PlanTier.Free, 5, 1), PostgresFixture.Agora));
            await primeiro.SaveChangesAsync(ct);
        }

        await using AppDbContext segundo = postgres.CriarContexto();
        segundo.Tenants.Add(Tenant.Register("Segundo", slug, new Plan(PlanTier.Free, 5, 1), PostgresFixture.Agora));

        Func<Task> gravar = async () => await segundo.SaveChangesAsync(ct);

        await gravar.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task RegisteredAt_EObrigatorioESemDefault()
    {
        // Sem default de propósito: o domínio sempre informa o instante, e um default no banco esconderia um
        // caminho de criação que esquecesse de informar.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using AppDbContext contexto = postgres.CriarContexto();

        List<string> coluna = await contexto.Database
            .SqlQuery<string>($"""
                SELECT is_nullable || '|' || coalesce(column_default, '') AS "Value"
                  FROM information_schema.columns
                 WHERE table_name = 'tenants' AND column_name = 'registered_at'
                """)
            .ToListAsync(ct);

        coluna.Should().ContainSingle().Which.Should().Be("NO|");
    }
}
