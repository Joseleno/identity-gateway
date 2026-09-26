using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Domain.Tenants.Events;
using IdentityGateway.Infrastructure.Configuration;
using IdentityGateway.Infrastructure.Persistence;
using IdentityGateway.Infrastructure.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace IdentityGateway.Infrastructure.IntegrationTests.Persistence.Outbox;

/// <summary>
/// Prova, contra PostgreSQL real, que uma falha por <see cref="OperationCanceledException"/> sem relação com o
/// cancelamento do lote não aborta o registro das demais mensagens.
/// </summary>
/// <remarks>
/// <para>
/// <b>Por que <see cref="TaskCanceledException"/> e não outra exceção qualquer.</b> É exatamente o tipo que o
/// timeout de <c>HttpClient.Timeout</c> produz (ver <c>ITokenEndpoint</c>) — um <see cref="OperationCanceledException"/>
/// que nada tem a ver com o <see cref="CancellationToken"/> do <see cref="OutboxProcessor"/>. Um filtro que
/// descartasse todo <see cref="OperationCanceledException"/> do <c>catch</c> por mensagem, sem olhar o token,
/// deixaria esta falha escapar do <c>foreach</c> e abortar o <c>RegistrarResultadoAsync</c> do lote inteiro.
/// </para>
/// <para>
/// Fixture própria (não compartilhada com outros testes do Outbox): os tenants semeados aqui não podem colidir
/// com mensagens de outro teste no mesmo lote.
/// </para>
/// </remarks>
public sealed class OutboxProcessorTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    /// <summary>
    /// Publisher de teste: lança <see cref="TaskCanceledException"/> para um tenant escolhido e entrega os
    /// demais eventos normalmente.
    /// </summary>
    private sealed class PublisherComFalhaSeletiva(TenantId tenantQueFalha) : IOutboxPublisher
    {
        public Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
        {
            if (domainEvent is TenantRegistered registrado && registrado.TenantId == tenantQueFalha)
            {
                // Não é o cancelamento do lote — simula o timeout do HttpClient.Timeout, que chega como
                // TaskCanceledException mesmo com o CancellationToken do chamador intacto.
                throw new TaskCanceledException(
                    "Simula o timeout do HttpClient.Timeout, sem relacao com o cancelamento do lote.");
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task TaskCanceledSemCancelamentoDoLote_MarcaErroEEntregaAsDemais()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        var tenantComFalha = Tenant.Register(
            "Com falha", SlugUnico(), new Plan(PlanTier.Free, 5, 1), PostgresFixture.Agora);
        var tenantSemFalha = Tenant.Register(
            "Sem falha", SlugUnico(), new Plan(PlanTier.Free, 5, 1), PostgresFixture.Agora);

        await using (AppDbContext semente = postgres.CriarContexto())
        {
            semente.Tenants.AddRange(tenantComFalha, tenantSemFalha);
            await semente.SaveChangesAsync(ct);
        }

        IDateTimeProvider relogio = Substitute.For<IDateTimeProvider>();
        relogio.UtcNow.Returns(PostgresFixture.Agora);
        IOptions<OutboxOptions> opcoes = Options.Create(new OutboxOptions());

        await using AppDbContext contextoDoProcessador = postgres.CriarContexto();
        OutboxProcessor processador = new(
            contextoDoProcessador,
            new PublisherComFalhaSeletiva(tenantComFalha.Id),
            relogio,
            opcoes,
            NullLogger<OutboxProcessor>.Instance);

        int entregues = await processador.ProcessarLoteAsync(ct);

        entregues.Should().Be(1);

        await using AppDbContext leitura = postgres.CriarContexto();
        OutboxMessage mensagemComFalha = await MensagemDoTenantAsync(leitura, tenantComFalha.Id, ct);
        OutboxMessage mensagemSemFalha = await MensagemDoTenantAsync(leitura, tenantSemFalha.Id, ct);

        mensagemComFalha.ProcessedOn.Should().BeNull();
        mensagemComFalha.Error.Should().NotBeNullOrEmpty();

        mensagemSemFalha.ProcessedOn.Should().NotBeNull();
        mensagemSemFalha.Error.Should().BeNull();
    }

    /// <summary>A mensagem <c>tenant-registered</c> que carrega o id do tenant informado.</summary>
    /// <remarks>Filtro de conteúdo em memória: <c>content</c> é <c>jsonb</c> e não aceita <c>LIKE</c>.</remarks>
    private static async Task<OutboxMessage> MensagemDoTenantAsync(
        AppDbContext contexto, TenantId tenant, CancellationToken ct)
    {
        List<OutboxMessage> doTipo = await contexto.OutboxMessages.AsNoTracking()
            .Where(mensagem => mensagem.Type == "tenant-registered")
            .ToListAsync(ct);

        return doTipo.Single(mensagem => mensagem.Content.Contains(tenant.Value.ToString(), StringComparison.Ordinal));
    }

    private static TenantSlug SlugUnico() => TenantSlug.Create($"proc-{Guid.NewGuid():N}"[..18]).Value;
}
