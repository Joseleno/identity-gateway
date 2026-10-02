using System.Buffers.Text;
using System.Text.Json;

namespace IdentityGateway.Testing.Keycloak;

/// <summary>
/// Lê o payload de um JWT <b>sem validar nada</b>: para os testes afirmarem a forma de um token que acabou de ser
/// emitido.
/// </summary>
/// <remarks>
/// Nunca é a validação de ninguém. Existe porque "a API respondeu 401" sozinho é sobredeterminado: o teste precisa
/// mostrar que o token tinha a forma que torna aquele 401 o esperado.
/// </remarks>
public static class PayloadDoJwt
{
    public static JsonElement Ler(string jwt)
    {
        ArgumentNullException.ThrowIfNull(jwt);

        string[] partes = jwt.Split('.');

        if (partes.Length < 2)
        {
            throw new FormatException("O texto não tem a forma cabeçalho.payload.assinatura de um JWT.");
        }

        using var documento = JsonDocument.Parse(Base64Url.DecodeFromChars(partes[1]));
        return documento.RootElement.Clone();
    }

    /// <summary>O claim <c>aud</c> como lista: o Keycloak o emite como texto quando há um valor só.</summary>
    public static IReadOnlyList<string> Audiencias(JsonElement payload)
    {
        if (!payload.TryGetProperty("aud", out JsonElement aud))
        {
            return [];
        }

        return aud.ValueKind == JsonValueKind.Array
            ? [.. aud.EnumerateArray().Select(item => item.GetString()!)]
            : [aud.GetString()!];
    }
}
