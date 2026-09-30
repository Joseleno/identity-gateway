using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.ValueObjects;

namespace IdentityGateway.Application.Common.Abstractions;

/// <summary>
/// O que o provedor de identidade precisa para convidar alguém.
/// </summary>
/// <remarks>
/// O papel vem de quem convida (D11), e o prazo, da <see cref="IInvitationPolicy"/> (D9): o adaptador não decide
/// nenhum dos dois.
/// </remarks>
/// <param name="Email">Endereço do convidado; vira também o username no Keycloak.</param>
/// <param name="Role">Papel de realm atribuído no convite.</param>
/// <param name="LinkLifetime">Por quanto tempo o link do e-mail vale.</param>
public sealed record InviteData(Email Email, RoleName Role, TimeSpan LinkLifetime)
{
    /// <summary>Sem o e-mail: o <c>ToString</c> gerado do record o imprimiria (D15).</summary>
    public override string ToString() => $"InviteData {{ Role = {Role}, LinkLifetime = {LinkLifetime} }}";
}
