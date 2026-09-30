using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;
using IdentityGateway.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static IdentityGateway.Infrastructure.IntegrationTests.Provisioning.ComposicaoDoProvisionamento;

namespace IdentityGateway.Infrastructure.IntegrationTests.Provisioning;

/// <summary>
/// A ativação do tenant, o <c>Member</c> e o <c>tenant-activated</c> gravam juntos ou nenhum.
/// </summary>
/// <remarks>
/// <para>
/// O provedor de identidade é um dublê que devolve uma Organization e um <c>sub</c> conhecidos: o Keycloak não entra, e
/// o que se observa é só o commit. Ele é um <c>SaveChanges</c> só, que o EF manda num lote ordenado por tabela:
/// <c>INSERT INTO members</c>, <c>INSERT INTO outbox_messages</c> e, por último,
/// <c>UPDATE tenants … WHERE xmin = @original</c>.
/// </para>
/// <para>
/// Por isso a atomicidade se prova fazendo falhar o <b>último</b> comando: os dois <c>INSERT</c> executam e o rollback
/// os desfaz. Fazer falhar o primeiro prova menos — o PostgreSQL nem executa o resto do lote.
/// </para>
/// </remarks>
public sealed class AtomicidadeDoProvisionamentoTests(PostgresFixture postgres, KeycloakFixture keycloak)
    : IClassFixture<PostgresFixture>
{
    private const string Organizacao = "org-atomica";

    /// <summary>Devolve Organization e <c>sub</c> fixos, sem tocar o Keycloak.</summary>
    private class ProvedorFixo(ExternalUserId sub) : IIdentityProvider
    {
        public Task<string> EnsureOrganizationAsync(
            TenantId tenantId, TenantSlug slug, string name, CancellationToken cancellationToken) =>
            Task.FromResult(Organizacao);

        public virtual Task<ExternalUserId> EnsureInvitedUserAsync(
            string organizationId, TenantId tenantId, InviteData invite, CancellationToken cancellationToken) =>
            Task.FromResult(sub);
    }

    /// <summary>
    /// Durante o convite, outra conexão atualiza a linha do tenant — como uma entrega concorrente que commitasse
    /// primeiro. O <c>xmin</c> muda, e o <c>UPDATE</c> do commit, que é o último comando do lote, casa zero linhas.
    /// </summary>
    private sealed class ProvedorComTenantAlterado(PostgresFixture postgres, ExternalUserId sub) : ProvedorFixo(sub)
    {
        public override async Task<ExternalUserId> EnsureInvitedUserAsync(
            string organizationId, TenantId tenantId, InviteData invite, CancellationToken cancellationToken)
        {
            await using AppDbContext outraConexao = postgres.CriarContexto();
            await outraConexao.Database.ExecuteSqlAsync(
                $"UPDATE tenants SET name = name WHERE id = {tenantId.Value}", cancellationToken);

            return await base.EnsureInvitedUserAsync(organizationId, tenantId, invite, cancellationToken);
        }
    }

    [Fact]
    public async Task UltimoComandoDoCommitFalha_NadaDaAtivacaoEGravado()
    {
        // O UPDATE do tenant falha depois de o INSERT do Member e o do tenant-activated terem executado: zero membros
        // e nenhuma ativação no Outbox só acontecem se o rollback os desfez. Sem atomicidade, sobrariam os dois.
        CancellationToken ct = TestContext.Current.CancellationToken;
        var sub = ExternalUserId.From($"sub-{Guid.NewGuid():N}");
        await using ServiceProvider provider = Criar(postgres, keycloak, services =>
            services.AddTransient<IIdentityProvider>(_ => new ProvedorComTenantAlterado(postgres, sub)));

        TenantId tenant = await RegistrarAsync(provider, KeycloakFixture.EmailUnico(), ct);

        Func<Task> provisionar = () => ProvisionarAsync(provider, tenant, ct);

        await provisionar.Should().ThrowAsync<DbUpdateConcurrencyException>();

        Tenant depois = await TenantAsync(provider, tenant, ct);
        depois.Status.Should().Be(TenantStatus.Pending);
        depois.OccupiedSeats.Should().Be(0);
        (await EmailGravadoAsync(provider, tenant, ct)).Should().NotBe("<nulo>");
        (await MembrosAsync(provider, tenant, ct)).Should().BeEmpty("o INSERT do Member executou e foi desfeito");
        (await AtivacoesDoTenantAsync(tenant, ct)).Should().BeEmpty("o INSERT do Outbox executou e foi desfeito");
    }

    [Fact]
    public async Task MemberJaExistenteNoIndiceUnico_TenantNaoAtivaENadaEGravado()
    {
        // Uma linha em members com o mesmo (tenant_id, sub), pré-inserida por SQL, faz o INSERT do Member — o primeiro
        // comando do lote — violar o índice único; o PostgreSQL não executa o resto. O que isto prova é que a colisão
        // no índice não ativa o tenant, e que o Member não pode ser gravado num commit separado depois do tenant: um
        // handler que gravasse o tenant antes de adicionar o membro deixaria o tenant Active, sem o e-mail e com o
        // tenant-activated no Outbox.
        CancellationToken ct = TestContext.Current.CancellationToken;
        var sub = ExternalUserId.From($"sub-{Guid.NewGuid():N}");
        await using ServiceProvider provider = Criar(postgres, keycloak, services =>
            services.AddTransient<IIdentityProvider>(_ => new ProvedorFixo(sub)));

        TenantId tenant = await RegistrarAsync(provider, KeycloakFixture.EmailUnico(), ct);

        await using (AppDbContext contexto = postgres.CriarContexto())
        {
            await contexto.Database.ExecuteSqlAsync($"""
                INSERT INTO members (id, tenant_id, external_user_id, status, invited_at, created_at)
                VALUES ({Guid.CreateVersion7()}, {tenant.Value}, {sub.Value}, 'Invited', now(), now())
                """, ct);
        }

        Func<Task> provisionar = () => ProvisionarAsync(provider, tenant, ct);

        await provisionar.Should().ThrowAsync<DbUpdateException>();

        Tenant depois = await TenantAsync(provider, tenant, ct);
        depois.Status.Should().Be(TenantStatus.Pending);
        depois.OccupiedSeats.Should().Be(0);
        (await EmailGravadoAsync(provider, tenant, ct)).Should().NotBe("<nulo>");
        (await AtivacoesDoTenantAsync(tenant, ct)).Should().BeEmpty();
    }

    /// <summary>Os <c>tenant-activated</c> do tenant no Outbox.</summary>
    /// <remarks>Filtro de conteúdo em memória: <c>content</c> é <c>jsonb</c> e não aceita <c>LIKE</c>.</remarks>
    private async Task<List<string>> AtivacoesDoTenantAsync(TenantId tenant, CancellationToken ct)
    {
        await using AppDbContext conferencia = postgres.CriarContexto();
        List<string> ativacoes = await conferencia.OutboxMessages
            .Where(mensagem => mensagem.Type == "tenant-activated")
            .Select(mensagem => mensagem.Content)
            .ToListAsync(ct);

        return [.. ativacoes.Where(conteudo => conteudo.Contains(tenant.Value.ToString(), StringComparison.Ordinal))];
    }
}
