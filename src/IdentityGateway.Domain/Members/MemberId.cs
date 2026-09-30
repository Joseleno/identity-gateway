namespace IdentityGateway.Domain.Members;

/// <summary>
/// Identidade de um membro.
/// </summary>
/// <remarks>
/// Tipada pelo mesmo motivo do <c>TenantId</c>: <c>GetAsync(TenantId, MemberId)</c> (M2) não pode aceitar os dois
/// trocados. Versão 7 pelo mesmo motivo também — ids em sequência ficam adjacentes no índice.
/// </remarks>
/// <param name="Value">O identificador.</param>
public readonly record struct MemberId(Guid Value)
{
    /// <summary>Gera uma identidade nova.</summary>
    public static MemberId New() => new(Guid.CreateVersion7());
}
