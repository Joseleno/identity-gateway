using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

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
        services.AddSingleton<ITokenEndpoint, KeycloakTokenClient>();
        services.AddSingleton<ServiceAccountTokenCache>();
        services.AddTransient<ServiceAccountTokenHandler>();

        IHttpClientBuilder admin = services.AddHttpClient<KeycloakAdminClient>((provider, http) =>
        {
            http.BaseAddress = provider.GetRequiredService<IOptions<KeycloakAdminOptions>>().Value.AdminBaseAddress;

            // O timeout é da resiliência (por tentativa e total). Este cancelaria no meio do pipeline, com um
            // cancelamento indistinguível do que parte de quem chamou.
            http.Timeout = Timeout.InfiniteTimeSpan;
        });

        // A ORDEM IMPORTA: o primeiro handler registrado é o mais externo. Resiliência por fora e token por dentro
        // fazem cada tentativa renovar o token se preciso, e deixam o "401 → repete uma vez" DENTRO de uma
        // tentativa, contido no timeout total. Na ordem inversa, o reenvio após 401 entraria na pipeline do zero.
        admin.AddStandardResilienceHandler().Configure(
            (HttpStandardResilienceOptions resiliencia, IServiceProvider provider) =>
            {
                HttpResilienceOptions politica = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value;

                resiliencia.Retry.MaxRetryAttempts = politica.MaxRetryAttempts;
                resiliencia.Retry.Delay = TimeSpan.FromSeconds(politica.BaseDelaySeconds);

                // POST não é idempotente: retry automático só em métodos seguros. A idempotência das escritas vem do
                // "consultar antes de criar" do adaptador — e há teste contra o Keycloak real provando que o POST não
                // é repetido.
                resiliencia.Retry.DisableForUnsafeHttpMethods();

                resiliencia.AttemptTimeout.Timeout = TimeSpan.FromSeconds(politica.AttemptTimeoutSeconds);
                resiliencia.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(politica.TotalTimeoutSeconds);
                resiliencia.CircuitBreaker.FailureRatio = politica.FailureRatio;
                resiliencia.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(politica.BreakDurationSeconds);

                // O pipeline padrão recusa amostragem menor que o dobro do timeout por tentativa.
                resiliencia.CircuitBreaker.SamplingDuration =
                    TimeSpan.FromSeconds(Math.Max(30, 2 * politica.AttemptTimeoutSeconds));
            });

        admin.AddHttpMessageHandler<ServiceAccountTokenHandler>();

        services.AddTransient<IIdentityProvider, KeycloakIdentityProvider>();

        // Registrado aqui, e não pela Api: o check depende do cache do token, que é interno a esta camada, e a Api
        // não conhece nenhum tipo do Keycloak (teste de arquitetura). A tag "ready" é a que o /health/ready filtra.
        services.AddHealthChecks().AddCheck<KeycloakHealthCheck>(
            "keycloak",
            failureStatus: HealthStatus.Unhealthy,
            tags: ["ready"],
            timeout: TimeSpan.FromSeconds(5));

        // Token endpoint: cliente próprio, SEM resiliência. O jti é de uso único, e uma política de retry reenviaria
        // o mesmo assertion. Timeout curto, igual ao de uma tentativa da Admin API: sem ele, valeria o padrão de 100s
        // do HttpClient, e um Keycloak pendurado seguraria a sonda de health e a chamada de negócio por quase dois
        // minutos.
        services.AddHttpClient(KeycloakTokenClient.NomeDoCliente, (provider, http) =>
            http.Timeout = TimeSpan.FromSeconds(
                provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value.AttemptTimeoutSeconds));

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
