using IdentityGateway.Infrastructure.Configuration;

namespace IdentityGateway.Infrastructure.Persistence.Outbox;

/// <summary>
/// A fórmula do atraso entre tentativas do Outbox.
/// </summary>
/// <remarks>
/// Uma cópia só: o <see cref="OutboxProcessor"/> a usa para agendar a próxima tentativa, e a validação da subida a
/// usa para saber quanto tempo o Outbox insiste. Duas cópias da fórmula derivariam, e a validação passaria a
/// garantir uma cobertura que o processador não entrega.
/// </remarks>
internal static class OutboxBackoff
{
    /// <summary>
    /// Atraso depois da tentativa informada, sem a variação aleatória: <c>base * 2^(tentativa-1)</c>, limitado ao teto.
    /// </summary>
    /// <remarks>
    /// Com muitas tentativas a potência estoura o <c>double</c> para infinito; o <c>Math.Min</c> com o teto continua
    /// correto nesse caso, e o teste cobre a tentativa 1500.
    /// </remarks>
    internal static TimeSpan AtrasoSemVariacao(int tentativa, OutboxOptions opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);

        double dobrado = opcoes.BaseRetryDelaySeconds * Math.Pow(2, Math.Max(0, tentativa - 1));

        return TimeSpan.FromSeconds(Math.Min(dobrado, opcoes.MaxRetryDelaySeconds));
    }

    /// <summary>
    /// Tempo mínimo entre a primeira e a última tentativa de uma mensagem.
    /// </summary>
    /// <remarks>
    /// Soma as <c>MaxAttempts - 1</c> esperas sem a variação — que só alonga —, então é um piso: na prática o Outbox
    /// insiste um pouco mais que isto, nunca menos.
    /// </remarks>
    internal static TimeSpan CoberturaMinima(OutboxOptions opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);

        TimeSpan total = TimeSpan.Zero;

        for (int tentativa = 1; tentativa < opcoes.MaxAttempts; tentativa++)
        {
            total += AtrasoSemVariacao(tentativa, opcoes);
        }

        return total;
    }
}
