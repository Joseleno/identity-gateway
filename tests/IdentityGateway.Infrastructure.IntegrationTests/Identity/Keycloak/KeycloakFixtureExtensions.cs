using IdentityGateway.Domain.Tenants;
using Microsoft.Extensions.DependencyInjection;

// Um Keycloak para o assembly inteiro. O atributo fica aqui, e não na biblioteca do fixture: é este projeto que é um
// projeto de teste.
[assembly: AssemblyFixture(typeof(KeycloakFixture))]

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O que o fixture do Keycloak precisa saber da Gateway, e por isso não mora na biblioteca dele.
/// </summary>
/// <remarks>
/// Membros de extensão do C# 14: <c>KeycloakFixture.SlugUnico()</c> e <c>keycloak.CriarProvider(...)</c> continuam
/// sendo chamados como antes da mudança de projeto.
/// </remarks>
internal static class KeycloakFixtureExtensions
{
    extension(KeycloakFixture)
    {
        /// <summary>Slug aleatório, válido e curto — o isolamento entre testes.</summary>
        public static TenantSlug SlugUnico() => TenantSlug.Create($"t-{Guid.NewGuid():N}"[..18]).Value;
    }

    extension(KeycloakFixture keycloak)
    {
        /// <summary>
        /// A composição real (<c>AddInfrastructure</c>) apontada para este Keycloak, com handlers de teste opcionais
        /// acrescentados antes de construir.
        /// </summary>
        public ServiceProvider CriarProvider(Action<IServiceCollection>? ajustar = null, string? pem = null)
        {
            ServiceCollection services = KeycloakHealthCheckTests.ColecaoDaComposicao(
                keycloak.BaseUrl, pem ?? keycloak.Chaves.PemPrivado, KeycloakFixture.HostnamePublico);
            ajustar?.Invoke(services);

            return services.BuildServiceProvider(validateScopes: true);
        }
    }
}
