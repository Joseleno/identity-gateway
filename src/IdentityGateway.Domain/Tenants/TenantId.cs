namespace IdentityGateway.Domain.Tenants;

/// <summary>
/// Identidade de um tenant.
/// </summary>
/// <remarks>
/// <para>
/// <c>readonly record struct</c> e não classe: um id circula muito — chave de dicionário, item de lista,
/// comparação em laço — e alocar no heap a cada uso não se paga. O <c>record</c> dá igualdade por valor
/// sem escrevê-la à mão.
/// </para>
/// <para>
/// O tipo próprio existe para que <c>GetAsync(MemberId)</c> não aceite um <c>TenantId</c> por engano. Com
/// <c>Guid</c> cru os dois são o mesmo tipo, e a troca só aparece em produção, como "não encontrado".
/// </para>
/// </remarks>
/// <param name="Value">O identificador.</param>
public readonly record struct TenantId(Guid Value)
{
    /// <summary>
    /// Gera uma identidade nova.
    /// </summary>
    /// <remarks>
    /// Versão 7 e não 4: o v7 embute o instante de criação, então ids gerados em sequência são adjacentes
    /// no índice. Com v4 cada inserção cai num ponto aleatório da árvore B, o que fragmenta as páginas do
    /// PostgreSQL e piora conforme a tabela cresce.
    /// </remarks>
    public static TenantId New() => new(Guid.CreateVersion7());
}
