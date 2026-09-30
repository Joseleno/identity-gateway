using System.Net;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Tenants.ProvisionTenant;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Domain.ValueObjects;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace IdentityGateway.Application.UnitTests.Tenants.ProvisionTenant;

public sealed class ProvisionTenantHandlerTests
{
    private const string EnderecoDoAdmin = "segredo+admin@acme.test";
    private static readonly DateTimeOffset Registro = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Agora = Registro.AddMinutes(5);
    private static readonly TimeSpan Janela = TimeSpan.FromHours(24);
    private static readonly TimeSpan PrazoDoLink = TimeSpan.FromDays(3);
    private static readonly ExternalUserId Sub = ExternalUserId.From("sub-admin");

    private readonly ITenantRepository _tenants = Substitute.For<ITenantRepository>();
    private readonly IMemberRepository _membros = Substitute.For<IMemberRepository>();
    private readonly IIdentityProvider _identidade = Substitute.For<IIdentityProvider>();
    private readonly IProvisioningPolicy _politica = Substitute.For<IProvisioningPolicy>();
    private readonly IInvitationPolicy _convites = Substitute.For<IInvitationPolicy>();
    private readonly IDateTimeProvider _relogio = Substitute.For<IDateTimeProvider>();
    private readonly ColetorDeLogs<ProvisionTenantHandler> _logs = new();
    private readonly ProvisionTenantHandler _handler;

    public ProvisionTenantHandlerTests()
    {
        _politica.MaxPendingDuration.Returns(Janela);
        _convites.LinkLifetime.Returns(PrazoDoLink);
        _relogio.UtcNow.Returns(Agora);
        _handler = new ProvisionTenantHandler(
            _tenants, _membros, _identidade, _politica, _convites, _relogio, _logs);
    }

    /// <summary>
    /// As duas chamadas ao provedor, uma por linha: cada entrada faz lançar uma delas, e a anterior funciona.
    /// </summary>
    /// <remarks>
    /// O handler cobre as duas com o mesmo <c>try</c>; uma linha por chamada prova que a classificação do erro não
    /// depende de onde ele veio. O rótulo da linha nomeia a chamada no relatório do teste.
    /// </remarks>
    public static TheoryData<Action<IIdentityProvider, Exception>> ChamadasQueFalham => new()
    {
        new TheoryDataRow<Action<IIdentityProvider, Exception>>(OrganizacaoLanca) { Label = "Organization" },
        new TheoryDataRow<Action<IIdentityProvider, Exception>>(ConviteLanca) { Label = "convite" },
    };

    /// <summary>
    /// Cada ramo do handler que registra log, preparado sobre a instância do teste; devolve o tenant a provisionar.
    /// </summary>
    /// <remarks>
    /// O ramo transitório dentro da janela não está aqui: a exceção sobe, e quem registra é o <c>OutboxProcessor</c>
    /// (coberto no teste de vazamento do e-mail).
    /// </remarks>
    public static TheoryData<Func<ProvisionTenantHandlerTests, TenantId>> RamosQueRegistram => new()
    {
        new TheoryDataRow<Func<ProvisionTenantHandlerTests, TenantId>>(_ => TenantId.New()) { Label = "inexistente" },
        new TheoryDataRow<Func<ProvisionTenantHandlerTests, TenantId>>(teste => teste.TenantAtivo().Id)
        {
            Label = "ativo",
        },
        new TheoryDataRow<Func<ProvisionTenantHandlerTests, TenantId>>(teste => teste.TenantPendenteSemEmail().Id)
        {
            Label = "sem e-mail",
        },
        new TheoryDataRow<Func<ProvisionTenantHandlerTests, TenantId>>(teste => teste.TenantPendente(maxUsers: 0).Id)
        {
            Label = "sem vaga",
        },
        new TheoryDataRow<Func<ProvisionTenantHandlerTests, TenantId>>(teste => teste.TenantQueProvisiona().Id)
        {
            Label = "sucesso",
        },
        new TheoryDataRow<Func<ProvisionTenantHandlerTests, TenantId>>(teste => teste.TenantComContaAlheia().Id)
        {
            Label = "inconsistência no convite",
        },
        new TheoryDataRow<Func<ProvisionTenantHandlerTests, TenantId>>(teste => teste.TenantComJanelaEsgotada().Id)
        {
            Label = "janela esgotada no convite",
        },
    };

