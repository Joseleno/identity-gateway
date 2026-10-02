using System.Security.Claims;
using IdentityGateway.Api.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Api.FunctionalTests.Autorizacao;

/// <summary>
/// A policy <c>TenantAdmin</c> de verdade, fora do HTTP: cada caminho de negação termina num <c>Fail()</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Por que não basta o teste por HTTP.</b> Um requirement que faz <c>return</c> onde devia fazer <c>Fail()</c>
/// responde <c>403</c> do mesmo jeito — enquanto não houver outro handler que o aprove. No dia em que houver (uma
/// policy de leitura para o platform-admin, um handler de recurso), o <c>return</c> vira acesso. Aqui há um handler que
/// aprova tudo, de propósito: só o <c>Fail()</c> sobrevive a ele.
/// </para>
/// <para>
/// <b>Uma causa por caso.</b> Cada caso reprova em exatamente um requirement e passa nos outros: se dois reprovassem,
/// o <c>Fail()</c> de um esconderia o <c>return</c> do outro.
/// </para>
/// </remarks>
public sealed class TenantAdminPolicyTests
{
    private const string Tenant = "0199a000-0000-7000-8000-00000000000a";

    private const string OutroTenant = "0199a000-0000-7000-8000-00000000000b";

    private static readonly string[] SoTenantAdmin = ["tenant-admin"];

    private static readonly string[] SoOTenant = [Tenant];

    private static async Task<AuthorizationResult> AutorizarAsync(
        ClaimsPrincipal usuario, object? recurso, bool comHandlerQueAprovaTudo)
    {
        await using ServiceProvider provider = MontagemDaAutorizacao.Montar(services =>
        {
            if (comHandlerQueAprovaTudo)
            {
                services.AddSingleton<IAuthorizationHandler, HandlerQueAprovaTudo>();
            }
        });
        await using AsyncServiceScope escopo = provider.CreateAsyncScope();

        return await escopo.ServiceProvider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(usuario, recurso, Policies.TenantAdmin);
    }

    private static void DeveTerVetado<TQuemVeta>(AuthorizationResult resultado, string caso)
    {
        resultado.Succeeded.Should().BeFalse($"{caso}: a policy não pode passar, nem com um handler que aprova tudo");
        resultado.Failure!.FailCalled.Should().BeTrue($"{caso}: a negação precisa ser um Fail(), e não a falta de Succeed");

        // Quem vetou. Depois do primeiro Fail() nenhum outro handler roda, e por isso há um motivo só — o do
        // requirement do caso. Se ele deixasse de vetar, outro vetaria no lugar dele mais adiante, e é esta asserção
        // que diria qual.
        resultado.Failure.FailureReasons.Select(motivo => motivo.Handler.GetType())
            .Should().Equal([typeof(TQuemVeta)], $"{caso}: quem veta é o requirement do caso");
    }

    [Theory]
    [InlineData("próprio tenant", Tenant, Tenant)]
    [InlineData("rota em maiúsculas", "0199A000-0000-7000-8000-00000000000A", Tenant)]
    [InlineData("rota no formato N", "0199a00000007000800000000000000a", Tenant)]
    [InlineData("claim em maiúsculas", Tenant, "0199A000-0000-7000-8000-00000000000A")]
    public async Task TenantAdminDoTenantDaRota_Passa(string caso, string rota, string claim)
    {
        // Controles positivos, SEM o handler que aprova tudo: são os próprios requirements que aprovam. A comparação
        // é por Guid — a mesma identidade escrita de outro jeito é o mesmo tenant.
        ClaimsPrincipal usuario = MontagemDaAutorizacao.Usuario(SoTenantAdmin, [claim]);

        AuthorizationResult resultado = await AutorizarAsync(
            usuario, MontagemDaAutorizacao.Pedido(rota), comHandlerQueAprovaTudo: false);

        resultado.Succeeded.Should().BeTrue(caso);
    }

    [Theory]
    [InlineData("papel de outro nível", new[] { "reader" })]
    [InlineData("papel com outra caixa", new[] { "Tenant-Admin" })]
    [InlineData("sem o claim roles", new string[0])]
    public async Task SemOPapelTenantAdmin_Veta(string caso, string[] roles)
    {
        ClaimsPrincipal usuario = MontagemDaAutorizacao.Usuario(roles, SoOTenant);

        AuthorizationResult resultado = await AutorizarAsync(
            usuario, MontagemDaAutorizacao.Pedido(Tenant), comHandlerQueAprovaTudo: true);

        DeveTerVetado<RoleRequirement>(resultado, caso);
    }

