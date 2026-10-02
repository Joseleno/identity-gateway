using IdentityGateway.Api.FunctionalTests.Logs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// O coletor de logs de cada host só vê o que aquele host registrou.
/// </summary>
/// <remarks>
/// É a premissa de todo teste que afirma algo sobre log — "o aviso saiu", "o token não está no log". As classes de
/// teste rodam em paralelo, cada uma com o seu host; se os hosts dividissem um logger, a ausência de um texto no
/// coletor não provaria nada, e a presença poderia ser de outro teste.
/// </remarks>
public sealed class LogsPorHostTests(IdentityGatewayApiFactory factory) : IClassFixture<IdentityGatewayApiFactory>
{
    [Fact]
    public async Task CadaHost_SoVeOsPropriosLogs()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string outroCanal = Guid.NewGuid().ToString("N");
        using WebApplicationFactory<Program> outro = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Serilog:WriteTo:9:Args:canal", outroCanal));
        using HttpClient daFactory = factory.CreateClientAutenticado();
        using HttpClient doOutro = outro.CreateClient();
        Uri rotaDaFactory = new($"/api/v1/tenants/{Guid.NewGuid()}/provisioning", UriKind.Relative);
        Uri rotaDoOutro = new($"/api/v1/tenants/{Guid.NewGuid()}/provisioning", UriKind.Relative);

        // Intercalados, e o segundo host construído por último: com o logger estático do processo, tudo iria para ele.
        await daFactory.GetAsync(rotaDaFactory, ct);
        await doOutro.GetAsync(rotaDoOutro, ct);
        await daFactory.GetAsync(rotaDaFactory, ct);

        IReadOnlyList<string> logsDaFactory = factory.Logs.Textos;
        IReadOnlyList<string> logsDoOutro = ColetorDeLogsDaApi.DoCanal(outroCanal).Textos;

        logsDaFactory.Should().Contain(texto => texto.Contains(rotaDaFactory.OriginalString, StringComparison.Ordinal));
        logsDaFactory.Should().NotContain(texto => texto.Contains(rotaDoOutro.OriginalString, StringComparison.Ordinal));
        logsDoOutro.Should().Contain(texto => texto.Contains(rotaDoOutro.OriginalString, StringComparison.Ordinal));
        logsDoOutro.Should().NotContain(texto => texto.Contains(rotaDaFactory.OriginalString, StringComparison.Ordinal));
    }
}
