using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using IdentityGateway.Api.FunctionalTests.Logs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Serilog.Events;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// O aviso de "chave de assinatura não encontrada", visto no log de um host de verdade.
/// </summary>
/// <remarks>
/// Um host derivado, com o próprio canal de log e o próprio limitador do aviso: a contagem não pode depender do que as
/// outras classes — que rodam em paralelo, cada uma com o seu host — já registraram.
/// </remarks>
public sealed class AvisoDeChaveNoLogTests(IdentityGatewayApiFactory factory) : IClassFixture<IdentityGatewayApiFactory>
{
    private static readonly string[] PlatformAdmin = ["platform-admin"];

    private static bool EhOAvisoDeChave(LogEvent evento) =>
        evento.Level == LogEventLevel.Warning
        && evento.MessageTemplate.Text.Contains("chave de assinatura não encontrada", StringComparison.Ordinal);

    private static async Task<HttpStatusCode> RegistrarComAsync(HttpClient client, string token, CancellationToken ct)
    {
        using HttpRequestMessage pedido = new(HttpMethod.Post, "/api/v1/tenants");
        pedido.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage resposta = await client.SendAsync(pedido, ct);

        return resposta.StatusCode;
    }

    [Fact]
    public async Task TokenDeKidDesconhecido_Responde401EAvisaUmaVezSo_ERecusaComumNaoAvisa()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string canal = Guid.NewGuid().ToString("N");
        using WebApplicationFactory<Program> api = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Serilog:WriteTo:9:Args:canal", canal));
        using HttpClient client = api.CreateClient();
        var logs = ColetorDeLogsDaApi.DoCanal(canal);
        using var forasteira = RSA.Create(2048);

        // Uma recusa comum — audiência errada, assinatura certa — não é falta de chave, e não avisa.
        string comAudienciaErrada = factory.Emissor.Emitir(
            roles: PlatformAdmin, ajustar: payload => payload["aud"] = "account");
        (await RegistrarComAsync(client, comAudienciaErrada, ct)).Should().Be(HttpStatusCode.Unauthorized);
        logs.Eventos.Should().NotContain(evento => EhOAvisoDeChave(evento));

        // Três tokens de quem não tem a chave do realm, cada um com um kid inventado: qualquer pessoa consegue mandar
        // isto, sem se autenticar. 401 nos três, e um aviso só.
        for (int i = 0; i < 3; i++)
        {
            string forjado = factory.Emissor.Assinar(
                factory.Emissor.Payload(roles: PlatformAdmin), forasteira, kid: $"kid-inventado-{i}");

            (await RegistrarComAsync(client, forjado, ct)).Should().Be(HttpStatusCode.Unauthorized);
        }

        logs.Eventos.Count(EhOAvisoDeChave).Should().Be(1, "o aviso é limitado a um por intervalo");
    }
}
