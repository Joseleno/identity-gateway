using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IdentityGateway.Infrastructure.IntegrationTests.Persistence;

/// <summary>
/// Prova que o agregado sobrevive à ida e à volta do banco.
/// </summary>
/// <remarks>
/// <para>
/// <b>É o teste que cobre a decisão de não dar ao <c>Tenant</c> um construtor sem parâmetro.</b> O EF Core
/// materializa pelo construtor parametrizado, casando parâmetros com propriedades por nome — e se esse casamento
/// se quebrar (renomear um parâmetro, por exemplo), a falha é de materialização em <b>runtime</b>, não de
/// compilação. Sem este teste, o sintoma apareceria na primeira leitura real.
/// </para>
/// <para>
/// Relê num contexto novo, não no mesmo: o change tracker devolveria a instância que já está em memória, e o
/// teste passaria sem nunca provar que a gravação funcionou.
/// </para>
/// </remarks>
public sealed class MapeamentoDeTenantTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Tenant_SobreviveAoRoundTrip()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        TenantSlug slug = TenantSlug.Create($"acme-{Guid.NewGuid():N}"[..20]).Value;
        var original = Tenant.Register("Acme Corp", slug, new Plan(PlanTier.Standard, 50, 5));

        await using (AppDbContext escrita = postgres.CriarContexto())
        {
            escrita.Tenants.Add(original);
            await escrita.SaveChangesAsync(ct);
        }

        await using AppDbContext leitura = postgres.CriarContexto();
        Tenant? lido = await leitura.Tenants.SingleOrDefaultAsync(tenant => tenant.Id == original.Id, ct);

        lido.Should().NotBeNull();
        lido!.Name.Should().Be("Acme Corp");
        lido.Slug.Value.Should().Be(slug.Value);
        lido.Plan.Should().Be(new Plan(PlanTier.Standard, 50, 5));
        lido.Status.Should().Be(TenantStatus.Pending);
        lido.ExternalOrganizationId.Should().BeNull();
        lido.OccupiedSeats.Should().Be(0);
        lido.OverSubscribed.Should().BeFalse();
    }

    [Fact]
    public async Task OsEnums_SaoGravadosComoTexto()
    {
        // Texto e não int: um SELECT em producao dizendo 'Pending' responde a pergunta; dizendo '0', exige o
        // enum aberto ao lado. E a ordem dos membros deixa de ser dado de schema.
        CancellationToken ct = TestContext.Current.CancellationToken;
        TenantSlug slug = TenantSlug.Create($"enum-{Guid.NewGuid():N}"[..20]).Value;
        var tenant = Tenant.Register("Enum", slug, new Plan(PlanTier.Enterprise, 500, 50));

        await using AppDbContext contexto = postgres.CriarContexto();
        contexto.Tenants.Add(tenant);
        await contexto.SaveChangesAsync(ct);

        List<string> status = await contexto.Database
            .SqlQuery<string>($"SELECT status AS \"Value\" FROM tenants WHERE id = {tenant.Id.Value}")
            .ToListAsync(ct);

        status.Should().ContainSingle().Which.Should().Be("Pending");
    }
}
