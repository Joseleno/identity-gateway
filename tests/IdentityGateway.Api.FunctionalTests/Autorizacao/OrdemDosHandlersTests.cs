using System.Net;
using System.Net.Http.Headers;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Members;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Api.FunctionalTests.Autorizacao;

/// <summary>
/// Na composição real da Api, a pertença só é consultada para quem já passou nas camadas do token.
/// </summary>
/// <remarks>
/// <para>
/// O teste unitário prova a ordem no contêiner que ele mesmo monta. Aqui é o contêiner do <c>Program.cs</c>: se outro
/// registro passar a pôr um handler de autorização antes do <c>AddAutorizacaoDaGateway</c>, é este teste que vê.
/// </para>
/// <para>
/// <b>Exceção declarada à regra "só configuração" da factory:</b> a porta <c>IMemberQueries</c> é trocada por uma que
/// conta as consultas. É a única troca, vale só para esta classe, e o que está sob teste — a ordem dos handlers — não
/// é tocado por ela.
/// </para>
/// </remarks>
public sealed class OrdemDosHandlersTests(IdentityGatewayApiFactory factory) : IClassFixture<IdentityGatewayApiFactory>
{
    private const string Tenant = "0199a000-0000-7000-8000-00000000000a";

    private const string OutroTenant = "0199a000-0000-7000-8000-00000000000b";

    private static readonly string[] SoTenantAdmin = ["tenant-admin"];

    private async Task<(HttpStatusCode Status, int Consultas)> LerAsync(
        IReadOnlyCollection<string> roles, string? tenantIdDoToken, CancellationToken ct)
    {
        PertencaFalsa pertenca = new(MemberStatus.Active);

        await using WebApplicationFactory<Program> api = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddScoped<IMemberQueries>(_ => pertenca)));
        using HttpClient client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.Emissor.Emitir(roles: roles, tenantId: tenantIdDoToken));

        using HttpResponseMessage resposta = await client.GetAsync(
            new Uri($"/api/v1/tenants/{Tenant}", UriKind.Relative), ct);

        return (resposta.StatusCode, pertenca.Consultas);
    }

    [Theory]
    [InlineData("sem o papel tenant-admin", new[] { "reader" }, Tenant)]
    [InlineData("platform-admin que também é tenant-admin", new[] { "platform-admin", "tenant-admin" }, Tenant)]
    [InlineData("tenant-admin de outro tenant", new[] { "tenant-admin" }, OutroTenant)]
    [InlineData("tenant-admin sem tenant_id", new[] { "tenant-admin" }, null)]
    public async Task QuemNaoPassaNasCamadasDoToken_Recebe403SemConsultaAPertenca(
        string caso, string[] roles, string? tenantIdDoToken)
    {
        (HttpStatusCode status, int consultas) = await LerAsync(
            roles, tenantIdDoToken, TestContext.Current.CancellationToken);

        status.Should().Be(HttpStatusCode.Forbidden, caso);
        consultas.Should().Be(0, caso);
    }

    [Fact]
    public async Task QuemPassaNasCamadasDoToken_ProvocaUmaConsulta()
    {
        // Controle: sem ele, "zero consultas" também seria verdade se a porta falsa nem estivesse ligada. A pertença
        // falsa responde Active, a policy passa, e o tenant — que não existe no banco — vira o mesmo 403 no módulo.
        (HttpStatusCode status, int consultas) = await LerAsync(
            SoTenantAdmin, Tenant, TestContext.Current.CancellationToken);

        consultas.Should().Be(1);
        status.Should().Be(HttpStatusCode.Forbidden);
    }
}
