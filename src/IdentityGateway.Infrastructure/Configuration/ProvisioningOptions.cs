using System.ComponentModel.DataAnnotations;

namespace IdentityGateway.Infrastructure.Configuration;

/// <summary>
/// Política do provisionamento de tenant.
/// </summary>
/// <remarks>
/// Horas em inteiro, como as demais opções do repositório têm a unidade no nome. Um <c>TimeSpan</c> em JSON teria a
/// armadilha de <c>"24:00:00"</c> não ser 24 horas para o <c>TimeSpan.Parse</c>.
/// </remarks>
public sealed class ProvisioningOptions
{
    /// <summary>Seção correspondente no arquivo de configuração.</summary>
    public const string SectionName = "Provisioning";

    /// <summary>Por quantas horas o provisionamento insiste diante de falha transitória.</summary>
    /// <remarks>
    /// Longa de propósito: cobre queda real do Keycloak, deploy dele e a demonstração do M1 sem que o tenant caia em
    /// <c>ProvisioningFailed</c> — de onde, nesta versão, só sai por intervenção manual. Um Keycloak mal configurado
    /// leva a janela inteira para virar falha; até lá aparece como erro repetido no log e no <c>/health/ready</c>.
    /// </remarks>
    [Range(1, 720, ErrorMessage = "A janela de provisionamento deve estar entre 1 e 720 horas.")]
    public int MaxPendingHours { get; init; } = 24;
}
