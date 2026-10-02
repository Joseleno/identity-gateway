using System.ComponentModel.DataAnnotations;

namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// Como a Gateway alcança a Admin API do Keycloak e se autentica nela.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>class</c>, e não <c>record</c>, de propósito.</b> O <c>ToString()</c> que o compilador gera para um record
/// imprime todas as propriedades — inclusive <see cref="PrivateKeyPem"/>. Um log de diagnóstico que fizesse
/// <c>{options}</c> vazaria a chave privada da Gateway.
/// </para>
/// <para>
/// <b>A chave vem por arquivo ou por PEM, nunca pelos dois.</b> <see cref="PrivateKeyPath"/> é o preferido: segredo
/// montado como arquivo não aparece em <c>docker inspect</c> nem em <c>/proc/*/environ</c>, e é assim que cofres e
/// orquestradores entregam segredo. <see cref="PrivateKeyPem"/> existe para user-secrets e testes.
/// </para>
/// <para>
/// <b>Endereço público e transporte são coisas diferentes (fatia C, D8).</b> O <c>aud</c> do assertion é o emissor
/// público (<see cref="AssertionAudience"/>); o token endpoint e a Admin API são chamados pelo <see cref="BaseUrl"/>,
/// que pode ser o endereço interno.
/// </para>
/// </remarks>
internal sealed class KeycloakAdminOptions
{
    /// <summary>Seção correspondente no arquivo de configuração.</summary>
    public const string SectionName = "Keycloak:Admin";

    /// <summary>Endereço do Keycloak como a Gateway o alcança (transporte), com eventual prefixo de caminho.</summary>
    /// <remarks>
    /// O token endpoint e a Admin API derivam só daqui. Pode ser o endereço interno — <c>http://keycloak:8080</c> no
    /// compose —, porque, com <c>KC_HOSTNAME_BACKCHANNEL_DYNAMIC</c>, o Keycloak responde por ele sem redirecionar.
    /// </remarks>
    [Required(ErrorMessage = "Keycloak:Admin:BaseUrl é obrigatório.")]
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Endereço público do Keycloak — o <c>KC_HOSTNAME</c>. Alimenta o emissor; nunca é discado.</summary>
    /// <remarks>
    /// <para>
    /// Com <c>KC_HOSTNAME</c>, o Keycloak calcula o emissor pelo endereço público, mesmo com a requisição chegando pelo
    /// interno, e compara o <c>aud</c> por igualdade de texto: <c>127.0.0.1</c> no lugar de <c>localhost</c> é recusado
    /// com "Invalid token audience". Omitido, vale o <see cref="BaseUrl"/>, e nada muda para quem roda a API pela IDE
    /// com <c>BaseUrl=http://localhost:8081</c>. Absoluto, sem query nem fragmento; <c>http</c> só em Development.
    /// </para>
    /// <para>
    /// O emissor (<see cref="Issuer"/>) serve a duas coisas: é o <c>aud</c> do client assertion e é o <c>iss</c> que a
    /// Api aceita nos tokens de quem a chama. As duas saem daqui para nunca divergirem.
    /// </para>
    /// </remarks>
    public string? PublicBaseUrl { get; init; }

    /// <summary>Realm da Gateway. Nunca o <c>master</c>.</summary>
    [Required(ErrorMessage = "Keycloak:Admin:Realm é obrigatório.")]
    public string Realm { get; init; } = string.Empty;

    /// <summary>ClientId do client confidencial da Gateway.</summary>
    [Required(ErrorMessage = "Keycloak:Admin:ClientId é obrigatório.")]
    public string ClientId { get; init; } = string.Empty;

    /// <summary>Caminho do arquivo PEM com a chave privada RSA.</summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>A chave privada RSA em PEM, inline.</summary>
    public string? PrivateKeyPem { get; init; }

    /// <summary>Permite <c>http://</c>. Aceito só no ambiente Development.</summary>
    /// <remarks>
    /// Fora do desenvolvimento, o assertion e o token do service account trafegariam em claro — e o token dá
    /// <c>manage-organizations</c> e <c>manage-users</c> sobre o realm inteiro. A subida recusa a opção fora de
    /// Development (fatia C): "o transporte pode ser interno" não é licença para <c>http</c> em produção.
    /// </remarks>
    public bool AllowInsecureHttp { get; init; }

    /// <summary>O emissor público do realm, sem barra final: <c>{PublicBaseUrl ?? BaseUrl}/realms/{Realm}</c>.</summary>
    public string Issuer =>
        $"{(string.IsNullOrWhiteSpace(PublicBaseUrl) ? BaseUrl : PublicBaseUrl).TrimEnd('/')}/realms/{Realm}";

    /// <summary>O <c>aud</c> do client assertion: o emissor público do realm.</summary>
    public string AssertionAudience => Issuer;

    /// <summary>Os metadados OIDC do realm, pelo endereço de transporte — o público pode não resolver daqui.</summary>
    public string MetadataAddress =>
        $"{BaseUrl.TrimEnd('/')}/realms/{Realm}/.well-known/openid-configuration";

    /// <summary>URL do token endpoint, derivada só do <see cref="BaseUrl"/>.</summary>
    public string TokenEndpoint => $"{BaseUrl.TrimEnd('/')}/realms/{Realm}/protocol/openid-connect/token";

    /// <summary>Endereço base dos clientes HTTP, com barra final.</summary>
    /// <remarks>
    /// <para>
    /// A barra final não é estética: sem ela, <c>new Uri(base, "admin/realms/...")</c> descarta o último segmento
    /// do caminho — e um Keycloak em <c>/auth</c> seria chamado na raiz.
    /// </para>
    /// <para>
    /// <b>Nunca lança.</b> <c>ValidateDataAnnotations</c> percorre recursivamente as propriedades complexas do
    /// options para validar objetos aninhados, e chama este <c>get</c> antes de o <see cref="BaseUrl"/> vazio ou
    /// inválido ser rejeitado pelo <c>[Required]</c> — um <c>BaseUrl</c> ausente faria <c>new Uri("/")</c> lançar
    /// <see cref="UriFormatException"/> não tratada em vez da <c>OptionsValidationException</c> esperada. Com
    /// <c>BaseUrl</c> ausente ou inválido, o retorno é um placeholder sem sentido — que nunca chega a ser lido: a
    /// validação já barrou a subida antes de qualquer consumidor pedir esta propriedade.
    /// </para>
    /// </remarks>
    public Uri AdminBaseAddress => Uri.TryCreate($"{BaseUrl.TrimEnd('/')}/", UriKind.Absolute, out Uri? endereco)
        ? endereco
        : new Uri("about:blank");
}
