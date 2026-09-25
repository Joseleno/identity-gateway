using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O ready do Keycloak cai para Unhealthy — nunca Degraded — e não pendura a sonda.
/// </summary>
/// <remarks>
/// O caso saudável, contra o Keycloak real, está em <c>KeycloakRealTests</c>.
/// </remarks>
public sealed class KeycloakHealthCheckTests
{
    /// <summary>
    /// A composição real (<c>AddInfrastructure</c>) apontada para um Keycloak. A <c>KeycloakFixture</c> (Task 9) usa a
    /// mesma, acrescentando handlers de teste antes de construir.
    /// </summary>
    internal static ServiceCollection ColecaoDaComposicao(string baseUrl, string? pem = null)
    {
        IConfiguration configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = "Host=localhost;Database=x;Username=u;Password=p",
                ["Jwt:Issuer"] = "identitygateway",
                ["Jwt:Audience"] = "identitygateway-api",
                ["Jwt:SigningKey"] = new string('k', 32),
                ["Keycloak:Admin:BaseUrl"] = baseUrl,
                ["Keycloak:Admin:Realm"] = "identity-gateway",
                ["Keycloak:Admin:ClientId"] = "identity-gateway",
                ["Keycloak:Admin:PrivateKeyPem"] = pem ?? ChavesDeTeste.Gerar().PemPrivado,
                ["Keycloak:Admin:AllowInsecureHttp"] = "true",
                ["HttpResilience:MaxRetryAttempts"] = "1",
            })
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddInfrastructure(configuracao);

        return services;
    }

    internal static ServiceProvider Composicao(string baseUrl, string? pem = null) =>
        ColecaoDaComposicao(baseUrl, pem).BuildServiceProvider(validateScopes: true);

    private static async Task<HealthReportEntry> Checar(ServiceProvider provider, CancellationToken ct)
    {
        HealthReport relatorio = await provider.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(registro => registro.Name == "keycloak", ct);

        return relatorio.Entries["keycloak"];
    }

    [Fact]
    public async Task KeycloakInalcancavel_Unhealthy()
    {
        // Degraded responderia 200 no /health/ready, e a instância nunca sairia do balanceador.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = Composicao("http://127.0.0.1:9");

        HealthReportEntry entrada = await Checar(provider, ct);

        entrada.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task KeycloakQueAceitaENaoResponde_UnhealthyDentroDoTimeoutDoCheck()
    {
        // Um listener que aceita a conexão e nunca responde: sem timeout, a sonda penduraria pelos 100s padrão do
        // HttpClient, e o orquestrador trataria a instância como travada.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using TcpListener buracoNegro = new(IPAddress.Loopback, 0);
        buracoNegro.Start();
        int porta = ((IPEndPoint)buracoNegro.LocalEndpoint).Port;

        await using ServiceProvider provider = Composicao($"http://127.0.0.1:{porta}");

        var cronometro = Stopwatch.StartNew();
        HealthReportEntry entrada = await Checar(provider, ct);
        cronometro.Stop();

        entrada.Status.Should().Be(HealthStatus.Unhealthy);
        cronometro.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8));
    }
}
