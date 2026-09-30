using Microsoft.Extensions.Logging;

namespace IdentityGateway.Application.UnitTests;

/// <summary>
/// Logger que guarda tudo o que recebe: a mensagem formatada, os valores estruturados e a exceção.
/// </summary>
/// <remarks>
/// Para provar o que um log NÃO contém (D15), o teste precisa ver o que o sink veria — o texto e os argumentos
/// separados, que o Seq indexa um a um.
/// </remarks>
internal sealed class ColetorDeLogs<T> : ILogger<T>
{
    public List<(LogLevel Nivel, EventId Evento, string Texto)> Registros { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        string valores = state is IEnumerable<KeyValuePair<string, object?>> pares
            ? string.Join(" ", pares.Select(par => $"{par.Key}={par.Value}"))
            : string.Empty;

        Registros.Add((logLevel, eventId, $"{formatter(state, exception)} {valores} {exception}"));
    }
}
