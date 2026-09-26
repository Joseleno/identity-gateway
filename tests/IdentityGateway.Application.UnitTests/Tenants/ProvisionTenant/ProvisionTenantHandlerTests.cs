using System.Net;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Tenants.ProvisionTenant;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace IdentityGateway.Application.UnitTests.Tenants.ProvisionTenant;

public sealed class ProvisionTenantHandlerTests
{
    private static readonly DateTimeOffset Registro = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Janela = TimeSpan.FromHours(24);

    private readonly ITenantRepository _tenants = Substitute.For<ITenantRepository>();
    private readonly IIdentityProvider _identidade = Substitute.For<IIdentityProvider>();
    private readonly IProvisioningPolicy _politica = Substitute.For<IProvisioningPolicy>();
    private readonly IDateTimeProvider _relogio = Substitute.For<IDateTimeProvider>();
    private readonly ProvisionTenantHandler _handler;

    public ProvisionTenantHandlerTests()
    {
        _politica.MaxPendingDuration.Returns(Janela);
        _relogio.UtcNow.Returns(Registro.AddMinutes(5));
        _handler = new ProvisionTenantHandler(
            _tenants, _identidade, _politica, _relogio, NullLogger<ProvisionTenantHandler>.Instance);
    }

    private Tenant TenantPendente()
    {
        var tenant = Tenant.Register("Acme", TenantSlug.Create("acme").Value, new Plan(PlanTier.Free, 5, 1), Registro);
        _tenants.GetAsync(tenant.Id, Arg.Any<CancellationToken>()).Returns(tenant);
        return tenant;
    }

    private void ProvedorDevolve(string organizacao) =>
        _identidade
            .EnsureOrganizationAsync(Arg.Any<TenantId>(), Arg.Any<TenantSlug>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(organizacao);

    private void ProvedorLanca(Exception excecao) =>
        _identidade
            .EnsureOrganizationAsync(Arg.Any<TenantId>(), Arg.Any<TenantSlug>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(excecao);

    private void RelogioEm(DateTimeOffset instante) => _relogio.UtcNow.Returns(instante);

    [Fact]
    public async Task TenantInexistente_SucessoSemChamarOProvedor()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(TenantId.New()), ct);

        resultado.IsSuccess.Should().BeTrue();
        await _identidade.DidNotReceiveWithAnyArgs().EnsureOrganizationAsync(default, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TenantJaAtivo_SucessoSemChamarOProvedor()
    {
        // Mensagem repetida: o Outbox entrega at-least-once, e a segunda entrega precisa ser inofensiva.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        tenant.MarkProvisioned("org-1");

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        await _identidade.DidNotReceiveWithAnyArgs().EnsureOrganizationAsync(default, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TenantEmProvisioningFailed_NaoEReprovisionado()
    {
        // Failed é decisão registrada; uma mensagem repetida não pode desfazê-la em silêncio. A saída de Failed é o
        // retry manual, que devolve o tenant a Pending antes de reenfileirar.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        tenant.MarkProvisioningFailed();
        ProvedorDevolve("org-1");

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
        await _identidade.DidNotReceiveWithAnyArgs().EnsureOrganizationAsync(default, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TenantPendente_GaranteAOrganizacaoEAtiva()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        ProvedorDevolve("org-42");

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.Active);
        tenant.ExternalOrganizationId.Should().Be("org-42");
        await _identidade.Received(1).EnsureOrganizationAsync(tenant.Id, tenant.Slug, tenant.Name, ct);
    }

    [Fact]
    public async Task Inconsistencia_MarcaFailedSemEsperarAJanela()
    {
        // Erro permanente: repetir só adiaria o Failed por configuração, não por decisão.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        ProvedorLanca(new IdentityProviderInconsistencyException("slug em uso por outra Organization"));

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
    }

    [Fact]
    public async Task FalhaTransitoriaDentroDaJanela_PropagaEMantemPending()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        HttpRequestException falha = new("Keycloak fora do ar");
        ProvedorLanca(falha);

        Func<Task> provisionar = async () => await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        (await provisionar.Should().ThrowAsync<HttpRequestException>()).Which.Should().BeSameAs(falha);
        tenant.Status.Should().Be(TenantStatus.Pending);
    }

    [Fact]
    public async Task FalhaTransitoriaDepoisDaJanela_MarcaFailed()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        RelogioEm(Registro + Janela + TimeSpan.FromMinutes(1));
        ProvedorLanca(new HttpRequestException("Keycloak fora do ar"));

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
    }

    [Fact]
    public async Task FalhaNaFronteiraExataDaJanela_MarcaFailed()
    {
        // A janela é fechada no fim: RegisteredAt + janela já é "tempo demais".
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        RelogioEm(Registro + Janela);
        ProvedorLanca(new HttpRequestException("Keycloak fora do ar"));

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
    }

    [Fact]
    public async Task TimeoutDaResilienciaDepoisDaJanela_MarcaFailed()
    {
        // O timeout da resiliência chega como TaskCanceledException, que É um OperationCanceledException. Um filtro
        // por tipo tiraria da janela justamente o sintoma mais comum de Keycloak lento.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        RelogioEm(Registro + Janela + TimeSpan.FromMinutes(1));
        ProvedorLanca(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
    }

    [Fact]
    public async Task Keycloak403DentroEForaDaJanela_SoViraFailedDepoisDela()
    {
        // Service account sem manage-organizations: 403 para sempre. Não é inconsistência (repetir pode resolver
        // depois que alguém corrigir o realm), então segue a janela como qualquer falha transitória.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        ProvedorLanca(new HttpRequestException("Forbidden", inner: null, HttpStatusCode.Forbidden));

        Func<Task> dentro = async () => await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);
        await dentro.Should().ThrowAsync<HttpRequestException>();
        tenant.Status.Should().Be(TenantStatus.Pending);

        RelogioEm(Registro + Janela + TimeSpan.FromSeconds(1));
        Result depois = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        depois.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
    }

    [Fact]
    public async Task DesligamentoDoHostDepoisDaJanela_PropagaSemMarcarFailed()
    {
        // O host desligando não é falha do Keycloak: a mensagem volta no próximo ciclo, de outra instância ou desta.
        Tenant tenant = TenantPendente();
        RelogioEm(Registro + Janela + TimeSpan.FromMinutes(1));
        using CancellationTokenSource desligando = new();
        await desligando.CancelAsync();
        ProvedorLanca(new OperationCanceledException(desligando.Token));

        Func<Task> provisionar = async () =>
            await _handler.Handle(new ProvisionTenantCommand(tenant.Id), desligando.Token);

        await provisionar.Should().ThrowAsync<OperationCanceledException>();
        tenant.Status.Should().Be(TenantStatus.Pending);
    }
}
