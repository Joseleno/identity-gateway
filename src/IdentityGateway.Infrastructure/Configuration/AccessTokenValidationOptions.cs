namespace IdentityGateway.Infrastructure.Configuration;

/// <summary>
/// O que a Api precisa para validar o access token de quem a chama.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neutra quanto ao provedor de identidade.</b> A Api não conhece o Keycloak (ADR-008, com teste de arquitetura):
/// quem sabe como se escreve o emissor de um realm e onde ficam os metadados é o adaptador, que preenche
/// <see cref="Issuer"/>, <see cref="MetadataAddress"/> e <see cref="RequireHttpsMetadata"/>. Esses três têm setter
/// interno, que a configuração não alcança: ninguém aponta a validação para outro emissor por variável de ambiente.
/// </para>
/// <para>
/// <b>Da configuração vêm só a audiência e a lista de clients</b> (<see cref="SectionName"/>). A lista fica fora do
/// <c>appsettings.json</c> base de propósito: o <c>IConfiguration</c> mescla arrays por índice, e um item do arquivo
/// base sobreviveria à configuração de produção.
/// </para>
/// </remarks>
public sealed class AccessTokenValidationOptions
{
    /// <summary>Seção correspondente no arquivo de configuração.</summary>
    public const string SectionName = "Keycloak:Auth";

    /// <summary>A audiência que o token precisa trazer no <c>aud</c>.</summary>
    public string Audience { get; init; } = "identity-gateway-api";

    /// <summary>Os clients (<c>azp</c>) cujos tokens a Gateway aceita. Igualdade ordinal.</summary>
    /// <remarks>
    /// <b>Vazia é permitido, e fecha:</b> a API sobe e recusa todo token de usuário. Fora de Development ela fica vazia
    /// até existir um client administrativo; é melhor que recusar a subida, porque a API continua servindo o que não
    /// depende de usuário (o provisionamento pelo Outbox, os health checks).
    /// </remarks>
    public IReadOnlyList<string> AllowedClients { get; init; } = [];

    /// <summary>O emissor aceito: o endereço público do realm, sem barra final. Comparado por igualdade ordinal.</summary>
    public string Issuer { get; internal set; } = string.Empty;

    /// <summary>Onde buscar os metadados OIDC e, por eles, as chaves públicas — pelo endereço de transporte.</summary>
    public string MetadataAddress { get; internal set; } = string.Empty;

    /// <summary>Exige <c>https</c> na busca dos metadados. Falso só onde o transporte em <c>http</c> é aceito.</summary>
    public bool RequireHttpsMetadata { get; internal set; } = true;
}
