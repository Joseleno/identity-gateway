using System.Text.RegularExpressions;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Errors;

namespace IdentityGateway.Domain.ValueObjects;

/// <summary>
/// Endereço de e-mail válido em forma.
/// </summary>
/// <remarks>
/// <para>
/// O tipo serve para que uma função que recebe <c>Email</c> não precise revalidar: se a instância existe,
/// passou pela checagem. É o ganho de substituir <c>string</c> por value object — a validação acontece uma
/// vez, na fronteira, em vez de espalhada em cada uso.
/// </para>
/// <para>
/// <b>A regra é conservadora de propósito, e é a única do sistema.</b> O validador do <c>POST /tenants</c> chama
/// <see cref="IsValid"/>, e o convite do admin usa o endereço como username no Keycloak. Por isso ela é o recorte que
/// os dois aceitam: parte local de até 64 caracteres (RFC 5321, e o limite do Keycloak) em letras minúsculas, dígitos,
/// ponto, sublinhado, <c>+</c> e hífen, sem ponto nas pontas nem repetido; domínio em rótulos de letras, dígitos e
/// hífen. Um endereço que a Gateway aceitasse e o Keycloak recusasse viraria um convite repetido pela janela inteira
/// do provisionamento. Afrouxar depois não quebra ninguém; apertar invalidaria o que já foi aceito.
/// </para>
/// <para>
/// Nenhuma validação sintática prova que o endereço existe — só o envio prova, e a confirmação de verdade fica
/// para o fluxo de convite.
/// </para>
/// <para>
/// <b>O endereço é dado pessoal (D15).</b> <see cref="ToString"/> não o devolve, para que nenhum log, mensagem de
/// exceção ou interpolação o carregue por acidente. Quem precisa do valor usa <see cref="Value"/>, e isso fica
/// visível em revisão.
/// </para>
/// </remarks>
public sealed partial class Email : ValueObject
{
    /// <summary>Limite prático de tamanho, conforme RFC 5321 — e o tamanho da coluna que o guarda.</summary>
    private const int TamanhoMaximo = 254;

    /// <summary>Limite da parte local, conforme RFC 5321 — e o do Keycloak (<c>EmailValidationUtil</c>).</summary>
    private const int TamanhoMaximoDaParteLocal = 64;

    private Email(string value) => Value = value;

    /// <summary>O endereço, normalizado em minúsculas.</summary>
    public string Value { get; }

    /// <summary>
    /// Cria um e-mail a partir do texto informado.
    /// </summary>
    /// <remarks>
    /// Normaliza para minúsculas porque a parte do domínio é insensível a caixa e, na prática, a parte
    /// local também é nos provedores reais — guardar <c>Joao@x.com</c> e <c>joao@x.com</c> como endereços
    /// distintos produziria cadastro duplicado do mesmo usuário. O Keycloak também grava em minúsculas, e a busca
    /// exata do convite depende de os dois lados concordarem.
    /// </remarks>
    public static Result<Email> Of(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result.Failure<Email>(DomainErrors.General.TextoObrigatorio(nameof(Email)));
        }

        string normalizado = value.Trim().ToLowerInvariant();

        if (normalizado.Length > TamanhoMaximo || !TemFormaDeEndereco(normalizado))
        {
            return Result.Failure<Email>(DomainErrors.Email.Invalido());
        }

        return new Email(normalizado);
    }

    /// <summary>Se o texto seria aceito por <see cref="Of"/>.</summary>
    /// <remarks>Existe para o validador do <c>POST</c> usar a mesma regra, em vez de reescrevê-la.</remarks>
    public static bool IsValid(string? value) => value is not null && Of(value).IsSuccess;

    /// <summary>
    /// Um <c>@</c> só, parte local e domínio dentro do recorte descrito no XML doc da classe.
    /// </summary>
    private static bool TemFormaDeEndereco(string valor)
    {
        int arroba = valor.IndexOf('@');

        if (arroba <= 0 || arroba != valor.LastIndexOf('@'))
        {
            return false;
        }

        string local = valor[..arroba];
        string dominio = valor[(arroba + 1)..];

        return local.Length <= TamanhoMaximoDaParteLocal
            && ParteLocal().IsMatch(local)
            && Dominio().IsMatch(dominio);
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <summary>Não devolve o endereço (D15): use <see cref="Value"/>.</summary>
    public override string ToString() => "Email(***)";

    // Átomos separados por ponto: sem ponto no início, no fim ou repetido. Os caracteres são os que o validador de
    // username do Keycloak aceita — o username do convidado é o próprio e-mail.
    [GeneratedRegex(@"^[a-z0-9_+-]+(\.[a-z0-9_+-]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ParteLocal();

    // Rótulos DNS: letras e dígitos nas pontas, hífen só no meio, até 63 cada, e pelo menos um ponto.
    [GeneratedRegex(
        @"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Dominio();
}
