using System.Globalization;
using IdentityGateway.Infrastructure.Persistence.Outbox;
using Microsoft.Extensions.Options;

namespace IdentityGateway.Infrastructure.Configuration;

/// <summary>
/// Recusa subir com um Outbox que desiste antes da janela de provisionamento.
/// </summary>
/// <remarks>
/// A janela é do handler, mas quem traz a mensagem de volta é o Outbox. Se ele esgotasse as tentativas antes, a
/// mensagem pararia e o tenant ficaria em <c>Pending</c> para sempre — ninguém o marcaria <c>ProvisioningFailed</c>,
/// porque quem marca é o handler, e o handler só roda quando a mensagem volta.
/// </remarks>
internal sealed class OutboxCobreAJanelaDeProvisionamento(IOptions<ProvisioningOptions> provisionamento)
    : IValidateOptions<OutboxOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, OutboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var janela = TimeSpan.FromHours(provisionamento.Value.MaxPendingHours);
        TimeSpan cobertura = OutboxBackoff.CoberturaMinima(options);

        if (cobertura > janela)
        {
            return ValidateOptionsResult.Success;
        }

        // Duas chamadas a string.Create, e não uma interpolação concatenada com "+": o compilador não aceita o
        // handler de interpolação (ref struct) como operando de "+" — CS1620. Concatenar as duas strings já
        // materializadas, depois, é uma operação comum.
        string mensagem =
            string.Create(
                CultureInfo.InvariantCulture,
                $"Outbox: {options.MaxAttempts} tentativas cobrem no mínimo {cobertura.TotalHours:F1}h de retry, ")
            + string.Create(
                CultureInfo.InvariantCulture,
                $"menos que a janela de provisionamento de {janela.TotalHours:F0}h (Provisioning:MaxPendingHours). ")
            + "Aumente Outbox:MaxAttempts ou Outbox:MaxRetryDelaySeconds, ou reduza a janela.";

        return ValidateOptionsResult.Fail(mensagem);
    }
}
