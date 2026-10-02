using IdentityGateway.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace IdentityGateway.Api.Authentication;

/// <summary>
/// Como a Api valida o access token: por metadados do provedor, com emissor estrito, e mais nada.
/// </summary>
/// <remarks>
/// <para>
/// <b>Os metadados vêm pelo endereço de transporte; o emissor aceito é o público.</b> São endereços diferentes de
/// propósito (o público pode nem resolver de dentro da rede), e por isso não há <c>Authority</c>: ela amarraria os dois.
/// </para>
/// <para>
/// <b><c>IssuerValidator</c>, e não <c>ValidIssuer</c>.</b> Com metadados, a biblioteca aceita o token cujo <c>iss</c>
/// é igual ao <c>issuer</c> que o discovery anuncia <i>antes</i> de olhar <c>ValidIssuer</c> — um valor errado ali não
/// barra nada. O validador próprio tem precedência sobre tudo e compara com o emissor configurado, por igualdade
/// ordinal.
/// </para>
/// <para>
/// <b>Os números, e o que cada um evita.</b> Tolerância de relógio de 30 s: o padrão de 5 min dobraria a vida de um
/// token de 5 min. Prazo de 5 s na busca dos metadados: o padrão é de 60 s, e a busca é serializada — com o provedor
/// mudo, os pedidos se empilhariam. Intervalo de refresh de 30 s: o padrão de 5 min deixaria uma segunda rotação de
/// chave em <c>401</c> por todo esse tempo.
/// </para>
/// <para>
/// <b>Sem detalhe do erro na resposta, em nenhum ambiente.</b> O <c>WWW-Authenticate</c> ecoaria o <c>iss</c> e o
/// <c>aud</c> recusados, que vêm do token: um <c>iss</c> com caractere de controle faria o servidor recusar o próprio
/// cabeçalho, e o <c>401</c> viraria <c>500</c>. O diagnóstico vai para o log.
/// </para>
/// </remarks>
internal static class ValidacaoDoAccessToken
{
    /// <summary>Categoria dos logs de autenticação.</summary>
    internal const string Categoria = "IdentityGateway.Api.Authentication";

    internal static readonly TimeSpan Tolerancia = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan PrazoDosMetadados = TimeSpan.FromSeconds(5);

    internal static readonly TimeSpan IntervaloDeRefresh = TimeSpan.FromSeconds(30);

    private static readonly string[] SoRs256 = [SecurityAlgorithms.RsaSha256];

    internal static void Configurar(JwtBearerOptions jwt, AccessTokenValidationOptions validacao)
    {
        ArgumentNullException.ThrowIfNull(jwt);
        ArgumentNullException.ThrowIfNull(validacao);

        jwt.MetadataAddress = validacao.MetadataAddress;
        jwt.RequireHttpsMetadata = validacao.RequireHttpsMetadata;
        jwt.BackchannelTimeout = PrazoDosMetadados;
        jwt.RefreshInterval = IntervaloDeRefresh;

        // Sem isto, o handler remapeia claims curtos para URI antes de a policy ver o token: "roles" vira a URI longa
        // de papel, e RequireClaim("roles", ...) nunca casa — sempre 403, mesmo com o token certo.
        jwt.MapInboundClaims = false;
        jwt.IncludeErrorDetails = false;

        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            IssuerValidator = EmissorEstrito(validacao.Issuer),
            ValidateAudience = true,
            ValidAudience = validacao.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = SoRs256,
            ClockSkew = Tolerancia,

            // "sub", e não preferred_username: o username é o e-mail, e o nome do usuário vai parar em log.
            NameClaimType = "sub",
            RoleClaimType = "roles",
        };

        jwt.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = AoFalhar,
        };
    }

    /// <summary>Aceita só o emissor configurado, por igualdade ordinal.</summary>
    /// <remarks>
    /// Lança <see cref="SecurityTokenInvalidIssuerException"/>, que a biblioteca trata como recuperável: dispara um
    /// refresh dos metadados, limitado pelo intervalo de refresh, e o token continua recusado.
    /// </remarks>
    internal static IssuerValidator EmissorEstrito(string esperado) => (issuer, _, _) =>
        string.Equals(issuer, esperado, StringComparison.Ordinal)
            ? issuer
            : throw new SecurityTokenInvalidIssuerException("Emissor do token não é o do realm configurado.")
            {
                InvalidIssuer = issuer,
            };

    /// <summary>
    /// Registra por que a autenticação falhou — só o tipo da falha, nunca o token nem a mensagem da exceção.
    /// </summary>
    /// <remarks>
    /// Com o provedor fora do ar e os metadados ainda não carregados, a falha chega como "chave não encontrada", e a
    /// resposta é <c>401</c>: sem este aviso, o provedor fora viraria <c>401</c> em silêncio, apontando para o token.
    /// </remarks>
    private static Task AoFalhar(AuthenticationFailedContext contexto)
    {
        ILogger logger = contexto.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>().CreateLogger(Categoria);
        string tipo = contexto.Exception.GetType().Name;

        // Warning uma vez por intervalo; no resto, Debug. Um token com kid inventado também cai aqui, sem autenticação
        // e sem consumir cota: sem o limite, um laço de pedidos encheria o log de avisos.
        if (EhFalhaDeChaveOuDeMetadados(contexto.Exception)
            && contexto.HttpContext.RequestServices.GetRequiredService<AvisoDeChavesIndisponiveis>().PodeAvisar())
        {
            AutenticacaoLogs.ChavesIndisponiveis(logger, tipo);
        }
        else
        {
            AutenticacaoLogs.TokenRecusado(logger, tipo);
        }

        return Task.CompletedTask;
    }

    // Chave de assinatura não encontrada: kid desconhecido, ou nenhuma chave porque os metadados não vieram. A falha
    // da busca em si nunca chega aqui — a biblioteca a engole e reprova o token por falta de chave. Por isso a lista
    // tem um tipo só: incluir InvalidOperationException, por exemplo, rotularia qualquer defeito interno como
    // "chaves indisponíveis".
    internal static bool EhFalhaDeChaveOuDeMetadados(Exception excecao) =>
        excecao is SecurityTokenSignatureKeyNotFoundException;
}
