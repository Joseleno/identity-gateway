using System.Text.RegularExpressions;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Errors;

namespace IdentityGateway.Domain.Tenants;

/// <summary>
/// Identificador legível do tenant, único e imutável.
/// </summary>
/// <remarks>
/// <para>
/// Vira o <i>alias</i> da Organization no Keycloak, e pode vir a ser subdomínio ou segmento de URL. Por
/// isso a regra é a interseção conservadora do que os três aceitam: minúsculas, dígitos e hífen, de 3 a 63
/// caracteres, sem hífen no início, no fim ou repetido.
/// </para>
/// <para>
/// <b>Conservadora de propósito.</b> Afrouxar a regra depois não quebra ninguém; apertá-la invalidaria
/// slugs já cadastrados. O limite de 63 é o maior rótulo DNS válido, e o underscore fica de fora porque
/// DNS não o aceita — ainda que o Keycloak aceitasse.
/// </para>
/// </remarks>
public sealed partial class TenantSlug : ValueObject
{
    private const int TamanhoMinimo = 3;
    private const int TamanhoMaximo = 63;

    private TenantSlug(string value) => Value = value;

    /// <summary>O slug, normalizado em minúsculas.</summary>
    public string Value { get; }

    /// <summary>
    /// Cria um slug a partir do texto informado.
    /// </summary>
    /// <remarks>
    /// Normaliza antes de validar — apara o entorno e baixa a caixa —, de modo que <c>"  Acme-Corp  "</c>
    /// e <c>"acme-corp"</c> produzam o mesmo slug. Sem isso, dois tenants poderiam coexistir diferindo
    /// apenas na caixa, e o alias no Keycloak colidiria.
    /// </remarks>
    public static Result<TenantSlug> Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result.Failure<TenantSlug>(DomainErrors.General.TextoObrigatorio(nameof(TenantSlug)));
        }

        string normalizado = value.Trim().ToLowerInvariant();

        if (normalizado.Length is < TamanhoMinimo or > TamanhoMaximo || !FormaValida().IsMatch(normalizado))
        {
            return Result.Failure<TenantSlug>(DomainErrors.TenantSlug.Invalido(value));
        }

        return new TenantSlug(normalizado);
    }

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <summary>O slug em texto.</summary>
    public override string ToString() => Value;

    // Começa e termina em letra ou dígito; no meio, hífen isolado é permitido e hífen duplo não.
    // Regex compilada em tempo de build pelo gerador: sem custo de interpretação a cada chamada.
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex FormaValida();
}
