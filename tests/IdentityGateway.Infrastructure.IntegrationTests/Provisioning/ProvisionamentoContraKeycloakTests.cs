using System.Net;
using System.Text.Json;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;
using IdentityGateway.Infrastructure.Persistence;
using IdentityGateway.Infrastructure.Persistence.Outbox;
using Microsoft.Extensions.DependencyInjection;
using static IdentityGateway.Infrastructure.IntegrationTests.Provisioning.ComposicaoDoProvisionamento;

namespace IdentityGateway.Infrastructure.IntegrationTests.Provisioning;

/// <summary>
/// O provisionamento inteiro — registro, Outbox, despacho, handler, Keycloak — contra PostgreSQL e Keycloak reais.
/// </summary>
public sealed class ProvisionamentoContraKeycloakTests(PostgresFixture postgres, KeycloakFixture keycloak)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task KeycloakForaNoPrimeiroCiclo_TenantViraActiveQuandoEleVolta()
    {
        // A demonstração do M1 (§16) em forma de teste: o tenant é aceito com o Keycloak fora, e fica Active sozinho
        // quando ele volta.
        CancellationToken ct = TestContext.Current.CancellationToken;
        bool[] keycloakFora = [true];
        Interceptacao interceptacao = new()
        {
            ResponderSemEnviar = (_, _) => keycloakFora[0] ? HttpStatusCode.ServiceUnavailable : null,
        };
        await using ServiceProvider provider = Criar(postgres, keycloak, services => services.Interceptar(interceptacao));

        TenantId tenant = await RegistrarAsync(provider, ct);
        OutboxMessage registrado = await MensagemAsync(provider, "tenant-registered", tenant, ct);

        await LiberarAsync(provider, registrado.Id, ct);
        await ProcessarCicloAsync(provider, ct);

        (await TenantAsync(provider, tenant, ct)).Status.Should().Be(TenantStatus.Pending);
        OutboxMessage aposFalha = await MensagemAsync(provider, "tenant-registered", tenant, ct);
        aposFalha.Attempts.Should().Be(1);
        aposFalha.ProcessedOn.Should().BeNull();
        aposFalha.Error.Should().NotBeNullOrEmpty();

        keycloakFora[0] = false;
        await LiberarAsync(provider, registrado.Id, ct);
        await ProcessarCicloAsync(provider, ct);

        Tenant ativo = await TenantAsync(provider, tenant, ct);
        ativo.Status.Should().Be(TenantStatus.Active);
        (await MensagemAsync(provider, "tenant-registered", tenant, ct)).ProcessedOn.Should().NotBeNull();

        JsonElement organizacao = await keycloak.LerOrganizacaoCruaAsync(ativo.ExternalOrganizationId!, ct);
        organizacao.GetProperty("alias").GetString().Should().Be(ativo.Slug.Value);
        organizacao.GetProperty("attributes").GetProperty("gateway_tenant_id")[0].GetString()
            .Should().Be(tenant.Value.ToString());
    }

    [Fact]
    public async Task TenantActivatedDoProvisionamento_SaiComoEntregue()
    {
        // O próprio provisionamento grava tenant-activated no Outbox. Sem destino no publisher, essa mensagem falharia
        // a cada ciclo por ~25h; "sem consumidor" precisa sair como entregue no primeiro.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = Criar(postgres, keycloak);

        TenantId tenant = await RegistrarAsync(provider, ct);
        await LiberarAsync(provider, (await MensagemAsync(provider, "tenant-registered", tenant, ct)).Id, ct);
        await ProcessarCicloAsync(provider, ct);

        OutboxMessage ativado = await MensagemAsync(provider, "tenant-activated", tenant, ct);
        await LiberarAsync(provider, ativado.Id, ct);
        await ProcessarCicloAsync(provider, ct);

        OutboxMessage entregue = await MensagemAsync(provider, "tenant-activated", tenant, ct);
        entregue.ProcessedOn.Should().NotBeNull();
        entregue.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task CommitPerdidoDepoisDeCriarAOrganizacao_ProximoCicloReencontraSemDuplicar()
    {
        // Dois fatos num cenário só:
        // (1) isolamento de escopo — o commit do handler falha com o tenant já Active no contexto dele; se o publisher
        //     usasse o escopo do processador, o SaveChanges que registra o resultado do lote gravaria esse Active;
        // (2) idempotência entre Keycloak e banco — a Organization criada no primeiro ciclo é reencontrada pelo
        //     atributo no segundo, sem uma segunda.
        CancellationToken ct = TestContext.Current.CancellationToken;
        int[] falhasDoCommit = [0];
        await using ServiceProvider provider = Criar(postgres, keycloak, services =>
            services.AddScoped<IUnitOfWork>(sp => new UnitOfWorkQueFalha(
                sp.GetRequiredService<AppDbContext>(), falhasDoCommit)));

        TenantId tenant = await RegistrarAsync(provider, ct);
        Guid registrado = (await MensagemAsync(provider, "tenant-registered", tenant, ct)).Id;
        falhasDoCommit[0] = 1;

        await LiberarAsync(provider, registrado, ct);
        await ProcessarCicloAsync(provider, ct);

        Tenant aposCommitPerdido = await TenantAsync(provider, tenant, ct);
        aposCommitPerdido.Status.Should().Be(TenantStatus.Pending, "o commit do handler falhou; nada dele pode ter sido gravado");
        (await keycloak.ContarPorAliasAsync(aposCommitPerdido.Slug.Value, ct)).Should().Be(1);

        await LiberarAsync(provider, registrado, ct);
        await ProcessarCicloAsync(provider, ct);

        Tenant ativo = await TenantAsync(provider, tenant, ct);
        ativo.Status.Should().Be(TenantStatus.Active);
        (await keycloak.ContarPorAliasAsync(ativo.Slug.Value, ct)).Should().Be(1);
    }
}
