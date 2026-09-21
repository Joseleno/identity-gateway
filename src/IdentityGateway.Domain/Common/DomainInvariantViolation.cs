using System.Diagnostics.CodeAnalysis;

namespace IdentityGateway.Domain.Common;

/// <summary>
/// Invariante de domínio violada — erro de programação, não de negócio.
/// </summary>
/// <remarks>
/// <para>
/// A distinção importa: recusa de negócio ("não há vaga no plano") é resultado esperado e volta como
/// <see cref="Result"/>, para quem chamou decidir o que fazer. Esta exceção é o outro caso — o chamador
/// pediu algo que a regra não admite em nenhuma circunstância, como liberar uma vaga que não está ocupada.
/// Devolver <c>Result</c> aqui convidaria a tratar como fluxo alternativo o que é defeito.
/// </para>
/// <para>
/// Não tem construtor sem parâmetro nem sobrecarga com <c>innerException</c> de propósito: toda ocorrência
/// precisa dizer qual invariante caiu e em qual agregado, ou a mensagem não ajuda quem for depurar.
/// </para>
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1710:Identifiers should have correct suffix",
    Justification = "O nome vem da especificação arquitetural (§11.1) e é lido no código de domínio como "
        + "uma frase — `throw new DomainInvariantViolation(...)` diz o que aconteceu. O sufixo `Exception` "
        + "acrescentaria ruído sem informação: a herança já diz que é exceção.")]
public sealed class DomainInvariantViolation : Exception
{
    /// <summary>
    /// Cria a exceção descrevendo a invariante violada.
    /// </summary>
    /// <param name="message">Qual invariante caiu, e em qual agregado.</param>
    public DomainInvariantViolation(string message)
        : base(message)
    {
    }
}
