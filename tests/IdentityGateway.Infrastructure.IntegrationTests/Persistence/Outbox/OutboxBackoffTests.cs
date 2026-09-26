using IdentityGateway.Infrastructure.Configuration;
using IdentityGateway.Infrastructure.Persistence.Outbox;

namespace IdentityGateway.Infrastructure.IntegrationTests.Persistence.Outbox;

/// <summary>
/// A fórmula do atraso entre tentativas, compartilhada pelo processador e pela validação da subida.
/// </summary>
public sealed class OutboxBackoffTests
{
    private static readonly OutboxOptions Padroes = new();

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(4, 60)]
    [InlineData(1500, 60)]
    public void AtrasoSemVariacao_DobraAteOTeto(int tentativa, int segundos)
    {
        // A tentativa 1500 é o caso que importa: 2^1499 estoura o double para infinito, e o teto precisa continuar
        // valendo em vez de o atraso virar infinito ou NaN.
        OutboxBackoff.AtrasoSemVariacao(tentativa, Padroes).Should().Be(TimeSpan.FromSeconds(segundos));
    }

    [Fact]
    public void CoberturaMinima_ComOsPadroes_CobreAJanelaDe24hComFolga()
    {
        TimeSpan cobertura = OutboxBackoff.CoberturaMinima(Padroes);

        cobertura.Should().BeGreaterThan(TimeSpan.FromHours(24));
        cobertura.Should().BeLessThan(TimeSpan.FromHours(26));
    }

    [Fact]
    public void CoberturaMinima_ContaAsEsperasEntreTentativas()
    {
        // Cinco tentativas têm quatro esperas: 10 + 20 + 40 + 80. A quinta tentativa é a última — não há espera
        // depois dela.
        OutboxOptions antigos = new() { MaxAttempts = 5, MaxRetryDelaySeconds = 300 };

        OutboxBackoff.CoberturaMinima(antigos).Should().Be(TimeSpan.FromSeconds(150));
    }
}
