using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IdentityGateway.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace IdentityGateway.Api.FunctionalTests;

public sealed class ProvisionamentoDeTenantTests(IdentityGatewayApiFactory factory)
    : IClassFixture<IdentityGatewayApiFactory>
{
    private static string Rota(Guid id) => $"/api/v1/tenants/{id}/provisioning";

    private static async Task<(Guid Id, Uri Location)> RegistrarAsync(HttpClient client, CancellationToken ct)
    {
        string slug = $"prov-{Guid.NewGuid():N}"[..20];
        HttpResponseMessage resposta = await client.PostAsJsonAsync("/api/v1/tenants", new
        {
            name = "Acme Corp",
            slug,
            planCode = "free",
            initialAdminEmail = "admin@acme.com",
        }, ct);
        resposta.StatusCode.Should().Be(HttpStatusCode.Accepted);
        JsonElement json = await resposta.Content.ReadFromJsonAsync<JsonElement>(ct);
        return (Guid.Parse(json.GetProperty("tenantId").GetString()!), resposta.Headers.Location!);
    }

    [Fact]
    public async Task LocationDoRegistro_RespondeOStatusPending()
    {
        // Fecha o custo que o PR #1 declarou: o Location do 202 apontava para uma rota que não existia.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");
        (Guid id, Uri location) = await RegistrarAsync(client, ct);

        HttpResponseMessage resposta = await client.GetAsync(location, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement json = await resposta.Content.ReadFromJsonAsync<JsonElement>(ct);
        json.GetProperty("tenantId").GetGuid().Should().Be(id);
        json.GetProperty("status").GetString().Should().Be("Pending");
        json.GetProperty("registeredAt").GetDateTimeOffset().Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        json.TryGetProperty("externalOrganizationId", out _).Should().BeFalse();
    }

    [Fact]
    public async Task TenantEmProvisioningFailed_Responde200ComOStatus()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");
        (Guid id, _) = await RegistrarAsync(client, ct);
        TenantId alvo = new(id);
        await factory.ComEscopoAsync(async contexto =>
        {
            Tenant tenant = await contexto.Tenants.SingleAsync(item => item.Id == alvo, ct);
            tenant.MarkProvisioningFailed();
            await contexto.SaveChangesAsync(ct);
        });

        JsonElement json = await client.GetFromJsonAsync<JsonElement>(Rota(id), ct);

        json.GetProperty("status").GetString().Should().Be("ProvisioningFailed");
    }

    [Fact]
    public async Task IdEmMaiusculas_EOMesmoTenant()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");
        (Guid id, _) = await RegistrarAsync(client, ct);

        HttpResponseMessage resposta = await client.GetAsync(
            $"/api/v1/tenants/{id.ToString().ToUpperInvariant()}/provisioning", ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TenantInexistente_Responde404ComProblemDetails()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");

        HttpResponseMessage resposta = await client.GetAsync(Rota(Guid.NewGuid()), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        resposta.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task IdMalFormado_Responde404()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");

        HttpResponseMessage resposta = await client.GetAsync("/api/v1/tenants/nao-e-guid/provisioning", ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SemToken_Responde401()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage resposta = await client.GetAsync(Rota(Guid.NewGuid()), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TokenSemOPapel_Responde403()
    {
        // Prova que a policy está ligada nesta rota: sem ele, esquecer o RequireAuthorization não quebraria teste.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado();

        HttpResponseMessage resposta = await client.GetAsync(Rota(Guid.NewGuid()), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
