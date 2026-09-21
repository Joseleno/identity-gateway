using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Tenants.RegisterTenant;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;
using NSubstitute;
using Events = IdentityGateway.Domain.Tenants.Events;

namespace IdentityGateway.Application.UnitTests.Tenants.RegisterTenant;

/// <summary>
/// Cobre a orquestração do registro: validar o slug, checar unicidade, resolver o plano, registrar.
/// </summary>
/// <remarks>
/// As portas são dublês porque o que está sob teste é a <b>ordem das decisões</b> e a tradução em
/// <c>Result</c> — não a persistência, que é assunto do teste de integração.
/// </remarks>
public sealed class RegisterTenantHandlerTests
{
    private readonly ITenantRepository _repositorio = Substitute.For<ITenantRepository>();
    private readonly IPlanCatalog _catalogo = Substitute.For<IPlanCatalog>();
    private readonly RegisterTenantHandler _handler;

    public RegisterTenantHandlerTests()
    {
        _catalogo.Find("free").Returns(new Plan(PlanTier.Free, 5, 1));
        _repositorio.SlugExistsAsync(Arg.Any<TenantSlug>(), Arg.Any<CancellationToken>()).Returns(false);

        _handler = new RegisterTenantHandler(_repositorio, _catalogo);
    }

    private static RegisterTenantCommand Comando(string slug = "acme", string plano = "free") =>
        new("Acme", slug, plano, "admin@acme.com");

    [Fact]
    public async Task ComandoValido_RegistraEDevolveOId()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        Result<TenantId> resultado = await _handler.Handle(Comando(), ct);

        resultado.IsSuccess.Should().BeTrue();
        resultado.Value.Value.Should().NotBe(Guid.Empty);
        _repositorio.Received(1).Add(Arg.Is<Tenant>(tenant => tenant.Slug.Value == "acme"));
    }

    [Fact]
    public async Task ComandoValido_LevantaOEventoDeRegistro()
    {
        // O evento é o que o Outbox grava na mesma transação — sem ele, nenhum tenant seria provisionado.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant? capturado = null;
        _repositorio.Add(Arg.Do<Tenant>(tenant => capturado = tenant));

        await _handler.Handle(Comando(), ct);

        capturado.Should().NotBeNull();
        capturado!.DomainEvents.Should().ContainSingle(evento => evento is Events.TenantRegistered);
    }

    [Fact]
    public async Task SlugMalFormado_DevolveValidation()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        Result<TenantId> resultado = await _handler.Handle(Comando(slug: "-invalido-"), ct);

        resultado.IsFailure.Should().BeTrue();
        resultado.Error.Code.Should().Be("TenantSlug.Invalido");
        _repositorio.DidNotReceive().Add(Arg.Any<Tenant>());
    }

    [Fact]
    public async Task SlugJaEmUso_DevolveConflict()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        _repositorio.SlugExistsAsync(Arg.Any<TenantSlug>(), Arg.Any<CancellationToken>()).Returns(true);

        Result<TenantId> resultado = await _handler.Handle(Comando(), ct);

        resultado.IsFailure.Should().BeTrue();
        resultado.Error.Code.Should().Be("Tenant.SlugEmUso");
        _repositorio.DidNotReceive().Add(Arg.Any<Tenant>());
    }

    [Fact]
    public async Task PlanoDesconhecido_DevolveValidation()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        _catalogo.Find("inexistente").Returns((Plan?)null);

        Result<TenantId> resultado = await _handler.Handle(Comando(plano: "inexistente"), ct);

        resultado.IsFailure.Should().BeTrue();
        resultado.Error.Code.Should().Be("Tenant.PlanoDesconhecido");
        _repositorio.DidNotReceive().Add(Arg.Any<Tenant>());
    }
}
