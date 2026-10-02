using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Infrastructure;
using IdentityGateway.Infrastructure.Configuration;
using IdentityGateway.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace IdentityGateway.Infrastructure.IntegrationTests;

/// <summary>
/// Verifica que o contêiner resolve tudo o que a Application declara, e que a configuração inválida é barrada.
/// </summary>
/// <remarks>
/// <para>
/// Registro errado de DI não quebra o build: ele quebra ao <b>resolver</b> — uma dependência faltando, um tempo
/// de vida incompatível, um tipo não registrado. Estes testes resolvem de verdade, com escopo, que é o que prova
/// o registro.
/// </para>
/// <para>
/// <b>Sem banco.</b> Resolver o <c>AppDbContext</c> não abre conexão — isso só acontece na primeira consulta.
/// </para>
/// </remarks>
public sealed class DependencyInjectionTests
{
    private static readonly string ChaveDeTeste = ChavesDeTeste.Gerar().PemPrivado;

    private static IConfiguration ConfiguracaoValida(
        string? connectionString = "Host=localhost;Database=identitygateway;Username=postgres;Password=x") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = connectionString,
                ["Jwt:Issuer"] = "identitygateway",
                ["Jwt:Audience"] = "identitygateway-api",
                ["Jwt:SigningKey"] = new string('k', 32),
                ["Keycloak:Admin:BaseUrl"] = "http://keycloak.test:8080",
                ["Keycloak:Admin:Realm"] = "identity-gateway",
                ["Keycloak:Admin:ClientId"] = "identity-gateway",
                ["Keycloak:Admin:PrivateKeyPem"] = ChaveDeTeste,
                ["Keycloak:Admin:AllowInsecureHttp"] = "true",
            })
            .Build();

    private static ServiceProvider Construir(IConfiguration configuration)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.ComAmbiente();
        services.AddInfrastructure(configuration);

        return services.BuildServiceProvider(validateScopes: true);
    }

    [Theory]
    [InlineData(typeof(IUnitOfWork))]
    [InlineData(typeof(ICacheService))]
    [InlineData(typeof(IDateTimeProvider))]
    [InlineData(typeof(ICorrelationIdProvider))]
    [InlineData(typeof(ICurrentUser))]
    [InlineData(typeof(IOutboxPublisher))]
    [InlineData(typeof(ITenantRepository))]
    [InlineData(typeof(IMemberRepository))]
    [InlineData(typeof(IPlanCatalog))]
    [InlineData(typeof(IIdentityProvider))]
    [InlineData(typeof(IProvisioningPolicy))]
    [InlineData(typeof(IInvitationPolicy))]
    [InlineData(typeof(ITenantQueries))]
    public void TodasAsAbstracoesDaApplication_SaoResolviveis(Type servico)
    {
        // Se a Application declara uma interface que ninguém registrou, o erro aparece aqui — não na primeira
        // requisição que precisar dela.
        using ServiceProvider provider = Construir(ConfiguracaoValida());
        using IServiceScope scope = provider.CreateScope();

        object? resolvido = scope.ServiceProvider.GetService(servico);

        resolvido.Should().NotBeNull($"{servico.Name} é declarado pela Application e precisa de implementação");
    }

    [Fact]
    public void IUnitOfWork_EOMesmoAppDbContextDoEscopo()
    {
        // É a decisão de não ter classe UnitOfWork separada: a mesma instância serve as duas pontas. Se fossem
        // objetos diferentes, o repositório gravaria num contexto e o commit aconteceria em outro — e nada
        // seria persistido, sem erro nenhum.
        using ServiceProvider provider = Construir(ConfiguracaoValida());
        using IServiceScope scope = provider.CreateScope();

        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        AppDbContext contexto = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        unitOfWork.Should().BeSameAs(contexto);
    }

    [Fact]
    public void CorrelationId_EOMesmoDentroDoEscopoEDiferenteEntreEscopos()
    {
        using ServiceProvider provider = Construir(ConfiguracaoValida());

        string primeiro;
        string segundo;

        using (IServiceScope escopo = provider.CreateScope())
        {
            // Duas resoluções no mesmo escopo têm de dar o mesmo id, senão a correlação não acontece.
            primeiro = escopo.ServiceProvider.GetRequiredService<ICorrelationIdProvider>().CorrelationId;
            string mesmoEscopo = escopo.ServiceProvider.GetRequiredService<ICorrelationIdProvider>().CorrelationId;

            mesmoEscopo.Should().Be(primeiro, "Scoped, não Transient");
        }

        using (IServiceScope outroEscopo = provider.CreateScope())
        {
            segundo = outroEscopo.ServiceProvider.GetRequiredService<ICorrelationIdProvider>().CorrelationId;
        }

        segundo.Should().NotBe(primeiro, "operações distintas não compartilham correlation id");
    }

    [Fact]
    public void ConfiguracaoSemConnectionString_FalhaAoValidar()
    {
        // ValidateOnStart transforma configuração ausente em falha de startup. Sem isso, a aplicação subiria e
        // quebraria na primeira requisição, em produção.
        using ServiceProvider provider = Construir(ConfiguracaoValida(connectionString: null));

        Action validar = () => _ = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;

        validar.Should().Throw<OptionsValidationException>()
            .WithMessage("*connection string*");
    }

    [Fact]
    public void ChaveJwtCurta_FalhaAoValidar()
    {
        IConfiguration configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = "Host=localhost;Database=x;Username=u;Password=p",
                ["Jwt:Issuer"] = "identitygateway",
                ["Jwt:Audience"] = "identitygateway-api",
                ["Jwt:SigningKey"] = "curta",
            })
            .Build();

        using ServiceProvider provider = Construir(configuracao);

        // Chave menor que 256 bits é preenchida ou rejeitada conforme a biblioteca — nos dois casos, a
        // segurança que se acredita ter não existe. Melhor falhar ao subir.
        Action validar = () => _ = provider.GetRequiredService<IOptions<JwtOptions>>().Value;

        validar.Should().Throw<OptionsValidationException>()
            .WithMessage("*32 caracteres*");
    }

    [Fact]
    public void SemRedisConfigurado_OCacheAindaFunciona()
    {
        // O segundo nível é opcional: a aplicação sobe sem Redis e usa cache local. O que muda é ele não ser
        // compartilhado entre instâncias — decisão registrada na configuração, não surpresa em produção.
        using ServiceProvider provider = Construir(ConfiguracaoValida());
        using IServiceScope scope = provider.CreateScope();

        ICacheService cache = scope.ServiceProvider.GetRequiredService<ICacheService>();

        cache.Should().NotBeNull();
        provider.GetRequiredService<IOptions<RedisOptions>>().Value.Enabled.Should().BeFalse();
    }

    [Fact]
    public void LoteDoOutboxForaDaFaixa_FalhaAoValidar()
    {
        IConfiguration configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = "Host=localhost;Database=x;Username=u;Password=p",
                ["Outbox:BatchSize"] = "0",
            })
            .Build();

        using ServiceProvider provider = Construir(configuracao);

        // Lote zero não é configuração exótica: é o valor que faz o despachante rodar em laço sem nunca pegar
        // trabalho — sem erro, sem log, e com a fila crescendo. Falhar ao subir é o que torna isso visível.
        Action validar = () => _ = provider.GetRequiredService<IOptions<OutboxOptions>>().Value;

        validar.Should().Throw<OptionsValidationException>()
            .WithMessage("*lote*");
    }

    [Fact]
    public void OutboxSemConfiguracao_UsaOsPadroes()
    {
        // A seção inteira é opcional: quem não a declara recebe um despachante ligado e com valores sensatos.
        // O contrário — exigir a seção — faria o kit não subir de primeira, que é justamente o que ele promete.
        using ServiceProvider provider = Construir(ConfiguracaoValida());

        OutboxOptions outbox = provider.GetRequiredService<IOptions<OutboxOptions>>().Value;

        outbox.Enabled.Should().BeTrue("um outbox que ninguém despacha acumula eventos em silêncio");
        outbox.BatchSize.Should().Be(20);
        outbox.MaxAttempts.Should().Be(1500);
        outbox.MaxRetryDelaySeconds.Should().Be(60);
    }

    [Fact]
    public void OutboxHabilitado_RegistraODespachante()
    {
        using ServiceProvider provider = Construir(ConfiguracaoValida());

        provider.GetServices<IHostedService>().Should().ContainSingle(
            servico => servico.GetType().Name == "OutboxWorker",
            "com o outbox ligado, alguém precisa despachar as mensagens");
    }

    [Fact]
    public void OutboxDesligado_NaoRegistraODespachante()
    {
        IConfiguration configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = "Host=localhost;Database=x;Username=u;Password=p",
                ["Outbox:Enabled"] = "false",
            })
            .Build();

        using ServiceProvider provider = Construir(configuracao);

        // É o que permite subir uma instância só-API, e é o que impede o despachante de competir com os testes
        // funcionais pela mesma tabela. O processor continua registrado: desligar o laço não tira a capacidade
        // de despachar à mão.
        //
        // Não é mais "nenhum hosted service": o AddHealthChecks() do Keycloak registra o
        // HealthCheckPublisherHostedService, que nada tem a ver com o despachante do outbox. O que este teste prova
        // é que o OutboxWorker especificamente não está entre eles.
        provider.GetServices<IHostedService>().Should().NotContain(
            servico => servico.GetType().Name == "OutboxWorker",
            "com o outbox desligado, ninguém despacha sozinho");

        using IServiceScope escopo = provider.CreateScope();
        escopo.ServiceProvider.GetService<IOutboxPublisher>().Should().NotBeNull();
    }

    [Fact]
    public void IIdentityProvider_ETransient()
    {
        // Transient como o cliente tipado que envolve: ele não depende de DbContext, e a v2.3 o justificava como
        // Scoped por um motivo que não se aplica.
        using ServiceProvider provider = Construir(ConfiguracaoValida());
        using IServiceScope scope = provider.CreateScope();

        IIdentityProvider primeiro = scope.ServiceProvider.GetRequiredService<IIdentityProvider>();
        IIdentityProvider segundo = scope.ServiceProvider.GetRequiredService<IIdentityProvider>();

        primeiro.Should().NotBeSameAs(segundo);
    }

    private static IConfiguration ConfiguracaoValidaCom(params (string Chave, string? Valor)[] extras)
    {
        var valores = ConfiguracaoValida()
            .AsEnumerable()
            .Where(par => par.Value is not null)
            .ToDictionary(par => par.Key, par => par.Value);

        foreach ((string chave, string? valor) in extras)
        {
            valores[chave] = valor;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
    }

    [Fact]
    public void ProvisioningSemConfiguracao_UsaAJanelaDe24Horas()
    {
        using ServiceProvider provider = Construir(ConfiguracaoValida());

        provider.GetRequiredService<IProvisioningPolicy>().MaxPendingDuration.Should().Be(TimeSpan.FromHours(24));
    }

    [Fact]
    public void JanelaForaDaFaixa_FalhaAoValidar()
    {
        using ServiceProvider provider = Construir(ConfiguracaoValidaCom(("Provisioning:MaxPendingHours", "0")));

        Action validar = () => _ = provider.GetRequiredService<IOptions<ProvisioningOptions>>().Value;

        validar.Should().Throw<OptionsValidationException>().WithMessage("*janela*");
    }

    [Fact]
    public void OutboxComOsNumerosAntigos_FalhaAoValidarNomeandoTentativasEJanela()
    {
        // É a configuração que qualquer ambiente com o appsettings anterior à fatia B tem: cinco tentativas somam
        // 150s de retry, e o tenant ficaria em Pending para sempre depois que o Outbox desistisse — sem ninguém para
        // marcá-lo ProvisioningFailed. Falhar na subida, dizendo por quê, é o que torna isso visível.
        using ServiceProvider provider = Construir(ConfiguracaoValidaCom(
            ("Outbox:MaxAttempts", "5"),
            ("Outbox:MaxRetryDelaySeconds", "300")));

        Action validar = () => _ = provider.GetRequiredService<IOptions<OutboxOptions>>().Value;

        validar.Should().Throw<OptionsValidationException>().WithMessage("*5 tentativas*24h*");
    }

    [Fact]
    public void ConvitesSemConfiguracao_UsamSeteDias()
    {
        // Alinhado à §9.9: o padrão do realm (12 h) ficaria desalinhado do ciclo do convite.
        using ServiceProvider provider = Construir(ConfiguracaoValida());

        provider.GetRequiredService<IInvitationPolicy>().LinkLifetime.Should().Be(TimeSpan.FromDays(7));
    }

    [Theory]
    [InlineData("00:00:00")]        // zero: o link nasceria expirado
    [InlineData("-1.00:00:00")]     // negativo
    [InlineData("00:00:01.500")]    // fração: o Keycloak recebe segundos inteiros, e truncar mudaria o prazo em silêncio
    [InlineData("30.00:00:01")]     // acima do teto
    public void PrazoDoLinkInvalido_FalhaAoValidar(string prazo)
    {
        using ServiceProvider provider = Construir(ConfiguracaoValidaCom(("Invitations:LinkLifetime", prazo)));

        Action validar = () => _ = provider.GetRequiredService<IOptions<InvitationOptions>>().Value;

        validar.Should().Throw<OptionsValidationException>().WithMessage("*LinkLifetime*");
    }

    [Theory]
    [InlineData("00:00:01", 1)]
    [InlineData("2.12:00:00", 216_000)]
    [InlineData("30.00:00:00", 2_592_000)]
    public void PrazoDoLinkValido_ChegaInteiroAPolitica(string prazo, int segundos)
    {
        using ServiceProvider provider = Construir(ConfiguracaoValidaCom(("Invitations:LinkLifetime", prazo)));

        provider.GetRequiredService<IInvitationPolicy>().LinkLifetime.Should().Be(TimeSpan.FromSeconds(segundos));
    }

    [Fact]
    public void PlanoSemVagas_FalhaAoValidar()
    {
        // O admin inicial ocupa uma vaga na ativação (D2): um plano com maxUsers 0 não comporta nem ele, e cada tenant
        // nesse plano cairia em ProvisioningFailed.
        using ServiceProvider provider = Construir(ConfiguracaoValidaCom(
            ("Plans:gratis:tier", "Free"),
            ("Plans:gratis:maxUsers", "0"),
            ("Plans:gratis:maxClients", "1")));

        Action validar = () => _ = provider.GetRequiredService<IOptions<PlanOptions>>().Value;

        validar.Should().Throw<OptionsValidationException>().WithMessage("*maxUsers*");
    }
}
