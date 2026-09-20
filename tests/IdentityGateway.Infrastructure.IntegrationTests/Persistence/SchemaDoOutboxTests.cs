using IdentityGateway.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IdentityGateway.Infrastructure.IntegrationTests.Persistence;

/// <summary>
/// Prova que a migration cria de fato a tabela do Outbox.
/// </summary>
/// <remarks>
/// <para>
/// <b>Existe por causa de um verde vacuoso.</b> Sem nenhuma migration no assembly, <c>MigrateAsync</c> é um
/// no-op silencioso: não lança, cria apenas <c>__EFMigrationsHistory</c>, e toda a suíte continua passando
/// sobre um banco sem as tabelas que o código usa. O <c>DomainEventInterceptor</c> grava em
/// <c>outbox_messages</c> a cada commit com domain event, e o <c>OutboxWorker</c> a consulta em laço — os
/// dois quebrariam na primeira execução real, com a suíte inteira verde.
/// </para>
/// <para>
/// Consultar a tabela é o que distingue os dois estados: sem a migration, a consulta falha com
/// <c>relation "outbox_messages" does not exist</c>; com ela, devolve vazio.
/// </para>
/// </remarks>
public sealed class SchemaDoOutboxTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task AMigration_CriaATabelaDoOutbox()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using AppDbContext contexto = postgres.CriarContexto();

        int pendentes = await contexto.OutboxMessages.CountAsync(ct);

        pendentes.Should().Be(0);
    }

    [Fact]
    public async Task AMigration_CriaOIndiceParcialDePendentes()
    {
        // O índice com filtro `processed_on IS NULL` é o que faz o poller varrer só o que falta despachar.
        // Sem ele a consulta ainda funciona — e degrada conforme a tabela cresce, que é o tipo de regressão
        // que nenhum teste funcional acusa.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using AppDbContext contexto = postgres.CriarContexto();

        List<string> indices = await contexto.Database
            .SqlQuery<string>($"SELECT indexname AS \"Value\" FROM pg_indexes WHERE tablename = 'outbox_messages'")
            .ToListAsync(ct);

        indices.Should().Contain("ix_outbox_messages_pendentes");
    }
}
