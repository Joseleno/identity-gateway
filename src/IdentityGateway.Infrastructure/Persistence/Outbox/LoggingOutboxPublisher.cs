using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Common;
using Microsoft.Extensions.Logging;

namespace IdentityGateway.Infrastructure.Persistence.Outbox;

/// <summary>
/// Implementação padrão que apenas registra a entrega — o ponto onde o broker de verdade entra.
/// </summary>
/// <remarks>
/// <para>
/// <b>É deliberadamente o mínimo, e está marcado como tal.</b> O que a fundação entrega é tudo que vem
/// <i>antes</i> do broker — a gravação transacional, a reserva concorrente, o retry com recuo, o dead-letter — e
/// um ponto único e nomeado para ligar o destino real. No IdentityGateway o destino previsto é o RabbitMQ, que
/// entra no M0 conforme a especificação v2.3.
/// </para>
/// <para>
/// <b>Para ligar um broker de verdade:</b> escreva uma implementação de <see cref="IOutboxPublisher"/> e
/// registre-a no lugar desta. Nada mais no despachante muda — é o que a interface existe para garantir.
/// </para>
/// <para>
/// <b>Lançar é o contrato de falha.</b> Uma implementação que engula o erro e retorne normalmente fará o
/// despachante marcar a mensagem como entregue: ela nunca mais será tentada, e nada acusará que o evento não
/// chegou. É o modo de falha silenciosa que o padrão inteiro existe para evitar.
/// </para>
/// </remarks>
internal sealed partial class LoggingOutboxPublisher(
    ILogger<LoggingOutboxPublisher> logger) : IOutboxPublisher
{
    public Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        // Só o nome do tipo: o evento carrega dado de negócio, e log é indexado e lido por muita gente.
        PublicacaoSimulada(logger, domainEvent.GetType().Name, domainEvent.OccurredOn);

        // O despacho por tipo entra aqui, um ramo por domain event do IdentityGateway. Um evento que não
        // encontre ramo deve deixar isso explícito — sem isso o despachante marca a mensagem como entregue,
        // indistinguível, lendo o código, de um despacho esquecido.
        return Task.CompletedTask;
    }

    [LoggerMessage(
        EventId = 2100,
        Level = LogLevel.Information,
        Message = "Outbox: {Evento} entregue (ocorrido em {OcorridoEm}) — publisher de exemplo, sem broker")]
    private static partial void PublicacaoSimulada(ILogger logger, string evento, DateTimeOffset ocorridoEm);
}
