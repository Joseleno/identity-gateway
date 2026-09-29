namespace IdentityGateway.Application.Common.Abstractions;

/// <summary>
/// Por quanto tempo o provisionamento de um tenant insiste diante de falha transitória.
/// </summary>
/// <remarks>
/// <para>
/// Porta, e não <c>IOptions</c> na Application: a camada não referencia <c>Microsoft.Extensions.Options</c>, e o
/// precedente é o <see cref="IPlanCatalog"/> — contrato aqui, implementação na Infrastructure a partir de options
/// validadas na subida.
/// </para>
/// <para>
/// A janela é contada desde <c>Tenant.RegisteredAt</c>. Esgotada, o tenant vai a <c>ProvisioningFailed</c>. O
/// Outbox precisa continuar trazendo a mensagem de volta por mais tempo que isto — a Infrastructure valida.
/// </para>
/// </remarks>
public interface IProvisioningPolicy
{
    /// <summary>Tempo máximo em <c>Pending</c> antes de desistir de falhas transitórias.</summary>
    TimeSpan MaxPendingDuration { get; }
}
