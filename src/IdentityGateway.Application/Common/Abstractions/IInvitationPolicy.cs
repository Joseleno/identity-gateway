namespace IdentityGateway.Application.Common.Abstractions;

/// <summary>
/// Regras do convite que vêm de configuração.
/// </summary>
/// <remarks>
/// <para>
/// Porta pelo mesmo motivo do <see cref="IProvisioningPolicy"/>: a Application não referencia
/// <c>Microsoft.Extensions.Options</c>.
/// </para>
/// <para>
/// Nesta versão o prazo é global (D9). O prazo por tenant da §9.9 chega no M2, pela mesma porta — quem chama não muda.
/// </para>
/// </remarks>
public interface IInvitationPolicy
{
    /// <summary>Por quanto tempo o link do e-mail de convite vale.</summary>
    /// <remarks>Passado ao Keycloak em cada envio, em segundos inteiros; o padrão do realm (12 h) não é usado.</remarks>
    TimeSpan LinkLifetime { get; }
}
