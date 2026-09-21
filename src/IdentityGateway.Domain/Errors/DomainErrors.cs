
namespace IdentityGateway.Domain.Errors;

/// <summary>
/// Catálogo dos erros de negócio, agrupados por agregado.
/// </summary>
/// <remarks>
/// <para>
/// Centralizar em vez de construir <c>new Error("...", "...")</c> no ponto da falha resolve dois
/// problemas: o mesmo erro deixa de ganhar código diferente em dois lugares, e o teste pode afirmar
/// <c>resultado.Error.Should().Be(DomainErrors.General.ValorNaoPositivo)</c> em vez de comparar string —
/// asserção que sobrevive a reescrever a mensagem.
/// </para>
/// <para>
/// O grupo <c>General</c> guarda o que não pertence a um agregado só. Os grupos por agregado
/// (<c>Tenant</c>, <c>Member</c>, <c>Client</c>) entram com eles.
/// </para>
/// </remarks>
public static class DomainErrors
{
    /// <summary>Erros aplicáveis a mais de um agregado.</summary>
    public static class General
    {
        /// <summary>Texto obrigatório ausente ou em branco.</summary>
        public static Error TextoObrigatorio(string campo) => Error.Validation(
            "General.TextoObrigatorio",
            $"O campo '{campo}' é obrigatório.");

        /// <summary>Valor numérico que deveria ser maior que zero.</summary>
        public static Error ValorNaoPositivo(string campo) => Error.Validation(
            "General.ValorNaoPositivo",
            $"O campo '{campo}' deve ser maior que zero.");
    }

    /// <summary>Erros do value object <c>Email</c>.</summary>
    public static class Email
    {
        /// <summary>Endereço fora de um formato aceitável.</summary>
        public static Error Invalido(string valor) => Error.Validation(
            "Email.Invalido",
            $"'{valor}' não é um endereço de e-mail válido.");
    }

    /// <summary>Erros do value object <c>TenantSlug</c>.</summary>
    public static class TenantSlug
    {
        /// <summary>Slug fora do formato aceito.</summary>
        public static Error Invalido(string valor) => Error.Validation(
            "TenantSlug.Invalido",
            $"'{valor}' não é um slug válido: use de 3 a 63 caracteres entre letras minúsculas, dígitos e "
            + "hífen, sem hífen no início, no fim ou repetido.");
    }

}
