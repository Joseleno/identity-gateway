using IdentityGateway.Application;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Tenants.ProvisionTenant;
using IdentityGateway.Application.Tenants.RegisterTenant;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Members;
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
    /// <summary>A composição de produção sobre os containers do teste.</summary>
    /// <remarks>
    /// <paramref name="ajustar"/> registra por cima da composição de produção; <paramref name="extras"/> completa ou
    /// sobrescreve as chaves de configuração padrão.
    /// </remarks>
    internal static ServiceProvider Criar(
        PostgresFixture postgres,
        KeycloakFixture keycloak,
        Action<IServiceCollection>? ajustar = null,
        IReadOnlyDictionary<string, string?>? extras = null)
    {
        Dictionary<string, string?> valores = new()
        {
            ["Database:ConnectionString"] = postgres.ConnectionString,
            ["Keycloak:Admin:BaseUrl"] = keycloak.BaseUrl,
            ["Keycloak:Admin:PublicBaseUrl"] = KeycloakFixture.HostnamePublico,
            ["Keycloak:Admin:Realm"] = KeycloakFixture.Realm,
            ["Keycloak:Admin:ClientId"] = "identity-gateway",
            ["Keycloak:Admin:PrivateKeyPem"] = keycloak.Chaves.PemPrivado,
            ["Keycloak:Admin:AllowInsecureHttp"] = "true",
            ["HttpResilience:MaxRetryAttempts"] = "1",
            ["Outbox:Enabled"] = "false",
            ["Plans:free:tier"] = "Free",
            ["Plans:free:maxUsers"] = "5",
            ["Plans:free:maxClients"] = "1",
        };

        foreach ((string chave, string? valor) in extras ?? new Dictionary<string, string?>())
        {
            valores[chave] = valor;
        }

        IConfiguration configuracao = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.ComAmbiente();
        services.AddApplication();
        services.AddInfrastructure(configuracao);
        ajustar?.Invoke(services);

        return services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>Registra um tenant pelo caminho de produção, com e-mail de admin único.</summary>
    /// <remarks>
    /// Único por teste: com o convite no provisionamento, dois tenants com o mesmo e-mail caem no D5 (o segundo vira
    /// ProvisioningFailed), e os testes passariam a depender da ordem.
    /// </remarks>
    internal static Task<TenantId> RegistrarAsync(ServiceProvider provider, CancellationToken ct) =>
        RegistrarAsync(provider, KeycloakFixture.EmailUnico(), ct);

    /// <summary>Registra um tenant pelo caminho de produção: command, pipeline e commit com a mensagem no Outbox.</summary>
    internal static async Task<TenantId> RegistrarAsync(ServiceProvider provider, string email, CancellationToken ct)
    {
        string slug = KeycloakFixture.SlugUnico().Value;
        await using AsyncServiceScope escopo = provider.CreateAsyncScope();
        Mediator.ISender sender = escopo.ServiceProvider.GetRequiredService<Mediator.ISender>();

        Result<TenantId> resultado = await sender.Send(
            new RegisterTenantCommand("Acme Provisionamento", slug, "free", email), ct);

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

    /// <summary>Os membros gravados do tenant, lidos num escopo novo.</summary>
    internal static async Task<List<Member>> MembrosAsync(ServiceProvider provider, TenantId tenant, CancellationToken ct)
    {
        await using AsyncServiceScope escopo = provider.CreateAsyncScope();
        AppDbContext contexto = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

        return await contexto.Members.AsNoTracking().Where(membro => membro.TenantId == tenant).ToListAsync(ct);
    }

    /// <summary>O valor cru da coluna initial_admin_email, ou <c>&lt;nulo&gt;</c>.</summary>
    internal static async Task<string> EmailGravadoAsync(ServiceProvider provider, TenantId tenant, CancellationToken ct)
    {
        await using AsyncServiceScope escopo = provider.CreateAsyncScope();
        AppDbContext contexto = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

        List<string> valor = await contexto.Database
            .SqlQuery<string>(
                $"SELECT coalesce(initial_admin_email, '<nulo>') AS \"Value\" FROM tenants WHERE id = {tenant.Value}")
            .ToListAsync(ct);

        return valor.Single();
    }

    /// <summary>Entrega o command do provisionamento num escopo novo, pelo pipeline — como o despacho do Outbox faz.</summary>
    internal static async Task ProvisionarAsync(ServiceProvider provider, TenantId tenant, CancellationToken ct)
    {
        await using AsyncServiceScope escopo = provider.CreateAsyncScope();
        Mediator.ISender sender = escopo.ServiceProvider.GetRequiredService<Mediator.ISender>();

        await sender.Send(new ProvisionTenantCommand(tenant), ct);
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

/// <summary>Faz duas chamadas se encontrarem: nenhuma segue antes de a outra chegar — ou desistir.</summary>
internal sealed class EncontroDeDois
{
    private readonly TaskCompletionSource _ambas = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _chegaram;

    public Task ChegarAsync(CancellationToken ct)
    {
        if (Interlocked.Increment(ref _chegaram) == 2)
        {
            _ambas.TrySetResult();
        }

        return _ambas.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
    }

    /// <summary>Quem falhou antes do encontro libera o outro: esperar quem não vem só esgotaria o prazo.</summary>
    public void Desistir() => _ambas.TrySetResult();
}

/// <summary>
/// O provedor real, com um ponto de encontro depois do convite: as duas entregas terminam a parte do Keycloak antes de
/// qualquer uma commitar — e as duas leram o tenant em Pending antes disso.
/// </summary>
/// <remarks>
/// Uma entrega que falha no Keycloak (a corrida pode dar erro transitório a uma delas) desiste do encontro, e a outra
/// segue sozinha.
/// </remarks>
internal sealed class ProvedorComEncontro(IIdentityProvider real, EncontroDeDois encontro) : IIdentityProvider
{
    public async Task<string> EnsureOrganizationAsync(
        TenantId tenantId, TenantSlug slug, string name, CancellationToken cancellationToken)
    {
        try
        {
            return await real.EnsureOrganizationAsync(tenantId, slug, name, cancellationToken);
        }
        catch
        {
            encontro.Desistir();
            throw;
        }
    }

    public async Task<ExternalUserId> EnsureInvitedUserAsync(
        string organizationId, TenantId tenantId, InviteData invite, CancellationToken cancellationToken)
    {
        ExternalUserId sub;

        try
        {
            sub = await real.EnsureInvitedUserAsync(organizationId, tenantId, invite, cancellationToken);
        }
        catch
        {
            encontro.Desistir();
            throw;
        }

        await encontro.ChegarAsync(cancellationToken);
        return sub;
    }
}
