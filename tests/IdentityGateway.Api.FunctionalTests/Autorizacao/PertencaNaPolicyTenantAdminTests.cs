using System.Security.Claims;
using IdentityGateway.Api.Authorization;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Api.FunctionalTests.Autorizacao;

/// <summary>
/// A quarta camada da policy <c>TenantAdmin</c>: a pertença no banco (ADR-011), pela porta <c>IMemberQueries</c>.
/// </summary>
/// <remarks>
/// Aqui a porta é falsa e conta as consultas. O que se prova: quais estados do membro passam, que quem não é membro é
/// vetado, e que <b>a porta só é consultada para quem já passou nas três camadas do token</b>.
/// </remarks>
public sealed class PertencaNaPolicyTenantAdminTests
{
    private const string Tenant = "0199a000-0000-7000-8000-00000000000a";

    private const string OutroTenant = "0199a000-0000-7000-8000-00000000000b";

    private const string Sub = "5f1c0a2e-7c1d-4a55-9b0e-2f6f3c9d1a10";

    private static readonly string[] SoTenantAdmin = ["tenant-admin"];

    private static readonly string[] SoOTenant = [Tenant];

    /// <summary>
    /// A decisão para cada estado do membro, escrita à mão. Um estado novo no enum não está aqui — e o teste reprova
    /// até alguém decidir, em vez de herdar uma resposta.
    /// </summary>
    private static readonly Dictionary<MemberStatus, bool> PassaNaPertenca = new()
    {
        [MemberStatus.Invited] = true,
        [MemberStatus.Active] = true,
        [MemberStatus.Deactivated] = false,
        [MemberStatus.Expired] = false,
        [MemberStatus.Revoked] = false,
        [MemberStatus.Erased] = false,
    };

    public static TheoryData<MemberStatus> TodosOsEstados => [.. Enum.GetValues<MemberStatus>()];

    private static async Task<AuthorizationResult> AutorizarAsync(
        ClaimsPrincipal usuario, string? rota, PertencaFalsa pertenca, bool comHandlerQueAprovaTudo)
    {
        await using ServiceProvider provider = MontagemDaAutorizacao.Montar(services =>
        {
            services.AddSingleton<IMemberQueries>(pertenca);

            if (comHandlerQueAprovaTudo)
            {
                services.AddSingleton<IAuthorizationHandler, HandlerQueAprovaTudo>();
            }
        });
        await using AsyncServiceScope escopo = provider.CreateAsyncScope();

        return await escopo.ServiceProvider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(usuario, MontagemDaAutorizacao.Pedido(rota), Policies.TenantAdmin);
    }

    [Theory]
    [MemberData(nameof(TodosOsEstados))]
    public async Task CadaEstadoDoMembro_PassaOuEVetadoComoATabela(MemberStatus estado)
    {
        PassaNaPertenca.Should().ContainKey(
            estado, "estado novo no enum: decida se ele passa na pertença e acrescente à tabela deste teste");
        bool esperado = PassaNaPertenca[estado];
        ClaimsPrincipal usuario = MontagemDaAutorizacao.Usuario(SoTenantAdmin, SoOTenant);

        AuthorizationResult sozinho = await AutorizarAsync(
            usuario, Tenant, new PertencaFalsa(estado), comHandlerQueAprovaTudo: false);
        AuthorizationResult comVizinho = await AutorizarAsync(
            usuario, Tenant, new PertencaFalsa(estado), comHandlerQueAprovaTudo: true);

        sozinho.Succeeded.Should().Be(esperado);
        comVizinho.Succeeded.Should().Be(esperado, "um handler que aprova tudo não muda a decisão da pertença");

        if (!esperado)
        {
            comVizinho.Failure!.FailCalled.Should().BeTrue("a negação precisa ser um Fail()");
        }
    }

    [Fact]
    public async Task QuemNaoEMembroDoTenant_EVetado()
    {
        // O caso do ataque: papel certo, tenant_id certo (forjado por um grupo no Keycloak), e nenhum Member no banco.
        ClaimsPrincipal usuario = MontagemDaAutorizacao.Usuario(SoTenantAdmin, SoOTenant);
        PertencaFalsa pertenca = new(status: null);

        AuthorizationResult resultado = await AutorizarAsync(usuario, Tenant, pertenca, comHandlerQueAprovaTudo: true);

        resultado.Succeeded.Should().BeFalse();
        resultado.Failure!.FailCalled.Should().BeTrue();
        pertenca.Consultas.Should().Be(1, "controle: a negação veio da pertença, e não de uma camada anterior");
    }

