using System.Diagnostics;
using System.Net;
using System.Text.Json;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;
using IdentityGateway.Infrastructure.Persistence;
using IdentityGateway.Infrastructure.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry.Trace;
using static IdentityGateway.Infrastructure.IntegrationTests.Provisioning.ComposicaoDoProvisionamento;

namespace IdentityGateway.Infrastructure.IntegrationTests.Provisioning;

/// <summary>
/// D15: o e-mail do admin não aparece em log, span, <c>outbox_messages.error</c> — no caminho feliz e nas falhas.
/// </summary>
/// <remarks>
/// <para>
/// <b>Na composição real</b> (Application + Infrastructure, PostgreSQL, Keycloak e mailpit), com todas as categorias
/// em <c>Trace</c> e as instrumentações que a Api liga: um teste só da Application não enxergaria o EF, o
/// <c>HttpClient</c>, a resiliência nem o <c>OutboxProcessor</c>.
/// </para>
/// <para>
/// <b>O log de dados sensíveis do EF vale o que o <c>appsettings.Development.json</c> diz</b>, lido do arquivo: é a
/// configuração que a IDE e o compose usam. Ligado, o EF registra o parâmetro do <c>INSERT</c> por construção — e é
/// exatamente o que este teste pega se alguém religar a opção no arquivo.
/// </para>
/// <para>
/// <b>Antes de afirmar "nada vazou", afirma que o canal foi capturado</b>: um log e um span do <c>HttpClient</c> com
/// <c>/users</c>. Sem isso, um coletor mal ligado passaria o teste sem ver nada.
/// </para>
/// <para>
/// <b>Só os spans do próprio teste.</b> O <c>ActivityListener</c> do OpenTelemetry vale para o processo inteiro, e as
/// outras classes do assembly rodam em paralelo falando com o mesmo Npgsql e o mesmo <c>HttpClient</c>: sem filtro,
/// os spans delas cairiam na lista — alterando-a no meio da asserção e podendo satisfazer sozinhos a pré-condição do
/// canal. Cada teste abre uma raiz gravada (<see cref="RaizDoRastro"/>) e o amostrador descarta toda raiz sem pai,
/// então só o que roda debaixo dela, no fluxo do teste, é exportado.
/// </para>
/// </remarks>
public sealed class VazamentoDoEmailTests(PostgresFixture postgres, KeycloakFixture keycloak)
    : IClassFixture<PostgresFixture>
{
    private static readonly string[] SoAtualizarSenha = ["UPDATE_PASSWORD"];

    private static string SensitiveDataLoggingDoDevelopment()
    {
        string caminho = RaizDoRepositorio.Caminho("src", "IdentityGateway.Api", "appsettings.Development.json");

        using var desenvolvimento = JsonDocument.Parse(File.ReadAllText(caminho));
        return desenvolvimento.RootElement.GetProperty("Database").GetProperty("EnableSensitiveDataLogging")
            .GetBoolean() ? "true" : "false";
    }

    /// <summary>
    /// A raiz do rastro do teste, já gravada: o amostrador de <see cref="Compor"/> grava só o que vier debaixo dela.
    /// </summary>
    private static Activity RaizDoRastro() =>
        new Activity(nameof(VazamentoDoEmailTests)) { ActivityTraceFlags = ActivityTraceFlags.Recorded }.Start();

    private ServiceProvider Compor(ColetorDeLogs logs, List<Activity> spans, Action<IServiceCollection>? ajustar = null)
    {
        ServiceProvider provider = Criar(
            postgres,
            keycloak,
            services =>
            {
                services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
                services.AddOpenTelemetry().WithTracing(tracing => tracing
                    .SetSampler(new ParentBasedSampler(new AlwaysOffSampler()))
                    .AddHttpClientInstrumentation()
                    .AddNpgsql()
                    .AddInMemoryExporter(spans));
                ajustar?.Invoke(services);
            },
            new Dictionary<string, string?>
            {
                ["Database:EnableSensitiveDataLogging"] = SensitiveDataLoggingDoDevelopment(),
            });

        // Sem host, ninguém liga o TracerProvider: resolvê-lo é o que começa a ouvir as atividades.
        _ = provider.GetRequiredService<TracerProvider>();

        return provider;
    }

    private static async Task<TenantId> RegistrarEProcessarAsync(
        ServiceProvider provider, string email, CancellationToken ct)
    {
        TenantId tenant = await RegistrarAsync(provider, email, ct);
        await LiberarAsync(provider, (await MensagemAsync(provider, "tenant-registered", tenant, ct)).Id, ct);
        await ProcessarCicloAsync(provider, ct);
        return tenant;
    }

    private static string TextoDoSpan(Activity span) =>
        string.Join(
            " ",
            [
                span.DisplayName,
                span.StatusDescription ?? string.Empty,
                .. span.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"),
                .. span.Events.SelectMany(evento => evento.Tags.Select(tag => $"{evento.Name} {tag.Key}={tag.Value}")),
            ]);

    private static async Task AfirmarQueNadaVazouAsync(
        string email, ColetorDeLogs logs, List<Activity> spans, ServiceProvider provider, TenantId tenant,
        CancellationToken ct)
    {
        logs.Registros.Should().Contain(
            registro => registro.Categoria.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal)
                        && registro.Texto.Contains("/users", StringComparison.Ordinal),
            "sem o canal do HttpClient capturado, 'nada vazou' não prova nada");
        spans.Should().Contain(
            span => TextoDoSpan(span).Contains("/users", StringComparison.Ordinal),
            "idem para os spans do HttpClient");

        // O e-mail cru e escapado (é assim que ele iria numa URL: %2B e %40), e o fragmento GUID único dele, que
        // cobre qualquer outra codificação (o "+" do JSON, por exemplo).
        string fragmentoUnico = email[(email.IndexOf('+') + 1)..email.IndexOf('@')];
        string[] formas = [email, Uri.EscapeDataString(email), fragmentoUnico];

        foreach (string forma in formas)
        {
            logs.Registros.Where(registro => registro.Texto.Contains(forma, StringComparison.OrdinalIgnoreCase))
                .Select(registro => $"{registro.Categoria}: {registro.Texto}")
                .Should().BeEmpty($"nenhum log pode conter o e-mail ({forma})");

            spans.Where(span => TextoDoSpan(span).Contains(forma, StringComparison.OrdinalIgnoreCase))
                .Select(TextoDoSpan)
                .Should().BeEmpty($"nenhum span pode conter o e-mail ({forma})");
        }

        await using AsyncServiceScope escopo = provider.CreateAsyncScope();
        AppDbContext contexto = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
        List<OutboxMessage> mensagens = await contexto.OutboxMessages.AsNoTracking().ToListAsync(ct);

        mensagens.Where(mensagem => mensagem.Content.Contains(tenant.Value.ToString(), StringComparison.Ordinal))
            .Select(mensagem => mensagem.Error ?? string.Empty)
            .Should().NotContain(erro => formas.Any(forma => erro.Contains(forma, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task CaminhoFeliz_NadaVaza()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ColetorDeLogs logs = new();
        List<Activity> spans = [];
        using Activity rastro = RaizDoRastro();
        await using ServiceProvider provider = Compor(logs, spans);
        string email = KeycloakFixture.EmailUnico();

        TenantId tenant = await RegistrarEProcessarAsync(provider, email, ct);

        (await TenantAsync(provider, tenant, ct)).Status.Should().Be(TenantStatus.Active);
        await AfirmarQueNadaVazouAsync(email, logs, spans, provider, tenant, ct);
    }

    [Fact]
    public async Task Inconsistencia_NadaVaza()
    {
        // O e-mail já pertence a uma conta sem o nosso tenant_id (D5): a exceção vai para o log do handler.
        CancellationToken ct = TestContext.Current.CancellationToken;
        ColetorDeLogs logs = new();
        List<Activity> spans = [];
        using Activity rastro = RaizDoRastro();
        await using ServiceProvider provider = Compor(logs, spans);
        string email = KeycloakFixture.EmailUnico();
        await keycloak.CriarUsuarioComoMasterAsync(new { username = email, email, enabled = true }, ct);

        TenantId tenant = await RegistrarEProcessarAsync(provider, email, ct);

        (await TenantAsync(provider, tenant, ct)).Status.Should().Be(TenantStatus.ProvisioningFailed);
        await AfirmarQueNadaVazouAsync(email, logs, spans, provider, tenant, ct);
    }

    [Fact]
    public async Task ConflitoNoPost_NadaVaza()
    {
        // Username igual ao e-mail em outra conta: GET vazio, POST 409, reconsulta, inconsistência.
        CancellationToken ct = TestContext.Current.CancellationToken;
        ColetorDeLogs logs = new();
        List<Activity> spans = [];
        using Activity rastro = RaizDoRastro();
        await using ServiceProvider provider = Compor(logs, spans);
        string email = KeycloakFixture.EmailUnico();
        await keycloak.CriarUsuarioComoMasterAsync(
            new { username = email, email = $"outro+{Guid.NewGuid():N}@acme.test", enabled = true }, ct);

        TenantId tenant = await RegistrarEProcessarAsync(provider, email, ct);

        (await TenantAsync(provider, tenant, ct)).Status.Should().Be(TenantStatus.ProvisioningFailed);
        await AfirmarQueNadaVazouAsync(email, logs, spans, provider, tenant, ct);
    }

    [Fact]
    public async Task UsuarioDesabilitado_NadaVaza()
    {
        // O nosso usuário (com o tenant_id deste tenant), desabilitado à mão: o envio dá 400.
        CancellationToken ct = TestContext.Current.CancellationToken;
        ColetorDeLogs logs = new();
        List<Activity> spans = [];
        using Activity rastro = RaizDoRastro();
        await using ServiceProvider provider = Compor(logs, spans);
        string email = KeycloakFixture.EmailUnico();

        TenantId tenant = await RegistrarAsync(provider, email, ct);
        await keycloak.CriarUsuarioComoMasterAsync(new
        {
            username = email,
            email,
            enabled = false,
            requiredActions = SoAtualizarSenha,
            attributes = new Dictionary<string, string[]> { ["tenant_id"] = [tenant.Value.ToString()] },
        }, ct);
        await LiberarAsync(provider, (await MensagemAsync(provider, "tenant-registered", tenant, ct)).Id, ct);
        await ProcessarCicloAsync(provider, ct);

        (await TenantAsync(provider, tenant, ct)).Status.Should().Be(TenantStatus.ProvisioningFailed);
        await AfirmarQueNadaVazouAsync(email, logs, spans, provider, tenant, ct);
    }

    [Fact]
    public async Task SmtpFora_NadaVazaNemNoErroDoOutbox()
    {
        // O 500 sobe como transitório e a mensagem da exceção vai para outbox_messages.error.
        CancellationToken ct = TestContext.Current.CancellationToken;
        ColetorDeLogs logs = new();
        List<Activity> spans = [];
        using Activity rastro = RaizDoRastro();
        Interceptacao interceptacao = new()
        {
            ResponderSemEnviar = (pedido, _) =>
                pedido.Method == HttpMethod.Put ? HttpStatusCode.InternalServerError : null,
        };
        await using ServiceProvider provider = Compor(logs, spans, services => services.Interceptar(interceptacao));
        string email = KeycloakFixture.EmailUnico();

        TenantId tenant = await RegistrarEProcessarAsync(provider, email, ct);

        (await TenantAsync(provider, tenant, ct)).Status.Should().Be(TenantStatus.Pending);
        (await MensagemAsync(provider, "tenant-registered", tenant, ct)).Error.Should().NotBeNullOrEmpty();
        await AfirmarQueNadaVazouAsync(email, logs, spans, provider, tenant, ct);
    }

    [Fact]
    public async Task FalhaNoCommit_NadaVaza()
    {
        // Conflito de xmin de verdade: o tenant é tocado por fora entre a leitura do handler e o commit (durante o
        // envio do convite), e o SaveChanges do TransactionBehavior falha pelo caminho real do EF.
        CancellationToken ct = TestContext.Current.CancellationToken;
        ColetorDeLogs logs = new();
        List<Activity> spans = [];
        using Activity rastro = RaizDoRastro();
        TenantId[] alvo = [default];
        Interceptacao interceptacao = new()
        {
            AntesDeEnviar = async (pedido, _) =>
            {
                if (pedido.Method == HttpMethod.Put)
                {
                    await using AppDbContext externo = postgres.CriarContexto();
                    await externo.Database.ExecuteSqlAsync(
                        $"UPDATE tenants SET name = name WHERE id = {alvo[0].Value}", ct);
                }
            },
        };
        await using ServiceProvider provider = Compor(logs, spans, services => services.Interceptar(interceptacao));
        string email = KeycloakFixture.EmailUnico();

        TenantId tenant = await RegistrarAsync(provider, email, ct);
        alvo[0] = tenant;
        await LiberarAsync(provider, (await MensagemAsync(provider, "tenant-registered", tenant, ct)).Id, ct);
        await ProcessarCicloAsync(provider, ct);

        (await TenantAsync(provider, tenant, ct)).Status.Should().Be(TenantStatus.Pending);
        (await MensagemAsync(provider, "tenant-registered", tenant, ct)).Error.Should().NotBeNullOrEmpty();
        await AfirmarQueNadaVazouAsync(email, logs, spans, provider, tenant, ct);
    }
}
