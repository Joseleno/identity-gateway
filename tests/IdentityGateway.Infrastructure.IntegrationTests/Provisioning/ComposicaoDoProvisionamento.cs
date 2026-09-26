using IdentityGateway.Application;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Tenants.RegisterTenant;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;
using IdentityGateway.Infrastructure.Persistence;
using IdentityGateway.Infrastructure.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Infrastructure.IntegrationTests.Provisioning;

/// <summary>
/// A composição da aplicação (Application + Infrastructure) sobre PostgreSQL e Keycloak reais, e os passos do
/// provisionamento que os testes repetem.
/// </summary>
/// <remarks>
/// O despachante roda à mão (<see cref="ProcessarCicloAsync"/>), um ciclo por chamada: o <c>OutboxWorker</c> fica
/// desligado, porque um laço com temporizador tornaria o teste dependente de tempo.
/// </remarks>
internal static class ComposicaoDoProvisionamento
{
    internal static ServiceProvider Criar(
        PostgresFixture postgres, KeycloakFixture keycloak, Action<IServiceCollection>? ajustar = null)
    {
        IConfiguration configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = postgres.ConnectionString,
                ["Jwt:Issuer"] = "identitygateway",
                ["Jwt:Audience"] = "identitygateway-api",
                ["Jwt:SigningKey"] = new string('k', 32),
                ["Keycloak:Admin:BaseUrl"] = keycloak.BaseUrl,
                ["Keycloak:Admin:Realm"] = KeycloakFixture.Realm,
                ["Keycloak:Admin:ClientId"] = "identity-gateway",
                ["Keycloak:Admin:PrivateKeyPem"] = keycloak.Chaves.PemPrivado,
                ["Keycloak:Admin:AllowInsecureHttp"] = "true",
                ["HttpResilience:MaxRetryAttempts"] = "1",
                ["Outbox:Enabled"] = "false",
                ["Plans:free:tier"] = "Free",
                ["Plans:free:maxUsers"] = "5",
                ["Plans:free:maxClients"] = "1",
            })
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddApplication();
        services.AddInfrastructure(configuracao);
        ajustar?.Invoke(services);

        return services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>Registra um tenant pelo caminho de produção: command, pipeline e commit com a mensagem no Outbox.</summary>
    internal static async Task<TenantId> RegistrarAsync(ServiceProvider provider, CancellationToken ct)
    {
        string slug = KeycloakFixture.SlugUnico().Value;
        await using AsyncServiceScope escopo = provider.CreateAsyncScope();
        Mediator.ISender sender = escopo.ServiceProvider.GetRequiredService<Mediator.ISender>();

        Result<TenantId> resultado = await sender.Send(
            new RegisterTenantCommand("Acme Provisionamento", slug, "free", "admin@acme.com"), ct);

        resultado.IsSuccess.Should().BeTrue(resultado.IsFailure ? resultado.Error.Message : string.Empty);
        return resultado.Value;
    }

    /// <summary>Um ciclo do despachante, num escopo novo — como o <c>OutboxWorker</c> faz.</summary>
    internal static async Task ProcessarCicloAsync(ServiceProvider provider, CancellationToken ct)
    {
        await using AsyncServiceScope escopo = provider.CreateAsyncScope();
        await escopo.ServiceProvider.GetRequiredService<OutboxProcessor>().ProcessarLoteAsync(ct);
    }

    /// <summary>A mensagem do tipo informado que carrega o id do tenant.</summary>
    /// <remarks>Filtro de conteúdo em memória: <c>content</c> é <c>jsonb</c> e não aceita <c>LIKE</c>.</remarks>
    internal static async Task<OutboxMessage> MensagemAsync(
        ServiceProvider provider, string tipo, TenantId tenant, CancellationToken ct)
    {
        await using AsyncServiceScope escopo = provider.CreateAsyncScope();
        AppDbContext contexto = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

        List<OutboxMessage> doTipo = await contexto.OutboxMessages.AsNoTracking()
            .Where(mensagem => mensagem.Type == tipo)
            .ToListAsync(ct);

        return doTipo.Single(mensagem => mensagem.Content.Contains(tenant.Value.ToString(), StringComparison.Ordinal));
    }

    /// <summary>Torna a mensagem elegível agora, sem esperar o backoff.</summary>
    /// <remarks>
    /// Usa o <c>now()</c> do banco, que é o relógio que a reserva compara. Chamado também antes do primeiro ciclo: o
    /// <c>next_attempt_on</c> inicial vem do relógio da aplicação, e um contêiner com o relógio alguns segundos atrás
    /// deixaria a mensagem fora do lote — o teste ficaria intermitente.
    /// </remarks>
    internal static async Task LiberarAsync(ServiceProvider provider, Guid mensagem, CancellationToken ct)
    {
        await using AsyncServiceScope escopo = provider.CreateAsyncScope();
        AppDbContext contexto = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

        await contexto.Database.ExecuteSqlAsync(
            $"UPDATE outbox_messages SET next_attempt_on = now() - interval '1 second' WHERE id = {mensagem}", ct);
    }

    internal static async Task<Tenant> TenantAsync(ServiceProvider provider, TenantId tenant, CancellationToken ct)
    {
        await using AsyncServiceScope escopo = provider.CreateAsyncScope();
        AppDbContext contexto = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

        return await contexto.Tenants.AsNoTracking().SingleAsync(item => item.Id == tenant, ct);
    }
}

/// <summary>
/// <see cref="IUnitOfWork"/> que falha as próximas N vezes e depois grava normalmente.
/// </summary>
/// <remarks>
/// Simula o commit perdido depois de a Organization já existir no Keycloak. Lança <b>sem</b> chamar o
/// <c>SaveChanges</c>, então o que o handler alterou fica só rastreado no contexto do escopo dele.
/// </remarks>
internal sealed class UnitOfWorkQueFalha(AppDbContext contexto, int[] falhasRestantes) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Decrement(ref falhasRestantes[0]) >= 0)
        {
            throw new DbUpdateException("Commit perdido (injetado pelo teste).");
        }

        return contexto.SaveChangesAsync(cancellationToken);
    }
}
