using IdentityGateway.Domain.Common;

namespace IdentityGateway.Domain.Members;

/// <summary>
/// Identificador do usuário no provedor de identidade — o <c>sub</c> (§11.3).
/// </summary>
/// <remarks>
/// <para>
/// É o que a Gateway guarda da pessoa: o nome e o e-mail ficam no Keycloak (§6). Value object, e não
/// <c>string</c>, para que um id de Organization ou um slug não entrem por engano onde se espera um usuário.
/// </para>
/// <para>
/// <b>Lança, e não devolve <c>Result</c>.</b> O valor vem do adaptador, nunca de quem chama a API: vazio é defeito
/// de integração, não resposta de negócio.
/// </para>
/// </remarks>
public sealed class ExternalUserId : ValueObject
{
    private ExternalUserId(string value) => Value = value;

    /// <summary>O id, como o provedor o devolveu (aparado).</summary>
    public string Value { get; }

    /// <summary>Cria o identificador a partir do id devolvido pelo provedor.</summary>
    /// <exception cref="ArgumentException">Se o valor for vazio ou só espaços.</exception>
    public static ExternalUserId From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return new ExternalUserId(value.Trim());
    }

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <summary>O id em texto. Não é dado pessoal: é um identificador opaco do provedor.</summary>
    public override string ToString() => Value;
}
