using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// Exercita <c>POST /api/v1/tenants</c> por HTTP, do JSON até a linha no banco.
/// </summary>
/// <remarks>
/// Entra pela porta da frente e atravessa tudo — binding, validação, pipeline, persistência e tradução de erro
/// —, que é o que distingue o teste funcional dos das camadas de baixo.
/// </remarks>
public sealed class RegistroDeTenantTests(IdentityGatewayApiFactory factory)
    : IClassFixture<IdentityGatewayApiFactory>
{
    private const string Rota = "/api/v1/tenants";

    private static object Corpo(string slug, string plano = "free") => new
    {
        name = "Acme Corp",
        slug,
        planCode = plano,
        initialAdminEmail = "admin@acme.com",
    };

    private static string SlugUnico() => $"acme-{Guid.NewGuid():N}"[..20];

    [Fact]
    public async Task ComandoValido_Responde202ComLocationECorpo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");
        string slug = SlugUnico();

        HttpResponseMessage resposta = await client.PostAsJsonAsync(Rota, Corpo(slug), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Accepted);

        JsonElement json = await resposta.Content.ReadFromJsonAsync<JsonElement>(ct);
        string id = json.GetProperty("tenantId").GetString()!;
        json.GetProperty("status").GetString().Should().Be("Pending");

        resposta.Headers.Location!.ToString()
            .Should().Be($"/api/v1/tenants/{id}/provisioning");
    }

    [Fact]
    public async Task ComandoValido_GravaOTenantEAMensagemDoOutbox()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");
        string slug = SlugUnico();

        await client.PostAsJsonAsync(Rota, Corpo(slug), ct);

        // Comparação pelo TenantSlug inteiro, não por .Value: o conversor do EF opera sobre a propriedade
        // completa, e tenant.Slug.Value acessa um membro do lado CLR depois da conversão — o provider não
        // sabe traduzir isso e lança em tempo de execução, não de compilação.
        TenantSlug slugAlvo = TenantSlug.Create(slug).Value;

        await factory.ComEscopoAsync(async contexto =>
        {
            bool gravado = await contexto.Tenants.AnyAsync(tenant => tenant.Slug == slugAlvo, ct);
            gravado.Should().BeTrue();

            // O filtro por Type roda no servidor; o de Content roda em memória, depois do ToListAsync. O
            // Postgres mapeia Content como jsonb, e não expõe LIKE (~~) entre dois jsonb — nem via
            // EF.Functions.Like, que ainda assim tenta invocar like_escape(jsonb, unknown), inexistente.
            // Contains em memória evita depender de conversão de tipo na tradução da query.
            List<string> conteudos = await contexto.OutboxMessages
                .Where(m => m.Type == "tenant-registered")
                .Select(m => m.Content)
                .ToListAsync(ct);
            bool mensagem = conteudos.Any(conteudo => conteudo.Contains(slug, StringComparison.Ordinal));
            mensagem.Should().BeTrue();
        });
    }

    [Fact]
    public async Task SemToken_Responde401()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage resposta = await client.PostAsJsonAsync(Rota, Corpo(SlugUnico()), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TokenSemOPapel_Responde403()
    {
        // O teste que prova que a policy esta ligada. Sem ele, remover o RequireAuthorization do endpoint nao
        // quebraria teste nenhum: o cliente autenticado passaria igual.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado();

        HttpResponseMessage resposta = await client.PostAsJsonAsync(Rota, Corpo(SlugUnico()), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SlugMalFormado_Responde400()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");

        HttpResponseMessage resposta = await client.PostAsJsonAsync(Rota, Corpo("-invalido-"), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SlugDuplicado_Responde409()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");
        string slug = SlugUnico();

        await client.PostAsJsonAsync(Rota, Corpo(slug), ct);
        HttpResponseMessage segunda = await client.PostAsJsonAsync(Rota, Corpo(slug), ct);

        segunda.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PlanoDesconhecido_Responde400()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");

        HttpResponseMessage resposta = await client.PostAsJsonAsync(
            Rota, Corpo(SlugUnico(), plano: "inexistente"), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
