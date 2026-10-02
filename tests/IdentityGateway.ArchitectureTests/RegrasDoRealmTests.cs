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

    private const string ClientDaGateway = "identity-gateway";

    private const string ClientDeDemonstracao = "identity-gateway-demo";

    private const string AtributoDoDeviceFlow = "oauth2.device.authorization.grant.enabled";

    private const string MapperDeAudiencia = "oidc-audience-mapper";

    private static readonly string[] Catalogo = ["platform-admin", "tenant-admin", "financial-manager", "reader"];

    // Declarados só para o import não os pôr no papel padrão (RealmManager.java L664-666 da 26.7.4).
    private static readonly string[] PapeisForaDoPadrao = ["offline_access", "uma_authorization"];

    private static readonly string[] PapeisDeclarados = [.. Catalogo, .. PapeisForaDoPadrao];

    private static readonly string[] ScopesDaGateway = ["gateway-roles", "gateway-tenant", "gateway-api"];

    private static readonly string[] ScopesDoDemo = ["basic", "acr", "gateway-roles", "gateway-tenant", "gateway-api"];

    private static readonly string[] ScopesDoServiceAccount = ["basic", "roles"];

    private static readonly string[] AcoesDoBootstrap = ["UPDATE_PASSWORD", "VERIFY_EMAIL"];

    private static readonly string[] ChavesObrigatoriasDoClient =
    [
        "fullScopeAllowed", "directAccessGrantsEnabled", "standardFlowEnabled", "defaultClientScopes",
        "optionalClientScopes",
    ];

    private static readonly string[] ChavesQueOBootstrapNaoTem = ["attributes", "groups", "clientRoles", "credentials"];

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

    private static JsonElement Client(string clientId) => Realm().GetProperty("clients").EnumerateArray()
        .Single(client => client.GetProperty("clientId").GetString() == clientId);

    private static JsonElement Scope(string nome) => Realm().GetProperty("clientScopes").EnumerateArray()
        .Single(scope => scope.GetProperty("name").GetString() == nome);

    /// <summary>Os textos de um array, ou vazio se a chave não existe.</summary>
    private static string[] Textos(JsonElement elemento, string propriedade) =>
        elemento.TryGetProperty(propriedade, out JsonElement lista)
            ? [.. lista.EnumerateArray().Select(item => item.GetString()!)]
            : [];

    private static bool Verdadeiro(JsonElement elemento, string propriedade) =>
        elemento.TryGetProperty(propriedade, out JsonElement valor) && valor.ValueKind == JsonValueKind.True;

    // Num método, e não direto na lambda da asserção: NotContain e OnlyContain recebem árvore de expressão, e árvore
    // de expressão não aceita o descarte do out (CS8207).
    private static bool Tem(JsonElement elemento, string propriedade) => elemento.TryGetProperty(propriedade, out _);

    private static JsonElement ConfigDoMapperUnico(string scope, string tipo)
    {
        JsonElement[] mappers = [.. Scope(scope).GetProperty("protocolMappers").EnumerateArray()];

        mappers.Should().ContainSingle($"o scope {scope} tem um mapper só");
        mappers[0].GetProperty("protocolMapper").GetString().Should().Be(tipo);

        return mappers[0].GetProperty("config");
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
            // O User Profile é um JSON em texto dentro do JSON: sem percorrê-lo também, uma chave proibida ali passaria.
            .. Percorrer(Realm(), "$").Concat(Percorrer(ConfiguracaoDoPerfil(), "$.kc.user.profile.config"))
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

    [Fact]
    public void CatalogoDePapeisExatoENuncaComposto()
    {
        // O checkAdminRoles do Keycloak olha só o NOME do papel: um papel do catálogo composto com papéis de
        // realm-management seria atribuível por quem tem manage-users — a chave da Gateway.
        JsonElement realm = Realm();
        JsonElement[] papeis = [.. realm.GetProperty("roles").GetProperty("realm").EnumerateArray()];

        papeis.Select(papel => papel.GetProperty("name").GetString())
            .Should().BeEquivalentTo(PapeisDeclarados);
        papeis.Should().NotContain(papel => Verdadeiro(papel, "composite") || Tem(papel, "composites"),
            "nenhum papel do catálogo é composto");
        realm.TryGetProperty("defaultRole", out _).Should().BeFalse(
            "o papel padrão é o que o import monta; declarado aqui, levaria papéis para todo usuário novo");
    }

    [Fact]
    public void CreateDefaultClientScopesLigado()
    {
        // Declarar clientScopes desliga a criação dos scopes embutidos (profile, email, roles, basic, acr...), sem
        // erro: o token sairia sem sub. O atributo não é documentado e não persiste — a prova viva está no fixture.
        JsonElement realm = Realm();

        realm.GetProperty("clientScopes").GetArrayLength().Should().BePositive();
        realm.GetProperty("attributes").GetProperty("CreateDefaultClientScopes").GetString().Should().Be("true");
    }

    [Fact]
    public void ScopesDaGatewayExistemEForaDosDefaultsDoRealm()
    {
        // D-e: num scope default, qualquer client do realm — o service account, os de tenant, um criado pela Admin API —
        // emitiria token com a audiência da Gateway.
        JsonElement realm = Realm();
        string[] noRealm = [.. Textos(realm, "defaultDefaultClientScopes"), .. Textos(realm, "defaultOptionalClientScopes")];

        realm.GetProperty("clientScopes").EnumerateArray().Select(scope => scope.GetProperty("name").GetString())
            .Should().Contain(ScopesDaGateway);
        noRealm.Should().NotContain(ScopesDaGateway).And.NotContain("offline_access");
    }

    [Fact]
    public void GatewayRolesEmiteSoOCatalogo()
    {
        JsonElement config = ConfigDoMapperUnico("gateway-roles", "oidc-usermodel-realm-role-mapper");

        config.GetProperty("claim.name").GetString().Should().Be("roles");
        config.GetProperty("multivalued").GetString().Should().Be("true");
        config.GetProperty("access.token.claim").GetString().Should().Be("true");
        config.GetProperty("id.token.claim").GetString().Should().Be("false");
        config.GetProperty("userinfo.token.claim").GetString().Should().Be("false");

        // Com fullScopeAllowed falso no client, o claim traz só os papéis mapeados no scope. Sem este mapeamento, ou
        // com um papel a mais nele, default-roles-* e papéis fora do catálogo entrariam no token.
        JsonElement[] mapeamentos = [.. Realm().GetProperty("scopeMappings").EnumerateArray()];

        mapeamentos.Should().ContainSingle();
        mapeamentos[0].GetProperty("clientScope").GetString().Should().Be("gateway-roles");
        Textos(mapeamentos[0], "roles").Should().BeEquivalentTo(Catalogo);
        Realm().TryGetProperty("clientScopeMappings", out _).Should().BeFalse();
    }

    [Fact]
    public void GatewayTenantEmiteUmValorSoSemAgregar()
    {
        JsonElement config = ConfigDoMapperUnico("gateway-tenant", "oidc-usermodel-attribute-mapper");

        config.GetProperty("user.attribute").GetString().Should().Be("tenant_id");
        config.GetProperty("claim.name").GetString().Should().Be("tenant_id");
        config.GetProperty("multivalued").GetString().Should().Be("false");
        config.GetProperty("aggregate.attrs").GetString().Should().Be("false",
            "agregando, os valores dos grupos se somariam ao do usuário");
        config.GetProperty("access.token.claim").GetString().Should().Be("true");
        config.GetProperty("id.token.claim").GetString().Should().Be("false");
    }

    [Fact]
    public void AudienciaDaGatewaySoNoScopeGatewayApi()
    {
        JsonElement config = ConfigDoMapperUnico("gateway-api", MapperDeAudiencia);

        config.GetProperty("included.custom.audience").GetString().Should().Be("identity-gateway-api");
        config.GetProperty("access.token.claim").GetString().Should().Be("true");
        config.GetProperty("id.token.claim").GetString().Should().Be("false");
        config.TryGetProperty("lightweight.claim", out _).Should().BeFalse(
            "com ele, o token leve do admin-cli do realm ganharia a audiência");

        // Em nenhum outro lugar do realm: nem noutro scope, nem como mapper direto de um client.
        string[] outros =
        [
            .. Percorrer(Realm(), "$")
                .Where(item => item.Caminho.EndsWith(".protocolMapper", StringComparison.Ordinal))
                .Where(item => item.Valor.GetString() == MapperDeAudiencia)
                .Select(item => item.Caminho),
        ];

        outros.Should().ContainSingle("o audience mapper existe só no scope gateway-api");
    }

    [Fact]
    public void TodoClientDeclaraEscoposEFluxos()
    {
        // Client sem defaultClientScopes herda os defaults do realm; sem fullScopeAllowed, vale o padrão (true); sem
        // standardFlowEnabled, também (ligado); com direct grant, é ROPC (ADR-003). Nada disso pode ficar por conta do
        // padrão do Keycloak.
        foreach (JsonElement client in Realm().GetProperty("clients").EnumerateArray())
        {
            string id = client.GetProperty("clientId").GetString()!;

            ChavesObrigatoriasDoClient.Should().OnlyContain(
                chave => Tem(client, chave), $"o client {id} declara as cinco chaves");
            Verdadeiro(client, "directAccessGrantsEnabled").Should().BeFalse($"{id}: sem ROPC");
            Verdadeiro(client, "serviceAccountsEnabled").Should().Be(id == ClientDaGateway,
                "só a Gateway tem service account");
        }

        // DT10: basic (de onde vem o sub) e roles (de onde vem o resource_access que o health check lê); nenhum
        // gateway-*, senão o token do service account passaria na audiência da própria Gateway.
        Textos(Client(ClientDaGateway), "defaultClientScopes").Should().BeEquivalentTo(ScopesDoServiceAccount);
        Textos(Client(ClientDaGateway), "optionalClientScopes").Should().BeEmpty();
        Verdadeiro(Client(ClientDaGateway), "fullScopeAllowed").Should().BeTrue(
            "com false, a Admin API responde 403 e o health check perde o resource_access");
    }

    [Fact]
    public void ClientDeDemonstracaoSoComDeviceFlow()
    {
        JsonElement demo = Client(ClientDeDemonstracao);

        Verdadeiro(demo, "publicClient").Should().BeTrue();
        Verdadeiro(demo, "standardFlowEnabled").Should().BeFalse();
        Verdadeiro(demo, "implicitFlowEnabled").Should().BeFalse();
        Verdadeiro(demo, "fullScopeAllowed").Should().BeFalse("com true, default-roles-* entra no claim roles");
        Textos(demo, "redirectUris").Should().BeEmpty();

        demo.GetProperty("attributes").GetProperty(AtributoDoDeviceFlow).GetString().Should().Be("true");
        demo.GetProperty("attributes").GetProperty("oauth2.device.code.lifespan").GetString().Should().Be("300");

        // basic: sem ele o access token sai sem sub. Sem profile nem email: o token não carrega e-mail nem nome (DT11).
        Textos(demo, "defaultClientScopes").Should().BeEquivalentTo(ScopesDoDemo);
        Textos(demo, "optionalClientScopes").Should().BeEmpty();
    }

    [Fact]
    public void SoOClientDeDemonstracaoTemDeviceFlow()
    {
        string[] comDeviceFlow =
        [
            .. Realm().GetProperty("clients").EnumerateArray()
                .Where(client => client.TryGetProperty("attributes", out JsonElement atributos)
                                 && atributos.TryGetProperty(AtributoDoDeviceFlow, out JsonElement valor)
                                 && valor.GetString() == "true")
                .Select(client => client.GetProperty("clientId").GetString()!),
        ];

        comDeviceFlow.Should().Equal(ClientDeDemonstracao);
    }

    [Fact]
    public void RealmSemGrupos()
    {
        // O mapper do tenant_id recua para o atributo de mesmo nome de um grupo, e não há configuração que desligue
        // isso. Sem grupo no realm, o recuo não tem de onde tirar valor (um grupo criado em runtime é o limite do
        // ADR-011).
        JsonElement realm = Realm();

        realm.TryGetProperty("groups", out _).Should().BeFalse();
        realm.TryGetProperty("defaultGroups", out _).Should().BeFalse();
    }

    [Fact]
    public void OfflineAccessDeclaradoEForaDeTodoClient()
    {
        // O import recria o papel offline_access no papel padrão se faltar o papel OU o scope.
        JsonElement scope = Scope("offline_access");

        scope.TryGetProperty("protocolMappers", out _).Should().BeFalse();

        foreach (JsonElement client in Realm().GetProperty("clients").EnumerateArray())
        {
            Textos(client, "defaultClientScopes").Should().NotContain("offline_access");
            Textos(client, "optionalClientScopes").Should().NotContain("offline_access");
        }
    }

    [Fact]
    public void TemposEProtecoesDoRealm()
    {
        JsonElement realm = Realm();

        realm.GetProperty("accessTokenLifespan").GetInt32().Should().Be(300, "os 5 minutos do ADR-005, travados");
        realm.GetProperty("registrationAllowed").GetBoolean().Should().BeFalse();
        realm.GetProperty("bruteForceProtected").GetBoolean().Should().BeTrue();

        // D-n: um refresh token já usado é recusado.
        realm.GetProperty("revokeRefreshToken").GetBoolean().Should().BeTrue();
        realm.GetProperty("refreshTokenMaxReuse").GetInt32().Should().Be(0);
    }

    [Fact]
    public void PlatformAdminDoBootstrapNasceSemSenhaESoComOPapel()
    {
        // D-h: a conta nasce no JSON, sem credencial; o one-shot só dispara o e-mail e nunca atribui papel.
        JsonElement[] pessoas =
        [
            .. Realm().GetProperty("users").EnumerateArray()
                .Where(usuario => !usuario.TryGetProperty("serviceAccountClientId", out _)),
        ];

        pessoas.Should().ContainSingle("além do service account, só o platform-admin do bootstrap");
        JsonElement admin = pessoas[0];

        admin.GetProperty("username").GetString().Should().Be("${PLATFORM_ADMIN_EMAIL}");
        admin.GetProperty("email").GetString().Should().Be("${PLATFORM_ADMIN_EMAIL}");
        admin.GetProperty("enabled").GetBoolean().Should().BeTrue();
        admin.GetProperty("emailVerified").GetBoolean().Should().BeFalse();
        Textos(admin, "realmRoles").Should().Equal("platform-admin");
        Textos(admin, "requiredActions").Should().BeEquivalentTo(AcoesDoBootstrap);
        ChavesQueOBootstrapNaoTem.Should().NotContain(chave => Tem(admin, chave));
    }
}
