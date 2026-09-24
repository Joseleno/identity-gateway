# Fundação Keycloak — Plano de Implementação

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Pôr o Keycloak 26.7.4 no compose, nos testes e na CI, com o service account da Gateway autenticado por `private_key_jwt`, e entregar a porta `IIdentityProvider.EnsureOrganizationAsync` provada contra o Keycloak real.

**Architecture:** A Application ganha a porta `IIdentityProvider` e a exceção permanente `IdentityProviderInconsistencyException`. Tudo o que conhece o Keycloak mora em `Infrastructure/Identity/Keycloak/`: options, chave, assertion, cliente do token endpoint (sem retry), cache single-flight do token, `DelegatingHandler` do token (401 → invalida e repete uma vez), cliente tipado da Admin API (resiliência por fora, token por dentro), adaptador e health check. O compose ganha dois one-shots (`gateway-keys`, `keycloak-db`) e o Keycloak; a CI ganha um job que sobe o compose e exige o `/health/ready`.

**Tech Stack:** .NET 10, `Microsoft.IdentityModel.JsonWebTokens`, `Microsoft.Extensions.Http.Resilience` 10.10.0, `Microsoft.Extensions.Diagnostics.HealthChecks`, xUnit v3 + AwesomeAssertions + NSubstitute, `Testcontainers.Keycloak` 4.15.0, NetArchTest, Docker Compose, GitHub Actions.

**Spec:** [`docs/superpowers/specs/2026-09-24-fundacao-keycloak-design.md`](../specs/2026-09-24-fundacao-keycloak-design.md) · normativa: [`docs/especificacao-arquitetural-v2.4.md`](../../especificacao-arquitetural-v2.4.md) (§10.2, §11.3, §11.6, §11.8, §13, §14, §15).

## Global Constraints

- Keycloak **26.7.4**, imagem `quay.io/keycloak/keycloak:26.7.4`; realm `identity-gateway`; client `identity-gateway`.
- Busca de Organization: `q=gateway_tenant_id:{id}&briefRepresentation=false&max=2`. Nunca `searchQuery` nem `exact`.
- Assertion: `iss` = `sub` = clientId; `aud` = issuer `{BaseUrl sem barra}/realms/{realm}` como **string única**; `jti` novo por assertion; `iat`/`nbf`/`exp` explícitos com vida **60s**; `RsaSecurityKey` **sem `KeyId`** (nenhum `kid` no header); algoritmo **PS256**.
- Token endpoint **sem retry**; um assertion novo a cada tentativa.
- Ordem no cliente da Admin API: `AddStandardResilienceHandler` **primeiro**, `ServiceAccountTokenHandler` **depois**; `DisableForUnsafeHttpMethods()`.
- Organization: `name` = slug, `alias` = slug, `description` = nome do tenant, atributo `gateway_tenant_id`.
- Service account só com `realm-management` → `manage-organizations`.
- Adaptador `KeycloakIdentityProvider` é **Transient**; cache do token e chave são **Singleton**.
- Options com a chave são `class` (nunca `record`); mensagens de validação **nunca** contêm o valor.
- Logs do Keycloak: `LoggerMessage`, EventIds **2200–2299**; nunca token, assertion, chave nem corpo de requisição/resposta.
- Health check `keycloak`: tag `ready`, `failureStatus: Unhealthy`, obtém token pelo cache.
- Nenhuma credencial literal no repositório: realm só com placeholders `${VAR}` sem default; nenhum `.pem` de teste versionado.
- Convenções do repo: membros privados, variáveis e testes em português (`Metodo_Cenario_Resultado`); comentários explicam o **porquê**; `TreatWarningsAsErrors` está ligado — build com aviso é build quebrado.
- Mensagens de commit no padrão Conventional Commits do repo, **sem nenhuma referência a IA, Claude ou Anthropic** (sem `Co-Authored-By`).
- **Docker precisa estar rodando** para as Tasks 9–13 (Testcontainers e compose).
- `volume.subpath` no compose exige Docker Engine 26+ e Compose 2.23+ (o runner `ubuntu-latest` e o Docker Desktop atual atendem).
- 🧪 = o passo "Prova por mutação" é obrigatório: quebrar o código de propósito, ver o teste ficar vermelho, **reverter** e ver verde de novo. Registrar na mensagem de commit qual mutação foi feita.

## Dois desvios deliberados da letra da spec

- **Relógio:** a spec fala em `TimeProvider`/`FakeTimeProvider`. O plano usa o `IDateTimeProvider` que o repositório já
  tem (substituído por NSubstitute nos testes, como na `PostgresFixture`): mesma função, sem uma segunda abstração de
  relógio no código.
- **"https fora de Development":** implementado pela flag `Keycloak:Admin:AllowInsecureHttp` (padrão `false`, ligada
  só no `appsettings.Development.json`), porque a validação das options roda também em testes de DI sem
  `IHostEnvironment`. O efeito em cada ambiente é o mesmo.

## Review Focus

1. **`BaseUrl` com prefixo de caminho** (Keycloak atrás de proxy em `https://sso.exemplo.com/auth`): issuer e rotas da Admin API precisam preservar o `/auth`. Coberto na Task 1.
2. **PEM com CRLF** (chave colada em user-secrets no Windows): precisa carregar igual. Coberto na Task 1.
3. **Token com `expires_in` menor que a margem de 30s**: a chamada que o obteve usa o token; a seguinte busca outro — sem laço, sem erro. Coberto na Task 4.
4. **Keycloak que aceita a conexão e não responde**: o `ready` precisa voltar `Unhealthy` dentro do timeout do check, sem pendurar a sonda. Coberto na Task 7.
5. **Nome de tenant com acento, aspas e `&`**: chega intacto na `description`. Coberto na Task 10.

---

## Estrutura de arquivos

**Application**
- Create `src/IdentityGateway.Application/Common/Abstractions/IIdentityProvider.cs` — a porta.
- Create `src/IdentityGateway.Application/Common/Abstractions/IdentityProviderInconsistencyException.cs` — erro permanente.

**Infrastructure** (`src/IdentityGateway.Infrastructure/Identity/Keycloak/`)
- `KeycloakAdminOptions.cs` — configuração, issuer e endereço base.
- `GatewaySigningKey.cs` — importa a chave RSA uma vez.
- `ClientAssertionFactory.cs` — monta o assertion.
- `ITokenEndpoint.cs` — contrato interno do token endpoint + `TokenObtido`.
- `KeycloakTokenClient.cs` — chama o token endpoint.
- `ServiceAccountTokenCache.cs` — cache single-flight.
- `ServiceAccountTokenHandler.cs` — põe o token; 401 → repete uma vez.
- `OrganizationRepresentation.cs` — DTO da Admin API + `KeycloakConflictException`.
- `KeycloakAdminClient.cs` — cliente tipado da Admin API.
- `KeycloakIdentityProvider.cs` — adaptador da porta.
- `KeycloakHealthCheck.cs` — health check.
- `KeycloakLogs.cs` — mensagens de log.
- `KeycloakServiceCollectionExtensions.cs` — registro.
- Modify `src/IdentityGateway.Infrastructure/DependencyInjection.cs`, `Configuration/HttpResilienceOptions.cs`, `IdentityGateway.Infrastructure.csproj`.

**Api**
- Modify `src/IdentityGateway.Api/appsettings.json`, `appsettings.Development.json`.

**Realm e ambiente**
- Create `keycloak/bootstrap/realm-identity-gateway.json`.
- Modify `docker-compose.yml`, `.github/workflows/ci.yml`, `README.md`, `Directory.Packages.props`.

**Testes**
- `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/` — `ChavesDeTeste.cs`, `OpcoesDeTeste.cs`, `HandlerFalso.cs`, `HandlerDeInterceptacao.cs`, `KeycloakFixture.cs`, e as classes de teste de cada task.
- Modify `tests/IdentityGateway.Infrastructure.IntegrationTests/DependencyInjectionTests.cs`, `.csproj`.
- Modify `tests/IdentityGateway.Api.FunctionalTests/IdentityGatewayApiFactory.cs`, `SegurancaTests.cs`.
- Create `tests/IdentityGateway.ArchitectureTests/RegrasDoKeycloakTests.cs`, `RegrasDoRealmTests.cs`, `RaizDoRepositorio.cs`.

**Documentos**
- Modify `src/IdentityGateway.Application/Tenants/RegisterTenant/RegisterTenantCommand.cs` (comentário falso).
- Create `docs/superpowers/specs/2026-09-2X-fundacao-keycloak-handoff.md` (data do dia da entrega).

---

### Task 1: Pacotes, options do Keycloak e chave da Gateway

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `src/IdentityGateway.Infrastructure/IdentityGateway.Infrastructure.csproj`
- Modify: `src/IdentityGateway.Infrastructure/Configuration/HttpResilienceOptions.cs`
- Modify: `src/IdentityGateway.Infrastructure/DependencyInjection.cs` (método `AddOptionsValidadas`)
- Create: `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakAdminOptions.cs`
- Create: `src/IdentityGateway.Infrastructure/Identity/Keycloak/GatewaySigningKey.cs`
- Create: `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakServiceCollectionExtensions.cs`
- Modify: `tests/IdentityGateway.Infrastructure.IntegrationTests/IdentityGateway.Infrastructure.IntegrationTests.csproj`
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/ChavesDeTeste.cs`
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakAdminOptionsTests.cs`

**Interfaces:**
- Produces: `internal sealed class KeycloakAdminOptions { const string SectionName = "Keycloak:Admin"; string BaseUrl; string Realm; string ClientId; string? PrivateKeyPath; string? PrivateKeyPem; bool AllowInsecureHttp; string Issuer; Uri AdminBaseAddress; }`
- Produces: `internal sealed class GatewaySigningKey : IDisposable { RSA Rsa { get; } static RSA Carregar(KeycloakAdminOptions); }`
- Produces: `internal static IServiceCollection AddKeycloakIdentity(this IServiceCollection, IConfiguration)` (cresce nas Tasks 3–7).
- Produces (teste): `internal static ParDeChaves ChavesDeTeste.Gerar()`, `internal sealed record ParDeChaves(RSA Rsa, string PemPrivado, string CertificadoBase64)`.

> **Nota sobre "https fora de Development".** A spec pede que `BaseUrl` sem https seja recusada fora de `Development`. O mecanismo é a flag `AllowInsecureHttp` (padrão `false`), ligada só no `appsettings.Development.json`: a validação de options roda também em testes de DI sem `IHostEnvironment` registrado, e a flag dá o mesmo resultado sem depender do host.

- [ ] **Step 1: Adicionar os pacotes ao `Directory.Packages.props`**

Descobrir a versão de `Microsoft.IdentityModel.JsonWebTokens` que o `JwtBearer` já traz, para não criar conflito de versão:

Run: `dotnet list src/IdentityGateway.Api/IdentityGateway.Api.csproj package --include-transitive | Select-String "Microsoft.IdentityModel.JsonWebTokens"`
Expected: uma linha com a versão resolvida (ex.: `8.x.y`). Use **essa** versão abaixo.

Acrescentar, junto das demais `PackageVersion`:

```xml
    <PackageVersion Include="Microsoft.IdentityModel.JsonWebTokens" Version="<versão da linha acima>" />
    <PackageVersion Include="Microsoft.Extensions.Diagnostics.HealthChecks" Version="10.0.12" />
    <PackageVersion Include="Testcontainers.Keycloak" Version="4.15.0" />
```

`Microsoft.Extensions.Http.Resilience` 10.10.0 já está no arquivo.

- [ ] **Step 2: Referenciar os pacotes na Infrastructure e nos testes de integração**

Em `src/IdentityGateway.Infrastructure/IdentityGateway.Infrastructure.csproj`, depois do `ItemGroup` de configuração validada, acrescentar:

```xml
  <!--
    Keycloak (Identity/Keycloak): o client assertion do private_key_jwt é montado com o mesmo stack de JWT que a
    Api usa para validar tokens; a resiliência é a do .NET 10 (Polly v8 por baixo); o health check do Keycloak
    mora aqui porque depende do cache do token, que é interno a esta camada.
  -->
  <ItemGroup>
    <PackageReference Include="Microsoft.IdentityModel.JsonWebTokens" />
    <PackageReference Include="Microsoft.Extensions.Http.Resilience" />
    <PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks" />
  </ItemGroup>
```

Em `tests/IdentityGateway.Infrastructure.IntegrationTests/IdentityGateway.Infrastructure.IntegrationTests.csproj`, dentro do `ItemGroup` do Testcontainers, acrescentar:

```xml
    <PackageReference Include="Testcontainers.Keycloak" />
    <PackageReference Include="Microsoft.IdentityModel.JsonWebTokens" />
```

Run: `dotnet restore`
Expected: sucesso, sem `NU1605`/`NU1608` (downgrade/conflito).

- [ ] **Step 3: Escrever os testes das options (falham: os tipos não existem)**

Create `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/ChavesDeTeste.cs`:

```csharp
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// Par de chaves da Gateway gerado em memória para o teste.
/// </summary>
/// <remarks>
/// <b>Nunca um <c>.pem</c> versionado.</b> O repositório é público, e o secret scanning do GitHub dispara em chave
/// privada commitada — mesmo de teste. Gerar a cada execução custa milissegundos.
/// </remarks>
internal static class ChavesDeTeste
{
    public static ParDeChaves Gerar()
    {
        RSA rsa = RSA.Create(2048);

        CertificateRequest pedido = new(
            "CN=identity-gateway", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        using X509Certificate2 certificado = pedido.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        // DER em base64 numa linha só: é o formato que o placeholder do realm espera — o import substitui o texto
        // antes do parse do JSON, e uma quebra de linha no valor quebraria o JSON.
        return new ParDeChaves(
            rsa,
            rsa.ExportPkcs8PrivateKeyPem(),
            Convert.ToBase64String(certificado.Export(X509ContentType.Cert)));
    }
}

/// <summary>A chave, sua forma PEM (o que a Gateway lê) e o certificado (o que o Keycloak registra).</summary>
internal sealed record ParDeChaves(RSA Rsa, string PemPrivado, string CertificadoBase64);
```

Create `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakAdminOptionsTests.cs`:

```csharp
using IdentityGateway.Infrastructure.Identity.Keycloak;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// A configuração do Keycloak é barrada na subida quando está incompleta, ambígua ou insegura.
/// </summary>
public sealed class KeycloakAdminOptionsTests
{
    private static readonly string PemValido = ChavesDeTeste.Gerar().PemPrivado;

    private static KeycloakAdminOptions Resolver(Dictionary<string, string?> valores)
    {
        IConfiguration configuracao = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();

        ServiceCollection services = new();
        services.AddKeycloakIdentity(configuracao);

        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<KeycloakAdminOptions>>().Value;
    }

    private static Dictionary<string, string?> Validos() => new()
    {
        ["Keycloak:Admin:BaseUrl"] = "https://sso.exemplo.test",
        ["Keycloak:Admin:Realm"] = "identity-gateway",
        ["Keycloak:Admin:ClientId"] = "identity-gateway",
        ["Keycloak:Admin:PrivateKeyPem"] = PemValido,
    };

    [Fact]
    public void ConfiguracaoCompleta_ExpoeIssuerEEnderecoBase()
    {
        KeycloakAdminOptions opcoes = Resolver(Validos());

        opcoes.Issuer.Should().Be("https://sso.exemplo.test/realms/identity-gateway");
        opcoes.AdminBaseAddress.Should().Be(new Uri("https://sso.exemplo.test/"));
    }

    [Fact]
    public void BaseUrlComPrefixoDeCaminhoEBarraFinal_PreservaOPrefixo()
    {
        // Keycloak atrás de proxy reverso em /auth: perder o prefixo mandaria o assertion e a Admin API para a
        // raiz do host, e o resultado seria 404 — ou pior, o issuer errado e invalid_client.
        Dictionary<string, string?> valores = Validos();
        valores["Keycloak:Admin:BaseUrl"] = "https://sso.exemplo.test/auth/";

        KeycloakAdminOptions opcoes = Resolver(valores);

        opcoes.Issuer.Should().Be("https://sso.exemplo.test/auth/realms/identity-gateway");
        opcoes.AdminBaseAddress.Should().Be(new Uri("https://sso.exemplo.test/auth/"));
    }

    [Fact]
    public void SemASecao_FalhaAoValidar()
    {
        Action resolver = () => Resolver([]);

        resolver.Should().Throw<OptionsValidationException>().WithMessage("*BaseUrl*");
    }

    [Fact]
    public void SemNenhumaChave_FalhaAoValidar()
    {
        Dictionary<string, string?> valores = Validos();
        valores.Remove("Keycloak:Admin:PrivateKeyPem");

        Action resolver = () => Resolver(valores);

        resolver.Should().Throw<OptionsValidationException>().WithMessage("*exatamente um*");
    }

    [Fact]
    public void ComAsDuasChaves_FalhaAoValidar()
    {
        // Duas fontes para o mesmo segredo: qual vence seria detalhe de implementação, e quem trocou a chave num
        // lugar continuaria usando a do outro sem saber.
        Dictionary<string, string?> valores = Validos();
        valores["Keycloak:Admin:PrivateKeyPath"] = "/keys/private.pem";

        Action resolver = () => Resolver(valores);

        resolver.Should().Throw<OptionsValidationException>().WithMessage("*exatamente um*");
    }

    [Fact]
    public void ArquivoDeChaveInexistente_FalhaAoValidarSemExporOCaminhoNemOValor()
    {
        Dictionary<string, string?> valores = Validos();
        valores.Remove("Keycloak:Admin:PrivateKeyPem");
        valores["Keycloak:Admin:PrivateKeyPath"] = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pem");

        Action resolver = () => Resolver(valores);

        resolver.Should().Throw<OptionsValidationException>()
            .WithMessage("*chave privada*")
            .Which.Message.Should().NotContain(valores["Keycloak:Admin:PrivateKeyPath"]);
    }

    [Fact]
    public void PemInvalido_FalhaAoValidarSemExporOValor()
    {
        Dictionary<string, string?> valores = Validos();
        valores["Keycloak:Admin:PrivateKeyPem"] = "-----BEGIN PRIVATE KEY-----\nisto-nao-e-chave\n-----END PRIVATE KEY-----";

        Action resolver = () => Resolver(valores);

        resolver.Should().Throw<OptionsValidationException>()
            .WithMessage("*chave privada*")
            .Which.Message.Should().NotContain("isto-nao-e-chave");
    }

    [Fact]
    public void PemComQuebrasDeLinhaDoWindows_Carrega()
    {
        // Chave colada nos user-secrets no Windows chega com CRLF. Precisa carregar igual.
        Dictionary<string, string?> valores = Validos();
        valores["Keycloak:Admin:PrivateKeyPem"] = PemValido.ReplaceLineEndings("\r\n");

        KeycloakAdminOptions opcoes = Resolver(valores);

        using var chave = GatewaySigningKey.Carregar(opcoes);
        chave.KeySize.Should().Be(2048);
    }

    [Fact]
    public void HttpSemPermissao_FalhaAoValidar()
    {
        Dictionary<string, string?> valores = Validos();
        valores["Keycloak:Admin:BaseUrl"] = "http://keycloak:8080";

        Action resolver = () => Resolver(valores);

        resolver.Should().Throw<OptionsValidationException>().WithMessage("*https*");
    }

    [Fact]
    public void HttpComPermissaoDeDesenvolvimento_Aceita()
    {
        Dictionary<string, string?> valores = Validos();
        valores["Keycloak:Admin:BaseUrl"] = "http://keycloak:8080";
        valores["Keycloak:Admin:AllowInsecureHttp"] = "true";

        KeycloakAdminOptions opcoes = Resolver(valores);

        opcoes.Issuer.Should().Be("http://keycloak:8080/realms/identity-gateway");
    }
}
```

- [ ] **Step 4: Rodar e ver falhar**

Run: `dotnet build tests/IdentityGateway.Infrastructure.IntegrationTests`
Expected: FAIL — `KeycloakAdminOptions`, `GatewaySigningKey` e `AddKeycloakIdentity` não existem.

- [ ] **Step 5: Implementar `KeycloakAdminOptions`**

