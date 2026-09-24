using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Infrastructure.Configuration;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using IdentityGateway.Infrastructure.Persistence;
using IdentityGateway.Infrastructure.Persistence.Interceptors;
using IdentityGateway.Infrastructure.Persistence.Outbox;
using IdentityGateway.Infrastructure.Persistence.Repositories;
using IdentityGateway.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IdentityGateway.Infrastructure;

/// <summary>
/// Registra a infraestrutura no contêiner.
/// </summary>
/// <remarks>
/// Único ponto de entrada da camada: a Api chama <see cref="AddInfrastructure"/> e não conhece
/// <c>AppDbContext</c>, interceptor nem repositório concreto. É o que permite trocar a implementação sem tocar em
/// quem a consome.
/// </remarks>
public static class DependencyInjection
{
    /// <summary>
    /// Acrescenta persistência, cache e os serviços de infraestrutura.
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptionsValidadas(configuration)
            .AddPersistencia()
            .AddCache(configuration)
            .AddServicos()
            .AddOutbox(configuration)
            .AddKeycloakIdentity(configuration);

        return services;
    }

    /// <summary>
    /// Liga as seções de configuração, validadas no startup.
    /// </summary>
    /// <remarks>
    /// <c>ValidateOnStart</c> é o que importa aqui: sem ele, a validação só roda quando alguém pede o
    /// <c>IOptions</c> pela primeira vez — ou seja, já em produção, na primeira requisição. Com ele, configuração
    /// ausente derruba a aplicação ao subir, com mensagem dizendo qual chave falta.
    /// </remarks>
    private static IServiceCollection AddOptionsValidadas(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<RedisOptions>()
            .Bind(configuration.GetSection(RedisOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection(OutboxOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Catálogo inválido derruba a aplicação na subida, não na primeira requisição.
        //
        // A validação é escrita à mão, e não por `ValidateDataAnnotations`: `PlanOptions` herda de
        // `Dictionary`, e as anotações só valem para as propriedades do objeto raiz — nunca para os VALORES
        // do dicionário, que é justamente onde os limites moram. Verificado: com `[Range]` em
        // `PlanDefinition.MaxUsers` e `ValidateDataAnnotations()`, um `maxUsers: -5` no appsettings passa
        // sem erro nenhum.
        //
        // Limite negativo aqui viraria `ArgumentOutOfRangeException` lá no construtor do `Plan`, no meio do
        // primeiro registro de tenant — erro de catálogo mal configurado disfarçado de falha de requisição.
        services.AddOptions<PlanOptions>()
            .Bind(configuration.GetSection(PlanOptions.SectionName))
            .Validate(
                planos => planos.Values.All(plano => plano.MaxUsers >= 0 && plano.MaxClients >= 0),
                "Plans: nenhum plano pode ter maxUsers ou maxClients negativo.")
            .ValidateOnStart();

        // Política do cliente da Admin API do Keycloak — o consumidor que o comentário da classe esperava.
        services.AddOptions<HttpResilienceOptions>()
            .Bind(configuration.GetSection(HttpResilienceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }

    private static IServiceCollection AddPersistencia(this IServiceCollection services)
    {
        // Os interceptors entram no contêiner porque dependem de IDateTimeProvider e ICurrentUser — construí-los
        // à mão aqui significaria resolver essas dependências à mão também.
        services.AddScoped<AuditableInterceptor>();
        services.AddScoped<SoftDeleteInterceptor>();
        services.AddScoped<DomainEventInterceptor>();

        services.AddDbContext<AppDbContext>((provider, options) =>
        {
            DatabaseOptions database = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;

            options.UseNpgsql(database.ConnectionString, npgsql =>
            {
                npgsql.CommandTimeout(database.CommandTimeoutSeconds);

                // Retry de falha transiente: queda de rede e failover são esperados em nuvem, e repetir é o
                // comportamento correto. Não cobre erro de lógica — só o que é transitório por natureza.
                npgsql.EnableRetryOnFailure(database.MaxRetryCount);
            });

            // Parâmetros de consulta no log carregam PII; a opção existe para desenvolvimento e é falsa por
            // padrão (ver DatabaseOptions).
            options.EnableSensitiveDataLogging(database.EnableSensitiveDataLogging);

            // A ordem dos três é fixada em um lugar só, com o motivo escrito lá — ela importa: o soft delete
            // precisa converter Deleted em Modified antes de a auditoria rodar.
            options.AddIdentityGatewayInterceptors(
                provider.GetRequiredService<SoftDeleteInterceptor>(),
                provider.GetRequiredService<AuditableInterceptor>(),
                provider.GetRequiredService<DomainEventInterceptor>());
        });

        // IUnitOfWork é o próprio contexto — não existe classe UnitOfWork separada, porque ela só delegaria
        // SaveChangesAsync e nada mais. Se um dia o limite transacional precisar de comportamento próprio,
        // a mudança é nesta linha, porque a Application já fala com a interface.
        services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<AppDbContext>());

        services.AddScoped<ITenantRepository, TenantRepository>();

        return services;
    }

    private static IServiceCollection AddCache(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        RedisOptions redis = configuration
            .GetSection(RedisOptions.SectionName)
            .Get<RedisOptions>() ?? new RedisOptions();

        services.AddHybridCache();

        if (redis.Enabled)
        {
            // O segundo nível é opcional: sem Redis configurado, o HybridCache usa só memória local. A
            // aplicação sobe igual — o que muda é o cache não ser compartilhado entre instâncias.
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redis.ConnectionString;
                options.InstanceName = redis.InstanceName;
            });
        }

        services.AddScoped<ICacheService, HybridCacheService>();

        return services;
    }

    private static IServiceCollection AddServicos(this IServiceCollection services)
    {
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();

        // Scoped, não Transient: o correlation id precisa ser o mesmo durante toda a operação, senão a
        // correlação — a única razão de ele existir — não acontece.
        services.AddScoped<ICorrelationIdProvider, ScopedCorrelationIdProvider>();

        // Padrão sem usuário. A Api registra por cima a implementação que lê o HttpContext (Fase 4); job e
        // seed continuam com esta, gravando autoria nula.
        services.AddScoped<ICurrentUser, NoCurrentUser>();

        // Singleton: é configuração imutável, e uma instância por requisição só produziria lixo.
        services.AddSingleton<IPlanCatalog, PlanCatalog>();

        return services;
    }

    /// <summary>
    /// Registra o despachante do outbox e o destino para onde ele publica.
    /// </summary>
    /// <remarks>
    /// <para>
    /// O publisher e o processor são Scoped como o <c>AppDbContext</c> de que dependem; o worker é singleton, por
    /// ser um <c>BackgroundService</c>, e por isso cria um escopo próprio a cada ciclo.
    /// </para>
    /// <para>
    /// <b>Só o worker é condicional.</b> O processor continua registrado mesmo com o despachante desligado —
    /// quem o desligou pode querer chamá-lo à mão, de um comando ou de um teste. O que a flag decide é se
    /// alguém o chama sozinho, de tempos em tempos.
    /// </para>
    /// </remarks>
    private static IServiceCollection AddOutbox(this IServiceCollection services, IConfiguration configuration)
    {
        // Troque esta linha pela sua implementação para ligar um broker de verdade; nada mais muda.
        services.AddScoped<IOutboxPublisher, LoggingOutboxPublisher>();
        services.AddScoped<OutboxProcessor>();

        // Singleton porque a memória do que já foi notificado precisa atravessar os escopos — um por ciclo do
        // despachante. Scoped faria cada ciclo esquecer tudo, e a proteção contra entrega repetida sumiria
        // justamente no caso que ela existe para cobrir. Num sistema real esse estado é uma tabela, e aí o
        // tempo de vida do serviço deixa de importar.


        // Lido direto da configuração, e não por IOptions: a decisão é sobre o que REGISTRAR, e acontece antes
        // de existir um provider de onde resolver options.
        bool habilitado = configuration
            .GetSection(OutboxOptions.SectionName)
            .GetValue("Enabled", defaultValue: true);

        if (habilitado)
        {
            services.AddHostedService<OutboxWorker>();
        }

        return services;
    }

}