    [Theory]
    [InlineData(Tenant)]
    [InlineData("0199A000-0000-7000-8000-00000000000A")]
    [InlineData("0199a00000007000800000000000000a")]
    public async Task APertenca_EConsultadaUmaVezComOTenantDaRotaEOSubDoToken(string rota)
    {
        ClaimsPrincipal usuario = MontagemDaAutorizacao.Usuario(SoTenantAdmin, SoOTenant, Sub);
        PertencaFalsa pertenca = new(MemberStatus.Active);

        AuthorizationResult resultado = await AutorizarAsync(usuario, rota, pertenca, comHandlerQueAprovaTudo: false);

        resultado.Succeeded.Should().BeTrue();
        pertenca.Consultas.Should().Be(1);
        pertenca.Ultima.Should().Be((new TenantId(Guid.Parse(Tenant)), ExternalUserId.From(Sub)));
    }

    [Theory]
    [InlineData("sem o papel tenant-admin", new[] { "reader" }, new[] { Tenant })]
    [InlineData("platform-admin que também é tenant-admin", new[] { "platform-admin", "tenant-admin" }, new[] { Tenant })]
    [InlineData("tenant-admin de outro tenant", new[] { "tenant-admin" }, new[] { OutroTenant })]
    [InlineData("tenant-admin sem tenant_id", new[] { "tenant-admin" }, new string[0])]
    public async Task QuemNaoPassaNasCamadasDoToken_NaoProvocaConsultaAoBanco(
        string caso, string[] roles, string[] tenantIds)
    {
        // O tempo de resposta não pode dizer se um sub é membro de um tenant: a pertença só é lida para quem já provou,
        // pelo token, que é tenant-admin daquele tenant. É a ordem de registro dos handlers que garante.
        ClaimsPrincipal usuario = MontagemDaAutorizacao.Usuario(roles, tenantIds);
        PertencaFalsa pertenca = new(MemberStatus.Active);

        AuthorizationResult resultado = await AutorizarAsync(usuario, Tenant, pertenca, comHandlerQueAprovaTudo: false);

        resultado.Succeeded.Should().BeFalse(caso);
        pertenca.Consultas.Should().Be(0, caso);
    }

    [Theory]
    [InlineData("sem sub", null)]
    [InlineData("sub vazio", "")]
    [InlineData("sub só com espaços", "   ")]
    public async Task TokenSemSubUtilizavel_EVetadoSemConsultar(string caso, string? sub)
    {
        // A autenticação já recusa token sem sub (401). Se um chegar aqui, não há de quem conferir a pertença.
        ClaimsPrincipal usuario = MontagemDaAutorizacao.Usuario(SoTenantAdmin, SoOTenant, sub);
        PertencaFalsa pertenca = new(MemberStatus.Active);

        AuthorizationResult resultado = await AutorizarAsync(usuario, Tenant, pertenca, comHandlerQueAprovaTudo: true);

        resultado.Succeeded.Should().BeFalse(caso);
        resultado.Failure!.FailCalled.Should().BeTrue(caso);
        pertenca.Consultas.Should().Be(0, caso);
    }

    [Theory]
    [InlineData("SUB", "sub")]
    [InlineData("sub", "SUB")]
    [InlineData("sub", "sub")]
    public async Task TokenComDoisClaimsDeSub_EVetadoSemConsultar(string primeiro, string segundo)
    {
        // O ClaimsPrincipal acha claims sem olhar a caixa do nome, e a autenticação só validou o "sub" do JSON. Com um
        // "SUB" ao lado, ler "o primeiro" conferiria a pertença de um sub que ninguém validou — e o membro seria outro.
        ClaimsPrincipal usuario = MontagemDaAutorizacao.Usuario(SoTenantAdmin, SoOTenant, sub: null);
        ((ClaimsIdentity)usuario.Identity!).AddClaims(
            [new Claim(primeiro, Sub), new Claim(segundo, "0199a000-0000-7000-8000-0000000000aa")]);
        PertencaFalsa pertenca = new(MemberStatus.Active);

        AuthorizationResult resultado = await AutorizarAsync(usuario, Tenant, pertenca, comHandlerQueAprovaTudo: true);

        resultado.Succeeded.Should().BeFalse();
        resultado.Failure!.FailCalled.Should().BeTrue();
        pertenca.Consultas.Should().Be(0);
    }
}