    [Fact]
    public async Task PlatformAdminQueTambemETenantAdminDoProprioTenant_Veta()
    {
        // Separação de funções: tem o papel, tem o tenant, e mesmo assim não entra. Sem este veto, somar tenant-admin
        // e um tenant_id a uma conta de plataforma contornaria o "platform-admin não lê tenant sem auditoria".
        ClaimsPrincipal usuario = MontagemDaAutorizacao.Usuario(["platform-admin", "tenant-admin"], SoOTenant);

        AuthorizationResult resultado = await AutorizarAsync(
            usuario, MontagemDaAutorizacao.Pedido(Tenant), comHandlerQueAprovaTudo: true);

        DeveTerVetado<NotPlatformAdminRequirement>(resultado, "platform-admin + tenant-admin");
    }

    [Theory]
    [InlineData("tenant_id de outro tenant", new[] { OutroTenant })]
    [InlineData("tenant_id ausente", new string[0])]
    [InlineData("tenant_id vazio", new[] { "" })]
    [InlineData("tenant_id que não é GUID", new[] { "acme" })]
    [InlineData("tenant_id com espaço antes", new[] { " " + Tenant })]
    [InlineData("tenant_id com espaço depois", new[] { Tenant + " " })]
    [InlineData("tenant_id entre chaves", new[] { "{" + Tenant + "}" })]
    [InlineData("tenant_id no formato N", new[] { "0199a00000007000800000000000000a" })]
    [InlineData("tenant_id com sinal de mais num componente", new[] { "+199a000-0000-7000-8000-00000000000a" })]
    [InlineData("tenant_id com prefixo 0x num componente", new[] { "0199a000-0x00-7000-8000-00000000000a" })]
    [InlineData("dois tenant_id: o próprio e outro", new[] { Tenant, OutroTenant })]
    [InlineData("dois tenant_id: outro e o próprio", new[] { OutroTenant, Tenant })]
    [InlineData("dois tenant_id iguais ao próprio", new[] { Tenant, Tenant })]
    public async Task TenantDoTokenQueNaoEODaRota_Veta(string caso, string[] tenantIds)
    {
        ClaimsPrincipal usuario = MontagemDaAutorizacao.Usuario(SoTenantAdmin, tenantIds);

        AuthorizationResult resultado = await AutorizarAsync(
            usuario, MontagemDaAutorizacao.Pedido(Tenant), comHandlerQueAprovaTudo: true);

        DeveTerVetado<SameTenantRequirement>(resultado, caso);
    }

    [Theory]
    [InlineData("rota sem tenantId", null)]
    [InlineData("tenantId da rota que não é GUID", "acme")]
    [InlineData("tenantId da rota vazio", "")]
    public async Task RotaSemUmTenantIdUtilizavel_Veta(string caso, string? rota)
    {
        // Rota com policy de tenant e sem {tenantId} é erro de configuração; o teste de subida a reprova antes. Se
        // chegar aqui, nega.
        ClaimsPrincipal usuario = MontagemDaAutorizacao.Usuario(SoTenantAdmin, SoOTenant);

        AuthorizationResult resultado = await AutorizarAsync(
            usuario, MontagemDaAutorizacao.Pedido(rota), comHandlerQueAprovaTudo: true);

        DeveTerVetado<SameTenantRequirement>(resultado, caso);
    }

    [Fact]
    public async Task RecursoQueNaoEHttpContext_Veta()
    {
        // Quem chamar a policy à mão, com outro recurso (ou nenhum), não tem rota de onde ler o tenant.
        ClaimsPrincipal usuario = MontagemDaAutorizacao.Usuario(SoTenantAdmin, SoOTenant);

        AuthorizationResult semRecurso = await AutorizarAsync(usuario, recurso: null, comHandlerQueAprovaTudo: true);
        AuthorizationResult outroRecurso = await AutorizarAsync(usuario, new object(), comHandlerQueAprovaTudo: true);

        DeveTerVetado<SameTenantRequirement>(semRecurso, "sem recurso");
        DeveTerVetado<SameTenantRequirement>(outroRecurso, "recurso que não é HttpContext");
    }
}
