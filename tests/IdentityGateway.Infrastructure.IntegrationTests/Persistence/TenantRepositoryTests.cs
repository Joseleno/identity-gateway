using System.Diagnostics.CodeAnalysis;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.Persistence;
using IdentityGateway.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace IdentityGateway.Infrastructure.IntegrationTests.Persistence;

/// <summary>
/// Cobre a consulta de unicidade do slug contra o PostgreSQL.
/// </summary>
/// <remarks>
/// Contra o banco real e não com dublê: o <c>SlugExistsAsync</c> compara um value object convertido, e uma
/// comparação mal mapeada viraria avaliação client-side — que num dublê passaria despercebida e em produção
/// traria a tabela inteira para a memória.
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1859:Use concrete types when possible for improved performance",
    Justification = "O teste exercita o repositório PELA PORTA que a Application consome. Trocar por "
        + "TenantRepository provaria a classe concreta e não o contrato — e é o contrato que o handler usa.")]
public sealed class TenantRepositoryTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task SlugExistsAsync_VerdadeiroQuandoJaGravado()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        TenantSlug slug = TenantSlug.Create($"rep-{Guid.NewGuid():N}"[..18]).Value;

        await using AppDbContext contexto = postgres.CriarContexto();
        ITenantRepository repositorio = new TenantRepository(contexto);

        repositorio.Add(Tenant.Register("Repo", slug, new Plan(PlanTier.Free, 5, 1), PostgresFixture.Agora));
        await contexto.SaveChangesAsync(ct);

        bool existe = await repositorio.SlugExistsAsync(slug, ct);

        existe.Should().BeTrue();
    }

    [Fact]
    public async Task SlugExistsAsync_FalsoQuandoNaoExiste()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        TenantSlug inexistente = TenantSlug.Create($"nao-{Guid.NewGuid():N}"[..18]).Value;

        await using AppDbContext contexto = postgres.CriarContexto();
        ITenantRepository repositorio = new TenantRepository(contexto);

        bool existe = await repositorio.SlugExistsAsync(inexistente, ct);

        existe.Should().BeFalse();
    }

    [Fact]
    public async Task Add_NaoGravaSozinho()
    {
        // O commit e do TransactionBehavior. Um repositorio que salvasse sozinho tiraria o INSERT e a mensagem
        // do Outbox do mesmo SaveChanges.
        CancellationToken ct = TestContext.Current.CancellationToken;
        TenantSlug slug = TenantSlug.Create($"sem-{Guid.NewGuid():N}"[..18]).Value;

        await using AppDbContext contexto = postgres.CriarContexto();
        ITenantRepository repositorio = new TenantRepository(contexto);

        repositorio.Add(Tenant.Register("Sem commit", slug, new Plan(PlanTier.Free, 5, 1), PostgresFixture.Agora));

        await using AppDbContext outro = postgres.CriarContexto();
        ITenantRepository leitura = new TenantRepository(outro);

        bool existe = await leitura.SlugExistsAsync(slug, ct);

        existe.Should().BeFalse();
    }

    [Fact]
    public async Task GetAsync_DevolveOTenantRastreado()
    {
        // Rastreado é contrato: o ProvisionTenantHandler muda o estado e quem grava é o TransactionBehavior, sem
        // chamar Update. Um GetAsync com AsNoTracking faria o provisionamento "funcionar" sem nunca persistir.
        CancellationToken ct = TestContext.Current.CancellationToken;
        TenantSlug slug = TenantSlug.Create($"get-{Guid.NewGuid():N}"[..18]).Value;
        var tenant = Tenant.Register("Get", slug, new Plan(PlanTier.Free, 5, 1), PostgresFixture.Agora);
        await using (AppDbContext escrita = postgres.CriarContexto())
        {
            escrita.Tenants.Add(tenant);
            await escrita.SaveChangesAsync(ct);
        }

        await using (AppDbContext contexto = postgres.CriarContexto())
        {
            ITenantRepository repositorio = new TenantRepository(contexto);
            Tenant? lido = await repositorio.GetAsync(tenant.Id, ct);
            lido.Should().NotBeNull();
            lido!.MarkProvisioned("org-get");
            await contexto.SaveChangesAsync(ct);
        }

        await using AppDbContext conferencia = postgres.CriarContexto();
        Tenant gravado = await conferencia.Tenants.SingleAsync(item => item.Id == tenant.Id, ct);
        gravado.Status.Should().Be(TenantStatus.Active);
    }

    [Fact]
    public async Task GetAsync_NuloQuandoNaoExiste()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using AppDbContext contexto = postgres.CriarContexto();
        ITenantRepository repositorio = new TenantRepository(contexto);

        Tenant? lido = await repositorio.GetAsync(TenantId.New(), ct);

        lido.Should().BeNull();
    }
}
