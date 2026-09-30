using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace IdentityGateway.Infrastructure.IntegrationTests;

/// <summary>
/// O <see cref="IHostEnvironment"/> que o host registraria, para as composições montadas à mão.
/// </summary>
/// <remarks>
/// A validação das options do Keycloak recusa <c>AllowInsecureHttp</c> fora de <c>Development</c> e depende do
/// ambiente pelo contêiner. Sem host, ninguém o registra, e a validação falha fechada — é o comportamento certo em
/// produção, e o motivo deste helper existir nos testes.
/// </remarks>
internal static class AmbienteDeTeste
{
    public static IServiceCollection ComAmbiente(this IServiceCollection services, string nome = "Development")
    {
        IHostEnvironment ambiente = Substitute.For<IHostEnvironment>();
        ambiente.EnvironmentName.Returns(nome);

        return services.AddSingleton(ambiente);
    }
}
