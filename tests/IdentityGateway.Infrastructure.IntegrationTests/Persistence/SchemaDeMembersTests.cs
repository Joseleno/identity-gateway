using IdentityGateway.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IdentityGateway.Infrastructure.IntegrationTests.Persistence;

/// <summary>
/// A migration cria a tabela <c>members</c> com as colunas, a FK e o índice único da spec (§4.6).
/// </summary>
public sealed class SchemaDeMembersTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Colunas_TemTiposENulidadeDaSpec()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using AppDbContext contexto = postgres.CriarContexto();

        List<string> colunas = await contexto.Database
            .SqlQuery<string>($"""
                SELECT column_name || '|' || is_nullable || '|' || data_type AS "Value"
                  FROM information_schema.columns
                 WHERE table_name = 'members'
                 ORDER BY column_name
                """)
            .ToListAsync(ct);

        colunas.Should().Equal(
            "created_at|NO|timestamp with time zone",
            "created_by|YES|uuid",
            "external_user_id|NO|character varying",
            "id|NO|uuid",
            "invited_at|NO|timestamp with time zone",
            "status|NO|character varying",
            "tenant_id|NO|uuid",
            "updated_at|YES|timestamp with time zone",
            "updated_by|YES|uuid");
    }

    [Fact]
    public async Task ForeignKey_ApontaParaTenants()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using AppDbContext contexto = postgres.CriarContexto();

        List<string> referencias = await contexto.Database
            .SqlQuery<string>($"""
                SELECT ccu.table_name || '.' || ccu.column_name AS "Value"
                  FROM information_schema.table_constraints tc
                  JOIN information_schema.constraint_column_usage ccu ON ccu.constraint_name = tc.constraint_name
                 WHERE tc.table_name = 'members' AND tc.constraint_type = 'FOREIGN KEY'
                """)
            .ToListAsync(ct);

        referencias.Should().ContainSingle().Which.Should().Be("tenants.id");
    }

    [Fact]
    public async Task IndiceUnico_CobreTenantESub()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using AppDbContext contexto = postgres.CriarContexto();

        List<string> definicao = await contexto.Database
            .SqlQuery<string>($"""
                SELECT indexdef AS "Value"
                  FROM pg_indexes
                 WHERE tablename = 'members' AND indexname = 'ix_members_tenant_id_external_user_id'
                """)
            .ToListAsync(ct);

        definicao.Should().ContainSingle().Which.Should()
            .Contain("UNIQUE").And.Contain("(tenant_id, external_user_id)");
    }
}
