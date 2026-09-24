using System.Reflection;
using NetArchTest.Rules;

using ArchTestResult = NetArchTest.Rules.TestResult;

namespace IdentityGateway.ArchitectureTests;

/// <summary>
/// Nenhum tipo do Keycloak atravessa a fronteira de <c>Infrastructure.Identity.Keycloak</c> (ADR-008).
/// </summary>
public sealed class RegrasDoKeycloakTests
{
    private const string NamespaceKeycloak = "IdentityGateway.Infrastructure.Identity.Keycloak";

    private static readonly Assembly Application = typeof(IdentityGateway.Application.AssemblyMarker).Assembly;
    private static readonly Assembly Infrastructure = typeof(IdentityGateway.Infrastructure.AssemblyMarker).Assembly;
    private static readonly Assembly Api = typeof(IdentityGateway.Api.AssemblyMarker).Assembly;

    [Fact]
    public void ONamespaceDoKeycloakTemTipos()
    {
        // Sem isto, as regras abaixo passariam vazias se o namespace fosse renomeado: "nenhum tipo depende de X" é
        // verdade trivial quando X não existe.
        Types.InAssembly(Infrastructure).That().ResideInNamespace(NamespaceKeycloak).GetTypes()
            .Should().NotBeEmpty();
    }

    [Fact]
    public void AApiNaoConheceOKeycloak()
    {
        ArchTestResult resultado = Types.InAssembly(Api)
            .Should().NotHaveDependencyOn(NamespaceKeycloak)
            .GetResult();

        resultado.Should().NaoTerViolacao("a Api fala com a porta IIdentityProvider, nunca com o adaptador");
    }

    [Fact]
    public void AApplicationNaoConheceOKeycloak()
    {
        ArchTestResult resultado = Types.InAssembly(Application)
            .Should().NotHaveDependencyOn(NamespaceKeycloak)
            .GetResult();

        resultado.Should().NaoTerViolacao("a Application declara a porta; o Keycloak é detalhe de quem a implementa");
    }

    [Fact]
    public void ORestoDaInfrastructureSoAlcancaOKeycloakPeloRegistro()
    {
        ArchTestResult resultado = Types.InAssembly(Infrastructure)
            .That().DoNotResideInNamespace(NamespaceKeycloak)
            .And().DoNotHaveName("DependencyInjection")
            .Should().NotHaveDependencyOn(NamespaceKeycloak)
            .GetResult();

        resultado.Should().NaoTerViolacao(
            "a troca de provedor de identidade precisa ficar contida em Identity/Keycloak; o único ponto de contato "
            + "é o registro em DependencyInjection");
    }
}
