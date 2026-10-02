using System.Collections.Concurrent;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace IdentityGateway.Api.FunctionalTests.Logs;

/// <summary>
/// Um sink do Serilog em memória: o que a Api registraria, visto pelo teste.
/// </summary>
/// <remarks>
/// <para>
/// <b>Entra por configuração</b> (<c>Serilog:Using</c> e <c>Serilog:WriteTo</c>), pelo mesmo
/// <c>ReadFrom.Configuration</c> do <c>Program.cs</c>: a Api usa o Serilog no lugar dos provedores de log do host, e um
/// <c>ILoggerProvider</c> acrescentado pelo teste seria ignorado.
/// </para>
/// <para>
/// <b>Um canal por factory.</b> As classes de teste rodam em paralelo, cada uma com o seu host; o canal — um texto
/// aleatório passado na configuração — separa os eventos de cada um.
/// </para>
/// </remarks>
public sealed class ColetorDeLogsDaApi : ILogEventSink
{
    private static readonly ConcurrentDictionary<string, ColetorDeLogsDaApi> Canais = new(StringComparer.Ordinal);

    private readonly ConcurrentQueue<LogEvent> _eventos = new();

    public static ColetorDeLogsDaApi DoCanal(string canal) => Canais.GetOrAdd(canal, _ => new ColetorDeLogsDaApi());

    /// <summary>Os eventos registrados até agora, na ordem.</summary>
    public IReadOnlyList<LogEvent> Eventos => [.. _eventos];

    /// <summary>Cada evento como texto: a mensagem renderizada, as propriedades e a exceção inteira.</summary>
    /// <remarks>É onde um teste de vazamento procura: um segredo pode estar na mensagem, numa propriedade ou na exceção.</remarks>
    public IReadOnlyList<string> Textos =>
    [
        .. _eventos.Select(evento =>
            $"{evento.Level} {evento.RenderMessage(System.Globalization.CultureInfo.InvariantCulture)} "
            + $"{string.Join(' ', evento.Properties.Select(par => $"{par.Key}={par.Value}"))} {evento.Exception}"),
    ];

    public void Emit(LogEvent logEvent) => _eventos.Enqueue(logEvent);
}

/// <summary>O método que o <c>ReadFrom.Configuration</c> do Serilog acha pelo nome em <c>Serilog:WriteTo</c>.</summary>
public static class ColetorDeLogsDaApiExtensions
{
    public static LoggerConfiguration ColetorEmMemoria(this LoggerSinkConfiguration sinks, string canal)
    {
        ArgumentNullException.ThrowIfNull(sinks);

        return sinks.Sink(ColetorDeLogsDaApi.DoCanal(canal));
    }
}
