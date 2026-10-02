using System.Text.Json;
using System.Text.RegularExpressions;

namespace IdentityGateway.Testing.Keycloak;

/// <summary>
/// A API HTTP do mailpit: as mensagens de um destinatário e o link de ações que o Keycloak mandou.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sempre filtrado pelo destinatário exato</b>, comparado em minúsculas. A busca <c>to:</c> do mailpit casa por
/// trecho — <c>x@acme.test</c> acharia <c>pre.x@acme.test</c> —, e o Keycloak grava o e-mail em minúsculas.
/// </para>
/// <para>
/// <b>O link sai do campo <c>Text</c></b>, não do HTML: no HTML o "e comercial" do link vem escapado.
/// </para>
/// <para>
/// <b>O link nunca é registrado nem posto em exceção:</b> ele troca a senha da conta enquanto não expira.
/// </para>
/// </remarks>
public sealed class ClienteDoMailpit : IDisposable
{
    private readonly HttpClient _http;

    public ClienteDoMailpit(Uri baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);

        _http = new HttpClient
        {
            BaseAddress = new Uri($"{baseUrl.ToString().TrimEnd('/')}/"),
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Os ids das mensagens cujo destinatário é exatamente o informado, da mais nova para a mais antiga.</summary>
    public async Task<IReadOnlyList<string>> MensagensParaAsync(string destinatario, CancellationToken cancellationToken)
    {
        string consulta = Uri.EscapeDataString($"to:\"{destinatario}\"");

        using var busca = JsonDocument.Parse(await LerAsync($"api/v1/search?query={consulta}", cancellationToken));

        return
        [
            .. busca.RootElement.GetProperty("messages").EnumerateArray()
                .Where(mensagem => mensagem.GetProperty("To").EnumerateArray().Any(para =>
                    string.Equals(para.GetProperty("Address").GetString(), destinatario, StringComparison.OrdinalIgnoreCase)))
                .Select(mensagem => mensagem.GetProperty("ID").GetString()!),
        ];
    }

    /// <summary>Espera até haver ao menos <paramref name="quantidade"/> mensagens para o destinatário (até 5 s).</summary>
    /// <remarks>
    /// O Keycloak envia dentro da requisição de <c>execute-actions-email</c>, então o e-mail já deveria estar lá quando
    /// a chamada volta; a espera curta só absorve a gravação do mailpit.
    /// </remarks>
    public async Task<IReadOnlyList<string>> EsperarMensagensAsync(
        string destinatario, int quantidade, CancellationToken cancellationToken)
    {
        for (int tentativa = 0; tentativa < 20; tentativa++)
        {
            IReadOnlyList<string> ids = await MensagensParaAsync(destinatario, cancellationToken);

            if (ids.Count >= quantidade)
            {
                return ids;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        return await MensagensParaAsync(destinatario, cancellationToken);
    }

    /// <summary>O corpo em texto da mensagem.</summary>
    public async Task<string> TextoDaMensagemAsync(string id, CancellationToken cancellationToken)
    {
        using var mensagem = JsonDocument.Parse(await LerAsync($"api/v1/message/{id}", cancellationToken));

        return mensagem.RootElement.GetProperty("Text").GetString()!;
    }

    // Mailpit fora do ar vira falha da família certa: quem lê o log do job precisa saber que a peça é o mailpit, e não
    // o Keycloak ou a API.
    private async Task<string> LerAsync(string rota, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.GetStringAsync(new Uri(rota, UriKind.Relative), cancellationToken);
        }
        catch (HttpRequestException falha)
        {
            throw new FalhaDoHarnessException(
                FamiliaDeFalha.Mailpit, "mailpit",
                $"a API do mailpit não respondeu ({falha.StatusCode?.ToString() ?? "sem conexão"}).", falha);
        }
    }

    /// <summary>O link de ações da mensagem mais recente para o destinatário, no endereço público do Keycloak.</summary>
    /// <exception cref="FalhaDoHarnessException">Não há mensagem, ou ela não traz o link no endereço público.</exception>
    public async Task<Uri> LinkDeAcoesAsync(
        string destinatario, Uri enderecoPublico, string realm, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enderecoPublico);

        IReadOnlyList<string> ids = await EsperarMensagensAsync(destinatario, 1, cancellationToken);

        if (ids.Count == 0)
        {
            throw new FalhaDoHarnessException(
                FamiliaDeFalha.Mailpit, "convite", "nenhuma mensagem para o destinatário no mailpit.");
        }

        Regex linkDeAcoes = new(
            Regex.Escape(enderecoPublico.ToString().TrimEnd('/')) + "/realms/" + Regex.Escape(realm)
            + @"/login-actions/action-token\?key=\S+",
            RegexOptions.CultureInvariant);
        Match link = linkDeAcoes.Match(await TextoDaMensagemAsync(ids[0], cancellationToken));

        if (!link.Success)
        {
            throw new FalhaDoHarnessException(
                FamiliaDeFalha.Mailpit, "convite",
                "a mensagem não traz o link de ações no endereço público (KC_HOSTNAME).");
        }

        return new Uri(link.Value);
    }
}
