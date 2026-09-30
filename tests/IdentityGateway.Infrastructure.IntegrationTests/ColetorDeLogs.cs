using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace IdentityGateway.Infrastructure.IntegrationTests;

/// <summary>
/// Provider de log que guarda tudo, de todas as categorias: mensagem formatada, valores estruturados, escopos e
/// exceção.
/// </summary>
/// <remarks>
/// Guarda os escopos também: o <c>LogicalHandler</c> do <c>HttpClient</c> abre um escopo com a URI, e um vazamento
/// ali não apareceria na mensagem.
/// </remarks>
internal sealed class ColetorDeLogs : ILoggerProvider
{
    public ConcurrentQueue<RegistroDeLog> Registros { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Registros);

    public void Dispose()
    {
    }

    private sealed class Logger(string categoria, ConcurrentQueue<RegistroDeLog> destino) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            destino.Enqueue(new RegistroDeLog(categoria, LogLevel.None, $"[escopo] {Valores(state)} {state}"));
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            destino.Enqueue(new RegistroDeLog(
                categoria, logLevel, $"{formatter(state, exception)} {Valores(state)} {exception}"));
        }

        private static string Valores<TState>(TState state) =>
            state is IEnumerable<KeyValuePair<string, object?>> pares
                ? string.Join(" ", pares.Select(par => $"{par.Key}={par.Value}"))
                : string.Empty;
    }
}

/// <summary>Um registro capturado: categoria, nível e todo o texto que um sink poderia gravar.</summary>
internal sealed record RegistroDeLog(string Categoria, LogLevel Nivel, string Texto);
