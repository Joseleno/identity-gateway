using System.Text.Json;
using System.Text.RegularExpressions;

namespace IdentityGateway.ArchitectureTests;

/// <summary>
/// O JSON de bootstrap do realm não contém credencial literal, e tem a forma que a Gateway exige.
/// </summary>
/// <remarks>
/// <para>
/// O repositório é público. Um segredo commitado aqui é segredo publicado, e removê-lo do histórico não o tira de
/// quem já clonou. O teste percorre a ÁRVORE do JSON, e não procura texto: um grep por "secret" passaria por um
/// <c>credentials[].value</c>, e reprovaria a palavra num comentário.
/// </para>
/// <para>
/// <b>Placeholder sem default.</b> <c>${GATEWAY_CLIENT_CERT:MIIC...}</c> parece placeholder e carrega um literal no
/// default. Só <c>${NOME}</c> puro passa.
/// </para>
/// <para>
/// <b>Limite honesto.</b> Estes testes pegam chave proibida, placeholder impuro, base64 longo e bloco PEM; não
/// pegam um segredo curto colado numa chave qualquer, fora da lista de <see cref="ChavesProibidas"/> — isso fica
/// para a revisão humana.
/// </para>
/// </remarks>
public sealed partial class RegrasDoRealmTests
{
    private static readonly string[] ChavesProibidas =
    [
        "credentials", "secret", "secretData", "clientSecret", "bindCredential", "password", "privateKey",
        "components",
    ];

    private static JsonElement Realm()
    {
        string caminho = RaizDoRepositorio.Caminho("keycloak", "bootstrap", "realm-identity-gateway.json");
        return JsonDocument.Parse(File.ReadAllText(caminho)).RootElement.Clone();
    }

    [GeneratedRegex(@"^\$\{[A-Z0-9_]+\}$")]
    private static partial Regex PlaceholderPuro();

    // Base64 longo é o formato de certificado e de chave colados: nenhum valor legítimo do realm tem essa cara.
    [GeneratedRegex(@"^[A-Za-z0-9+/=]{200,}$")]
    private static partial Regex Base64Longo();

    private static IEnumerable<(string Caminho, JsonElement Valor)> Percorrer(JsonElement elemento, string caminho)
    {
        yield return (caminho, elemento);

        if (elemento.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty propriedade in elemento.EnumerateObject())
            {
                foreach ((string Caminho, JsonElement Valor) item in
                    Percorrer(propriedade.Value, $"{caminho}.{propriedade.Name}"))
                {
                    yield return item;
                }
            }
        }
        else if (elemento.ValueKind == JsonValueKind.Array)
        {
            int indice = 0;

            foreach (JsonElement item in elemento.EnumerateArray())
            {
                foreach ((string Caminho, JsonElement Valor) filho in Percorrer(item, $"{caminho}[{indice++}]"))
                {
                    yield return filho;
                }
            }
        }
    }

    [Fact]
    public void NenhumaChaveDeCredencial()
    {
        string[] violacoes =
        [
            .. Percorrer(Realm(), "$")
                .Where(item => ChavesProibidas.Any(chave =>
                    item.Caminho.EndsWith($".{chave}", StringComparison.OrdinalIgnoreCase)))
                .Select(item => item.Caminho),
        ];

        violacoes.Should().BeEmpty("o realm é versionado num repositório público");
    }

    [Fact]
    public void TodoPlaceholderEPuro()
    {
        string[] violacoes =
        [
            .. Percorrer(Realm(), "$")
                .Where(item => item.Valor.ValueKind == JsonValueKind.String)
                .Where(item => item.Valor.GetString()!.Contains("${", StringComparison.Ordinal))
                .Where(item => !PlaceholderPuro().IsMatch(item.Valor.GetString()!))
                .Select(item => item.Caminho),
        ];

        violacoes.Should().BeEmpty("placeholder com default ou embutido em texto carrega literal para o repositório");
    }

    [Fact]
    public void NenhumBase64LongoLiteral()
    {
        string[] violacoes =
        [
            .. Percorrer(Realm(), "$")
                .Where(item => item.Valor.ValueKind == JsonValueKind.String)
                .Where(item => Base64Longo().IsMatch(item.Valor.GetString()!))
                .Select(item => item.Caminho),
        ];

        violacoes.Should().BeEmpty("certificado ou chave colados no realm são literal versionado");
    }

    [Fact]
    public void NenhumBlocoPemLiteral()
    {
        string[] violacoes =
        [
            .. Percorrer(Realm(), "$")
                .Where(item => item.Valor.ValueKind == JsonValueKind.String)
                .Where(item => item.Valor.GetString()!.Contains("-----BEGIN", StringComparison.Ordinal))
                .Select(item => item.Caminho),
        ];

        violacoes.Should().BeEmpty(
            "certificado ou chave colados com armadura PEM são literal versionado, mesmo quando o base64 sozinho "
            + "não chega a 200 caracteres por causa das quebras de linha e do cabeçalho/rodapé");
    }

    [Fact]
    public void RealmTemOrganizationsEOClientDaGatewayComPrivateKeyJwt()
    {
        JsonElement realm = Realm();

        realm.GetProperty("realm").GetString().Should().Be("identity-gateway");
        realm.GetProperty("organizationsEnabled").GetBoolean().Should().BeTrue(
            "sem isto o import passa e POST /organizations responde 404");

        JsonElement client = realm.GetProperty("clients").EnumerateArray()
            .Single(c => c.GetProperty("clientId").GetString() == "identity-gateway");

        client.GetProperty("clientAuthenticatorType").GetString().Should().Be("client-jwt");
        client.GetProperty("serviceAccountsEnabled").GetBoolean().Should().BeTrue();
        client.GetProperty("attributes").GetProperty("jwt.credential.certificate").GetString()
            .Should().Be("${GATEWAY_CLIENT_CERT}");
        client.GetProperty("attributes").GetProperty("token.endpoint.auth.signing.alg").GetString()
            .Should().Be("PS256");
    }

    [Fact]
    public void ServiceAccountSoComManageOrganizations()
    {
        JsonElement usuario = Realm().GetProperty("users").EnumerateArray()
            .Single(u => u.GetProperty("username").GetString() == "service-account-identity-gateway");

        usuario.GetProperty("clientRoles").EnumerateObject().Select(p => p.Name)
            .Should().Equal("realm-management");
        usuario.GetProperty("clientRoles").GetProperty("realm-management").EnumerateArray()
            .Select(papel => papel.GetString())
            .Should().Equal("manage-organizations");
    }
}
