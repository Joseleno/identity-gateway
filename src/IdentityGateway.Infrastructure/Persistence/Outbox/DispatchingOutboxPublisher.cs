using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Common.Messaging;
using IdentityGateway.Application.Tenants.ProvisionTenant;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IdentityGateway.Infrastructure.Persistence.Outbox;

/// <summary>
/// Entrega cada evento do Outbox ao seu destino dentro do próprio processo, como command do Mediator.
/// </summary>
/// <remarks>
/// <para>
/// <b>Transporte provisório.</b> Sem broker nesta versão (fatia B), o Outbox é o transporte e este publisher é o
/// consumidor. Quando o broker chegar, este é o único ponto que muda: ele passa a publicar lá, e um consumidor do
/// broker envia o mesmo command.
/// </para>
/// <para>
/// <b>Um escopo de DI por mensagem, e é regra.</b> O <see cref="OutboxProcessor"/> rastreia as mensagens do lote no
/// <c>AppDbContext</c> do seu escopo e grava o resultado com um <c>SaveChanges</c> depois do despacho. Se o handler
/// usasse o mesmo contexto, esse <c>SaveChanges</c> gravaria também o que um handler que falhou deixou modificado —
/// um tenant <c>Active</c> persistido por um provisionamento que lançou. Por isso o <c>ISender</c> é resolvido num
/// escopo novo, nunca recebido pelo construtor.
/// </para>
/// <para>
/// <b>Contrato com os handlers:</b> falha transitória chega como exceção, e o Outbox repete; desfecho terminal é
/// <see cref="Result.Success()"/>. Um <c>Result</c> de falha é erro de programação e lança — engoli-lo marcaria a
/// mensagem como entregue sem nada ter acontecido.
/// </para>
/// </remarks>
internal sealed partial class DispatchingOutboxPublisher(
    IServiceScopeFactory scopeFactory,
    ILogger<DispatchingOutboxPublisher> logger) : IOutboxPublisher
{
    // Nulo = "sem consumidor nesta versão": entregue de propósito, e dito no log. Um evento fora do mapa lança — o
    // teste de cobertura obriga a decidir o destino no mesmo PR que cria o evento.
    private static readonly Dictionary<Type, Func<IDomainEvent, ICommand?>> Destinos = new()
    {
        [typeof(TenantRegistered)] = evento => new ProvisionTenantCommand(((TenantRegistered)evento).TenantId),
        [typeof(TenantActivated)] = _ => null,
    };

    /// <summary>Tipos de evento com destino decidido — o teste de cobertura compara com o mapa do Outbox.</summary>
    internal static IReadOnlyCollection<Type> TiposComDestino => Destinos.Keys;

    /// <inheritdoc />
    public async Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (!Destinos.TryGetValue(domainEvent.GetType(), out Func<IDomainEvent, ICommand?>? destino))
        {
            throw new InvalidOperationException(
                $"O evento '{domainEvent.GetType().Name}' não tem destino em {nameof(DispatchingOutboxPublisher)}. "
                + "Acrescente uma entrada no mapa — um command, ou nulo para 'sem consumidor'.");
        }

        ICommand? comando = destino(domainEvent);

        if (comando is null)
        {
            SemConsumidor(logger, domainEvent.GetType().Name);
            return;
        }

        await using AsyncServiceScope escopo = scopeFactory.CreateAsyncScope();
        Mediator.ISender sender = escopo.ServiceProvider.GetRequiredService<Mediator.ISender>();

        Result resultado = await sender.Send(comando, cancellationToken);

        if (resultado.IsFailure)
        {
            CommandFalhou(logger, comando.GetType().Name, resultado.Error.Code);
            throw new InvalidOperationException(
                $"{comando.GetType().Name} devolveu falha ({resultado.Error.Code}): {resultado.Error.Message}. "
                + "Handler despachado pelo Outbox só devolve Success; falha transitória é exceção.");
        }
    }

    [LoggerMessage(
        EventId = 2100,
        Level = LogLevel.Debug,
        Message = "Outbox: {Evento} sem consumidor nesta versão; dado como entregue")]
    private static partial void SemConsumidor(ILogger logger, string evento);

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Error,
        Message = "Outbox: {Comando} devolveu falha {Codigo}; erro de programação")]
    private static partial void CommandFalhou(ILogger logger, string comando, string codigo);
}
