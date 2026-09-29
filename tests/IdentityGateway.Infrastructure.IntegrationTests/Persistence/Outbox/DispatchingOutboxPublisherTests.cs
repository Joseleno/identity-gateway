using IdentityGateway.Application.Tenants.ProvisionTenant;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Errors;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Domain.Tenants.Events;
using IdentityGateway.Infrastructure.Persistence.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace IdentityGateway.Infrastructure.IntegrationTests.Persistence.Outbox;

public sealed class DispatchingOutboxPublisherTests
{
    private sealed record EventoSemDestino : IDomainEvent
    {
        public DateTimeOffset OccurredOn { get; init; } = DateTimeOffset.UtcNow;
    }

    // O provider não é descartado: vive o tempo de um teste e não segura recurso externo. Se o analisador acusar
    // CA2000 aqui, transformar a classe em IDisposable guardando os providers numa lista, em vez de suprimir.
    private static DispatchingOutboxPublisher Publisher(Action<IServiceCollection>? registrar = null)
    {
        ServiceCollection services = new();
        registrar?.Invoke(services);
        IServiceScopeFactory fabrica = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new DispatchingOutboxPublisher(fabrica, NullLogger<DispatchingOutboxPublisher>.Instance);
    }

    [Fact]
    public void TodoEventoDoOutbox_TemDestinoNoPublisher()
    {
        // Um evento novo no mapa do Outbox, sem destino aqui, seria gravado e lançaria a cada tentativa. Este teste
        // obriga a decidir o destino — mesmo que seja "sem consumidor" — no mesmo PR que cria o evento.
        OutboxEventTypes.Registrados.Should().BeSubsetOf(DispatchingOutboxPublisher.TiposComDestino);
    }

    [Fact]
    public async Task EventoForaDoMapa_Lanca()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        Func<Task> publicar = () => Publisher().PublishAsync(new EventoSemDestino(), ct);

        await publicar.Should().ThrowAsync<InvalidOperationException>().WithMessage("*EventoSemDestino*");
    }

    [Fact]
    public async Task TenantActivated_EEntregueSemResolverNada()
    {
        // O provider está vazio: se o publisher tentasse resolver o ISender, lançaria. "Sem consumidor" é entregue.
        CancellationToken ct = TestContext.Current.CancellationToken;

        Func<Task> publicar = () => Publisher().PublishAsync(new TenantActivated(TenantId.New()), ct);

        await publicar.Should().NotThrowAsync();
    }

    [Fact]
    public async Task TenantRegistered_EnviaOProvisionamentoDoMesmoTenant()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Mediator.ISender sender = Substitute.For<Mediator.ISender>();

        // CA2012 falso positivo: o NSubstitute encadeia Returns(...) sobre a ValueTask que Send devolve — ela é
        // consumida ali mesmo, na mesma expressão, nunca escapa. Suprimir é o caminho, não reescrever a chamada.
#pragma warning disable CA2012
        sender.Send(Arg.Any<Mediator.ICommand<Result>>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(Result.Success()));
#pragma warning restore CA2012
        var tenant = TenantId.New();

        await Publisher(services => services.AddScoped(_ => sender))
            .PublishAsync(new TenantRegistered(tenant, "acme"), ct);

        await sender.Received(1).Send(
            Arg.Is<Mediator.ICommand<Result>>(comando => comando is ProvisionTenantCommand
                && ((ProvisionTenantCommand)comando).TenantId == tenant),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CommandQueDevolveFalha_Lanca()
    {
        // Todo desfecho terminal do handler é Success; uma falha aqui é erro de programação, e engoli-la marcaria a
        // mensagem como entregue sem nada ter acontecido.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Mediator.ISender sender = Substitute.For<Mediator.ISender>();

        // CA2012 falso positivo: ver comentário no teste acima.
#pragma warning disable CA2012
        sender.Send(Arg.Any<Mediator.ICommand<Result>>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(Result.Failure(Error.Failure("Teste.Falha", "falha de teste"))));
#pragma warning restore CA2012

        Func<Task> publicar = () => Publisher(services => services.AddScoped(_ => sender))
            .PublishAsync(new TenantRegistered(TenantId.New(), "acme"), ct);

        await publicar.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Teste.Falha*");
    }
}
