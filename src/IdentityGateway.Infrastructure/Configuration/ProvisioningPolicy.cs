using IdentityGateway.Application.Common.Abstractions;
using Microsoft.Extensions.Options;

namespace IdentityGateway.Infrastructure.Configuration;

/// <summary>
/// Implementa <see cref="IProvisioningPolicy"/> a partir de <see cref="ProvisioningOptions"/>.
/// </summary>
internal sealed class ProvisioningPolicy(IOptions<ProvisioningOptions> opcoes) : IProvisioningPolicy
{
    /// <inheritdoc />
    public TimeSpan MaxPendingDuration { get; } = TimeSpan.FromHours(opcoes.Value.MaxPendingHours);
}
