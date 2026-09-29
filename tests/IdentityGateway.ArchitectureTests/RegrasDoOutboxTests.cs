using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    // As mesmas opcoes que o DomainEventInterceptor grava e o OutboxProcessor le. Opcoes diferentes nas
    // duas pontas produziriam um JSON que se grava e nao se le de volta — e este teste nao veria.
    private static readonly JsonSerializerOptions OpcoesDoOutbox = new(JsonSerializerDefaults.Web);

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

    [Fact]
    public void TodoEventoRegistrado_SobreviveAoRoundTripDoOutbox()
    {
        // Estar no mapa não basta: o despachante relê a mensagem com
        // JsonSerializer.Deserialize(content, tipo, ...), e um evento que carregue tipo sem construtor
        // público — um value object de domínio, tipicamente — serializa bem e lança ao voltar. O sintoma
        // seria uma fila inteira indo a dead-letter depois de esgotar as tentativas, com o build verde.
        //
        // Aconteceu de verdade: TenantRegistered nasceu carregando TenantSlug, e nenhuma review de task
        // isolada podia ver — o evento é de uma task, o registro no mapa é de outra, e o despachante é
        // código de infraestrutura que nenhuma das duas tocou.
        // A verificação é sobre a FORMA do contrato, não sobre uma instância: instanciar o evento com
        // valores default passaria `null` no campo problemático, e desserializar `null` nunca constrói o
        // tipo — o teste passaria com o defeito presente. Verifiquei isso na prática antes de escrever
        // assim.
        List<string> quebrados = [];

        foreach (Type evento in EventosRegistrados())
        {
            foreach (PropertyInfo campo in evento.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (DesserializavelPorSiSo(campo.PropertyType))
                {
                    continue;
                }

                quebrados.Add($"{evento.Name}.{campo.Name} ({campo.PropertyType.Name})");
            }
        }

        quebrados.Should().BeEmpty(
            "todo campo de evento no mapa do Outbox precisa voltar do JSON, senão a mensagem é gravada e "
            + "nunca despachada — ela esgota as tentativas e vai para dead-letter. Payload de evento é "
            + "contrato de fio: use primitivo, não value object com construtor privado. Campos que não "
            + "voltam: " + string.Join(" | ", quebrados));
    }

    [Fact]
    public void TodoEventoRegistrado_TemOccurredOnRestauravel()
    {
        // O OutboxProcessor relê o evento com JsonSerializer.Deserialize, e o System.Text.Json não atribui
        // propriedade só com get: o OccurredOn voltava com o instante da desserialização, não o da ocorrência. Nada
        // o lia quando o defeito foi achado (fatia B), e por isso mesmo nenhum teste de comportamento o pegaria — o
        // primeiro consumidor a ler receberia um dado falso sem erro nenhum. A regra olha a forma do contrato.
        var semSetter = EventosRegistrados()
            .Where(evento => evento.GetProperty(nameof(IDomainEvent.OccurredOn))?.SetMethod is null)
            .Select(evento => evento.Name)
            .ToList();

        semSetter.Should().BeEmpty(
            "o OccurredOn precisa de setter (init basta) para voltar do JSON com o valor gravado. Sem setter: "
            + string.Join(" | ", semSetter));
    }

    /// <summary>
    /// Diz se o <c>System.Text.Json</c> consegue reconstruir o tipo sozinho.
    /// </summary>
    /// <remarks>
    /// O critério é o do próprio serializador: primitivo, tipo que ele conhece, ou tipo com construtor
    /// sem parâmetro, com um único construtor parametrizado, ou com <c>[JsonConstructor]</c>. Um
    /// <c>sealed class</c> de domínio com construtor privado — o caso do value object — não atende a
    /// nenhum dos três e lança <c>NotSupportedException</c> na volta.
    /// </remarks>
    private static bool DesserializavelPorSiSo(Type tipo)
    {
        Type efetivo = Nullable.GetUnderlyingType(tipo) ?? tipo;

        if (efetivo.IsPrimitive || efetivo.IsEnum || efetivo == typeof(string)
            || efetivo == typeof(Guid) || efetivo == typeof(decimal)
            || efetivo == typeof(DateTimeOffset) || efetivo == typeof(DateTime)
            || efetivo == typeof(TimeSpan) || efetivo == typeof(Uri))
        {
            return true;
        }

        // record struct e struct: o compilador gera o construtor público que o serializador usa.
        if (efetivo.IsValueType)
        {
            return true;
        }

        // Converter próprio resolve a reconstrução sozinho, qualquer que seja a forma dos construtores.
        // Sem esta saída a regra reprovaria um tipo que o serializador sabe ler — bloquear código correto
        // é pior que o defeito que a regra caça, porque o autor não tem como satisfazê-la.
        if (efetivo.GetCustomAttribute<JsonConverterAttribute>() is not null)
        {
            return true;
        }

        ConstructorInfo[] publicos = efetivo.GetConstructors();

        return publicos.Any(c => c.GetParameters().Length == 0)
            || publicos.Length == 1
            || publicos.Any(c => c.GetCustomAttribute<JsonConstructorAttribute>() is not null);
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
