using System.Net.Http.Headers;
using IdentityGateway.Infrastructure.Persistence;
using IdentityGateway.Testing.Keycloak;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// A Api inteira contra um Keycloak 26.7.4 de verdade, com o realm do repositório.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sem chave de teste.</b> A <c>IdentityGatewayApiFactory</c> valida tokens assinados por uma chave que o próprio
/// teste gerou. Aqui a Api busca os metadados e as chaves no Keycloak, e os tokens vêm do device flow — o caminho de
/// quem usa a API de fato.
/// </para>
/// <para>
/// <b>Uma por coleção</b>, porque sobe três containers (PostgreSQL, Redis e Keycloak, com o mailpit dele). As classes
/// que a usam ficam na coleção <see cref="ColecaoComKeycloak"/> e rodam em série.
/// </para>
/// <para>
/// <b>A lista de clients aceitos ganha o client de device flow do fixture.</b> É de propósito: o token dele tem o
/// <c>azp</c> aceito e <b>não</b> tem a audiência da Gateway, e por isso o <c>401</c> que ele recebe só pode vir da
/// audiência.
/// </para>
/// </remarks>
public sealed class ApiComKeycloakFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("identitygateway_keycloak")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

    /// <summary>O Keycloak desta coleção.</summary>
    public KeycloakFixture Keycloak { get; } = new();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), Keycloak.InitializeAsync().AsTask());

        using IServiceScope scope = Services.CreateScope();
        AppDbContext contexto = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await contexto.Database.MigrateAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Task.WhenAll(
            _postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask(), Keycloak.DisposeAsync().AsTask());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        builder.UseSetting("Database:ConnectionString", _postgres.GetConnectionString());
        builder.UseSetting("Redis:ConnectionString", _redis.GetConnectionString());

        // Desligado na D1: nenhum teste daqui espera o provisionamento. A D2 o liga, para o admin convidado existir.
        builder.UseSetting("Outbox:Enabled", "false");

        // Os mesmos três valores do compose: transporte pela porta mapeada, emissor público pelo KC_HOSTNAME do
        // fixture, e a chave que o realm registrou.
        builder.UseSetting("Keycloak:Admin:BaseUrl", Keycloak.BaseUrl);
        builder.UseSetting("Keycloak:Admin:PublicBaseUrl", KeycloakFixture.HostnamePublico);
        builder.UseSetting("Keycloak:Admin:PrivateKeyPem", Keycloak.Chaves.PemPrivado);

        // O índice 0 é o identity-gateway-demo, do appsettings.Development.json.
        builder.UseSetting("Keycloak:Auth:AllowedClients:1", KeycloakFixture.ClientDeConta);
    }

    /// <summary>
    /// Um cliente HTTP da Api autenticado como o usuário, com um token obtido pelo device flow no client de
    /// demonstração.
    /// </summary>
    public async Task<HttpClient> CriarClienteComoAsync(UsuarioDeTeste usuario, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(usuario);

        using HarnessDeLogin harness = Keycloak.CriarHarness();
        TokensDeUsuario tokens = await harness.TokenPorDispositivoAsync(usuario.Email, usuario.Senha, cancellationToken);

        HttpClient cliente = CreateClient();
        cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        return cliente;
    }
}