Create `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakAdminOptions.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// Como a Gateway alcança a Admin API do Keycloak e se autentica nela.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>class</c>, e não <c>record</c>, de propósito.</b> O <c>ToString()</c> que o compilador gera para um record
/// imprime todas as propriedades — inclusive <see cref="PrivateKeyPem"/>. Um log de diagnóstico que fizesse
/// <c>{options}</c> vazaria a chave privada da Gateway.
/// </para>
/// <para>
/// <b>A chave vem por arquivo ou por PEM, nunca pelos dois.</b> <see cref="PrivateKeyPath"/> é o preferido: segredo
/// montado como arquivo não aparece em <c>docker inspect</c> nem em <c>/proc/*/environ</c>, e é assim que cofres e
/// orquestradores entregam segredo. <see cref="PrivateKeyPem"/> existe para user-secrets e testes.
/// </para>
/// </remarks>
internal sealed class KeycloakAdminOptions
{
    /// <summary>Seção correspondente no arquivo de configuração.</summary>
    public const string SectionName = "Keycloak:Admin";

    /// <summary>Endereço do Keycloak como a Gateway o alcança, com eventual prefixo de caminho.</summary>
    /// <remarks>
    /// Precisa ser <b>o mesmo</b> endereço que o Keycloak usa para calcular o próprio issuer: o <c>aud</c> do
    /// assertion é derivado daqui. Em produção, igual ao <c>KC_HOSTNAME</c>.
    /// </remarks>
    [Required(ErrorMessage = "Keycloak:Admin:BaseUrl é obrigatório.")]
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Realm da Gateway. Nunca o <c>master</c>.</summary>
    [Required(ErrorMessage = "Keycloak:Admin:Realm é obrigatório.")]
    public string Realm { get; init; } = string.Empty;

    /// <summary>ClientId do client confidencial da Gateway.</summary>
    [Required(ErrorMessage = "Keycloak:Admin:ClientId é obrigatório.")]
    public string ClientId { get; init; } = string.Empty;

    /// <summary>Caminho do arquivo PEM com a chave privada RSA.</summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>A chave privada RSA em PEM, inline.</summary>
    public string? PrivateKeyPem { get; init; }

    /// <summary>Permite <c>http://</c>. Ligado só no ambiente de desenvolvimento.</summary>
    /// <remarks>
    /// Fora do desenvolvimento, o assertion e o token do service account trafegariam em claro — e o token dá
    /// <c>manage-organizations</c> sobre o realm inteiro.
    /// </remarks>
    public bool AllowInsecureHttp { get; init; }

    /// <summary>Issuer do realm, sem barra final. É o <c>aud</c> do client assertion.</summary>
    public string Issuer => $"{BaseUrl.TrimEnd('/')}/realms/{Realm}";

    /// <summary>Endereço base dos clientes HTTP, com barra final.</summary>
    /// <remarks>
    /// A barra final não é estética: sem ela, <c>new Uri(base, "admin/realms/...")</c> descarta o último segmento
    /// do caminho — e um Keycloak em <c>/auth</c> seria chamado na raiz.
    /// </remarks>
    public Uri AdminBaseAddress => new($"{BaseUrl.TrimEnd('/')}/");
}
```

- [ ] **Step 6: Implementar `GatewaySigningKey`**

Create `src/IdentityGateway.Infrastructure/Identity/Keycloak/GatewaySigningKey.cs`:

```csharp
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// A chave privada da Gateway, importada uma única vez.
/// </summary>
/// <remarks>
/// Singleton: ler e decodificar o PEM a cada assertion seria trabalho repetido, e manter a string PEM em memória
/// por toda a vida do processo seria uma cópia a mais do segredo. Aqui a string é lida, importada e descartada;
/// fica só o objeto <see cref="RSA"/>.
/// </remarks>
internal sealed class GatewaySigningKey(IOptions<KeycloakAdminOptions> options) : IDisposable
{
    /// <summary>A chave, pronta para assinar.</summary>
    public RSA Rsa { get; } = Carregar(options.Value);

    /// <summary>
    /// Lê a chave do arquivo ou do PEM inline e a importa.
    /// </summary>
    /// <exception cref="IOException">Arquivo ilegível.</exception>
    /// <exception cref="ArgumentException">Conteúdo sem bloco PEM reconhecível.</exception>
    /// <exception cref="CryptographicException">Bloco PEM que não é uma chave RSA válida.</exception>
    public static RSA Carregar(KeycloakAdminOptions opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);

        string pem = string.IsNullOrWhiteSpace(opcoes.PrivateKeyPath)
            ? opcoes.PrivateKeyPem ?? string.Empty
            : File.ReadAllText(opcoes.PrivateKeyPath);

        RSA rsa = RSA.Create();

        try
        {
            rsa.ImportFromPem(pem);
            return rsa;
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Se a chave configurada pode ser carregada — usado pela validação na subida.
    /// </summary>
    /// <remarks>
    /// Quando a regra "exatamente uma fonte" já está violada, devolve <c>true</c> e deixa a mensagem para ela: duas
    /// mensagens para o mesmo erro confundem mais do que ajudam.
    /// </remarks>
    public static bool EhLegivel(KeycloakAdminOptions opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);

        if (!TemExatamenteUmaFonte(opcoes))
        {
            return true;
        }

        try
        {
            using RSA _ = Carregar(opcoes);
            return true;
        }
        catch (Exception excecao) when (excecao is IOException or UnauthorizedAccessException
                                            or ArgumentException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Exatamente uma entre <c>PrivateKeyPath</c> e <c>PrivateKeyPem</c>.</summary>
    public static bool TemExatamenteUmaFonte(KeycloakAdminOptions opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);

        return string.IsNullOrWhiteSpace(opcoes.PrivateKeyPath) != string.IsNullOrWhiteSpace(opcoes.PrivateKeyPem);
    }

    public void Dispose() => Rsa.Dispose();
}
```

- [ ] **Step 7: Criar `AddKeycloakIdentity` com as options validadas**

Create `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// Registra a integração com o Keycloak. Único ponto por onde o resto da Infrastructure a alcança.
/// </summary>
/// <remarks>
/// Há teste de arquitetura garantindo que nenhum tipo deste namespace é usado fora dele, exceto por
/// <c>DependencyInjection</c> — que chama este método e nada mais (ADR-008).
/// </remarks>
internal static class KeycloakServiceCollectionExtensions
{
    internal static IServiceCollection AddKeycloakIdentity(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // As mensagens nunca incluem o valor: o que está sendo validado é, entre outras coisas, uma chave privada.
        services.AddOptions<KeycloakAdminOptions>()
            .Bind(configuration.GetSection(KeycloakAdminOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                BaseUrlAceitavel,
                "Keycloak:Admin:BaseUrl precisa ser uma URL absoluta https (http só com AllowInsecureHttp, em "
                + "desenvolvimento).")
            .Validate(
                GatewaySigningKey.TemExatamenteUmaFonte,
                "Keycloak:Admin: informe exatamente um entre PrivateKeyPath e PrivateKeyPem.")
            .Validate(
                GatewaySigningKey.EhLegivel,
                "Keycloak:Admin: a chave privada não pôde ser lida como RSA em PEM (arquivo ausente, sem permissão "
                + "ou conteúdo inválido).")
            .ValidateOnStart();

        services.AddSingleton<GatewaySigningKey>();

        return services;
    }

    private static bool BaseUrlAceitavel(KeycloakAdminOptions opcoes)
    {
        if (!Uri.TryCreate(opcoes.BaseUrl, UriKind.Absolute, out Uri? endereco))
        {
            // Vazia ou relativa: o [Required] cobre a vazia, e aqui não se repete a mensagem.
            return string.IsNullOrWhiteSpace(opcoes.BaseUrl);
        }

        return endereco.Scheme == Uri.UriSchemeHttps
               || (endereco.Scheme == Uri.UriSchemeHttp && opcoes.AllowInsecureHttp);
    }
}
```

- [ ] **Step 8: Registrar `HttpResilienceOptions` e corrigir o que o comentário dela diz**

Em `src/IdentityGateway.Infrastructure/DependencyInjection.cs`, dentro de `AddOptionsValidadas`, depois do bloco de `OutboxOptions`, acrescentar:

```csharp
        // Política do cliente da Admin API do Keycloak — o consumidor que o comentário da classe esperava.
        services.AddOptions<HttpResilienceOptions>()
            .Bind(configuration.GetSection(HttpResilienceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
```

Em `src/IdentityGateway.Infrastructure/Configuration/HttpResilienceOptions.cs`:
- trocar o parágrafo que começa em `<b>Ainda não está registrada no contêiner.</b>` e o parágrafo seguinte `<b>Ao cabear o cliente</b>...` por:

```csharp
/// <para>
/// <b>Consumida pelo cliente da Admin API do Keycloak</b> (<c>Identity/Keycloak</c>). A ordem do pipeline importa:
/// timeout total por fora, retry dentro dele, circuit breaker dentro do retry, timeout por tentativa no centro — é a
/// ordem do <c>AddStandardResilienceHandler</c>. O <c>HttpClient.Timeout</c> fica em <c>InfiniteTimeSpan</c>: ele
/// cancelaria no meio do pipeline, com um cancelamento indistinguível do que parte do usuário.
/// </para>
```

- trocar o `[Range(0, 10, ...)]` de `MaxRetryAttempts` por `[Range(1, 10, ErrorMessage = "O número de tentativas deve estar entre 1 e 10.")]` e acrescentar ao `<remarks>` dela: `O mínimo é 1 porque o pipeline padrão recusa zero; para não repetir, o lugar é desligar o retry no cliente, não zerar a política de todos.`

- [ ] **Step 9: Rodar os testes e ver passar**

Run: `dotnet test --project tests/IdentityGateway.Infrastructure.IntegrationTests/IdentityGateway.Infrastructure.IntegrationTests.csproj --filter-class "*KeycloakAdminOptionsTests"`
Expected: PASS (10 testes).

- [ ] **Step 10: 🧪 Prova por mutação**

1. Em `KeycloakAdminOptions.Issuer`, trocar `BaseUrl.TrimEnd('/')` por `BaseUrl` → `BaseUrlComPrefixoDeCaminhoEBarraFinal_PreservaOPrefixo` fica vermelho. Reverter.
2. Em `BaseUrlAceitavel`, trocar `&& opcoes.AllowInsecureHttp` por `|| true` → `HttpSemPermissao_FalhaAoValidar` fica vermelho. Reverter.

Rodar de novo o Step 9: PASS.

- [ ] **Step 11: Build da solução inteira**

Run: `dotnet build`
Expected: 0 avisos, 0 erros.

- [ ] **Step 12: Commit**

```bash
git add Directory.Packages.props src/IdentityGateway.Infrastructure tests/IdentityGateway.Infrastructure.IntegrationTests
git commit -m "feat: options do Keycloak e chave da Gateway, validadas na subida" -m "Mutacoes: Issuer sem TrimEnd e http sem AllowInsecureHttp, ambas pegas."
```

---

### Task 2: Client assertion do `private_key_jwt`

**Files:**
- Create: `src/IdentityGateway.Infrastructure/Identity/Keycloak/ClientAssertionFactory.cs`
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/OpcoesDeTeste.cs`
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/ClientAssertionFactoryTests.cs`
- Modify: `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakServiceCollectionExtensions.cs`

**Interfaces:**
- Consumes: `KeycloakAdminOptions`, `GatewaySigningKey` (Task 1); `IDateTimeProvider` (`IdentityGateway.Application.Common.Abstractions`, propriedade `DateTimeOffset UtcNow`).
- Produces: `internal sealed class ClientAssertionFactory(GatewaySigningKey, IOptions<KeycloakAdminOptions>, IDateTimeProvider) { static readonly TimeSpan Vida; string Criar(); }`
- Produces (teste): `internal static class OpcoesDeTeste { IOptions<KeycloakAdminOptions> Keycloak(string baseUrl = "http://keycloak.test:8080", string? pem = null); }`

- [ ] **Step 1: Escrever os testes (falham: `ClientAssertionFactory` não existe)**

Create `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/OpcoesDeTeste.cs`:

```csharp
using IdentityGateway.Infrastructure.Identity.Keycloak;
using Microsoft.Extensions.Options;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>Options do Keycloak para testes que montam as peças à mão, sem contêiner de DI.</summary>
internal static class OpcoesDeTeste
{
    public static IOptions<KeycloakAdminOptions> Keycloak(
        string baseUrl = "http://keycloak.test:8080", string? pem = null) =>
        Options.Create(new KeycloakAdminOptions
        {
            BaseUrl = baseUrl,
            Realm = "identity-gateway",
            ClientId = "identity-gateway",
            PrivateKeyPem = pem ?? ChavesDeTeste.Gerar().PemPrivado,
            AllowInsecureHttp = true,
        });
}
```

Create `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/ClientAssertionFactoryTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Text.Json;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O assertion do <c>private_key_jwt</c> tem exatamente a forma que o Keycloak 26.7 aceita — e nada que ele recuse
/// em silêncio.
/// </summary>
public sealed class ClientAssertionFactoryTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    // Issuer LITERAL, e não recalculado pela fórmula do código: um teste que reusa a fórmula aprova a fórmula
    // errada. A BaseUrl termina em barra de propósito — é o caso que produziria "//realms".
    private const string IssuerEsperado = "http://keycloak.test:8080/realms/identity-gateway";

    private readonly ParDeChaves _chaves = ChavesDeTeste.Gerar();

    private ClientAssertionFactory CriarFabrica()
    {
        var opcoes = OpcoesDeTeste.Keycloak("http://keycloak.test:8080/", _chaves.PemPrivado);
        IDateTimeProvider relogio = Substitute.For<IDateTimeProvider>();
        relogio.UtcNow.Returns(Agora);

        return new ClientAssertionFactory(new GatewaySigningKey(opcoes), opcoes, relogio);
    }

    private static JsonElement Payload(string assertion)
    {
        JsonWebToken token = new(assertion);
        return JsonDocument.Parse(Base64UrlEncoder.Decode(token.EncodedPayload)).RootElement.Clone();
    }

    [Fact]
    public void Criar_AudEStringUnicaIgualAoIssuer()
    {
        JsonElement payload = Payload(CriarFabrica().Criar());

        // String, e não array: o Keycloak 26.2+ recusa aud com mais de um valor, e um array de um elemento só está
        // a uma linha de virar dois.
        payload.GetProperty("aud").ValueKind.Should().Be(JsonValueKind.String);
        payload.GetProperty("aud").GetString().Should().Be(IssuerEsperado);
    }

    [Fact]
    public void Criar_IssESubSaoOClientId()
    {
        JsonElement payload = Payload(CriarFabrica().Criar());

        payload.GetProperty("iss").GetString().Should().Be("identity-gateway");
        payload.GetProperty("sub").GetString().Should().Be("identity-gateway");
    }

    [Fact]
    public void Criar_VidaDeSessentaSegundosAPartirDoRelogio()
    {
        // O padrão do JsonWebTokenHandler é 60 MINUTOS. O Keycloak aceitaria — ele só confere a idade pelo iat —, e
        // um assertion vazado valeria uma hora contra qualquer outro verificador.
        JsonElement payload = Payload(CriarFabrica().Criar());

        long iat = payload.GetProperty("iat").GetInt64();
        payload.GetProperty("exp").GetInt64().Should().Be(iat + 60);
        payload.GetProperty("nbf").GetInt64().Should().Be(iat);
        iat.Should().Be(Agora.ToUnixTimeSeconds());
    }

    [Fact]
    public void Criar_JtiNovoACadaChamada()
    {
        ClientAssertionFactory fabrica = CriarFabrica();

        string primeiro = Payload(fabrica.Criar()).GetProperty("jti").GetString()!;
        string segundo = Payload(fabrica.Criar()).GetProperty("jti").GetString()!;

        // O Keycloak guarda o jti num cache de uso único: repetir é "Token reuse detected".
        primeiro.Should().NotBe(segundo);
    }

    [Fact]
    public void Criar_HeaderSemKidEComPs256()
    {
        JsonWebToken token = new(CriarFabrica().Criar());

        // Com kid no header, o Keycloak exige que ele bata com o SHA-256 da chave pública; o .NET, com
        // X509SecurityKey, poria o thumbprint SHA-1. Sem kid, o Keycloak usa o certificado padrão do client.
        token.TryGetHeaderValue("kid", out string _).Should().BeFalse();
        token.Alg.Should().Be("PS256");
    }

    [Fact]
    public async Task Criar_AssinaturaConfereComAChavePublica()
    {
        using RSA publica = RSA.Create();
        publica.ImportParameters(_chaves.Rsa.ExportParameters(includePrivateParameters: false));

        TokenValidationResult resultado = await new JsonWebTokenHandler().ValidateTokenAsync(
            CriarFabrica().Criar(),
            new TokenValidationParameters
            {
                IssuerSigningKey = new RsaSecurityKey(publica),
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = false,
            });

        resultado.IsValid.Should().BeTrue(resultado.Exception?.Message);
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet build tests/IdentityGateway.Infrastructure.IntegrationTests`
Expected: FAIL — `ClientAssertionFactory` não existe.

- [ ] **Step 3: Implementar**

Create `src/IdentityGateway.Infrastructure/Identity/Keycloak/ClientAssertionFactory.cs`:

```csharp
using IdentityGateway.Application.Common.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// Monta o client assertion do <c>private_key_jwt</c> (RFC 7523): a prova de posse da chave que a Gateway apresenta
/// ao token endpoint no lugar de um segredo.
/// </summary>
/// <remarks>
/// <para>Cada campo tem um motivo verificado no código do Keycloak 26.7.4 (spec v2.4, §10.2):</para>
/// <list type="bullet">
///   <item><b><c>aud</c> = issuer, string única.</b> Aceito sempre e recomendado desde a 26.2; <c>aud</c> com mais de
///   um valor é recusado.</item>
///   <item><b><c>jti</c> novo.</b> O Keycloak o exige e o guarda num cache de uso único.</item>
///   <item><b>Tempos explícitos, 60s.</b> Deixados à biblioteca, seriam 60 minutos.</item>
///   <item><b><see cref="RsaSecurityKey"/> sem <c>KeyId</c>.</b> Nenhum <c>kid</c> sai no header, e o Keycloak usa o
///   certificado padrão do client. Um <c>kid</c> calculado pelo .NET não bateria com o do Keycloak.</item>
///   <item><b>PS256</b>, igual ao fixado no client do realm.</item>
/// </list>
/// </remarks>
internal sealed class ClientAssertionFactory(
    GatewaySigningKey chave,
    IOptions<KeycloakAdminOptions> options,
    IDateTimeProvider relogio)
{
    /// <summary>Vida do assertion.</summary>
    internal static readonly TimeSpan Vida = TimeSpan.FromSeconds(60);

    // Thread-safe e sem estado por token: uma instância serve a todos.
    private static readonly JsonWebTokenHandler Emissor = new();

    /// <summary>Um assertion novo, com <c>jti</c> próprio.</summary>
    public string Criar()
    {
        KeycloakAdminOptions opcoes = options.Value;
        DateTime agora = relogio.UtcNow.UtcDateTime;

        SecurityTokenDescriptor descritor = new()
        {
            Issuer = opcoes.ClientId,

            // Só aqui, e nunca também em Claims["aud"]: as duas fontes juntas viram um array.
            Audience = opcoes.Issuer,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = opcoes.ClientId,
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString(),
            },
            IssuedAt = agora,
            NotBefore = agora,
            Expires = agora + Vida,
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(chave.Rsa), SecurityAlgorithms.RsaSsaPssSha256),
        };

        return Emissor.CreateToken(descritor);
    }
}
```

Em `KeycloakServiceCollectionExtensions.AddKeycloakIdentity`, depois de `services.AddSingleton<GatewaySigningKey>();`:

```csharp
        services.AddSingleton<ClientAssertionFactory>();
```

- [ ] **Step 4: Rodar e ver passar**

Run: `dotnet test --project tests/IdentityGateway.Infrastructure.IntegrationTests/IdentityGateway.Infrastructure.IntegrationTests.csproj --filter-class "*ClientAssertionFactoryTests"`
Expected: PASS (6 testes).

- [ ] **Step 5: 🧪 Prova por mutação**

1. Trocar `new RsaSecurityKey(chave.Rsa)` por `new RsaSecurityKey(chave.Rsa) { KeyId = "x" }` → `Criar_HeaderSemKidEComPs256` vermelho. Reverter.
2. Remover a linha `Expires = agora + Vida,` → `Criar_VidaDeSessentaSegundosAPartirDoRelogio` vermelho. Reverter.
3. Acrescentar `[JwtRegisteredClaimNames.Aud] = opcoes.Issuer,` ao dicionário de `Claims` → `Criar_AudEStringUnicaIgualAoIssuer` vermelho. Reverter.

