using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IdentityGateway.Infrastructure.IntegrationTests.Persistence;

/// <summary>
/// Prova que o tenant e a sua mensagem de Outbox gravam juntos, ou nenhum dos dois.
/// </summary>
/// <remarks>
/// <para>
/// <b>Não existe <c>BeginTransaction</c> no código, e é de propósito.</b> O <c>TransactionBehavior</c> chama
/// <c>SaveChangesAsync</c> uma vez, e o EF Core envolve um <c>SaveChanges</c> numa transação implícita; o
/// <c>DomainEventInterceptor</c> roda em <c>SavingChanges</c>, acrescentando os <c>OutboxMessage</c> ao change
/// tracker antes de o comando ir ao banco. Os dois <c>INSERT</c> saem na mesma unidade.
/// </para>
/// <para>
/// A propriedade observável, então, não é "existe uma transação" — é que os dois gravam juntos. O teste força a
/// falha por violação do índice único e confere que <b>nenhuma</b> mensagem sobrou do tenant recusado. Sem
/// atomicidade, a mensagem teria sido gravada e o contador daria 2 — é o par de estados que dá valor ao teste.
/// </para>
/// </remarks>
public sealed class AtomicidadeDoRegistroTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task OTenantEAMensagem_GravamJuntosOuNenhum()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string valor = $"atom-{Guid.NewGuid():N}"[..18];
        TenantSlug slug = TenantSlug.Create(valor).Value;

        await using (AppDbContext primeiro = postgres.CriarContexto())
        {
            primeiro.Tenants.Add(Tenant.Register("Primeiro", slug, new Plan(PlanTier.Free, 5, 1)));
            await primeiro.SaveChangesAsync(ct);
        }

        await using (AppDbContext segundo = postgres.CriarContexto())
        {
            segundo.Tenants.Add(Tenant.Register("Segundo", slug, new Plan(PlanTier.Free, 5, 1)));

            Func<Task> gravar = async () => await segundo.SaveChangesAsync(ct);

            await gravar.Should().ThrowAsync<DbUpdateException>();
        }

        // Uma mensagem, do tenant que de fato entrou — não duas.
        //
        // O filtro pelo slug roda em memória, e não no banco: `Content` é `jsonb`, e um `Contains` traduzido
        // pelo EF viraria `LIKE`, que o PostgreSQL não define para esse tipo (`operator does not exist:
        // jsonb ~~ jsonb`). O universo aqui é o de um teste, então trazer as mensagens do tipo e filtrar
        // depois é barato e diz exatamente o que se quer dizer.
        await using AppDbContext conferencia = postgres.CriarContexto();
        List<string> conteudos = await conferencia.OutboxMessages
            .Where(mensagem => mensagem.Type == "tenant-registered")
            .Select(mensagem => mensagem.Content)
            .ToListAsync(ct);

        conteudos.Where(conteudo => conteudo.Contains(valor, StringComparison.Ordinal))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task RegistroBemSucedido_GravaAMensagemComOSlugEmTexto()
    {
        // O payload carrega string, nunca value object: TenantRegistered com TenantSlug lançava
        // NotSupportedException ao voltar do Outbox, e toda mensagem iria a dead-letter.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string valor = $"ok-{Guid.NewGuid():N}"[..16];
        TenantSlug slug = TenantSlug.Create(valor).Value;

        await using AppDbContext contexto = postgres.CriarContexto();
        contexto.Tenants.Add(Tenant.Register("Ok", slug, new Plan(PlanTier.Free, 5, 1)));
        await contexto.SaveChangesAsync(ct);

        // Filtro em memória pelo mesmo motivo do teste acima: `Content` é `jsonb` e não aceita `LIKE`.
        List<string> conteudos = await contexto.OutboxMessages
            .Where(mensagem => mensagem.Type == "tenant-registered")
            .Select(mensagem => mensagem.Content)
            .ToListAsync(ct);

        conteudos.Where(conteudo => conteudo.Contains(valor, StringComparison.Ordinal))
            .Should().ContainSingle()
            .Which.Should().Contain($"\"{valor}\"");
    }
}
