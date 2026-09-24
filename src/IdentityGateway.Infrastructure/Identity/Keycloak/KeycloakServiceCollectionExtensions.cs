using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// Registra a integração com o Keycloak. Único ponto por onde o resto da Infrastructure a alcança.
/// </summary>
/// <remarks>
/// Há teste de arquitetura garantindo que nenhum tipo deste namespace é usado fora dele, exceto por
/// <c>DependencyInjection</c> — que chama este método e nada mais (ADR-008).
/// </remarks>
internal static class KeycloakServiceCollectionExtensions
{
    internal static IServiceCollection AddKeycloakIdentity(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // As mensagens nunca incluem o valor: o que está sendo validado é, entre outras coisas, uma chave privada.
        services.AddOptions<KeycloakAdminOptions>()
            .Bind(configuration.GetSection(KeycloakAdminOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                BaseUrlAceitavel,
                "Keycloak:Admin:BaseUrl precisa ser uma URL absoluta https (http só com AllowInsecureHttp, em "
                + "desenvolvimento).")
            .Validate(
                GatewaySigningKey.TemExatamenteUmaFonte,
                "Keycloak:Admin: informe exatamente um entre PrivateKeyPath e PrivateKeyPem.")
            .Validate(
                GatewaySigningKey.EhLegivel,
                "Keycloak:Admin: a chave privada não pôde ser lida como RSA em PEM (arquivo ausente, sem permissão "
                + "ou conteúdo inválido).")
            .ValidateOnStart();

        services.AddSingleton<GatewaySigningKey>();
        services.AddSingleton<ClientAssertionFactory>();

        return services;
    }

    private static bool BaseUrlAceitavel(KeycloakAdminOptions opcoes)
    {
        if (!Uri.TryCreate(opcoes.BaseUrl, UriKind.Absolute, out Uri? endereco))
        {
            // Vazia ou relativa: o [Required] cobre a vazia, e aqui não se repete a mensagem.
            return string.IsNullOrWhiteSpace(opcoes.BaseUrl);
        }

        return endereco.Scheme == Uri.UriSchemeHttps
               || (endereco.Scheme == Uri.UriSchemeHttp && opcoes.AllowInsecureHttp);
    }
}