    private Tenant TenantPendente(int maxUsers = 5)
    {
        var tenant = Tenant.Register(
            "Acme", TenantSlug.Create("acme").Value, new Plan(PlanTier.Free, maxUsers, 1),
            Email.Of(EnderecoDoAdmin).Value, Registro);
        _tenants.GetAsync(tenant.Id, Arg.Any<CancellationToken>()).Returns(tenant);
        return tenant;
    }

    // Tenant registrado antes da fatia C: a coluna nasceu nula na migration. O domínio não produz esse estado — a
    // reflexão simula a linha antiga que o EF materializaria.
    private Tenant TenantPendenteSemEmail()
    {
        Tenant tenant = TenantPendente();
        typeof(Tenant).GetProperty(nameof(Tenant.InitialAdminEmail))!.SetValue(tenant, null);
        return tenant;
    }

    private Tenant TenantAtivo()
    {
        Tenant tenant = TenantPendente();
        tenant.CompleteProvisioning("org-1", Sub, Agora);
        return tenant;
    }

    private Tenant TenantQueProvisiona()
    {
        Tenant tenant = TenantPendente();
        OrganizacaoDevolve(_identidade, "org-1");
        ConviteDevolve(Sub);
        return tenant;
    }

    private Tenant TenantComContaAlheia()
    {
        Tenant tenant = TenantPendente();
        ConviteLanca(_identidade, new IdentityProviderInconsistencyException("conta alheia no provedor"));
        return tenant;
    }

    private Tenant TenantComJanelaEsgotada()
    {
        Tenant tenant = TenantPendente();
        RelogioEm(Registro + Janela + TimeSpan.FromMinutes(1));
        ConviteLanca(_identidade, new HttpRequestException("Keycloak fora do ar"));
        return tenant;
    }

    private static void OrganizacaoDevolve(IIdentityProvider identidade, string organizacao) =>
        identidade
            .EnsureOrganizationAsync(Arg.Any<TenantId>(), Arg.Any<TenantSlug>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(organizacao);

    private void ConviteDevolve(ExternalUserId sub) =>
        _identidade
            .EnsureInvitedUserAsync(Arg.Any<string>(), Arg.Any<TenantId>(), Arg.Any<InviteData>(), Arg.Any<CancellationToken>())
            .Returns(sub);

    private static void OrganizacaoLanca(IIdentityProvider identidade, Exception excecao) =>
        identidade
            .EnsureOrganizationAsync(Arg.Any<TenantId>(), Arg.Any<TenantSlug>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(excecao);

    // A Organization funciona; o convite, não.
    private static void ConviteLanca(IIdentityProvider identidade, Exception excecao)
    {
        OrganizacaoDevolve(identidade, "org-1");
        identidade
            .EnsureInvitedUserAsync(Arg.Any<string>(), Arg.Any<TenantId>(), Arg.Any<InviteData>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(excecao);
    }

    private void RelogioEm(DateTimeOffset instante) => _relogio.UtcNow.Returns(instante);

    private async Task NadaFoiChamadoNoKeycloakAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await _identidade.DidNotReceiveWithAnyArgs().EnsureOrganizationAsync(default, default!, default!, ct);
        await _identidade.DidNotReceiveWithAnyArgs().EnsureInvitedUserAsync(default!, default, default!, ct);
    }

    [Fact]
    public async Task TenantInexistente_SucessoSemChamarOProvedor()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(TenantId.New()), ct);

        resultado.IsSuccess.Should().BeTrue();
        await NadaFoiChamadoNoKeycloakAsync();
    }

