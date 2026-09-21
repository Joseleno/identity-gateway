using System.Reflection;
using IdentityGateway.Domain.Common;

namespace IdentityGateway.ArchitectureTests;

/// <summary>
/// Trava a regra que o <c>OutboxEventTypes</c> documenta: todo domain event precisa estar no mapa.
/// </summary>
/// <remarks>
/// <para>
/// <b>Esta regra existia como promessa antes de existir como teste.</b> O XML doc do
/// <c>OutboxEventTypes</c> afirmava "há teste de arquitetura exigindo que todo <c>IDomainEvent</c> esteja
/// aqui" — e não havia: a regra ficou no template de origem e não veio junto. Documentação que promete
/// uma verificação inexistente é pior que documentação ausente, porque quem lê para de conferir.
/// </para>
/// <para>
/// O custo de não ter a regra: <c>NomeDe</c> lança para evento não registrado, então o esquecimento não
/// passa em silêncio — mas é descoberto na gravação, em tempo de execução, e não no build de quem
/// escreveu o evento.
/// </para>
/// </remarks>
public sealed class RegrasDoOutboxTests
{
    private static readonly Assembly Domain = typeof(IDomainEvent).Assembly;
    private static readonly Assembly Infrastructure =
        typeof(IdentityGateway.Infrastructure.AssemblyMarker).Assembly;

    [Fact]
    public void TodoDomainEvent_EstaRegistradoNoMapaDoOutbox()
    {
        List<Type> eventos =
        [
            .. Domain.GetTypes()
                .Where(tipo => typeof(IDomainEvent).IsAssignableFrom(tipo))
                .Where(tipo => tipo is { IsInterface: false, IsAbstract: false }),
        ];

        // Guarda contra o teste virar vacuidade: sem nenhum evento no domínio, a regra passaria sem
        // verificar nada, e o primeiro evento escrito entraria sem ninguém cobrar o registro.
        eventos.Should().NotBeEmpty("o teste precisa de domain events para inspecionar");

        IReadOnlyCollection<Type> registrados = EventosRegistrados();

        List<string> ausentes =
        [
            .. eventos
                .Where(evento => !registrados.Contains(evento))
                .Select(evento => evento.Name),
        ];

        ausentes.Should().BeEmpty(
            "todo domain event precisa de um nome curto em OutboxEventTypes.PorTipo, senão a gravação da "
            + "mensagem falha em tempo de execução. Eventos sem registro: " + string.Join(", ", ausentes));
    }

    /// <summary>
    /// Lê <c>OutboxEventTypes.Registrados</c> por reflexão.
    /// </summary>
    /// <remarks>
    /// O tipo é <c>internal</c> à Infrastructure, e a alternativa seria abrir <c>InternalsVisibleTo</c>
    /// para este projeto. Reflexão aqui é o custo menor: a regra é do teste de arquitetura, e alargar a
    /// visibilidade do assembly para satisfazê-la afrouxaria a fronteira que os outros testes protegem.
    /// </remarks>
    private static IReadOnlyCollection<Type> EventosRegistrados()
    {
        Type? mapa = Infrastructure.GetType(
            "IdentityGateway.Infrastructure.Persistence.Outbox.OutboxEventTypes");

        mapa.Should().NotBeNull("OutboxEventTypes precisa existir para a regra fazer sentido");

        PropertyInfo? registrados = mapa!.GetProperty(
            "Registrados",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        registrados.Should().NotBeNull("OutboxEventTypes.Registrados é o que expõe o mapa para esta regra");

        return (IReadOnlyCollection<Type>)registrados!.GetValue(null)!;
    }
}
