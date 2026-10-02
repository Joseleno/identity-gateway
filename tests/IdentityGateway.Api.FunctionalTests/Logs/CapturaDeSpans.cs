using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using OpenTelemetry;

namespace IdentityGateway.Api.FunctionalTests.Logs;

/// <summary>
/// Exportador OpenTelemetry em memória: os spans que a Api exportaria, vistos pelo teste.
/// </summary>
/// <remarks>
/// Uma fila concorrente, e não a lista do exportador em memória do pacote: o span da requisição é exportado numa
/// thread do servidor enquanto o teste lê, e uma <c>List</c> lida durante a escrita lança.
/// </remarks>
internal sealed class ExportadorDeSpansEmMemoria : BaseExporter<Activity>
{
    private readonly ConcurrentQueue<Activity> _spans = new();

    /// <summary>Cada span como texto: nome, tags, eventos e a descrição do status — onde um segredo poderia estar.</summary>
    public IReadOnlyList<string> Textos =>
    [
        .. _spans.Select(span =>
            $"{span.DisplayName} {span.StatusDescription} "
            + $"{string.Join(' ', span.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"))} "
            + string.Join(' ', span.Events.Select(evento =>
                $"{evento.Name} {string.Join(' ', evento.Tags.Select(tag => $"{tag.Key}={tag.Value}"))}"))),
    ];

    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (Activity span in batch)
        {
            _spans.Enqueue(span);
        }

        return ExportResult.Success;
    }
}

/// <summary>As formas em que um texto pode aparecer num log ou num span: cru, e dentro de um base64url.</summary>
internal static class FormasDeUmSegredo
{
    /// <summary>
    /// O texto e as três formas dele em base64url.
    /// </summary>
    /// <remarks>
    /// O e-mail viaja dentro do payload do token, que é base64url. Um log que registrasse o token (ou só o payload)
    /// conteria o e-mail codificado — e a codificação de um trecho depende de onde ele cai no bloco de três bytes. São
    /// três alinhamentos possíveis, e cada um dá um texto diferente; o que fica de fora de cada forma são os caracteres
    /// das pontas, que dependem dos bytes vizinhos.
    /// </remarks>
    public static IReadOnlyList<string> De(string segredo)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(segredo);
        List<string> formas = [segredo];

        for (int deslocamento = 0; deslocamento < 3; deslocamento++)
        {
            byte[] alinhado = new byte[deslocamento + bytes.Length];
            bytes.CopyTo(alinhado, deslocamento);

            string codificado = Base64Url.EncodeToString(alinhado);

            // No começo: os bytes de preenchimento contaminam 2 caracteres (1 byte) ou 3 (2 bytes). No fim: quando o
            // total não fecha um bloco de três bytes, o último caractere mistura bits do byte seguinte.
            int inicio = deslocamento == 0 ? 0 : deslocamento + 1;
            int fim = alinhado.Length % 3 == 0 ? codificado.Length : codificado.Length - 1;

            formas.Add(codificado[inicio..fim]);
        }

        return formas;
    }
}
