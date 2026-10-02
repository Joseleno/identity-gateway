using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace IdentityGateway.Api.Authentication;

/// <summary>
/// O que a Gateway exige do access token além do que a biblioteca confere: quem o pediu, que tipo de token é, e de
/// quem ele fala.
/// </summary>
/// <remarks>
/// <para>
/// <b>Na autenticação, e não numa policy.</b> A policy padrão não se soma a uma policy nomeada: um requisito posto ali
/// não valeria para as rotas que têm policy própria — que são todas as que importam.
/// </para>
/// <para>
/// <b>Lê o JSON do payload, e não os claims.</b> O <c>ClaimsPrincipal</c> achata arrays: <c>"typ": ["Bearer"]</c> vira
/// um claim só, indistinguível de <c>"typ": "Bearer"</c>. A forma só é conferida de verdade no JSON. (Para <c>azp</c> e
/// <c>sub</c>, a própria biblioteca já recusa o token em que eles não são texto, ao ler o JWT; para <c>typ</c>, não.)
/// </para>
/// <list type="bullet">
///   <item><b><c>azp</c></b> — texto, presente na lista de clients permitidos, por igualdade ordinal. A audiência diz
///   para quem o token vale; o <c>azp</c> diz qual client o obteve.</item>
///   <item><b><c>typ</c> igual a <c>Bearer</c></b> — o cabeçalho de um ID token também diz <c>JWT</c>; quem distingue
///   é o claim. Defesa em profundidade: o ID token já cairia na audiência.</item>
///   <item><b><c>sub</c></b> — GUID no formato <c>D</c>, que é como o provedor o emite. Sem o scope que o carrega, o
///   token sai sem <c>sub</c>, e a auditoria gravaria autoria nula.</item>
/// </list>
/// </remarks>
internal static class FormaDoAccessToken
{
    /// <summary>
    /// Devolve <see langword="null"/> se a forma é aceita; senão, o motivo — texto fixo, sem nenhum valor do token.
    /// </summary>
    internal static string? Recusar(JsonWebToken token, IReadOnlyList<string> clientsPermitidos)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(clientsPermitidos);

        JsonElement payload;

        try
        {
            using var documento = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.EncodedPayload));
            payload = documento.RootElement.Clone();
        }
        catch (Exception excecao) when (excecao is JsonException or FormatException or ArgumentException)
        {
            return "payload ilegível";
        }

        string? azp = Texto(payload, "azp");

        if (string.IsNullOrEmpty(azp) || !clientsPermitidos.Contains(azp, StringComparer.Ordinal))
        {
            return "azp ausente, sem a forma de texto ou fora da lista de clients permitidos";
        }

        if (!string.Equals(Texto(payload, "typ"), "Bearer", StringComparison.Ordinal))
        {
            return "typ diferente de Bearer";
        }

        // O tamanho antes do parse: Guid.TryParseExact tolera espaço nas pontas, e o formato D tem exatamente 36
        // caracteres.
        if (Texto(payload, "sub") is not { Length: 36 } sub || !Guid.TryParseExact(sub, "D", out _))
        {
            return "sub ausente ou fora do formato de GUID";
        }

        return null;
    }

    /// <summary>O valor do claim, só se ele for um texto JSON. Array, número, objeto e ausência dão nulo.</summary>
    private static string? Texto(JsonElement payload, string claim) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(claim, out JsonElement valor)
        && valor.ValueKind == JsonValueKind.String
            ? valor.GetString()
            : null;
}
