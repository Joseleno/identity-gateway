using System.Text.Json;

namespace IdentityGateway.ArchitectureTests;

/// <summary>
/// O ambiente de desenvolvimento (IDE e compose) não expõe o e-mail do admin, e o compose é coerente consigo mesmo.
/// </summary>
/// <remarks>
/// Lê os arquivos, como <c>RegrasDoRealmTests</c> lê o realm: são configuração versionada, e um valor errado neles
/// não quebra build nem teste de comportamento — só aparece num log de desenvolvimento, que é público na prática.
/// </remarks>
public sealed class RegrasDoAmbienteLocalTests
{
    [Fact]
    public void Development_NaoRegistraDadosSensiveisDoEf()
    {
        // D15, "inclusive em Development": com o log de dados sensíveis ligado, o EF registra o parâmetro do INSERT do
        // e-mail e, no DetectChanges, o valor antigo da coluna ao apagá-la.
        using var desenvolvimento = JsonDocument.Parse(File.ReadAllText(
            RaizDoRepositorio.Caminho("src", "IdentityGateway.Api", "appsettings.Development.json")));
        JsonElement raiz = desenvolvimento.RootElement;

        raiz.GetProperty("Database").GetProperty("EnableSensitiveDataLogging").GetBoolean().Should().BeFalse();
        raiz.GetProperty("Serilog").GetProperty("MinimumLevel").GetProperty("Override")
            .GetProperty("Microsoft.EntityFrameworkCore").GetString().Should().Be("Warning");
    }
}
