using System.Globalization;
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
/// <b><c>components</c> deixou de ser proibido em absoluto (fatia C).</b> O motivo da regra eram os provedores de chave,
/// que levariam material de chave para o repositório; eles continuam proibidos. O único componente aceito é o User
/// Profile, sem o qual o Keycloak descarta o atributo <c>tenant_id</c> em silêncio.
/// </para>
/// <para>
/// <b>Limite honesto.</b> Estes testes pegam chave proibida, placeholder impuro, base64 longo e bloco PEM; não
/// pegam um segredo curto colado numa chave qualquer, fora da lista de <see cref="ChavesProibidas"/> — isso fica
/// para a revisão humana.
/// </para>
/// </remarks>
public sealed partial class RegrasDoRealmTests
{
    private const string ProvedorDePerfil = "org.keycloak.userprofile.UserProfileProvider";

    private static readonly string[] AtributosDoPerfil = ["username", "email", "firstName", "lastName", "tenant_id"];

    private static readonly string[] PapeisDoServiceAccount = ["manage-organizations", "manage-users"];

    private static readonly string[] ChavesDeTimeout = ["connectionTimeout", "timeout", "writeTimeout"];

    private static readonly string[] ChavesProibidas =
    [
        "credentials", "secret", "secretData", "clientSecret", "bindCredential", "password", "privateKey",
    ];

    private static JsonElement Realm()
    {
        string caminho = RaizDoRepositorio.Caminho("keycloak", "bootstrap", "realm-identity-gateway.json");
        return JsonDocument.Parse(File.ReadAllText(caminho)).RootElement.Clone();
    }

    /// <summary>A configuração do User Profile, que o realm guarda como texto JSON dentro do JSON.</summary>
    private static JsonElement ConfiguracaoDoPerfil()
    {
        string texto = Realm().GetProperty("components").GetProperty(ProvedorDePerfil)[0]
            .GetProperty("config").GetProperty("kc.user.profile.config")[0].GetString()!;

        return JsonDocument.Parse(texto).RootElement.Clone();
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
    public void ComponentsSoComOUserProfile()
    {
        // Provedores de chave (org.keycloak.keys.KeyProvider) levariam material de chave para o repositório — o motivo
        // original de "components" ser proibido. O User Profile é o único componente de que a Gateway precisa.
        JsonElement componentes = Realm().GetProperty("components");

        componentes.EnumerateObject().Select(tipo => tipo.Name).Should().Equal(ProvedorDePerfil);
        componentes.GetProperty(ProvedorDePerfil).EnumerateArray()
            .Select(componente => componente.GetProperty("providerId").GetString())
            .Should().Equal("declarative-user-profile");
    }

    [Fact]
    public void AtributoTenantIdDeclaradoSoParaAdmin()
    {
        // D10: sem a declaração, o Keycloak descarta o atributo em silêncio no POST /users; com "user" em edit, o
        // próprio usuário trocaria o tenant_id pela account console e sequestraria a correlação e o isolamento.
        JsonElement atributo = ConfiguracaoDoPerfil().GetProperty("attributes").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "tenant_id");

        atributo.GetProperty("permissions").GetProperty("view").EnumerateArray()
            .Select(papel => papel.GetString()).Should().Equal("admin");
        atributo.GetProperty("permissions").GetProperty("edit").EnumerateArray()
            .Select(papel => papel.GetString()).Should().Equal("admin");
    }

    [Fact]
    public void PerfilSemUnmanagedAttributePolicy()
    {
        // ENABLED é o conserto que qualquer busca sugere para "o atributo sumiu" — e é o que entrega o tenant_id ao
        // usuário (D10). Ausente, vale o padrão: só atributos declarados.
        ConfiguracaoDoPerfil().TryGetProperty("unmanagedAttributePolicy", out _).Should().BeFalse();
    }

    [Fact]
    public void PerfilDeclaraOsAtributosPadrao()
    {
        // Um perfil declarado substitui o padrão inteiro: sem username, email, firstName e lastName, o Keycloak recusa o
        // perfil ou perde o VERIFY_PROFILE que pede nome e sobrenome ao convidado (D16).
        ConfiguracaoDoPerfil().GetProperty("attributes").EnumerateArray()
            .Select(item => item.GetProperty("name").GetString())
            .Should().BeEquivalentTo(AtributosDoPerfil);
    }

    [Fact]
    public void ResetDeSenhaDesligadoEEventosDeAdministracaoLigados()
    {
        // "Esqueci a senha" daria acesso ao convidado habilitado fora do ciclo do convite; os eventos de administração
        // registram as atribuições de papel feitas pelo service account (§10.3).
        JsonElement realm = Realm();

        realm.GetProperty("resetPasswordAllowed").GetBoolean().Should().BeFalse();
        realm.GetProperty("adminEventsEnabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void SmtpPorAmbienteComTimeoutsAbaixoDoDaGateway()
    {
        // Sem o prefixo KC_, que o Keycloak leria como opção dele. Os timeouts (DefaultEmailSenderProvider, padrão
        // 10 s cada) somados precisam caber no AttemptTimeout da Admin API, senão a Gateway desiste antes de o Keycloak
        // responder que o SMTP caiu.
        JsonElement smtp = Realm().GetProperty("smtpServer");

        smtp.GetProperty("host").GetString().Should().Be("${SMTP_HOST}");
        smtp.GetProperty("port").GetString().Should().Be("${SMTP_PORT}");
        smtp.GetProperty("from").GetString().Should().Be("${SMTP_FROM}");
        smtp.GetProperty("auth").GetString().Should().Be("false");
        smtp.GetProperty("ssl").GetString().Should().Be("false");
        smtp.GetProperty("starttls").GetString().Should().Be("false");

        int soma = ChavesDeTimeout
            .Sum(chave => int.Parse(smtp.GetProperty(chave).GetString()!, CultureInfo.InvariantCulture));

        using var appsettings = JsonDocument.Parse(
            File.ReadAllText(RaizDoRepositorio.Caminho("src", "IdentityGateway.Api", "appsettings.json")));
        int tentativaMs = appsettings.RootElement.GetProperty("HttpResilience")
            .GetProperty("AttemptTimeoutSeconds").GetInt32() * 1000;

        soma.Should().BeLessThan(tentativaMs);
    }

    [Fact]
    public void PapelDeRealmTenantAdminExiste()
    {
        Realm().GetProperty("roles").GetProperty("realm").EnumerateArray()
            .Select(papel => papel.GetProperty("name").GetString())
            .Should().Contain("tenant-admin");
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
    public void ServiceAccountComManageOrganizationsEManageUsers()
    {
        // D6: vincular um usuário à Organization exige as duas. Em qualquer ordem; nenhum outro papel.
        JsonElement usuario = Realm().GetProperty("users").EnumerateArray()
            .Single(u => u.GetProperty("username").GetString() == "service-account-identity-gateway");

        usuario.GetProperty("clientRoles").EnumerateObject().Select(p => p.Name)
            .Should().Equal("realm-management");
        usuario.GetProperty("clientRoles").GetProperty("realm-management").EnumerateArray()
            .Select(papel => papel.GetString())
            .Should().BeEquivalentTo(PapeisDoServiceAccount);
        usuario.TryGetProperty("realmRoles", out _).Should().BeFalse();
    }
}