    [Fact]
    public async Task TenantJaAtivo_SucessoSemChamarOProvedor()
    {
        // Mensagem repetida: o Outbox entrega at-least-once, e a segunda entrega precisa ser inofensiva.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantAtivo();

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        await NadaFoiChamadoNoKeycloakAsync();
        _membros.DidNotReceive().Add(Arg.Any<Member>());
    }

    [Fact]
    public async Task TenantEmProvisioningFailed_NaoEReprovisionado()
    {
        // Failed é decisão registrada; uma mensagem repetida não pode desfazê-la em silêncio. A saída de Failed é o
        // retry manual, que devolve o tenant a Pending antes de reenfileirar.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        tenant.MarkProvisioningFailed();

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
        await NadaFoiChamadoNoKeycloakAsync();
    }

    [Fact]
    public async Task TenantPendente_GaranteOrganizacaoConvidaEAtivaComOMember()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        OrganizacaoDevolve(_identidade, "org-42");
        ConviteDevolve(Sub);
        Member? adicionado = null;
        _membros.Add(Arg.Do<Member>(membro => adicionado = membro));

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.Active);
        tenant.ExternalOrganizationId.Should().Be("org-42");
        tenant.OccupiedSeats.Should().Be(1);
        tenant.InitialAdminEmail.Should().BeNull();

        await _identidade.Received(1).EnsureOrganizationAsync(tenant.Id, tenant.Slug, tenant.Name, ct);
        await _identidade.Received(1).EnsureInvitedUserAsync(
            "org-42",
            tenant.Id,
            Arg.Is<InviteData>(convite =>
                convite.Email.Value == EnderecoDoAdmin
                && convite.Role == RoleName.TenantAdmin
                && convite.LinkLifetime == PrazoDoLink),
            ct);

        _membros.Received(1).Add(Arg.Any<Member>());
        adicionado!.TenantId.Should().Be(tenant.Id);
        adicionado.ExternalUserId.Should().Be(Sub);
        adicionado.InvitedAt.Should().Be(Agora, "CompleteProvisioning recebe relogio.UtcNow");
    }

    [Fact]
    public async Task SemEmailDoAdmin_MarcaFailedSemTocarOKeycloak()
    {
        // Só acontece com tenant registrado antes da fatia C. Sem o e-mail, ninguém seria convidado, e repetir não
        // traz o e-mail de volta: falha permanente, e o retry manual o recebe de novo (D13).
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendenteSemEmail();

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
        await NadaFoiChamadoNoKeycloakAsync();
    }

    [Fact]
    public async Task SemVaga_MarcaFailedSemTocarOKeycloak()
    {
        // D14: deixar a falta de vaga para o CompleteProvisioning faria cada retry reenviar o convite até a janela.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente(maxUsers: 0);

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
        tenant.InitialAdminEmail.Should().BeNull("D13: a falha também apaga o e-mail");
        await NadaFoiChamadoNoKeycloakAsync();
    }

    [Theory]
    [MemberData(nameof(ChamadasQueFalham))]
    public async Task Inconsistencia_MarcaFailedSemEsperarAJanela(Action<IIdentityProvider, Exception> falhar)
    {
        // Erro permanente, venha de qualquer uma das duas chamadas: repetir só adiaria o Failed por configuração, não
        // por decisão.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        falhar(_identidade, new IdentityProviderInconsistencyException("conta alheia no provedor"));

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
        _membros.DidNotReceive().Add(Arg.Any<Member>());
    }

    [Theory]
    [MemberData(nameof(ChamadasQueFalham))]
    public async Task FalhaTransitoriaDentroDaJanela_PropagaEMantemPendingComOEmail(
        Action<IIdentityProvider, Exception> falhar)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        HttpRequestException falha = new("Keycloak fora do ar");
        falhar(_identidade, falha);

        Func<Task> provisionar = async () => await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        (await provisionar.Should().ThrowAsync<HttpRequestException>()).Which.Should().BeSameAs(falha);
        tenant.Status.Should().Be(TenantStatus.Pending);
        tenant.InitialAdminEmail!.Value.Should().Be(EnderecoDoAdmin, "a próxima entrega precisa do e-mail");
        _membros.DidNotReceive().Add(Arg.Any<Member>());
    }

    [Theory]
    [MemberData(nameof(ChamadasQueFalham))]
    public async Task FalhaTransitoriaDepoisDaJanela_MarcaFailed(Action<IIdentityProvider, Exception> falhar)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        RelogioEm(Registro + Janela + TimeSpan.FromMinutes(1));
        falhar(_identidade, new HttpRequestException("Keycloak fora do ar"));

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
        _membros.DidNotReceive().Add(Arg.Any<Member>());
    }

    [Fact]
    public async Task SmtpForaEDeVolta_FicaPendingComEmailEDepoisAtiva()
    {
        // O 500 do SMTP é transitório; na volta, a mesma entrega completa.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        OrganizacaoDevolve(_identidade, "org-1");
        _identidade
            .EnsureInvitedUserAsync(Arg.Any<string>(), Arg.Any<TenantId>(), Arg.Any<InviteData>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new HttpRequestException("500", inner: null, HttpStatusCode.InternalServerError),
                _ => Task.FromResult(Sub));

        Func<Task> primeira = async () => await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);
        await primeira.Should().ThrowAsync<HttpRequestException>();
        tenant.Status.Should().Be(TenantStatus.Pending);
        tenant.InitialAdminEmail.Should().NotBeNull();

        Result segunda = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        segunda.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.Active);
        tenant.InitialAdminEmail.Should().BeNull();
        _membros.Received(1).Add(Arg.Any<Member>());
    }

    [Fact]
    public async Task FalhaNaFronteiraExataDaJanela_MarcaFailed()
    {
        // A janela é fechada no fim: RegisteredAt + janela já é "tempo demais".
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        RelogioEm(Registro + Janela);
        OrganizacaoLanca(_identidade, new HttpRequestException("Keycloak fora do ar"));

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
    }

    [Fact]
    public async Task TimeoutDaResilienciaDepoisDaJanela_MarcaFailed()
    {
        // TaskCanceledException é o que o timeout cru do HttpClient.Timeout produz, e É um OperationCanceledException;
        // o filtro olha o CancellationToken, não o tipo.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        RelogioEm(Registro + Janela + TimeSpan.FromMinutes(1));
        ConviteLanca(
            _identidade, new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
    }

    [Fact]
    public async Task Keycloak403DentroEForaDaJanela_SoViraFailedDepoisDela()
    {
        // Um volume antigo sem manage-users dá 403 no vínculo: não é inconsistência (corrigir o realm resolve), então
        // segue a janela — e o health check avisa antes.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        ConviteLanca(_identidade, new HttpRequestException("Forbidden", inner: null, HttpStatusCode.Forbidden));

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
        ConviteLanca(_identidade, new OperationCanceledException(desligando.Token));

        Func<Task> provisionar = async () =>
            await _handler.Handle(new ProvisionTenantCommand(tenant.Id), desligando.Token);

        await provisionar.Should().ThrowAsync<OperationCanceledException>();
        tenant.Status.Should().Be(TenantStatus.Pending);
    }

    [Theory]
    [MemberData(nameof(RamosQueRegistram))]
    public async Task CadaRamoQueRegistra_TemLogENenhumContemOEmail(
        Func<ProvisionTenantHandlerTests, TenantId> preparar)
    {
        // D15: contagem maior que zero em cada ramo prova que o coletor viu o log — um teste só com "nenhum contém"
        // passaria com o log desligado.
        CancellationToken ct = TestContext.Current.CancellationToken;
        TenantId alvo = preparar(this);

        await _handler.Handle(new ProvisionTenantCommand(alvo), ct);

        _logs.Registros.Should().NotBeEmpty();
        _logs.Registros.Select(registro => registro.Texto).Should().NotContain(texto =>
            texto.Contains("segredo", StringComparison.OrdinalIgnoreCase));
    }
}
