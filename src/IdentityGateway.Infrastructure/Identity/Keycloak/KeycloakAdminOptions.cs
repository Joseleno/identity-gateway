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
/// </remarks>
internal sealed class KeycloakAdminOptions
{
    /// <summary>Seção correspondente no arquivo de configuração.</summary>
    public const string SectionName = "Keycloak:Admin";

    /// <summary>Endereço do Keycloak como a Gateway o alcança, com eventual prefixo de caminho.</summary>
    /// <remarks>
    /// Precisa ser <b>o mesmo</b> endereço que o Keycloak usa para calcular o próprio issuer: o <c>aud</c> do
    /// assertion é derivado daqui. Em produção, igual ao <c>KC_HOSTNAME</c>.
    /// </remarks>
    [Required(ErrorMessage = "Keycloak:Admin:BaseUrl é obrigatório.")]
    public string BaseUrl { get; init; } = string.Empty;

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

    /// <summary>Permite <c>http://</c>. Ligado só no ambiente de desenvolvimento.</summary>
    /// <remarks>
    /// Fora do desenvolvimento, o assertion e o token do service account trafegariam em claro — e o token dá
    /// <c>manage-organizations</c> sobre o realm inteiro.
    /// </remarks>
    public bool AllowInsecureHttp { get; init; }

    /// <summary>Issuer do realm, sem barra final. É o <c>aud</c> do client assertion.</summary>
    public string Issuer => $"{BaseUrl.TrimEnd('/')}/realms/{Realm}";

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
