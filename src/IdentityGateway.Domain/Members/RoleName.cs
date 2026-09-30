using System.Text.RegularExpressions;
using IdentityGateway.Domain.Common;

namespace IdentityGateway.Domain.Members;

/// <summary>
/// Nome de um papel do catálogo global (ADR-005).
/// </summary>
/// <remarks>
/// <para>
/// O papel vive no Keycloak, como papel de realm. Esta fatia só usa <see cref="TenantAdmin"/>, e o escolhe no
/// handler, não no adaptador (D11): escolher o papel é regra de negócio, e o M2 convida com outros.
/// </para>
/// <para>
/// Mesmo formato conservador do slug — minúsculas, dígitos e hífen isolado —, porque o nome aparece no token de
/// todos os usuários e nas policies das APIs consumidoras.
/// </para>
/// </remarks>
public sealed partial class RoleName : ValueObject
{
    /// <summary>Administrador de um tenant: o papel do admin inicial.</summary>
    public static readonly RoleName TenantAdmin = new("tenant-admin");

    private RoleName(string value) => Value = value;

    /// <summary>O nome, como está no realm.</summary>
    public string Value { get; }

    /// <summary>Cria o nome de um papel do catálogo.</summary>
    /// <exception cref="ArgumentException">Se o nome estiver fora do formato.</exception>
    public static RoleName From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (!FormaValida().IsMatch(value))
        {
            throw new ArgumentException(
                "Nome de papel fora do formato: minúsculas, dígitos e hífen isolado.", nameof(value));
        }

        return new RoleName(value);
    }

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <summary>O nome em texto.</summary>
    public override string ToString() => Value;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex FormaValida();
}