Rodar o Step 4 de novo: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/IdentityGateway.Infrastructure/Identity/Keycloak tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak
git commit -m "feat: client assertion do private_key_jwt, sem kid e com vida de 60s" -m "Mutacoes: kid presente, Expires ausente e aud duplicado, todas pegas."
```

---

### Task 3: Cliente do token endpoint (sem retry)

**Files:**
- Create: `src/IdentityGateway.Infrastructure/Identity/Keycloak/ITokenEndpoint.cs`
- Create: `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakTokenClient.cs`
- Create: `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakLogs.cs`
- Modify: `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakServiceCollectionExtensions.cs`
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/HandlerFalso.cs`
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakTokenClientTests.cs`

**Interfaces:**
- Consumes: `ClientAssertionFactory.Criar()` (Task 2), `KeycloakAdminOptions.Issuer`/`ClientId` (Task 1).
- Produces: `internal interface ITokenEndpoint { Task<TokenObtido> ObterAsync(CancellationToken cancellationToken); }`, `internal sealed record TokenObtido(string AccessToken, TimeSpan ExpiraEm);`
- Produces: `internal sealed class KeycloakTokenClient(IHttpClientFactory, IOptions<KeycloakAdminOptions>, ClientAssertionFactory, ILogger<KeycloakTokenClient>) : ITokenEndpoint { const string NomeDoCliente = "keycloak-token"; }`
- Produces: `internal static partial class KeycloakLogs` (EventIds 2200–2299; Task 3 cria `TokenRecusado` = 2204; a Task 6 acrescenta 2200–2203).
- Produces (teste): `internal sealed class HandlerFalso(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>) : HttpMessageHandler { int Chamadas; }`

- [ ] **Step 1: Escrever os testes (falham: os tipos não existem)**

Create `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/HandlerFalso.cs`:

```csharp
namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// Handler HTTP que responde o que o teste mandar, contando as chamadas.
/// </summary>
/// <remarks>
/// Para testar as peças sem Keycloak. O comportamento real do Keycloak é provado nos testes contra o container
/// (<c>KeycloakFixture</c>); aqui se prova o que o NOSSO código faz com cada resposta.
/// </remarks>
internal sealed class HandlerFalso(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    private int _chamadas;

    public int Chamadas => Volatile.Read(ref _chamadas);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _chamadas);
        return responder(request, cancellationToken);
    }
}
```

Create `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakTokenClientTests.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O cliente do token endpoint pede token com um assertion novo a cada tentativa, e nunca repete sozinho.
/// </summary>
public sealed class KeycloakTokenClientTests
{
    private static HttpResponseMessage TokenOk(string token = "t1", int expiraEm = 300) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"access_token":"{{token}}","expires_in":{{expiraEm}},"token_type":"Bearer"}""",
                Encoding.UTF8,
                "application/json"),
        };

    private static KeycloakTokenClient Criar(HandlerFalso handler)
    {
        var opcoes = OpcoesDeTeste.Keycloak();
        IDateTimeProvider relogio = Substitute.For<IDateTimeProvider>();
        relogio.UtcNow.Returns(DateTimeOffset.UtcNow);

        IHttpClientFactory fabrica = Substitute.For<IHttpClientFactory>();
        fabrica.CreateClient(KeycloakTokenClient.NomeDoCliente)
            .Returns(_ => new HttpClient(handler, disposeHandler: false));

        return new KeycloakTokenClient(
            fabrica,
            opcoes,
            new ClientAssertionFactory(new GatewaySigningKey(opcoes), opcoes, relogio),
            NullLogger<KeycloakTokenClient>.Instance);
    }

    [Fact]
    public async Task ObterAsync_EnviaClientCredentialsComAssertionParaOTokenEndpointDoRealm()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Uri? destino = null;
        Dictionary<string, string> formulario = [];

        HandlerFalso handler = new(async (pedido, token) =>
        {
            destino = pedido.RequestUri;
            string corpo = await pedido.Content!.ReadAsStringAsync(token);
            formulario = corpo.Split('&')
                .Select(par => par.Split('=', 2))
                .ToDictionary(par => Uri.UnescapeDataString(par[0]), par => Uri.UnescapeDataString(par[1]));
            return TokenOk();
        });

        TokenObtido obtido = await Criar(handler).ObterAsync(ct);

        obtido.AccessToken.Should().Be("t1");
        obtido.ExpiraEm.Should().Be(TimeSpan.FromSeconds(300));
        destino.Should().Be(new Uri("http://keycloak.test:8080/realms/identity-gateway/protocol/openid-connect/token"));
        formulario["grant_type"].Should().Be("client_credentials");
        formulario["client_assertion_type"].Should().Be("urn:ietf:params:oauth:client-assertion-type:jwt-bearer");
        formulario.Should().ContainKey("client_assertion");
    }

    [Fact]
    public async Task ObterAsync_DuasTentativas_GeramAssertionsComJtiDiferente()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ConcurrentQueue<string> jtis = new();

        HandlerFalso handler = new(async (pedido, token) =>
        {
            string corpo = await pedido.Content!.ReadAsStringAsync(token);
            string assertion = Uri.UnescapeDataString(
                corpo.Split('&').Single(par => par.StartsWith("client_assertion=", StringComparison.Ordinal))
                    .Split('=', 2)[1]);
            JsonWebToken jwt = new(assertion);
            using JsonDocument payload = JsonDocument.Parse(Base64UrlEncoder.Decode(jwt.EncodedPayload));
            jtis.Enqueue(payload.RootElement.GetProperty("jti").GetString()!);
            return TokenOk();
        });

        KeycloakTokenClient cliente = Criar(handler);
        await cliente.ObterAsync(ct);
        await cliente.ObterAsync(ct);

        // Reenviar o mesmo assertion seria "Token reuse detected": cada tentativa precisa de um jti próprio.
        jtis.Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task ObterAsync_Recusado_LancaComStatusECodigoDeErroSemOAssertion()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        HandlerFalso handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                """{"error":"invalid_client","error_description":"Invalid client or Invalid client credentials"}""",
                Encoding.UTF8,
                "application/json"),
        }));

        Func<Task> obter = () => Criar(handler).ObterAsync(ct);

        (await obter.Should().ThrowAsync<HttpRequestException>())
            .Which.Should().Match<HttpRequestException>(excecao =>
                excecao.StatusCode == HttpStatusCode.Unauthorized
                && excecao.Message.Contains("invalid_client", StringComparison.Ordinal)
                && !excecao.Message.Contains("eyJ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClienteRegistrado_NaoRepeteONoTokenEndpoint()
    {
        // Composição real: se alguém pendurar resiliência no cliente do token, o mesmo corpo seria reenviado — e
        // recusado por reuso de jti. Um 503 aqui precisa sair na primeira tentativa.
        CancellationToken ct = TestContext.Current.CancellationToken;
        HandlerFalso handler = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        IConfiguration configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Keycloak:Admin:BaseUrl"] = "http://keycloak.test:8080",
                ["Keycloak:Admin:Realm"] = "identity-gateway",
                ["Keycloak:Admin:ClientId"] = "identity-gateway",
                ["Keycloak:Admin:PrivateKeyPem"] = ChavesDeTeste.Gerar().PemPrivado,
                ["Keycloak:Admin:AllowInsecureHttp"] = "true",
            })
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDateTimeProvider>());
        services.AddOptions<IdentityGateway.Infrastructure.Configuration.HttpResilienceOptions>();
        services.AddKeycloakIdentity(configuracao);
        services.AddHttpClient(KeycloakTokenClient.NomeDoCliente).ConfigurePrimaryHttpMessageHandler(() => handler);

        await using ServiceProvider provider = services.BuildServiceProvider();
        ITokenEndpoint endpoint = provider.GetRequiredService<ITokenEndpoint>();

        Func<Task> obter = () => endpoint.ObterAsync(ct);

        await obter.Should().ThrowAsync<HttpRequestException>();
        handler.Chamadas.Should().Be(1);
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet build tests/IdentityGateway.Infrastructure.IntegrationTests`
Expected: FAIL — `ITokenEndpoint`, `TokenObtido`, `KeycloakTokenClient` não existem.

- [ ] **Step 3: Implementar**

Create `src/IdentityGateway.Infrastructure/Identity/Keycloak/ITokenEndpoint.cs`:

```csharp
namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// Obtém um token do service account. Existe como interface para o cache poder ser testado sem HTTP.
/// </summary>
internal interface ITokenEndpoint
{
    /// <summary>Pede um token novo ao Keycloak. Cada chamada é uma tentativa, com assertion próprio.</summary>
    /// <exception cref="HttpRequestException">O Keycloak recusou ou não respondeu.</exception>
    Task<TokenObtido> ObterAsync(CancellationToken cancellationToken);
}

/// <summary>O access token e por quanto tempo ele vale, a partir do pedido.</summary>
internal sealed record TokenObtido(string AccessToken, TimeSpan ExpiraEm);
```

Create `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakLogs.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// Mensagens de log da integração com o Keycloak, na faixa 2200–2299.
/// </summary>
/// <remarks>
/// <b>Nenhuma registra token, assertion, chave ou corpo de requisição.</b> O formulário do token endpoint leva o
/// <c>client_assertion</c>, e o token dá <c>manage-organizations</c> sobre o realm: qualquer um dos dois no log
/// seria credencial indexada e retida. O que se registra é o tenant, a operação e o status.
/// </remarks>
internal static partial class KeycloakLogs
{
    [LoggerMessage(
        EventId = 2204,
        Level = LogLevel.Warning,
        Message = "Keycloak: token do service account recusado com {Status} — {Erro}")]
    public static partial void TokenRecusado(ILogger logger, int status, string erro);
}
```

Create `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakTokenClient.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// Pede o token do service account ao Keycloak por <c>client_credentials</c> com <c>private_key_jwt</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sem retry, de propósito.</b> O <c>jti</c> do assertion é de uso único: uma política que reenviasse o mesmo
/// corpo seria recusada com "Token reuse detected". Quem precisa de outra tentativa chama de novo, e ganha um
/// assertion novo. O cliente HTTP nomeado <see cref="NomeDoCliente"/> é registrado sem resiliência, e um teste
/// garante que continue assim.
/// </para>
/// <para>
/// Singleton, e por isso pede o <see cref="HttpClient"/> à fábrica a cada chamada em vez de guardá-lo: um cliente
/// capturado por um singleton nunca troca de handler, e para de enxergar mudança de DNS.
/// </para>
/// </remarks>
internal sealed class KeycloakTokenClient(
    IHttpClientFactory fabrica,
    IOptions<KeycloakAdminOptions> options,
    ClientAssertionFactory assertions,
    ILogger<KeycloakTokenClient> logger) : ITokenEndpoint
{
    /// <summary>Nome do cliente HTTP do token endpoint.</summary>
    internal const string NomeDoCliente = "keycloak-token";

    private const int TamanhoMaximoDoErro = 200;

    public async Task<TokenObtido> ObterAsync(CancellationToken cancellationToken)
    {
        KeycloakAdminOptions opcoes = options.Value;
        using HttpClient http = fabrica.CreateClient(NomeDoCliente);

        using FormUrlEncodedContent corpo = new(
        [
            new("grant_type", "client_credentials"),
            new("client_id", opcoes.ClientId),
            new("client_assertion_type", "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"),
            new("client_assertion", assertions.Criar()),
        ]);

        using HttpResponseMessage resposta = await http.PostAsync(
            new Uri($"{opcoes.Issuer}/protocol/openid-connect/token"), corpo, cancellationToken);

        if (!resposta.IsSuccessStatusCode)
        {
            string erro = await LerErroAsync(resposta, cancellationToken);
            KeycloakLogs.TokenRecusado(logger, (int)resposta.StatusCode, erro);

            throw new HttpRequestException(
                $"O Keycloak recusou o token do service account: {(int)resposta.StatusCode} {erro}",
                inner: null,
                resposta.StatusCode);
        }

        RespostaDeToken? token = await resposta.Content.ReadFromJsonAsync<RespostaDeToken>(cancellationToken);

        if (token is null || string.IsNullOrEmpty(token.AccessToken))
        {
            throw new HttpRequestException("O Keycloak respondeu sucesso sem access_token.");
        }

        return new TokenObtido(token.AccessToken, TimeSpan.FromSeconds(token.ExpiresIn));
    }

    /// <summary>
    /// <c>error</c> e <c>error_description</c> do Keycloak, truncados.
    /// </summary>
    /// <remarks>
    /// O <c>error_description</c> do token endpoint é genérico ("Invalid client or Invalid client credentials") e
    /// ajuda a diagnosticar sem expor nada. O corpo inteiro nunca: se um dia o Keycloak ecoasse o que recebeu, o
    /// assertion iria junto para o log.
    /// </remarks>
    private static async Task<string> LerErroAsync(HttpResponseMessage resposta, CancellationToken cancellationToken)
    {
        try
        {
            RespostaDeErro? erro = await resposta.Content.ReadFromJsonAsync<RespostaDeErro>(cancellationToken);
            string texto = $"{erro?.Error}: {erro?.ErrorDescription}";

            return texto.Length <= TamanhoMaximoDoErro ? texto : texto[..TamanhoMaximoDoErro];
        }
        catch (JsonException)
        {
            return "(corpo de erro fora do formato OAuth)";
        }
    }

    private sealed record RespostaDeToken(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record RespostaDeErro(
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);
}
```

Em `KeycloakServiceCollectionExtensions.AddKeycloakIdentity`, depois de `services.AddSingleton<ClientAssertionFactory>();`, acrescentar (e os `using`s `IdentityGateway.Infrastructure.Configuration` e `Microsoft.Extensions.Options`):

```csharp
        services.AddSingleton<ITokenEndpoint, KeycloakTokenClient>();

        // Token endpoint: cliente próprio, SEM resiliência. O jti é de uso único, e uma política de retry reenviaria
        // o mesmo assertion. Timeout curto, igual ao de uma tentativa da Admin API: sem ele, valeria o padrão de 100s
        // do HttpClient, e um Keycloak pendurado seguraria a sonda de health e a chamada de negócio por quase dois
        // minutos.
        services.AddHttpClient(KeycloakTokenClient.NomeDoCliente, (provider, http) =>
            http.Timeout = TimeSpan.FromSeconds(
                provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value.AttemptTimeoutSeconds));
```

- [ ] **Step 4: Rodar e ver passar**

Run: `dotnet test --project tests/IdentityGateway.Infrastructure.IntegrationTests/IdentityGateway.Infrastructure.IntegrationTests.csproj --filter-class "*KeycloakTokenClientTests"`
Expected: PASS (4 testes).

- [ ] **Step 5: 🧪 Prova por mutação**

1. Pendurar `.AddStandardResilienceHandler()` no `AddHttpClient(KeycloakTokenClient.NomeDoCliente, ...)` → `ClienteRegistrado_NaoRepeteONoTokenEndpoint` vermelho (chamadas > 1). Reverter.
2. Em `ObterAsync`, trocar `assertions.Criar()` por um campo `private readonly string _assertion = ...` criado uma vez no construtor → `ObterAsync_DuasTentativas_GeramAssertionsComJtiDiferente` vermelho. Reverter.

Rodar o Step 4 de novo: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/IdentityGateway.Infrastructure/Identity/Keycloak tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak
git commit -m "feat: cliente do token endpoint, sem retry e com assertion novo por tentativa" -m "Mutacoes: resiliencia no cliente do token e assertion reaproveitado, ambas pegas."
```

---

### Task 4: Cache single-flight do token

**Files:**
- Create: `src/IdentityGateway.Infrastructure/Identity/Keycloak/ServiceAccountTokenCache.cs`
- Modify: `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakServiceCollectionExtensions.cs`
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/ServiceAccountTokenCacheTests.cs`

**Interfaces:**
- Consumes: `ITokenEndpoint`, `TokenObtido` (Task 3); `IDateTimeProvider`.
- Produces: `internal sealed class ServiceAccountTokenCache(ITokenEndpoint, IDateTimeProvider) : IDisposable { static readonly TimeSpan Margem; ValueTask<string> ObterAsync(CancellationToken); void Invalidar(string tokenRecusado); }`

- [ ] **Step 1: Escrever os testes (falham: o tipo não existe)**

Create `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/ServiceAccountTokenCacheTests.cs`:

```csharp
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using NSubstitute;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O cache pede um token por vez, reusa enquanto ele vale com folga, e não guarda falha.
/// </summary>
public sealed class ServiceAccountTokenCacheTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly IDateTimeProvider _relogio = Substitute.For<IDateTimeProvider>();

    public ServiceAccountTokenCacheTests() => _relogio.UtcNow.Returns(T0);

    /// <summary>Token endpoint falso: devolve t1, t2, t3... e conta as chamadas.</summary>
    private sealed class EndpointFalso(Func<int, CancellationToken, Task<TokenObtido>>? comportamento = null)
        : ITokenEndpoint
    {
        private int _chamadas;

        public int Chamadas => Volatile.Read(ref _chamadas);

        public Task<TokenObtido> ObterAsync(CancellationToken cancellationToken)
        {
            int numero = Interlocked.Increment(ref _chamadas);

            return comportamento?.Invoke(numero, cancellationToken)
                   ?? Task.FromResult(new TokenObtido($"t{numero}", TimeSpan.FromSeconds(300)));
        }
    }

    [Fact]
    public async Task VinteChamadasSimultaneas_FazemUmaUnicaRequisicao()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        TaskCompletionSource primeiraChegou = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource liberar = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // A resposta fica retida até todas as chamadas terem entrado: se ela voltasse na hora, uma implementação
        // SEM trava também faria uma requisição só, e o teste passaria sem provar nada.
        EndpointFalso endpoint = new(async (numero, _) =>
        {
            primeiraChegou.TrySetResult();
            await liberar.Task;
            return new TokenObtido($"t{numero}", TimeSpan.FromSeconds(300));
        });

        using ServiceAccountTokenCache cache = new(endpoint, _relogio);

        Task<string>[] chamadas =
        [
            .. Enumerable.Range(0, 20).Select(_ => Task.Run(() => cache.ObterAsync(ct).AsTask(), ct)),
        ];

        await primeiraChegou.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await Task.Delay(200, ct);
        liberar.SetResult();

        string[] tokens = await Task.WhenAll(chamadas);

        endpoint.Chamadas.Should().Be(1);
        tokens.Should().AllBe("t1");
    }

    [Fact]
    public async Task TrintaEUmSegundosRestantes_Reusa()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        EndpointFalso endpoint = new();
        using ServiceAccountTokenCache cache = new(endpoint, _relogio);

        await cache.ObterAsync(ct);
        _relogio.UtcNow.Returns(T0.AddSeconds(300 - 31));
        string segundo = await cache.ObterAsync(ct);

        segundo.Should().Be("t1");
        endpoint.Chamadas.Should().Be(1);
    }

    [Fact]
    public async Task VinteENoveSegundosRestantes_BuscaDeNovo()
    {
        // A margem existe porque o token pode expirar no caminho até o Keycloak: um token com 5s de vida sai daqui
        // válido e chega vencido, e a chamada volta 401 por nada.
        CancellationToken ct = TestContext.Current.CancellationToken;
        EndpointFalso endpoint = new();
        using ServiceAccountTokenCache cache = new(endpoint, _relogio);

        await cache.ObterAsync(ct);
        _relogio.UtcNow.Returns(T0.AddSeconds(300 - 29));
        string segundo = await cache.ObterAsync(ct);

        segundo.Should().Be("t2");
        endpoint.Chamadas.Should().Be(2);
    }

    [Fact]
    public async Task TokenComVidaMenorQueAMargem_EUsadoUmaVezEDepoisRenovado()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        EndpointFalso endpoint = new((numero, _) =>
            Task.FromResult(new TokenObtido($"t{numero}", TimeSpan.FromSeconds(10))));
        using ServiceAccountTokenCache cache = new(endpoint, _relogio);

        string primeiro = await cache.ObterAsync(ct);
        string segundo = await cache.ObterAsync(ct);

        // Sem laço e sem erro: quem pediu usa o que veio; a próxima chamada busca outro.
        primeiro.Should().Be("t1");
        segundo.Should().Be("t2");
    }

    [Fact]
    public async Task FalhaNaObtencao_NaoFicaEmCache()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        EndpointFalso endpoint = new((numero, _) => numero == 1
            ? Task.FromException<TokenObtido>(new HttpRequestException("fora do ar"))
            : Task.FromResult(new TokenObtido($"t{numero}", TimeSpan.FromSeconds(300))));
        using ServiceAccountTokenCache cache = new(endpoint, _relogio);

        Func<Task> primeira = () => cache.ObterAsync(ct).AsTask();
        await primeira.Should().ThrowAsync<HttpRequestException>();

        string segunda = await cache.ObterAsync(ct);

        segunda.Should().Be("t2");
    }

    [Fact]
    public async Task CancelarQuemDisparouABusca_NaoCancelaQuemEspera()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        TaskCompletionSource primeiraChegou = new(TaskCreationOptions.RunContinuationsAsynchronously);

        EndpointFalso endpoint = new(async (numero, token) =>
        {
            if (numero == 1)
            {
                primeiraChegou.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }

            return new TokenObtido($"t{numero}", TimeSpan.FromSeconds(300));
        });

        using ServiceAccountTokenCache cache = new(endpoint, _relogio);
        using CancellationTokenSource cancelaPrimeiro = new();

        Task<string> primeiro = cache.ObterAsync(cancelaPrimeiro.Token).AsTask();
        await primeiraChegou.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Task<string> segundo = cache.ObterAsync(ct).AsTask();

        await cancelaPrimeiro.CancelAsync();

        Func<Task> esperarPrimeiro = () => primeiro;
        await esperarPrimeiro.Should().ThrowAsync<OperationCanceledException>();
        (await segundo.WaitAsync(TimeSpan.FromSeconds(10), ct)).Should().Be("t2");
    }

    [Fact]
    public async Task Invalidar_SoDescartaOTokenRecusado()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        EndpointFalso endpoint = new();
        using ServiceAccountTokenCache cache = new(endpoint, _relogio);

        await cache.ObterAsync(ct);

        // Um 401 atrasado de um token antigo não pode derrubar o token novo que outra chamada acabou de obter.
        cache.Invalidar("um-token-antigo");
        (await cache.ObterAsync(ct)).Should().Be("t1");

        cache.Invalidar("t1");
        (await cache.ObterAsync(ct)).Should().Be("t2");
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet build tests/IdentityGateway.Infrastructure.IntegrationTests`
Expected: FAIL — `ServiceAccountTokenCache` não existe.

- [ ] **Step 3: Implementar**

Create `src/IdentityGateway.Infrastructure/Identity/Keycloak/ServiceAccountTokenCache.cs`:

```csharp
using IdentityGateway.Application.Common.Abstractions;

namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// Guarda o token do service account e garante que só uma chamada por vez vá buscá-lo.
/// </summary>
/// <remarks>
/// <para>
/// <b>Singleton, e não dentro do handler.</b> O <c>IHttpClientFactory</c> recicla os handlers a cada ~2 minutos; um
/// cache guardado neles morreria junto, sem erro — e cada ciclo pediria um token novo.
/// </para>
/// <para>
/// <b>Single-flight.</b> Sem a trava, N chamadas simultâneas com o cache vazio pediriam N tokens — e cada pedido
/// assina um assertion e ocupa o Keycloak. Com ela, uma busca e as demais esperam o resultado.
/// </para>
/// <para>
/// <b>Falha não fica em cache.</b> A trava é liberada no <c>finally</c> sem gravar nada, e a próxima chamada tenta de
/// novo. Pelo mesmo motivo, cancelar quem disparou a busca não afeta quem espera: a exceção é só dele, e o próximo
/// da fila busca outra vez.
/// </para>
/// </remarks>
internal sealed class ServiceAccountTokenCache(ITokenEndpoint endpoint, IDateTimeProvider relogio) : IDisposable
{
    /// <summary>Folga antes da expiração em que o token deixa de ser usado.</summary>
    internal static readonly TimeSpan Margem = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _trava = new(1, 1);
    private TokenEmCache? _atual;

    /// <summary>Um token válido, do cache ou recém-obtido.</summary>
    public async ValueTask<string> ObterAsync(CancellationToken cancellationToken)
    {
        if (ValorSeValido(Volatile.Read(ref _atual)) is { } rapido)
        {
            return rapido;
        }

        await _trava.WaitAsync(cancellationToken);

        try
        {
            // Quem esperou na trava encontra o token que a chamada da frente acabou de obter.
            if (ValorSeValido(_atual) is { } jaObtido)
            {
                return jaObtido;
            }

            // A validade conta a partir do PEDIDO, não da resposta: o Keycloak começou a contar antes de responder.
            DateTimeOffset pedidoEm = relogio.UtcNow;
            TokenObtido obtido = await endpoint.ObterAsync(cancellationToken);

            Volatile.Write(ref _atual, new TokenEmCache(obtido.AccessToken, pedidoEm + obtido.ExpiraEm));

            // Devolve o que veio mesmo que já esteja dentro da margem: quem pediu usa, a próxima chamada renova.
            return obtido.AccessToken;
        }
        finally
        {
            _trava.Release();
        }
    }

    /// <summary>Descarta o token, se ainda for o que foi recusado.</summary>
    /// <remarks>
    /// Compara antes de descartar: um 401 atrasado de um token antigo não pode derrubar o token novo que outra
    /// chamada acabou de obter.
    /// </remarks>
    public void Invalidar(string tokenRecusado)
    {
        TokenEmCache? atual = Volatile.Read(ref _atual);

        if (atual is not null && string.Equals(atual.Valor, tokenRecusado, StringComparison.Ordinal))
        {
            Interlocked.CompareExchange(ref _atual, null, atual);
        }
    }

    public void Dispose() => _trava.Dispose();

    private string? ValorSeValido(TokenEmCache? token) =>
        token is not null && relogio.UtcNow < token.ExpiraEm - Margem ? token.Valor : null;

    private sealed record TokenEmCache(string Valor, DateTimeOffset ExpiraEm);
}
```

Em `KeycloakServiceCollectionExtensions.AddKeycloakIdentity`, depois do registro de `ITokenEndpoint`:

```csharp
        services.AddSingleton<ServiceAccountTokenCache>();
```

- [ ] **Step 4: Rodar e ver passar**

Run: `dotnet test --project tests/IdentityGateway.Infrastructure.IntegrationTests/IdentityGateway.Infrastructure.IntegrationTests.csproj --filter-class "*ServiceAccountTokenCacheTests"`
Expected: PASS (7 testes).

- [ ] **Step 5: 🧪 Prova por mutação**

1. Remover o `await _trava.WaitAsync(...)` e o `_trava.Release()` → `VinteChamadasSimultaneas_FazemUmaUnicaRequisicao` vermelho. Reverter.
2. Trocar `Margem = TimeSpan.FromSeconds(30)` por `TimeSpan.Zero` → `VinteENoveSegundosRestantes_BuscaDeNovo` vermelho. Reverter.
3. Em `Invalidar`, remover a comparação (descartar sempre) → `Invalidar_SoDescartaOTokenRecusado` vermelho. Reverter.

Rodar o Step 4 de novo: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/IdentityGateway.Infrastructure/Identity/Keycloak tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak
git commit -m "feat: cache single-flight do token do service account" -m "Mutacoes: sem trava, margem zero e invalidacao incondicional, todas pegas."
```

---

### Task 5: Handler do token (401 → repete uma vez)

**Files:**
- Create: `src/IdentityGateway.Infrastructure/Identity/Keycloak/ServiceAccountTokenHandler.cs`
- Modify: `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakServiceCollectionExtensions.cs`
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/ServiceAccountTokenHandlerTests.cs`

**Interfaces:**
- Consumes: `ServiceAccountTokenCache.ObterAsync`, `.Invalidar` (Task 4).
- Produces: `internal sealed class ServiceAccountTokenHandler(ServiceAccountTokenCache) : DelegatingHandler`

- [ ] **Step 1: Escrever os testes (falham: o tipo não existe)**

Create `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/ServiceAccountTokenHandlerTests.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using NSubstitute;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O handler põe o token e, num 401, troca o token e tenta de novo — uma vez só.
/// </summary>
public sealed class ServiceAccountTokenHandlerTests
{
    private sealed class EndpointSequencial : ITokenEndpoint
    {
        private int _chamadas;

        public int Chamadas => Volatile.Read(ref _chamadas);

        public Task<TokenObtido> ObterAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new TokenObtido($"t{Interlocked.Increment(ref _chamadas)}", TimeSpan.FromMinutes(5)));
    }

    private static (HttpMessageInvoker Invocador, EndpointSequencial Endpoint, ConcurrentQueue<string> TokensVistos)
        Montar(Func<string, HttpStatusCode> statusPorToken)
    {
        IDateTimeProvider relogio = Substitute.For<IDateTimeProvider>();
        relogio.UtcNow.Returns(DateTimeOffset.UtcNow);

        EndpointSequencial endpoint = new();
        ConcurrentQueue<string> vistos = new();

        HandlerFalso keycloak = new((pedido, _) =>
        {
            string token = pedido.Headers.Authorization!.Parameter!;
            vistos.Enqueue(token);
            return Task.FromResult(new HttpResponseMessage(statusPorToken(token)));
        });

        ServiceAccountTokenHandler handler = new(new ServiceAccountTokenCache(endpoint, relogio))
        {
            InnerHandler = keycloak,
        };

        return (new HttpMessageInvoker(handler), endpoint, vistos);
    }

    [Fact]
    public async Task Com401_InvalidaBuscaTokenNovoERepeteUmaVez()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var (invocador, endpoint, vistos) = Montar(token => token == "t1"
            ? HttpStatusCode.Unauthorized
            : HttpStatusCode.OK);

        using HttpRequestMessage pedido = new(HttpMethod.Get, "http://keycloak.test/admin/realms/x/organizations");
        using HttpResponseMessage resposta = await invocador.SendAsync(pedido, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        vistos.Should().Equal("t1", "t2");
        endpoint.Chamadas.Should().Be(2);
    }

    [Fact]
    public async Task SegundoAinda401_DevolveO401SemLaco()
    {
        // Token recém-emitido recusado é problema de configuração (papel, realm, relógio), não de expiração.
        // Insistir transformaria um erro de configuração em laço contra o Keycloak.
        CancellationToken ct = TestContext.Current.CancellationToken;
        var (invocador, _, vistos) = Montar(_ => HttpStatusCode.Unauthorized);

        using HttpRequestMessage pedido = new(HttpMethod.Get, "http://keycloak.test/admin/realms/x/organizations");
        using HttpResponseMessage resposta = await invocador.SendAsync(pedido, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        vistos.Should().HaveCount(2);
    }

    [Fact]
    public async Task Sem401_UsaOTokenDoCacheSemBuscarOutro()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var (invocador, endpoint, _) = Montar(_ => HttpStatusCode.OK);

        using HttpRequestMessage primeiro = new(HttpMethod.Get, "http://keycloak.test/a");
        using HttpRequestMessage segundo = new(HttpMethod.Get, "http://keycloak.test/b");
        (await invocador.SendAsync(primeiro, ct)).Dispose();
        (await invocador.SendAsync(segundo, ct)).Dispose();

        endpoint.Chamadas.Should().Be(1);
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet build tests/IdentityGateway.Infrastructure.IntegrationTests`
Expected: FAIL — `ServiceAccountTokenHandler` não existe.

- [ ] **Step 3: Implementar**

Create `src/IdentityGateway.Infrastructure/Identity/Keycloak/ServiceAccountTokenHandler.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;

namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// Autentica cada chamada à Admin API com o token do service account.
/// </summary>
/// <remarks>
/// <para>
/// <b>Num 401, invalida o token e repete uma vez.</b> O token pode ter sido revogado ou expirado antes da hora que o
/// cache calculou. Repetir é seguro até num POST: 401 significa que o Keycloak não executou nada. Uma vez só — um
/// token recém-emitido recusado é erro de configuração, e insistir viraria laço.
/// </para>
/// <para>
/// <b>Fica DENTRO da resiliência</b> (registrado depois dela). Cada tentativa da resiliência passa por aqui e renova o
/// token se preciso, e a repetição após 401 acontece dentro de uma tentativa, contida no timeout total.
/// </para>
/// <para>
/// O mesmo <see cref="HttpRequestMessage"/> é reenviado: por isso o <c>KeycloakAdminClient</c> usa corpo em
/// <c>StringContent</c>, que pode ser lido de novo.
/// </para>
/// </remarks>
internal sealed class ServiceAccountTokenHandler(ServiceAccountTokenCache cache) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string token = await cache.ObterAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage resposta = await base.SendAsync(request, cancellationToken);

        if (resposta.StatusCode != HttpStatusCode.Unauthorized)
        {
            return resposta;
        }

        resposta.Dispose();
        cache.Invalidar(token);

        string novo = await cache.ObterAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", novo);

        return await base.SendAsync(request, cancellationToken);
    }
}
```

Em `KeycloakServiceCollectionExtensions.AddKeycloakIdentity`, depois do cache:

```csharp
        services.AddTransient<ServiceAccountTokenHandler>();
```

- [ ] **Step 4: Rodar e ver passar**

Run: `dotnet test --project tests/IdentityGateway.Infrastructure.IntegrationTests/IdentityGateway.Infrastructure.IntegrationTests.csproj --filter-class "*ServiceAccountTokenHandlerTests"`
Expected: PASS (3 testes).

- [ ] **Step 5: 🧪 Prova por mutação**

1. Remover a linha `cache.Invalidar(token);` → `Com401_InvalidaBuscaTokenNovoERepeteUmaVez` vermelho (repete com `t1`). Reverter.
2. Trocar o `if (... != Unauthorized) return` por um `while` que repete enquanto for 401 (limitado a 5 para não travar) → `SegundoAinda401_DevolveO401SemLaco` vermelho. Reverter.

Rodar o Step 4 de novo: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/IdentityGateway.Infrastructure/Identity/Keycloak tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak
git commit -m "feat: handler do token do service account, com uma repeticao no 401" -m "Mutacoes: sem invalidacao e repeticao em laco, ambas pegas."
```

---

### Task 6: Porta, cliente da Admin API, adaptador e composição

**Files:**
- Create: `src/IdentityGateway.Application/Common/Abstractions/IIdentityProvider.cs`
- Create: `src/IdentityGateway.Application/Common/Abstractions/IdentityProviderInconsistencyException.cs`
- Create: `src/IdentityGateway.Infrastructure/Identity/Keycloak/OrganizationRepresentation.cs`
- Create: `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakAdminClient.cs`
- Create: `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakIdentityProvider.cs`
- Modify: `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakLogs.cs`
- Modify: `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakServiceCollectionExtensions.cs`
- Modify: `src/IdentityGateway.Infrastructure/DependencyInjection.cs`
- Modify: `src/IdentityGateway.Api/appsettings.json`, `src/IdentityGateway.Api/appsettings.Development.json`
- Modify: `tests/IdentityGateway.Api.FunctionalTests/IdentityGatewayApiFactory.cs`
- Modify: `tests/IdentityGateway.Infrastructure.IntegrationTests/DependencyInjectionTests.cs`
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakIdentityProviderTests.cs`

**Interfaces:**
- Consumes: `ServiceAccountTokenHandler` (Task 5), `HttpResilienceOptions`, `KeycloakAdminOptions.AdminBaseAddress` (Task 1).
- Produces: `public interface IIdentityProvider { Task<string> EnsureOrganizationAsync(TenantId tenantId, TenantSlug slug, string name, CancellationToken cancellationToken); }`
- Produces: `public sealed class IdentityProviderInconsistencyException : Exception`
- Produces: `internal sealed record OrganizationRepresentation(string? Id, string Name, string Alias, string? Description, bool Enabled, Dictionary<string, List<string>>? Attributes)`; `internal sealed class KeycloakConflictException : Exception`
- Produces: `internal sealed class KeycloakAdminClient(HttpClient, IOptions<KeycloakAdminOptions>) { Task<string> CreateOrganizationAsync(OrganizationRepresentation, CancellationToken); Task<OrganizationRepresentation?> FindOrganizationByAttributeAsync(string chave, string valor, CancellationToken); }`
- Produces: `internal sealed class KeycloakIdentityProvider(KeycloakAdminClient, ILogger<KeycloakIdentityProvider>) : IIdentityProvider { const string AtributoDoTenant = "gateway_tenant_id"; }`

- [ ] **Step 1: Criar a porta e a exceção na Application**

Create `src/IdentityGateway.Application/Common/Abstractions/IIdentityProvider.cs`:

```csharp
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Application.Common.Abstractions;

/// <summary>
/// Única forma de a Application falar com o provedor de identidade.
/// </summary>
/// <remarks>
/// <para>
/// Nenhum tipo do Keycloak atravessa esta interface (ADR-008), e toda operação é idempotente: "garanta que existe",
/// não "crie". A mesma mensagem do Outbox pode chegar duas vezes, e a segunda entrega precisa ser inofensiva.
/// </para>
/// <para>
/// <b>A interface cresce por fatia.</b> Cada operação entra quando o caso de uso que a chama entra. Declarar as
/// outras cinco da §11.3 agora seria contrato que mente: métodos que existem e lançam
/// <see cref="NotImplementedException"/>.
/// </para>
/// </remarks>
public interface IIdentityProvider
{
    /// <summary>
    /// Garante que existe a Organization do tenant, e devolve o id dela no provedor.
    /// </summary>
    /// <param name="tenantId">Correlaciona a Organization ao tenant; é a chave da idempotência.</param>
    /// <param name="slug">Identificador único e imutável do tenant.</param>
    /// <param name="name">Nome de exibição.</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    /// <exception cref="IdentityProviderInconsistencyException">
    /// Estado que repetir não resolve: o slug está em uso por outra Organization, ou há mais de uma correlacionada ao
    /// mesmo tenant.
    /// </exception>
    /// <exception cref="HttpRequestException">Falha transiente de comunicação com o provedor.</exception>
    Task<string> EnsureOrganizationAsync(
        TenantId tenantId, TenantSlug slug, string name, CancellationToken cancellationToken);
}
```

Create `src/IdentityGateway.Application/Common/Abstractions/IdentityProviderInconsistencyException.cs`:

```csharp
namespace IdentityGateway.Application.Common.Abstractions;

/// <summary>
/// O provedor de identidade está num estado que repetir a operação não corrige.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exception e não <c>Result</c>.</b> O repositório reserva <c>Result</c> para resposta de negócio e exception para
/// falha de infraestrutura — e isto é infraestrutura: um slug tomado por uma Organization criada fora da Gateway, ou
/// duas Organizations apontando para o mesmo tenant.
/// </para>
/// <para>
/// <b>Contrato com quem consome:</b> o retry do consumidor do provisionamento precisa IGNORAR este tipo. Repeti-lo só
/// adiaria o <c>ProvisioningFailed</c>, por configuração e não por decisão.
/// </para>
/// </remarks>
public sealed class IdentityProviderInconsistencyException : Exception
{
    public IdentityProviderInconsistencyException()
    {
    }

    public IdentityProviderInconsistencyException(string message)
        : base(message)
    {
    }

    public IdentityProviderInconsistencyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
```

- [ ] **Step 2: Escrever os testes do adaptador com HTTP falso (falham: os tipos não existem)**

Create `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakIdentityProviderTests.cs`:

```csharp
using System.Net;
using System.Text;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using Microsoft.Extensions.Logging.Abstractions;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O que o adaptador faz com cada resposta da Admin API — sem Keycloak. O comportamento do Keycloak de verdade é
/// provado em <c>EnsureOrganizationContraKeycloakTests</c>.
/// </summary>
public sealed class KeycloakIdentityProviderTests
{
    private static readonly TenantId Tenant = new(Guid.Parse("0199a1b2-0000-7000-8000-000000000001"));
    private static readonly TenantSlug Slug = TenantSlug.Create("acme").Value;

    private static HttpResponseMessage Lista(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Criada(string id)
    {
        HttpResponseMessage resposta = new(HttpStatusCode.Created);
        resposta.Headers.Location = new Uri($"http://keycloak.test/admin/realms/identity-gateway/organizations/{id}");
        return resposta;
    }

    private static (KeycloakIdentityProvider Adaptador, List<HttpMethod> Metodos) Montar(
        params Func<HttpRequestMessage, HttpResponseMessage>[] respostas)
    {
        List<HttpMethod> metodos = [];
        int indice = 0;

        HandlerFalso handler = new((pedido, _) =>
        {
            metodos.Add(pedido.Method);
            return Task.FromResult(respostas[indice++](pedido));
        });

        KeycloakAdminClient admin = new(
            new HttpClient(handler) { BaseAddress = new Uri("http://keycloak.test/") },
            OpcoesDeTeste.Keycloak());

        return (new KeycloakIdentityProvider(admin, NullLogger<KeycloakIdentityProvider>.Instance), metodos);
    }

    [Fact]
    public async Task JaExiste_DevolveOIdSemCriar()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var (adaptador, metodos) = Montar(_ => Lista("""[{"id":"org-1","name":"acme","alias":"acme","enabled":true}]"""));

        string id = await adaptador.EnsureOrganizationAsync(Tenant, Slug, "Acme", ct);

        id.Should().Be("org-1");
        metodos.Should().Equal(HttpMethod.Get);
    }

    [Fact]
    public async Task NaoExiste_CriaComNameAliasDescriptionEAtributo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string? corpo = null;

        var (adaptador, _) = Montar(
            _ => Lista("[]"),
            pedido =>
            {
                corpo = pedido.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
                return Criada("org-2");
            });

        string id = await adaptador.EnsureOrganizationAsync(Tenant, Slug, "Acme Ltda", ct);

        id.Should().Be("org-2");
        corpo.Should().Contain("\"name\":\"acme\"")
            .And.Contain("\"alias\":\"acme\"")
            .And.Contain("\"description\":\"Acme Ltda\"")
            .And.Contain($"\"gateway_tenant_id\":[\"{Tenant.Value}\"]");
    }

    [Fact]
    public async Task BuscaUsaQComBriefRepresentationFalse()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Uri? consultada = null;

        var (adaptador, _) = Montar(pedido =>
        {
            consultada = pedido.RequestUri;
            return Lista("""[{"id":"org-1","name":"acme","alias":"acme","enabled":true}]""");
        });

        await adaptador.EnsureOrganizationAsync(Tenant, Slug, "Acme", ct);

        // Comparado já decodificado: se o Uri normaliza ou não o %3A é detalhe do .NET, e não o que se quer provar.
        Uri.UnescapeDataString(consultada!.Query).Should().Be(
            $"?q=gateway_tenant_id:{Tenant.Value}&briefRepresentation=false&max=2");
    }

    [Fact]
    public async Task ConflitoEReconsultaAcha_DevolveOIdDaVencedora()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var (adaptador, metodos) = Montar(
            _ => Lista("[]"),
            _ => new HttpResponseMessage(HttpStatusCode.Conflict),
            _ => Lista("""[{"id":"org-3","name":"acme","alias":"acme","enabled":true}]"""));

        string id = await adaptador.EnsureOrganizationAsync(Tenant, Slug, "Acme", ct);

        id.Should().Be("org-3");
        metodos.Should().Equal(HttpMethod.Get, HttpMethod.Post, HttpMethod.Get);
    }

    [Fact]
    public async Task ConflitoEReconsultaVazia_LancaInconsistencia()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var (adaptador, _) = Montar(
            _ => Lista("[]"),
            _ => new HttpResponseMessage(HttpStatusCode.Conflict),
            _ => Lista("[]"));

        Func<Task> garantir = () => adaptador.EnsureOrganizationAsync(Tenant, Slug, "Acme", ct);

        await garantir.Should().ThrowAsync<IdentityProviderInconsistencyException>().WithMessage("*acme*");
    }

    [Fact]
    public async Task MaisDeUmaComOAtributo_LancaInconsistencia()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var (adaptador, _) = Montar(_ => Lista(
            """[{"id":"a","name":"x","alias":"x","enabled":true},{"id":"b","name":"y","alias":"y","enabled":true}]"""));

        Func<Task> garantir = () => adaptador.EnsureOrganizationAsync(Tenant, Slug, "Acme", ct);

        await garantir.Should().ThrowAsync<IdentityProviderInconsistencyException>();
    }

    [Fact]
    public async Task ErroDoServidor_SobeComoHttpRequestException()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var (adaptador, _) = Montar(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        Func<Task> garantir = () => adaptador.EnsureOrganizationAsync(Tenant, Slug, "Acme", ct);

        // Transiente: é o consumidor quem decide repetir. O adaptador não engole nada.
        await garantir.Should().ThrowAsync<HttpRequestException>();
    }
}
```

- [ ] **Step 3: Rodar e ver falhar**

Run: `dotnet build tests/IdentityGateway.Infrastructure.IntegrationTests`
Expected: FAIL — `KeycloakAdminClient`, `KeycloakIdentityProvider` não existem.

- [ ] **Step 4: Implementar DTO, cliente e adaptador**

Create `src/IdentityGateway.Infrastructure/Identity/Keycloak/OrganizationRepresentation.cs`:

```csharp
namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// A <c>OrganizationRepresentation</c> da Admin API, só com os campos usados.
/// </summary>
/// <remarks>
/// <see cref="Name"/> recebe o <b>slug</b>, não o nome de exibição: <c>name</c> é único no realm, e o nome do tenant
/// não é. O nome de exibição vai em <see cref="Description"/> (até 4000 caracteres, devolvida em toda leitura).
/// </remarks>
internal sealed record OrganizationRepresentation(
    string? Id,
    string Name,
    string Alias,
    string? Description,
    bool Enabled,
    Dictionary<string, List<string>>? Attributes);

/// <summary>O Keycloak respondeu 409 ao criar: <c>name</c> ou <c>alias</c> já em uso.</summary>
/// <remarks>Interna: nunca sai do adaptador. Ou vira corrida resolvida, ou vira inconsistência.</remarks>
internal sealed class KeycloakConflictException : Exception
{
    public KeycloakConflictException()
    {
    }

    public KeycloakConflictException(string message)
        : base(message)
    {
    }

    public KeycloakConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
```

Create `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakAdminClient.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IdentityGateway.Application.Common.Abstractions;
using Microsoft.Extensions.Options;

namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// Cliente tipado da Admin API do Keycloak — só os endpoints usados (ADR-008).
/// </summary>
/// <remarks>
/// O token e a resiliência não aparecem aqui: são handlers no pipeline do <see cref="HttpClient"/> injetado
/// (<c>AddKeycloakIdentity</c>). Este cliente só sabe montar a requisição e ler a resposta.
/// </remarks>
internal sealed class KeycloakAdminClient(HttpClient http, IOptions<KeycloakAdminOptions> options)
{
    // Web = camelCase, que é o que o Jackson do Keycloak usa. Nulos fora: o Keycloak interpreta campo presente como
    // intenção.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private string Organizations => $"admin/realms/{Uri.EscapeDataString(options.Value.Realm)}/organizations";

    /// <summary>Cria a Organization e devolve o id que o Keycloak atribuiu.</summary>
    /// <exception cref="KeycloakConflictException"><c>name</c> ou <c>alias</c> já em uso (409).</exception>
    public async Task<string> CreateOrganizationAsync(
        OrganizationRepresentation organizacao, CancellationToken cancellationToken)
    {
        // StringContent, e não JsonContent: num 401 o handler do token reenvia o MESMO request, e o corpo precisa
        // poder ser lido de novo.
        using StringContent corpo = new(
            JsonSerializer.Serialize(organizacao, Json), Encoding.UTF8, "application/json");

        using HttpResponseMessage resposta = await http.PostAsync(
            new Uri(Organizations, UriKind.Relative), corpo, cancellationToken);

        // Decide pelo status, nunca pelo texto: a mensagem do 409 é detalhe do Keycloak e muda entre versões.
        if (resposta.StatusCode == HttpStatusCode.Conflict)
        {
            throw new KeycloakConflictException("O Keycloak respondeu 409 ao criar a Organization.");
        }

        resposta.EnsureSuccessStatusCode();

        // 201 sem corpo; o id vem no fim do Location.
        Uri local = resposta.Headers.Location
                    ?? throw new HttpRequestException("O Keycloak respondeu 201 sem o cabeçalho Location.");

        return local.Segments[^1].TrimEnd('/');
    }

    /// <summary>A Organization com o atributo informado, ou <c>null</c>.</summary>
    /// <remarks>
    /// O parâmetro é <c>q</c> — <c>searchQuery</c> é só o nome da variável Java, e <c>exact</c> vale para
    /// <c>search</c>, não para <c>q</c>, que já compara por igualdade. <c>briefRepresentation=false</c> é o que faz os
    /// atributos voltarem. <c>max=2</c> basta para detectar duplicidade sem trazer o realm inteiro.
    /// </remarks>
    /// <exception cref="IdentityProviderInconsistencyException">Mais de uma Organization com o atributo.</exception>
    public async Task<OrganizationRepresentation?> FindOrganizationByAttributeAsync(
        string chave, string valor, CancellationToken cancellationToken)
    {
        string q = Uri.EscapeDataString($"{chave}:{valor}");

        List<OrganizationRepresentation>? achadas = await http.GetFromJsonAsync<List<OrganizationRepresentation>>(
            new Uri($"{Organizations}?q={q}&briefRepresentation=false&max=2", UriKind.Relative),
            Json,
            cancellationToken);

        // Mais de uma é correlação corrompida: falhar alto em vez de escolher uma e provisionar sobre a errada.
        if (achadas is { Count: > 1 })
        {
            throw new IdentityProviderInconsistencyException($"Mais de uma Organization com {chave}={valor}.");
        }

        return achadas?.SingleOrDefault();
    }
}
```

Create `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakIdentityProvider.cs`:

```csharp
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Tenants;
using Microsoft.Extensions.Logging;

namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// Implementação de <see cref="IIdentityProvider"/> sobre o Keycloak.
/// </summary>
/// <remarks>
/// <para>
/// <b>Consulta antes de criar</b>, pelo atributo <see cref="AtributoDoTenant"/>. Uma tentativa anterior pode ter
/// criado a Organization e falhado antes de gravar o id no banco da Gateway: sem a consulta, a repetição da mensagem
/// criaria outra.
/// </para>
/// <para>
/// <b>Transient</b>, como o cliente tipado que envolve. Não depende de <c>DbContext</c>, e não há motivo para viver o
/// escopo inteiro.
/// </para>
/// </remarks>
internal sealed class KeycloakIdentityProvider(
    KeycloakAdminClient admin,
    ILogger<KeycloakIdentityProvider> logger) : IIdentityProvider
{
    /// <summary>
    /// Atributo que correlaciona a Organization ao tenant: estável, escolhido por nós, imune a rename — e o que
    /// distingue a nossa Organization de outra com o mesmo alias criada fora da Gateway.
    /// </summary>
    internal const string AtributoDoTenant = "gateway_tenant_id";

    public async Task<string> EnsureOrganizationAsync(
        TenantId tenantId, TenantSlug slug, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string valor = tenantId.Value.ToString();

        OrganizationRepresentation? existente =
            await admin.FindOrganizationByAttributeAsync(AtributoDoTenant, valor, cancellationToken);

        if (existente?.Id is { } idExistente)
        {
            KeycloakLogs.OrganizacaoJaExistia(logger, tenantId.Value);
            return idExistente;
        }

        try
        {
            string id = await admin.CreateOrganizationAsync(
                new OrganizationRepresentation(
                    Id: null,
                    Name: slug.Value,
                    Alias: slug.Value,
                    Description: name,
                    Enabled: true,
                    Attributes: new() { [AtributoDoTenant] = [valor] }),
                cancellationToken);

            KeycloakLogs.OrganizacaoCriada(logger, tenantId.Value);
            return id;
        }
        catch (KeycloakConflictException)
        {
            OrganizationRepresentation? vencedora =
                await admin.FindOrganizationByAttributeAsync(AtributoDoTenant, valor, cancellationToken);

            if (vencedora?.Id is { } idVencedora)
            {
                // Duas entregas da mesma mensagem concorreram, e a outra criou primeiro.
                KeycloakLogs.CorridaResolvida(logger, tenantId.Value);
                return idVencedora;
            }

            // O 409 é de uma Organization que NÃO é deste tenant. Repetir não resolve.
            KeycloakLogs.ConflitoNaoCorrelacionado(logger, tenantId.Value);

            throw new IdentityProviderInconsistencyException(
                $"Alias '{slug.Value}' em uso por Organization não correlacionada ao tenant {tenantId.Value}.");
        }
    }
}
```

Em `KeycloakLogs.cs`, acrescentar antes de `TokenRecusado`:

```csharp
    [LoggerMessage(
        EventId = 2200,
        Level = LogLevel.Debug,
        Message = "Keycloak: Organization do tenant {TenantId} já existia")]
    public static partial void OrganizacaoJaExistia(ILogger logger, Guid tenantId);

    [LoggerMessage(
        EventId = 2201,
        Level = LogLevel.Information,
        Message = "Keycloak: Organization do tenant {TenantId} criada")]
    public static partial void OrganizacaoCriada(ILogger logger, Guid tenantId);

    [LoggerMessage(
        EventId = 2202,
        Level = LogLevel.Information,
        Message = "Keycloak: corrida na criação da Organization do tenant {TenantId} resolvida pela reconsulta")]
    public static partial void CorridaResolvida(ILogger logger, Guid tenantId);

    [LoggerMessage(
        EventId = 2203,
        Level = LogLevel.Error,
        Message = "Keycloak: o slug do tenant {TenantId} está em uso por Organization não correlacionada")]
    public static partial void ConflitoNaoCorrelacionado(ILogger logger, Guid tenantId);
```

- [ ] **Step 5: Rodar os testes do adaptador e ver passar**

Run: `dotnet test --project tests/IdentityGateway.Infrastructure.IntegrationTests/IdentityGateway.Infrastructure.IntegrationTests.csproj --filter-class "*KeycloakIdentityProviderTests"`
Expected: PASS (7 testes).

- [ ] **Step 6: Registrar o cliente tipado e o adaptador**

Em `KeycloakServiceCollectionExtensions.AddKeycloakIdentity`, depois de `services.AddTransient<ServiceAccountTokenHandler>();`, acrescentar (e os `using`s `IdentityGateway.Application.Common.Abstractions` e `Microsoft.Extensions.Http.Resilience`):

```csharp
        IHttpClientBuilder admin = services.AddHttpClient<KeycloakAdminClient>((provider, http) =>
        {
            http.BaseAddress = provider.GetRequiredService<IOptions<KeycloakAdminOptions>>().Value.AdminBaseAddress;

            // O timeout é da resiliência (por tentativa e total). Este cancelaria no meio do pipeline, com um
            // cancelamento indistinguível do que parte de quem chamou.
            http.Timeout = Timeout.InfiniteTimeSpan;
        });

        // A ORDEM IMPORTA: o primeiro handler registrado é o mais externo. Resiliência por fora e token por dentro
        // fazem cada tentativa renovar o token se preciso, e deixam o "401 → repete uma vez" DENTRO de uma
        // tentativa, contido no timeout total. Na ordem inversa, o reenvio após 401 entraria na pipeline do zero.
        admin.AddStandardResilienceHandler().Configure((HttpStandardResilienceOptions resiliencia, IServiceProvider provider) =>
        {
            HttpResilienceOptions politica = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value;

            resiliencia.Retry.MaxRetryAttempts = politica.MaxRetryAttempts;
            resiliencia.Retry.Delay = TimeSpan.FromSeconds(politica.BaseDelaySeconds);

            // POST não é idempotente: retry automático só em métodos seguros. A idempotência das escritas vem do
            // "consultar antes de criar" do adaptador — e há teste contra o Keycloak real provando que o POST não
            // é repetido.
            resiliencia.Retry.DisableForUnsafeHttpMethods();

            resiliencia.AttemptTimeout.Timeout = TimeSpan.FromSeconds(politica.AttemptTimeoutSeconds);
            resiliencia.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(politica.TotalTimeoutSeconds);
            resiliencia.CircuitBreaker.FailureRatio = politica.FailureRatio;
            resiliencia.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(politica.BreakDurationSeconds);

            // O pipeline padrão recusa amostragem menor que o dobro do timeout por tentativa.
            resiliencia.CircuitBreaker.SamplingDuration =
                TimeSpan.FromSeconds(Math.Max(30, 2 * politica.AttemptTimeoutSeconds));
        });

        admin.AddHttpMessageHandler<ServiceAccountTokenHandler>();

        services.AddTransient<IIdentityProvider, KeycloakIdentityProvider>();
```

> Se o overload `Configure(Action<HttpStandardResilienceOptions, IServiceProvider>)` não existir na versão resolvida, o equivalente é: `var pipeline = admin.AddStandardResilienceHandler(); services.AddOptions<HttpStandardResilienceOptions>(pipeline.PipelineName).Configure<IOptions<HttpResilienceOptions>>((resiliencia, politica) => { /* mesmo corpo */ });`.

- [ ] **Step 7: Ligar o Keycloak em `AddInfrastructure`**

Em `src/IdentityGateway.Infrastructure/DependencyInjection.cs`, acrescentar `using IdentityGateway.Infrastructure.Identity.Keycloak;` e, na cadeia de `AddInfrastructure`, depois de `.AddOutbox(configuration)`:

```csharp
            .AddOutbox(configuration)
            .AddKeycloakIdentity(configuration);
```

(remover o `;` que fechava a cadeia em `.AddOutbox(configuration)`).

- [ ] **Step 8: Configuração da Api e dos testes que sobem a composição**

`src/IdentityGateway.Api/appsettings.json` — acrescentar a seção (Realm e ClientId são os mesmos em todo ambiente; `BaseUrl` e a chave, não):

```json
  "Keycloak": {
    "Admin": {
      "Realm": "identity-gateway",
      "ClientId": "identity-gateway"
    }
  },
```

`src/IdentityGateway.Api/appsettings.Development.json` — acrescentar:

```json
  "Keycloak": {
    "Admin": {
      "BaseUrl": "http://localhost:8081",
      "AllowInsecureHttp": true
    }
  }
```

(Separar com vírgula da seção `Database` existente.)

`tests/IdentityGateway.Api.FunctionalTests/IdentityGatewayApiFactory.cs` — acrescentar `using System.Security.Cryptography;`, o campo

```csharp
    /// <summary>
    /// Chave fictícia: a Api valida a configuração do Keycloak na subida, mas nenhum teste funcional chama o Keycloak.
    /// Gerada em memória — nunca um <c>.pem</c> versionado.
    /// </summary>
    private static readonly string ChaveFicticia = GerarChave();

    private static string GerarChave()
    {
        using RSA rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }
```

e, em `ConfigureWebHost`, depois da linha do `Outbox:Enabled`:

```csharp
        // O Keycloak não sobe na suíte funcional: nenhum endpoint desta fatia o chama, e o ready com Keycloak é
        // coberto pelos testes de integração e pelo job de compose da CI. A porta 9 (discard) garante que, se algo
        // tentar, falhe na hora em vez de pendurar.
        builder.UseSetting("Keycloak:Admin:BaseUrl", "http://127.0.0.1:9");
        builder.UseSetting("Keycloak:Admin:PrivateKeyPem", ChaveFicticia);
```

`tests/IdentityGateway.Infrastructure.IntegrationTests/DependencyInjectionTests.cs`:
- em `ConfiguracaoValida`, acrescentar ao dicionário:

```csharp
                ["Keycloak:Admin:BaseUrl"] = "http://keycloak.test:8080",
                ["Keycloak:Admin:Realm"] = "identity-gateway",
                ["Keycloak:Admin:ClientId"] = "identity-gateway",
                ["Keycloak:Admin:PrivateKeyPem"] = ChaveDeTeste,
                ["Keycloak:Admin:AllowInsecureHttp"] = "true",
```

- acrescentar o campo `private static readonly string ChaveDeTeste = Identity.Keycloak.ChavesDeTeste.Gerar().PemPrivado;`
- no `[Theory]` `TodasAsAbstracoesDaApplication_SaoResolviveis`, acrescentar `[InlineData(typeof(IIdentityProvider))]`;
- acrescentar o teste:

```csharp
    [Fact]
    public void IIdentityProvider_ETransient()
    {
        // Transient como o cliente tipado que envolve: ele não depende de DbContext, e a v2.3 o justificava como
        // Scoped por um motivo que não se aplica.
        using ServiceProvider provider = Construir(ConfiguracaoValida());
        using IServiceScope scope = provider.CreateScope();

        IIdentityProvider primeiro = scope.ServiceProvider.GetRequiredService<IIdentityProvider>();
        IIdentityProvider segundo = scope.ServiceProvider.GetRequiredService<IIdentityProvider>();

        primeiro.Should().NotBeSameAs(segundo);
    }
```

- [ ] **Step 9: Rodar a suíte da Infrastructure e a funcional**

Run: `dotnet test --project tests/IdentityGateway.Infrastructure.IntegrationTests/IdentityGateway.Infrastructure.IntegrationTests.csproj --filter-class "*DependencyInjectionTests"`
Expected: PASS (inclui o novo `InlineData` e `IIdentityProvider_ETransient`).

Run: `dotnet build`
Expected: 0 avisos, 0 erros.

(A suíte funcional precisa de Docker e é rodada na Task 7, junto do ajuste do `SegurancaTests`.)

- [ ] **Step 10: 🧪 Prova por mutação**

1. Em `FindOrganizationByAttributeAsync`, trocar `?q=` por `?searchQuery=` → `BuscaUsaQComBriefRepresentationFalse` vermelho. Reverter.
2. Em `KeycloakIdentityProvider`, trocar `Name: slug.Value` por `Name: name` → `NaoExiste_CriaComNameAliasDescriptionEAtributo` vermelho. Reverter.
3. Registrar `AddScoped<IIdentityProvider, ...>` no lugar do `AddTransient` → `IIdentityProvider_ETransient` vermelho. Reverter.

Rodar os Steps 5 e 9 de novo: PASS.

- [ ] **Step 11: Commit**

```bash
git add src tests
git commit -m "feat: porta IIdentityProvider e adaptador do Keycloak com EnsureOrganizationAsync" -m "Cliente da Admin API com resiliencia por fora e token por dentro; Name da Organization e o slug." -m "Mutacoes: searchQuery, Name=name e Scoped, todas pegas."
```

---

### Task 7: Health check do Keycloak

**Files:**
- Create: `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakHealthCheck.cs`
- Modify: `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakServiceCollectionExtensions.cs`
- Modify: `src/IdentityGateway.Api/Program.cs:101` (comentário do ready)
- Modify: `tests/IdentityGateway.Api.FunctionalTests/SegurancaTests.cs` (teste `OsHealthChecks_ContinuamAbertos`)
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakHealthCheckTests.cs`

**Interfaces:**
- Consumes: `ServiceAccountTokenCache.ObterAsync` (Task 4).
- Produces: `internal sealed class KeycloakHealthCheck(ServiceAccountTokenCache) : IHealthCheck`; registro `"keycloak"`, tag `ready`, `failureStatus: Unhealthy`, `timeout: 5s`.

- [ ] **Step 1: Escrever os testes sem container (falham: o check não existe)**

Create `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakHealthCheckTests.cs`:

```csharp
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O ready do Keycloak cai para Unhealthy — nunca Degraded — e não pendura a sonda.
/// </summary>
/// <remarks>
/// O caso saudável, contra o Keycloak real, está em <c>KeycloakRealTests</c>.
/// </remarks>
public sealed class KeycloakHealthCheckTests
{
    /// <summary>
    /// A composição real (<c>AddInfrastructure</c>) apontada para um Keycloak. A <c>KeycloakFixture</c> (Task 9) usa a
    /// mesma, acrescentando handlers de teste antes de construir.
    /// </summary>
    internal static ServiceCollection ColecaoDaComposicao(string baseUrl, string? pem = null)
    {
        IConfiguration configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = "Host=localhost;Database=x;Username=u;Password=p",
                ["Jwt:Issuer"] = "identitygateway",
                ["Jwt:Audience"] = "identitygateway-api",
                ["Jwt:SigningKey"] = new string('k', 32),
                ["Keycloak:Admin:BaseUrl"] = baseUrl,
                ["Keycloak:Admin:Realm"] = "identity-gateway",
                ["Keycloak:Admin:ClientId"] = "identity-gateway",
                ["Keycloak:Admin:PrivateKeyPem"] = pem ?? ChavesDeTeste.Gerar().PemPrivado,
                ["Keycloak:Admin:AllowInsecureHttp"] = "true",
                ["HttpResilience:MaxRetryAttempts"] = "1",
            })
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddInfrastructure(configuracao);

        return services;
    }

    internal static ServiceProvider Composicao(string baseUrl, string? pem = null) =>
        ColecaoDaComposicao(baseUrl, pem).BuildServiceProvider(validateScopes: true);

    private static async Task<HealthReportEntry> Checar(ServiceProvider provider, CancellationToken ct)
    {
        HealthReport relatorio = await provider.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(registro => registro.Name == "keycloak", ct);

        return relatorio.Entries["keycloak"];
    }

    [Fact]
    public async Task KeycloakInalcancavel_Unhealthy()
    {
        // Degraded responderia 200 no /health/ready, e a instância nunca sairia do balanceador.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = Composicao("http://127.0.0.1:9");

        HealthReportEntry entrada = await Checar(provider, ct);

        entrada.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task KeycloakQueAceitaENaoResponde_UnhealthyDentroDoTimeoutDoCheck()
    {
        // Um listener que aceita a conexão e nunca responde: sem timeout, a sonda penduraria pelos 100s padrão do
        // HttpClient, e o orquestrador trataria a instância como travada.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using TcpListener buracoNegro = new(IPAddress.Loopback, 0);
        buracoNegro.Start();
        int porta = ((IPEndPoint)buracoNegro.LocalEndpoint).Port;

        await using ServiceProvider provider = Composicao($"http://127.0.0.1:{porta}");

        Stopwatch cronometro = Stopwatch.StartNew();
        HealthReportEntry entrada = await Checar(provider, ct);
        cronometro.Stop();

        entrada.Status.Should().Be(HealthStatus.Unhealthy);
        cronometro.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8));
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test --project tests/IdentityGateway.Infrastructure.IntegrationTests/IdentityGateway.Infrastructure.IntegrationTests.csproj --filter-class "*KeycloakHealthCheckTests"`
Expected: FAIL — `KeyNotFoundException` em `Entries["keycloak"]` (o check não está registrado).

- [ ] **Step 3: Implementar e registrar**

Create `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakHealthCheck.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// O Keycloak está pronto para a Gateway se ela consegue obter o token do service account.
/// </summary>
/// <remarks>
/// <para>
/// <b>Obter o token, e não só ler a metadata OIDC.</b> A metadata responderia 200 com a chave errada, o realm sem o
/// client ou o certificado dessincronizado. Obter o token prova chave, realm e <c>private_key_jwt</c> de uma vez — e
/// custa zero por sonda enquanto o token do cache vale.
/// </para>
/// <para>
/// Devolve o <c>FailureStatus</c> do registro (<c>Unhealthy</c>): o <c>MapHealthChecks</c> responde 200 para
/// <c>Degraded</c>, e um check degradado nunca tiraria a instância do balanceador.
/// </para>
/// </remarks>
internal sealed class KeycloakHealthCheck(ServiceAccountTokenCache cache) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            _ = await cache.ObterAsync(cancellationToken);
            return HealthCheckResult.Healthy("Token do service account obtido.");
        }
        catch (HttpRequestException excecao)
        {
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                "Não foi possível obter o token do service account no Keycloak.",
                excecao);
        }
    }
}
```

Em `KeycloakServiceCollectionExtensions.AddKeycloakIdentity`, depois do registro de `IIdentityProvider` (e o `using Microsoft.Extensions.Diagnostics.HealthChecks;`):

```csharp
        // Registrado aqui, e não pela Api: o check depende do cache do token, que é interno a esta camada, e a Api
        // não conhece nenhum tipo do Keycloak (teste de arquitetura). A tag "ready" é a que o /health/ready filtra.
        services.AddHealthChecks().AddCheck<KeycloakHealthCheck>(
            "keycloak",
            failureStatus: HealthStatus.Unhealthy,
            tags: ["ready"],
            timeout: TimeSpan.FromSeconds(5));
```

Em `src/IdentityGateway.Api/Program.cs`, o comentário da linha 101 passa a:

```csharp
// ready: posso receber tráfego. Checa Postgres, Redis e o Keycloak (obtendo o token do service account) — sem eles,
// a instância sai do balanceador.
```

- [ ] **Step 4a: Ajustar o teste de DI que o `AddHealthChecks` quebra**

`AddHealthChecks()` registra o `HealthCheckPublisherHostedService` como `IHostedService`. O teste
`OutboxDesligado_NaoRegistraODespachante` (em `DependencyInjectionTests.cs`) afirmava que **nenhum** hosted service
existia; o que ele quer provar é que o **despachante** não existe. Trocar a asserção:

```csharp
        provider.GetServices<IHostedService>().Should().NotContain(
            servico => servico.GetType().Name == "OutboxWorker",
            "com o outbox desligado, ninguém despacha sozinho");
```

- [ ] **Step 4: Ajustar o teste funcional de health**

Em `tests/IdentityGateway.Api.FunctionalTests/SegurancaTests.cs`, substituir o corpo de `OsHealthChecks_ContinuamAbertos` por:

```csharp
    [Fact]
    public async Task OsHealthChecks_ContinuamAbertos()
    {
        // Exigir token no health check quebraria o orquestrador: o Kubernetes não se autentica, e a instância
        // saudável seria marcada como morta e reiniciada em laço.
        //
        // Só o live: o ready passou a exigir o Keycloak, que a suíte funcional não sobe. O ready é coberto pelos
        // testes de integração (Unhealthy com Keycloak fora, Healthy contra o container) e pelo job de compose da CI.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage live = await client.GetAsync("/health/live", ct);

        live.StatusCode.Should().Be(HttpStatusCode.OK);
    }
```

- [ ] **Step 5: Rodar e ver passar**

Run: `dotnet test --project tests/IdentityGateway.Infrastructure.IntegrationTests/IdentityGateway.Infrastructure.IntegrationTests.csproj --filter-class "*KeycloakHealthCheckTests"`
Expected: PASS (2 testes).

Run: `dotnet test --project tests/IdentityGateway.Infrastructure.IntegrationTests/IdentityGateway.Infrastructure.IntegrationTests.csproj --filter-class "*DependencyInjectionTests"`
Expected: PASS (inclui `OutboxDesligado_NaoRegistraODespachante` ajustado).

Run: `dotnet test --project tests/IdentityGateway.Api.FunctionalTests/IdentityGateway.Api.FunctionalTests.csproj`
Expected: PASS (suíte funcional inteira; precisa de Docker).

- [ ] **Step 6: 🧪 Prova por mutação**

1. Trocar `failureStatus: HealthStatus.Unhealthy` por `HealthStatus.Degraded` → `KeycloakInalcancavel_Unhealthy` vermelho. Reverter.
2. Remover o `timeout:` do registro e o `http.Timeout = ...` do cliente do token → `KeycloakQueAceitaENaoResponde_UnhealthyDentroDoTimeoutDoCheck` vermelho (ou estoura o tempo). Reverter.

Rodar o Step 5 de novo: PASS.

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "feat: health check do Keycloak no ready, obtendo o token do service account" -m "Mutacoes: failureStatus Degraded e sem timeout, ambas pegas."
```

---

### Task 8: Realm de bootstrap e regras de arquitetura

**Files:**
- Create: `keycloak/bootstrap/realm-identity-gateway.json`
- Create: `tests/IdentityGateway.ArchitectureTests/RaizDoRepositorio.cs`
- Create: `tests/IdentityGateway.ArchitectureTests/RegrasDoRealmTests.cs`
- Create: `tests/IdentityGateway.ArchitectureTests/RegrasDoKeycloakTests.cs`

**Interfaces:**
- Produces: realm `identity-gateway` com client `identity-gateway` e service account só com `manage-organizations`; placeholder `${GATEWAY_CLIENT_CERT}`.
- Produces (teste): `internal static class RaizDoRepositorio { static string Caminho(params string[] partes); }`

- [ ] **Step 1: Escrever os testes (falham: o realm e as regras não existem)**

Create `tests/IdentityGateway.ArchitectureTests/RaizDoRepositorio.cs`:

```csharp
namespace IdentityGateway.ArchitectureTests;

/// <summary>Localiza a raiz do repositório subindo a partir da pasta do binário de teste.</summary>
internal static class RaizDoRepositorio
{
    public static string Caminho(params string[] partes)
    {
        DirectoryInfo? pasta = new(AppContext.BaseDirectory);

        while (pasta is not null && !File.Exists(Path.Combine(pasta.FullName, "IdentityGateway.slnx")))
        {
            pasta = pasta.Parent;
        }

        if (pasta is null)
        {
            throw new InvalidOperationException("Raiz do repositório (IdentityGateway.slnx) não encontrada.");
        }

        return Path.Combine([pasta.FullName, .. partes]);
    }
}
```

Create `tests/IdentityGateway.ArchitectureTests/RegrasDoRealmTests.cs`:

```csharp
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
                foreach (var item in Percorrer(propriedade.Value, $"{caminho}.{propriedade.Name}"))
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
                foreach (var filho in Percorrer(item, $"{caminho}[{indice++}]"))
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
```

Create `tests/IdentityGateway.ArchitectureTests/RegrasDoKeycloakTests.cs`:

```csharp
using System.Reflection;
using NetArchTest.Rules;
using ArchTestResult = NetArchTest.Rules.TestResult;

namespace IdentityGateway.ArchitectureTests;

/// <summary>
/// Nenhum tipo do Keycloak atravessa a fronteira de <c>Infrastructure.Identity.Keycloak</c> (ADR-008).
/// </summary>
public sealed class RegrasDoKeycloakTests
{
    private const string NamespaceKeycloak = "IdentityGateway.Infrastructure.Identity.Keycloak";

    private static readonly Assembly Application = typeof(IdentityGateway.Application.AssemblyMarker).Assembly;
    private static readonly Assembly Infrastructure = typeof(IdentityGateway.Infrastructure.AssemblyMarker).Assembly;
    private static readonly Assembly Api = typeof(Program).Assembly;

    [Fact]
    public void ONamespaceDoKeycloakTemTipos()
    {
        // Sem isto, as regras abaixo passariam vazias se o namespace fosse renomeado: "nenhum tipo depende de X" é
        // verdade trivial quando X não existe.
        Types.InAssembly(Infrastructure).That().ResideInNamespace(NamespaceKeycloak).GetTypes()
            .Should().NotBeEmpty();
    }

    [Fact]
    public void AApiNaoConheceOKeycloak()
    {
        ArchTestResult resultado = Types.InAssembly(Api)
            .Should().NotHaveDependencyOn(NamespaceKeycloak)
            .GetResult();

        resultado.Should().NaoTerViolacao("a Api fala com a porta IIdentityProvider, nunca com o adaptador");
    }

    [Fact]
    public void AApplicationNaoConheceOKeycloak()
    {
        ArchTestResult resultado = Types.InAssembly(Application)
            .Should().NotHaveDependencyOn(NamespaceKeycloak)
            .GetResult();

        resultado.Should().NaoTerViolacao("a Application declara a porta; o Keycloak é detalhe de quem a implementa");
    }

    [Fact]
    public void ORestoDaInfrastructureSoAlcancaOKeycloakPeloRegistro()
    {
        ArchTestResult resultado = Types.InAssembly(Infrastructure)
            .That().DoNotResideInNamespace(NamespaceKeycloak)
            .And().DoNotHaveName("DependencyInjection")
            .Should().NotHaveDependencyOn(NamespaceKeycloak)
            .GetResult();

        resultado.Should().NaoTerViolacao(
            "a troca de provedor de identidade precisa ficar contida em Identity/Keycloak; o único ponto de contato "
            + "é o registro em DependencyInjection");
    }
}
```

> `typeof(Program)`: é o mesmo tipo que os testes funcionais usam em `WebApplicationFactory<Program>`. Se `RegrasDaApiTests.cs` já obtém o assembly da Api de outro jeito, use o mesmo.

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test --project tests/IdentityGateway.ArchitectureTests/IdentityGateway.ArchitectureTests.csproj --filter-class "*RegrasDoRealmTests"`
Expected: FAIL — `FileNotFoundException` (o realm não existe).

- [ ] **Step 3: Criar o realm**

Create `keycloak/bootstrap/realm-identity-gateway.json`:

```json
{
  "realm": "identity-gateway",
  "enabled": true,
  "organizationsEnabled": true,
  "clients": [
    {
      "clientId": "identity-gateway",
      "name": "IdentityGateway (service account)",
      "description": "Client confidencial da Gateway. Só client_credentials, autenticado por private_key_jwt.",
      "enabled": true,
      "protocol": "openid-connect",
      "publicClient": false,
      "bearerOnly": false,
      "standardFlowEnabled": false,
      "implicitFlowEnabled": false,
      "directAccessGrantsEnabled": false,
      "serviceAccountsEnabled": true,
      "clientAuthenticatorType": "client-jwt",
      "attributes": {
        "jwt.credential.certificate": "${GATEWAY_CLIENT_CERT}",
        "token.endpoint.auth.signing.alg": "PS256"
      }
    }
  ],
  "users": [
    {
      "username": "service-account-identity-gateway",
      "enabled": true,
      "serviceAccountClientId": "identity-gateway",
      "clientRoles": {
        "realm-management": [
          "manage-organizations"
        ]
      }
    }
  ]
}
```

- [ ] **Step 4: Rodar e ver passar**

Run: `dotnet test --project tests/IdentityGateway.ArchitectureTests/IdentityGateway.ArchitectureTests.csproj`
Expected: PASS (suíte de arquitetura inteira, incluindo as 9 regras novas).

- [ ] **Step 5: 🧪 Prova por mutação**

1. No realm, inserir no client `"secret": "x"` → `NenhumaChaveDeCredencial` vermelho. Reverter.
2. Trocar o placeholder por `"${GATEWAY_CLIENT_CERT:MIIC}"` → `TodoPlaceholderEPuro` vermelho. Reverter.
3. Inserir no realm `"smtpServer": { "password": "x" }` → `NenhumaChaveDeCredencial` vermelho. Reverter.
4. Acrescentar `"manage-realm"` aos papéis do service account → `ServiceAccountSoComManageOrganizations` vermelho. Reverter.
5. Em `src/IdentityGateway.Infrastructure/Persistence/Outbox/OutboxProcessor.cs`, acrescentar um campo `private static readonly Type _x = typeof(Identity.Keycloak.KeycloakAdminOptions);` → `ORestoDaInfrastructureSoAlcancaOKeycloakPeloRegistro` vermelho. Reverter. (`typeof`, não `nameof`: `nameof` vira uma constante de texto na compilação e não deixa dependência no IL — a regra não teria o que pegar.)

Rodar o Step 4 de novo: PASS.

- [ ] **Step 6: Commit**

```bash
git add keycloak tests/IdentityGateway.ArchitectureTests
git commit -m "feat: realm de bootstrap e regras de arquitetura do Keycloak" -m "Mutacoes: secret, placeholder com default, smtpServer.password, manage-realm e referencia ao Keycloak fora do namespace, todas pegas."
```

---

### Task 9: Fixture do Keycloak real — token, menor privilégio, smoke e health

**Files:**
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakFixture.cs`
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakRealTests.cs`

**Interfaces:**
- Consumes: realm (Task 8), `KeycloakHealthCheckTests.Composicao(string baseUrl, string? pem)` (Task 7), `ITokenEndpoint`, `ServiceAccountTokenCache`.
- Produces (teste): `public sealed class KeycloakFixture : IAsyncLifetime { const string Realm; string BaseUrl; ParDeChaves Chaves; ServiceProvider CriarProvider(Action<IServiceCollection>? ajustar = null, string? pem = null); Task<HttpClient> CriarClienteMasterAsync(CancellationToken); Task<string> CriarOrganizacaoComoMasterAsync(string alias, string? tenantId, CancellationToken); Task<JsonElement> LerOrganizacaoCruaAsync(string id, CancellationToken); Task<int> ContarPorAliasAsync(string alias, CancellationToken); static TenantSlug SlugUnico(); }`

> Docker precisa estar rodando. A primeira execução baixa a imagem do Keycloak (~450 MB).

- [ ] **Step 1: Escrever a fixture**

Create `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakFixture.cs`:

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.Keycloak;

[assembly: AssemblyFixture(typeof(KeycloakFixture))]

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// Um Keycloak 26.7.4 de verdade para o assembly inteiro, importando o MESMO realm que o compose usa.
/// </summary>
/// <remarks>
/// <para>
/// <b>Um container por assembly</b>, e não por classe: o Keycloak leva dezenas de segundos para subir, e o xUnit v3
/// rodaria as classes em paralelo, cada uma com o seu.
/// </para>
/// <para>
/// <b>O realm é o arquivo do repositório</b>, não uma cópia de teste: o teste prova o arquivo que o compose importa.
/// O certificado entra pelo mesmo placeholder, por variável de ambiente.
/// </para>
/// <para>
/// <b>Isolamento por slug único.</b> Os testes compartilham o realm; cada um cria as próprias Organizations com slug
/// aleatório e nunca afirma sobre a contagem global.
/// </para>
/// </remarks>
public sealed class KeycloakFixture : IAsyncLifetime
{
    public const string Realm = "identity-gateway";

    private readonly KeycloakContainer _container;

    public KeycloakFixture()
    {
        Chaves = ChavesDeTeste.Gerar();

        _container = new KeycloakBuilder("quay.io/keycloak/keycloak:26.7.4")
            .WithRealm(CaminhoDoRealm())
            .WithEnvironment("GATEWAY_CLIENT_CERT", Chaves.CertificadoBase64)
            .Build();
    }

    public ParDeChaves Chaves { get; }

    public string BaseUrl => _container.GetBaseAddress().TrimEnd('/');

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    /// <summary>Slug aleatório, válido e curto — o isolamento entre testes.</summary>
    public static TenantSlug SlugUnico() => TenantSlug.Create($"t-{Guid.NewGuid():N}"[..18]).Value;

    /// <summary>
    /// A composição real (<c>AddInfrastructure</c>) apontada para este Keycloak, com handlers de teste opcionais
    /// acrescentados antes de construir.
    /// </summary>
    public ServiceProvider CriarProvider(Action<IServiceCollection>? ajustar = null, string? pem = null)
    {
        ServiceCollection services = KeycloakHealthCheckTests.ColecaoDaComposicao(BaseUrl, pem ?? Chaves.PemPrivado);
        ajustar?.Invoke(services);

        return services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>Cliente HTTP autenticado como admin do realm master — para preparar e conferir estado.</summary>
    public async Task<HttpClient> CriarClienteMasterAsync(CancellationToken cancellationToken)
    {
        HttpClient http = new() { BaseAddress = new Uri($"{BaseUrl}/") };

        using FormUrlEncodedContent corpo = new(
        [
            new("grant_type", "password"),
            new("client_id", "admin-cli"),
            new("username", KeycloakBuilder.DefaultUsername),
            new("password", KeycloakBuilder.DefaultPassword),
        ]);

        using HttpResponseMessage resposta = await http.PostAsync(
            new Uri("realms/master/protocol/openid-connect/token", UriKind.Relative), corpo, cancellationToken);
        resposta.EnsureSuccessStatusCode();

        using JsonDocument token = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync(cancellationToken));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", token.RootElement.GetProperty("access_token").GetString());

        return http;
    }

    /// <summary>Cria uma Organization por fora da Gateway, como o master faria.</summary>
    public async Task<string> CriarOrganizacaoComoMasterAsync(
        string alias, string? tenantId, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);

        Dictionary<string, object> organizacao = new()
        {
            ["name"] = alias,
            ["alias"] = alias,
            ["enabled"] = true,
        };

        if (tenantId is not null)
        {
            organizacao["attributes"] = new Dictionary<string, string[]> { ["gateway_tenant_id"] = [tenantId] };
        }

        using HttpResponseMessage resposta = await master.PostAsJsonAsync(
            $"admin/realms/{Realm}/organizations", organizacao, cancellationToken);
        resposta.EnsureSuccessStatusCode();

        return resposta.Headers.Location!.Segments[^1];
    }

    /// <summary>A Organization em JSON cru, lida pelo master — nunca pelo DTO do adaptador sob teste.</summary>
    public async Task<JsonElement> LerOrganizacaoCruaAsync(string id, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        string json = await master.GetStringAsync(
            new Uri($"admin/realms/{Realm}/organizations/{id}", UriKind.Relative), cancellationToken);

        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>Quantas Organizations têm o alias — pela chave especial <c>alias</c> do <c>q</c>.</summary>
    public async Task<int> ContarPorAliasAsync(string alias, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        string json = await master.GetStringAsync(
            new Uri($"admin/realms/{Realm}/organizations?q={Uri.EscapeDataString($"alias:{alias}")}&max=10",
                UriKind.Relative),
            cancellationToken);

        using JsonDocument lista = JsonDocument.Parse(json);
        return lista.RootElement.GetArrayLength();
    }

    private static string CaminhoDoRealm()
    {
        DirectoryInfo? pasta = new(AppContext.BaseDirectory);

        while (pasta is not null && !File.Exists(Path.Combine(pasta.FullName, "IdentityGateway.slnx")))
        {
            pasta = pasta.Parent;
        }

        return Path.Combine(
            pasta?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada."),
            "keycloak", "bootstrap", "realm-identity-gateway.json");
    }
}
```

- [ ] **Step 2: Escrever os testes contra o Keycloak real**

Create `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakRealTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O service account autentica por <c>private_key_jwt</c> com <c>aud</c> = issuer, e tem só o papel que precisa.
/// </summary>
public sealed class KeycloakRealTests(KeycloakFixture keycloak)
{
    [Fact]
    public async Task ComAChaveRegistrada_ObtemToken()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();

        TokenObtido token = await provider.GetRequiredService<ITokenEndpoint>().ObterAsync(ct);

        token.AccessToken.Should().NotBeNullOrEmpty();
        token.ExpiraEm.Should().BePositive();
    }

    [Fact]
    public async Task ComOutraChave_RecebeInvalidClient()
    {
        // A metade que prova que a primeira não passa por acaso: assinatura com chave que o realm não conhece.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider(pem: ChavesDeTeste.Gerar().PemPrivado);

        Func<Task> obter = () => provider.GetRequiredService<ITokenEndpoint>().ObterAsync(ct);

        await obter.Should().ThrowAsync<HttpRequestException>().WithMessage("*invalid_client*");
    }

    [Fact]
    public async Task ServiceAccount_RecebeProibidoEmUsuarios()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();
        string token = await provider.GetRequiredService<ServiceAccountTokenCache>().ObterAsync(ct);

        using HttpClient http = new() { BaseAddress = new Uri($"{keycloak.BaseUrl}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage resposta = await http.GetAsync(
            new Uri($"admin/realms/{KeycloakFixture.Realm}/users", UriKind.Relative), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ServiceAccount_TemExatamenteManageOrganizations()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient master = await keycloak.CriarClienteMasterAsync(ct);
        string realm = KeycloakFixture.Realm;

        using JsonDocument usuarios = JsonDocument.Parse(await master.GetStringAsync(
            new Uri($"admin/realms/{realm}/users?username=service-account-identity-gateway&exact=true",
                UriKind.Relative), ct));
        string usuarioId = usuarios.RootElement[0].GetProperty("id").GetString()!;

        using JsonDocument clientes = JsonDocument.Parse(await master.GetStringAsync(
            new Uri($"admin/realms/{realm}/clients?clientId=realm-management", UriKind.Relative), ct));
        string realmManagementId = clientes.RootElement[0].GetProperty("id").GetString()!;

        using JsonDocument papeis = JsonDocument.Parse(await master.GetStringAsync(
            new Uri($"admin/realms/{realm}/users/{usuarioId}/role-mappings/clients/{realmManagementId}",
                UriKind.Relative), ct));

        papeis.RootElement.EnumerateArray().Select(papel => papel.GetProperty("name").GetString())
            .Should().Equal("manage-organizations");
    }

    [Fact]
    public async Task EndpointDeOrganizations_NaoResponde404()
    {
        // O smoke do realm: sem organizationsEnabled, este endpoint responde 404 e o import passa sem erro.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();
        string token = await provider.GetRequiredService<ServiceAccountTokenCache>().ObterAsync(ct);

        using HttpClient http = new() { BaseAddress = new Uri($"{keycloak.BaseUrl}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage resposta = await http.GetAsync(
            new Uri($"admin/realms/{KeycloakFixture.Realm}/organizations?max=1", UriKind.Relative), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HealthCheck_ComKeycloakDePe_Healthy()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();

        HealthReport relatorio = await provider.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(registro => registro.Name == "keycloak", ct);

        relatorio.Entries["keycloak"].Status.Should().Be(HealthStatus.Healthy);
    }
}
```

- [ ] **Step 3: Rodar e ver passar**

Run: `dotnet test --project tests/IdentityGateway.Infrastructure.IntegrationTests/IdentityGateway.Infrastructure.IntegrationTests.csproj --filter-class "*KeycloakRealTests"`
Expected: PASS (6 testes). Se `ComAChaveRegistrada_ObtemToken` falhar com `invalid_client`, conferir **primeiro** o header do assertion (Task 2: sem `kid`) e o `aud` (issuer igual ao `BaseUrl` do container).

- [ ] **Step 4: 🧪 Prova por mutação**

1. Em `ClientAssertionFactory`, trocar `new RsaSecurityKey(chave.Rsa)` por `new RsaSecurityKey(chave.Rsa) { KeyId = "qualquer" }` → `ComAChaveRegistrada_ObtemToken` vermelho (`invalid_client`). Reverter.
2. No realm, trocar `"organizationsEnabled": true` por `false` → `EndpointDeOrganizations_NaoResponde404` vermelho. Reverter.
3. No realm, acrescentar `"view-users"` aos papéis → `ServiceAccount_RecebeProibidoEmUsuarios` vermelho (e a regra da Task 8 também). Reverter.

Rodar o Step 3 de novo: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/IdentityGateway.Infrastructure.IntegrationTests
git commit -m "test: Keycloak real para o assembly, provando token, menor privilegio e health" -m "Mutacoes: kid presente, organizationsEnabled false e view-users no service account, todas pegas."
```

---

### Task 10: `EnsureOrganizationAsync` contra o Keycloak real

**Files:**
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/HandlerDeInterceptacao.cs`
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/EnsureOrganizationContraKeycloakTests.cs`

**Interfaces:**
- Consumes: `KeycloakFixture` (Task 9), `IIdentityProvider`, `KeycloakAdminClient.FindOrganizationByAttributeAsync`.
- Produces (teste): `internal sealed class Interceptacao { ... }`, `internal sealed class HandlerDeInterceptacao(Interceptacao) : DelegatingHandler`, `internal static class InterceptacaoExtensions { static void Interceptar(this IServiceCollection, Interceptacao); }`

- [ ] **Step 1: Escrever o handler de interceptação**

Create `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/HandlerDeInterceptacao.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O que o teste quer que aconteça com cada requisição à Admin API, e o registro do que aconteceu.
/// </summary>
/// <remarks>
/// O <c>ordinal</c> conta por método, a partir de 1: "o primeiro POST", "o segundo GET".
/// </remarks>
internal sealed class Interceptacao
{
    private readonly ConcurrentDictionary<HttpMethod, int> _ordinais = new();

    /// <summary>Espera antes de enviar (barreira de corrida).</summary>
    public Func<HttpRequestMessage, int, Task>? AntesDeEnviar { get; init; }

    /// <summary>Responde este status sem enviar ao Keycloak.</summary>
    public Func<HttpRequestMessage, int, HttpStatusCode?>? ResponderSemEnviar { get; init; }

    /// <summary>Troca o token por lixo antes de enviar.</summary>
    public Func<HttpRequestMessage, int, bool>? TrocarTokenPorLixo { get; init; }

    /// <summary>Envia, descarta a resposta e lança — a resposta "se perdeu na rede".</summary>
    public Func<HttpRequestMessage, int, bool>? PerderResposta { get; init; }

    public ConcurrentQueue<(HttpMethod Metodo, HttpStatusCode? Status)> Registro { get; } = new();

    public int Contar(HttpMethod metodo) => Registro.Count(chamada => chamada.Metodo == metodo);

    public IEnumerable<HttpStatusCode?> Status(HttpMethod metodo) =>
        Registro.Where(chamada => chamada.Metodo == metodo).Select(chamada => chamada.Status);

    internal int ProximoOrdinal(HttpMethod metodo) => _ordinais.AddOrUpdate(metodo, 1, (_, atual) => atual + 1);
}

/// <summary>
/// Pendurado no FIM do pipeline do <c>KeycloakAdminClient</c> — dentro da resiliência e do handler do token —, é o
/// último a ver a requisição antes da rede.
/// </summary>
internal sealed class HandlerDeInterceptacao(Interceptacao estado) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int ordinal = estado.ProximoOrdinal(request.Method);

        if (estado.AntesDeEnviar is not null)
        {
            await estado.AntesDeEnviar(request, ordinal);
        }

        if (estado.ResponderSemEnviar?.Invoke(request, ordinal) is { } status)
        {
            estado.Registro.Enqueue((request.Method, status));
            return new HttpResponseMessage(status);
        }

        if (estado.TrocarTokenPorLixo?.Invoke(request, ordinal) == true)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "token-lixo");
        }

        HttpResponseMessage resposta = await base.SendAsync(request, cancellationToken);
        estado.Registro.Enqueue((request.Method, resposta.StatusCode));

        if (estado.PerderResposta?.Invoke(request, ordinal) == true)
        {
            resposta.Dispose();
            throw new HttpRequestException("Resposta perdida (injetado pelo teste).");
        }

        return resposta;
    }
}

internal static class InterceptacaoExtensions
{
    /// <summary>Acrescenta a interceptação ao fim do pipeline do cliente da Admin API.</summary>
    /// <remarks>Uma instância nova de handler por pipeline, com o estado compartilhado.</remarks>
    public static void Interceptar(this IServiceCollection services, Interceptacao estado) =>
        services.AddHttpClient<KeycloakAdminClient>().AddHttpMessageHandler(() => new HandlerDeInterceptacao(estado));
}
```

- [ ] **Step 2: Escrever os testes**

Create `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/EnsureOrganizationContraKeycloakTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// <c>EnsureOrganizationAsync</c> contra o Keycloak real: cria, reencontra, resolve corrida e recusa o que não é seu.
/// </summary>
public sealed class EnsureOrganizationContraKeycloakTests(KeycloakFixture keycloak)
{
    [Fact]
    public async Task Cria_ELeituraCruaMostraNameSlugDescriptionEAtributo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();
        TenantId tenant = TenantId.New();
        TenantSlug slug = KeycloakFixture.SlugUnico();

        // Acento, aspas e & no nome de exibição: precisam chegar intactos na description.
        const string nome = "Café & Cia \"Ltda\"";

        string id = await provider.GetRequiredService<IIdentityProvider>()
            .EnsureOrganizationAsync(tenant, slug, nome, ct);

        JsonElement crua = await keycloak.LerOrganizacaoCruaAsync(id, ct);

        crua.GetProperty("name").GetString().Should().Be(slug.Value);
        crua.GetProperty("alias").GetString().Should().Be(slug.Value);
        crua.GetProperty("description").GetString().Should().Be(nome);
        crua.GetProperty("attributes").GetProperty("gateway_tenant_id")[0].GetString()
            .Should().Be(tenant.Value.ToString());
    }

    [Fact]
    public async Task BuscaComDistrator_DevolveSoAOrganizacaoDoTenant()
    {
        // Se o q fosse ignorado, a busca devolveria as Organizations do realm; com UMA só no realm, o teste de
        // idempotência passaria sem provar nada. Aqui há duas, e a busca precisa separar.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();
        IIdentityProvider identidade = provider.GetRequiredService<IIdentityProvider>();

        TenantId x = TenantId.New();
        TenantId y = TenantId.New();
        await identidade.EnsureOrganizationAsync(x, KeycloakFixture.SlugUnico(), "X", ct);
        string idY = await identidade.EnsureOrganizationAsync(y, KeycloakFixture.SlugUnico(), "Y", ct);

        OrganizationRepresentation? achada = await provider.GetRequiredService<KeycloakAdminClient>()
            .FindOrganizationByAttributeAsync(KeycloakIdentityProvider.AtributoDoTenant, y.Value.ToString(), ct);

        achada!.Id.Should().Be(idY);
    }

    [Fact]
    public async Task DuasChamadasEmSequencia_MesmoIdEUmaOrganizacao()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();
        IIdentityProvider identidade = provider.GetRequiredService<IIdentityProvider>();
        TenantId tenant = TenantId.New();
        TenantSlug slug = KeycloakFixture.SlugUnico();

        string primeiro = await identidade.EnsureOrganizationAsync(tenant, slug, "Acme", ct);
        string segundo = await identidade.EnsureOrganizationAsync(tenant, slug, "Acme", ct);

        segundo.Should().Be(primeiro);
        (await keycloak.ContarPorAliasAsync(slug.Value, ct)).Should().Be(1);
    }

    [Fact]
    public async Task DuasChamadasEmParalelo_MesmoIdUmaOrganizacaoEUm409()
    {
        // Sem a barreira as duas tendem a serializar, e o 409 nunca acontece: o teste passaria sem exercitar a
        // reconsulta. A barreira só libera os dois GETs iniciais juntos — as duas chamadas veem "não existe" e
        // disputam o POST.
        CancellationToken ct = TestContext.Current.CancellationToken;
        TaskCompletionSource ambos = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int[] chegaram = [0];

        Interceptacao interceptacao = new()
        {
            AntesDeEnviar = async (pedido, ordinal) =>
            {
                if (pedido.Method == HttpMethod.Get && ordinal <= 2)
                {
                    if (Interlocked.Increment(ref chegaram[0]) == 2)
                    {
                        ambos.SetResult();
                    }

                    await ambos.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
                }
            },
        };

        await using ServiceProvider provider = keycloak.CriarProvider(services => services.Interceptar(interceptacao));
        TenantId tenant = TenantId.New();
        TenantSlug slug = KeycloakFixture.SlugUnico();

        string[] ids = await Task.WhenAll(
            provider.GetRequiredService<IIdentityProvider>().EnsureOrganizationAsync(tenant, slug, "Acme", ct),
            provider.GetRequiredService<IIdentityProvider>().EnsureOrganizationAsync(tenant, slug, "Acme", ct));

        ids[1].Should().Be(ids[0]);
        (await keycloak.ContarPorAliasAsync(slug.Value, ct)).Should().Be(1);
        interceptacao.Status(HttpMethod.Post).Should().Contain(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task SlugTomadoPorOrganizacaoDeFora_LancaInconsistencia()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        TenantSlug slug = KeycloakFixture.SlugUnico();
        await keycloak.CriarOrganizacaoComoMasterAsync(slug.Value, tenantId: null, ct);

        await using ServiceProvider provider = keycloak.CriarProvider();

        Func<Task> garantir = () => provider.GetRequiredService<IIdentityProvider>()
            .EnsureOrganizationAsync(TenantId.New(), slug, "Acme", ct);

        await garantir.Should().ThrowAsync<IdentityProviderInconsistencyException>();
    }

    [Fact]
    public async Task DuasOrganizacoesComOMesmoTenant_LancaInconsistencia()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        TenantId tenant = TenantId.New();
        await keycloak.CriarOrganizacaoComoMasterAsync(KeycloakFixture.SlugUnico().Value, tenant.Value.ToString(), ct);
        await keycloak.CriarOrganizacaoComoMasterAsync(KeycloakFixture.SlugUnico().Value, tenant.Value.ToString(), ct);

        await using ServiceProvider provider = keycloak.CriarProvider();

        Func<Task> garantir = () => provider.GetRequiredService<IIdentityProvider>()
            .EnsureOrganizationAsync(tenant, KeycloakFixture.SlugUnico(), "Acme", ct);

        await garantir.Should().ThrowAsync<IdentityProviderInconsistencyException>();
    }
}
```

- [ ] **Step 3: Rodar e ver passar**

Run: `dotnet test --project tests/IdentityGateway.Infrastructure.IntegrationTests/IdentityGateway.Infrastructure.IntegrationTests.csproj --filter-class "*EnsureOrganizationContraKeycloakTests"`
Expected: PASS (6 testes).

- [ ] **Step 4: 🧪 Prova por mutação**

1. Em `KeycloakAdminClient.FindOrganizationByAttributeAsync`, trocar `?q=` por `?searchQuery=` → `BuscaComDistrator_DevolveSoAOrganizacaoDoTenant` vermelho. Reverter.
2. Em `KeycloakIdentityProvider`, no `catch (KeycloakConflictException)`, lançar a inconsistência direto, sem reconsultar → `DuasChamadasEmParalelo_MesmoIdUmaOrganizacaoEUm409` vermelho. Reverter.
3. Em `KeycloakIdentityProvider`, remover a consulta inicial (ir direto ao POST) → `DuasChamadasEmSequencia_MesmoIdEUmaOrganizacao` continua verde (o 409 + reconsulta salvam) — **isso é esperado**, e é por isso que o teste do Step 1 da Task 11 conta os POSTs. Reverter.
4. Trocar `Description: name` por `Description: null` → `Cria_ELeituraCruaMostraNameSlugDescriptionEAtributo` vermelho. Reverter.

Rodar o Step 3 de novo: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/IdentityGateway.Infrastructure.IntegrationTests
git commit -m "test: EnsureOrganizationAsync contra o Keycloak real, com distrator e corrida deterministica" -m "Mutacoes: searchQuery, sem reconsulta pos-409 e description nula, todas pegas."
```

---

### Task 11: Resiliência do cliente da Admin API contra o Keycloak real

**Files:**
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/ResilienciaDoAdminClientTests.cs`

**Interfaces:**
- Consumes: `Interceptacao`, `InterceptacaoExtensions.Interceptar` (Task 10); `KeycloakFixture`; `ITokenEndpoint`, `KeycloakTokenClient`.

- [ ] **Step 1: Escrever os testes**

Create `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/ResilienciaDoAdminClientTests.cs`:

```csharp
using System.Net;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// A resiliência repete o que é seguro repetir, e só isso — medido contando requisições, não contando Organizations.
/// </summary>
/// <remarks>
/// Contar Organizations seria vacuoso: se o POST fosse repetido, o Keycloak responderia 409 (name e alias são únicos
/// no realm), o adaptador reconsultaria, e continuaria existindo uma só. Quem garantiria o resultado seria o Keycloak,
/// não o código sob teste.
/// </remarks>
public sealed class ResilienciaDoAdminClientTests(KeycloakFixture keycloak)
{
    [Fact]
    public async Task PostComRespostaPerdida_NaoERepetido()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Interceptacao interceptacao = new()
        {
            PerderResposta = (pedido, ordinal) => pedido.Method == HttpMethod.Post && ordinal == 1,
        };

        await using ServiceProvider provider = keycloak.CriarProvider(services => services.Interceptar(interceptacao));
        IIdentityProvider identidade = provider.GetRequiredService<IIdentityProvider>();
        TenantId tenant = TenantId.New();
        TenantSlug slug = KeycloakFixture.SlugUnico();

        Func<Task> primeira = () => identidade.EnsureOrganizationAsync(tenant, slug, "Acme", ct);

        // A exceção transiente sobe para quem chamou decidir — e o POST saiu UMA vez.
        await primeira.Should().ThrowAsync<HttpRequestException>();
        interceptacao.Contar(HttpMethod.Post).Should().Be(1);

        // A Organization foi criada; a próxima tentativa a encontra pelo atributo em vez de criar outra.
        string id = await identidade.EnsureOrganizationAsync(tenant, slug, "Acme", ct);

        id.Should().NotBeNullOrEmpty();
        interceptacao.Contar(HttpMethod.Post).Should().Be(1);
        (await keycloak.ContarPorAliasAsync(slug.Value, ct)).Should().Be(1);
    }

    [Fact]
    public async Task GetCom503_ERepetido()
    {
        // Controle positivo: sem ele, "a resiliência nem foi registrada" também passaria no teste acima.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Interceptacao interceptacao = new()
        {
            ResponderSemEnviar = (pedido, ordinal) =>
                pedido.Method == HttpMethod.Get && ordinal == 1 ? HttpStatusCode.ServiceUnavailable : null,
        };

        await using ServiceProvider provider = keycloak.CriarProvider(services => services.Interceptar(interceptacao));

        string id = await provider.GetRequiredService<IIdentityProvider>()
            .EnsureOrganizationAsync(TenantId.New(), KeycloakFixture.SlugUnico(), "Acme", ct);

        id.Should().NotBeNullOrEmpty();
        interceptacao.Status(HttpMethod.Get).Should().StartWith(
            new HttpStatusCode?[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK });
    }

    [Fact]
    public async Task PostCom401_ReenviaOCorpoComTokenNovo()
    {
        // O 401 acontece no POST — e não no GET que vem antes —, que é o caso em que o corpo precisa ser relido.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Interceptacao interceptacao = new()
        {
            TrocarTokenPorLixo = (pedido, ordinal) => pedido.Method == HttpMethod.Post && ordinal == 1,
        };

        int[] tokensPedidos = [0];

        await using ServiceProvider provider = keycloak.CriarProvider(services =>
        {
            services.Interceptar(interceptacao);

            // Conta os pedidos de token sem trocar o que o cache faz com eles.
            services.AddSingleton<ITokenEndpoint>(sp => new EndpointContador(
                ActivatorUtilities.CreateInstance<KeycloakTokenClient>(sp), tokensPedidos));
        });

        TenantSlug slug = KeycloakFixture.SlugUnico();

        string id = await provider.GetRequiredService<IIdentityProvider>()
            .EnsureOrganizationAsync(TenantId.New(), slug, "Acme", ct);

        id.Should().NotBeNullOrEmpty();
        interceptacao.Status(HttpMethod.Post).Should().Equal(HttpStatusCode.Unauthorized, HttpStatusCode.Created);
        tokensPedidos[0].Should().Be(2);
        (await keycloak.ContarPorAliasAsync(slug.Value, ct)).Should().Be(1);
    }

    private sealed class EndpointContador(ITokenEndpoint real, int[] contador) : ITokenEndpoint
    {
        public Task<TokenObtido> ObterAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref contador[0]);
            return real.ObterAsync(cancellationToken);
        }
    }
}
```

- [ ] **Step 2: Rodar e ver passar**

Run: `dotnet test --project tests/IdentityGateway.Infrastructure.IntegrationTests/IdentityGateway.Infrastructure.IntegrationTests.csproj --filter-class "*ResilienciaDoAdminClientTests"`
Expected: PASS (3 testes).

- [ ] **Step 3: 🧪 Prova por mutação**

1. Remover `resiliencia.Retry.DisableForUnsafeHttpMethods();` → `PostComRespostaPerdida_NaoERepetido` vermelho (POST == 2). Reverter.
2. Inverter a ordem: registrar `admin.AddHttpMessageHandler<ServiceAccountTokenHandler>()` **antes** de `AddStandardResilienceHandler` → rodar os três; registrar no commit o resultado (a ordem é garantida pela spec e pelo comentário; se nenhum ficar vermelho, anotar que a ordem é protegida só pela revisão). Reverter.
3. Trocar `StringContent` por `JsonContent.Create(organizacao, options: Json)` em `CreateOrganizationAsync` → rodar `PostCom401_ReenviaOCorpoComTokenNovo`; registrar o resultado no commit. Reverter.

Rodar o Step 2 de novo: PASS.

- [ ] **Step 4: Commit**

```bash
git add tests/IdentityGateway.Infrastructure.IntegrationTests
git commit -m "test: resiliencia do cliente da Admin API contada por requisicao, contra o Keycloak real" -m "Mutacoes: sem DisableForUnsafeHttpMethods pega; ordem dos handlers e JsonContent: <resultado observado>."
```

---

### Task 12: Docker Compose

**Files:**
- Modify: `docker-compose.yml`

**Interfaces:**
- Consumes: `keycloak/bootstrap/realm-identity-gateway.json` (Task 8); configuração `Keycloak:Admin:*` (Task 6).

- [ ] **Step 1: Fixar Seq e Jaeger**

Descobrir as versões hoje servidas como `latest`:

```bash
docker pull datalust/seq:latest
docker image inspect datalust/seq:latest --format '{{ index .Config.Labels "org.opencontainers.image.version" }}'
docker pull jaegertracing/all-in-one:latest
docker image inspect jaegertracing/all-in-one:latest --format '{{ index .Config.Labels "org.opencontainers.image.version" }}'
```

Trocar `datalust/seq:latest` e `jaegertracing/all-in-one:latest` pelas versões impressas. Se o label vier vazio, usar `docker image inspect <imagem> --format '{{ .RepoDigests }}'` e fixar por digest (`imagem@sha256:...`). No serviço `seq`, acrescentar `SEQ_FIRSTRUN_NOAUTHENTICATION: "true"` ao `environment` (versões recentes do Seq exigem senha de admin ou esta opção para subir; ambiente local, sem autenticação).

- [ ] **Step 2: Cabeçalho e serviços novos**

No cabeçalho de `docker-compose.yml`, depois do parágrafo "NÃO É PRODUÇÃO...", acrescentar:

```yaml
#
# O Keycloak também não: `start-dev`, http e chave gerada em volume são de desenvolvimento. Em produção, o BaseUrl da
# Gateway precisa ser igual ao KC_HOSTNAME (o issuer é calculado da URL), a chave vem do cofre como arquivo montado, e
# a Api recusa BaseUrl sem https (AllowInsecureHttp só existe no appsettings.Development.json).
#
# Chave e realm andam juntos: o realm é importado só na primeira subida. Apagar SÓ o volume gateway-keys gera uma
# chave que o realm não conhece, e o /health/ready da Api acusa invalid_client. Para recomeçar: docker compose down -v.
```

Acrescentar os serviços (antes de `postgres:`):

```yaml
  # One-shot: gera, só na primeira subida, o par de chaves da Gateway e a senha do admin master do Keycloak.
  #
  # Script INLINE, e não um .sh montado: o .editorconfig deste repositório define CRLF, e um script montado por bind
  # mount chegaria ao contêiner com \r. `$$` é o `$` literal para o compose.
  #
  # Geração atômica: tudo nasce em .tmp e é movido no fim; o marcador .complete é gravado por último. Sem ele, a
  # geração recomeça do zero — nunca fica meio par.
  gateway-keys:
    image: alpine:3.22
    volumes:
      - gateway-keys:/keys
    entrypoint: ["/bin/sh", "-euc"]
    command:
      - |
        if [ -f /keys/.complete ]; then echo "gateway-keys: chaves já existem"; exit 0; fi
        apk add --no-cache openssl > /dev/null
        rm -rf /keys/.tmp /keys/api /keys/keycloak
        mkdir -p /keys/.tmp/api /keys/.tmp/keycloak
        openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out /keys/.tmp/api/private.pem
        openssl req -new -x509 -key /keys/.tmp/api/private.pem -subj "/CN=identity-gateway" -days 3650 -outform DER -out /keys/.tmp/cert.der
        openssl base64 -A -in /keys/.tmp/cert.der > /keys/.tmp/keycloak/cert.b64
        openssl rand -base64 24 | tr -d '\n' > /keys/.tmp/keycloak/admin-password
        # 1654 é o usuário "app" da imagem aspnet (Dockerfile); 1000, o do Keycloak. Cada um lê só a sua pasta.
        chown -R 1654:1654 /keys/.tmp/api && chmod 0500 /keys/.tmp/api && chmod 0400 /keys/.tmp/api/private.pem
        chown -R 1000:1000 /keys/.tmp/keycloak && chmod 0500 /keys/.tmp/keycloak
        chmod 0444 /keys/.tmp/keycloak/cert.b64 && chmod 0400 /keys/.tmp/keycloak/admin-password
        mv /keys/.tmp/api /keys/api && mv /keys/.tmp/keycloak /keys/keycloak && rm -rf /keys/.tmp
        touch /keys/.complete
        echo "gateway-keys: senha do admin master do Keycloak (exibida só nesta subida): $$(cat /keys/keycloak/admin-password)"

  # One-shot: cria o banco do Keycloak se ele não existir. Não é script de initdb porque o initdb só roda com o
  # volume vazio — e quebraria quem já tem o postgres-data.
  keycloak-db:
    image: postgres:17-alpine
    environment:
      PGPASSWORD: postgres
    entrypoint: ["/bin/sh", "-euc"]
    command:
      - |
        if psql -h postgres -U postgres -tAc "SELECT 1 FROM pg_database WHERE datname = 'keycloak'" | grep -q 1; then
          echo "keycloak-db: banco já existe"
        else
          psql -h postgres -U postgres -c "CREATE DATABASE keycloak"
        fi
    depends_on:
      postgres:
        condition: service_healthy

  # Console em http://localhost:8081 (usuário admin; senha no log do gateway-keys). Publicado só em 127.0.0.1: o
  # admin master manda no Keycloak inteiro, e não fica exposto à rede local.
  keycloak:
    image: quay.io/keycloak/keycloak:26.7.4
    # O ENTRYPOINT da imagem é o kc.sh, então a troca vai em `entrypoint`, não em `command`. `test -s` falha cedo se o
    # arquivo não existir: um placeholder sem variável seria gravado como texto literal, sem erro, e só falharia na
    # autenticação. `exec` para o kc.sh receber o SIGTERM.
    entrypoint:
      - /bin/sh
      - -c
      - >-
        test -s /keys/cert.b64 && test -s /keys/admin-password &&
        export GATEWAY_CLIENT_CERT="$$(cat /keys/cert.b64)" KC_BOOTSTRAP_ADMIN_PASSWORD="$$(cat /keys/admin-password)" &&
        exec /opt/keycloak/bin/kc.sh start-dev --import-realm
    environment:
      KC_DB: postgres
      KC_DB_URL_HOST: postgres
      KC_DB_URL_DATABASE: keycloak
      KC_DB_USERNAME: postgres
      KC_DB_PASSWORD: postgres
      KC_BOOTSTRAP_ADMIN_USERNAME: admin
      KC_HEALTH_ENABLED: "true"
    ports:
      - "127.0.0.1:8081:8080"
    volumes:
      - type: volume
        source: gateway-keys
        target: /keys
        read_only: true
        volume:
          subpath: keycloak
      - ./keycloak/bootstrap:/opt/keycloak/data/import:ro
    healthcheck:
      # Sem curl na imagem: o comando é o do guia oficial de health do Keycloak, na porta de gestão 9000. Testa o
      # /health/ready e não só a porta — com health ligado, as portas abrem antes de o servidor estar pronto.
      test: ["CMD", "bash", "-c", "{ printf 'HEAD /health/ready HTTP/1.0\\r\\n\\r\\n' >&0; grep -q 'HTTP/1.0 200'; } 0<>/dev/tcp/127.0.0.1/9000"]
      interval: 10s
      timeout: 5s
      retries: 20
      start_period: 90s
    depends_on:
      gateway-keys:
        condition: service_completed_successfully
      keycloak-db:
        condition: service_completed_successfully
```

- [ ] **Step 3: Ajustar o serviço `api`**

No `environment` do serviço `api`, acrescentar:

```yaml
      # A Api alcança o Keycloak pelo nome do serviço. O issuer que o Keycloak calcula vem desta mesma URL, então o
      # aud do assertion confere sem fixar KC_HOSTNAME.
      Keycloak__Admin__BaseUrl: "http://keycloak:8080"
      Keycloak__Admin__PrivateKeyPath: "/keys/private.pem"
```

Acrescentar ao serviço `api`:

```yaml
    volumes:
      - type: volume
        source: gateway-keys
        target: /keys
        read_only: true
        volume:
          subpath: api
```

No `depends_on` do `api`, acrescentar:

```yaml
      # Conveniência de desenvolvimento, para o primeiro curl funcionar. Não é garantia: a Api responde com o
      # Keycloak parado, e é o provisionamento que espera por ele.
      keycloak:
        condition: service_healthy
      gateway-keys:
        condition: service_completed_successfully
```

Substituir o `healthcheck` do `api` (o `dotnet --info` não verifica nada — era verde vacuoso):

```yaml
    healthcheck:
      # O /health/ready de verdade: Postgres, Redis e o token do service account no Keycloak. A imagem aspnet é
      # Debian e tem bash; sem curl, a requisição sai por /dev/tcp.
      test: ["CMD", "bash", "-c", "{ printf 'GET /health/ready HTTP/1.0\\r\\n\\r\\n' >&0; grep -q ' 200 '; } 0<>/dev/tcp/127.0.0.1/8080"]
      interval: 15s
      timeout: 5s
      retries: 10
      start_period: 30s
```

Em `volumes:` (fim do arquivo), acrescentar:

```yaml
  # Chaves da Gateway e senha do admin do Keycloak. Anda junto com o banco do Keycloak: ver o cabeçalho.
  gateway-keys:
```

- [ ] **Step 4: Validar o arquivo**

Run: `docker compose config --quiet`
Expected: sem saída (arquivo válido).

- [ ] **Step 5: Verificação no ambiente real**

```bash
docker compose down -v
docker compose up -d --build --wait --wait-timeout 300 api
curl -f http://localhost:8080/health/ready
docker compose logs gateway-keys
docker compose down
docker compose up -d --wait --wait-timeout 300 api
curl -f http://localhost:8080/health/ready
```

Expected: os dois `curl` imprimem `Healthy`; o log do `gateway-keys` mostra a senha na primeira subida e "chaves já existem" na segunda.

Conferir que o volume-par falha alto:

```bash
docker compose down
docker volume rm identitygateway_gateway-keys
docker compose up -d --wait --wait-timeout 300 api
```

Expected: o `up --wait` **falha** (api unhealthy) e `docker compose logs api` mostra `invalid_client`. Depois: `docker compose down -v`.

- [ ] **Step 6: Commit**

```bash
git add docker-compose.yml
git commit -m "feat: Keycloak no compose, com chave e senha do admin geradas na primeira subida" -m "One-shots idempotentes gateway-keys e keycloak-db; healthcheck real na api; Seq e Jaeger fixados."
```

---

### Task 13: Job de compose na CI

**Files:**
- Modify: `.github/workflows/ci.yml`

- [ ] **Step 1: Acrescentar o job**

No fim de `.github/workflows/ci.yml`, acrescentar:

```yaml
  # ─────────────────────────── compose ───────────────────────────
  #
  # A promessa do M0 é "git clone + docker compose up funcionam na primeira tentativa". Verificada à mão, ela vale só
  # no dia em que alguém testou; aqui vale a cada PR. E o /health/ready da Api obtém o token do service account: um
  # verde aqui prova a geração das chaves, o import do realm, o banco do Keycloak e o private_key_jwt juntos.
  #
  # `up ... api`, com o nome do serviço: o --wait espera a api ficar saudável e não tropeça nos one-shots, que
  # terminam de propósito. Seq e Jaeger não sobem aqui — a api não depende deles.
  compose:
    name: Compose
    runs-on: ubuntu-latest
    needs: build

    steps:
      - uses: actions/checkout@v4

      - name: Subir até a API ficar pronta
        run: docker compose up -d --build --wait --wait-timeout 300 api

      - name: Conferir o ready
        run: curl --fail --silent --show-error http://localhost:8080/health/ready

      # Derruba SEM apagar volumes e sobe de novo: prova que os one-shots são idempotentes e que o realm já
      # importado continua casando com a chave que ficou no volume.
      - name: Subir de novo sobre os mesmos volumes
        run: |
          docker compose down
          docker compose up -d --wait --wait-timeout 300 api
          curl --fail --silent --show-error http://localhost:8080/health/ready

      - name: Logs em caso de falha
        if: failure()
        run: docker compose logs --no-color

      - name: Limpar
        if: always()
        run: docker compose down -v
```

- [ ] **Step 2: Validar a sintaxe**

Run: `docker run --rm -v "${PWD}:/repo" -w /repo rhysd/actionlint:latest -color .github/workflows/ci.yml`
Expected: sem erros. (Se o Docker não puder montar a pasta, validar o YAML abrindo o workflow no GitHub depois do push.)

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: job que sobe o compose e exige o health ready da API"
```

---

### Task 14: README, comentário corrigido, suíte completa e handoff

**Files:**
- Modify: `README.md`
- Modify: `src/IdentityGateway.Application/Tenants/RegisterTenant/RegisterTenantCommand.cs:15-18`
- Create: `docs/superpowers/specs/<data-de-hoje>-fundacao-keycloak-handoff.md`

- [ ] **Step 1: Corrigir o comentário falso do command**

Em `RegisterTenantCommand.cs`, substituir o segundo `<para>` do `<remarks>`:

```csharp
/// <para>
/// <b>Nesta versão o campo é validado e descartado</b>: o handler não o repassa ao <c>Tenant</c> nem ao evento
/// <c>TenantRegistered</c>, e nada o persiste. Onde ele deve viver entre o <c>POST</c> e o convite é decisão pendente
/// da fatia C (spec v2.4, §9.1): pô-lo no evento o levaria ao Outbox e ao RabbitMQ, contra a regra de dados pessoais
/// só no Keycloak. A versão anterior deste comentário afirmava que o campo era "carregado" — não era.
/// </para>
```

- [ ] **Step 2: README**

Em `README.md`, na seção que descreve o `docker compose up`, acrescentar:

```markdown
### Keycloak

O compose sobe o Keycloak 26.7.4 com o realm `identity-gateway` importado de `keycloak/bootstrap/`.

| O quê | Onde |
|---|---|
| Console | http://localhost:8081 (só no localhost) |
| Usuário | `admin` |
| Senha | gerada na primeira subida: `docker compose logs gateway-keys` |

Nenhuma credencial fica no repositório: a chave da Gateway e a senha do admin são geradas pelo serviço
`gateway-keys` num volume, na primeira subida.

**Chave e realm andam juntos.** O realm é importado só na primeira subida. Se só o volume `gateway-keys` for apagado,
a chave nova não bate com o certificado registrado, e o `/health/ready` da API responde 503 com `invalid_client` no
log. Para recomeçar do zero: `docker compose down -v`.

### Rodar a API pela IDE

A API exige a configuração do Keycloak para subir. Com o compose rodando só as dependências:

```powershell
docker compose up -d postgres redis keycloak
$pem = docker run --rm -v identitygateway_gateway-keys:/k alpine cat /k/api/private.pem | Out-String
dotnet user-secrets set "Keycloak:Admin:PrivateKeyPem" $pem --project src/IdentityGateway.Api
```

O `appsettings.Development.json` já aponta `Keycloak:Admin:BaseUrl` para `http://localhost:8081`. O nome do volume
leva o prefixo do projeto do compose (o nome da pasta); confira com `docker volume ls`.
```

Se `src/IdentityGateway.Api/IdentityGateway.Api.csproj` não tiver `<UserSecretsId>`, rodar `dotnet user-secrets init --project src/IdentityGateway.Api` e incluir o csproj no commit.

- [ ] **Step 3: Suíte completa**

Run: `dotnet build`
Expected: 0 avisos, 0 erros.

Run: `dotnet test`
Expected: PASS em todos os projetos, **0 skips**. Anotar o total por projeto (vai no handoff e no PR).

- [ ] **Step 4: Handoff**

Create `docs/superpowers/specs/<data-de-hoje>-fundacao-keycloak-handoff.md` com: estado da branch (HEAD, commits sobre `main`), o que a fatia entregou (tabela por camada), totais de teste por projeto, a lista de mutações feitas e o que cada uma pegou (inclusive as da Task 11 com o resultado observado), o que ficou fora (Seção 8 da spec), e a **errata** do handoff de 2026-09-22:

```markdown
## Errata do handoff de 2026-09-22

O handoff da vertical afirmava que o `initialAdminEmail` era "validado e carregado até o evento". Não era: o handler
chamava `Tenant.Register(name, slug, plan)` sem ele, e `TenantRegistered` só tem `TenantId` e `Slug`. O campo é
validado e descartado. O comentário de `RegisterTenantCommand.cs` que repetia a afirmação foi corrigido nesta fatia;
onde o e-mail deve viver é decisão pendente da fatia C (spec v2.4, §9.1).
```

Próximo passo registrado: **fatia B — consumidor do provisionamento**, começando por brainstorming (transporte).

- [ ] **Step 5: Commit**

```bash
git add README.md src/IdentityGateway.Application docs/superpowers/specs src/IdentityGateway.Api/IdentityGateway.Api.csproj
git commit -m "docs: README do Keycloak, comentario do command corrigido e handoff da fundacao"
```

- [ ] **Step 6: Push e PR (só com autorização do usuário)**

Perguntar ao usuário antes. Com a autorização: `git push -u origin feat/fundacao-keycloak` e abrir o PR para `main` com o resumo do handoff — **sem** a linha "Generated with Claude Code" nem qualquer referência a IA.
