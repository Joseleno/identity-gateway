using IdentityGateway.Application.Common.Abstractions;
using Microsoft.Extensions.Options;

namespace IdentityGateway.Infrastructure.Configuration;

/// <summary>
/// Implementa <see cref="IInvitationPolicy"/> a partir de <see cref="InvitationOptions"/>.
/// </summary>
internal sealed class InvitationPolicy(IOptions<InvitationOptions> opcoes) : IInvitationPolicy
{
    /// <inheritdoc />
    public TimeSpan LinkLifetime { get; } = opcoes.Value.LinkLifetime;
}
