# Fatia D — Tokens do Keycloak: plano de implementação

> **Para agentes:** SUB-SKILL OBRIGATÓRIA: use superpowers:subagent-driven-development (recomendado) ou superpowers:executing-plans para executar este plano tarefa por tarefa. Os passos usam checkbox (`- [ ]`).

**Objetivo:** A API passa a aceitar só access tokens do Keycloak — RS256, validados por metadados, com emissor, audiência, client de origem e forma conferidos — e o JWT simétrico do template deixa de existir. O realm emite o token da §10.1 (`roles` plano só com o catálogo, `tenant_id` plano, audiência `identity-gateway-api` num scope próprio), o primeiro platform-admin nasce sem senha e é convidado por e-mail uma única vez, e entra a primeira rota de tenant, `GET /api/v1/tenants/{tenantId}`, protegida em quatro camadas.

**Arquitetura:** Dois PRs. A **D1** (Tarefas 1–12) leva o Keycloak de ponta a ponta até o `POST /tenants`: um projeto de suporte de testes (`IdentityGateway.Testing.Keycloak`, biblioteca) com o fixture, o cliente do mailpit e um harness de login por Device Authorization Grant; o realm final; a `AccessTokenValidationOptions` neutra, preenchida pelo adaptador do Keycloak; o JwtBearer por metadados internos com `IssuerValidator` estrito e checagens de `azp`, `typ` e `sub`; um one-shot `platform-admin-invite` no compose; e um app de arquivo único (`tools/jornada-compose.cs`) que roda a jornada na CI. A **D2** (Tarefas 13–17) leva a rota e a policy `TenantAdmin` (papel ∧ ¬platform-admin ∧ mesmo tenant ∧ `Member` no banco), sem mexer no realm, no compose nem no bootstrap.

**Stack:** .NET 10 (SDK 10.0.401, C# 14), ASP.NET Core JwtBearer 10.0.12, Microsoft.IdentityModel 8.19.2, Carter 10, Mediator 3.0.2, EF Core 10 + Npgsql, Serilog, OpenTelemetry 1.18, xUnit v3 4.0.1 + AwesomeAssertions + NSubstitute, NetArchTest 1.3.2, Testcontainers 4.15 (PostgreSQL 17, Redis 7, Keycloak 26.7.4, mailpit v1.31.3), Docker Compose, GitHub Actions.

**Spec:** docs/superpowers/specs/2026-09-30-tokens-keycloak-design.md (leia a seção citada em cada tarefa antes de começar; este plano argumenta a partir dela)

## Restrições globais

- **Validação do token (§4.2, DT2, DT5):** só `RS256`; `ClockSkew` de 30 s; `BackchannelTimeout` de 5 s; `RefreshInterval` de 30 s; `RequireHttpsMetadata = !AllowInsecureHttp`; `MapInboundClaims = false`; `NameClaimType = "sub"`; `RoleClaimType = "roles"`; `IncludeErrorDetails = false` em todo ambiente. Emissor aceito: **só** `{PublicBaseUrl ?? BaseUrl}/realms/{Realm}`, por igualdade ordinal, num `IssuerValidator` próprio (o `ValidIssuer` não restringe). Metadados: `{BaseUrl}/realms/{Realm}/.well-known/openid-configuration`, sem `Authority`.
- **`Keycloak:Auth` (DT1, DT4):** `Audience` com padrão `identity-gateway-api`; `AllowedClients` (a lista de `azp`) **fora** do `appsettings.json` base, com `identity-gateway-demo` só no `appsettings.Development.json`. Fora de Development, a subida recusa o `identity-gateway-demo` na lista. Lista vazia sobe, recusa todo token e registra um aviso.
- **Checagens além da biblioteca (DT3):** em `OnTokenValidated`, `401` se o `azp` não for texto presente na lista (igualdade ordinal), se o claim `typ` não for o texto `Bearer`, ou se o `sub` não for um GUID no formato `D`.
- **Keycloak fora com metadados frios = `401` com log (DT6).** Nunca `500`, nunca `503` nesta fatia.
- **Realm (§4.4):** `accessTokenLifespan` 300; `registrationAllowed` falso; `bruteForceProtected` verdadeiro; `revokeRefreshToken` verdadeiro e `refreshTokenMaxReuse` 0; atributo `CreateDefaultClientScopes` igual a `"true"`; catálogo `platform-admin`, `tenant-admin`, `financial-manager`, `reader`, nunca compostos, mais `offline_access` e `uma_authorization` declarados só para sair do papel padrão; scopes `gateway-roles`, `gateway-tenant` e `gateway-api`, nenhum default do realm; client `identity-gateway` com `basic` e `roles` e `fullScopeAllowed: true`; client `identity-gateway-demo` público, só device flow, com `basic`, `acr`, `gateway-roles`, `gateway-tenant` e `gateway-api`, `fullScopeAllowed: false` e `oauth2.device.code.lifespan` 300; nenhum grupo; descrições em texto puro (sem `${...}` de i18n).
- **Bootstrap do platform-admin (§4.5):** `PLATFORM_ADMIN_EMAIL` com padrão `platform-admin@identity-gateway.local`, sempre em minúsculas; marcador `platformAdminInviteSentAt` como atributo do realm, gravado **antes** do envio; link de 4 h (`lifespan=14400`); o one-shot **nunca** atribui papel; saída `0` ou `1`; reenvio só por `docker compose run --rm -e REENVIAR=1 platform-admin-invite`. Toda leitura de `attributes` pelo `kcadm` é sem `--fields` (com ele, o objeto vem vazio).
- **Rotação do refresh token (D-n):** quem renova guarda o refresh token novo antes de qualquer outro passo e **nunca repete uma renovação** (sem retry): reusar um refresh token derruba a sessão inteira do client. Se a renovação falhar, o caminho é um device flow novo.
- **Segredos fora de log, exceção e saída:** access token, refresh token, `device_code`, `user_code`, `verification_uri_complete`, link de ação, senha e HTML nunca são impressos, registrados nem postos em mensagem de exceção. O log de `gateway-keys` (imprime a senha do master) nunca entra no log da CI.
- **Compose:** a `api` publica em `127.0.0.1:8080:8080`, e o Jaeger em `127.0.0.1:16686:16686` e `127.0.0.1:4317:4317`; sai `Jwt__SigningKey`. Na CI, sempre `127.0.0.1` nos endereços discados (`localhost` pode tentar `::1`), e `localhost:8081` só como endereço **público** do Keycloak.
- **Rota de tenant (D2, §4.3):** `GET /api/v1/tenants/{tenantId:guid}` responde `200` com **exatamente** as chaves `tenantId`, `name`, `slug`, `status`, `plan` (`tier`, `maxUsers`, `maxClients`), `occupiedSeats` e `registeredAt`; nunca o e-mail; **nunca `404`** — tenant inexistente é `403` (D-k); todo `403` é o mesmo Problem Details. `MemberRequirement` aceita só `Invited` e `Active` (D-j). A D2 não toca `keycloak/`, `docker-compose.yml` nem o one-shot.
- **EventIds novos:** `2100`–`2103` (`AutenticacaoLogs`, na Api).
- **Convenções do repo:** identificadores públicos em inglês; variáveis, membros privados, tipos de teste e de infraestrutura de teste em português; testes `Metodo_Cenario_Resultado`; `sealed`; `partial` para `LoggerMessage` e `GeneratedRegex`; comentários XML em português que explicam o porquê; `TestContext.Current.CancellationToken` nos testes. `TreatWarningsAsErrors` está ligado, e as regras que mais pegam: `var` **obrigatório** quando o tipo está aparente à direita (`new`, cast, fábrica estática do próprio tipo como `JsonDocument.Parse`, `Guid.NewGuid`, `RSA.Create`) e **proibido** no resto (IDE0007/IDE0008); array constante como argumento vira campo `static readonly` (CA1861); `StringComparison` e `CultureInfo` explícitos (CA1305, CA1307, CA1310); membro que não usa a instância vira `static` (CA1822).
- **Arquivos em LF** (o `.gitattributes` força `eol=lf`, menos `.slnx`).
- **A sequência de escape do "e comercial" em JSON** (barra invertida, `u`, `0026`) nunca é escrita pelas ferramentas de edição de arquivo dos agentes, que a decodificam em silêncio. Onde ela precisa existir (errata E1 da v2.7, Tarefa 12), o texto é gravado por shell e conferido com `grep -c 'u0026'`.
- **Commits** em Conventional Commits, em português sem acentos, como o histórico. **Nenhum trailer de coautoria nem linha de atribuição de ferramenta** (sem `Co-Authored-By`, sem "Generated with"). Push e PR só com autorização do autor.
- **Docker Desktop ligado** nas Tarefas 1 a 5 e 9 a 11 (Testcontainers e compose) e na suíte completa. Sem ele, `DockerUnavailableException` é ambiente, não regressão.
- **Verificação local do compose sempre num projeto isolado** (`docker compose -p igverif …`), derrubado com `down -v` no fim. Os volumes `identitygateway_*` do autor nunca são tocados.
- 🧪 = passo "Prova por mutação" obrigatório: aplicar a mutação, rodar o teste indicado, ver vermelho por asserção (erro de compilação não conta), **reverter** (conferir com `git diff --stat` que o arquivo voltou), ver verde. Registrar a mutação na mensagem de commit e, depois, na tabela do handoff.

## Verificado ao vivo ao escrever este plano (2026-10-01)

O material da sessão de design ficou num scratchpad efêmero. Antes de escrever as tarefas, três peças foram refeitas e executadas contra um Keycloak 26.7.4 com mailpit v1.31.3, num ambiente descartável:

- **O realm da Tarefa 2 importa** (`Realm 'identity-gateway' imported`), e a leitura pelo master confere: embutidos presentes, `gateway-*` fora dos defaults do realm, papel padrão `[manage-account, view-profile]`, demo com os cinco scopes, service account com `basic` e `roles`.
- **O script do one-shot da Tarefa 10 roda como está:** primeira execução envia (1 e-mail, link com `exp − iat = 14400`), a segunda sai `0` com "convite já enviado em …" (continua 1 e-mail), `REENVIAR=1` envia outro, e o e-mail configurado em maiúsculas é comparado em minúsculas.
- **O `HarnessDeLogin` da Tarefa 4 roda como está:** link de ações em 4 páginas (informação → senha → perfil → informação); device flow em 5,2 s; platform-admin sem Organization no realm entra em **1** passo de login, e um usuário com Organization em **2** (identity-first); segundo login na mesma instância, **0** passos (cookie de SSO); renovação devolve refresh token novo; o refresh reusado leva `invalid_grant` e, depois dele, **o novo também**. Claims do demo iguais aos da §3.1 da spec. O client de device flow do fixture, criado sem scopes declarados (herda os defaults do realm), emite token com `aud: "account"`, sem `identity-gateway-api`, aceito pela Account API (`204` na alteração permitida, `400` no `tenant_id`). O `admin-cli` do realm por ROPC emite token leve, sem `aud` e sem `sub`.

Dois achados dessa execução entraram no plano: **senha errada fazia o harness reenviar o login doze vezes** (o formulário volta igual), o que com `bruteForceProtected` bloquearia a conta — o harness agora falha na primeira volta do formulário, com teste; e **um link de ações já concluído responde `400` com a página de erro**, que o harness reporta na hora. Os doze testes unitários do harness (Tarefa 4) também foram compilados com os analisadores do repositório e executados.

O que **não** foi executado e fica por conta dos testes de cada tarefa: tudo o que é ASP.NET Core (OIDC falso, opções do JwtBearer, host em `Production`), o app de arquivo único e o job da CI.

## Foco de revisão

Cinco condições que a spec implica mas não lista como caso; cada uma tem teste na tarefa dona:

1. **A pessoa clica duas vezes no link do e-mail** (ou abre um link já concluído): o harness e o app falham na hora, com a etapa e "página de erro", sem laço e sem esperar prazo. Tarefa 5 (`LinkJaConcluido_FalhaNaHoraComAPaginaDeErro`).
2. **A pessoa nega o consentimento, ou deixa o código do dispositivo expirar:** falha imediata com o nome do erro (`access_denied`, `expired_token`), nunca espera pelo prazo inteiro nem repete. Tarefa 4 (`ConsentimentoNegado_FalhaNaHora`, `DeviceCodeExpirado_FalhaNaHora`).
3. **`PLATFORM_ADMIN_EMAIL` vazio ou com maiúsculas** no ambiente de quem sobe o compose: o Keycloak não sobe, em vez de importar um placeholder literal ou um e-mail que o one-shot não acha. Tarefa 10 (`EntrypointDoKeycloakRecusaEmailVazioOuComMaiusculas` e o passo ao vivo).
4. **Token forjado com caractere de controle no `iss`:** `401`, nunca `500` (com `IncludeErrorDetails` ligado, o Kestrel recusaria o cabeçalho e o `401` viraria `500`). Tarefa 8 (caso `iss com caractere de controle` da suíte negativa).
5. **Segunda subida depois de perder o e-mail** (o mailpit não tem volume): o one-shot diz "já enviado" e não reenvia; quem ainda não concluiu só recupera o convite pelo comando de reenvio — que precisa funcionar nesse estado. Tarefa 10 (passo ao vivo "reenvio depois do `down`/`up`").

Nota para o revisor: **a D1 troca o mecanismo de autenticação inteiro**, e os 16 testes funcionais que usam `CreateClientAutenticado` continuam verdes sem mudar uma linha. Isso é o desenho (o emissor de teste imita o token real), mas também é o risco: um verde ali não prova nada sobre o Keycloak. Quem prova são a coleção com Keycloak real (Tarefa 9), a ponte de contrato entre o token real e o do emissor de teste, e o job `Compose`.

---

## Desvios deliberados da spec e da decomposição

- **`SlugUnico` e `CriarProvider` viram membros de extensão do C# 14** (`extension(KeycloakFixture)`), no projeto de integração. As chamadas `KeycloakFixture.SlugUnico()` e `keycloak.CriarProvider(...)` não mudam em nenhum dos arquivos que as usam. Compilação da sintaxe conferida no SDK 10.0.401 com os analisadores do repositório.
- **`FalhaDoHarnessException` e `FamiliaDeFalha` nascem na Tarefa 1**, não na 4: o `LinkDoConviteAsync` do fixture usava AwesomeAssertions, que a biblioteca não tem, e precisa de uma exceção própria já na mudança de projeto.
- **`PLATFORM_ADMIN_EMAIL` entra no `KeycloakFixture` na Tarefa 2**, não na 4: o realm passa a ter o usuário com o placeholder, e o import precisa do valor para o fixture continuar subindo.
- **O client `identity-gateway-demo` declara `enabled` e `protocol`** além das chaves da §4.4 da spec. É a forma que foi importada e exercitada ao escrever o plano.
- **A configuração do JwtBearer fica em `Api/Authentication/`** (`ValidacaoDoAccessToken`, `FormaDoAccessToken`, `AutenticacaoLogs`, `AvisoDeClientsPermitidos`), e não dentro do `DependencyInjection.cs`, que só a chama. O arquivo já tem 333 linhas, e as funções precisam ser `internal static` para ter teste próprio. A v2.7 lista as duas pastas (Tarefa 12).
- **`Api/Authorization/Policies.cs` nasce na Tarefa 7 (D1)** só com `PlatformAdmin`, junto com o result handler do Problem Details, que mora na mesma pasta. A D2 acrescenta `TenantAdmin` e move o `AddAuthorization` para o `AddAutorizacaoDaGateway`.
- **`ApiEmProducaoFactory` deriva de `WebApplicationFactory<Program>`, não da `IdentityGatewayApiFactory`:** os testes em `Production` terminam na autenticação e não precisam de PostgreSQL nem de Redis. Sem containers, as classes rodam em segundos e sem fixture.
- **As checagens de `azp`, `typ` e `sub` leem o JSON do payload**, e não os claims: o `ClaimsPrincipal` achata um array de um elemento num claim só, e `"typ": ["Bearer"]` passaria. Visto no protótipo: para `azp` e `sub` que não sejam texto (array, número), a própria biblioteca já recusa o token ao lê-lo, antes das nossas checagens — por isso a mutação "ler o `azp` com `FindFirst`" da §5.3 da spec é equivalente, e a testemunha da leitura pelo JSON é o caso "`typ` em array".
- **O `sub` é conferido pelo tamanho antes do parse:** `Guid.TryParseExact(…, "D")` aceita espaço nas pontas, e o formato `D` tem exatamente 36 caracteres.
- **O `Program.cs` passa a usar `UseSerilog(..., preserveStaticLogger: true)`** (Tarefa 7). Sem isso, cada host registra pelo `Log.Logger` estático do processo — o do último host construído —, e o sink em memória que a §5.5 da spec pede receberia os logs dos outros hosts de teste (visto no protótipo). Em produção há um host só, e nada muda.
- **A fase `com-keycloak-parado` do app confere que o Keycloak não responde, e não o `/health/ready`** (como a §4.6 da spec descreve): a api guarda o token do service account em memória por até ~4,5 min (`ServiceAccountTokenCache`), e o ready continua `200` logo depois do `stop`.
- **O aviso da lista de `azp` vazia é um `IHostedService`** (`AvisoDeClientsPermitidos`): é o que roda na subida do host real e do `WebApplicationFactory`, e por isso tem teste.
- **O teste de vazamento do e-mail no token usa a factory do OIDC falso** (Tarefa 9), com um sink do Serilog e um exportador OpenTelemetry em memória, as duas exceções declaradas à regra "só configuração" (§5.5 da spec).

## Mapa de arquivos

**Projeto de suporte (novo) — `tests/IdentityGateway.Testing.Keycloak/`**
- Create `IdentityGateway.Testing.Keycloak.csproj` — biblioteca (`IsTestProject=false`, `OutputType=Library`), sem referência a `src/`.
- Create `KeycloakFixture.cs` (movido do projeto de integração), `ChavesDeTeste.cs` (movido, público), `RaizDoRepositorio.cs` (movido, público).
- Create `FamiliaDeFalha.cs`, `FalhaDoHarnessException.cs` (Tarefa 1); `PayloadDoJwt.cs` (Tarefa 3); `ClienteDoMailpit.cs`, `HarnessDeLogin.cs`, `TokensDeUsuario.cs`, `UsuarioDeTeste.cs`, `SenhasDeTeste.cs` (Tarefa 4).

**Infrastructure**
- Create `src/IdentityGateway.Infrastructure/Configuration/AccessTokenValidationOptions.cs`.
- Modify `Identity/Keycloak/KeycloakAdminOptions.cs` (`Issuer`, `MetadataAddress`), `Identity/Keycloak/KeycloakServiceCollectionExtensions.cs` (registro e validação da option).
- Delete `Configuration/JwtOptions.cs`; modify `DependencyInjection.cs` (sai o registro).
- D2: create `Persistence/Queries/MemberQueries.cs`; modify `Persistence/Queries/TenantQueries.cs`, `DependencyInjection.cs`.

**Application (D2)**
- Create `Common/Abstractions/IMemberQueries.cs`, `Tenants/GetTenant/GetTenantQuery.cs`, `GetTenantHandler.cs`, `TenantDetailsResponse.cs`, `TenantDetailsView.cs`; modify `Common/Abstractions/ITenantQueries.cs`.

**Api**
- Create `Authentication/ValidacaoDoAccessToken.cs`, `Authentication/AutenticacaoLogs.cs` (Tarefa 7); `Authentication/FormaDoAccessToken.cs`, `Authentication/AvisoDeClientsPermitidos.cs` (Tarefa 8).
- Create `Authorization/Policies.cs`, `Authorization/RespostasDeAutorizacao.cs`, `Authorization/ProblemDetailsDeAutorizacao.cs` (Tarefa 7); D2: `Authorization/RoleRequirement.cs`, `NotPlatformAdminRequirement.cs`, `SameTenantRequirement.cs`, `MemberRequirement.cs`, `MemberRequirementHandler.cs`, `AutorizacaoDaGateway.cs`.
- Modify `DependencyInjection.cs`, `Program.cs`, `Services/HttpCurrentUser.cs`, `Modules/TenantsModule.cs`, `appsettings.json`, `appsettings.Development.json`, `IdentityGateway.Api.csproj`.
- Delete `Security/JwtTokenService.cs`.

**Realm, compose, CI e ferramentas**
- Modify `keycloak/bootstrap/realm-identity-gateway.json`, `docker-compose.yml`, `.github/workflows/ci.yml`.
- Create `tools/jornada-compose.cs`.
- Modify `Directory.Packages.props` (`xunit.v3.extensibility.core`), `IdentityGateway.slnx`.

**Testes**
- Architecture: `RegrasDoRealmTests.cs`, `RegrasDoAmbienteLocalTests.cs`, `RegrasDaApiTests.cs`; create `RegrasDeFerramentasTests.cs`; D2: `RegrasDeDominioTests.cs`.
- Integration: create `Identity/Keycloak/KeycloakFixtureExtensions.cs`, `RealmVivoTests.cs`, `HarnessDeLoginTests.cs`, `HarnessContraKeycloakTests.cs`, `FormaDoTokenContraKeycloakTests.cs`, `AccessTokenValidationOptionsTests.cs`; modify `GlobalUsings.cs`, `KeycloakRealTests.cs`, `KeycloakHealthCheckTests.cs`, `DependencyInjectionTests.cs`, `Provisioning/ComposicaoDoProvisionamento.cs`, o `.csproj`; delete `Identity/Keycloak/KeycloakFixture.cs`, `ChavesDeTeste.cs`, `RaizDoRepositorio.cs`. D2: create `Persistence/MemberQueriesTests.cs`, `Persistence/TenantDetailsTests.cs`.
- Functional: create `Oidc/OidcFalso.cs`, `Oidc/EmissorDeTeste.cs`, `Logs/ColetorDeLogsDaApi.cs`, `Logs/CapturaDeSpans.cs`, `ApiEmProducaoFactory.cs`, `EmissorEstritoTests.cs`, `OpcoesDoJwtBearerTests.cs`, `LogsPorHostTests.cs`, `AutenticacaoNegativaTests.cs`, `FormaDoAccessTokenTests.cs`, `HostEmProducaoTests.cs`, `EndpointsDeclaramAutorizacaoTests.cs`, `ApiComKeycloakFactory.cs`, `ColecaoComKeycloak.cs`, `TokensDoKeycloakNaApiTests.cs`, `VazamentoDoEmailNoTokenTests.cs`; rewrite `IdentityGatewayApiFactory.cs`; modify `SegurancaTests.cs`, o `.csproj`. D2: create `Autorizacao/TenantAdminPolicyTests.cs`, `Autorizacao/OrdemDosHandlersTests.cs`, `LeituraDeTenantTests.cs`, `PoliciesDeTenantExigemTenantIdTests.cs`, `LeituraDeTenantComKeycloakTests.cs`.

**Documentos**
- Create `docs/especificacao-arquitetural-v2.7.md`; modify `docs/documentacao-negocio.md`, `README.md`, `CONTRIBUTING.md`; create os handoffs da D1 e da D2 em `docs/superpowers/specs/`.

---

# PARTE D1 — o Keycloak de ponta a ponta (um PR)

A branch é `feat/tokens-keycloak`. A CI só roda no PR: a branch pode passar por um trecho em que o job `Compose` ficaria vermelho (entre a Tarefa 2 e a 11), desde que o HEAD do PR esteja verde. `dotnet build` e `dotnet test` ficam verdes ao fim de **cada** tarefa.

---

### Tarefa 1: Projeto de suporte `IdentityGateway.Testing.Keycloak`

Spec: §4.6 (primeiro bloco), §5.4 ("Mudam com o fixture"), DT7.

Move o `KeycloakFixture` para uma **biblioteca** que o projeto de integração, o funcional (Tarefa 9) e o app de CI (Tarefa 11) vão usar. **Nenhuma mudança de comportamento:** os 189 testes de integração continuam os mesmos e verdes.

**Arquivos:**
- Create: `tests/IdentityGateway.Testing.Keycloak/IdentityGateway.Testing.Keycloak.csproj`
- Move: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakFixture.cs` → `tests/IdentityGateway.Testing.Keycloak/KeycloakFixture.cs`
- Move: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/ChavesDeTeste.cs` → `tests/IdentityGateway.Testing.Keycloak/ChavesDeTeste.cs`
- Move: `tests/IdentityGateway.Infrastructure.IntegrationTests/RaizDoRepositorio.cs` → `tests/IdentityGateway.Testing.Keycloak/RaizDoRepositorio.cs`
- Create: `tests/IdentityGateway.Testing.Keycloak/FamiliaDeFalha.cs`, `tests/IdentityGateway.Testing.Keycloak/FalhaDoHarnessException.cs`
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakFixtureExtensions.cs`
- Modify: `tests/IdentityGateway.Infrastructure.IntegrationTests/IdentityGateway.Infrastructure.IntegrationTests.csproj`, `GlobalUsings.cs`, `DependencyInjectionTests.cs:27`
- Modify: `Directory.Packages.props` (grupo "Testes"), `IdentityGateway.slnx`
- Test: `tests/IdentityGateway.ArchitectureTests/RegrasDoAmbienteLocalTests.cs`

**Interfaces:**
- Consome: nada.
- Produz (namespace `IdentityGateway.Testing.Keycloak`, tudo `public`):
  - `KeycloakFixture` — o mesmo de hoje, **menos** `SlugUnico()` e `CriarProvider(...)`, **mais** `public const string ImagemDoKeycloak = "quay.io/keycloak/keycloak:26.7.4"` e com `public ParDeChaves Chaves { get; }`. Continuam: `Realm`, `HostnamePublico`, `ImagemDoMailpit`, `BaseUrl`, `MailpitUrl`, `EmailUnico()`, `EmailUnicoDe254Caracteres()`, `CriarClienteMasterAsync`, `CriarOrganizacaoComoMasterAsync`, `LerOrganizacaoCruaAsync`, `ContarPorAliasAsync`, `LerUsuarioCruAsync`, `UsuariosPorEmailAsync`, `CriarUsuarioComoMasterAsync`, `DesabilitarUsuarioComoMasterAsync`, `MembrosDaOrganizacaoAsync`, `PapeisDeRealmDoUsuarioAsync`, `TokenDeUsuarioComumAsync` (sai na Tarefa 4), `MensagensParaAsync`, `EsperarMensagensAsync`, `TextoDaMensagemAsync`, `LinkDoConviteAsync`.
  - `static class ChavesDeTeste { public static ParDeChaves Gerar(); }` e `sealed record ParDeChaves(RSA Rsa, string PemPrivado, string CertificadoBase64)`.
  - `static class RaizDoRepositorio { public static string Caminho(params string[] partes); }`.
  - `enum FamiliaDeFalha { Mailpit = 10, Formulario = 20, DeviceFlow = 30, Api = 40, Prazo = 50 }`.
  - `sealed class FalhaDoHarnessException : Exception` com `FalhaDoHarnessException(FamiliaDeFalha familia, string etapa, string message)`, a mesma com `Exception innerException` no fim, `FamiliaDeFalha Familia { get; }` e `string Etapa { get; }`.
- Produz (projeto de integração, namespace `IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak`, `internal`): membros de extensão `KeycloakFixture.SlugUnico()` (estático, devolve `TenantSlug`) e `keycloak.CriarProvider(Action<IServiceCollection>? ajustar = null, string? pem = null)` (devolve `ServiceProvider`) — as mesmas assinaturas de hoje.

- [ ] **Passo 1: Apontar as regras de arquitetura para o lugar novo do fixture**

Em `tests/IdentityGateway.ArchitectureTests/RegrasDoAmbienteLocalTests.cs`, substituir o teste `ComposeEFixtureUsamAMesmaTagDoMailpit` inteiro por estes dois, e acrescentar o helper `Fixture()` e a regex `ImagensDoKeycloak()` junto dos demais membros privados do fim da classe:

```csharp
    [Fact]
    public void ComposeEFixtureUsamAMesmaTagDoMailpit()
    {
        Match imagem = ImagemDoMailpit().Match(Compose());

        imagem.Success.Should().BeTrue("o compose precisa do serviço mailpit com a versão fixada");
        Fixture().Should().Contain($"ImagemDoMailpit = \"{imagem.Groups["imagem"].Value}\"",
            "o teste de integração precisa provar o mesmo mailpit que o compose sobe");
    }

    [Fact]
    public void ComposeEFixtureUsamAMesmaTagDoKeycloak()
    {
        // Os fatos do Keycloak que o projeto usa foram verificados numa tag: testar contra uma e subir outra deixaria
        // o compose sem prova. Todos os serviços do compose que usam a imagem precisam da mesma tag que o fixture.
        string[] imagens =
        [
            .. ImagensDoKeycloak().Matches(Compose()).Select(achado => achado.Groups["imagem"].Value).Distinct(),
        ];

        imagens.Should().ContainSingle("o compose usa uma tag só do Keycloak");
        Fixture().Should().Contain($"ImagemDoKeycloak = \"{imagens[0]}\"",
            "o teste de integração precisa provar o mesmo Keycloak que o compose sobe");
    }
```

```csharp
    private static string Fixture() => File.ReadAllText(
        RaizDoRepositorio.Caminho("tests", "IdentityGateway.Testing.Keycloak", "KeycloakFixture.cs"));

    [GeneratedRegex(@"^\s*image:\s*(?<imagem>quay\.io/keycloak/keycloak:\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex ImagensDoKeycloak();
```

- [ ] **Passo 2: Rodar e ver falhar**

Run: `dotnet test tests/IdentityGateway.ArchitectureTests --filter-class "*RegrasDoAmbienteLocalTests"`
Expected: FAIL nos dois testes, com `DirectoryNotFoundException` (o caminho `tests/IdentityGateway.Testing.Keycloak/KeycloakFixture.cs` ainda não existe). Os outros três continuam verdes.

- [ ] **Passo 3: O pacote e o projeto**

Em `Directory.Packages.props`, no `ItemGroup Label="Testes"`, logo depois da linha `<PackageVersion Include="xunit.v3" Version="4.0.1" />`:

```xml
    <!--
      Só o núcleo do xUnit v3 (IAsyncLifetime e os atributos), sem o executável de teste: é o que uma biblioteca de
      suporte de testes referencia. Mesma versão do xunit.v3 — com o pinning transitivo, versões diferentes brigariam.
    -->
    <PackageVersion Include="xunit.v3.extensibility.core" Version="4.0.1" />
```

Criar `tests/IdentityGateway.Testing.Keycloak/IdentityGateway.Testing.Keycloak.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    Biblioteca de suporte de testes, e NÃO projeto de teste: o fixture do Keycloak, o cliente do mailpit e o harness
    de login, usados pelos projetos de integração e funcional e pelo app de CI (tools/jornada-compose.cs).

    O tests/Directory.Build.props faz de todo projeto desta pasta um executável do xUnit v3. Aqui isso é desfeito, uma
    propriedade e um pacote de cada vez: um executável de teste referenciado por um app de arquivo único dá dois Main
    (CS7022), e `dotnet test` tentaria rodar um projeto sem teste nenhum.
  -->
  <PropertyGroup>
    <IsTestProject>false</IsTestProject>
    <OutputType>Library</OutputType>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Remove="xunit.v3" />
    <PackageReference Remove="Microsoft.Testing.Extensions.CodeCoverage" />
    <PackageReference Remove="AwesomeAssertions" />
  </ItemGroup>

  <!--
    Sem referência a src/: quem usa esta biblioteca não deve arrastar a Gateway junto (o app de CI fala com a API por
    HTTP). O que depende de tipos da Gateway — o slug de teste, a composição da Infrastructure — fica como extensão
    em cada projeto de teste.
  -->
  <ItemGroup>
    <PackageReference Include="xunit.v3.extensibility.core" />
    <PackageReference Include="Testcontainers.Keycloak" />
  </ItemGroup>

</Project>
```

Em `IdentityGateway.slnx`, dentro de `<Folder Name="/tests/">`, depois da linha do `IdentityGateway.Infrastructure.IntegrationTests` (o arquivo é CRLF; mantenha):

```xml
    <Project Path="tests/IdentityGateway.Testing.Keycloak/IdentityGateway.Testing.Keycloak.csproj" />
```

- [ ] **Passo 4: Mover os três arquivos**

```bash
mkdir -p tests/IdentityGateway.Testing.Keycloak
git mv tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakFixture.cs tests/IdentityGateway.Testing.Keycloak/KeycloakFixture.cs
git mv tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/ChavesDeTeste.cs tests/IdentityGateway.Testing.Keycloak/ChavesDeTeste.cs
git mv tests/IdentityGateway.Infrastructure.IntegrationTests/RaizDoRepositorio.cs tests/IdentityGateway.Testing.Keycloak/RaizDoRepositorio.cs
```

Em `tests/IdentityGateway.Testing.Keycloak/ChavesDeTeste.cs`: o namespace vira `IdentityGateway.Testing.Keycloak`, e os dois tipos ficam públicos — `public static class ChavesDeTeste` e `public sealed record ParDeChaves(RSA Rsa, string PemPrivado, string CertificadoBase64);`. Acrescentar ao `<remarks>` de `ChavesDeTeste`:

```csharp
/// <para>
/// Públicos: o fixture usa a chave no <c>GATEWAY_CLIENT_CERT</c>, e a coleção de testes da Api precisa do PEM para
/// configurar a Gateway contra o mesmo Keycloak.
/// </para>
```

(o `<remarks>` atual é um parágrafo só, sem `<para>`; envolva o texto existente num `<para>` e acrescente o novo.)

Em `tests/IdentityGateway.Testing.Keycloak/RaizDoRepositorio.cs`: namespace `IdentityGateway.Testing.Keycloak` e `public static class RaizDoRepositorio`.

- [ ] **Passo 5: As exceções da biblioteca**

`tests/IdentityGateway.Testing.Keycloak/FamiliaDeFalha.cs`:

```csharp
namespace IdentityGateway.Testing.Keycloak;

/// <summary>
/// Família da falha do harness. O valor é o código de saída do app de CI: quem lê o log do job sabe em que peça olhar
/// antes de abrir o detalhe.
/// </summary>
public enum FamiliaDeFalha
{
    Mailpit = 10,
    Formulario = 20,
    DeviceFlow = 30,
    Api = 40,
    Prazo = 50,
}
```

`tests/IdentityGateway.Testing.Keycloak/FalhaDoHarnessException.cs`:

```csharp
namespace IdentityGateway.Testing.Keycloak;

/// <summary>
/// Falha de uma peça de suporte de teste: o mailpit, um formulário do Keycloak, o device flow, a API ou um prazo.
/// </summary>
/// <remarks>
/// <b>Sem segredo, por construção de quem a lança:</b> a mensagem leva a etapa e o diagnóstico — o título da página, o
/// id do formulário, os NOMES dos campos, o código de erro OAuth —, nunca token, senha, código de dispositivo, link de
/// ação nem HTML. Ela vai para o log da CI.
/// </remarks>
public sealed class FalhaDoHarnessException : Exception
{
    public FalhaDoHarnessException()
    {
    }

    public FalhaDoHarnessException(string message)
        : base(message)
    {
    }

    public FalhaDoHarnessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public FalhaDoHarnessException(FamiliaDeFalha familia, string etapa, string message)
        : base($"{etapa}: {message}")
    {
        Familia = familia;
        Etapa = etapa;
    }

    public FalhaDoHarnessException(FamiliaDeFalha familia, string etapa, string message, Exception innerException)
        : base($"{etapa}: {message}", innerException)
    {
        Familia = familia;
        Etapa = etapa;
    }

    /// <summary>A peça que falhou.</summary>
    public FamiliaDeFalha Familia { get; } = FamiliaDeFalha.Formulario;

    /// <summary>O passo da jornada em que a falha aconteceu, em texto.</summary>
    public string Etapa { get; } = string.Empty;
}
```

- [ ] **Passo 6: Ajustar o fixture ao novo lugar**

Em `tests/IdentityGateway.Testing.Keycloak/KeycloakFixture.cs`, sete edições. Nada além delas muda.

1. Os `using` e o topo do arquivo. Trocar tudo até a linha do `namespace` (inclusive) por:

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.Keycloak;
using Xunit;

namespace IdentityGateway.Testing.Keycloak;
```

(saem `IdentityGateway.Domain.Tenants`, o `using` do próprio namespace antigo, `Microsoft.Extensions.DependencyInjection` e a linha `[assembly: AssemblyFixture(typeof(KeycloakFixture))]`, que passa a viver em cada projeto de teste.)

2. No `<remarks>` da classe, trocar o primeiro `<para>` por:

```csharp
/// <para>
/// <b>Um container por assembly de teste</b>, e não por classe: o Keycloak leva dezenas de segundos para subir, e o
/// xUnit v3 rodaria as classes em paralelo, cada uma com o seu. O <c>[assembly: AssemblyFixture]</c> fica em cada
/// projeto de teste que o usa — esta biblioteca não é um projeto de teste.
/// </para>
```

3. A declaração da classe perde o `partial` (a regex gerada sai no item 7): `public sealed class KeycloakFixture : IAsyncLifetime`.

4. Depois da constante `ImagemDoMailpit`, acrescentar:

```csharp
    /// <summary>A mesma tag do <c>docker-compose.yml</c> — um teste de arquitetura confere.</summary>
    public const string ImagemDoKeycloak = "quay.io/keycloak/keycloak:26.7.4";
```

e, no construtor, trocar `new KeycloakBuilder("quay.io/keycloak/keycloak:26.7.4")` por `new KeycloakBuilder(ImagemDoKeycloak)`.

5. Trocar o comentário e a propriedade `Chaves`:

```csharp
    // Internal, não public: ParDeChaves é internal (ChavesDeTeste.cs), e uma propriedade não pode ser mais
    // acessível que o próprio tipo. Nenhum teste precisa da chave por fora — só CriarProvider a usa como padrão.
    internal ParDeChaves Chaves { get; }
```

por

```csharp
    /// <summary>O par de chaves da Gateway registrado no realm. Quem compõe a Gateway contra este Keycloak usa o PEM.</summary>
    public ParDeChaves Chaves { get; }
```

6. Apagar os dois membros que dependem da Gateway, com os comentários XML deles: `public static TenantSlug SlugUnico()` e `public ServiceProvider CriarProvider(...)`. Eles voltam como extensão no Passo 7.

7. Trocar `LinkDoConviteAsync`, a regex gerada `LinkDeAcoes()` e deixar `CaminhoDoRealm()` como está:

```csharp
    /// <summary>O link de ações do convite mais recente para o destinatário, com o host público.</summary>
    /// <exception cref="FalhaDoHarnessException">Não há mensagem, ou ela não traz o link no endereço público.</exception>
    public async Task<Uri> LinkDoConviteAsync(string destinatario, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> ids = await EsperarMensagensAsync(destinatario, 1, cancellationToken);

        if (ids.Count == 0)
        {
            throw new FalhaDoHarnessException(
                FamiliaDeFalha.Mailpit, "convite", "nenhuma mensagem para o destinatário no mailpit.");
        }

        Match link = LinkDeAcoes.Match(await TextoDaMensagemAsync(ids[0], cancellationToken));

        if (!link.Success)
        {
            throw new FalhaDoHarnessException(
                FamiliaDeFalha.Mailpit, "convite",
                "a mensagem não traz o link de ações no endereço público (KC_HOSTNAME).");
        }

        return new Uri(link.Value);
    }

    // Montada com o host e o realm das constantes, e não repetindo os dois numa regex literal.
    private static readonly Regex LinkDeAcoes = new(
        Regex.Escape(HostnamePublico) + "/realms/" + Regex.Escape(Realm) + @"/login-actions/action-token\?key=\S+",
        RegexOptions.CultureInvariant);
```

Mover o campo `LinkDeAcoes` para junto dos demais campos, no topo da classe (o analisador de ordem não reclama, mas campo depois de método destoa do arquivo).

- [ ] **Passo 7: O que depende da Gateway volta como extensão, no projeto de integração**

`tests/IdentityGateway.Infrastructure.IntegrationTests/GlobalUsings.cs` passa a ser:

```csharp
global using AwesomeAssertions;
global using IdentityGateway.Testing.Keycloak;
global using Xunit;
```

(é o que dá o namespace novo do fixture aos arquivos que o usam, sem tocar em cada um.)

Criar `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakFixtureExtensions.cs`:

```csharp
using IdentityGateway.Domain.Tenants;
using Microsoft.Extensions.DependencyInjection;

// Um Keycloak para o assembly inteiro. O atributo fica aqui, e não na biblioteca do fixture: é este projeto que é um
// projeto de teste.
[assembly: AssemblyFixture(typeof(KeycloakFixture))]

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O que o fixture do Keycloak precisa saber da Gateway, e por isso não mora na biblioteca dele.
/// </summary>
/// <remarks>
/// Membros de extensão do C# 14: <c>KeycloakFixture.SlugUnico()</c> e <c>keycloak.CriarProvider(...)</c> continuam
/// sendo chamados como antes da mudança de projeto.
/// </remarks>
internal static class KeycloakFixtureExtensions
{
    extension(KeycloakFixture)
    {
        /// <summary>Slug aleatório, válido e curto — o isolamento entre testes.</summary>
        public static TenantSlug SlugUnico() => TenantSlug.Create($"t-{Guid.NewGuid():N}"[..18]).Value;
    }

    extension(KeycloakFixture keycloak)
    {
        /// <summary>
        /// A composição real (<c>AddInfrastructure</c>) apontada para este Keycloak, com handlers de teste opcionais
        /// acrescentados antes de construir.
        /// </summary>
        public ServiceProvider CriarProvider(Action<IServiceCollection>? ajustar = null, string? pem = null)
        {
            ServiceCollection services = KeycloakHealthCheckTests.ColecaoDaComposicao(
                keycloak.BaseUrl, pem ?? keycloak.Chaves.PemPrivado, KeycloakFixture.HostnamePublico);
            ajustar?.Invoke(services);

            return services.BuildServiceProvider(validateScopes: true);
        }
    }
}
```

Em `tests/IdentityGateway.Infrastructure.IntegrationTests/DependencyInjectionTests.cs`, linha 27, trocar `Identity.Keycloak.ChavesDeTeste.Gerar().PemPrivado` por `ChavesDeTeste.Gerar().PemPrivado` (o tipo saiu daquele namespace).

Em `tests/IdentityGateway.Infrastructure.IntegrationTests/IdentityGateway.Infrastructure.IntegrationTests.csproj`:

1. No primeiro `ItemGroup`, depois da referência à Application:

```xml

    <!-- O fixture do Keycloak, o mailpit e o harness de login: biblioteca compartilhada com os testes funcionais. -->
    <ProjectReference Include="..\IdentityGateway.Testing.Keycloak\IdentityGateway.Testing.Keycloak.csproj" />
```

2. Remover a linha `<PackageReference Include="Testcontainers.Keycloak" />`: nenhum arquivo deste projeto usa mais o pacote direto (conferir antes: `grep -rn "Testcontainers.Keycloak\|KeycloakBuilder\|KeycloakContainer" tests/IdentityGateway.Infrastructure.IntegrationTests --include=*.cs` não acha nada fora de `bin/` e `obj/`).

- [ ] **Passo 8: Compilar e rodar**

Run: `dotnet build IdentityGateway.slnx`
Expected: `0 Aviso(s)`, `0 Erro(s)`. Se aparecer `CS0246` para `KeycloakFixture`, `ChavesDeTeste` ou `RaizDoRepositorio` em algum arquivo do projeto de integração, o `global using` do Passo 7 não foi gravado.

Run: `dotnet test tests/IdentityGateway.ArchitectureTests --filter-class "*RegrasDoAmbienteLocalTests"`
Expected: PASS — `total: 5`.

Run (Docker ligado): `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests`
Expected: PASS — `total: 189`, `falhou: 0`, `ignorados: 0`. O número é o de antes da tarefa: nada mudou de comportamento.

Run: `dotnet test`
Expected: cinco projetos de teste no resumo (a biblioteca não aparece como projeto de teste), `total: 492` (os 491 de antes mais a regra da tag do Keycloak), `falhou: 0`.

- [ ] **Passo 9: 🧪 Prova por mutação**

1. Em `tests/IdentityGateway.Testing.Keycloak/KeycloakFixture.cs`, trocar a tag em `ImagemDoKeycloak` para `26.7.3`.

Run: `dotnet test tests/IdentityGateway.ArchitectureTests --filter-method "*ComposeEFixtureUsamAMesmaTagDoKeycloak"`
Expected: FAIL — "o teste de integração precisa provar o mesmo Keycloak que o compose sobe".

2. Reverter (`git checkout tests/IdentityGateway.Testing.Keycloak/KeycloakFixture.cs` **não serve**: o arquivo ainda não foi commitado neste caminho; desfaça a edição à mão e confira com `grep -n "26.7" tests/IdentityGateway.Testing.Keycloak/KeycloakFixture.cs`, que deve mostrar só `26.7.4`). Rodar de novo e ver verde.

- [ ] **Passo 10: Commit**

```bash
git add -A tests/IdentityGateway.Testing.Keycloak tests/IdentityGateway.Infrastructure.IntegrationTests tests/IdentityGateway.ArchitectureTests/RegrasDoAmbienteLocalTests.cs Directory.Packages.props IdentityGateway.slnx
git commit -m "test: fixture do Keycloak numa biblioteca de suporte

O KeycloakFixture, as chaves de teste e a raiz do repositorio saem do
projeto de integracao para tests/IdentityGateway.Testing.Keycloak, uma
biblioteca (IsTestProject=false, sem xunit.v3 nem AwesomeAssertions, com
xunit.v3.extensibility.core) que os testes funcionais e o app de CI
tambem vao usar. Sem mudanca de comportamento: SlugUnico e CriarProvider
viram membros de extensao no projeto de integracao, e o link do convite
passa a lancar FalhaDoHarnessException em vez de usar assercoes.

Regra nova de arquitetura: o compose e o fixture usam a mesma tag do
Keycloak. Mutacao: tag 26.7.3 no fixture deixa a regra vermelha."
```

Run: `git log -1 --format=%B | grep -Eci "co-authored|generated with"`
Expected: `0`.

---

### Tarefa 2: Realm, parte estática — o JSON final e as regras

Spec: §4.4 inteira, §3.1 (os fatos por trás de cada regra), D-e, D-n, DT9, DT10, DT11.

Tudo o que a fatia muda no realm entra aqui, de uma vez: a D2 não pode exigir outro `docker compose down -v`.

**Arquivos:**
- Modify: `keycloak/bootstrap/realm-identity-gateway.json` (arquivo inteiro)
- Modify: `tests/IdentityGateway.Testing.Keycloak/KeycloakFixture.cs` (constante `EmailDoPlatformAdmin` e a variável do container)
- Test: `tests/IdentityGateway.ArchitectureTests/RegrasDoRealmTests.cs`

**Interfaces:**
- Consome: `KeycloakFixture` na biblioteca (Tarefa 1).
- Produz:
  - o realm com os scopes `gateway-roles`, `gateway-tenant`, `gateway-api`, os clients `identity-gateway` e `identity-gateway-demo`, o catálogo de papéis e o usuário `${PLATFORM_ADMIN_EMAIL}`;
  - `public const string KeycloakFixture.EmailDoPlatformAdmin = "platform-admin@identity-gateway.test"`.

- [ ] **Passo 1: Escrever as regras novas do realm**

Em `tests/IdentityGateway.ArchitectureTests/RegrasDoRealmTests.cs`:

1. Acrescentar, junto dos campos estáticos do topo da classe:

```csharp
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
        ["fullScopeAllowed", "directAccessGrantsEnabled", "defaultClientScopes", "optionalClientScopes"];

    private static readonly string[] ChavesQueOBootstrapNaoTem = ["attributes", "groups", "clientRoles", "credentials"];
```

2. Acrescentar, depois de `ConfiguracaoDoPerfil()`:

```csharp
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

    private static JsonElement ConfigDoMapperUnico(string scope, string tipo)
    {
        JsonElement[] mappers = [.. Scope(scope).GetProperty("protocolMappers").EnumerateArray()];

        mappers.Should().ContainSingle($"o scope {scope} tem um mapper só");
        mappers[0].GetProperty("protocolMapper").GetString().Should().Be(tipo);

        return mappers[0].GetProperty("config");
    }
```

3. Em `NenhumaChaveDeCredencial`, trocar `.. Percorrer(Realm(), "$")` por:

```csharp
            // O User Profile é um JSON em texto dentro do JSON: sem percorrê-lo também, uma chave proibida ali passaria.
            .. Percorrer(Realm(), "$").Concat(Percorrer(ConfiguracaoDoPerfil(), "$.kc.user.profile.config"))
```

4. Apagar o teste `PapelDeRealmTenantAdminExiste` (a regra do catálogo o substitui) e acrescentar, no fim da classe:

```csharp
    [Fact]
    public void CatalogoDePapeisExatoENuncaComposto()
    {
        // O checkAdminRoles do Keycloak olha só o NOME do papel: um papel do catálogo composto com papéis de
        // realm-management seria atribuível por quem tem manage-users — a chave da Gateway.
        JsonElement realm = Realm();
        JsonElement[] papeis = [.. realm.GetProperty("roles").GetProperty("realm").EnumerateArray()];

        papeis.Select(papel => papel.GetProperty("name").GetString())
            .Should().BeEquivalentTo(PapeisDeclarados);
        papeis.Should().NotContain(papel => Verdadeiro(papel, "composite") || papel.TryGetProperty("composites", out _),
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
        // Client sem defaultClientScopes herda os defaults do realm; sem fullScopeAllowed, vale o padrão (true); com
        // direct grant, é ROPC (ADR-003). Nada disso pode ficar por conta do padrão do Keycloak.
        foreach (JsonElement client in Realm().GetProperty("clients").EnumerateArray())
        {
            string id = client.GetProperty("clientId").GetString()!;

            ChavesObrigatoriasDoClient.Should().OnlyContain(
                chave => client.TryGetProperty(chave, out _), $"o client {id} declara as quatro chaves");
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
        ChavesQueOBootstrapNaoTem.Should().NotContain(chave => admin.TryGetProperty(chave, out _));
    }
```

- [ ] **Passo 2: Rodar e ver falhar**

Run: `dotnet test tests/IdentityGateway.ArchitectureTests --filter-class "*RegrasDoRealmTests"`
Expected: FAIL em doze dos treze testes novos (`KeyNotFoundException` em `clientScopes`/`attributes`, `InvalidOperationException` no `Single` do demo, ou asserção — o realm atual não tem nada disso). `RealmSemGrupos` já passa: o realm de hoje não tem grupos, e a regra existe para travar isso. Os testes antigos continuam verdes, inclusive o `NenhumaChaveDeCredencial` ampliado.

- [ ] **Passo 3: O realm final**

Substituir `keycloak/bootstrap/realm-identity-gateway.json` **inteiro** pelo conteúdo abaixo. É o arquivo que foi importado e exercitado ao escrever este plano; o `smtpServer`, o User Profile e o client do service account são os da fatia C, com `fullScopeAllowed`, `defaultClientScopes` e `optionalClientScopes` acrescentados. Grave em LF, com uma linha em branco só no fim.

```json
{
  "realm": "identity-gateway",
  "enabled": true,
  "organizationsEnabled": true,
  "resetPasswordAllowed": false,
  "registrationAllowed": false,
  "bruteForceProtected": true,
  "adminEventsEnabled": true,
  "accessTokenLifespan": 300,
  "revokeRefreshToken": true,
  "refreshTokenMaxReuse": 0,
  "attributes": {
    "CreateDefaultClientScopes": "true"
  },
  "smtpServer": {
    "host": "${SMTP_HOST}",
    "port": "${SMTP_PORT}",
    "from": "${SMTP_FROM}",
    "auth": "false",
    "ssl": "false",
    "starttls": "false",
    "connectionTimeout": "2000",
    "timeout": "3000",
    "writeTimeout": "3000"
  },
  "roles": {
    "realm": [
      {
        "name": "tenant-admin",
        "description": "Administrador de um tenant. O tenant vem do atributo tenant_id do usuário."
      },
      {
        "name": "platform-admin",
        "description": "Operador da plataforma. Não pertence a nenhum tenant."
      },
      {
        "name": "financial-manager",
        "description": "Gestor financeiro de um tenant."
      },
      {
        "name": "reader",
        "description": "Leitura dentro de um tenant."
      },
      {
        "name": "offline_access",
        "description": "Declarado só para ficar fora do papel padrão: o projeto não emite offline tokens."
      },
      {
        "name": "uma_authorization",
        "description": "Declarado só para ficar fora do papel padrão: o projeto não usa UMA."
      }
    ]
  },
  "components": {
    "org.keycloak.userprofile.UserProfileProvider": [
      {
        "providerId": "declarative-user-profile",
        "subComponents": {},
        "config": {
          "kc.user.profile.config": [
            "{\"attributes\":[{\"name\":\"username\",\"displayName\":\"Username\",\"permissions\":{\"view\":[\"admin\",\"user\"],\"edit\":[\"admin\",\"user\"]},\"validations\":{\"length\":{\"min\":3,\"max\":255},\"username-prohibited-characters\":{},\"up-username-not-idn-homograph\":{}}},{\"name\":\"email\",\"displayName\":\"Email\",\"required\":{\"roles\":[\"user\"]},\"permissions\":{\"view\":[\"admin\",\"user\"],\"edit\":[\"admin\",\"user\"]},\"validations\":{\"email\":{},\"length\":{\"max\":255}}},{\"name\":\"firstName\",\"displayName\":\"First name\",\"required\":{\"roles\":[\"user\"]},\"permissions\":{\"view\":[\"admin\",\"user\"],\"edit\":[\"admin\",\"user\"]},\"validations\":{\"length\":{\"max\":255},\"person-name-prohibited-characters\":{}}},{\"name\":\"lastName\",\"displayName\":\"Last name\",\"required\":{\"roles\":[\"user\"]},\"permissions\":{\"view\":[\"admin\",\"user\"],\"edit\":[\"admin\",\"user\"]},\"validations\":{\"length\":{\"max\":255},\"person-name-prohibited-characters\":{}}},{\"name\":\"tenant_id\",\"displayName\":\"Tenant\",\"permissions\":{\"view\":[\"admin\"],\"edit\":[\"admin\"]},\"multivalued\":false}],\"groups\":[{\"name\":\"user-metadata\",\"displayHeader\":\"User metadata\",\"displayDescription\":\"Attributes, which refer to user metadata\"}]}"
          ]
        }
      }
    ]
  },
  "clientScopes": [
    {
      "name": "gateway-roles",
      "protocol": "openid-connect",
      "attributes": {
        "include.in.token.scope": "false",
        "display.on.consent.screen": "false"
      },
      "protocolMappers": [
        {
          "name": "roles-plano",
          "protocol": "openid-connect",
          "protocolMapper": "oidc-usermodel-realm-role-mapper",
          "config": {
            "claim.name": "roles",
            "jsonType.label": "String",
            "multivalued": "true",
            "access.token.claim": "true",
            "id.token.claim": "false",
            "userinfo.token.claim": "false",
            "introspection.token.claim": "true"
          }
        }
      ]
    },
    {
      "name": "gateway-tenant",
      "protocol": "openid-connect",
      "attributes": {
        "include.in.token.scope": "false",
        "display.on.consent.screen": "false"
      },
      "protocolMappers": [
        {
          "name": "tenant-id-plano",
          "protocol": "openid-connect",
          "protocolMapper": "oidc-usermodel-attribute-mapper",
          "config": {
            "user.attribute": "tenant_id",
            "claim.name": "tenant_id",
            "jsonType.label": "String",
            "multivalued": "false",
            "aggregate.attrs": "false",
            "access.token.claim": "true",
            "id.token.claim": "false",
            "userinfo.token.claim": "false",
            "introspection.token.claim": "true"
          }
        }
      ]
    },
    {
      "name": "gateway-api",
      "protocol": "openid-connect",
      "attributes": {
        "include.in.token.scope": "false",
        "display.on.consent.screen": "false"
      },
      "protocolMappers": [
        {
          "name": "audiencia-identity-gateway-api",
          "protocol": "openid-connect",
          "protocolMapper": "oidc-audience-mapper",
          "config": {
            "included.custom.audience": "identity-gateway-api",
            "access.token.claim": "true",
            "id.token.claim": "false",
            "introspection.token.claim": "true"
          }
        }
      ]
    },
    {
      "name": "offline_access",
      "protocol": "openid-connect",
      "attributes": {
        "display.on.consent.screen": "false"
      }
    }
  ],
  "scopeMappings": [
    {
      "clientScope": "gateway-roles",
      "roles": [
        "platform-admin",
        "tenant-admin",
        "financial-manager",
        "reader"
      ]
    }
  ],
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
      },
      "fullScopeAllowed": true,
      "defaultClientScopes": [
        "basic",
        "roles"
      ],
      "optionalClientScopes": []
    },
    {
      "clientId": "identity-gateway-demo",
      "name": "Demonstração por curl (device flow)",
      "description": "Client público de demonstração, SÓ do ambiente local: Device Authorization Grant e nada mais. Não vai ao Terraform de produção.",
      "enabled": true,
      "protocol": "openid-connect",
      "publicClient": true,
      "standardFlowEnabled": false,
      "implicitFlowEnabled": false,
      "directAccessGrantsEnabled": false,
      "serviceAccountsEnabled": false,
      "fullScopeAllowed": false,
      "attributes": {
        "oauth2.device.authorization.grant.enabled": "true",
        "oauth2.device.code.lifespan": "300"
      },
      "defaultClientScopes": [
        "basic",
        "acr",
        "gateway-roles",
        "gateway-tenant",
        "gateway-api"
      ],
      "optionalClientScopes": []
    }
  ],
  "users": [
    {
      "username": "service-account-identity-gateway",
      "enabled": true,
      "serviceAccountClientId": "identity-gateway",
      "clientRoles": {
        "realm-management": [
          "manage-organizations",
          "manage-users"
        ]
      }
    },
    {
      "username": "${PLATFORM_ADMIN_EMAIL}",
      "email": "${PLATFORM_ADMIN_EMAIL}",
      "enabled": true,
      "emailVerified": false,
      "requiredActions": [
        "UPDATE_PASSWORD",
        "VERIFY_EMAIL"
      ],
      "realmRoles": [
        "platform-admin"
      ]
    }
  ]
}
```

Run: `grep -c '\${' keycloak/bootstrap/realm-identity-gateway.json`
Expected: `6` — `SMTP_HOST`, `SMTP_PORT`, `SMTP_FROM`, `GATEWAY_CLIENT_CERT` e as duas ocorrências de `PLATFORM_ADMIN_EMAIL`. Nenhum placeholder de i18n do Keycloak (`${role_…}`, `${…ConsentText}`): eles reprovariam o `TodoPlaceholderEPuro`.

- [ ] **Passo 4: O fixture passa o e-mail do platform-admin**

O realm agora tem um usuário com `${PLATFORM_ADMIN_EMAIL}`; sem o valor, o import gravaria o placeholder literal como username e e-mail.

Em `tests/IdentityGateway.Testing.Keycloak/KeycloakFixture.cs`, depois da constante `ImagemDoKeycloak`:

```csharp
    /// <summary>
    /// O e-mail do platform-admin do bootstrap — o <c>${PLATFORM_ADMIN_EMAIL}</c> do realm.
    /// </summary>
    /// <remarks>
    /// Em minúsculas, como o import o grava. Este usuário serve à prova viva do realm importado; os testes que
    /// precisam de um platform-admin logado criam o seu, porque o link de ações é de uso único.
    /// </remarks>
    public const string EmailDoPlatformAdmin = "platform-admin@identity-gateway.test";
```

e, no construtor, depois da linha do `SMTP_FROM`:

```csharp
            .WithEnvironment("PLATFORM_ADMIN_EMAIL", EmailDoPlatformAdmin)
```

- [ ] **Passo 5: Rodar e ver passar**

Run: `dotnet test tests/IdentityGateway.ArchitectureTests --filter-class "*RegrasDoRealmTests"`
Expected: PASS — todos, inclusive `TodoPlaceholderEPuro`, `NenhumaChaveDeCredencial` e `NenhumBase64LongoLiteral`.

Run (Docker ligado): `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests`
Expected: PASS — `total: 189`, `falhou: 0`. É a prova de que o realm novo **importa** e não quebra o que a fatia C entregou: o fixture inteiro cai se o import falhar, e `HealthCheck_ComKeycloakDePe_Healthy` cai se o service account perder o `resource_access` (é o que `fullScopeAllowed: false` ou a falta do scope `roles` fariam).

Se o fixture não subir, o motivo está no log do container: rode um teste só (`--filter-method "*ComAChaveRegistrada_ObtemToken"`) e leia a exceção do Testcontainers; o Keycloak escreve `ERROR` com a chave do JSON que recusou.

- [ ] **Passo 6: 🧪 Provas por mutação — uma por regra**

Primeiro, pôr o realm no índice, para a reversão ser exata: `git add keycloak/bootstrap/realm-identity-gateway.json`. Depois de cada mutação: rodar, ver o teste indicado vermelho, e reverter com `git restore keycloak/bootstrap/realm-identity-gateway.json` (restaura do índice; `git diff --stat` fica vazio).

Run (a cada mutação): `dotnet test tests/IdentityGateway.ArchitectureTests --filter-class "*RegrasDoRealmTests"`

| # | Mutação no JSON | Teste que fica vermelho |
|---|---|---|
| 1 | Acrescentar `"composite": true` ao papel `reader` | `CatalogoDePapeisExatoENuncaComposto` |
| 2 | Apagar a linha `"CreateDefaultClientScopes": "true"` (deixando `"attributes": {}`) | `CreateDefaultClientScopesLigado` |
| 3 | Acrescentar `"defaultDefaultClientScopes": ["gateway-api"]` na raiz | `ScopesDaGatewayExistemEForaDosDefaultsDoRealm` |
| 4 | Acrescentar `"offline_access"` à lista `roles` do `scopeMappings` | `GatewayRolesEmiteSoOCatalogo` |
| 5 | Trocar `"aggregate.attrs": "false"` por `"true"` | `GatewayTenantEmiteUmValorSoSemAgregar` |
| 6 | Trocar `"id.token.claim": "false"` por `"true"` no mapper do `gateway-api` | `AudienciaDaGatewaySoNoScopeGatewayApi` |
| 7 | Acrescentar `"gateway-api"` aos `defaultClientScopes` do client `identity-gateway` | `TodoClientDeclaraEscoposEFluxos` |
| 8 | Trocar `"fullScopeAllowed": false` por `true` no demo; depois, noutra rodada, apagar a linha `"optionalClientScopes": []` do demo | `ClientDeDemonstracaoSoComDeviceFlow` na primeira; `TodoClientDeclaraEscoposEFluxos` na segunda |
| 9 | Trocar `"directAccessGrantsEnabled": false` por `true` no demo | `TodoClientDeclaraEscoposEFluxos` |
| 10 | Acrescentar `"email"` aos `defaultClientScopes` do demo | `ClientDeDemonstracaoSoComDeviceFlow` |
| 11 | Tirar `"basic"` dos `defaultClientScopes` do demo | `ClientDeDemonstracaoSoComDeviceFlow` |
| 12 | Acrescentar `"oauth2.device.authorization.grant.enabled": "true"` aos `attributes` do client `identity-gateway` | `SoOClientDeDemonstracaoTemDeviceFlow` |
| 13 | Acrescentar `"groups": [{ "name": "g", "attributes": { "tenant_id": ["x"] } }]` na raiz | `RealmSemGrupos` |
| 14 | Apagar o scope `offline_access` de `clientScopes` | `OfflineAccessDeclaradoEForaDeTodoClient` |
| 15 | Trocar `"revokeRefreshToken": true` por `false` | `TemposEProtecoesDoRealm` |
| 16 | Trocar `"accessTokenLifespan": 300` por `3600` | `TemposEProtecoesDoRealm` |
| 17 | Acrescentar `"tenant-admin"` aos `realmRoles` do usuário do bootstrap | `PlatformAdminDoBootstrapNasceSemSenhaESoComOPapel` |
| 18 | Acrescentar `"credentials": [{ "type": "password", "value": "x" }]` ao usuário do bootstrap | `PlatformAdminDoBootstrapNasceSemSenhaESoComOPapel` e `NenhumaChaveDeCredencial` |
| 19 | Dentro do texto de `kc.user.profile.config`, acrescentar `,\"secret\":\"x\"` logo depois de `{\"name\":\"tenant_id\"` | `NenhumaChaveDeCredencial` (pelo trecho novo: a chave está dentro do JSON embutido) |
| 20 | Trocar a descrição do papel `offline_access` por `"${role_offline-access}"` | `TodoPlaceholderEPuro` |

Anotar, para o handoff, o teste e a mensagem de cada vermelho. As mutações 3, 7, 8 (`fullScopeAllowed`), 2, 14 e 15 têm uma segunda testemunha contra o Keycloak real, nas Tarefas 3 e 5.

Depois da última reversão:

Run: `git diff --stat`
Expected: vazio (só o que está no índice).

- [ ] **Passo 7: Commit**

```bash
git add keycloak/bootstrap/realm-identity-gateway.json tests/IdentityGateway.ArchitectureTests/RegrasDoRealmTests.cs tests/IdentityGateway.Testing.Keycloak/KeycloakFixture.cs
git commit -m "feat: realm com o catalogo, os scopes da Gateway e o client de demonstracao

O realm passa a emitir o token da 10.1: roles plano so com o catalogo
(gateway-roles, com scope mappings e fullScopeAllowed falso no client),
tenant_id plano sem agregar (gateway-tenant) e a audiencia
identity-gateway-api num scope proprio (gateway-api), nenhum deles
default do realm. CreateDefaultClientScopes mantem os scopes embutidos,
que declarar clientScopes apagaria. Entram o client publico
identity-gateway-demo, so com device flow, o platform-admin do bootstrap
sem senha, a rotacao do refresh token, accessTokenLifespan 300 e
bruteForceProtected. O service account fica com basic e roles.

Treze regras novas em RegrasDoRealmTests, cada uma provada por mutacao no
JSON; NenhumaChaveDeCredencial passa a percorrer o User Profile.

Quem ja tem volumes do compose precisa de docker compose down -v."
```

Run: `git log -1 --format=%B | grep -Eci "co-authored|generated with"`
Expected: `0`.

---

### Tarefa 3: Realm, parte viva — o que o import realmente produziu

Spec: §3.1 (ordem do import, scopes embutidos, `fullScopeAllowed` do service account), §5.1 (linha "Integração, Keycloak real"), DT10.

As regras da Tarefa 2 leem o JSON. Esta tarefa lê o **realm importado**, pelo master: o atributo `CreateDefaultClientScopes` não persiste, o papel padrão é montado pelo import, e o token do service account só existe depois que o Keycloak o emite.

**Arquivos:**
- Create: `tests/IdentityGateway.Testing.Keycloak/PayloadDoJwt.cs`
- Modify: `tests/IdentityGateway.Testing.Keycloak/KeycloakFixture.cs` (`LerComoMasterAsync`)
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/RealmVivoTests.cs`
- Modify: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakRealTests.cs:24-25` (`PapeisDeRealmAceitos`)

**Interfaces:**
- Consome: o realm da Tarefa 2; `KeycloakFixture.EmailDoPlatformAdmin`; `keycloak.CriarProvider()` (Tarefa 1); `ServiceAccountTokenCache.ObterAsync(ct)` (existente, `internal` da Infrastructure, visível ao projeto de integração).
- Produz:
  - `public Task<JsonElement> KeycloakFixture.LerComoMasterAsync(string caminho, CancellationToken cancellationToken)` — `GET admin/realms/identity-gateway/{caminho}` como admin do master; `caminho` vazio lê o próprio realm.
  - `public static class PayloadDoJwt` com `public static JsonElement Ler(string jwt)` e `public static IReadOnlyList<string> Audiencias(JsonElement payload)`.

- [ ] **Passo 1: O leitor de payload e a leitura pelo master**

`tests/IdentityGateway.Testing.Keycloak/PayloadDoJwt.cs`:

```csharp
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
```

Em `tests/IdentityGateway.Testing.Keycloak/KeycloakFixture.cs`, depois de `CriarClienteMasterAsync`:

```csharp
    /// <summary>
    /// Lê um recurso do realm pela Admin API, como admin do master — o JSON cru, nunca um DTO de quem está sob teste.
    /// </summary>
    /// <param name="caminho">Relativo a <c>admin/realms/identity-gateway/</c>; vazio lê o próprio realm.</param>
    public async Task<JsonElement> LerComoMasterAsync(string caminho, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        string json = await master.GetStringAsync(
            new Uri($"admin/realms/{Realm}/{caminho}".TrimEnd('/'), UriKind.Relative), cancellationToken);

        using var documento = JsonDocument.Parse(json);
        return documento.RootElement.Clone();
    }
```

- [ ] **Passo 2: Escrever os testes do realm vivo**

`tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/RealmVivoTests.cs`:

```csharp
using System.Text.Json;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O realm depois do import, lido pelo master: o que o JSON declara só vale se o Keycloak o gravou assim.
/// </summary>
/// <remarks>
/// As regras de <c>RegrasDoRealmTests</c> leem o arquivo. Três coisas elas não veem: o atributo
/// <c>CreateDefaultClientScopes</c> não persiste (só o efeito dele: os scopes embutidos existem), o papel padrão é
/// montado pelo import, e o token do service account só existe emitido.
/// </remarks>
public sealed class RealmVivoTests(KeycloakFixture keycloak)
{
    private static readonly string[] ScopesEmbutidos = ["basic", "roles", "acr", "profile", "email", "web-origins"];

    private static readonly string[] ScopesDaGateway = ["gateway-roles", "gateway-tenant", "gateway-api"];

    private static readonly string[] ScopesDoDemo = ["basic", "acr", "gateway-roles", "gateway-tenant", "gateway-api"];

    private static readonly string[] ScopesDoServiceAccount = ["basic", "roles"];

    private static readonly string[] PapeisDaConta = ["manage-account", "view-profile"];

    private static readonly string[] PapeisDoServiceAccount = ["manage-organizations", "manage-users"];

    private static readonly string[] AcoesDoBootstrap = ["UPDATE_PASSWORD", "VERIFY_EMAIL"];

    private static string[] Nomes(JsonElement lista) =>
        [.. lista.EnumerateArray().Select(item => item.GetProperty("name").GetString()!)];

    private async Task<string> IdDoClientAsync(string clientId, CancellationToken ct)
    {
        JsonElement clients = await keycloak.LerComoMasterAsync($"clients?clientId={clientId}", ct);
        return clients[0].GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task ScopesEmbutidos_ExistemNoRealm()
    {
        // Sem o CreateDefaultClientScopes, declarar clientScopes no JSON apaga os embutidos sem erro — e o access token
        // sai sem sub, que vem do scope basic.
        CancellationToken ct = TestContext.Current.CancellationToken;

        string[] scopes = Nomes(await keycloak.LerComoMasterAsync("client-scopes", ct));

        scopes.Should().Contain(ScopesEmbutidos).And.Contain(ScopesDaGateway);
    }

    [Fact]
    public async Task ScopesDaGateway_NaoSaoDefaultNemOpcionalDoRealm()
    {
        // D-e: um client criado depois pela Admin API herda os defaults do realm. Com gateway-api entre eles, qualquer
        // client novo emitiria token aceito pela Gateway.
        CancellationToken ct = TestContext.Current.CancellationToken;

        string[] defaults = Nomes(await keycloak.LerComoMasterAsync("default-default-client-scopes", ct));
        string[] opcionais = Nomes(await keycloak.LerComoMasterAsync("default-optional-client-scopes", ct));

        defaults.Should().Contain("basic", "controle: a lista de defaults do realm foi lida de verdade");
        defaults.Concat(opcionais).Should().NotContain(ScopesDaGateway).And.NotContain("offline_access");
    }

    [Fact]
    public async Task PapelPadrao_SoComOsPapeisDaConta()
    {
        // offline_access e uma_authorization entrariam aqui se o JSON não declarasse os dois papéis e o scope — e, pelo
        // papel padrão, em todo usuário novo.
        CancellationToken ct = TestContext.Current.CancellationToken;
        JsonElement padrao = await keycloak.LerComoMasterAsync($"roles/default-roles-{KeycloakFixture.Realm}", ct);

        string[] compostos = Nomes(await keycloak.LerComoMasterAsync(
            $"roles-by-id/{padrao.GetProperty("id").GetString()}/composites", ct));

        compostos.Should().BeEquivalentTo(PapeisDaConta);
    }

    [Fact]
    public async Task Clients_FicaramComOsScopesDoJson()
    {
        // Os clients embutidos nascem antes dos scopes do JSON; os declarados no JSON precisam ter ficado exatamente
        // com os scopes que o JSON lista, sem herdar os defaults do realm.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string demo = await IdDoClientAsync("identity-gateway-demo", ct);
        string gateway = await IdDoClientAsync("identity-gateway", ct);

        Nomes(await keycloak.LerComoMasterAsync($"clients/{demo}/default-client-scopes", ct))
            .Should().BeEquivalentTo(ScopesDoDemo);
        Nomes(await keycloak.LerComoMasterAsync($"clients/{demo}/optional-client-scopes", ct)).Should().BeEmpty();
        Nomes(await keycloak.LerComoMasterAsync($"clients/{gateway}/default-client-scopes", ct))
            .Should().BeEquivalentTo(ScopesDoServiceAccount);
    }

    [Fact]
    public async Task Realm_GravouOsTemposEARotacaoDoRefresh()
    {
        // Chave que o import não reconhece é ignorada em silêncio: o que vale é o que o realm devolve.
        CancellationToken ct = TestContext.Current.CancellationToken;

        JsonElement realm = await keycloak.LerComoMasterAsync(string.Empty, ct);

        realm.GetProperty("accessTokenLifespan").GetInt32().Should().Be(300);
        realm.GetProperty("revokeRefreshToken").GetBoolean().Should().BeTrue();
        realm.GetProperty("refreshTokenMaxReuse").GetInt32().Should().Be(0);
        realm.GetProperty("registrationAllowed").GetBoolean().Should().BeFalse();
        realm.GetProperty("bruteForceProtected").GetBoolean().Should().BeTrue();
        realm.GetProperty("resetPasswordAllowed").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task PlatformAdminDoBootstrap_NasceSemSenhaSoComOPapelEComAsDuasAcoes()
    {
        // D-h. Leitura crua: o placeholder foi substituído, o e-mail ficou em minúsculas, não há credencial, e o único
        // papel de realm atribuído é platform-admin — sem o papel padrão, que o import não dá a usuário do JSON com
        // realmRoles.
        CancellationToken ct = TestContext.Current.CancellationToken;
        IReadOnlyList<JsonElement> achados = await keycloak.UsuariosPorEmailAsync(KeycloakFixture.EmailDoPlatformAdmin, ct);

        achados.Should().ContainSingle();
        JsonElement usuario = achados[0];
        string id = usuario.GetProperty("id").GetString()!;
        JsonElement papeis = await keycloak.LerComoMasterAsync($"users/{id}/role-mappings", ct);

        usuario.GetProperty("username").GetString().Should().Be(KeycloakFixture.EmailDoPlatformAdmin);
        usuario.GetProperty("email").GetString().Should().Be(KeycloakFixture.EmailDoPlatformAdmin);
        usuario.GetProperty("enabled").GetBoolean().Should().BeTrue();
        usuario.GetProperty("emailVerified").GetBoolean().Should().BeFalse();
        usuario.GetProperty("requiredActions").EnumerateArray().Select(acao => acao.GetString())
            .Should().BeEquivalentTo(AcoesDoBootstrap);
        usuario.TryGetProperty("attributes", out _).Should().BeFalse();

        Nomes(papeis.GetProperty("realmMappings")).Should().Equal("platform-admin");
        papeis.TryGetProperty("clientMappings", out _).Should().BeFalse();
        (await keycloak.LerComoMasterAsync($"users/{id}/credentials", ct)).GetArrayLength().Should().Be(0);
        (await keycloak.LerComoMasterAsync($"users/{id}/groups", ct)).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task TokenDoServiceAccount_TemOsPapeisDeAdministracaoENaoAAudienciaDaGateway()
    {
        // DT10 e D-e. O token do service account abre a Admin API (resource_access, do scope roles) e NÃO serve na
        // própria Gateway: sem gateway-api, a audiência identity-gateway-api não entra; sem gateway-roles, não há claim
        // roles; sem gateway-tenant, não há tenant_id.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();

        JsonElement payload = PayloadDoJwt.Ler(
            await provider.GetRequiredService<ServiceAccountTokenCache>().ObterAsync(ct));

        payload.GetProperty("resource_access").GetProperty("realm-management").GetProperty("roles")
            .EnumerateArray().Select(papel => papel.GetString())
            .Should().BeEquivalentTo(PapeisDoServiceAccount);
        PayloadDoJwt.Audiencias(payload).Should().NotContain("identity-gateway-api");
        payload.GetProperty("azp").GetString().Should().Be("identity-gateway");
        payload.TryGetProperty("roles", out _).Should().BeFalse();
        payload.TryGetProperty("tenant_id", out _).Should().BeFalse();
    }
}
```

Em `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakRealTests.cs`, apertar a lista de papéis de realm aceitos no service account — o papel padrão deixou de carregar `offline_access` e `uma_authorization`:

```csharp
    private static readonly string[] PapeisDeRealmAceitos = ["default-roles-identity-gateway"];
```

- [ ] **Passo 3: Rodar**

Run (Docker ligado): `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter-class "*RealmVivoTests"`
Expected: PASS — `total: 7`. (O realm já é o da Tarefa 2: estes testes nascem verdes, e o vermelho deles é observado no Passo 4, por mutação. É a ordem certa para uma prova de configuração: o defeito que eles pegam é o JSON errado, não código ausente.)

Não há teste vivo de "o realm não tem grupos": a regra estática da Tarefa 2 cobre o JSON, e um teste de caracterização da Tarefa 5 cria um grupo por alguns segundos no realm compartilhado — um teste vivo sobre a contagem de grupos correria contra ele.

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter-class "*KeycloakRealTests"`
Expected: PASS — `total: 7`, inclusive `ServiceAccount_PapeisEfetivosSaoExatamenteOsDois` com a lista apertada e `HealthCheck_ComKeycloakDePe_Healthy`.

- [ ] **Passo 4: 🧪 Provas por mutação no realm importado**

Cada mutação muda o JSON, e o fixture reimporta na execução seguinte. Reverter com `git checkout keycloak/bootstrap/realm-identity-gateway.json` depois de cada uma.

Run (a cada mutação): `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter-class "*RealmVivoTests"`

| # | Mutação no JSON | Vermelho esperado |
|---|---|---|
| 1 | Apagar a linha `"CreateDefaultClientScopes": "true"` (deixando `"attributes": {}`) | `ScopesEmbutidos_ExistemNoRealm` — ou o fixture inteiro não sobe, se o import recusar o client que referencia `basic` e `roles`. Anote qual dos dois aconteceu: os dois são vermelho, e o handoff registra o observado |
| 2 | Acrescentar `"gateway-api"` aos `defaultClientScopes` do client `identity-gateway` | `TokenDoServiceAccount_TemOsPapeisDeAdministracaoENaoAAudienciaDaGateway` e `Clients_FicaramComOsScopesDoJson` |
| 3 | Apagar o scope `offline_access` de `clientScopes` | `PapelPadrao_SoComOsPapeisDaConta` |
| 4 | Trocar `"fullScopeAllowed": true` por `false` no client `identity-gateway` | `TokenDoServiceAccount_…` (o `resource_access` some) — e, no resto da suíte, `HealthCheck_ComKeycloakDePe_Healthy` |
| 5 | Acrescentar `"tenant-admin"` aos `realmRoles` do usuário do bootstrap | `PlatformAdminDoBootstrap_NasceSemSenhaSoComOPapelEComAsDuasAcoes` |
| 6 | Trocar `"revokeRefreshToken": true` por `false` | `Realm_GravouOsTemposEARotacaoDoRefresh` |

Depois da última reversão:

Run: `git status --short keycloak/`
Expected: vazio.

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests`
Expected: PASS — `total: 196` (189 + 7), `falhou: 0`.

- [ ] **Passo 5: Commit**

```bash
git add tests/IdentityGateway.Testing.Keycloak tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/RealmVivoTests.cs tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakRealTests.cs
git commit -m "test: prova viva do realm importado

Lido pelo master, depois do import: os scopes embutidos existem (o efeito
do CreateDefaultClientScopes, que nao persiste), os gateway-* ficam fora
dos defaults do realm, o papel padrao so tem manage-account e
view-profile, cada client ficou com os scopes do JSON, o platform-admin
do bootstrap nasce sem credencial e so com o papel, e o token do service
account abre a Admin API sem carregar a audiencia da Gateway.

Mutacoes no JSON, com reimport: sem CreateDefaultClientScopes, gateway-api
no service account, sem o scope offline_access, fullScopeAllowed falso no
service account, tenant-admin no bootstrap e revokeRefreshToken falso."
```

Run: `git log -1 --format=%B | grep -Eci "co-authored|generated with"`
Expected: `0`.

---

### Tarefa 4: Harness de login por device flow, cliente do mailpit e o fim do ROPC no fixture

Spec: §4.6 ("Harness de login por HTTP", "O `KeycloakFixture`", "Um platform-admin por teste"), §3.1 (device flow, `slow_down`), §3.2 (páginas do link de ações), D-c, DT7.

O harness é um navegador mínimo: conclui o link do e-mail de ações e faz login pelo Device Authorization Grant, submetendo as páginas do Keycloak. É a **única** forma de obter token de usuário nos testes, na CI e no README. O código do `HarnessDeLogin` abaixo foi executado contra o Keycloak 26.7.4 ao escrever este plano (ver "Verificado ao vivo").

**Arquivos:**
- Create: `tests/IdentityGateway.Testing.Keycloak/TokensDeUsuario.cs`, `HarnessDeLogin.cs`, `ClienteDoMailpit.cs`, `UsuarioDeTeste.cs`, `SenhasDeTeste.cs`
- Modify: `tests/IdentityGateway.Testing.Keycloak/IdentityGateway.Testing.Keycloak.csproj` (`InternalsVisibleTo`)
- Modify: `tests/IdentityGateway.Testing.Keycloak/KeycloakFixture.cs`
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/HarnessDeLoginTests.cs`
- Modify: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakRealTests.cs` (`UsuarioComum_NaoAlteraOTenantIdPelaAccountApi`)

**Interfaces:**
- Consome: `FalhaDoHarnessException`, `FamiliaDeFalha` (Tarefa 1); `PayloadDoJwt` (Tarefa 3); o client `identity-gateway-demo` do realm (Tarefa 2); `HandlerFalso` (existente no projeto de integração: `new HandlerFalso(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)`, com `Chamadas`).
- Produz (namespace `IdentityGateway.Testing.Keycloak`, `public`):
  - `sealed record TokensDeUsuario(string AccessToken, string RefreshToken, TimeSpan ExpiraEm)` — `ToString` sem os tokens.
  - `sealed class HarnessDeLogin : IDisposable`
    - `HarnessDeLogin(Uri enderecoPublico, Uri enderecoDeTransporte, string clientId, Action<string>? log = null, string realm = "identity-gateway")`
    - `internal HarnessDeLogin(Uri enderecoPublico, Uri enderecoDeTransporte, string clientId, HttpMessageHandler transporte, Func<TimeSpan, CancellationToken, Task> esperar, Action<string>? log = null, string realm = "identity-gateway")`
    - `Task ConcluirLinkDeAcoesAsync(Uri link, string novaSenha, CancellationToken cancellationToken)`
    - `Task<bool> LinkDeAcoesAbreAsync(Uri link, CancellationToken cancellationToken)` — abre o link sem concluí-lo; verdadeiro se o Keycloak mostrou a página de ações
    - `Task<TokensDeUsuario> TokenPorDispositivoAsync(string usuario, string senha, CancellationToken cancellationToken)`
    - `Task<TokensDeUsuario> RenovarAsync(string refreshToken, CancellationToken cancellationToken)`
    - `int PassosDoUltimoLogin { get; }` — quantas vezes o formulário de login foi enviado (0 com sessão viva, 1 sem Organization no realm, 2 com).
  - `sealed class ClienteDoMailpit : IDisposable` — `ClienteDoMailpit(Uri baseUrl)`; `MensagensParaAsync(string destinatario, CancellationToken)`, `EsperarMensagensAsync(string destinatario, int quantidade, CancellationToken)` (devolvem `Task<IReadOnlyList<string>>`, ids da mais nova para a mais antiga); `TextoDaMensagemAsync(string id, CancellationToken)`; `LinkDeAcoesAsync(string destinatario, Uri enderecoPublico, string realm, CancellationToken)` (devolve `Task<Uri>`).
  - `sealed record UsuarioDeTeste(string Id, string Email, string Senha)` — `ToString` sem a senha.
  - `static class SenhasDeTeste { public static string Gerar(); }`
  - Em `KeycloakFixture`: `const string ClientDeDemonstracao = "identity-gateway-demo"`; `const string ClientDeConta = "fixture-conta-dispositivo"` (client de device flow criado em runtime, sem scopes declarados); `ClienteDoMailpit Mailpit { get; }`; `HarnessDeLogin CriarHarness(string clientId = ClientDeDemonstracao, Action<string>? log = null)`; `Task<UsuarioDeTeste> NovoUsuarioAsync(IReadOnlyList<string> papeis, string? tenantId, CancellationToken)`; `Task<UsuarioDeTeste> NovoPlatformAdminAsync(CancellationToken)`; `Task AtribuirPapelDeRealmComoMasterAsync(string usuarioId, string papel, CancellationToken)`; `Task EnviarEmailDeAcoesComoMasterAsync(string usuarioId, CancellationToken)`. **Sai** `TokenDeUsuarioComumAsync`.

- [ ] **Passo 1: Abrir os internos da biblioteca aos testes dela**

Em `tests/IdentityGateway.Testing.Keycloak/IdentityGateway.Testing.Keycloak.csproj`, antes de `</Project>`:

```xml
  <!--
    O harness tem um construtor interno que recebe o transporte e a espera, para ser testado sem Keycloak e sem
    esperar os 5 s do device flow. Quem o testa é o projeto de integração.
  -->
  <ItemGroup>
    <InternalsVisibleTo Include="IdentityGateway.Infrastructure.IntegrationTests" />
  </ItemGroup>

```

- [ ] **Passo 2: Escrever os testes do harness (sem Keycloak)**

`tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/HarnessDeLoginTests.cs`:

```csharp
using System.Net;
using System.Text;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O que o harness faz com cada resposta, sem Keycloak: transporte e espera são injetados.
/// </summary>
/// <remarks>
/// O comportamento contra o Keycloak real está em <c>HarnessContraKeycloakTests</c>. Aqui ficam os ramos que o
/// Keycloak real não produz por encomenda — consentimento negado, código expirado, <c>slow_down</c>, página
/// desconhecida — e as duas garantias que nenhum teste de integração enxerga: nada de segredo na exceção, e nenhuma
/// renovação repetida.
/// </remarks>
public sealed class HarnessDeLoginTests
{
    private const string Publico = "http://keycloak.test:8081";

    private const string PaginaFinal =
        "<html><head><title>Sign in to identity-gateway</title></head><body><div id=\"kc-info-message\">ok</div></body></html>";

    private const string PedidoDeDispositivo =
        """
        {"device_code":"CODIGO-DO-DISPOSITIVO","user_code":"ABCD-EFGH",
         "verification_uri":"http://keycloak.test:8081/realms/identity-gateway/device",
         "verification_uri_complete":"http://keycloak.test:8081/realms/identity-gateway/device?user_code=ABCD-EFGH",
         "expires_in":300,"interval":5}
        """;

    private const string TokensEmitidos =
        """{"access_token":"ACCESS-SECRETO","refresh_token":"REFRESH-SECRETO","expires_in":300}""";

    private static HarnessDeLogin Criar(HandlerFalso transporte, List<TimeSpan>? esperas = null) => new(
        new Uri(Publico),
        new Uri("http://127.0.0.1:18081"),
        "identity-gateway-demo",
        transporte,
        (duracao, _) =>
        {
            esperas?.Add(duracao);
            return Task.CompletedTask;
        });

    private static HttpResponseMessage Json(HttpStatusCode status, string corpo) =>
        new(status) { Content = new StringContent(corpo, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Html(string corpo) =>
        new(HttpStatusCode.OK) { Content = new StringContent(corpo, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Redirecionar(string destino, string? cookie = null)
    {
        HttpResponseMessage resposta = new(HttpStatusCode.Found);
        resposta.Headers.Location = new Uri(destino);

        if (cookie is not null)
        {
            resposta.Headers.TryAddWithoutValidation("Set-Cookie", cookie);
        }

        return resposta;
    }

    private static bool EhOTokenEndpoint(HttpRequestMessage pedido) =>
        pedido.RequestUri!.AbsolutePath.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal);

    private static bool EhOPedidoDeDispositivo(HttpRequestMessage pedido) =>
        pedido.RequestUri!.AbsolutePath.EndsWith("/auth/device", StringComparison.Ordinal);

    /// <summary>Um device flow cuja autorização já foi concedida: só o token endpoint varia.</summary>
    private static HandlerFalso DeviceFlowCom(Func<int, HttpResponseMessage> tokenNaTentativa)
    {
        int tentativas = 0;

        return new HandlerFalso((pedido, _) =>
        {
            if (EhOPedidoDeDispositivo(pedido))
            {
                return Task.FromResult(Json(HttpStatusCode.OK, PedidoDeDispositivo));
            }

            return Task.FromResult(EhOTokenEndpoint(pedido)
                ? tokenNaTentativa(Interlocked.Increment(ref tentativas))
                : Html(PaginaFinal));
        });
    }

    [Fact]
    public async Task PaginaDesconhecida_LancaComTituloFormularioENomesDosCamposSemValores()
    {
        // O que vai para o log da CI quando o Keycloak mostra uma página que o harness não conhece: o bastante para
        // diagnosticar (título, id do formulário, NOMES dos campos) e nada que seja segredo.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HandlerFalso transporte = new((_, _) => Task.FromResult(Html(
            """
            <html><head><title>Página estranha</title></head><body>
            <form id="kc-otp-login-form" action="http://keycloak.test:8081/realms/identity-gateway/login-actions/x">
              <input type="hidden" name="credentialId" value="VALOR-SECRETO">
              <input type="text" name="otp">
            </form></body></html>
            """)));
        using HarnessDeLogin harness = Criar(transporte);

        Func<Task> concluir = () => harness.ConcluirLinkDeAcoesAsync(
            new Uri($"{Publico}/realms/identity-gateway/login-actions/action-token?key=CHAVE-SECRETA"),
            "SENHA-SECRETA",
            ct);

        FalhaDoHarnessException falha = (await concluir.Should().ThrowAsync<FalhaDoHarnessException>()).Which;
        falha.Familia.Should().Be(FamiliaDeFalha.Formulario);
        falha.Etapa.Should().Be("link de ações");
        falha.Message.Should().Contain("Página estranha").And.Contain("kc-otp-login-form")
            .And.Contain("credentialId").And.Contain("otp");
        falha.Message.Should().NotContain("VALOR-SECRETO").And.NotContain("CHAVE-SECRETA")
            .And.NotContain("SENHA-SECRETA").And.NotContain("<form");
    }

    [Fact]
    public async Task PaginaDeErroDoKeycloak_LancaNaHora()
    {
        // Link já usado ou vencido: o Keycloak responde com kc-error-message. Sem este ramo, a página viraria
        // "desconhecida" — ou, pior, um laço até o limite de páginas.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HandlerFalso transporte = new((_, _) => Task.FromResult(Html(
            "<html><head><title>Sign in to identity-gateway</title></head><body>"
            + "<p id=\"kc-error-message\">Action expired.</p></body></html>")));
        using HarnessDeLogin harness = Criar(transporte);

        Func<Task> concluir = () => harness.ConcluirLinkDeAcoesAsync(
            new Uri($"{Publico}/realms/identity-gateway/login-actions/action-token?key=x"), "s", ct);

        (await concluir.Should().ThrowAsync<FalhaDoHarnessException>())
            .Which.Message.Should().Contain("página de erro");
        transporte.Chamadas.Should().Be(1);
    }

    [Fact]
    public async Task LoginRecusado_FalhaNaPrimeiraVoltaDoFormularioSemInsistir()
    {
        // Senha errada: o Keycloak devolve o mesmo formulário. O realm tem proteção contra força bruta — um harness que
        // reenviasse até o limite de páginas bloquearia a conta que está testando. (Visto ao vivo: sem este ramo, doze
        // envios seguidos.)
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HandlerFalso transporte = new((pedido, _) => Task.FromResult(EhOPedidoDeDispositivo(pedido)
            ? Json(HttpStatusCode.OK, PedidoDeDispositivo)
            : Html(
                """
                <html><head><title>Sign in to identity-gateway</title></head><body>
                <form id="kc-form-login" action="http://keycloak.test:8081/realms/identity-gateway/login-actions/authenticate?x=1" method="post">
                  <input type="text" name="username"><input type="password" name="password">
                  <input type="hidden" name="credentialId"><button name="login" type="submit">Sign In</button>
                </form></body></html>
                """)));
        using HarnessDeLogin harness = Criar(transporte);

        Func<Task> entrar = () => harness.TokenPorDispositivoAsync("alguem@acme.test", "SENHA-SECRETA", ct);

        FalhaDoHarnessException falha = (await entrar.Should().ThrowAsync<FalhaDoHarnessException>()).Which;
        falha.Familia.Should().Be(FamiliaDeFalha.Formulario);
        falha.Message.Should().Contain("recusados").And.NotContain("SENHA-SECRETA");
        harness.PassosDoUltimoLogin.Should().Be(1);

        // O pedido de dispositivo, a página de verificação e UM envio do formulário.
        transporte.Chamadas.Should().Be(3);
    }

    [Theory]
    [InlineData("access_denied")]
    [InlineData("expired_token")]
    public async Task ErroDefinitivoNoToken_FalhaNaHoraSemRepetir(string erro)
    {
        // Foco de revisão 2: quem nega o consentimento ou deixa o código expirar não pode ficar esperando o prazo
        // inteiro, e repetir o pedido não conserta nenhum dos dois.
        CancellationToken ct = TestContext.Current.CancellationToken;
        int pedidosDeToken = 0;
        List<TimeSpan> esperas = [];
        using HandlerFalso transporte = DeviceFlowCom(tentativa =>
        {
            // Só a primeira resposta é o erro: se o harness insistisse, a segunda entregaria os tokens, e o teste
            // falharia por não ver a exceção — em vez de girar até o prazo.
            pedidosDeToken = tentativa;
            return tentativa == 1
                ? Json(HttpStatusCode.BadRequest, $$"""{"error":"{{erro}}"}""")
                : Json(HttpStatusCode.OK, TokensEmitidos);
        });
        using HarnessDeLogin harness = Criar(transporte, esperas);

        Func<Task> entrar = () => harness.TokenPorDispositivoAsync("alguem@acme.test", "SENHA-SECRETA", ct);

        FalhaDoHarnessException falha = (await entrar.Should().ThrowAsync<FalhaDoHarnessException>()).Which;
        falha.Familia.Should().Be(FamiliaDeFalha.DeviceFlow);
        falha.Message.Should().Contain(erro).And.NotContain("CODIGO-DO-DISPOSITIVO").And.NotContain("ABCD-EFGH");
        pedidosDeToken.Should().Be(1);
        esperas.Should().Equal(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConsentimentoNegado_FalhaNaHora()
    {
        // O nome que o Foco de revisão cita; o caso é o access_denied da theory acima, afirmado pelo nome do erro.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HandlerFalso transporte = DeviceFlowCom(tentativa => tentativa == 1
            ? Json(HttpStatusCode.BadRequest, """{"error":"access_denied"}""")
            : Json(HttpStatusCode.OK, TokensEmitidos));
        using HarnessDeLogin harness = Criar(transporte);

        Func<Task> entrar = () => harness.TokenPorDispositivoAsync("alguem@acme.test", "s", ct);

        (await entrar.Should().ThrowAsync<FalhaDoHarnessException>()).Which.Message.Should().Contain("access_denied");
    }

    [Fact]
    public async Task DeviceCodeExpirado_FalhaNaHora()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HandlerFalso transporte = DeviceFlowCom(tentativa => tentativa == 1
            ? Json(HttpStatusCode.BadRequest, """{"error":"expired_token"}""")
            : Json(HttpStatusCode.OK, TokensEmitidos));
        using HarnessDeLogin harness = Criar(transporte);

        Func<Task> entrar = () => harness.TokenPorDispositivoAsync("alguem@acme.test", "s", ct);

        (await entrar.Should().ThrowAsync<FalhaDoHarnessException>()).Which.Message.Should().Contain("expired_token");
    }

    [Fact]
    public async Task SlowDown_SomaCincoSegundosAoIntervaloESegue()
    {
        // O Keycloak responde slow_down a um poll antes do intervalo, mesmo com a autorização concedida, e não aumenta
        // o intervalo sozinho: a RFC 8628 manda o client somar 5 s.
        CancellationToken ct = TestContext.Current.CancellationToken;
        List<TimeSpan> esperas = [];
        using HandlerFalso transporte = DeviceFlowCom(tentativa => tentativa switch
        {
            1 => Json(HttpStatusCode.BadRequest, """{"error":"slow_down"}"""),
            2 => Json(HttpStatusCode.BadRequest, """{"error":"authorization_pending"}"""),
            _ => Json(HttpStatusCode.OK, TokensEmitidos),
        });
        using HarnessDeLogin harness = Criar(transporte, esperas);

        TokensDeUsuario tokens = await harness.TokenPorDispositivoAsync("alguem@acme.test", "s", ct);

        tokens.AccessToken.Should().Be("ACCESS-SECRETO");
        tokens.RefreshToken.Should().Be("REFRESH-SECRETO");
        esperas.Should().Equal(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Renovacao_Recusada_LancaSemRepetir()
    {
        // Com a rotação ligada, repetir uma renovação reusa o refresh token — e o reuso derruba a sessão inteira do
        // client. O harness não tem retry: uma chamada, e a falha diz que o caminho é um device flow novo.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HandlerFalso transporte = new((_, _) => Task.FromResult(
            Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""")));
        using HarnessDeLogin harness = Criar(transporte);

        Func<Task> renovar = () => harness.RenovarAsync("REFRESH-SECRETO", ct);

        FalhaDoHarnessException falha = (await renovar.Should().ThrowAsync<FalhaDoHarnessException>()).Which;
        falha.Message.Should().Contain("invalid_grant").And.Contain("device flow novo").And.NotContain("REFRESH-SECRETO");
        transporte.Chamadas.Should().Be(1);
    }

    [Fact]
    public async Task Cookies_SecureVoltaPorHttpEVencidoSome()
    {
        // O Keycloak marca os cookies como Secure; o CookieContainer não os devolveria por http, e o login se perderia
        // entre uma página e a seguinte. E um cookie apagado pelo servidor (Max-Age=0) não pode voltar.
        CancellationToken ct = TestContext.Current.CancellationToken;
        List<string?> cookiesRecebidos = [];
        using HandlerFalso transporte = new((pedido, _) =>
        {
            cookiesRecebidos.Add(pedido.Headers.TryGetValues("Cookie", out IEnumerable<string>? valores)
                ? string.Join("; ", valores)
                : null);

            return Task.FromResult(cookiesRecebidos.Count switch
            {
                1 => Redirecionar(
                    $"{Publico}/realms/identity-gateway/passo-2",
                    "AUTH_SESSION_ID=abc; Path=/realms/identity-gateway/; Secure; HttpOnly"),
                2 => Redirecionar(
                    $"{Publico}/realms/identity-gateway/passo-3",
                    "AUTH_SESSION_ID=; Max-Age=0; Path=/realms/identity-gateway/; Secure; HttpOnly"),
                _ => Html(PaginaFinal),
            });
        });
        using HarnessDeLogin harness = Criar(transporte);

        await harness.ConcluirLinkDeAcoesAsync(new Uri($"{Publico}/realms/identity-gateway/passo-1"), "s", ct);

        cookiesRecebidos.Should().Equal(null, "AUTH_SESSION_ID=abc", null);
    }

    [Fact]
    public async Task EnderecoPublico_EDiscadoNoTransporteComOHostPublico()
    {
        // KC_HOSTNAME=keycloak.test:8081 não resolve na máquina do teste: o harness disca a porta mapeada e mantém o
        // Host que o Keycloak espera.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Uri? discado = null;
        string? host = null;
        using HandlerFalso transporte = new((pedido, _) =>
        {
            discado = pedido.RequestUri;
            host = pedido.Headers.Host;
            return Task.FromResult(Html(PaginaFinal));
        });
        using HarnessDeLogin harness = Criar(transporte);

        await harness.ConcluirLinkDeAcoesAsync(new Uri($"{Publico}/realms/identity-gateway/x?key=1&tab=2"), "s", ct);

        discado!.Authority.Should().Be("127.0.0.1:18081");
        discado.PathAndQuery.Should().Be("/realms/identity-gateway/x?key=1&tab=2");
        host.Should().Be("keycloak.test:8081");
    }

    [Fact]
    public void TokensDeUsuario_ToStringNaoRevelaOsTokens()
    {
        TokensDeUsuario tokens = new("ACCESS-SECRETO", "REFRESH-SECRETO", TimeSpan.FromMinutes(5));

        tokens.ToString().Should().NotContain("SECRETO");
    }
}
```

- [ ] **Passo 3: Rodar e ver falhar**

Run: `dotnet build IdentityGateway.slnx`
Expected: FAIL de compilação — `CS0246` para `HarnessDeLogin` e `TokensDeUsuario`. (Só o build: os tipos ainda não existem.)

- [ ] **Passo 4: `TokensDeUsuario` e `HarnessDeLogin`**

`tests/IdentityGateway.Testing.Keycloak/TokensDeUsuario.cs`:

```csharp
namespace IdentityGateway.Testing.Keycloak;

/// <summary>O par de tokens de um login. O <c>ToString</c> não os revela.</summary>
public sealed record TokensDeUsuario(string AccessToken, string RefreshToken, TimeSpan ExpiraEm)
{
    public override string ToString() => $"TokensDeUsuario(expira em {ExpiraEm.TotalSeconds:0}s)";
}
```

`tests/IdentityGateway.Testing.Keycloak/HarnessDeLogin.cs` (copie como está). O núcleo deste arquivo — o link de ações, o device flow, a renovação, a recusa do login devolvido — rodou contra o Keycloak 26.7.4 ao escrever o plano; `LinkDeAcoesAbreAsync` foi acrescentado depois, compilado, e é provado pelo teste da Tarefa 5. O que ele reconhece em cada página veio do tema padrão da 26.7.4: `kc-form-login`, `kc-passwd-update-form`, `kc-update-profile-form`, o formulário de consentimento sem id com o botão `accept`, e a página de informação `kc-info-message`:

```csharp
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IdentityGateway.Testing.Keycloak;

/// <summary>
/// Um navegador mínimo para o Keycloak: conclui o link de ações e faz login pelo Device Authorization Grant.
/// </summary>
/// <remarks>
/// <para>
/// <b>Uma instância é uma sessão de navegador</b>: o jar de cookies vive nela. Um segundo login na mesma instância
/// encontra o cookie de SSO e pula a página de login; para entrar como outro usuário, use outra instância.
/// </para>
/// <para>
/// <b>Só o device flow, e a senha só no formulário do Keycloak.</b> O harness submete a página de login com uma senha
/// que o próprio teste definiu; a credencial nunca passa pela Gateway, e por isso não é ROPC (ADR-003).
/// </para>
/// <para>
/// <b>Endereço público e de transporte.</b> O Keycloak escreve os links com o <c>KC_HOSTNAME</c>, que nos testes não
/// resolve (<c>keycloak.test:8081</c>). O harness disca o endereço de transporte e mantém o <c>Host</c> público.
/// </para>
/// <para>
/// <b>Nada do que ele registra ou lança carrega segredo</b>: nem token, nem senha, nem código de dispositivo, nem link,
/// nem HTML. Uma página desconhecida vira exceção com o título, o id do formulário e os NOMES dos campos.
/// </para>
/// </remarks>
public sealed partial class HarnessDeLogin : IDisposable
{
    private const int LimiteDePaginas = 12;
    private const int LimiteDeRedirecionamentos = 10;

    private readonly HttpClient _http;
    private readonly Dictionary<string, Biscoito> _cookies = new(StringComparer.Ordinal);
    private readonly Uri _publico;
    private readonly string _realm;
    private readonly string _clientId;
    private readonly Action<string> _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _esperar;

    public HarnessDeLogin(
        Uri enderecoPublico, Uri enderecoDeTransporte, string clientId, Action<string>? log = null,
        string realm = "identity-gateway")
        : this(
            enderecoPublico,
            enderecoDeTransporte,
            clientId,
            new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false },
            Task.Delay,
            log,
            realm)
    {
    }

    /// <summary>Para os testes do próprio harness: o transporte e a espera são injetados.</summary>
    internal HarnessDeLogin(
        Uri enderecoPublico, Uri enderecoDeTransporte, string clientId, HttpMessageHandler transporte,
        Func<TimeSpan, CancellationToken, Task> esperar, Action<string>? log = null,
        string realm = "identity-gateway")
    {
        ArgumentNullException.ThrowIfNull(enderecoPublico);
        ArgumentNullException.ThrowIfNull(enderecoDeTransporte);

        _publico = enderecoPublico;
        _realm = realm;
        _clientId = clientId;
        _log = log ?? (_ => { });
        _esperar = esperar;
        _http = new HttpClient(new ReescritaDeAutoridade(enderecoPublico, enderecoDeTransporte, transporte))
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    /// <summary>Quantas vezes o formulário de login foi enviado no último login (1 ou 2; 0 com sessão viva).</summary>
    public int PassosDoUltimoLogin { get; private set; }

    public void Dispose() => _http.Dispose();

    /// <summary>Segue o link do e-mail de ações: define a senha e preenche o perfil.</summary>
    public async Task ConcluirLinkDeAcoesAsync(Uri link, string novaSenha, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);

        Pagina pagina = await NavegarAsync(HttpMethod.Get, link, corpo: null, cancellationToken);
        await PercorrerAsync("link de ações", pagina, usuario: null, novaSenha, cancellationToken);
    }

    /// <summary>
    /// Abre o link do e-mail de ações <b>sem concluí-lo</b>: verdadeiro se o Keycloak mostrou a página de ações.
    /// </summary>
    /// <remarks>
    /// A primeira página do link só informa o que será pedido e oferece o "prosseguir"; abri-la não consome o link. É
    /// como se confere que um convite chegou com um link que funciona, deixando-o para o convidado.
    /// </remarks>
    public async Task<bool> LinkDeAcoesAbreAsync(Uri link, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);

        Pagina pagina = await NavegarAsync(HttpMethod.Get, link, corpo: null, cancellationToken);

        return pagina.Status == 200 && pagina.TemInformacao && !pagina.TemErro;
    }

    /// <summary>Login pelo device flow, com o consentimento. Devolve o access token e o refresh token.</summary>
    public async Task<TokensDeUsuario> TokenPorDispositivoAsync(
        string usuario, string senha, CancellationToken cancellationToken)
    {
        const string etapa = "device flow";

        using JsonDocument pedido = await PostarFormularioAsync(
            etapa,
            Rota("protocol/openid-connect/auth/device"),
            [new("client_id", _clientId), new("scope", "openid")],
            aceitarErro: false,
            cancellationToken);

        string deviceCode = pedido.RootElement.GetProperty("device_code").GetString()!;
        Uri verificacao = new(pedido.RootElement.GetProperty("verification_uri_complete").GetString()!);
        int intervalo = pedido.RootElement.GetProperty("interval").GetInt32();
        DateTimeOffset prazo = DateTimeOffset.UtcNow.AddSeconds(pedido.RootElement.GetProperty("expires_in").GetInt32());

        // O login e o consentimento acontecem ANTES do primeiro poll: um poll antes do intervalo leva slow_down mesmo
        // com a autorização já concedida.
        PassosDoUltimoLogin = 0;
        Pagina pagina = await NavegarAsync(HttpMethod.Get, verificacao, corpo: null, cancellationToken);
        await PercorrerAsync(etapa, pagina, usuario, senha, cancellationToken);

        while (true)
        {
            await _esperar(TimeSpan.FromSeconds(intervalo), cancellationToken);

            if (DateTimeOffset.UtcNow > prazo)
            {
                throw new FalhaDoHarnessException(FamiliaDeFalha.Prazo, etapa, "o device code expirou antes do token.");
            }

            using JsonDocument resposta = await PostarFormularioAsync(
                etapa,
                Rota("protocol/openid-connect/token"),
                [
                    new("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                    new("client_id", _clientId),
                    new("device_code", deviceCode),
                ],
                aceitarErro: true,
                cancellationToken);

            if (resposta.RootElement.TryGetProperty("access_token", out _))
            {
                return LerTokens(resposta.RootElement);
            }

            string erro = resposta.RootElement.TryGetProperty("error", out JsonElement codigo)
                ? codigo.GetString() ?? "desconhecido"
                : "desconhecido";

            switch (erro)
            {
                case "authorization_pending":
                    break;
                case "slow_down":
                    intervalo += 5;
                    _log($"{etapa}: slow_down, intervalo agora {intervalo}s");
                    break;
                default:
                    // expired_token, access_denied e o resto: repetir não conserta.
                    throw new FalhaDoHarnessException(FamiliaDeFalha.DeviceFlow, etapa, $"o token endpoint respondeu {erro}.");
            }
        }
    }

    /// <summary>
    /// Renova pelo refresh token. Com a rotação ligada, o refresh token usado deixa de valer: quem chama guarda o novo
    /// antes de qualquer outro passo, e NUNCA repete a chamada — o reuso derruba a sessão inteira do client.
    /// </summary>
    public async Task<TokensDeUsuario> RenovarAsync(string refreshToken, CancellationToken cancellationToken)
    {
        const string etapa = "renovação";

        using JsonDocument resposta = await PostarFormularioAsync(
            etapa,
            Rota("protocol/openid-connect/token"),
            [new("grant_type", "refresh_token"), new("client_id", _clientId), new("refresh_token", refreshToken)],
            aceitarErro: true,
            cancellationToken);

        if (!resposta.RootElement.TryGetProperty("access_token", out _))
        {
            string erro = resposta.RootElement.TryGetProperty("error", out JsonElement codigo)
                ? codigo.GetString() ?? "desconhecido"
                : "desconhecido";

            throw new FalhaDoHarnessException(
                FamiliaDeFalha.DeviceFlow, etapa, $"o token endpoint respondeu {erro}; o caminho é um device flow novo.");
        }

        return LerTokens(resposta.RootElement);
    }

    private static TokensDeUsuario LerTokens(JsonElement raiz) => new(
        raiz.GetProperty("access_token").GetString()!,
        raiz.GetProperty("refresh_token").GetString()!,
        TimeSpan.FromSeconds(raiz.GetProperty("expires_in").GetInt32()));

    private Uri Rota(string caminho) => new($"{_publico.ToString().TrimEnd('/')}/realms/{_realm}/{caminho}");

    // ───────────────────────────── as páginas ─────────────────────────────

    private async Task PercorrerAsync(
        string etapa, Pagina pagina, string? usuario, string senha, CancellationToken cancellationToken)
    {
        bool usuarioEnviado = false;
        bool senhaEnviada = false;

        for (int passo = 0; passo < LimiteDePaginas; passo++)
        {
            _log($"{etapa}: página \"{pagina.Titulo}\" ({pagina.Descricao()})");

            if (pagina.TemErro)
            {
                throw Desconhecida(etapa, pagina, "o Keycloak mostrou a página de erro");
            }

            if (pagina.Formulario("kc-form-login") is { } login)
            {
                bool pedeSenha = login.Tem("password");

                // O mesmo formulário de volta quer dizer credencial recusada. Insistir não conserta, e cada envio
                // conta como tentativa para a proteção contra força bruta do realm, que bloquearia a conta.
                if (pedeSenha ? senhaEnviada : usuarioEnviado)
                {
                    throw new FalhaDoHarnessException(
                        FamiliaDeFalha.Formulario, etapa,
                        "o Keycloak devolveu o formulário de login: usuário ou senha recusados.");
                }

                Dictionary<string, string> campos = login.Ocultos();

                if (login.Tem("username") && !login.EhOculto("username"))
                {
                    campos["username"] = usuario
                        ?? throw Desconhecida(etapa, pagina, "o fluxo pediu login, e nenhum usuário foi informado");
                }

                if (pedeSenha)
                {
                    campos["password"] = senha;
                }

                usuarioEnviado = true;
                senhaEnviada |= pedeSenha;
                PassosDoUltimoLogin++;
                pagina = await NavegarAsync(HttpMethod.Post, login.Acao, campos, cancellationToken);
            }
            else if (pagina.Formulario("kc-passwd-update-form") is { } novaSenha)
            {
                Dictionary<string, string> campos = novaSenha.Ocultos();
                campos["password-new"] = senha;
                campos["password-confirm"] = senha;
                pagina = await NavegarAsync(HttpMethod.Post, novaSenha.Acao, campos, cancellationToken);
            }
            else if (pagina.Formulario("kc-update-profile-form") is { } perfil)
            {
                Dictionary<string, string> campos = perfil.Valores();
                campos["firstName"] = "Teste";
                campos["lastName"] = "Harness";
                pagina = await NavegarAsync(HttpMethod.Post, perfil.Acao, campos, cancellationToken);
            }
            else if (pagina.FormularioCom("accept") is { } consentimento)
            {
                Dictionary<string, string> campos = consentimento.Ocultos();
                campos["accept"] = "Yes";
                pagina = await NavegarAsync(HttpMethod.Post, consentimento.Acao, campos, cancellationToken);
            }
            else if (pagina.LinkDeProsseguir is { } prosseguir)
            {
                pagina = await NavegarAsync(HttpMethod.Get, prosseguir, corpo: null, cancellationToken);
            }
            else if (pagina.TemInformacao)
            {
                // A página final: "Your account has been updated" ou "Device Login Successful".
                return;
            }
            else
            {
                throw Desconhecida(etapa, pagina, "página que o harness não conhece");
            }
        }

        throw new FalhaDoHarnessException(FamiliaDeFalha.Formulario, etapa, $"mais de {LimiteDePaginas} páginas sem chegar ao fim.");
    }

    private static FalhaDoHarnessException Desconhecida(string etapa, Pagina pagina, string motivo) =>
        new(FamiliaDeFalha.Formulario, etapa, $"{motivo}. Título: \"{pagina.Titulo}\"; {pagina.Descricao()}.");

    // ───────────────────────────── o transporte ─────────────────────────────

    private async Task<Pagina> NavegarAsync(
        HttpMethod metodo, Uri destino, Dictionary<string, string>? corpo, CancellationToken cancellationToken)
    {
        for (int salto = 0; salto < LimiteDeRedirecionamentos; salto++)
        {
            using HttpRequestMessage pedido = new(metodo, destino);

            if (corpo is not null)
            {
                pedido.Content = new FormUrlEncodedContent(corpo);
            }

            string cabecalho = CookiesPara(destino);

            if (cabecalho.Length > 0)
            {
                pedido.Headers.TryAddWithoutValidation("Cookie", cabecalho);
            }

            using HttpResponseMessage resposta = await _http.SendAsync(pedido, cancellationToken);
            GuardarCookies(resposta);

            if ((int)resposta.StatusCode is >= 300 and < 400 && resposta.Headers.Location is { } proximo)
            {
                destino = proximo.IsAbsoluteUri ? proximo : new Uri(destino, proximo);
                metodo = HttpMethod.Get;
                corpo = null;
                continue;
            }

            string html = await resposta.Content.ReadAsStringAsync(cancellationToken);
            return new Pagina(destino, (int)resposta.StatusCode, html);
        }

        throw new FalhaDoHarnessException(
            FamiliaDeFalha.Formulario, "navegação", $"mais de {LimiteDeRedirecionamentos} redirecionamentos.");
    }

    private async Task<JsonDocument> PostarFormularioAsync(
        string etapa, Uri destino, KeyValuePair<string, string>[] campos, bool aceitarErro,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage pedido = new(HttpMethod.Post, destino) { Content = new FormUrlEncodedContent(campos) };
        using HttpResponseMessage resposta = await _http.SendAsync(pedido, cancellationToken);
        string corpo = await resposta.Content.ReadAsStringAsync(cancellationToken);

        if (!resposta.IsSuccessStatusCode && !aceitarErro)
        {
            throw new FalhaDoHarnessException(
                FamiliaDeFalha.DeviceFlow, etapa, $"{destino.AbsolutePath} respondeu {(int)resposta.StatusCode}.");
        }

        try
        {
            return JsonDocument.Parse(corpo);
        }
        catch (JsonException)
        {
            throw new FalhaDoHarnessException(
                FamiliaDeFalha.DeviceFlow, etapa,
                $"{destino.AbsolutePath} respondeu {(int)resposta.StatusCode} com um corpo que não é JSON.");
        }
    }

    // ───────────────────────────── os cookies ─────────────────────────────

    // Jar manual: o Keycloak marca os cookies como Secure, e o CookieContainer não os devolveria por http.
    private sealed record Biscoito(string Nome, string Valor, string Caminho);

    private void GuardarCookies(HttpResponseMessage resposta)
    {
        if (!resposta.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cabecalhos))
        {
            return;
        }

        foreach (string cabecalho in cabecalhos)
        {
            string[] partes = cabecalho.Split(';', StringSplitOptions.TrimEntries);
            int igual = partes[0].IndexOf('=', StringComparison.Ordinal);

            if (igual <= 0)
            {
                continue;
            }

            string nome = partes[0][..igual];
            string valor = partes[0][(igual + 1)..];
            string caminho = "/";
            bool vencido = valor.Length == 0;

            foreach (string atributo in partes.Skip(1))
            {
                if (atributo.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
                {
                    caminho = atributo[5..];
                }
                else if (atributo.StartsWith("Max-Age=", StringComparison.OrdinalIgnoreCase))
                {
                    vencido |= int.TryParse(atributo[8..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int idade)
                               && idade <= 0;
                }
                else if (atributo.StartsWith("Expires=", StringComparison.OrdinalIgnoreCase))
                {
                    vencido |= DateTimeOffset.TryParse(
                                   atributo[8..], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
                                   out DateTimeOffset quando)
                               && quando <= DateTimeOffset.UtcNow;
                }
            }

            string chave = $"{nome}|{caminho}";

            if (vencido)
            {
                _cookies.Remove(chave);
            }
            else
            {
                _cookies[chave] = new Biscoito(nome, valor, caminho);
            }
        }
    }

    private string CookiesPara(Uri destino) => string.Join(
        "; ",
        _cookies.Values
            .Where(cookie => destino.AbsolutePath.StartsWith(cookie.Caminho, StringComparison.Ordinal))
            .Select(cookie => $"{cookie.Nome}={cookie.Valor}"));

    /// <summary>Troca o endereço público pelo de transporte, mantendo o <c>Host</c> público.</summary>
    private sealed class ReescritaDeAutoridade(Uri publico, Uri transporte, HttpMessageHandler interno)
        : DelegatingHandler(interno)
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri original = request.RequestUri!;

            if (string.Equals(original.Authority, publico.Authority, StringComparison.OrdinalIgnoreCase))
            {
                request.RequestUri = new UriBuilder(original)
                {
                    Scheme = transporte.Scheme,
                    Host = transporte.Host,
                    Port = transporte.Port,
                }.Uri;
                request.Headers.Host = publico.Authority;
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    // ───────────────────────────── o HTML ─────────────────────────────

    private sealed partial class Pagina(Uri endereco, int status, string html)
    {
        private readonly List<Formulario> _formularios =
            [.. Formularios().Matches(html).Select(achado => new Formulario(endereco, achado.Value))];

        public int Status { get; } = status;

        public string Titulo { get; } = Titulos().Match(html) is { Success: true } titulo
            ? WebUtility.HtmlDecode(titulo.Groups[1].Value).Trim()
            : string.Empty;

        public bool TemErro { get; } = html.Contains("id=\"kc-error-message\"", StringComparison.Ordinal);

        public bool TemInformacao { get; } = html.Contains("id=\"kc-info-message\"", StringComparison.Ordinal);

        /// <summary>O link "clique para prosseguir" da página de informação — só o que continua o fluxo de login.</summary>
        public Uri? LinkDeProsseguir { get; } = Links().Matches(html)
            .Select(achado => WebUtility.HtmlDecode(achado.Groups[1].Value))
            .Where(href => href.Contains("/login-actions/", StringComparison.Ordinal))
            .Select(href => Uri.TryCreate(href, UriKind.Absolute, out Uri? absoluto) ? absoluto : new Uri(endereco, href))
            .FirstOrDefault();

        public Formulario? Formulario(string id) => _formularios.FirstOrDefault(formulario => formulario.Id == id);

        public Formulario? FormularioCom(string campo) => _formularios.FirstOrDefault(formulario => formulario.Tem(campo));

        /// <summary>Só nomes: id dos formulários e nomes dos campos, nunca valores nem HTML.</summary>
        public string Descricao() => _formularios.Count == 0
            ? $"HTTP {Status}, sem formulário"
            : $"HTTP {Status}, " + string.Join(
                "; ", _formularios.Select(f => $"formulário \"{f.Id}\" com os campos [{string.Join(", ", f.Nomes)}]"));

        [GeneratedRegex("<form\\b.*?</form>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex Formularios();

        [GeneratedRegex("<title>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex Titulos();

        [GeneratedRegex("<a\\b[^>]*\\bhref=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
        private static partial Regex Links();
    }

    private sealed partial class Formulario
    {
        private readonly List<(string Nome, string Tipo, string Valor)> _campos;

        public Formulario(Uri pagina, string html)
        {
            string abertura = Abertura().Match(html).Value;
            Id = Atributo(abertura, "id");

            string acao = WebUtility.HtmlDecode(Atributo(abertura, "action"));
            Acao = Uri.TryCreate(acao, UriKind.Absolute, out Uri? absoluta) ? absoluta : new Uri(pagina, acao);

            _campos =
            [
                .. Campos().Matches(html)
                    .Select(achado => achado.Value)
                    .Where(campo => Atributo(campo, "name").Length > 0)
                    .Select(campo => (
                        Atributo(campo, "name"),
                        Atributo(campo, "type").ToLowerInvariant(),
                        WebUtility.HtmlDecode(Atributo(campo, "value")))),
            ];
        }

        public string Id { get; }

        public Uri Acao { get; }

        public IEnumerable<string> Nomes => _campos.Select(campo => campo.Nome).Distinct(StringComparer.Ordinal);

        public bool Tem(string nome) => _campos.Any(campo => campo.Nome == nome);

        public bool EhOculto(string nome) => _campos.Any(campo => campo.Nome == nome && campo.Tipo == "hidden");

        /// <summary>Os campos ocultos, com o valor que a página trouxe.</summary>
        public Dictionary<string, string> Ocultos() => _campos
            .Where(campo => campo.Tipo == "hidden")
            .GroupBy(campo => campo.Nome, StringComparer.Ordinal)
            .ToDictionary(grupo => grupo.Key, grupo => grupo.First().Valor, StringComparer.Ordinal);

        /// <summary>Todos os campos de valor (sem os botões), com o que a página trouxe.</summary>
        public Dictionary<string, string> Valores() => _campos
            .Where(campo => campo.Tipo is not ("submit" or "button"))
            .GroupBy(campo => campo.Nome, StringComparer.Ordinal)
            .ToDictionary(grupo => grupo.Key, grupo => grupo.First().Valor, StringComparer.Ordinal);

        private static string Atributo(string elemento, string nome)
        {
            Match achado = Regex.Match(
                elemento, $"\\b{Regex.Escape(nome)}\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);

            return achado.Success ? achado.Groups[1].Value : string.Empty;
        }

        [GeneratedRegex("<form\\b[^>]*>", RegexOptions.IgnoreCase)]
        private static partial Regex Abertura();

        [GeneratedRegex("<(?:input|button)\\b[^>]*>", RegexOptions.IgnoreCase)]
        private static partial Regex Campos();
    }
}
```

Quatro decisões do código acima que não são óbvias, para quem for mexer:
- **O formulário de login de volta é falha imediata.** Senha errada devolve o mesmo `kc-form-login`. O realm tem `bruteForceProtected`: reenviar até o limite de páginas bloquearia a conta. O harness envia a senha uma vez; se o formulário com senha volta, lança.
- **O link "prosseguir" só é seguido se apontar para `/login-actions/`.** A página final do link de ações também traz um link ("Back to Application", para a account console); segui-lo levaria o harness a uma página de login que ele preencheria sem usuário.
- **`Ocultos()` no login e `Valores()` no perfil.** O formulário de login manda os campos ocultos (`credentialId`) e o que o harness preenche; o de perfil manda de volta o que a página trouxe (o `email` já preenchido), mais nome e sobrenome.
- **A espera vem antes de cada poll, inclusive do primeiro.** O device flow leva pelo menos um `interval` (5 s): um poll imediato leva `slow_down`.

- [ ] **Passo 5: Rodar os testes do harness**

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter-class "*HarnessDeLoginTests"`
Expected: PASS — `total: 12` (dez `[Fact]` e a `[Theory]` com dois casos). Nenhum deles sobe container, mas o `KeycloakFixture` é do assembly e sobe do mesmo jeito: com o Docker desligado, o que falha é o fixture, não o teste.

- [ ] **Passo 6: Cliente do mailpit, usuário e senha de teste**

`tests/IdentityGateway.Testing.Keycloak/ClienteDoMailpit.cs`:

```csharp
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
```

`tests/IdentityGateway.Testing.Keycloak/UsuarioDeTeste.cs`:

```csharp
namespace IdentityGateway.Testing.Keycloak;

/// <summary>Um usuário criado para um teste, com a senha que o teste definiu pelo link de ações.</summary>
/// <param name="Id">O <c>sub</c> no Keycloak.</param>
public sealed record UsuarioDeTeste(string Id, string Email, string Senha)
{
    /// <summary>Sem a senha e sem o e-mail: o <c>ToString</c> do record imprimiria os dois em qualquer log.</summary>
    public override string ToString() => $"UsuarioDeTeste({Id})";
}
```

`tests/IdentityGateway.Testing.Keycloak/SenhasDeTeste.cs`:

```csharp
using System.Security.Cryptography;

namespace IdentityGateway.Testing.Keycloak;

/// <summary>Senhas descartáveis, geradas em memória: nenhuma senha de teste fica escrita no repositório.</summary>
public static class SenhasDeTeste
{
    /// <summary>32 caracteres hexadecimais aleatórios, com um prefixo que satisfaz políticas de composição.</summary>
    public static string Gerar() => $"Aa1!{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}";
}
```

- [ ] **Passo 7: O fixture ganha o harness e perde o ROPC**

Em `tests/IdentityGateway.Testing.Keycloak/KeycloakFixture.cs`:

1. Campos e constantes novos, junto dos existentes:

```csharp
    /// <summary>O client público de demonstração do realm: só device flow, com os scopes da Gateway.</summary>
    public const string ClientDeDemonstracao = "identity-gateway-demo";

    /// <summary>
    /// Client público criado pelo fixture, em runtime, só com device flow, <b>herdando os scopes default do realm</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Existe para o teste da Account REST API, que exige <c>manage-account</c> e <c>aud=account</c> (do scope
    /// <c>roles</c>, um default do realm) — o que o client de demonstração não emite. Nunca é declarado no JSON.
    /// </para>
    /// <para>
    /// <b>Sem <c>defaultClientScopes</c> de propósito:</b> ele recebe exatamente o que um client qualquer criado pela
    /// Admin API receberia. O token dele NÃO traz a audiência da Gateway — e é isso que prova, no realm vivo, que
    /// <c>gateway-api</c> não é scope default. Com os scopes listados aqui, a prova seria vacuosa.
    /// </para>
    /// </remarks>
    public const string ClientDeConta = "fixture-conta-dispositivo";

    private static readonly string[] AcoesDoConvite = ["UPDATE_PASSWORD", "VERIFY_EMAIL"];

    private static readonly string[] SoPlatformAdmin = ["platform-admin"];

    private ClienteDoMailpit? _mailpitCliente;
```

2. Depois de `MailpitUrl`:

```csharp
    /// <summary>O cliente da API do mailpit deste fixture.</summary>
    public ClienteDoMailpit Mailpit => _mailpitCliente ??= new ClienteDoMailpit(new Uri(MailpitUrl));
```

3. `InitializeAsync` e `DisposeAsync` passam a ser:

```csharp
    public async ValueTask InitializeAsync()
    {
        await _rede.CreateAsync();
        await Task.WhenAll(_mailpit.StartAsync(), _container.StartAsync());
        await CriarClientDeContaAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _mailpitCliente?.Dispose();
        await _container.DisposeAsync();
        await _mailpit.DisposeAsync();
        await _rede.DisposeAsync();
    }
```

4. Apagar `TokenDeUsuarioComumAsync` inteiro (com o comentário XML) e pôr no lugar:

```csharp
    /// <summary>Um harness de login (uma sessão de navegador nova) apontado para este Keycloak.</summary>
    public HarnessDeLogin CriarHarness(string clientId = ClientDeDemonstracao, Action<string>? log = null) =>
        new(new Uri(HostnamePublico), new Uri(BaseUrl), clientId, log, Realm);

    /// <summary>
    /// Cria um usuário pelo master, com os papéis e o tenant pedidos, e conclui o convite dele pelo link do e-mail.
    /// </summary>
    /// <remarks>
    /// O caminho é o do convite real: o usuário nasce sem senha, com as duas ações obrigatórias; o Keycloak manda o
    /// e-mail; o harness segue o link e define uma senha gerada. Nenhuma senha é atribuída pela Admin API.
    /// </remarks>
    public async Task<UsuarioDeTeste> NovoUsuarioAsync(
        IReadOnlyList<string> papeis, string? tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(papeis);

        string email = EmailUnico();
        string senha = SenhasDeTeste.Gerar();

        Dictionary<string, object> usuario = new()
        {
            ["username"] = email,
            ["email"] = email,
            ["enabled"] = true,
            ["emailVerified"] = false,
            ["requiredActions"] = AcoesDoConvite,
        };

        if (tenantId is not null)
        {
            usuario["attributes"] = new Dictionary<string, string[]> { ["tenant_id"] = [tenantId] };
        }

        string id = await CriarUsuarioComoMasterAsync(usuario, cancellationToken);

        foreach (string papel in papeis)
        {
            await AtribuirPapelDeRealmComoMasterAsync(id, papel, cancellationToken);
        }

        await EnviarEmailDeAcoesComoMasterAsync(id, cancellationToken);

        using HarnessDeLogin harness = CriarHarness();
        await harness.ConcluirLinkDeAcoesAsync(await LinkDoConviteAsync(email, cancellationToken), senha, cancellationToken);

        return new UsuarioDeTeste(id, email, senha);
    }

    /// <summary>Um platform-admin próprio do teste, com o convite já concluído.</summary>
    /// <remarks>
    /// <b>Um por teste.</b> No Testcontainers não há one-shot, e o link de ações é de uso único: com um platform-admin
    /// só, dois testes paralelos disputariam o link, e o segundo receberia "Action expired". O platform-admin do JSON
    /// (<see cref="EmailDoPlatformAdmin"/>) fica para a prova do realm importado.
    /// </remarks>
    public Task<UsuarioDeTeste> NovoPlatformAdminAsync(CancellationToken cancellationToken) =>
        NovoUsuarioAsync(SoPlatformAdmin, tenantId: null, cancellationToken);

    /// <summary>Atribui um papel de realm ao usuário, como o master.</summary>
    public async Task AtribuirPapelDeRealmComoMasterAsync(
        string usuarioId, string papel, CancellationToken cancellationToken)
    {
        JsonElement representacao = await LerComoMasterAsync($"roles/{papel}", cancellationToken);

        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        using HttpResponseMessage resposta = await master.PostAsJsonAsync(
            $"admin/realms/{Realm}/users/{usuarioId}/role-mappings/realm",
            new[] { new { id = representacao.GetProperty("id").GetString(), name = papel } },
            cancellationToken);
        resposta.EnsureSuccessStatusCode();
    }

    /// <summary>Dispara o e-mail de ações do usuário, como o master, com um link de 10 minutos.</summary>
    public async Task EnviarEmailDeAcoesComoMasterAsync(string usuarioId, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        using HttpResponseMessage resposta = await master.PutAsJsonAsync(
            $"admin/realms/{Realm}/users/{usuarioId}/execute-actions-email?lifespan=600",
            AcoesDoConvite,
            cancellationToken);
        resposta.EnsureSuccessStatusCode();
    }

    // Uma vez por fixture: o ROPC que existia aqui criava um client por chamada, com direct grant, e não o removia.
    private async Task CriarClientDeContaAsync(CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        using HttpResponseMessage resposta = await master.PostAsJsonAsync(
            $"admin/realms/{Realm}/clients",
            new
            {
                clientId = ClientDeConta,
                publicClient = true,
                standardFlowEnabled = false,
                implicitFlowEnabled = false,
                directAccessGrantsEnabled = false,
                serviceAccountsEnabled = false,
                fullScopeAllowed = true,
                attributes = new Dictionary<string, string> { ["oauth2.device.authorization.grant.enabled"] = "true" },
            },
            cancellationToken);
        resposta.EnsureSuccessStatusCode();
    }
```

5. Os quatro métodos do mailpit viram delegação ao cliente (as assinaturas não mudam, e os testes da fatia C continuam chamando o fixture). Trocar os corpos de `MensagensParaAsync`, `EsperarMensagensAsync`, `TextoDaMensagemAsync` e `LinkDoConviteAsync`, mantendo os comentários XML de cada um, e apagar o campo `LinkDeAcoes` (a regex foi para o cliente):

```csharp
    public Task<IReadOnlyList<string>> MensagensParaAsync(string destinatario, CancellationToken cancellationToken) =>
        Mailpit.MensagensParaAsync(destinatario, cancellationToken);

    public Task<IReadOnlyList<string>> EsperarMensagensAsync(
        string destinatario, int quantidade, CancellationToken cancellationToken) =>
        Mailpit.EsperarMensagensAsync(destinatario, quantidade, cancellationToken);

    public Task<string> TextoDaMensagemAsync(string id, CancellationToken cancellationToken) =>
        Mailpit.TextoDaMensagemAsync(id, cancellationToken);

    public Task<Uri> LinkDoConviteAsync(string destinatario, CancellationToken cancellationToken) =>
        Mailpit.LinkDeAcoesAsync(destinatario, new Uri(HostnamePublico), Realm, cancellationToken);
```

Depois da edição, o arquivo não usa mais `System.Text.RegularExpressions`; tire o `using`.

6. No `<remarks>` da classe, acrescentar um parágrafo no fim:

```csharp
/// <para>
/// <b>Sem ROPC no realm da aplicação (ADR-003).</b> Token de usuário, aqui, só pelo harness de device flow. O
/// <c>grant_type=password</c> que resta é o do <c>admin-cli</c> do realm <b>master</b>, em
/// <see cref="CriarClienteMasterAsync"/>: é a infraestrutura do Testcontainers, fora do realm da aplicação.
/// </para>
```

- [ ] **Passo 8: O teste da Account API passa a usar o device flow**

Em `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakRealTests.cs`, no teste `UsuarioComum_NaoAlteraOTenantIdPelaAccountApi`, trocar as duas linhas

```csharp
        string token = await keycloak.TokenDeUsuarioComumAsync(username, senha, ct);
        using HttpClient conta = new() { BaseAddress = new Uri($"{keycloak.BaseUrl}/") };
        conta.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
```

por

```csharp
        using HarnessDeLogin harness = keycloak.CriarHarness(KeycloakFixture.ClientDeConta);
        TokensDeUsuario tokens = await harness.TokenPorDispositivoAsync(username, senha, ct);

        // O client de device flow do fixture herda os scopes default do realm, como qualquer client criado pela Admin
        // API: o token dele serve à Account API (aud account) e não à Gateway. É a prova, no realm vivo, de que a
        // audiência da Gateway não é default.
        PayloadDoJwt.Audiencias(PayloadDoJwt.Ler(tokens.AccessToken))
            .Should().Contain("account").And.NotContain("identity-gateway-api");

        using HttpClient conta = new() { BaseAddress = new Uri($"{keycloak.BaseUrl}/") };
        conta.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
```

(o usuário desse teste continua nascendo com senha pela Admin API — `credentials` — e `emailVerified = true`: sem ações pendentes, o device flow pede só o login e o consentimento.)

- [ ] **Passo 9: Rodar tudo e conferir que o ROPC saiu**

Run: `dotnet build IdentityGateway.slnx`
Expected: `0 Aviso(s)`, `0 Erro(s)`.

Run (Docker ligado): `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests`
Expected: PASS — `total: 208` (196 + 12), `falhou: 0`. `UsuarioComum_NaoAlteraOTenantIdPelaAccountApi` leva alguns segundos a mais: o device flow espera um `interval` de 5 s.

Run: `grep -rn '"password"' tests/IdentityGateway.Testing.Keycloak --include=*.cs`
Expected: só as linhas de `CriarClienteMasterAsync` (o `grant_type` e o campo `password` do `admin-cli` do master). Nenhuma outra.

Run: `grep -rn "directAccessGrantsEnabled = true\|TokenDeUsuarioComumAsync" tests --include=*.cs`
Expected: nenhuma linha.

- [ ] **Passo 10: 🧪 Provas por mutação**

O arquivo ainda não foi commitado: antes da primeira mutação, ponha-o no índice (`git add tests/IdentityGateway.Testing.Keycloak/HarnessDeLogin.cs`) e reverta cada mutação com `git restore tests/IdentityGateway.Testing.Keycloak/HarnessDeLogin.cs`, que restaura do índice.

Run (a cada mutação): `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter-class "*HarnessDeLoginTests"`

| # | Mutação em `HarnessDeLogin.cs` | Vermelho esperado |
|---|---|---|
| 1 | Apagar a linha `intervalo += 5;` | `SlowDown_SomaCincoSegundosAoIntervaloESegue` (esperas `5, 5, 5`) |
| 2 | No `switch` do poll, acrescentar `case "access_denied":` e `case "expired_token":` logo acima de `case "authorization_pending":` (os três só com o `break`) | `ErroDefinitivoNoToken_FalhaNaHoraSemRepetir` (os dois casos), `ConsentimentoNegado_FalhaNaHora` e `DeviceCodeExpirado_FalhaNaHora`: nenhuma exceção, porque o segundo poll entrega os tokens |
| 3 | Em `Descricao()`, trocar `string.Join(", ", f.Nomes)` por um `string.Join` sobre os valores (acrescentar em `Formulario` um `public IEnumerable<string> ValoresCrus => _campos.Select(campo => campo.Valor);` e usá-lo) | `PaginaDesconhecida_LancaComTituloFormularioENomesDosCamposSemValores` ("VALOR-SECRETO") |
| 4 | Em `GuardarCookies`, trocar `if (vencido)` por `if (vencido && nome.Length == 0)` (nunca remove) | `Cookies_SecureVoltaPorHttpEVencidoSome` (o terceiro pedido leva um cookie) |
| 5 | Em `ReescritaDeAutoridade`, apagar a linha `request.Headers.Host = publico.Authority;` | `EnderecoPublico_EDiscadoNoTransporteComOHostPublico` |
| 6 | Apagar o ramo `if (pagina.TemErro) { throw … }` de `PercorrerAsync` | `PaginaDeErroDoKeycloak_LancaNaHora` (a mensagem passa a ser a de página desconhecida) |
| 7 | Em `RenovarAsync`, envolver a chamada num laço de duas tentativas | `Renovacao_Recusada_LancaSemRepetir` (`Chamadas` igual a 2) |
| 8 | Em `PercorrerAsync`, apagar o `if (pedeSenha ? senhaEnviada : usuarioEnviado) { throw … }` | `LoginRecusado_FalhaNaPrimeiraVoltaDoFormularioSemInsistir` (a mensagem vira "mais de 12 páginas", e `Chamadas` passa de 3) |

Depois da última reversão: `git diff --stat` vazio para o arquivo, e a classe verde.

- [ ] **Passo 11: Commit**

```bash
git add tests/IdentityGateway.Testing.Keycloak tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/HarnessDeLoginTests.cs tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakRealTests.cs
git commit -m "test: harness de login por device flow e o fim do ROPC no fixture

HarnessDeLogin e um navegador minimo para o Keycloak: conclui o link de
acoes e faz login pelo Device Authorization Grant, com jar de cookies
manual (o Keycloak os marca Secure), reescrita do endereco publico para o
de transporte, consentimento, slow_down somando 5 s e falha imediata em
access_denied e expired_token. A renovacao nunca e repetida: reusar um
refresh token derruba a sessao do client. Nenhuma excecao carrega token,
senha, codigo, link ou HTML.

O fixture ganha o cliente do mailpit extraido, NovoUsuarioAsync e
NovoPlatformAdminAsync (um por teste, pelo link do e-mail) e um client de
device flow criado uma vez, so para a Account API. Sai o
TokenDeUsuarioComumAsync, que fazia ROPC num client criado por chamada.

Mutacoes: sem a soma do slow_down, access_denied tratado como pendente,
valores dos campos na excecao, cookie vencido mantido, Host do transporte,
sem o ramo da pagina de erro, renovacao repetida e login reenviado."
```

Run: `git log -1 --format=%B | grep -Eci "co-authored|generated with"`
Expected: `0`.

---

### Tarefa 5: O convite concluído pelo link e a forma do token, contra o Keycloak real

Spec: §3.1 (claims por client), §3.2 (link de ações; rotação do refresh, fim da §3), §5.1 (linha "Integração, Keycloak real"), §5.3 (mutações do realm com testemunha K), D-n.

Nenhum código de produção muda. Esta tarefa prova, no Keycloak de verdade, o que o realm da Tarefa 2 emite e o que o harness da Tarefa 4 faz — e é a segunda testemunha das mutações do realm.

**Arquivos:**
- Modify: `tests/IdentityGateway.Testing.Keycloak/KeycloakFixture.cs` (três ajudantes de grupo)
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/HarnessContraKeycloakTests.cs`
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/FormaDoTokenContraKeycloakTests.cs`

**Interfaces:**
- Consome: `KeycloakFixture.NovoUsuarioAsync`, `NovoPlatformAdminAsync`, `CriarHarness`, `LinkDoConviteAsync`, `EnviarEmailDeAcoesComoMasterAsync`, `CriarUsuarioComoMasterAsync`, `LerUsuarioCruAsync`, `LerComoMasterAsync`, `CriarOrganizacaoComoMasterAsync`; `HarnessDeLogin`; `PayloadDoJwt`; `KeycloakFixture.SlugUnico()` (extensão do projeto de integração).
- Produz, em `KeycloakFixture`:
  - `Task<string> CriarGrupoComoMasterAsync(string nome, IReadOnlyDictionary<string, string[]> atributos, CancellationToken cancellationToken)` — devolve o id do grupo;
  - `Task PorNoGrupoComoMasterAsync(string usuarioId, string grupoId, CancellationToken cancellationToken)`;
  - `Task ApagarGrupoComoMasterAsync(string grupoId, CancellationToken cancellationToken)`.

- [ ] **Passo 1: Ajudantes de grupo no fixture**

Em `tests/IdentityGateway.Testing.Keycloak/KeycloakFixture.cs`, depois de `AtribuirPapelDeRealmComoMasterAsync`:

```csharp
    /// <summary>Cria um grupo de realm com atributos, como o master, e devolve o id.</summary>
    /// <remarks>
    /// O realm não tem grupos, e nenhum código da Gateway os cria. Só os testes que caracterizam o recuo do mapper de
    /// atributo para o grupo usam isto — e apagam o grupo no fim.
    /// </remarks>
    public async Task<string> CriarGrupoComoMasterAsync(
        string nome, IReadOnlyDictionary<string, string[]> atributos, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        using HttpResponseMessage resposta = await master.PostAsJsonAsync(
            $"admin/realms/{Realm}/groups", new { name = nome, attributes = atributos }, cancellationToken);
        resposta.EnsureSuccessStatusCode();

        return resposta.Headers.Location!.Segments[^1];
    }

    public async Task PorNoGrupoComoMasterAsync(string usuarioId, string grupoId, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        using HttpResponseMessage resposta = await master.PutAsync(
            new Uri($"admin/realms/{Realm}/users/{usuarioId}/groups/{grupoId}", UriKind.Relative),
            content: null,
            cancellationToken);
        resposta.EnsureSuccessStatusCode();
    }

    public async Task ApagarGrupoComoMasterAsync(string grupoId, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        using HttpResponseMessage resposta = await master.DeleteAsync(
            new Uri($"admin/realms/{Realm}/groups/{grupoId}", UriKind.Relative), cancellationToken);
        resposta.EnsureSuccessStatusCode();
    }
```

- [ ] **Passo 2: Testes do harness contra o Keycloak real**

`tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/HarnessContraKeycloakTests.cs`:

```csharp
using System.Text.Json;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O harness contra as páginas de verdade do Keycloak 26.7.4: o link de ações, o login e a sessão.
/// </summary>
/// <remarks>
/// <b>O login em um passo não é afirmado aqui.</b> Sem nenhuma Organization no realm, o Keycloak mostra usuário e
/// senha numa página só; com uma, vira identity-first em duas. O realm do fixture é compartilhado, e a existência de
/// Organization depende da ordem dos testes — o um-passo é determinístico só no job da CI, onde o platform-admin entra
/// antes de qualquer tenant existir.
/// </remarks>
public sealed class HarnessContraKeycloakTests(KeycloakFixture keycloak)
{
    private static readonly string[] SoTenantAdmin = ["tenant-admin"];

    private static readonly string[] AcoesDoConvite = ["UPDATE_PASSWORD", "VERIFY_EMAIL"];

    [Fact]
    public async Task LinkDeAcoes_DefineASenhaZeraAsAcoesEVerificaOEmail()
    {
        // O link do e-mail resolve o VERIFY_EMAIL ao ser aberto; a senha e o perfil (nome e sobrenome, exigidos pelo
        // User Profile) completam as ações. Leitura crua pelo master, nunca pelo que o harness "acha" que fez.
        CancellationToken ct = TestContext.Current.CancellationToken;

        UsuarioDeTeste usuario = await keycloak.NovoUsuarioAsync(SoTenantAdmin, TenantId.New().Value.ToString(), ct);

        JsonElement cru = await keycloak.LerUsuarioCruAsync(usuario.Id, ct);
        cru.GetProperty("emailVerified").GetBoolean().Should().BeTrue();
        cru.GetProperty("requiredActions").GetArrayLength().Should().Be(0);
        cru.GetProperty("firstName").GetString().Should().NotBeNullOrEmpty();
        (await keycloak.LerComoMasterAsync($"users/{usuario.Id}/credentials", ct)).GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task LinkJaConcluido_FalhaNaHoraComAPaginaDeErro()
    {
        // Foco de revisão 1: a pessoa clica duas vezes no link do e-mail. O token de ação morre no próprio uso, e o
        // Keycloak responde 400 com a página de erro. O harness precisa dizer isso na hora — sem laço, sem esperar
        // prazo — e a senha definida na primeira vez continua valendo.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string email = KeycloakFixture.EmailUnico();
        string senha = SenhasDeTeste.Gerar();
        string id = await keycloak.CriarUsuarioComoMasterAsync(
            new { username = email, email, enabled = true, emailVerified = false, requiredActions = AcoesDoConvite }, ct);
        await keycloak.EnviarEmailDeAcoesComoMasterAsync(id, ct);
        Uri link = await keycloak.LinkDoConviteAsync(email, ct);

        using (HarnessDeLogin primeiro = keycloak.CriarHarness())
        {
            await primeiro.ConcluirLinkDeAcoesAsync(link, senha, ct);
        }

        using HarnessDeLogin segundo = keycloak.CriarHarness();
        Func<Task> deNovo = () => segundo.ConcluirLinkDeAcoesAsync(link, SenhasDeTeste.Gerar(), ct);

        FalhaDoHarnessException falha = (await deNovo.Should().ThrowAsync<FalhaDoHarnessException>()).Which;
        falha.Familia.Should().Be(FamiliaDeFalha.Formulario);
        falha.Message.Should().Contain("página de erro").And.NotContain("key=");

        using HarnessDeLogin login = keycloak.CriarHarness();
        TokensDeUsuario tokens = await login.TokenPorDispositivoAsync(email, senha, ct);
        tokens.AccessToken.Should().NotBeNullOrEmpty("a senha da primeira conclusão continua valendo");
    }

    [Fact]
    public async Task LoginComOrganizationNoRealm_LevaDoisPassos()
    {
        // Identity-first: havendo ao menos uma Organization, o Keycloak pede o usuário numa página e a senha em outra.
        // O harness preenche "o que houver" em cada kc-form-login.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await keycloak.CriarOrganizacaoComoMasterAsync(KeycloakFixture.SlugUnico().Value, tenantId: null, ct);
        UsuarioDeTeste usuario = await keycloak.NovoUsuarioAsync(SoTenantAdmin, TenantId.New().Value.ToString(), ct);
        using HarnessDeLogin harness = keycloak.CriarHarness();

        await harness.TokenPorDispositivoAsync(usuario.Email, usuario.Senha, ct);

        harness.PassosDoUltimoLogin.Should().Be(2);
    }

    [Fact]
    public async Task SegundoLoginNaMesmaInstancia_ReaproveitaASessaoSemPedirLogin()
    {
        // Uma instância do harness é uma sessão de navegador. É também o motivo do aviso do README: no mesmo
        // navegador, o segundo device flow sai como o usuário que já está logado.
        CancellationToken ct = TestContext.Current.CancellationToken;
        UsuarioDeTeste usuario = await keycloak.NovoPlatformAdminAsync(ct);
        using HarnessDeLogin harness = keycloak.CriarHarness();
        TokensDeUsuario primeiro = await harness.TokenPorDispositivoAsync(usuario.Email, usuario.Senha, ct);

        TokensDeUsuario segundo = await harness.TokenPorDispositivoAsync(usuario.Email, usuario.Senha, ct);

        harness.PassosDoUltimoLogin.Should().Be(0);
        PayloadDoJwt.Ler(segundo.AccessToken).GetProperty("sub").GetString()
            .Should().Be(PayloadDoJwt.Ler(primeiro.AccessToken).GetProperty("sub").GetString());
    }

    [Fact]
    public async Task LinkDeAcoes_AbreSemSerConsumido()
    {
        // É como o job da CI confere o convite do admin do tenant sem concluí-lo: abrir a primeira página do link não o
        // gasta, e o convidado ainda consegue usá-lo. Concluído, o link passa a abrir a página de erro.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string email = KeycloakFixture.EmailUnico();
        string id = await keycloak.CriarUsuarioComoMasterAsync(
            new { username = email, email, enabled = true, emailVerified = false, requiredActions = AcoesDoConvite }, ct);
        await keycloak.EnviarEmailDeAcoesComoMasterAsync(id, ct);
        Uri link = await keycloak.LinkDoConviteAsync(email, ct);

        using (HarnessDeLogin curioso = keycloak.CriarHarness())
        {
            (await curioso.LinkDeAcoesAbreAsync(link, ct)).Should().BeTrue();
            (await curioso.LinkDeAcoesAbreAsync(link, ct)).Should().BeTrue("abrir duas vezes também não consome");
        }

        using HarnessDeLogin convidado = keycloak.CriarHarness();
        await convidado.ConcluirLinkDeAcoesAsync(link, SenhasDeTeste.Gerar(), ct);

        using HarnessDeLogin depois = keycloak.CriarHarness();
        (await depois.LinkDeAcoesAbreAsync(link, ct)).Should().BeFalse("concluído, o link abre a página de erro");
    }

    [Fact]
    public async Task SenhaErrada_FalhaSemInsistirEAContaContinuaEntrando()
    {
        // O realm tem bruteForceProtected. Um harness que reenviasse o formulário bloquearia a conta; com uma tentativa
        // só, o login certo logo em seguida passa.
        CancellationToken ct = TestContext.Current.CancellationToken;
        UsuarioDeTeste usuario = await keycloak.NovoPlatformAdminAsync(ct);

        using (HarnessDeLogin errado = keycloak.CriarHarness())
        {
            Func<Task> entrar = () => errado.TokenPorDispositivoAsync(usuario.Email, SenhasDeTeste.Gerar(), ct);

            (await entrar.Should().ThrowAsync<FalhaDoHarnessException>()).Which.Message.Should().Contain("recusados");
        }

        using HarnessDeLogin certo = keycloak.CriarHarness();
        TokensDeUsuario tokens = await certo.TokenPorDispositivoAsync(usuario.Email, usuario.Senha, ct);
        tokens.AccessToken.Should().NotBeNullOrEmpty();
    }
}
```

- [ ] **Passo 3: Testes da forma do token**

`tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/FormaDoTokenContraKeycloakTests.cs`:

```csharp
using System.Text.Json;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// O access token que o client de demonstração emite, claim a claim — o que a Api vai validar.
/// </summary>
/// <remarks>
/// Cada asserção aqui é a segunda testemunha de uma regra do realm: <c>fullScopeAllowed</c> falso (só o catálogo no
/// <c>roles</c>), <c>basic</c> nos defaults (o <c>sub</c>), sem <c>profile</c> nem <c>email</c> (nenhum dado pessoal
/// no token), <c>gateway-api</c> (a audiência) e a rotação do refresh token.
/// </remarks>
public sealed class FormaDoTokenContraKeycloakTests(KeycloakFixture keycloak)
{
    private static readonly string[] Catalogo = ["platform-admin", "tenant-admin", "financial-manager", "reader"];

    private static readonly string[] SoTenantAdmin = ["tenant-admin"];

    private static readonly string[] SemPapeis = [];

    private static readonly string[] ClaimsQueNaoPodemSair =
    [
        "email", "email_verified", "preferred_username", "name", "given_name", "family_name", "realm_access",
        "resource_access",
    ];

    private async Task<JsonElement> PayloadDeAsync(UsuarioDeTeste usuario, CancellationToken ct)
    {
        using HarnessDeLogin harness = keycloak.CriarHarness();
        TokensDeUsuario tokens = await harness.TokenPorDispositivoAsync(usuario.Email, usuario.Senha, ct);

        return PayloadDoJwt.Ler(tokens.AccessToken);
    }

    private static string[] Roles(JsonElement payload) =>
        [.. payload.GetProperty("roles").EnumerateArray().Select(papel => papel.GetString()!)];

    [Fact]
    public async Task TokenDoTenantAdmin_TemAFormaQueAGatewayValida()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string tenant = TenantId.New().Value.ToString();
        UsuarioDeTeste usuario = await keycloak.NovoUsuarioAsync(SoTenantAdmin, tenant, ct);

        JsonElement payload = await PayloadDeAsync(usuario, ct);

        payload.GetProperty("iss").GetString().Should().Be($"{KeycloakFixture.HostnamePublico}/realms/{KeycloakFixture.Realm}");

        // aud como TEXTO, e só a Gateway: com um valor só, o audience mapper não emite array.
        payload.GetProperty("aud").ValueKind.Should().Be(JsonValueKind.String);
        payload.GetProperty("aud").GetString().Should().Be("identity-gateway-api");
        payload.GetProperty("azp").GetString().Should().Be(KeycloakFixture.ClientDeDemonstracao);
        payload.GetProperty("typ").GetString().Should().Be("Bearer");
        payload.GetProperty("acr").ValueKind.Should().Be(JsonValueKind.String);

        // sub: o id do usuário, GUID no formato D — vem do scope basic.
        payload.GetProperty("sub").GetString().Should().Be(usuario.Id);
        Guid.TryParseExact(usuario.Id, "D", out _).Should().BeTrue();

        payload.GetProperty("tenant_id").ValueKind.Should().Be(JsonValueKind.String);
        payload.GetProperty("tenant_id").GetString().Should().Be(tenant);
        payload.GetProperty("roles").ValueKind.Should().Be(JsonValueKind.Array);
        Roles(payload).Should().Equal("tenant-admin");

        // ADR-005: 5 minutos, configurados no realm.
        (payload.GetProperty("exp").GetInt64() - payload.GetProperty("iat").GetInt64()).Should().Be(300);

        // DT11: o token não carrega e-mail nem nome — ele vai a histórico de shell, a proxies e à demonstração.
        ClaimsQueNaoPodemSair.Should().NotContain(claim => payload.TryGetProperty(claim, out _));
    }

    [Fact]
    public async Task TokenDoPlatformAdmin_TemOPapelENaoTemTenant()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        UsuarioDeTeste usuario = await keycloak.NovoPlatformAdminAsync(ct);

        JsonElement payload = await PayloadDeAsync(usuario, ct);

        Roles(payload).Should().Equal("platform-admin");
        payload.TryGetProperty("tenant_id", out _).Should().BeFalse();
        PayloadDoJwt.Audiencias(payload).Should().Equal("identity-gateway-api");
    }

    [Fact]
    public async Task Roles_SoTrazOCatalogo_MesmoComPapelForaDeleNoUsuario()
    {
        // Com fullScopeAllowed verdadeiro no demo, o claim traria default-roles-identity-gateway. A pré-condição é
        // conferida antes, pelo master: o usuário criado pela Admin API TEM um papel fora do catálogo. Sem ela o teste
        // seria vacuoso — o platform-admin do JSON, por exemplo, não tem o papel padrão.
        CancellationToken ct = TestContext.Current.CancellationToken;
        UsuarioDeTeste usuario = await keycloak.NovoUsuarioAsync(SoTenantAdmin, TenantId.New().Value.ToString(), ct);
        JsonElement efetivos = await keycloak.LerComoMasterAsync($"users/{usuario.Id}/role-mappings/realm/composite", ct);
        efetivos.EnumerateArray().Select(papel => papel.GetProperty("name").GetString())
            .Should().Contain($"default-roles-{KeycloakFixture.Realm}");

        JsonElement payload = await PayloadDeAsync(usuario, ct);

        Roles(payload).Should().BeSubsetOf(Catalogo).And.Equal("tenant-admin");
    }

    [Fact]
    public async Task UsuarioSemPapelDoCatalogo_TokenSaiSemOClaimRoles()
    {
        // Sem papel do catálogo, o mapper não emite o claim. A Api trata a ausência como "sem papel", não como erro.
        CancellationToken ct = TestContext.Current.CancellationToken;
        UsuarioDeTeste usuario = await keycloak.NovoUsuarioAsync(SemPapeis, tenantId: null, ct);

        JsonElement payload = await PayloadDeAsync(usuario, ct);

        payload.TryGetProperty("roles", out _).Should().BeFalse();
        payload.GetProperty("sub").GetString().Should().Be(usuario.Id);
    }

    [Fact]
    public async Task RefreshToken_RotacionaEOReusoDerrubaASessaoDoClient()
    {
        // D-n. A renovação devolve um refresh token NOVO, e o usado passa a ser recusado. E o reuso tem um efeito que
        // molda todo o resto: depois dele, até o refresh token novo é recusado — o Keycloak derruba a sessão daquele
        // client. Por isso ninguém neste repositório repete uma renovação. Sessão própria (harness e usuário só deste
        // teste), para o reuso não derrubar a sessão de ninguém.
        CancellationToken ct = TestContext.Current.CancellationToken;
        UsuarioDeTeste usuario = await keycloak.NovoPlatformAdminAsync(ct);
        using HarnessDeLogin harness = keycloak.CriarHarness();
        TokensDeUsuario primeiro = await harness.TokenPorDispositivoAsync(usuario.Email, usuario.Senha, ct);

        TokensDeUsuario renovado = await harness.RenovarAsync(primeiro.RefreshToken, ct);

        renovado.RefreshToken.Should().NotBe(primeiro.RefreshToken);
        PayloadDoJwt.Ler(renovado.AccessToken).GetProperty("sub").GetString().Should().Be(usuario.Id);

        Func<Task> reuso = () => harness.RenovarAsync(primeiro.RefreshToken, ct);
        (await reuso.Should().ThrowAsync<FalhaDoHarnessException>()).Which.Message.Should().Contain("invalid_grant");

        Func<Task> depoisDoReuso = () => harness.RenovarAsync(renovado.RefreshToken, ct);
        (await depoisDoReuso.Should().ThrowAsync<FalhaDoHarnessException>("o reuso derrubou a sessão do client"))
            .Which.Message.Should().Contain("invalid_grant");
    }

    [Fact]
    public async Task GrupoComTenantId_DaOClaimAUsuarioSemOAtributo()
    {
        // CARACTERIZAÇÃO, não requisito: o mapper de atributo recua para o atributo de mesmo nome do grupo, e não há
        // configuração que desligue isso. É o motivo de a rota de tenant exigir a pertença no banco (ADR-011). Se um
        // upgrade do Keycloak mudar este comportamento, o teste avisa — e a decisão pode ser revista.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string forjado = TenantId.New().Value.ToString();
        UsuarioDeTeste usuario = await keycloak.NovoUsuarioAsync(SoTenantAdmin, tenantId: null, ct);
        string grupo = await keycloak.CriarGrupoComoMasterAsync(
            $"g-{Guid.NewGuid():N}", new Dictionary<string, string[]> { ["tenant_id"] = [forjado] }, ct);

        try
        {
            await keycloak.PorNoGrupoComoMasterAsync(usuario.Id, grupo, ct);

            JsonElement payload = await PayloadDeAsync(usuario, ct);

            payload.GetProperty("tenant_id").GetString().Should().Be(forjado);
            Roles(payload).Should().Equal("tenant-admin");
        }
        finally
        {
            await keycloak.ApagarGrupoComoMasterAsync(grupo, CancellationToken.None);
        }
    }
}
```

- [ ] **Passo 4: Rodar**

Run: `dotnet build IdentityGateway.slnx`
Expected: `0 Aviso(s)`, `0 Erro(s)`.

Run (Docker ligado): `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter-class "*HarnessContraKeycloakTests"`
Expected: PASS — `total: 6`. Cada teste leva de 6 a 15 s: todo device flow espera um `interval` de 5 s.

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter-class "*FormaDoTokenContraKeycloakTests"`
Expected: PASS — `total: 6`.

Se um teste falhar com `FalhaDoHarnessException` de "página que o harness não conhece", a mensagem traz o título, o id do formulário e os nomes dos campos: é o que falta ensinar ao `PercorrerAsync`. Não mexa no teste antes de entender a página.

- [ ] **Passo 5: 🧪 Provas por mutação no realm**

Cada mutação muda `keycloak/bootstrap/realm-identity-gateway.json`; o fixture reimporta na execução seguinte. Reverter com `git checkout keycloak/bootstrap/realm-identity-gateway.json` depois de cada uma.

Run (a cada mutação): `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter-class "*FormaDoTokenContraKeycloakTests"`

| # | Mutação no JSON | Vermelho esperado |
|---|---|---|
| 1 | `"fullScopeAllowed": true` no client `identity-gateway-demo` | `Roles_SoTrazOCatalogo_MesmoComPapelForaDeleNoUsuario` e `TokenDoTenantAdmin_…` (o `roles` ganha `default-roles-identity-gateway`); `UsuarioSemPapelDoCatalogo_…` (o claim aparece) |
| 2 | `"revokeRefreshToken": false` | `RefreshToken_RotacionaEOReusoDerrubaASessaoDoClient` (o reuso é aceito) |
| 3 | `"refreshTokenMaxReuse": 1` | `RefreshToken_RotacionaEOReusoDerrubaASessaoDoClient` (o primeiro reuso é aceito) |
| 4 | Tirar `"basic"` dos `defaultClientScopes` do demo | `TokenDoTenantAdmin_TemAFormaQueAGatewayValida` (`KeyNotFoundException` em `sub`: o token sai sem ele) |
| 5 | Acrescentar `"email"` aos `defaultClientScopes` do demo | `TokenDoTenantAdmin_…` (`email` e `email_verified` no token) |
| 6 | Tirar `"gateway-api"` dos `defaultClientScopes` do demo | `TokenDoTenantAdmin_…` e `TokenDoPlatformAdmin_…` (sem `aud`) |
| 7 | Trocar `"multivalued": "true"` por `"false"` no mapper do `gateway-roles` | `TokenDoTenantAdmin_…` (o `roles` deixa de ser array) |

As mutações 2 e 3 são as que a spec deixou como **não verificadas** (fim da §3): anote o resultado observado de cada uma no handoff, inclusive se o comportamento for diferente do esperado.

Depois da última reversão:

Run: `git status --short keycloak/`
Expected: vazio.

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests`
Expected: PASS — `total: 220` (208 + 12), `falhou: 0`.

- [ ] **Passo 6: Commit**

```bash
git add tests/IdentityGateway.Testing.Keycloak/KeycloakFixture.cs tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/HarnessContraKeycloakTests.cs tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/FormaDoTokenContraKeycloakTests.cs
git commit -m "test: convite concluido pelo link e a forma do token no Keycloak real

Contra o Keycloak 26.7.4: o link de acoes define a senha, zera as acoes
e verifica o e-mail; um link ja concluido falha na hora com a pagina de
erro; o login com Organization leva dois passos e a mesma sessao nao pede
login de novo; senha errada falha sem insistir. O token do client de
demonstracao sai com aud texto identity-gateway-api, azp, typ Bearer, sub
GUID, tenant_id, roles so com o catalogo e 300 s, sem e-mail nem nome. A
rotacao do refresh token: o usado e recusado, e o reuso derruba a sessao
do client. Caracterizacao: grupo com tenant_id da o claim a usuario sem o
atributo (o motivo do ADR-011).

Mutacoes no realm, com reimport: fullScopeAllowed no demo,
revokeRefreshToken falso, refreshTokenMaxReuse 1, demo sem basic, com
email, sem gateway-api e roles nao multivalorado."
```

Run: `git log -1 --format=%B | grep -Eci "co-authored|generated with"`
Expected: `0`.

---

### Tarefa 6: `AccessTokenValidationOptions` — o que a Api precisa saber, sem conhecer o Keycloak

Spec: §4.2 (primeiro bloco), DT1, DT4, DT5 (`RequireHttpsMetadata`), §5.1 (linha "Configuração").

A Api vai validar o token, mas não pode ler `KeycloakAdminOptions` (teste de arquitetura `AApiNaoConheceOKeycloak`, ADR-008) nem refazer a conta do emissor. O adaptador do Keycloak preenche uma option **neutra**, que a Api só lê. O emissor aceito e o `aud` do client assertion saem da **mesma propriedade**: duas derivações poderiam divergir.

**Arquivos:**
- Create: `src/IdentityGateway.Infrastructure/Configuration/AccessTokenValidationOptions.cs`
- Modify: `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakAdminOptions.cs` (`Issuer`, `MetadataAddress`, o comentário de `PublicBaseUrl`)
- Modify: `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakServiceCollectionExtensions.cs`
- Modify: `src/IdentityGateway.Api/appsettings.Development.json`
- Test: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/AccessTokenValidationOptionsTests.cs`
- Test: `tests/IdentityGateway.ArchitectureTests/RegrasDoAmbienteLocalTests.cs`

**Interfaces:**
- Consome: `KeycloakAdminOptions` (`BaseUrl`, `PublicBaseUrl`, `Realm`, `AllowInsecureHttp`), `AmbienteDeTeste.ComAmbiente(nome)` e `ChavesDeTeste.Gerar()` (existentes).
- Produz:
  - `public sealed class AccessTokenValidationOptions` (namespace `IdentityGateway.Infrastructure.Configuration`):
    - `public const string SectionName = "Keycloak:Auth";`
    - `public string Audience { get; init; }` — padrão `"identity-gateway-api"`;
    - `public IReadOnlyList<string> AllowedClients { get; init; }` — padrão vazio;
    - `public string Issuer { get; internal set; }`, `public string MetadataAddress { get; internal set; }`, `public bool RequireHttpsMetadata { get; internal set; }` — preenchidos pelo adaptador, nunca pela configuração.
  - Em `KeycloakAdminOptions` (interno): `public string Issuer`, `public string MetadataAddress`; `AssertionAudience` passa a ser `=> Issuer`.
  - Registro: `IOptions<AccessTokenValidationOptions>` resolvível depois de `AddInfrastructure` (por `AddKeycloakIdentity`), com `ValidateOnStart`.

- [ ] **Passo 1: Escrever os testes**

`tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/AccessTokenValidationOptionsTests.cs`:

```csharp
using IdentityGateway.Infrastructure.Configuration;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// A option que a Api lê para validar o access token: derivada do adaptador do Keycloak, validada na subida.
/// </summary>
/// <remarks>
/// Sem Keycloak e sem host: resolve as options pelo mesmo <c>AddKeycloakIdentity</c> da produção, em Development e em
/// Production. Os mesmos ramos são provados com o host de pé, e com pedidos, nos testes funcionais da Api.
/// </remarks>
public sealed class AccessTokenValidationOptionsTests
{
    private static readonly string PemValido = ChavesDeTeste.Gerar().PemPrivado;

    private static ServiceProvider Compor(Dictionary<string, string?> valores, string ambiente)
    {
        IConfiguration configuracao = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();

        ServiceCollection services = new();
        services.ComAmbiente(ambiente);
        services.AddKeycloakIdentity(configuracao);

        return services.BuildServiceProvider();
    }

    private static AccessTokenValidationOptions Resolver(
        Dictionary<string, string?> valores, string ambiente = "Development")
    {
        using ServiceProvider provider = Compor(valores, ambiente);
        return provider.GetRequiredService<IOptions<AccessTokenValidationOptions>>().Value;
    }

    private static Dictionary<string, string?> EmProducao() => new()
    {
        ["Keycloak:Admin:BaseUrl"] = "https://keycloak.interno.test",
        ["Keycloak:Admin:PublicBaseUrl"] = "https://sso.exemplo.test",
        ["Keycloak:Admin:Realm"] = "identity-gateway",
        ["Keycloak:Admin:ClientId"] = "identity-gateway",
        ["Keycloak:Admin:PrivateKeyPem"] = PemValido,
    };

    private static Dictionary<string, string?> DoCompose() => new()
    {
        ["Keycloak:Admin:BaseUrl"] = "http://keycloak:8080",
        ["Keycloak:Admin:PublicBaseUrl"] = "http://localhost:8081",
        ["Keycloak:Admin:Realm"] = "identity-gateway",
        ["Keycloak:Admin:ClientId"] = "identity-gateway",
        ["Keycloak:Admin:PrivateKeyPem"] = PemValido,
        ["Keycloak:Admin:AllowInsecureHttp"] = "true",
        ["Keycloak:Auth:AllowedClients:0"] = "identity-gateway-demo",
    };

    [Fact]
    public void Issuer_ComPublicBaseUrl_EOMesmoTextoDoAudDoAssertion()
    {
        // DT1: uma propriedade só alimenta os dois. Se a Api refizesse a conta, um dia as duas divergiriam — e o
        // sintoma seria "o service account autentica, e todo token de usuário leva 401".
        Dictionary<string, string?> valores = EmProducao();
        using ServiceProvider provider = Compor(valores, "Production");

        AccessTokenValidationOptions validacao = provider.GetRequiredService<IOptions<AccessTokenValidationOptions>>().Value;
        KeycloakAdminOptions admin = provider.GetRequiredService<IOptions<KeycloakAdminOptions>>().Value;

        validacao.Issuer.Should().Be("https://sso.exemplo.test/realms/identity-gateway");
        validacao.Issuer.Should().Be(admin.AssertionAudience);
    }

    [Fact]
    public void Issuer_SemPublicBaseUrl_SaiDoBaseUrlEContinuaIgualAoAud()
    {
        Dictionary<string, string?> valores = EmProducao();
        valores.Remove("Keycloak:Admin:PublicBaseUrl");
        using ServiceProvider provider = Compor(valores, "Production");

        AccessTokenValidationOptions validacao = provider.GetRequiredService<IOptions<AccessTokenValidationOptions>>().Value;
        KeycloakAdminOptions admin = provider.GetRequiredService<IOptions<KeycloakAdminOptions>>().Value;

        validacao.Issuer.Should().Be("https://keycloak.interno.test/realms/identity-gateway");
        validacao.Issuer.Should().Be(admin.AssertionAudience);
    }

    [Fact]
    public void MetadataAddress_SaiDoBaseUrlInternoENuncaDoPublico()
    {
        // O endereço público pode nem resolver de dentro da rede (localhost:8081 visto de dentro do container da api).
        AccessTokenValidationOptions validacao = Resolver(DoCompose());

        validacao.MetadataAddress.Should().Be(
            "http://keycloak:8080/realms/identity-gateway/.well-known/openid-configuration");
        validacao.Issuer.Should().Be("http://localhost:8081/realms/identity-gateway");
    }

    [Fact]
    public void MetadataAddress_PreservaOPrefixoDeCaminhoDoBaseUrl()
    {
        Dictionary<string, string?> valores = EmProducao();
        valores["Keycloak:Admin:BaseUrl"] = "https://keycloak.interno.test/auth/";

        AccessTokenValidationOptions validacao = Resolver(valores, "Production");

        validacao.MetadataAddress.Should().Be(
            "https://keycloak.interno.test/auth/realms/identity-gateway/.well-known/openid-configuration");
    }

    [Fact]
    public void RequireHttpsMetadata_SegueOAllowInsecureHttp()
    {
        // DT5: !IsDevelopment() solto faria a app subir e responder 500 a todo pedido — a checagem do JwtBearer roda
        // no primeiro pedido, não na subida. A regra que já é validada na subida é a do AllowInsecureHttp.
        Resolver(DoCompose()).RequireHttpsMetadata.Should().BeFalse();
        Resolver(EmProducao(), "Production").RequireHttpsMetadata.Should().BeTrue();
    }

    [Fact]
    public void SemSecaoAuth_AudiencePadraoEListaVazia()
    {
        // Fail-closed: sem lista, a API sobe e recusa todo token de usuário (o aviso da subida é da Api).
        AccessTokenValidationOptions validacao = Resolver(EmProducao(), "Production");

        validacao.Audience.Should().Be("identity-gateway-api");
        validacao.AllowedClients.Should().BeEmpty();
    }

    [Fact]
    public void AllowedClients_VemDaConfiguracao()
    {
        Dictionary<string, string?> valores = EmProducao();
        valores["Keycloak:Auth:AllowedClients:0"] = "console-administrativo";
        valores["Keycloak:Auth:AllowedClients:1"] = "portal";

        AccessTokenValidationOptions validacao = Resolver(valores, "Production");

        validacao.AllowedClients.Should().Equal("console-administrativo", "portal");
    }

    [Fact]
    public void ClientDeDemonstracaoEmDevelopment_Aceito()
    {
        Resolver(DoCompose()).AllowedClients.Should().Equal("identity-gateway-demo");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void ClientDeDemonstracaoForaDeDevelopment_FalhaAoValidarComMensagemNeutra(string ambiente)
    {
        // DT4: o client público de device flow é o vetor clássico de phishing de código de dispositivo. "Só no arquivo
        // de Development" não é garantia — toda a suíte roda em Development, e o IConfiguration mescla arrays por
        // índice. A garantia é a subida recusar.
        Dictionary<string, string?> valores = EmProducao();
        valores["Keycloak:Auth:AllowedClients:0"] = "console-administrativo";
        valores["Keycloak:Auth:AllowedClients:1"] = "identity-gateway-demo";

        Action validar = () => Resolver(valores, ambiente);

        validar.Should().Throw<OptionsValidationException>()
            .WithMessage("*AllowedClients*Development*")
            .Which.Message.Should().NotContain("console-administrativo");
    }

    [Fact]
    public void ItemVazioNaLista_FalhaAoValidar()
    {
        // Um item em branco casaria com um azp em branco.
        Dictionary<string, string?> valores = EmProducao();
        valores["Keycloak:Auth:AllowedClients:0"] = " ";

        Action validar = () => Resolver(valores, "Production");

        validar.Should().Throw<OptionsValidationException>().WithMessage("*AllowedClients*");
    }

    [Fact]
    public void AudienceVazia_FalhaAoValidar()
    {
        Dictionary<string, string?> valores = EmProducao();
        valores["Keycloak:Auth:Audience"] = "";

        Action validar = () => Resolver(valores, "Production");

        validar.Should().Throw<OptionsValidationException>().WithMessage("*Audience*");
    }

    [Fact]
    public void IssuerEMetadadosNaConfiguracao_SaoIgnorados()
    {
        // Os três são derivados, com setter interno: ninguém aponta a validação para outro emissor por configuração.
        Dictionary<string, string?> valores = EmProducao();
        valores["Keycloak:Auth:Issuer"] = "https://atacante.test/realms/x";
        valores["Keycloak:Auth:MetadataAddress"] = "https://atacante.test/.well-known/openid-configuration";
        valores["Keycloak:Auth:RequireHttpsMetadata"] = "false";

        AccessTokenValidationOptions validacao = Resolver(valores, "Production");

        validacao.Issuer.Should().Be("https://sso.exemplo.test/realms/identity-gateway");
        validacao.MetadataAddress.Should().StartWith("https://keycloak.interno.test/");
        validacao.RequireHttpsMetadata.Should().BeTrue();
    }
}
```

Em `tests/IdentityGateway.ArchitectureTests/RegrasDoAmbienteLocalTests.cs`, acrescentar:

```csharp
    [Fact]
    public void ListaDeClientsPermitidosSoNoAppsettingsDeDevelopment()
    {
        // DT4: o IConfiguration mescla arrays POR ÍNDICE. Uma lista no appsettings.json base sobreviveria, do segundo
        // item em diante, à configuração de produção. A lista de azp vive só no arquivo de Development.
        using var baseDaApi = JsonDocument.Parse(File.ReadAllText(
            RaizDoRepositorio.Caminho("src", "IdentityGateway.Api", "appsettings.json")));
        using var desenvolvimento = JsonDocument.Parse(File.ReadAllText(
            RaizDoRepositorio.Caminho("src", "IdentityGateway.Api", "appsettings.Development.json")));

        baseDaApi.RootElement.GetProperty("Keycloak").TryGetProperty("Auth", out _).Should().BeFalse(
            "nem a lista, nem a seção: fora de Development a lista vem do ambiente");
        desenvolvimento.RootElement.GetProperty("Keycloak").GetProperty("Auth").GetProperty("AllowedClients")
            .EnumerateArray().Select(client => client.GetString())
            .Should().Equal("identity-gateway-demo");
    }
```

- [ ] **Passo 2: Rodar e ver falhar**

Run: `dotnet build IdentityGateway.slnx`
Expected: FAIL de compilação — `CS0246: AccessTokenValidationOptions`.

Run: `dotnet test tests/IdentityGateway.ArchitectureTests --filter-method "*ListaDeClientsPermitidosSoNoAppsettingsDeDevelopment"`
Expected: FAIL — `KeyNotFoundException` (o `appsettings.Development.json` ainda não tem `Auth`).

- [ ] **Passo 3: A option**

`src/IdentityGateway.Infrastructure/Configuration/AccessTokenValidationOptions.cs`:

```csharp
namespace IdentityGateway.Infrastructure.Configuration;

/// <summary>
/// O que a Api precisa para validar o access token de quem a chama.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neutra quanto ao provedor de identidade.</b> A Api não conhece o Keycloak (ADR-008, com teste de arquitetura):
/// quem sabe como se escreve o emissor de um realm e onde ficam os metadados é o adaptador, que preenche
/// <see cref="Issuer"/>, <see cref="MetadataAddress"/> e <see cref="RequireHttpsMetadata"/>. Esses três têm setter
/// interno, que a configuração não alcança: ninguém aponta a validação para outro emissor por variável de ambiente.
/// </para>
/// <para>
/// <b>Da configuração vêm só a audiência e a lista de clients</b> (<see cref="SectionName"/>). A lista fica fora do
/// <c>appsettings.json</c> base de propósito: o <c>IConfiguration</c> mescla arrays por índice, e um item do arquivo
/// base sobreviveria à configuração de produção.
/// </para>
/// </remarks>
public sealed class AccessTokenValidationOptions
{
    /// <summary>Seção correspondente no arquivo de configuração.</summary>
    public const string SectionName = "Keycloak:Auth";

    /// <summary>A audiência que o token precisa trazer no <c>aud</c>.</summary>
    public string Audience { get; init; } = "identity-gateway-api";

    /// <summary>Os clients (<c>azp</c>) cujos tokens a Gateway aceita. Igualdade ordinal.</summary>
    /// <remarks>
    /// <b>Vazia é permitido, e fecha:</b> a API sobe e recusa todo token de usuário. Fora de Development ela fica vazia
    /// até existir um client administrativo; é melhor que recusar a subida, porque a API continua servindo o que não
    /// depende de usuário (o provisionamento pelo Outbox, os health checks).
    /// </remarks>
    public IReadOnlyList<string> AllowedClients { get; init; } = [];

    /// <summary>O emissor aceito: o endereço público do realm, sem barra final. Comparado por igualdade ordinal.</summary>
    public string Issuer { get; internal set; } = string.Empty;

    /// <summary>Onde buscar os metadados OIDC e, por eles, as chaves públicas — pelo endereço de transporte.</summary>
    public string MetadataAddress { get; internal set; } = string.Empty;

    /// <summary>Exige <c>https</c> na busca dos metadados. Falso só onde o transporte em <c>http</c> é aceito.</summary>
    public bool RequireHttpsMetadata { get; internal set; } = true;
}
```

- [ ] **Passo 4: O adaptador preenche e valida**

Em `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakAdminOptions.cs`:

1. No `<summary>`/`<remarks>` de `PublicBaseUrl`, trocar a primeira linha do `<summary>` por:

```csharp
    /// <summary>Endereço público do Keycloak — o <c>KC_HOSTNAME</c>. Alimenta o emissor; nunca é discado.</summary>
```

e acrescentar ao fim do `<remarks>` dele, antes de `</remarks>`:

```csharp
    /// <para>
    /// O emissor (<see cref="Issuer"/>) serve a duas coisas: é o <c>aud</c> do client assertion e é o <c>iss</c> que a
    /// Api aceita nos tokens de quem a chama. As duas saem daqui para nunca divergirem.
    /// </para>
```

(o `<remarks>` de `PublicBaseUrl` é hoje um parágrafo só; envolva o texto existente num `<para>`.)

2. Trocar a propriedade `AssertionAudience` por estas três:

```csharp
    /// <summary>O emissor público do realm, sem barra final: <c>{PublicBaseUrl ?? BaseUrl}/realms/{Realm}</c>.</summary>
    public string Issuer =>
        $"{(string.IsNullOrWhiteSpace(PublicBaseUrl) ? BaseUrl : PublicBaseUrl).TrimEnd('/')}/realms/{Realm}";

    /// <summary>O <c>aud</c> do client assertion: o emissor público do realm.</summary>
    public string AssertionAudience => Issuer;

    /// <summary>Os metadados OIDC do realm, pelo endereço de transporte — o público pode não resolver daqui.</summary>
    public string MetadataAddress =>
        $"{BaseUrl.TrimEnd('/')}/realms/{Realm}/.well-known/openid-configuration";
```

Em `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakServiceCollectionExtensions.cs`:

1. Constante, no topo da classe:

```csharp
    /// <summary>O client público de demonstração do realm local. Aceito só em Development.</summary>
    private const string ClientDeDemonstracao = "identity-gateway-demo";
```

2. Logo depois do bloco `services.AddOptions<KeycloakAdminOptions>()…ValidateOnStart();`:

```csharp
        // O que a Api lê para validar o access token. Preenchida aqui, e não pela Api, porque o formato do emissor e
        // o endereço dos metadados são conhecimento do Keycloak (ADR-008). O Configure roda depois do Bind: o que a
        // configuração tentasse pôr nos três campos derivados seria sobrescrito — e o setter interno já não é
        // alcançado pelo binder.
        services.AddOptions<AccessTokenValidationOptions>()
            .Bind(configuration.GetSection(AccessTokenValidationOptions.SectionName))
            .Configure<IOptions<KeycloakAdminOptions>>((validacao, admin) =>
            {
                validacao.Issuer = admin.Value.Issuer;
                validacao.MetadataAddress = admin.Value.MetadataAddress;

                // A mesma regra que já é validada na subida: http só com AllowInsecureHttp, e ele só em Development.
                validacao.RequireHttpsMetadata = !admin.Value.AllowInsecureHttp;
            })
            .Validate(
                validacao => !string.IsNullOrWhiteSpace(validacao.Audience),
                "Keycloak:Auth:Audience é obrigatório.")
            .Validate(
                validacao => validacao.AllowedClients.All(client => !string.IsNullOrWhiteSpace(client)),
                "Keycloak:Auth:AllowedClients não aceita item vazio.")
            .Validate<IHostEnvironment>(
                ClientDeDemonstracaoSoEmDesenvolvimento,
                "Keycloak:Auth:AllowedClients traz um client aceito só no ambiente Development.")
            .ValidateOnStart();
```

3. Junto das outras regras privadas, no fim da classe:

```csharp
    // O client de device flow é público e existe só no realm local: fora de Development, aceitá-lo abriria a API ao
    // phishing de código de dispositivo. A mensagem não lista os clients configurados.
    private static bool ClientDeDemonstracaoSoEmDesenvolvimento(
        AccessTokenValidationOptions validacao, IHostEnvironment ambiente) =>
        ambiente.IsDevelopment()
        || !validacao.AllowedClients.Contains(ClientDeDemonstracao, StringComparer.Ordinal);
```

- [ ] **Passo 5: A lista no arquivo de Development**

Em `src/IdentityGateway.Api/appsettings.Development.json`, a seção `Keycloak` passa a ser:

```json
  "Keycloak": {
    "Admin": {
      "BaseUrl": "http://localhost:8081",
      "AllowInsecureHttp": true
    },
    "Auth": {
      "AllowedClients": [ "identity-gateway-demo" ]
    }
  }
```

O `appsettings.json` **não muda**: sem `Auth`.

- [ ] **Passo 6: Rodar e ver passar**

Run: `dotnet build IdentityGateway.slnx`
Expected: `0 Aviso(s)`, `0 Erro(s)`.

Run (Docker ligado — o fixture do assembly sobe mesmo para estes testes): `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter-class "*AccessTokenValidationOptionsTests"`
Expected: PASS — `total: 13` (onze `[Fact]` e a `[Theory]` com dois casos).

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter-class "*KeycloakAdminOptionsTests"`
Expected: PASS — os testes existentes do `AssertionAudience` continuam verdes: o valor não mudou, só passou a sair de `Issuer`.

Run: `dotnet test tests/IdentityGateway.ArchitectureTests`
Expected: PASS — inclusive `AApiNaoConheceOKeycloak` (a Api ainda nem usa a option) e `ListaDeClientsPermitidosSoNoAppsettingsDeDevelopment`.

- [ ] **Passo 7: 🧪 Provas por mutação**

Antes da primeira mutação, ponha no índice o que a tarefa escreveu (`git add src tests`). Reverta cada mutação com `git restore <arquivo mutado>`, que restaura do índice, e confira com `git diff --stat` vazio.

Run (mutações 1 a 5): `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter-class "*AccessTokenValidationOptionsTests"`
Run (mutação 6): `dotnet test tests/IdentityGateway.ArchitectureTests --filter-method "*ListaDeClientsPermitidosSoNoAppsettingsDeDevelopment"`

| # | Mutação | Vermelho esperado |
|---|---|---|
| 1 | Em `KeycloakAdminOptions.Issuer`, usar sempre `BaseUrl` (tirar o ramo do `PublicBaseUrl`) | `Issuer_ComPublicBaseUrl_EOMesmoTextoDoAudDoAssertion` (a primeira asserção), `MetadataAddress_SaiDoBaseUrlInternoENuncaDoPublico` — e, no resto da suíte, os testes contra o Keycloak real (`Invalid token audience`) |
| 2 | Em `KeycloakAdminOptions.MetadataAddress`, usar `Issuer` como base | `MetadataAddress_SaiDoBaseUrlInternoENuncaDoPublico`, `IssuerEMetadadosNaConfiguracao_SaoIgnorados` |
| 3 | No `Configure`, `validacao.RequireHttpsMetadata = false;` | `RequireHttpsMetadata_SegueOAllowInsecureHttp`, `IssuerEMetadadosNaConfiguracao_SaoIgnorados` |
| 4 | Apagar o `.Validate<IHostEnvironment>(ClientDeDemonstracaoSoEmDesenvolvimento, …)` | `ClientDeDemonstracaoForaDeDevelopment_FalhaAoValidarComMensagemNeutra` (os dois casos) |
| 5 | Em `ClientDeDemonstracaoSoEmDesenvolvimento`, trocar `ambiente.IsDevelopment()` por `!ambiente.IsProduction()` | `ClientDeDemonstracaoForaDeDevelopment_…("Staging")` |
| 6 | Em `src/IdentityGateway.Api/appsettings.json`, acrescentar `"Auth": { "AllowedClients": [ "identity-gateway-demo" ] }` dentro de `Keycloak` | `ListaDeClientsPermitidosSoNoAppsettingsDeDevelopment` |

Mutação **não** executada, registrada como equivalente quando isolada: tornar públicos os setters de `Issuer`, `MetadataAddress` e `RequireHttpsMetadata`. O `Configure` roda depois do `Bind` e sobrescreve o que a configuração puser; `IssuerEMetadadosNaConfiguracao_SaoIgnorados` continua verde. As duas proteções se cobrem, e só a remoção das duas é pega.

- [ ] **Passo 8: Commit**

```bash
git add src/IdentityGateway.Infrastructure src/IdentityGateway.Api/appsettings.Development.json tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/AccessTokenValidationOptionsTests.cs tests/IdentityGateway.ArchitectureTests/RegrasDoAmbienteLocalTests.cs
git commit -m "feat: AccessTokenValidationOptions preenchida pelo adaptador do Keycloak

A Api vai validar o access token sem conhecer o Keycloak: o adaptador
preenche uma option neutra (secao Keycloak:Auth) com o emissor publico, o
endereco interno dos metadados e RequireHttpsMetadata = !AllowInsecureHttp.
O emissor e a mesma propriedade que alimenta o aud do client assertion.
Da configuracao vem so a audiencia e a lista de clients (azp), que fica
fora do appsettings.json base e so traz o identity-gateway-demo em
Development; fora dele, a subida recusa o client de demonstracao. Lista
vazia sobe e fecha.

Mutacoes: emissor pelo BaseUrl, metadados pelo endereco publico,
RequireHttpsMetadata fixo, sem a recusa do demo, recusa so em Production
e a lista no appsettings base."
```

Run: `git log -1 --format=%B | grep -Eci "co-authored|generated with"`
Expected: `0`.

---

### Tarefa 7: A troca da autenticação — JwtBearer por metadados, emissor estrito, e o fim do HS256

Spec: §4.2 (JwtBearer, `OnAuthenticationFailed`, autorização global, Problem Details, "Sai"), §3.3 (os fatos do ASP.NET Core), §5.1 (linhas "Arquitetura", "Configuração", "Funcional, OIDC falso"), §5.4, §5.5, DT2, DT5, DT6.

É a tarefa em que o JWT simétrico do template deixa de existir. Depois dela, a Api só aceita token RS256 de um emissor cujas chaves ela busca por metadados — nos testes funcionais, um OIDC falso em loopback; no compose, o Keycloak. As checagens de `azp`, `typ` e `sub` entram na Tarefa 8.

O código de produção desta tarefa e da seguinte (`ValidacaoDoAccessToken`, `FormaDoAccessToken`, o handler de Problem Details), o `OidcFalso`, o `EmissorDeTeste`, o coletor de logs e a suíte negativa foram compilados com os analisadores do repositório e executados num host mínimo ao escrever este plano: os 34 casos da suíte, o HTTPS com certificado confiado pela impressão digital e os metadados frios (`401` em 5,1 s, com o aviso) passaram. O que **não** foi executado é a integração com o `Program.cs` e a `WebApplicationFactory` reais — é o que os passos abaixo provam.

**Arquivos:**
- Create: `src/IdentityGateway.Api/Authentication/ValidacaoDoAccessToken.cs`, `Authentication/AutenticacaoLogs.cs`
- Create: `src/IdentityGateway.Api/Authorization/Policies.cs`, `Authorization/RespostasDeAutorizacao.cs`, `Authorization/ProblemDetailsDeAutorizacao.cs`
- Modify: `src/IdentityGateway.Api/DependencyInjection.cs`, `Program.cs`, `Services/HttpCurrentUser.cs`, `Modules/TenantsModule.cs`, `appsettings.json`, `IdentityGateway.Api.csproj`
- Delete: `src/IdentityGateway.Api/Security/JwtTokenService.cs`, `src/IdentityGateway.Infrastructure/Configuration/JwtOptions.cs`
- Modify: `src/IdentityGateway.Infrastructure/DependencyInjection.cs` (sai o registro de `JwtOptions`), `Directory.Packages.props` (comentário)
- Create (testes funcionais): `Oidc/EmissorDeTeste.cs`, `Oidc/OidcFalso.cs`, `Logs/ColetorDeLogsDaApi.cs`, `EmissorEstritoTests.cs`, `OpcoesDoJwtBearerTests.cs`, `AutenticacaoNegativaTests.cs`, `EndpointsDeclaramAutorizacaoTests.cs`
- Rewrite: `tests/IdentityGateway.Api.FunctionalTests/IdentityGatewayApiFactory.cs`
- Modify: `tests/IdentityGateway.Api.FunctionalTests/SegurancaTests.cs`, `IdentityGateway.Api.FunctionalTests.csproj`
- Modify: `tests/IdentityGateway.ArchitectureTests/RegrasDaApiTests.cs`
- Modify (limpeza das chaves `Jwt:*`): `tests/IdentityGateway.Infrastructure.IntegrationTests/DependencyInjectionTests.cs`, `Provisioning/ComposicaoDoProvisionamento.cs`, `Identity/Keycloak/KeycloakHealthCheckTests.cs`

**Interfaces:**
- Consome: `AccessTokenValidationOptions` (Tarefa 6): `Issuer`, `MetadataAddress`, `RequireHttpsMetadata`, `Audience`, `AllowedClients`; `ICorrelationIdProvider.CorrelationId` (existente).
- Produz, na Api (`internal`, visíveis ao projeto funcional por `InternalsVisibleTo`):
  - `static class ValidacaoDoAccessToken` (namespace `IdentityGateway.Api.Authentication`): `const string Categoria = "IdentityGateway.Api.Authentication"`; `static readonly TimeSpan Tolerancia`, `PrazoDosMetadados`, `IntervaloDeRefresh`; `static void Configurar(JwtBearerOptions jwt, AccessTokenValidationOptions validacao)`; `static IssuerValidator EmissorEstrito(string esperado)`; `static bool EhFalhaDeChaveOuDeMetadados(Exception excecao)`.
  - `static partial class AutenticacaoLogs`: `ChavesIndisponiveis(ILogger, string tipo)` (2100, Warning), `TokenRecusado(ILogger, string tipo)` (2101, Debug), `FormaRecusada(ILogger, string motivo)` (2102, Debug), `NenhumClientPermitido(ILogger)` (2103, Warning).
  - `static class Policies` (namespace `IdentityGateway.Api.Authorization`): `const string PlatformAdmin = "PlatformAdmin"`.
  - `static class RespostasDeAutorizacao`: `static IResult Proibido(HttpContext contexto)`, `static IResult NaoAutenticado(HttpContext contexto)`, e as constantes `TipoDoProibido`, `TituloDoProibido`, `DetalheDoProibido`, `TipoDoNaoAutenticado`, `TituloDoNaoAutenticado`, `DetalheDoNaoAutenticado`.
  - `sealed class ProblemDetailsDeAutorizacao : IAuthorizationMiddlewareResultHandler`.
- Produz, nos testes funcionais (namespace `IdentityGateway.Api.FunctionalTests`):
  - `Oidc.EmissorDeTeste` (`internal`): `EmissorDeTeste(string emissor)`; `const string Kid`; `string Emissor`; `string Jwks`; `Dictionary<string, object?> Payload(Guid? sub = null, IReadOnlyCollection<string>? roles = null, string? tenantId = null)`; `string Emitir(Guid? sub = null, IReadOnlyCollection<string>? roles = null, string? tenantId = null, Action<Dictionary<string, object?>>? ajustar = null)`; `string Assinar(Dictionary<string, object?> payload, RSA? chave = null, string kid = Kid)`; `static string AssinarComHs256(Dictionary<string, object?> payload, byte[] segredo, string? kid = null)`; `static string SemAssinatura(Dictionary<string, object?> payload)`; `byte[] ChavePublicaEmPem()`.
  - `Oidc.OidcFalso` (`internal`, `IAsyncDisposable`): `static Task<OidcFalso> IniciarAsync(EmissorDeTeste emissor, bool https = false)`; `string BaseUrl`; `string EmissorAnunciado`; `X509Certificate2? Certificado`; `const string Realm = "identity-gateway"`.
  - `Logs.ColetorDeLogsDaApi` (`public`): `static ColetorDeLogsDaApi DoCanal(string canal)`; `IReadOnlyList<LogEvent> Eventos`; `IReadOnlyList<string> Textos`. E o método `ColetorEmMemoria(this LoggerSinkConfiguration, string canal)`, que a configuração do Serilog acha pelo nome.
  - `IdentityGatewayApiFactory`: `const string EnderecoPublicoDoKeycloak = "http://keycloak.publico.test:8081"`; `const string EmissorPublico`; `internal EmissorDeTeste Emissor`; `internal OidcFalso Oidc`; `ColetorDeLogsDaApi Logs`; e, **com a mesma assinatura de hoje**, `HttpClient CreateClientAutenticado(Guid? usuarioId = null, params string[] roles)` e `Task ComEscopoAsync(Func<AppDbContext, Task> acao)`.

- [ ] **Passo 1: As regras de arquitetura que a remoção do HS256 precisa satisfazer**

Em `tests/IdentityGateway.ArchitectureTests/RegrasDaApiTests.cs`, acrescentar (o helper `CamadasDeProducao` junto dos campos; os testes, antes de `TiposEscritosAMao`):

```csharp
    private static readonly Assembly[] CamadasDeProducao =
    [
        typeof(IdentityGateway.Domain.AssemblyMarker).Assembly,
        typeof(IdentityGateway.Application.AssemblyMarker).Assembly,
        typeof(IdentityGateway.Infrastructure.AssemblyMarker).Assembly,
        Api,
    ];

    /// <summary>
    /// Nenhuma camada de produção assina ou valida token com chave simétrica.
    /// </summary>
    /// <remarks>
    /// A Gateway não emite token, e quem valida usa as chaves públicas do provedor. Uma <c>SymmetricSecurityKey</c> em
    /// produção é a chave de desenvolvimento publicada voltando — o risco que a troca para o Keycloak existe para
    /// matar. O NetArchTest enxerga corpos de método, e este é um <b>tipo</b>: a regra não nasce vacuosa. (Ela não vê
    /// propriedades como <c>IssuerSigningKey</c>; essas são conferidas nas opções resolvidas, em execução.)
    /// </remarks>
    [Fact]
    public void NenhumaCamadaDeProducaoUsaChaveSimetrica()
    {
        foreach (Assembly camada in CamadasDeProducao)
        {
            ArchTestResult resultado = Types.InAssembly(camada)
                .Should()
                .NotHaveDependencyOn("Microsoft.IdentityModel.Tokens.SymmetricSecurityKey")
                .GetResult();

            resultado.Should().NaoTerViolacao(
                $"{camada.GetName().Name}: token da Gateway é RS256 do provedor de identidade, validado por JWKS");
        }
    }

    /// <summary>
    /// A Api não usa o pacote legado <c>System.IdentityModel.Tokens.Jwt</c>.
    /// </summary>
    /// <remarks>
    /// Era por ele que o <c>JwtTokenService</c> emitia token. O que a Api precisa de JWT hoje é ler um token já
    /// validado, e isso é <c>Microsoft.IdentityModel.JsonWebTokens</c>. O pacote continua copiado (vem com o
    /// JwtBearer); a regra é sobre o código da Api depender dele.
    /// </remarks>
    [Fact]
    public void Api_NaoUsaOPacoteJwtLegado()
    {
        ArchTestResult resultado = Types.InAssembly(Api)
            .Should()
            .NotHaveDependencyOn("System.IdentityModel.Tokens.Jwt")
            .GetResult();

        resultado.Should().NaoTerViolacao("a Api não emite token; o claim do sujeito é o texto \"sub\"");
    }

    /// <summary>
    /// Nenhuma camada liga o registro de dados pessoais ou de tokens inteiros da biblioteca de identidade.
    /// </summary>
    /// <remarks>
    /// <c>ShowPII</c> e <c>LogCompleteSecurityArtifact</c> são propriedades estáticas de
    /// <c>IdentityModelEventSource</c>: ligadas, as mensagens de falha passam a trazer o token e os claims — e-mail
    /// inclusive. Ninguém em produção tem motivo para tocar nesse tipo.
    /// </remarks>
    [Fact]
    public void NenhumaCamadaLigaPiiDaBibliotecaDeIdentidade()
    {
        foreach (Assembly camada in CamadasDeProducao)
        {
            ArchTestResult resultado = Types.InAssembly(camada)
                .Should()
                .NotHaveDependencyOn("Microsoft.IdentityModel.Logging.IdentityModelEventSource")
                .GetResult();

            resultado.Should().NaoTerViolacao($"{camada.GetName().Name}: ShowPII levaria tokens e claims para o log");
        }
    }

    /// <summary>
    /// A Api não transforma claims depois da validação.
    /// </summary>
    /// <remarks>
    /// ADR-004: os claims vêm exclusivamente dos mappers do provedor. Um <c>IClaimsTransformation</c> acrescentaria
    /// papel ou tenant do lado de cá — autorização decidida por algo que o token não diz.
    /// </remarks>
    [Fact]
    public void Api_NaoTransformaClaims()
    {
        string[] transformadores =
        [
            .. Api.GetTypes()
                .Where(tipo => tipo.GetInterfaces().Any(contrato =>
                    contrato.FullName == "Microsoft.AspNetCore.Authentication.IClaimsTransformation"))
                .Select(tipo => tipo.Name),
        ];

        transformadores.Should().BeEmpty("os claims vêm só do token (ADR-004)");
    }
```

Run: `dotnet test tests/IdentityGateway.ArchitectureTests --filter-class "*RegrasDaApiTests"`
Expected: FAIL em `NenhumaCamadaDeProducaoUsaChaveSimetrica` (IdentityGateway.Api: `DependencyInjection` e `JwtTokenService`) e em `Api_NaoUsaOPacoteJwtLegado` (`JwtTokenService`, `DependencyInjection`). É o vermelho que prova que as duas regras enxergam o que proíbem. As outras duas passam: travam o que ainda não existe.

- [ ] **Passo 2: A infraestrutura de teste — emissor, OIDC falso e coletor de logs**

Em `tests/IdentityGateway.Api.FunctionalTests/IdentityGateway.Api.FunctionalTests.csproj`, no `ItemGroup` dos pacotes, acrescentar:

```xml

    <!--
      Usados direto no código dos testes: as opções do JwtBearer (conferidas em execução) e o sink em memória do
      Serilog. Vêm transitivos pela Api, mas dependência usada no código é dependência declarada.
    -->
    <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" />
    <PackageReference Include="Serilog.AspNetCore" />
```

`tests/IdentityGateway.Api.FunctionalTests/Oidc/EmissorDeTeste.cs`:

```csharp
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdentityGateway.Api.FunctionalTests.Oidc;

/// <summary>
/// Emite tokens com a forma dos do Keycloak, assinados com uma chave RSA de teste — e também os malformados.
/// </summary>
/// <remarks>
/// <para>
/// <b>O JWT é montado à mão</b> (cabeçalho, payload, assinatura), e não por uma biblioteca de emissão: os casos
/// negativos precisam de coisas que nenhuma biblioteca deixa escrever — <c>alg: none</c>, <c>azp</c> em array, token
/// sem <c>exp</c>, HS256 com a chave pública como segredo.
/// </para>
/// <para>
/// <b>O payload padrão imita o token real</b> do client de demonstração (os tipos de cada claim são conferidos contra
/// um token de verdade na coleção com Keycloak): <c>aud</c> texto, <c>sub</c> GUID, <c>typ</c> <c>Bearer</c>,
/// <c>acr</c> texto, <c>roles</c> array, <c>tenant_id</c> texto, 5 minutos.
/// </para>
/// <para>
/// A chave nasce em memória, a cada execução. Nunca um <c>.pem</c> versionado.
/// </para>
/// </remarks>
internal sealed class EmissorDeTeste : IDisposable
{
    /// <summary>O <c>kid</c> fixo da chave de teste, publicado no JWKS do OIDC falso.</summary>
    public const string Kid = "chave-de-teste";

    public const string AudienciaDaGateway = "identity-gateway-api";

    public const string ClientDeDemonstracao = "identity-gateway-demo";

    private readonly RSA _rsa = RSA.Create(2048);

    /// <param name="emissor">O <c>iss</c> dos tokens: o emissor público que a Api está configurada para aceitar.</param>
    public EmissorDeTeste(string emissor)
    {
        Emissor = emissor;

        RSAParameters publica = _rsa.ExportParameters(includePrivateParameters: false);
        Jwks = JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = "RS256",
                    kid = Kid,
                    n = Base64Url.EncodeToString(publica.Modulus),
                    e = Base64Url.EncodeToString(publica.Exponent),
                },
            },
        });
    }

    public string Emissor { get; }

    /// <summary>O documento JWKS com a chave pública, como o endpoint de certificados o serve.</summary>
    public string Jwks { get; }

    /// <summary>A chave pública em PEM — para o ataque clássico de usá-la como segredo HS256.</summary>
    public byte[] ChavePublicaEmPem() => Encoding.ASCII.GetBytes(_rsa.ExportSubjectPublicKeyInfoPem());

    public void Dispose() => _rsa.Dispose();

    /// <summary>O payload de um token válido. O teste o altera antes de assinar.</summary>
    public Dictionary<string, object?> Payload(Guid? sub = null, IReadOnlyCollection<string>? roles = null, string? tenantId = null)
    {
        long agora = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Dictionary<string, object?> payload = new()
        {
            ["exp"] = agora + 300,
            ["iat"] = agora,
            ["auth_time"] = agora,
            ["jti"] = Guid.NewGuid().ToString(),
            ["iss"] = Emissor,
            ["aud"] = AudienciaDaGateway,
            ["sub"] = (sub ?? Guid.NewGuid()).ToString("D"),
            ["typ"] = "Bearer",
            ["azp"] = ClientDeDemonstracao,
            ["sid"] = Guid.NewGuid().ToString("N"),
            ["acr"] = "1",
            ["scope"] = "openid",
        };

        if (tenantId is not null)
        {
            payload["tenant_id"] = tenantId;
        }

        // Como o Keycloak: usuário sem papel do catálogo recebe o token SEM o claim.
        if (roles is { Count: > 0 })
        {
            payload["roles"] = roles;
        }

        return payload;
    }

    /// <summary>Um token válido, com as alterações que o teste pedir no payload.</summary>
    public string Emitir(
        Guid? sub = null, IReadOnlyCollection<string>? roles = null, string? tenantId = null,
        Action<Dictionary<string, object?>>? ajustar = null)
    {
        Dictionary<string, object?> payload = Payload(sub, roles, tenantId);
        ajustar?.Invoke(payload);

        return Assinar(payload);
    }

    /// <summary>Assina com RS256. Sem <paramref name="chave"/>, usa a chave de teste, que a Api conhece pelo JWKS.</summary>
    public string Assinar(Dictionary<string, object?> payload, RSA? chave = null, string kid = Kid)
    {
        string conteudo = $"{Codificar(new { alg = "RS256", typ = "JWT", kid })}.{Codificar(payload)}";
        byte[] assinatura = (chave ?? _rsa).SignData(
            Encoding.ASCII.GetBytes(conteudo), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{conteudo}.{Base64Url.EncodeToString(assinatura)}";
    }

    /// <summary>Um token HS256, assinado com o segredo dado.</summary>
    public static string AssinarComHs256(Dictionary<string, object?> payload, byte[] segredo, string? kid = null)
    {
        object cabecalho = kid is null ? new { alg = "HS256", typ = "JWT" } : new { alg = "HS256", typ = "JWT", kid };
        string conteudo = $"{Codificar(cabecalho)}.{Codificar(payload)}";
        byte[] assinatura = HMACSHA256.HashData(segredo, Encoding.ASCII.GetBytes(conteudo));

        return $"{conteudo}.{Base64Url.EncodeToString(assinatura)}";
    }

    /// <summary>Um token <c>alg: none</c>, sem assinatura.</summary>
    public static string SemAssinatura(Dictionary<string, object?> payload) =>
        $"{Codificar(new { alg = "none", typ = "JWT" })}.{Codificar(payload)}.";

    private static string Codificar(object valor) =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(valor));
}
```

`tests/IdentityGateway.Api.FunctionalTests/Oidc/OidcFalso.cs`:

```csharp
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace IdentityGateway.Api.FunctionalTests.Oidc;

/// <summary>
/// Um provedor OIDC mínimo em loopback: serve o discovery e o JWKS da chave de teste, e nada mais.
/// </summary>
/// <remarks>
/// <para>
/// <b>É o que deixa a Api de verdade validar tokens sem Keycloak</b>, só por configuração: o <c>BaseUrl</c> aponta
/// para cá, e o JwtBearer busca os metadados e as chaves como faria em produção. Nenhum esquema de autenticação é
/// trocado, nenhuma chave é injetada nas opções.
/// </para>
/// <para>
/// <b>O discovery anuncia um emissor diferente do que a Api aceita</b> (<see cref="EmissorAnunciado"/> é o endereço
/// deste servidor; a Api é configurada com outro, o "público"). É de propósito: a biblioteca aceitaria o emissor do
/// discovery mesmo com <c>ValidIssuer</c> configurado, e só com os dois diferentes um teste distingue o
/// <c>IssuerValidator</c> estrito da validação padrão.
/// </para>
/// <para>
/// A tudo o que não é discovery nem JWKS, responde <c>404</c> — inclusive ao token endpoint, e por isso o
/// <c>/health/ready</c> da Api fica <c>Unhealthy</c> nos testes funcionais, como já ficava.
/// </para>
/// </remarks>
internal sealed class OidcFalso : IAsyncDisposable
{
    public const string Realm = "identity-gateway";

    private readonly WebApplication _servidor;

    private OidcFalso(WebApplication servidor, string baseUrl, X509Certificate2? certificado)
    {
        _servidor = servidor;
        BaseUrl = baseUrl;
        Certificado = certificado;
    }

    /// <summary>O endereço deste servidor, sem barra final: o <c>Keycloak:Admin:BaseUrl</c> da Api sob teste.</summary>
    public string BaseUrl { get; }

    /// <summary>O emissor que o discovery anuncia. <b>Não</b> é o que a Api aceita.</summary>
    public string EmissorAnunciado => $"{BaseUrl}/realms/{Realm}";

    /// <summary>O certificado autoassinado, quando o servidor é HTTPS.</summary>
    public X509Certificate2? Certificado { get; }

    public static async Task<OidcFalso> IniciarAsync(EmissorDeTeste emissor, bool https = false)
    {
        ArgumentNullException.ThrowIfNull(emissor);

        X509Certificate2? certificado = https ? CertificadoAutoassinado() : null;

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port: 0, escuta =>
        {
            if (certificado is not null)
            {
                escuta.UseHttps(certificado);
            }
        }));

        WebApplication servidor = builder.Build();
        string esquema = https ? "https" : "http";
        string? baseUrl = null;

        servidor.MapGet($"/realms/{Realm}/.well-known/openid-configuration", () => Results.Json(new
        {
            issuer = $"{baseUrl}/realms/{Realm}",
            jwks_uri = $"{baseUrl}/realms/{Realm}/protocol/openid-connect/certs",
            token_endpoint = $"{baseUrl}/realms/{Realm}/protocol/openid-connect/token",
            authorization_endpoint = $"{baseUrl}/realms/{Realm}/protocol/openid-connect/auth",
            id_token_signing_alg_values_supported = AlgoritmosAnunciados,
            response_types_supported = TiposDeRespostaAnunciados,
            subject_types_supported = TiposDeSujeitoAnunciados,
        }));

        servidor.MapGet(
            $"/realms/{Realm}/protocol/openid-connect/certs", () => Results.Text(emissor.Jwks, "application/json"));

        await servidor.StartAsync();

        // A porta é escolhida pelo sistema; depois de subir, o Kestrel diz qual foi.
        int porta = new Uri(servidor.Urls.Single()).Port;
        baseUrl = $"{esquema}://127.0.0.1:{porta}";

        return new OidcFalso(servidor, baseUrl, certificado);
    }

    public async ValueTask DisposeAsync()
    {
        await _servidor.DisposeAsync();
        Certificado?.Dispose();
    }

    private static readonly string[] AlgoritmosAnunciados = ["RS256"];

    private static readonly string[] TiposDeRespostaAnunciados = ["code"];

    private static readonly string[] TiposDeSujeitoAnunciados = ["public"];

    /// <summary>Certificado autoassinado para o loopback, gerado em memória.</summary>
    /// <remarks>
    /// Exportado e reimportado como PKCS#12: no Windows, o Kestrel não consegue usar a chave efêmera que o
    /// <c>CreateSelfSigned</c> devolve ("No credentials are available in the security package").
    /// </remarks>
    private static X509Certificate2 CertificadoAutoassinado()
    {
        using var rsa = RSA.Create(2048);
        CertificateRequest pedido = new("CN=oidc-falso", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        SubjectAlternativeNameBuilder nomes = new();
        nomes.AddIpAddress(IPAddress.Loopback);
        pedido.CertificateExtensions.Add(nomes.Build());

        using X509Certificate2 efemero = pedido.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        return X509CertificateLoader.LoadPkcs12(efemero.Export(X509ContentType.Pfx), password: null);
    }
}
```

`tests/IdentityGateway.Api.FunctionalTests/Logs/ColetorDeLogsDaApi.cs`:

```csharp
using System.Collections.Concurrent;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace IdentityGateway.Api.FunctionalTests.Logs;

/// <summary>
/// Um sink do Serilog em memória: o que a Api registraria, visto pelo teste.
/// </summary>
/// <remarks>
/// <para>
/// <b>Entra por configuração</b> (<c>Serilog:Using</c> e <c>Serilog:WriteTo</c>), pelo mesmo
/// <c>ReadFrom.Configuration</c> do <c>Program.cs</c>: a Api usa o Serilog no lugar dos provedores de log do host, e um
/// <c>ILoggerProvider</c> acrescentado pelo teste seria ignorado.
/// </para>
/// <para>
/// <b>Um canal por factory.</b> As classes de teste rodam em paralelo, cada uma com o seu host; o canal — um texto
/// aleatório passado na configuração — separa os eventos de cada um.
/// </para>
/// </remarks>
public sealed class ColetorDeLogsDaApi : ILogEventSink
{
    private static readonly ConcurrentDictionary<string, ColetorDeLogsDaApi> Canais = new(StringComparer.Ordinal);

    private readonly ConcurrentQueue<LogEvent> _eventos = new();

    public static ColetorDeLogsDaApi DoCanal(string canal) => Canais.GetOrAdd(canal, _ => new ColetorDeLogsDaApi());

    /// <summary>Os eventos registrados até agora, na ordem.</summary>
    public IReadOnlyList<LogEvent> Eventos => [.. _eventos];

    /// <summary>Cada evento como texto: a mensagem renderizada, as propriedades e a exceção inteira.</summary>
    /// <remarks>É onde um teste de vazamento procura: um segredo pode estar na mensagem, numa propriedade ou na exceção.</remarks>
    public IReadOnlyList<string> Textos =>
    [
        .. _eventos.Select(evento =>
            $"{evento.Level} {evento.RenderMessage(System.Globalization.CultureInfo.InvariantCulture)} "
            + $"{string.Join(' ', evento.Properties.Select(par => $"{par.Key}={par.Value}"))} {evento.Exception}"),
    ];

    public void Emit(LogEvent logEvent) => _eventos.Enqueue(logEvent);
}

/// <summary>O método que o <c>ReadFrom.Configuration</c> do Serilog acha pelo nome em <c>Serilog:WriteTo</c>.</summary>
public static class ColetorDeLogsDaApiExtensions
{
    public static LoggerConfiguration ColetorEmMemoria(this LoggerSinkConfiguration sinks, string canal)
    {
        ArgumentNullException.ThrowIfNull(sinks);

        return sinks.Sink(ColetorDeLogsDaApi.DoCanal(canal));
    }
}
```

- [ ] **Passo 3: Reescrever a factory dos testes funcionais**

`tests/IdentityGateway.Api.FunctionalTests/IdentityGatewayApiFactory.cs`, inteiro:

```csharp
using System.Net.Http.Headers;
using System.Security.Cryptography;
using IdentityGateway.Api.FunctionalTests.Logs;
using IdentityGateway.Api.FunctionalTests.Oidc;
using IdentityGateway.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// Sobe a Api inteira em memória, com PostgreSQL e Redis em container e um provedor OIDC falso em loopback.
/// </summary>
/// <remarks>
/// <para>
/// <b>A Api de verdade, não uma montagem de teste.</b> O <c>WebApplicationFactory</c> executa o <c>Program.cs</c>
/// real — os mesmos middlewares, o mesmo pipeline de behaviors, a mesma DI. O que se substitui é só a
/// configuração: as connection strings apontam para os containers, e o <c>Keycloak:Admin:BaseUrl</c>, para o OIDC
/// falso.
/// </para>
/// <para>
/// <b>A autenticação é a de produção.</b> O JwtBearer busca os metadados e as chaves no OIDC falso, por HTTP, como
/// buscaria no Keycloak; os tokens dos testes são assinados com a chave que esse servidor publica. Nenhum esquema de
/// autenticação é trocado e nenhuma chave é posta nas opções — um teste aqui exercita emissor, audiência, prazo,
/// algoritmo e assinatura de verdade.
/// </para>
/// <para>
/// <b>O emissor que a Api aceita é diferente do que o discovery anuncia</b> (<see cref="EmissorPublico"/> contra o
/// endereço do OIDC falso). É a mesma separação do compose — endereço público e de transporte —, e é o que deixa a
/// suíte negativa distinguir o emissor estrito da validação padrão da biblioteca.
/// </para>
/// <para>
/// <b>Sem depender do docker-compose de desenvolvimento</b>: os containers sobem e caem com a suíte, e nenhum serviço
/// local precisa estar rodando. Quem prova a Api contra o Keycloak real é a <c>ApiComKeycloakFactory</c>.
/// </para>
/// </remarks>
public sealed class IdentityGatewayApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>O <c>Keycloak:Admin:PublicBaseUrl</c> dos testes. Não resolve — e não precisa: nunca é discado.</summary>
    public const string EnderecoPublicoDoKeycloak = "http://keycloak.publico.test:8081";

    /// <summary>O único emissor que a Api sob teste aceita.</summary>
    public const string EmissorPublico = EnderecoPublicoDoKeycloak + "/realms/identity-gateway";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("identitygateway_functional")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

    private readonly string _canalDeLogs = Guid.NewGuid().ToString("N");

    private OidcFalso? _oidc;

    /// <summary>
    /// Chave fictícia do service account: a Api valida a configuração do Keycloak na subida, mas nenhum teste
    /// funcional obtém o token do service account. Gerada em memória — nunca um <c>.pem</c> versionado.
    /// </summary>
    private static readonly string ChaveFicticia = GerarChave();

    private static string GerarChave()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }

    /// <summary>Quem assina os tokens dos testes, com a chave que o OIDC falso publica.</summary>
    internal EmissorDeTeste Emissor { get; } = new(EmissorPublico);

    /// <summary>O provedor OIDC falso para o qual a Api aponta.</summary>
    internal OidcFalso Oidc =>
        _oidc ?? throw new InvalidOperationException("O OIDC falso só existe depois do InitializeAsync.");

    /// <summary>O que esta Api registrou em log, visto pelo teste.</summary>
    public ColetorDeLogsDaApi Logs => ColetorDeLogsDaApi.DoCanal(_canalDeLogs);

    public async ValueTask InitializeAsync()
    {
        // O OIDC falso sobe ANTES de qualquer acesso a Services ou CreateClient: o WebApplicationFactory aplica as
        // UseSetting de forma preguiçosa, e o BaseUrl (que leva a porta escolhida pelo sistema) precisa existir quando
        // ele as aplicar.
        _oidc = await OidcFalso.IniciarAsync(Emissor);

        // Em paralelo: são independentes, e subir em série dobra o tempo de arranque da suíte.
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

        // Aplica as migrations de verdade — é o mesmo caminho que a aplicação usa em produção. EnsureCreated
        // montaria o schema do modelo e passaria mesmo com a migration quebrada.
        using IServiceScope scope = Services.CreateScope();
        AppDbContext contexto = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await contexto.Database.MigrateAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());

        if (_oidc is not null)
        {
            await _oidc.DisposeAsync();
        }

        Emissor.Dispose();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");

        // Só a configuração é substituída, não o registro de serviços: trocar implementação aqui faria o teste
        // exercitar uma composição que não existe em produção.
        builder.UseSetting("Database:ConnectionString", _postgres.GetConnectionString());
        builder.UseSetting("Redis:ConnectionString", _redis.GetConnectionString());

        // O despachante do outbox fica desligado nos testes funcionais. Ele competiria com o teste pela mesma
        // tabela: um teste que confira a mensagem gerada por um caso de uso veria a limpeza de processadas
        // antigas apagá-la entre a requisição e a asserção — uma falha intermitente, dependente de tempo, que
        // apareceria na CI e não aqui. Quem exercita o despachante é o teste de integração, que o chama
        // diretamente. Note que isto continua sendo configuração, não troca de registro.
        builder.UseSetting("Outbox:Enabled", "false");

        // O "Keycloak" desta suíte é o OIDC falso: serve o discovery e o JWKS, e responde 404 ao resto. A validação
        // do token funciona de verdade; o token do service account não sai (o token endpoint não existe), e por isso
        // o /health/ready fica Unhealthy aqui — o ready com Keycloak é coberto pelos testes de integração, pela
        // coleção com Keycloak real e pelo job de compose da CI.
        builder.UseSetting("Keycloak:Admin:BaseUrl", Oidc.BaseUrl);

        // Diferente do BaseUrl de propósito: o emissor aceito sai daqui, e os metadados, do BaseUrl. O
        // AllowInsecureHttp e a lista de clients (identity-gateway-demo) vêm do appsettings.Development.json.
        builder.UseSetting("Keycloak:Admin:PublicBaseUrl", EnderecoPublicoDoKeycloak);
        builder.UseSetting("Keycloak:Admin:PrivateKeyPem", ChaveFicticia);

        // O coletor de logs em memória, pelo mesmo ReadFrom.Configuration do Program.cs. É configuração, não troca de
        // registro; o índice alto não colide com os sinks dos appsettings.
        builder.UseSetting("Serilog:Using:0", typeof(ColetorDeLogsDaApi).Assembly.GetName().Name);
        builder.UseSetting("Serilog:WriteTo:9:Name", nameof(ColetorDeLogsDaApiExtensions.ColetorEmMemoria));
        builder.UseSetting("Serilog:WriteTo:9:Args:canal", _canalDeLogs);
    }

    /// <summary>
    /// Cria um cliente HTTP com um token válido no cabeçalho <c>Authorization</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Um token de verdade, validado de verdade</b> — e não um handler de autenticação falso, que faria os testes
    /// passarem sem nunca exercitar emissor, audiência, expiração e assinatura. O token tem a forma do que o client
    /// de demonstração do Keycloak emite (<see cref="EmissorDeTeste"/>).
    /// </para>
    /// </remarks>
    /// <param name="usuarioId">
    /// O usuário do token (o <c>sub</c>), ou nulo para gerar um. É este identificador que a auditoria grava em
    /// <c>CreatedBy</c>, então um teste que confira autoria precisa informá-lo.
    /// </param>
    /// <param name="roles">Os papéis do claim <c>roles</c>. Sem nenhum, o token sai sem o claim.</param>
    public HttpClient CreateClientAutenticado(Guid? usuarioId = null, params string[] roles)
    {
        HttpClient cliente = CreateClient();

        cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", Emissor.Emitir(usuarioId ?? Guid.CreateVersion7(), roles));

        return cliente;
    }

    /// <summary>
    /// Executa uma ação com um escopo de DI próprio.
    /// </summary>
    /// <remarks>
    /// Para preparar estado que não tem endpoint que o crie, e para conferir o que foi persistido: inserir ou
    /// ler por SQL cru deixaria o teste dependente do nome das colunas em vez do modelo.
    /// </remarks>
    public async Task ComEscopoAsync(Func<AppDbContext, Task> acao)
    {
        ArgumentNullException.ThrowIfNull(acao);

        using IServiceScope scope = Services.CreateScope();
        AppDbContext contexto = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await acao(contexto);
    }
}
```

- [ ] **Passo 4: Escrever os testes**

`tests/IdentityGateway.Api.FunctionalTests/EmissorEstritoTests.cs` (sem fixture: não sobe container nem host):

```csharp
using IdentityGateway.Api.Authentication;
using Microsoft.IdentityModel.Tokens;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// O emissor aceito é um texto só, comparado por igualdade ordinal.
/// </summary>
/// <remarks>
/// A função é a única barreira de emissor que existe: com metadados, a biblioteca aceitaria o emissor que o discovery
/// anuncia, com ou sem <c>ValidIssuer</c>. Por isso ela tem teste próprio, fora do HTTP.
/// </remarks>
public sealed class EmissorEstritoTests
{
    private const string Esperado = "https://sso.exemplo.test/realms/identity-gateway";

    private static string Validar(string emissor) =>
        ValidacaoDoAccessToken.EmissorEstrito(Esperado)(emissor, securityToken: null!, validationParameters: null!);

    [Fact]
    public void EmissorIgualAoConfigurado_EAceito()
    {
        Validar(Esperado).Should().Be(Esperado);
    }

    [Theory]
    [InlineData("https://sso.exemplo.test/realms/identity-gateway/")]       // barra final
    [InlineData("https://sso.exemplo.test/realms/identity-gateway-outro")]  // o esperado é prefixo
    [InlineData("https://sso.exemplo.test/realms/identity")]                // é prefixo do esperado
    [InlineData("HTTPS://SSO.EXEMPLO.TEST/REALMS/IDENTITY-GATEWAY")]        // só a caixa muda
    [InlineData("http://sso.exemplo.test/realms/identity-gateway")]         // outro esquema
    [InlineData("https://keycloak.interno.test/realms/identity-gateway")]   // o endereço de transporte
    [InlineData("")]
    public void QualquerOutroTexto_ERecusadoComOEmissorNaExcecao(string emissor)
    {
        Action validar = () => Validar(emissor);

        // SecurityTokenInvalidIssuerException, e não outra: é a que a biblioteca trata como recuperável.
        validar.Should().Throw<SecurityTokenInvalidIssuerException>()
            .Which.InvalidIssuer.Should().Be(emissor);
    }
}
```

`tests/IdentityGateway.Api.FunctionalTests/OpcoesDoJwtBearerTests.cs`:

```csharp
using IdentityGateway.Api.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// As opções do JwtBearer como o host as resolveu — não como o código parece configurá-las.
/// </summary>
/// <remarks>
/// <b>Em execução, e não por regra estática.</b> <c>IssuerSigningKey</c>, <c>SignatureValidator</c> e os
/// <c>Validate*</c> são propriedades de <c>TokenValidationParameters</c>: o teste de arquitetura enxerga tipos, não
/// membros, e uma regra sobre elas ficaria verde sem conferir nada. Aqui o valor é lido depois de todos os
/// <c>Configure</c> e <c>PostConfigure</c>, que é o que a validação de fato usa.
/// </remarks>
public sealed class OpcoesDoJwtBearerTests(IdentityGatewayApiFactory factory) : IClassFixture<IdentityGatewayApiFactory>
{
    private JwtBearerOptions Resolvidas() => factory.Services
        .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);

    [Fact]
    public void AsChaves_VemSoDosMetadados()
    {
        // Uma chave fixada aqui valeria junto com as do provedor — ou no lugar delas.
        TokenValidationParameters parametros = Resolvidas().TokenValidationParameters;

        parametros.IssuerSigningKey.Should().BeNull();
        (parametros.IssuerSigningKeys ?? []).Should().BeEmpty();
        parametros.SignatureValidator.Should().BeNull();
        parametros.IssuerSigningKeyResolver.Should().BeNull();
        Resolvidas().ConfigurationManager.Should().NotBeNull("as chaves vêm do JWKS anunciado pelos metadados");
    }

    [Fact]
    public void AValidacao_EstaLigadaPorInteiroESoAceitaRs256()
    {
        TokenValidationParameters parametros = Resolvidas().TokenValidationParameters;

        parametros.ValidateIssuer.Should().BeTrue();
        parametros.IssuerValidator.Should().NotBeNull("o ValidIssuer sozinho não restringe");
        parametros.ValidateAudience.Should().BeTrue();
        parametros.ValidAudience.Should().Be("identity-gateway-api");
        parametros.ValidateLifetime.Should().BeTrue();
        parametros.RequireExpirationTime.Should().BeTrue();
        parametros.ValidateIssuerSigningKey.Should().BeTrue();
        parametros.ValidAlgorithms.Should().Equal(SecurityAlgorithms.RsaSha256);
        parametros.ClockSkew.Should().Be(TimeSpan.FromSeconds(30));
        parametros.NameClaimType.Should().Be("sub");
        parametros.RoleClaimType.Should().Be("roles");
    }

    [Fact]
    public void OsMetadados_VemPeloEnderecoDeTransporteComPrazoCurto()
    {
        JwtBearerOptions jwt = Resolvidas();

        jwt.Authority.Should().BeNull("Authority amarraria o endereço dos metadados ao emissor aceito");
        jwt.MetadataAddress.Should().Be(
            $"{factory.Oidc.BaseUrl}/realms/identity-gateway/.well-known/openid-configuration");
        jwt.BackchannelTimeout.Should().Be(ValidacaoDoAccessToken.PrazoDosMetadados).And.Be(TimeSpan.FromSeconds(5));
        jwt.RefreshInterval.Should().Be(TimeSpan.FromSeconds(30));

        // Em Development, com AllowInsecureHttp: é o único caso em que os metadados vêm por http.
        jwt.RequireHttpsMetadata.Should().BeFalse();
    }

    [Fact]
    public void AResposta_NaoDetalhaOErroEOsClaimsChegamComoOEmissorOsEscreveu()
    {
        JwtBearerOptions jwt = Resolvidas();

        jwt.IncludeErrorDetails.Should().BeFalse("o WWW-Authenticate ecoaria o iss e o aud recusados");
        jwt.MapInboundClaims.Should().BeFalse("senão o claim roles vira a URI longa e a policy nunca casa");
    }

    [Fact]
    public async Task HaUmEsquemaDeAutenticacaoSo()
    {
        // Um segundo esquema — cookie, chave de API, outro bearer — seria um segundo caminho para dentro, com outra
        // validação. Se um dia existir, entra por decisão registrada, e este teste é quem pergunta.
        IAuthenticationSchemeProvider esquemas = factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        IEnumerable<AuthenticationScheme> todos = await esquemas.GetAllSchemesAsync();

        todos.Select(esquema => esquema.Name).Should().Equal(JwtBearerDefaults.AuthenticationScheme);
    }
}
```

`tests/IdentityGateway.Api.FunctionalTests/EndpointsDeclaramAutorizacaoTests.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// Todo endpoint diz quem pode chamá-lo: uma policy nomeada, ou o anonimato declarado.
/// </summary>
/// <remarks>
/// A policy de fallback (usuário autenticado) cobre o endpoint que alguém esquecer de proteger — mas "autenticado" é
/// qualquer papel, de qualquer tenant. Este teste faz do esquecimento um vermelho: endpoint novo sem
/// <c>RequireAuthorization(policy)</c> nem <c>AllowAnonymous()</c> reprova aqui. O que ele <b>não</b> prova é a própria
/// fallback: quem prova é o caminho não mapeado sem token, em <c>SegurancaTests</c>.
/// </remarks>
public sealed class EndpointsDeclaramAutorizacaoTests(IdentityGatewayApiFactory factory)
    : IClassFixture<IdentityGatewayApiFactory>
{
    [Fact]
    public void TodoEndpoint_TemPolicyNomeadaOuAnonimatoDeclarado()
    {
        IReadOnlyList<Endpoint> endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        string[] semDeclaracao =
        [
            .. endpoints
                .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null)
                .Where(endpoint => !endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                    .Any(dado => !string.IsNullOrEmpty(dado.Policy)))
                .Select(endpoint => endpoint.DisplayName ?? endpoint.ToString()!),
        ];

        endpoints.Should().NotBeEmpty("sem endpoints, a regra passaria vazia");
        semDeclaracao.Should().BeEmpty("endpoint sem policy nomeada nem AllowAnonymous fica só com a fallback");
    }

    [Fact]
    public void AsRotasDeTenant_ExigemPlatformAdmin()
    {
        // Controle do teste acima: as duas rotas que existem têm a policy nomeada — e não, por engano, AllowAnonymous.
        IReadOnlyList<Endpoint> endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        string[] policies =
        [
            .. endpoints.OfType<RouteEndpoint>()
                .Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/api/v1/tenants", StringComparison.Ordinal))
                .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>())
                .Select(dado => dado.Policy!),
        ];

        policies.Should().HaveCount(2).And.OnlyContain(policy => policy == "PlatformAdmin");
    }
}
```

Em `tests/IdentityGateway.Api.FunctionalTests/SegurancaTests.cs`:

1. No `<remarks>` da classe, trocar o último `<para>` (o que começa com "<b>Os testes usam <c>POST</c>, e não <c>GET</c>.</b>") por:

```csharp
/// <para>
/// <b>Um caminho que não existe também responde <c>401</c> sem token.</b> A policy de fallback exige usuário
/// autenticado para tudo o que não declara outra coisa, inclusive para o que o roteamento não achou: quem não se
/// identificou não fica sabendo o que existe. Com token, o mesmo caminho responde <c>404</c>.
/// </para>
```

2. Trocar o teste `ComTokenInvalido_Retorna401` inteiro por:

```csharp
    [Fact]
    public async Task ComTokenAssinadoPorOutraChave_Retorna401()
    {
        // RS256, com o mesmo kid que a Api conhece, assinado por outra chave: tudo confere menos a assinatura. É o caso
        // que prova que ela é verificada — um HS256 qualquer seria recusado já pelo algoritmo, e provaria menos.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClient();
        using var outraChave = RSA.Create(2048);
        string token = factory.Emissor.Assinar(factory.Emissor.Payload(roles: PlatformAdmin), outraChave);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage resposta = await client.PostAsync(RotaProtegida, content: null, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
```

com o campo `private static readonly string[] PlatformAdmin = ["platform-admin"];` junto de `RotaProtegida`, e os `using` `System.Security.Cryptography`, `System.Net.Http.Json` e `System.Text.Json` no topo.

3. Acrescentar, no fim da classe:

```csharp
    [Fact]
    public async Task SemToken_OCorpoEProblemDetailsEOCabecalhoNaoDizPorQue()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage resposta = await client.PostAsync(RotaProtegida, content: null, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        resposta.Headers.WwwAuthenticate.ToString().Should().Be("Bearer");
        resposta.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        JsonElement problema = await resposta.Content.ReadFromJsonAsync<JsonElement>(ct);
        problema.GetProperty("status").GetInt32().Should().Be(401);
        problema.GetProperty("title").GetString().Should().Be("Não autenticado");
        problema.GetProperty("type").GetString().Should().Contain("rfc9110");
        problema.GetProperty("correlationId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task TokenValidoSemOPapel_Retorna403ComOProblemDetailsUnico()
    {
        // Autenticado, mas sem platform-admin. O 403 não diz o que faltou.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado();

        HttpResponseMessage resposta = await client.PostAsync(RotaProtegida, content: null, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        resposta.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        JsonElement problema = await resposta.Content.ReadFromJsonAsync<JsonElement>(ct);
        problema.GetProperty("status").GetInt32().Should().Be(403);
        problema.GetProperty("title").GetString().Should().Be("Acesso negado");
        problema.GetProperty("detail").GetString().Should().NotContain("platform-admin").And.NotContain("roles");
        problema.GetProperty("correlationId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CaminhoNaoMapeado_SemToken401EComToken404()
    {
        // A prova da policy de fallback: nenhum endpoint responde aqui, e mesmo assim quem não se identificou leva 401.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient anonimo = factory.CreateClient();
        using HttpClient autenticado = factory.CreateClientAutenticado();
        Uri caminho = new("/api/v1/nao-existe", UriKind.Relative);

        HttpResponseMessage semToken = await anonimo.GetAsync(caminho, ct);
        HttpResponseMessage comToken = await autenticado.GetAsync(caminho, ct);

        semToken.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        comToken.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
```

`tests/IdentityGateway.Api.FunctionalTests/LogsPorHostTests.cs`:

```csharp
using IdentityGateway.Api.FunctionalTests.Logs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// O coletor de logs de cada host só vê o que aquele host registrou.
/// </summary>
/// <remarks>
/// É a premissa de todo teste que afirma algo sobre log — "o aviso saiu", "o token não está no log". As classes de
/// teste rodam em paralelo, cada uma com o seu host; se os hosts dividissem um logger, a ausência de um texto no
/// coletor não provaria nada, e a presença poderia ser de outro teste.
/// </remarks>
public sealed class LogsPorHostTests(IdentityGatewayApiFactory factory) : IClassFixture<IdentityGatewayApiFactory>
{
    [Fact]
    public async Task CadaHost_SoVeOsPropriosLogs()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string outroCanal = Guid.NewGuid().ToString("N");
        using WebApplicationFactory<Program> outro = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Serilog:WriteTo:9:Args:canal", outroCanal));
        using HttpClient daFactory = factory.CreateClientAutenticado();
        using HttpClient doOutro = outro.CreateClient();
        Uri rotaDaFactory = new($"/api/v1/tenants/{Guid.NewGuid()}/provisioning", UriKind.Relative);
        Uri rotaDoOutro = new($"/api/v1/tenants/{Guid.NewGuid()}/provisioning", UriKind.Relative);

        // Intercalados, e o segundo host construído por último: com o logger estático do processo, tudo iria para ele.
        await daFactory.GetAsync(rotaDaFactory, ct);
        await doOutro.GetAsync(rotaDoOutro, ct);
        await daFactory.GetAsync(rotaDaFactory, ct);

        IReadOnlyList<string> logsDaFactory = factory.Logs.Textos;
        IReadOnlyList<string> logsDoOutro = ColetorDeLogsDaApi.DoCanal(outroCanal).Textos;

        logsDaFactory.Should().Contain(texto => texto.Contains(rotaDaFactory.OriginalString, StringComparison.Ordinal));
        logsDaFactory.Should().NotContain(texto => texto.Contains(rotaDoOutro.OriginalString, StringComparison.Ordinal));
        logsDoOutro.Should().Contain(texto => texto.Contains(rotaDoOutro.OriginalString, StringComparison.Ordinal));
        logsDoOutro.Should().NotContain(texto => texto.Contains(rotaDaFactory.OriginalString, StringComparison.Ordinal));
    }
}
```

`tests/IdentityGateway.Api.FunctionalTests/AutenticacaoNegativaTests.cs`:

```csharp
using System.Net;
using System.Security.Cryptography;
using System.Text;
using IdentityGateway.Api.FunctionalTests.Oidc;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// A suíte negativa da autenticação: cada token que a Api precisa recusar, nas rotas protegidas que existem.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tokens forjados com a chave de teste</b>, que a Api conhece pelo JWKS do OIDC falso: a assinatura confere, e o
/// que reprova é exatamente o defeito do caso. Os casos de assinatura usam outra chave, de propósito.
/// </para>
/// <para>
/// <b>O discovery do OIDC falso anuncia um emissor diferente do configurado.</b> É o que faz o caso "iss igual ao do
/// discovery" distinguir o <c>IssuerValidator</c> estrito da validação padrão da biblioteca, que aceitaria esse emissor.
/// </para>
/// <para>
/// <b>Todo <c>401</c> é igual por fora:</b> <c>WWW-Authenticate: Bearer</c>, sem <c>error</c> nem
/// <c>error_description</c>, e o mesmo Problem Details. Quem chama não aprende por que foi recusado.
/// </para>
/// <para>
/// O limitador de requisições não entra na conta: ele roda depois da autorização, e um <c>401</c> não consome cota.
/// </para>
/// </remarks>
public sealed class AutenticacaoNegativaTests(IdentityGatewayApiFactory factory)
    : IClassFixture<IdentityGatewayApiFactory>
{
    private const string Emissor = IdentityGatewayApiFactory.EmissorPublico;

    private static readonly string[] PlatformAdmin = ["platform-admin"];

    private static readonly string[] AudienciaDaGatewayEOutra = ["identity-gateway-api", "account"];

    // Outra chave RSA, que a Api não conhece. Estática: gerar 2048 bits por caso custaria mais que o teste.
    private static readonly RSA ChaveForasteira = RSA.Create(2048);

    private static long Agora => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static TheoryDataRow<Func<IdentityGatewayApiFactory, string>> Caso(
        string rotulo, Func<IdentityGatewayApiFactory, string> token) => new(token) { Label = rotulo };

    /// <summary>Um token de platform-admin válido em tudo, menos no que <paramref name="defeito"/> estragar.</summary>
    private static TheoryDataRow<Func<IdentityGatewayApiFactory, string>> Com(
        string rotulo, Action<Dictionary<string, object?>> defeito) =>
        Caso(rotulo, alvo => alvo.Emissor.Emitir(roles: PlatformAdmin, ajustar: defeito));

    /// <summary>A receita do README e da CI de antes desta fatia: HS256 com a chave de desenvolvimento publicada.</summary>
    private static string ReceitaHs256Antiga()
    {
        Dictionary<string, object?> payload = new()
        {
            ["sub"] = "0199a000-0000-7000-8000-000000000001",
            ["roles"] = "platform-admin",
            ["iss"] = "identitygateway",
            ["aud"] = "identitygateway-api",
            ["nbf"] = Agora,
            ["exp"] = Agora + 3600,
        };

        return EmissorDeTeste.AssinarComHs256(
            payload, Encoding.UTF8.GetBytes("chave-de-desenvolvimento-nao-use-em-producao"));
    }

    public static TheoryData<Func<IdentityGatewayApiFactory, string>> RecusadosPelaValidacao => new()
    {
        Caso("token malformado", _ => "nao-e-um-jwt"),
        Caso("Bearer vazio", _ => string.Empty),

        // 2 min: acima dos 30 s de tolerância, abaixo dos 5 min do padrão da biblioteca.
        Com("vencido há 2 min", payload =>
        {
            payload["exp"] = Agora - 120;
            payload["iat"] = Agora - 420;
        }),
        Com("nbf 2 min no futuro", payload => payload["nbf"] = Agora + 120),
        Com("sem exp", payload => payload.Remove("exp")),

        Com("aud = account", payload => payload["aud"] = "account"),
        Com("sem aud", payload => payload.Remove("aud")),

        Com("iss forasteiro", payload => payload["iss"] = "https://atacante.test/realms/identity-gateway"),
        Caso("iss igual ao do discovery, diferente do configurado", alvo => alvo.Emissor.Emitir(
            roles: PlatformAdmin, ajustar: payload => payload["iss"] = alvo.Oidc.EmissorAnunciado)),
        Com("iss com barra final", payload => payload["iss"] = Emissor + "/"),
        Com("iss com sufixo", payload => payload["iss"] = Emissor + "-outro"),
        Com("iss em maiúsculas", payload => payload["iss"] = Emissor.ToUpperInvariant()),

        Caso("outra chave RSA com o mesmo kid", alvo => alvo.Emissor.Assinar(
            alvo.Emissor.Payload(roles: PlatformAdmin), ChaveForasteira)),
        Caso("alg = none", alvo => EmissorDeTeste.SemAssinatura(alvo.Emissor.Payload(roles: PlatformAdmin))),
        Caso("HS256 com a chave pública como segredo", alvo => EmissorDeTeste.AssinarComHs256(
            alvo.Emissor.Payload(roles: PlatformAdmin), alvo.Emissor.ChavePublicaEmPem(), EmissorDeTeste.Kid)),
        Caso("a receita HS256 antiga", _ => ReceitaHs256Antiga()),
    };

    public static TheoryData<Func<IdentityGatewayApiFactory, string>> Aceitos => new()
    {
        Com("token padrão do platform-admin", _ => { }),
        Com("aud em array com a da Gateway e outra", payload => payload["aud"] = AudienciaDaGatewayEOutra),
        Com("vencido há 10 s (dentro da tolerância de 30 s)", payload => payload["exp"] = Agora - 10),
    };

    private async Task<HttpResponseMessage> EnviarAsync(HttpMethod metodo, string rota, string token, CancellationToken ct)
    {
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage pedido = new(metodo, rota);

        // Sem validação do cabeçalho: os casos incluem valores que o HttpClient recusaria formatar.
        pedido.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

        return await client.SendAsync(pedido, ct);
    }

    private static string RotaDeConsulta() => $"/api/v1/tenants/{Guid.NewGuid()}/provisioning";

    private async Task ConferirRecusaAsync(Func<IdentityGatewayApiFactory, string> token, CancellationToken ct)
    {
        (HttpMethod Metodo, string Rota)[] rotas =
        [
            (HttpMethod.Post, "/api/v1/tenants"),
            (HttpMethod.Get, RotaDeConsulta()),
        ];

        foreach ((HttpMethod metodo, string rota) in rotas)
        {
            using HttpResponseMessage resposta = await EnviarAsync(metodo, rota, token(factory), ct);

            resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{metodo} {rota}");
            resposta.Headers.WwwAuthenticate.ToString().Should().Be("Bearer", "o motivo da recusa fica no log, não na resposta");
            resposta.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        }
    }

    [Theory]
    [MemberData(nameof(RecusadosPelaValidacao))]
    public async Task TokenQueAValidacaoRecusa_Responde401SemDetalheNasDuasRotas(Func<IdentityGatewayApiFactory, string> token)
    {
        ArgumentNullException.ThrowIfNull(token);

        await ConferirRecusaAsync(token, TestContext.Current.CancellationToken);
    }

    [Theory]
    [MemberData(nameof(Aceitos))]
    public async Task TokenAceito_PassaDaAutenticacaoNasDuasRotas(Func<IdentityGatewayApiFactory, string> token)
    {
        // Os controles: sem eles, "tudo responde 401" também deixaria as duas theories acima verdes. Passar da
        // autenticação e da policy é chegar ao endpoint: o POST sem corpo responde 400, e a consulta de um tenant que
        // não existe, 404.
        ArgumentNullException.ThrowIfNull(token);
        CancellationToken ct = TestContext.Current.CancellationToken;

        using HttpResponseMessage registro = await EnviarAsync(HttpMethod.Post, "/api/v1/tenants", token(factory), ct);
        using HttpResponseMessage consulta = await EnviarAsync(HttpMethod.Get, RotaDeConsulta(), token(factory), ct);

        registro.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        consulta.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task EsquemaBasic_Responde401()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage pedido = new(HttpMethod.Post, "/api/v1/tenants");
        pedido.Headers.TryAddWithoutValidation("Authorization", "Basic dXN1YXJpbzpzZW5oYQ==");

        using HttpResponseMessage resposta = await client.SendAsync(pedido, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
```

- [ ] **Passo 5: Rodar e ver falhar**

Run: `dotnet build IdentityGateway.slnx`
Expected: FAIL de compilação — `CS0234`/`CS0246` para `IdentityGateway.Api.Authentication` e `ValidacaoDoAccessToken` (em `EmissorEstritoTests` e `OpcoesDoJwtBearerTests`). É o vermelho desta tarefa: os tipos de produção ainda não existem.

- [ ] **Passo 6: A validação do token**

`src/IdentityGateway.Api/Authentication/ValidacaoDoAccessToken.cs`:

```csharp
using IdentityGateway.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace IdentityGateway.Api.Authentication;

/// <summary>
/// Como a Api valida o access token: por metadados do provedor, com emissor estrito, e mais nada.
/// </summary>
/// <remarks>
/// <para>
/// <b>Os metadados vêm pelo endereço de transporte; o emissor aceito é o público.</b> São endereços diferentes de
/// propósito (o público pode nem resolver de dentro da rede), e por isso não há <c>Authority</c>: ela amarraria os dois.
/// </para>
/// <para>
/// <b><c>IssuerValidator</c>, e não <c>ValidIssuer</c>.</b> Com metadados, a biblioteca aceita o token cujo <c>iss</c>
/// é igual ao <c>issuer</c> que o discovery anuncia <i>antes</i> de olhar <c>ValidIssuer</c> — um valor errado ali não
/// barra nada. O validador próprio tem precedência sobre tudo e compara com o emissor configurado, por igualdade
/// ordinal.
/// </para>
/// <para>
/// <b>Os números, e o que cada um evita.</b> Tolerância de relógio de 30 s: o padrão de 5 min dobraria a vida de um
/// token de 5 min. Prazo de 5 s na busca dos metadados: o padrão é de 60 s, e a busca é serializada — com o provedor
/// mudo, os pedidos se empilhariam. Intervalo de refresh de 30 s: o padrão de 5 min deixaria uma segunda rotação de
/// chave em <c>401</c> por todo esse tempo.
/// </para>
/// <para>
/// <b>Sem detalhe do erro na resposta, em nenhum ambiente.</b> O <c>WWW-Authenticate</c> ecoaria o <c>iss</c> e o
/// <c>aud</c> recusados, que vêm do token: um <c>iss</c> com caractere de controle faria o servidor recusar o próprio
/// cabeçalho, e o <c>401</c> viraria <c>500</c>. O diagnóstico vai para o log.
/// </para>
/// </remarks>
internal static class ValidacaoDoAccessToken
{
    /// <summary>Categoria dos logs de autenticação.</summary>
    internal const string Categoria = "IdentityGateway.Api.Authentication";

    internal static readonly TimeSpan Tolerancia = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan PrazoDosMetadados = TimeSpan.FromSeconds(5);

    internal static readonly TimeSpan IntervaloDeRefresh = TimeSpan.FromSeconds(30);

    private static readonly string[] SoRs256 = [SecurityAlgorithms.RsaSha256];

    internal static void Configurar(JwtBearerOptions jwt, AccessTokenValidationOptions validacao)
    {
        ArgumentNullException.ThrowIfNull(jwt);
        ArgumentNullException.ThrowIfNull(validacao);

        jwt.MetadataAddress = validacao.MetadataAddress;
        jwt.RequireHttpsMetadata = validacao.RequireHttpsMetadata;
        jwt.BackchannelTimeout = PrazoDosMetadados;
        jwt.RefreshInterval = IntervaloDeRefresh;

        // Sem isto, o handler remapeia claims curtos para URI antes de a policy ver o token: "roles" vira a URI longa
        // de papel, e RequireClaim("roles", ...) nunca casa — sempre 403, mesmo com o token certo.
        jwt.MapInboundClaims = false;
        jwt.IncludeErrorDetails = false;

        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            IssuerValidator = EmissorEstrito(validacao.Issuer),
            ValidateAudience = true,
            ValidAudience = validacao.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = SoRs256,
            ClockSkew = Tolerancia,

            // "sub", e não preferred_username: o username é o e-mail, e o nome do usuário vai parar em log.
            NameClaimType = "sub",
            RoleClaimType = "roles",
        };

        jwt.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = AoFalhar,
        };
    }

    /// <summary>Aceita só o emissor configurado, por igualdade ordinal.</summary>
    /// <remarks>
    /// Lança <see cref="SecurityTokenInvalidIssuerException"/>, que a biblioteca trata como recuperável: dispara um
    /// refresh dos metadados, limitado pelo intervalo de refresh, e o token continua recusado.
    /// </remarks>
    internal static IssuerValidator EmissorEstrito(string esperado) => (issuer, _, _) =>
        string.Equals(issuer, esperado, StringComparison.Ordinal)
            ? issuer
            : throw new SecurityTokenInvalidIssuerException("Emissor do token não é o do realm configurado.")
            {
                InvalidIssuer = issuer,
            };

    /// <summary>
    /// Registra por que a autenticação falhou — só o tipo da falha, nunca o token nem a mensagem da exceção.
    /// </summary>
    /// <remarks>
    /// Com o provedor fora do ar e os metadados ainda não carregados, a falha chega como "chave não encontrada", e a
    /// resposta é <c>401</c>: sem este aviso, o provedor fora viraria <c>401</c> em silêncio, apontando para o token.
    /// </remarks>
    private static Task AoFalhar(AuthenticationFailedContext contexto)
    {
        ILogger logger = contexto.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>().CreateLogger(Categoria);
        string tipo = contexto.Exception.GetType().Name;

        if (EhFalhaDeChaveOuDeMetadados(contexto.Exception))
        {
            AutenticacaoLogs.ChavesIndisponiveis(logger, tipo);
        }
        else
        {
            AutenticacaoLogs.TokenRecusado(logger, tipo);
        }

        return Task.CompletedTask;
    }

    // Chave de assinatura não encontrada (kid desconhecido, ou nenhuma chave porque os metadados não vieram), e as
    // formas em que a própria busca dos metadados pode aparecer.
    internal static bool EhFalhaDeChaveOuDeMetadados(Exception excecao) => excecao
        is SecurityTokenSignatureKeyNotFoundException
        or InvalidOperationException
        or HttpRequestException
        or TaskCanceledException;
}
```

`src/IdentityGateway.Api/Authentication/AutenticacaoLogs.cs` (os quatro eventos nascem juntos; o 2102 e o 2103 passam a ser usados na Tarefa 8):

```csharp
namespace IdentityGateway.Api.Authentication;

/// <summary>
/// Mensagens de log da autenticação, na faixa 2100–2199.
/// </summary>
/// <remarks>
/// <b>Nenhuma registra o token, um claim dele nem a mensagem da exceção de validação.</b> O que entra é o tipo da
/// falha e, onde cabe, o motivo em texto fixo.
/// </remarks>
internal static partial class AutenticacaoLogs
{
    [LoggerMessage(
        EventId = 2100,
        Level = LogLevel.Warning,
        Message = "Autenticação: chaves de assinatura ou metadados do provedor de identidade indisponíveis ({Tipo}); "
                  + "os tokens são recusados com 401 até a busca voltar a funcionar")]
    public static partial void ChavesIndisponiveis(ILogger logger, string tipo);

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Debug,
        Message = "Autenticação: token recusado pela validação ({Tipo})")]
    public static partial void TokenRecusado(ILogger logger, string tipo);

    [LoggerMessage(
        EventId = 2102,
        Level = LogLevel.Debug,
        Message = "Autenticação: token recusado pela forma ({Motivo})")]
    public static partial void FormaRecusada(ILogger logger, string motivo);

    [LoggerMessage(
        EventId = 2103,
        Level = LogLevel.Warning,
        Message = "Autenticação: Keycloak:Auth:AllowedClients está vazia; todo token de usuário será recusado com 401")]
    public static partial void NenhumClientPermitido(ILogger logger);
}
```

- [ ] **Passo 7: Policies e Problem Details para `401` e `403`**

`src/IdentityGateway.Api/Authorization/Policies.cs`:

```csharp
namespace IdentityGateway.Api.Authorization;

/// <summary>
/// Os nomes das policies de autorização. Constantes, para um nome digitado errado ser erro de compilação — com texto
/// solto, o endpoint subiria e responderia 500 no primeiro pedido ("policy não encontrada").
/// </summary>
internal static class Policies
{
    /// <summary>Operador da plataforma: o claim <c>roles</c> traz <c>platform-admin</c>.</summary>
    public const string PlatformAdmin = "PlatformAdmin";
}
```

`src/IdentityGateway.Api/Authorization/RespostasDeAutorizacao.cs`:

```csharp
using IdentityGateway.Application.Common.Abstractions;

namespace IdentityGateway.Api.Authorization;

/// <summary>
/// As duas respostas de quem não entra: <c>401</c> e <c>403</c>, como Problem Details (RFC 9457).
/// </summary>
/// <remarks>
/// <para>
/// <b>Um texto só para cada status, sem nada que distinga o motivo.</b> O <c>403</c> de "papel errado", o de "tenant
/// alheio" e o de "tenant que não existe" são a mesma resposta, byte a byte no que é fixo: uma diferença entre eles
/// diria a quem chama o que existe e o que não existe. O motivo fica no log, do lado de cá.
/// </para>
/// <para>
/// <b>Uma função só escreve cada um.</b> O handler de resultado da autorização e os módulos que precisam negar por
/// conta própria chamam o mesmo método — não há segundo lugar onde o texto possa divergir.
/// </para>
/// </remarks>
internal static class RespostasDeAutorizacao
{
    internal const string TipoDoProibido = "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.4";

    internal const string TipoDoNaoAutenticado = "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.2";

    internal const string TituloDoProibido = "Acesso negado";

    internal const string TituloDoNaoAutenticado = "Não autenticado";

    internal const string DetalheDoProibido = "A identidade apresentada não tem permissão para esta operação.";

    internal const string DetalheDoNaoAutenticado = "A requisição não traz um access token válido.";

    /// <summary>O <c>403</c> único da Gateway.</summary>
    public static IResult Proibido(HttpContext contexto) => Problema(
        contexto, StatusCodes.Status403Forbidden, TipoDoProibido, TituloDoProibido, DetalheDoProibido);

    /// <summary>O <c>401</c> único da Gateway. O <c>WWW-Authenticate</c> é de quem desafia, não daqui.</summary>
    public static IResult NaoAutenticado(HttpContext contexto) => Problema(
        contexto, StatusCodes.Status401Unauthorized, TipoDoNaoAutenticado, TituloDoNaoAutenticado,
        DetalheDoNaoAutenticado);

    private static IResult Problema(HttpContext contexto, int status, string tipo, string titulo, string detalhe)
    {
        ArgumentNullException.ThrowIfNull(contexto);

        return Results.Problem(
            statusCode: status,
            type: tipo,
            title: titulo,
            detail: detalhe,
            extensions: new Dictionary<string, object?>
            {
                ["correlationId"] = contexto.RequestServices.GetRequiredService<ICorrelationIdProvider>().CorrelationId,
            });
    }
}
```

`src/IdentityGateway.Api/Authorization/ProblemDetailsDeAutorizacao.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace IdentityGateway.Api.Authorization;

/// <summary>
/// Dá corpo ao <c>401</c> e ao <c>403</c> da autorização: sem isto, o primeiro sai só com o <c>WWW-Authenticate</c> e o
/// segundo sem nada.
/// </summary>
/// <remarks>
/// <para>
/// <b>No <c>401</c>, o desafio padrão roda primeiro.</b> É ele que escreve o <c>WWW-Authenticate</c>, que o cliente
/// OAuth espera; o corpo vem depois, enquanto a resposta ainda não começou.
/// </para>
/// <para>
/// <b>É também onde a auditoria de negação vai nascer.</b> Os handlers de autorização param no primeiro que falha
/// (<c>InvokeHandlersAfterFailure</c> desligado, na rota de tenant), e por isso não servem para registrar negação;
/// este ponto vê todas.
/// </para>
/// </remarks>
internal sealed class ProblemDetailsDeAutorizacao : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _padrao = new();

    public async Task HandleAsync(
        RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        if (authorizeResult.Forbidden)
        {
            await RespostasDeAutorizacao.Proibido(context).ExecuteAsync(context);
            return;
        }

        if (authorizeResult.Challenged)
        {
            await _padrao.HandleAsync(next, context, policy, authorizeResult);

            if (!context.Response.HasStarted)
            {
                await RespostasDeAutorizacao.NaoAutenticado(context).ExecuteAsync(context);
            }

            return;
        }

        await _padrao.HandleAsync(next, context, policy, authorizeResult);
    }
}
```

- [ ] **Passo 8: Ligar tudo no `DependencyInjection` da Api**

Em `src/IdentityGateway.Api/DependencyInjection.cs`:

1. Os `using` do topo passam a ser exatamente:

```csharp
using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Carter;
using IdentityGateway.Api.Authentication;
using IdentityGateway.Api.Authorization;
using IdentityGateway.Api.Services;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
```

(saem `System.IdentityModel.Tokens.Jwt`, `System.Text`, `IdentityGateway.Api.Security` e `Microsoft.IdentityModel.Tokens`.)

2. Em `AddApiServices`, a cadeia do fim troca `.AddAutenticacao(configuration)` por `.AddAutenticacao()`.

3. Substituir o método `AddAutenticacao` inteiro — do comentário XML (`/// <summary>` "Validação de token JWT e autorização por policy.") até a chave que o fecha — por:

```csharp
    /// <summary>
    /// Validação do access token do provedor de identidade, e autorização por policy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A Gateway não emite token.</b> Quem emite é o Keycloak, direto para a aplicação cliente (ADR-002); aqui só se
    /// valida. As regras da validação — metadados pelo endereço de transporte, emissor estrito, só RS256 — estão em
    /// <see cref="ValidacaoDoAccessToken"/>, com o porquê de cada número.
    /// </para>
    /// <para>
    /// <b>Autenticado por padrão.</b> A policy de fallback exige usuário autenticado em todo endpoint que não declare
    /// outra coisa, e os poucos abertos (health checks, a documentação em Development) dizem <c>AllowAnonymous</c> com
    /// todas as letras. Esquecer o <c>RequireAuthorization</c> num endpoint novo deixa de abri-lo ao mundo — e um
    /// teste reprova o endpoint que não declarar nem policy nem anonimato.
    /// </para>
    /// <para>
    /// <b>Claim plano, não <c>RequireRole</c>:</b> o papel chega no claim <c>roles</c>, que o realm emite plano e só com
    /// o catálogo. Com <c>MapInboundClaims</c> desligado, ele chega com esse nome, e <c>RequireClaim</c> o compara.
    /// </para>
    /// </remarks>
    private static IServiceCollection AddAutenticacao(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configure sobre as opções nomeadas, e não a lambda do AddJwtBearer: a validação depende de uma option que
        // só o contêiner resolve (preenchida pelo adaptador do provedor), e este Configure roda antes do PostConfigure
        // do JwtBearer — que é quem monta a busca dos metadados a partir do MetadataAddress que encontrar.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AccessTokenValidationOptions>>((jwt, validacao) =>
                ValidacaoDoAccessToken.Configurar(jwt, validacao.Value));

        // Corpo para o 401 e o 403: sem isto, o primeiro sai só com o WWW-Authenticate e o segundo, vazio.
        services.AddProblemDetails();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemDetailsDeAutorizacao>();

        services.AddAuthorization(options =>
        {
            options.AddPolicy(Policies.PlatformAdmin, policy => policy.RequireClaim("roles", "platform-admin"));

            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        });

        return services;
    }
```

4. Em `ChaveDaParticao`, trocar `contexto.User.FindFirstValue(JwtRegisteredClaimNames.Sub)` por `contexto.User.FindFirstValue("sub")`. O comentário XML do método continua valendo.

- [ ] **Passo 9: Declarar o anonimato onde ele é deliberado**

Em `src/IdentityGateway.Api/Program.cs`:

0. O logger passa a ser **do host**, e não do processo. Trocar o bloco do `UseSerilog` por:

```csharp
// Serilog substitui o logging padrão antes de qualquer outro registro: o que falhar no startup a partir daqui já
// sai no formato estruturado. Lido da configuração para que o ambiente decida sink e nível sem recompilar.
//
// preserveStaticLogger: o logger é deste host, e não o Log.Logger estático do processo. Sem isto, o host registraria
// pelo logger estático — que é o do ÚLTIMO host construído no processo. Em produção há um host só e não faria
// diferença; nos testes funcionais há vários em paralelo, e os logs de um iriam para o coletor do outro.
builder.Host.UseSerilog(
    (context, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext(),
    preserveStaticLogger: true);
```

(Visto no protótipo deste plano: sem o parâmetro, o coletor de um host recebia os pedidos dos outros; com ele, cada canal só vê o próprio host.)

1. Dentro do `if (app.Environment.IsDevelopment())`, as três rotas passam a ser:

```csharp
    // OpenAPI só em desenvolvimento: o documento descreve a superfície inteira da API, e publicá-lo em produção
    // entrega o mapa a quem estiver procurando. Quem precisa dele em produção o expõe atrás de autenticação.
    //
    // AllowAnonymous explícito: a policy de fallback exige usuário autenticado em tudo o que não declarar outra
    // coisa. Em desenvolvimento, a documentação é aberta de propósito.
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();

    // A raiz leva à documentação. Sem isto, abrir https://localhost:7206 no navegador — que é o que a IDE faz
    // ao rodar — não mostra nada útil, e quem acabou de clonar o repositório lê aquilo como "não subiu".
    //
    // Só em Development, junto com o próprio Scalar: em produção "/" não é rota, e responde 401 a quem não se
    // identificou e 404 a quem se identificou.
    app.MapGet("/", () => Results.Redirect("/scalar/v1"))
       .AllowAnonymous()
       .ExcludeFromDescription();
```

2. Os dois health checks ganham `.AllowAnonymous()`:

```csharp
// live: o processo responde. Sem dependência externa — banco fora do ar não deve fazer o orquestrador reiniciar
// o pod, porque reiniciar não conserta banco e só remove capacidade.
//
// Anônimo, com todas as letras: o orquestrador não se autentica.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
}).AllowAnonymous();

// ready: posso receber tráfego. Checa Postgres, Redis e o Keycloak (obtendo o token do service account) — sem eles,
// a instância sai do balanceador.
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
}).AllowAnonymous();
```

3. O comentário da linha do `MapCarter()` passa a ser:

```csharp
// Carter mapeia os módulos descobertos por varredura. Cada rota deles declara a própria policy.
app.MapCarter();
```

Em `src/IdentityGateway.Api/Modules/TenantsModule.cs`: acrescentar `using IdentityGateway.Api.Authorization;`, trocar as duas ocorrências de `.RequireAuthorization("PlatformAdmin")` por `.RequireAuthorization(Policies.PlatformAdmin)` e, nas duas rotas, acrescentar depois do último `.ProducesProblem(...)`:

```csharp
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
```

(o `;` que fechava a cadeia sai da linha anterior.)

- [ ] **Passo 10: Tirar o JWT simétrico**

```bash
git rm src/IdentityGateway.Api/Security/JwtTokenService.cs src/IdentityGateway.Infrastructure/Configuration/JwtOptions.cs
```

Em `src/IdentityGateway.Infrastructure/DependencyInjection.cs`, apagar o bloco:

```csharp
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

```

Em `src/IdentityGateway.Api/appsettings.json`, apagar a seção inteira:

```json
  "Jwt": {
    "Issuer": "identitygateway",
    "Audience": "identitygateway-api",
    "ExpirationMinutes": 60
  },
```

`src/IdentityGateway.Api/Services/HttpCurrentUser.cs`, inteiro:

```csharp
using System.Security.Claims;
using IdentityGateway.Application.Common.Abstractions;

namespace IdentityGateway.Api.Services;

/// <summary>
/// Usuário autenticado da requisição HTTP.
/// </summary>
/// <remarks>
/// <para>
/// Substitui o <c>NoCurrentUser</c> da Infrastructure. Lê o claim de identidade do <c>HttpContext</c>, que é o
/// único lugar onde essa informação existe — e é por isso que esta implementação pertence à Api, não à
/// Infrastructure.
/// </para>
/// <para>
/// <b>O identificador é o <c>sub</c> do access token</b>: o id do usuário no provedor de identidade, um GUID. A
/// autenticação já recusou o token sem <c>sub</c> ou com <c>sub</c> fora desse formato; aqui ele só é lido.
/// </para>
/// </remarks>
internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    /// <inheritdoc />
    public Guid? Id
    {
        get
        {
            ClaimsPrincipal? usuario = accessor.HttpContext?.User;

            // Lê as DUAS formas do mesmo claim, e isso não é redundância defensiva: a validação do token roda com
            // `MapInboundClaims = false`, porque o remapeamento automático quebrava a policy `PlatformAdmin`
            // (o handler traduzia `roles` para a URI longa antes de a policy comparar). Com o remapeamento
            // desligado, o `sub` também deixa de virar `ClaimTypes.NameIdentifier` — e ler só a forma longa
            // devolveria nulo para todo usuário autenticado.
            //
            // A forma longa continua sendo consultada porque é o que valeria se o remapeamento voltasse a ser ligado.
            string? valor =
                usuario?.FindFirstValue("sub")
                ?? usuario?.FindFirstValue(ClaimTypes.NameIdentifier);

            // Guid.TryParse e não Parse: um claim malformado é dado externo, e derrubar a requisição por causa
            // dele seria pior que tratar a operação como anônima.
            return Guid.TryParse(valor, out Guid id) ? id : null;
        }
    }

    /// <inheritdoc />
    public bool IsAuthenticated => accessor.HttpContext?.User.Identity?.IsAuthenticated ?? false;
}
```

Em `src/IdentityGateway.Api/IdentityGateway.Api.csproj`, o comentário do `UserSecretsId` passa a ser:

```xml
    <!--
      Identificador do cofre de User Secrets. Não é segredo — é só o nome da pasta onde o dotnet guarda os
      segredos desta máquina, fora do repositório. A connection string e a chave privada do service account da
      Gateway (Keycloak:Admin:PrivateKeyPem) vivem lá, nunca em appsettings versionado.
    -->
```

Em `Directory.Packages.props`, o comentário acima de `Microsoft.AspNetCore.Authentication.JwtBearer` passa a ser:

```xml
    <!--
      Validação do access token do provedor de identidade, por metadados (JWKS). A Gateway não emite token. O rate
      limiting não aparece aqui de propósito: Microsoft.AspNetCore.RateLimiting faz parte do SDK Web desde o .NET 7
      e não precisa de pacote.
    -->
```

- [ ] **Passo 11: Limpar as chaves `Jwt:*` dos testes de integração**

1. `tests/IdentityGateway.Infrastructure.IntegrationTests/DependencyInjectionTests.cs`: apagar as três linhas `["Jwt:Issuer"]`, `["Jwt:Audience"]` e `["Jwt:SigningKey"]` de `ConfiguracaoValida`, e apagar o teste `ChaveJwtCurta_FalhaAoValidar` inteiro (a option que ele validava não existe mais; a validação da nova option está em `AccessTokenValidationOptionsTests`).
2. `tests/IdentityGateway.Infrastructure.IntegrationTests/Provisioning/ComposicaoDoProvisionamento.cs` e `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakHealthCheckTests.cs`: apagar as mesmas três linhas `["Jwt:…"]` de cada um.

Run: `grep -rn "Jwt:\|JwtOptions\|JwtTokenService\|Jwt__" src tests --include=*.cs --include=*.json --include=*.csproj`
Expected: nenhuma linha. (O `Jwt__SigningKey` do `docker-compose.yml` sai na Tarefa 10; até lá é uma variável que ninguém lê.)

- [ ] **Passo 12: Rodar e ver passar**

Run: `dotnet build IdentityGateway.slnx`
Expected: `0 Aviso(s)`, `0 Erro(s)`.

Run: `dotnet test tests/IdentityGateway.ArchitectureTests`
Expected: PASS — inclusive `NenhumaCamadaDeProducaoUsaChaveSimetrica`, `Api_NaoUsaOPacoteJwtLegado` e `AApiNaoConheceOKeycloak` (a Api lê a option neutra; nenhum tipo do namespace do Keycloak).

Run (Docker ligado): `dotnet test tests/IdentityGateway.Api.FunctionalTests`
Expected: PASS, `falhou: 0`. Em especial:
- os testes que já existiam e usam `CreateClientAutenticado` (`RegistroDeTenantTests`, `ProvisionamentoDeTenantTests`, `VazamentoDoEmailNaApiTests`) verdes **sem nenhuma linha alterada**;
- `AutenticacaoNegativaTests`: 16 casos recusados, 3 aceitos e o `Basic`;
- `EndpointsDeclaramAutorizacaoTests`: se `TodoEndpoint_TemPolicyNomeadaOuAnonimatoDeclarado` falhar, a mensagem lista os endpoints sem declaração — o esperado é nenhum; um endpoint do Scalar ou do OpenAPI na lista quer dizer que o `AllowAnonymous()` do Passo 9 não alcançou aquele mapeamento.

Run (Docker ligado): `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests`
Expected: PASS — `total: 232` (220 + 13 da Tarefa 6 − 1 teste removido), `falhou: 0`.

- [ ] **Passo 13: 🧪 Provas por mutação**

Antes da primeira: `git add -A src tests Directory.Packages.props`. Reverter cada uma com `git restore <arquivo>` e conferir `git diff --stat` vazio.

Run (a cada mutação): `dotnet test tests/IdentityGateway.Api.FunctionalTests` — o projeto inteiro, porque várias mutações têm testemunhas em mais de uma classe. Na mutação 14, também `dotnet test tests/IdentityGateway.ArchitectureTests --filter-class "*RegrasDaApiTests"`.

| # | Mutação | Vermelho esperado |
|---|---|---|
| 1 | Em `ValidacaoDoAccessToken.Configurar`, trocar `IssuerValidator = EmissorEstrito(validacao.Issuer),` por `ValidIssuer = validacao.Issuer,` | Na suíte negativa, **só** o caso "iss igual ao do discovery, diferente do configurado" (os outros casos de `iss` continuam `401` pela validação padrão — é a prova de que o `ValidIssuer` não restringe); e `AValidacao_EstaLigadaPorInteiroESoAceitaRs256` |
| 2 | Em `EmissorEstrito`, trocar `StringComparison.Ordinal` por `StringComparison.OrdinalIgnoreCase` | "iss em maiúsculas"; `EmissorEstritoTests` (o caso da caixa) |
| 3 | Em `EmissorEstrito`, trocar o `string.Equals(...)` por `issuer.StartsWith(esperado, StringComparison.Ordinal)` | "iss com barra final", "iss com sufixo"; `EmissorEstritoTests` |
| 4 | Em `KeycloakAdminOptions.Issuer`, usar sempre o `BaseUrl` | **Todos** os aceitos e todos os testes que usam `CreateClientAutenticado` (`401`): na factory, `BaseUrl` ≠ `PublicBaseUrl` |
| 5 | `ValidateAudience = false` | "aud = account", "sem aud"; `AValidacao_EstaLigadaPorInteiroESoAceitaRs256` |
| 6 | Apagar a linha `ClockSkew = Tolerancia,` | "vencido há 2 min" (aceito: o padrão é de 5 min); `AValidacao_…` |
| 7 | `ValidateLifetime = false` | "vencido há 2 min", "nbf 2 min no futuro"; `AValidacao_…` |
| 8 | `jwt.IncludeErrorDetails = true;` | Todos os recusados (o `WWW-Authenticate` deixa de ser só `Bearer`); `AResposta_NaoDetalhaOErro…` |
| 9 | `jwt.MapInboundClaims = true;` | Todos os aceitos e todo teste com `202`/`200`/`404` autenticado (viram `403`: o claim `roles` é remapeado) |
| 10 | Em `AddAutenticacao`, apagar a linha do `options.FallbackPolicy` | **Só** `CaminhoNaoMapeado_SemToken401EComToken404` (o anônimo recebe `404`). `TodoEndpoint_TemPolicyNomeadaOuAnonimatoDeclarado` continua verde: ele confere metadata, que a mutação não muda |
| 11 | Em `AddAutenticacao`, apagar a linha do `AddSingleton<IAuthorizationMiddlewareResultHandler, …>` | `SemToken_OCorpoEProblemDetails…`, `TokenValidoSemOPapel_Retorna403ComOProblemDetailsUnico` e a suíte negativa (o `401` sai sem corpo) |
| 12 | Em `Program.cs`, tirar o `.AllowAnonymous()` do `/health/live` | `OsHealthChecks_ContinuamAbertos` (`401`) e `TodoEndpoint_TemPolicyNomeadaOuAnonimatoDeclarado` |
| 13 | Em `TenantsModule`, tirar o `.RequireAuthorization(Policies.PlatformAdmin)` do `POST` | `AsRotasDeTenant_ExigemPlatformAdmin`, `TodoEndpoint_…`, e `TokenValidoSemOPapel_…` (o `POST` sem papel deixa de ser `403`) |
| 14 | Em `ValidacaoDoAccessToken.Configurar`, acrescentar `IssuerSigningKey = new SymmetricSecurityKey(new byte[32]),` ao `TokenValidationParameters` | Arquitetura (`NenhumaCamadaDeProducaoUsaChaveSimetrica`) e `AsChaves_VemSoDosMetadados`. A receita HS256 antiga continua `401` com ou sem a mutação (`ValidAlgorithms`, `iss` e `aud` antigos): quem pega a chave simétrica de volta é a regra de arquitetura |
| 15 | Em `ChaveDaParticao`, trocar `"sub"` por `"nameid"` | `ChaveDaParticaoTests.ComOClaimCurto_ParticionaPeloUsuario` |
| 16 | Em `Program.cs`, tirar o `preserveStaticLogger: true` do `UseSerilog` | `LogsPorHostTests.CadaHost_SoVeOsPropriosLogs` (o pedido de um host aparece no coletor do outro) |

Mutações equivalentes, **não** executadas (§5.3 da spec): `RoleClaimType`; `NameClaimType`, que nenhum código lê; `ValidAlgorithms`, que a biblioteca já cobre para o HS256 quando só há chaves RSA.

- [ ] **Passo 14: Commit**

```bash
git add -A src tests Directory.Packages.props
git commit -m "feat: a Api valida o access token do provedor por metadados e o HS256 sai

A autenticacao passa a ser a de producao em todo lugar: JwtBearer com os
metadados pelo endereco de transporte, emissor publico estrito por
igualdade ordinal (IssuerValidator: o ValidIssuer nao restringe quando ha
discovery), so RS256, tolerancia de 30 s, 5 s de prazo nos metadados e
refresh a cada 30 s, sem detalhe do erro na resposta. A policy de
fallback exige usuario autenticado, e health checks e documentacao
declaram AllowAnonymous. O 401 e o 403 ganham Problem Details, um texto
so para cada.

Saem o JwtTokenService, o JwtOptions, a secao Jwt e o uso de
System.IdentityModel.Tokens.Jwt na Api. Os testes funcionais passam a
validar tokens de verdade contra um OIDC falso em loopback, que anuncia
um emissor diferente do aceito; CreateClientAutenticado mantem a
assinatura.

Mutacoes: ValidIssuer no lugar do validador, comparacao sem caixa e por
prefixo, emissor pelo BaseUrl, sem audiencia, tolerancia padrao, sem
prazo, detalhe do erro ligado, MapInboundClaims, sem a fallback, sem o
result handler, health check sem anonimato, rota sem policy, chave
simetrica de volta, o claim da particao e o logger estatico."
```

Run: `git log -1 --format=%B | grep -Eci "co-authored|generated with"`
Expected: `0`.

---

### Tarefa 8: `azp`, `typ` e `sub`, o aviso da lista vazia e o host em `Production`

Spec: §4.2 (`OnTokenValidated`, `ValidateOnStart`, lista vazia), §5.1 (linha "Host em `Production`"), §5.2 (os casos `F` e `P` da D1), §5.5 ("O host em `Production`"), DT3, DT4, DT6.

A biblioteca confere assinatura, emissor, audiência e prazo. Esta tarefa acrescenta o que é da Gateway — qual client obteve o token, que ele é um access token, e que fala de um usuário identificável — e prova os ramos que só existem fora de Development, com o host de pé **e com pedidos**: a suíte inteira roda em Development, e um ramo de produção testado só na subida é verde vacuoso.

**Arquivos:**
- Create: `src/IdentityGateway.Api/Authentication/FormaDoAccessToken.cs`, `Authentication/AvisoDeClientsPermitidos.cs`
- Modify: `src/IdentityGateway.Api/Authentication/ValidacaoDoAccessToken.cs`, `DependencyInjection.cs`, `IdentityGateway.Api.csproj`
- Create (testes funcionais): `FormaDoAccessTokenTests.cs`, `ApiEmProducaoFactory.cs`, `HostEmProducaoTests.cs`
- Modify: `tests/IdentityGateway.Api.FunctionalTests/AutenticacaoNegativaTests.cs`

**Interfaces:**
- Consome: `ValidacaoDoAccessToken.Configurar`, `AutenticacaoLogs.FormaRecusada` e `NenhumClientPermitido`, `EmissorDeTeste`, `OidcFalso.IniciarAsync(emissor, https: true)` e `OidcFalso.Certificado`, `ColetorDeLogsDaApi` (Tarefa 7); `AccessTokenValidationOptions.AllowedClients` (Tarefa 6); a guarda `HttpSoEmDesenvolvimento` do adaptador (fatia C).
- Produz:
  - `internal static class FormaDoAccessToken` (Api): `static string? Recusar(JsonWebToken token, IReadOnlyList<string> clientsPermitidos)` — `null` se aceito; senão, o motivo em texto fixo.
  - `internal sealed class AvisoDeClientsPermitidos : IHostedService` (Api).
  - `internal sealed class ApiEmProducaoFactory : WebApplicationFactory<Program>` (testes): `ApiEmProducaoFactory(IReadOnlyDictionary<string, string?> configuracao, HttpMessageHandler? metadados = null)`; `ColetorDeLogsDaApi Logs`; `static IReadOnlyDictionary<string, string?> Configuracao(string baseUrl, string? publicBaseUrl, params (string Chave, string? Valor)[] extras)`.

- [ ] **Passo 1: Os testes da forma, fora do HTTP**

`tests/IdentityGateway.Api.FunctionalTests/FormaDoAccessTokenTests.cs` (sem fixture):

```csharp
using IdentityGateway.Api.Authentication;
using IdentityGateway.Api.FunctionalTests.Oidc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// As três checagens de forma, fora do HTTP: o que cada uma aceita, o que recusa, e o que o motivo nunca revela.
/// </summary>
/// <remarks>
/// A suíte negativa prova as mesmas regras por HTTP, onde o que se vê é só o <c>401</c>. Aqui se vê o motivo — e que
/// ele é texto fixo: vai para o log, e não pode carregar um valor que veio do token.
/// </remarks>
public sealed class FormaDoAccessTokenTests : IDisposable
{
    private static readonly string[] Permitidos = ["identity-gateway-demo", "console-administrativo"];

    private static readonly string[] TypEmArray = ["Bearer"];

    private readonly EmissorDeTeste _emissor = new("https://sso.exemplo.test/realms/identity-gateway");

    public void Dispose() => _emissor.Dispose();

    private string? Recusar(Action<Dictionary<string, object?>>? ajustar = null, IReadOnlyList<string>? permitidos = null) =>
        FormaDoAccessToken.Recusar(new JsonWebToken(_emissor.Emitir(ajustar: ajustar)), permitidos ?? Permitidos);

    [Fact]
    public void TokenComAFormaDoProvedor_EAceito()
    {
        Recusar().Should().BeNull();
    }

    [Fact]
    public void QualquerClientDaLista_EAceito()
    {
        Recusar(payload => payload["azp"] = "console-administrativo").Should().BeNull();
    }

    [Fact]
    public void ListaVazia_RecusaTodoToken()
    {
        // Fail-closed: lista vazia não é "qualquer client serve".
        Recusar(permitidos: []).Should().Contain("azp");
    }

    [Fact]
    public void AzpComOutraCaixa_ERecusado()
    {
        Recusar(payload => payload["azp"] = "Identity-Gateway-Demo").Should().Contain("azp");
    }

    [Fact]
    public void AzpAusenteOuVazio_ERecusado()
    {
        Recusar(payload => payload.Remove("azp")).Should().Contain("azp");
        Recusar(payload => payload["azp"] = string.Empty).Should().Contain("azp");
    }

    [Fact]
    public void TypDeIdToken_ERecusado()
    {
        Recusar(payload => payload["typ"] = "ID").Should().Contain("typ");
    }

    [Fact]
    public void TypEmArrayDeUmElemento_ERecusado()
    {
        // É o caso que só o JSON distingue: como claim, ["Bearer"] e "Bearer" são iguais. (Para azp e sub em array, a
        // biblioteca nem chega a montar o token — por isso eles não têm teste aqui, só na suíte por HTTP.)
        Recusar(payload => payload["typ"] = TypEmArray).Should().Contain("typ");
    }

    [Fact]
    public void TypAusente_ERecusado()
    {
        Recusar(payload => payload.Remove("typ")).Should().Contain("typ");
    }

    [Theory]
    [InlineData("joao")]
    [InlineData("0199a00000007000800000000000000a")]             // formato N
    [InlineData("{0199a000-0000-7000-8000-00000000000a}")]       // formato B
    [InlineData(" 0199a000-0000-7000-8000-00000000000a")]        // espaço
    [InlineData("")]
    public void SubForaDoFormatoD_ERecusado(string sub)
    {
        Recusar(payload => payload["sub"] = sub).Should().Contain("sub");
    }

    [Fact]
    public void OMotivo_NuncaCarregaOValorRecusado()
    {
        // O motivo vai para o log. Um azp, um typ ou um sub forjados podem ser qualquer texto — inclusive um e-mail.
        string?[] motivos =
        [
            Recusar(payload => payload["azp"] = "segredo@acme.test"),
            Recusar(payload => payload["typ"] = "segredo@acme.test"),
            Recusar(payload => payload["sub"] = "segredo@acme.test"),
        ];

        motivos.Should().OnlyContain(motivo => motivo != null && !motivo.Contains("segredo", StringComparison.Ordinal));
    }
}
```

Dois casos que a suíte por HTTP cobre e esta classe não: `azp` e `sub` em array ou numéricos. A biblioteca recusa o token ao lê-lo (`JsonWebToken` nem chega a ser construído), e não há como montar a entrada de `Recusar`.

- [ ] **Passo 2: Acrescentar os casos de forma à suíte negativa**

Em `tests/IdentityGateway.Api.FunctionalTests/AutenticacaoNegativaTests.cs`:

1. Depois do campo `AudienciaDaGatewayEOutra`:

```csharp
    private static readonly string[] AzpEmArrayDeUm = ["identity-gateway-demo"];

    private static readonly string[] AzpEmArrayDeDois = ["identity-gateway-demo", "outro-client"];

    private static readonly string[] TypEmArray = ["Bearer"];

```

2. Depois da propriedade `RecusadosPelaValidacao` (antes de `Aceitos`):

```csharp
    public static TheoryData<Func<IdentityGatewayApiFactory, string>> RecusadosPelaForma => new()
    {
        // O cabeçalho de um ID token também diz JWT; quem distingue é o claim typ.
        Com("typ = ID com a audiência certa", payload => payload["typ"] = "ID"),
        Com("typ em array", payload => payload["typ"] = TypEmArray),
        Com("sem typ", payload => payload.Remove("typ")),

        Com("azp fora da lista", payload => payload["azp"] = "outro-client"),
        Com("azp ausente", payload => payload.Remove("azp")),
        Com("azp vazio", payload => payload["azp"] = string.Empty),
        Com("azp em array de um", payload => payload["azp"] = AzpEmArrayDeUm),
        Com("azp em array de dois", payload => payload["azp"] = AzpEmArrayDeDois),
        Com("azp numérico", payload => payload["azp"] = 42),

        Com("sem sub", payload => payload.Remove("sub")),
        Com("sub que não é GUID", payload => payload["sub"] = "joao"),
        Com("sub no formato N", payload => payload["sub"] = Guid.NewGuid().ToString("N")),
        Com("sub em array", payload => payload["sub"] = new[] { Guid.NewGuid().ToString() }),

        // Foco de revisão 4: com o detalhe do erro ligado, o iss iria para o WWW-Authenticate, o Kestrel recusaria o
        // cabeçalho e o 401 viraria 500.
        Com("iss com caractere de controle", payload => payload["iss"] = Emissor + "\r\nX-Injetado: 1"),
    };

```

3. Depois da theory `TokenQueAValidacaoRecusa_Responde401SemDetalheNasDuasRotas`:

```csharp
    [Theory]
    [MemberData(nameof(RecusadosPelaForma))]
    public async Task TokenComAFormaErrada_Responde401SemDetalheNasDuasRotas(Func<IdentityGatewayApiFactory, string> token)
    {
        ArgumentNullException.ThrowIfNull(token);

        await ConferirRecusaAsync(token, TestContext.Current.CancellationToken);
    }

```

- [ ] **Passo 3: A factory do host em `Production` e os testes dele**

`tests/IdentityGateway.Api.FunctionalTests/ApiEmProducaoFactory.cs`:

```csharp
using System.Security.Cryptography;
using IdentityGateway.Api.FunctionalTests.Logs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// A Api com o ambiente <c>Production</c>, parametrizada pela configuração de cada caso.
/// </summary>
/// <remarks>
/// <para>
/// <b>Existe porque toda a suíte roda em Development.</b> Os ramos que só valem fora dele — a recusa do client de
/// demonstração, o <c>https</c> obrigatório nos metadados, a lista de clients vazia — nunca seriam exercitados, e um
/// "só em Development" garantido só pelo arquivo certo é verde vacuoso.
/// </para>
/// <para>
/// <b>Sem containers.</b> Os testes daqui terminam na subida ou na autenticação, antes de qualquer acesso ao banco: a
/// connection string aponta para a porta de descarte, e o despachante do Outbox fica desligado.
/// </para>
/// <para>
/// <b>Uma exceção declarada à regra "só configuração":</b> quando o caso usa o OIDC falso em HTTPS, o handler do canal
/// de metadados é trocado por um que confia <b>exatamente</b> no certificado autoassinado dele. Não toca nas opções
/// que os testes conferem (<c>RequireHttpsMetadata</c>, o prazo, a validação).
/// </para>
/// </remarks>
internal sealed class ApiEmProducaoFactory(
    IReadOnlyDictionary<string, string?> configuracao, HttpMessageHandler? metadados = null)
    : WebApplicationFactory<Program>
{
    private static readonly string ChaveFicticia = GerarChave();

    private readonly string _canalDeLogs = Guid.NewGuid().ToString("N");

    /// <summary>O que esta Api registrou em log.</summary>
    public ColetorDeLogsDaApi Logs => ColetorDeLogsDaApi.DoCanal(_canalDeLogs);

    /// <summary>A configuração mínima de um caso: os endereços do provedor e o que mais ele precisar.</summary>
    public static IReadOnlyDictionary<string, string?> Configuracao(
        string baseUrl, string? publicBaseUrl, params (string Chave, string? Valor)[] extras)
    {
        Dictionary<string, string?> valores = new() { ["Keycloak:Admin:BaseUrl"] = baseUrl };

        if (publicBaseUrl is not null)
        {
            valores["Keycloak:Admin:PublicBaseUrl"] = publicBaseUrl;
        }

        foreach ((string chave, string? valor) in extras)
        {
            valores[chave] = valor;
        }

        return valores;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Production");

        // O que a subida exige e nenhum caso exercita: banco (nunca alcançado) e a chave do service account.
        builder.UseSetting("Database:ConnectionString", "Host=127.0.0.1;Port=9;Database=x;Username=u;Password=p");
        builder.UseSetting("Outbox:Enabled", "false");
        builder.UseSetting("Keycloak:Admin:PrivateKeyPem", ChaveFicticia);

        builder.UseSetting("Serilog:Using:0", typeof(ColetorDeLogsDaApi).Assembly.GetName().Name);
        builder.UseSetting("Serilog:WriteTo:9:Name", nameof(ColetorDeLogsDaApiExtensions.ColetorEmMemoria));
        builder.UseSetting("Serilog:WriteTo:9:Args:canal", _canalDeLogs);

        foreach ((string chave, string? valor) in configuracao)
        {
            builder.UseSetting(chave, valor);
        }

        if (metadados is not null)
        {
            // Configure, e não PostConfigure: o canal de metadados é montado no PostConfigure do JwtBearer, a partir
            // do handler que encontrar.
            builder.ConfigureTestServices(services => services.Configure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme, jwt => jwt.BackchannelHttpHandler = metadados));
        }
    }

    private static string GerarChave()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }
}
```

`tests/IdentityGateway.Api.FunctionalTests/HostEmProducaoTests.cs`:

```csharp
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using IdentityGateway.Api.FunctionalTests.Oidc;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog.Events;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// O host com o ambiente <c>Production</c>: o que a subida recusa, e o que os pedidos recebem.
/// </summary>
/// <remarks>
/// <para>
/// <b>Com pedidos, e não só a subida.</b> "Sobe em Production" não diz se um token é aceito ou recusado ali. Cada
/// ramo de produção tem um pedido que o atravessa.
/// </para>
/// <para>
/// O OIDC falso serve HTTPS com um certificado autoassinado gerado em memória, porque fora de Development os
/// metadados só vêm por <c>https</c>; a Api confia nele pela impressão digital, e em mais nada.
/// </para>
/// </remarks>
public sealed class HostEmProducaoTests
{
    private const string EnderecoPublico = "https://sso.exemplo.test";

    private const string Emissor = EnderecoPublico + "/realms/identity-gateway";

    private const string ClientAdministrativo = "console-administrativo";

    private const string ListaComOClientAdministrativo = "Keycloak:Auth:AllowedClients:0";

    private static readonly string[] PlatformAdmin = ["platform-admin"];

    /// <summary>O emissor de teste, o OIDC falso em HTTPS e a Api em Production apontada para ele.</summary>
    private sealed class Cenario(EmissorDeTeste emissor, OidcFalso oidc, ApiEmProducaoFactory api) : IAsyncDisposable
    {
        public EmissorDeTeste Emissor { get; } = emissor;

        public ApiEmProducaoFactory Api { get; } = api;

        /// <summary>Um token de platform-admin obtido pelo client administrativo, com os ajustes do caso.</summary>
        public string Token(Action<Dictionary<string, object?>>? ajustar = null) => Emissor.Emitir(
            roles: PlatformAdmin,
            ajustar: payload =>
            {
                payload["azp"] = ClientAdministrativo;
                ajustar?.Invoke(payload);
            });

        public async ValueTask DisposeAsync()
        {
            await Api.DisposeAsync();
            await oidc.DisposeAsync();
            Emissor.Dispose();
        }
    }

    private static async Task<Cenario> SubirAsync(params (string Chave, string? Valor)[] extras)
    {
        EmissorDeTeste emissor = new(Emissor);
        OidcFalso oidc = await OidcFalso.IniciarAsync(emissor, https: true);
        ApiEmProducaoFactory api = new(
            ApiEmProducaoFactory.Configuracao(oidc.BaseUrl, EnderecoPublico, extras), QueConfiaSoEm(oidc.Certificado!));

        return new Cenario(emissor, oidc, api);
    }

    private static HttpClientHandler QueConfiaSoEm(X509Certificate2 certificado) => new()
    {
        ServerCertificateCustomValidationCallback = (_, apresentado, _, _) =>
            apresentado is not null
            && string.Equals(apresentado.Thumbprint, certificado.Thumbprint, StringComparison.Ordinal),
    };

    private static async Task<HttpResponseMessage> RegistrarSemCorpoAsync(
        HttpClient client, string token, CancellationToken ct)
    {
        using HttpRequestMessage pedido = new(HttpMethod.Post, "/api/v1/tenants");
        pedido.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

        return await client.SendAsync(pedido, ct);
    }

    private static IEnumerable<Exception> Cadeia(Exception excecao)
    {
        Queue<Exception> fila = new();
        fila.Enqueue(excecao);

        while (fila.TryDequeue(out Exception? atual))
        {
            yield return atual;

            if (atual is AggregateException agregada)
            {
                foreach (Exception interna in agregada.InnerExceptions)
                {
                    fila.Enqueue(interna);
                }
            }
            else if (atual.InnerException is not null)
            {
                fila.Enqueue(atual.InnerException);
            }
        }
    }

    /// <summary>Tenta subir o host e devolve a falha de validação das options — direta ou embrulhada pelo host.</summary>
    private static OptionsValidationException FalhaDeSubida(IReadOnlyDictionary<string, string?> configuracao)
    {
        using ApiEmProducaoFactory api = new(configuracao);

        Exception? falha = Record.Exception(() => api.CreateClient());

        falha.Should().NotBeNull("a subida precisava falhar com esta configuração");
        OptionsValidationException? validacao = Cadeia(falha!).OfType<OptionsValidationException>().FirstOrDefault();
        validacao.Should().NotBeNull($"a subida falhou com {falha!.GetType().Name}, e não por validação das options");

        return validacao!;
    }

    // ───────────────────────────── a subida ─────────────────────────────

    [Fact]
    public async Task ConfiguracaoDeProducaoValida_SobeEOLiveResponde()
    {
        // O controle: sem ele, "tudo falha ao subir em Production" deixaria os três testes de recusa verdes.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Cenario cenario = await SubirAsync((ListaComOClientAdministrativo, ClientAdministrativo));
        using HttpClient client = cenario.Api.CreateClient();

        HttpResponseMessage live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), ct);

        live.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void ClientDeDemonstracaoNaLista_ASubidaFalha()
    {
        // DT4. O client público de device flow é só do ambiente local.
        OptionsValidationException falha = FalhaDeSubida(ApiEmProducaoFactory.Configuracao(
            "https://keycloak.interno.test", EnderecoPublico,
            ("Keycloak:Auth:AllowedClients:0", ClientAdministrativo),
            ("Keycloak:Auth:AllowedClients:1", "identity-gateway-demo")));

        falha.Message.Should().Contain("AllowedClients").And.Contain("Development")
            .And.NotContain(ClientAdministrativo);
    }

    [Fact]
    public void TransporteEmHttpComAllowInsecureHttp_ASubidaFalha()
    {
        // A guarda HttpSoEmDesenvolvimento. Com a flag, o BaseUrl em http passa na regra "https ou a flag"; quem recusa
        // é a regra "a flag só em Development". É a prova que a fatia C deixou pendente.
        OptionsValidationException falha = FalhaDeSubida(ApiEmProducaoFactory.Configuracao(
            "http://keycloak:8080", EnderecoPublico, ("Keycloak:Admin:AllowInsecureHttp", "true")));

        falha.Message.Should().Contain("AllowInsecureHttp").And.Contain("Development");
    }

    [Fact]
    public void EnderecoPublicoEmHttp_ASubidaFalha()
    {
        // O outro ramo da mesma guarda: o emissor aceito em http diria que tokens emitidos em claro valem.
        OptionsValidationException falha = FalhaDeSubida(ApiEmProducaoFactory.Configuracao(
            "https://keycloak.interno.test", "http://sso.exemplo.test"));

        falha.Message.Should().Contain("PublicBaseUrl").And.Contain("Development");
    }

    [Fact]
    public void TransporteEmHttpSemAFlag_ASubidaFalha()
    {
        OptionsValidationException falha = FalhaDeSubida(ApiEmProducaoFactory.Configuracao(
            "http://keycloak:8080", EnderecoPublico));

        falha.Message.Should().Contain("BaseUrl").And.Contain("https");
    }

    // ───────────────────────────── os pedidos ─────────────────────────────

    [Fact]
    public async Task OpcoesResolvidas_ExigemHttpsNosMetadadosComPrazoDe5s()
    {
        await using Cenario cenario = await SubirAsync((ListaComOClientAdministrativo, ClientAdministrativo));

        JwtBearerOptions jwt = cenario.Api.Services
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);

        jwt.RequireHttpsMetadata.Should().BeTrue();
        jwt.BackchannelTimeout.Should().Be(TimeSpan.FromSeconds(5));
        jwt.IncludeErrorDetails.Should().BeFalse();
        jwt.MetadataAddress.Should().StartWith("https://127.0.0.1:");
    }

    [Fact]
    public async Task TokenDeClientDaLista_PassaDaAutenticacao()
    {
        // O controle dos pedidos: em Production, com HTTPS de ponta a ponta nos metadados, um token válido entra. O
        // POST sem corpo responde 400 — depois da autenticação e da policy.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Cenario cenario = await SubirAsync((ListaComOClientAdministrativo, ClientAdministrativo));
        using HttpClient client = cenario.Api.CreateClient();

        using HttpResponseMessage resposta = await RegistrarSemCorpoAsync(client, cenario.Token(), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AudienciaErrada_401SemDetalheDoErroTambemEmProducao()
    {
        // IncludeErrorDetails é falso em todo ambiente: não há ramo "em produção esconde, em desenvolvimento mostra".
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Cenario cenario = await SubirAsync((ListaComOClientAdministrativo, ClientAdministrativo));
        using HttpClient client = cenario.Api.CreateClient();

        using HttpResponseMessage resposta = await RegistrarSemCorpoAsync(
            client, cenario.Token(payload => payload["aud"] = "account"), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        resposta.Headers.WwwAuthenticate.ToString().Should().Be("Bearer").And.NotContain("error_description");
    }

    [Fact]
    public async Task TokenDoClientDeDemonstracao_EmProducaoLeva401()
    {
        // O demo não está na lista (e nem poderia). O token dele é válido em tudo — assinatura, emissor, audiência — e
        // é recusado só pelo azp.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Cenario cenario = await SubirAsync((ListaComOClientAdministrativo, ClientAdministrativo));
        using HttpClient client = cenario.Api.CreateClient();

        using HttpResponseMessage resposta = await RegistrarSemCorpoAsync(
            client, cenario.Emissor.Emitir(roles: PlatformAdmin), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListaDeClientsVazia_SobeAvisaERecusaTokenValido()
    {
        // Fail-closed: a API sobe (o provisionamento e os health checks não dependem de usuário), avisa na subida, e
        // nenhum token de usuário entra.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Cenario cenario = await SubirAsync();
        using HttpClient client = cenario.Api.CreateClient();

        using HttpResponseMessage resposta = await RegistrarSemCorpoAsync(client, cenario.Token(), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        cenario.Api.Logs.Eventos.Should().Contain(evento =>
            evento.Level == LogEventLevel.Warning
            && evento.MessageTemplate.Text.Contains("AllowedClients", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MetadadosFrios_401EmPoucosSegundosComOAvisoESemOTokenNoLog()
    {
        // DT5 e DT6. O provedor aceita a conexão e nunca responde, e a Api ainda não tem os metadados: o pedido leva
        // 401 (não 500), em cerca de 5 s (o prazo dos metadados; o padrão de 60 s reprovaria), e o log diz que o
        // problema é a busca das chaves — sem o token.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using TcpListener mudo = new(IPAddress.Loopback, 0);
        mudo.Start();
        int porta = ((IPEndPoint)mudo.LocalEndpoint).Port;

        using EmissorDeTeste emissor = new(Emissor);
        await using ApiEmProducaoFactory api = new(ApiEmProducaoFactory.Configuracao(
            $"https://127.0.0.1:{porta}", EnderecoPublico, (ListaComOClientAdministrativo, ClientAdministrativo)));
        using HttpClient client = api.CreateClient();
        string token = emissor.Emitir(roles: PlatformAdmin, ajustar: payload => payload["azp"] = ClientAdministrativo);

        var relogio = Stopwatch.StartNew();
        using HttpResponseMessage resposta = await RegistrarSemCorpoAsync(client, token, ct);
        relogio.Stop();

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        relogio.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(7));
        api.Logs.Eventos.Should().Contain(evento =>
            evento.Level == LogEventLevel.Warning
            && evento.MessageTemplate.Text.Contains("indisponíveis", StringComparison.Ordinal));
        api.Logs.Textos.Should().NotContain(texto =>
            texto.Contains(token, StringComparison.Ordinal)
            || texto.Contains(token.Split('.')[1], StringComparison.Ordinal));
    }
}
```

- [ ] **Passo 4: Rodar e ver falhar**

Run: `dotnet build IdentityGateway.slnx`
Expected: FAIL de compilação — `CS0103: FormaDoAccessToken` (em `FormaDoAccessTokenTests`).

Para ver o vermelho da suíte por HTTP antes de implementar, tire o arquivo que não compila do build por um instante — `mv tests/IdentityGateway.Api.FunctionalTests/FormaDoAccessTokenTests.cs tests/IdentityGateway.Api.FunctionalTests/FormaDoAccessTokenTests.cs.txt` — e:

Run (Docker ligado): `dotnet test tests/IdentityGateway.Api.FunctionalTests --filter-class "*AutenticacaoNegativaTests"`
Expected: FAIL em **nove** casos de `TokenComAFormaErrada_…` — `typ = ID`, `typ em array`, `sem typ`, `azp fora da lista`, `azp ausente`, `azp vazio`, `sem sub`, `sub que não é GUID` e `sub no formato N` — que hoje recebem `400`/`404` (o token é aceito). Os outros cinco já passam, e é esperado: `azp` em array, `azp` numérico e `sub` em array são recusados pela própria biblioteca ao ler o token, e o `iss` com caractere de controle, pelo emissor estrito da Tarefa 7.

Run: `dotnet test tests/IdentityGateway.Api.FunctionalTests --filter-class "*HostEmProducaoTests"`
Expected: FAIL em `TokenDoClientDeDemonstracao_EmProducaoLeva401` e em `ListaDeClientsVazia_SobeAvisaERecusaTokenValido` (os dois tokens entram: ainda não há checagem de `azp`). Os demais passam: provam a Tarefa 6 e a 7 com o host em Production.

Devolver o arquivo ao build antes de seguir: `mv tests/IdentityGateway.Api.FunctionalTests/FormaDoAccessTokenTests.cs.txt tests/IdentityGateway.Api.FunctionalTests/FormaDoAccessTokenTests.cs`.

- [ ] **Passo 5: A forma do token e o aviso da subida**

`src/IdentityGateway.Api/Authentication/FormaDoAccessToken.cs`:

```csharp
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
```

`src/IdentityGateway.Api/Authentication/AvisoDeClientsPermitidos.cs`:

```csharp
using IdentityGateway.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace IdentityGateway.Api.Authentication;

/// <summary>
/// Avisa, na subida, quando a lista de clients permitidos está vazia.
/// </summary>
/// <remarks>
/// Lista vazia é configuração válida e fechada: a API sobe e recusa todo token de usuário. Sem o aviso, o primeiro
/// sintoma seria um <c>401</c> em cada pedido, apontando para o token de quem chama — e o motivo estaria na
/// configuração de quem opera. Um <c>IHostedService</c>, e não uma linha no <c>Program.cs</c>, para rodar em toda
/// forma de subir o host, inclusive nos testes.
/// </remarks>
internal sealed class AvisoDeClientsPermitidos(
    IOptions<AccessTokenValidationOptions> validacao, ILoggerFactory loggers) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (validacao.Value.AllowedClients.Count == 0)
        {
            AutenticacaoLogs.NenhumClientPermitido(loggers.CreateLogger(ValidacaoDoAccessToken.Categoria));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

- [ ] **Passo 6: Ligar as duas peças**

Em `src/IdentityGateway.Api/Authentication/ValidacaoDoAccessToken.cs`:

1. Acrescentar o `using`:

```csharp
using Microsoft.IdentityModel.JsonWebTokens;
```

2. Em `Configurar`, o bloco dos eventos passa a ser:

```csharp
        jwt.Events = new JwtBearerEvents
        {
            OnTokenValidated = contexto => AoValidar(contexto, validacao.AllowedClients),
            OnAuthenticationFailed = AoFalhar,
        };
```

3. Acrescentar, logo depois do método `Configurar`:

```csharp
    /// <summary>
    /// Confere a forma do token depois de a biblioteca validar assinatura, emissor, audiência e prazo.
    /// </summary>
    /// <remarks>
    /// <c>Fail</c>, e não exceção: o resultado é o mesmo <c>401</c> de qualquer token recusado, e o motivo — texto fixo,
    /// sem valor do token — vai para o log em <c>Debug</c>.
    /// </remarks>
    private static Task AoValidar(TokenValidatedContext contexto, IReadOnlyList<string> clientsPermitidos)
    {
        string? motivo = contexto.SecurityToken is JsonWebToken token
            ? FormaDoAccessToken.Recusar(token, clientsPermitidos)
            : "token que não é um JWT";

        if (motivo is not null)
        {
            ILogger logger = contexto.HttpContext.RequestServices
                .GetRequiredService<ILoggerFactory>().CreateLogger(Categoria);

            AutenticacaoLogs.FormaRecusada(logger, motivo);
            contexto.Fail(motivo);
        }

        return Task.CompletedTask;
    }
```

(o `logger` numa variável, e não como argumento da chamada de log: o analisador CA1873 recusa argumento caro numa chamada que pode estar desligada.)

Em `src/IdentityGateway.Api/DependencyInjection.cs`, em `AddAutenticacao`, logo depois do bloco `services.AddOptions<JwtBearerOptions>(…)`:

```csharp

        // Lista de clients vazia é configuração válida e fechada; o aviso sai na subida, não no primeiro 401.
        services.AddHostedService<AvisoDeClientsPermitidos>();
```

Em `src/IdentityGateway.Api/IdentityGateway.Api.csproj`, no `ItemGroup` do Carter, depois da linha do `Microsoft.AspNetCore.Authentication.JwtBearer`:

```xml

    <!--
      JsonWebToken e Base64UrlEncoder, usados na conferência da forma do access token. Já vinha transitivo pelo
      JwtBearer; declarado porque é usado no código. A versão é a do Directory.Packages.props, a mesma que o
      JwtBearer resolve.
    -->
    <PackageReference Include="Microsoft.IdentityModel.JsonWebTokens" />
```

- [ ] **Passo 7: Rodar e ver passar**

Run: `dotnet build IdentityGateway.slnx`
Expected: `0 Aviso(s)`, `0 Erro(s)`.

Run (Docker ligado): `dotnet test tests/IdentityGateway.Api.FunctionalTests`
Expected: PASS, `falhou: 0`. Em especial `FormaDoAccessTokenTests` (14: nove `[Fact]` e a `[Theory]` com cinco casos), os 14 casos de `TokenComAFormaErrada_…` e os 11 testes de `HostEmProducaoTests`. `MetadadosFrios_…` leva cerca de 5 s — é o prazo dos metadados, e é o que o teste mede.

Run: `dotnet test tests/IdentityGateway.ArchitectureTests`
Expected: PASS.

- [ ] **Passo 8: 🧪 Provas por mutação**

Antes da primeira: `git add -A src tests`. Reverter cada uma com `git restore <arquivo>`.

Run (a cada mutação): `dotnet test tests/IdentityGateway.Api.FunctionalTests`

| # | Mutação | Vermelho esperado |
|---|---|---|
| 1 | Em `Configurar`, tirar a linha `OnTokenValidated = …` | Os nove casos de forma que dependem das nossas checagens (`typ = ID`, `typ em array`, `sem typ`, `azp fora da lista`, `azp ausente`, `azp vazio`, `sem sub`, `sub que não é GUID`, `sub no formato N`); `TokenDoClientDeDemonstracao_EmProducaoLeva401`; `ListaDeClientsVazia_…` |
| 2 | Em `FormaDoAccessToken.Recusar`, aceitar qualquer `azp` quando a lista está vazia (acrescentar `clientsPermitidos.Count > 0 &&` antes do `!clientsPermitidos.Contains`) | `ListaDeClientsVazia_SobeAvisaERecusaTokenValido`; `ListaVazia_RecusaTodoToken` |
| 3 | Trocar `StringComparer.Ordinal` por `StringComparer.OrdinalIgnoreCase` na comparação do `azp` | `AzpComOutraCaixa_ERecusado` |
| 4 | Apagar o bloco do `typ` | `typ = ID com a audiência certa`, `typ em array`, `sem typ`; `TypDeIdToken_ERecusado`, `TypEmArrayDeUmElemento_ERecusado`, `TypAusente_ERecusado` |
| 5 | Em `Texto`, fazer o que a leitura pelos claims faria: antes do `return` final, devolver `valor[0].GetString()` quando `valor.ValueKind == JsonValueKind.Array && valor.GetArrayLength() == 1` | `typ em array`; `TypEmArrayDeUmElemento_ERecusado` |
| 6 | Apagar o bloco do `sub` | `sem sub`, `sub que não é GUID`, `sub no formato N`; `SubForaDoFormatoD_ERecusado` (todos) |
| 7 | No bloco do `sub`, trocar `Guid.TryParseExact(sub, "D", out _)` por `Guid.TryParse(sub, out _)` e tirar o `{ Length: 36 }` | `sub no formato N`; `SubForaDoFormatoD_ERecusado` (os casos `N`, `B` e o do espaço) |
| 8 | Só tirar o `{ Length: 36 }`, mantendo o `TryParseExact` | `SubForaDoFormatoD_ERecusado(" 0199a000-…")` — o caso do espaço |
| 9 | Em `DependencyInjection`, apagar a linha do `AddHostedService<AvisoDeClientsPermitidos>()` | `ListaDeClientsVazia_SobeAvisaERecusaTokenValido` (o `401` continua; falta o aviso) |
| 10 | Em `ValidacaoDoAccessToken.AoFalhar`, apagar a chamada `AutenticacaoLogs.ChavesIndisponiveis(logger, tipo);` | `MetadadosFrios_…` (sem o aviso) |
| 11 | Em `Configurar`, apagar a linha `jwt.BackchannelTimeout = PrazoDosMetadados;` | `MetadadosFrios_…` (o pedido leva os 60 s do padrão) e `OpcoesResolvidas_ExigemHttpsNosMetadadosComPrazoDe5s`; `OsMetadados_VemPeloEnderecoDeTransporteComPrazoCurto` |
| 12 | No registro da option (Tarefa 6), `validacao.RequireHttpsMetadata = false;` | `OpcoesResolvidas_ExigemHttpsNosMetadadosComPrazoDe5s` |
| 13 | `jwt.IncludeErrorDetails = true;` | `AudienciaErrada_401SemDetalheDoErroTambemEmProducao` e toda a suíte negativa |
| 14 | No registro da option (Tarefa 6), apagar o `.Validate<IHostEnvironment>(ClientDeDemonstracaoSoEmDesenvolvimento, …)` | `ClientDeDemonstracaoNaLista_ASubidaFalha` |
| 15 | **A guarda `HttpSoEmDesenvolvimento` sempre verdadeira:** em `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakServiceCollectionExtensions.cs`, pôr `return true;` na primeira linha do corpo de `HttpSoEmDesenvolvimento` | `TransporteEmHttpComAllowInsecureHttp_ASubidaFalha` e `EnderecoPublicoEmHttp_ASubidaFalha` (a subida deixa de falhar). `ConfiguracaoDeProducaoValida_SobeEOLiveResponde` continua verde |

**Sobre a mutação 15.** É a prova por mutação que a fatia C deixou pendente: lá, a proteção automática do ambiente de execução recusou rodar os testes com a verificação de `https` enfraquecida. Tente executá-la. Se o ambiente recusar de novo, **não contorne**: registre "não executada — recusada pelo ambiente" no relatório da tarefa, reverta o arquivo, e o roteiro manual vai para o handoff (Tarefa 12), para o autor rodar. Ela não é critério de aceite da tarefa.

Mutação equivalente, **não** executada: ler o `azp` pelos claims, aceitando array. A biblioteca recusa, ao ler o token, o `azp` que não é texto (visto no protótipo deste plano): os casos `azp em array` e `azp numérico` continuam `401` com ou sem as nossas checagens. A leitura pelo JSON é provada pelo `typ em array` (mutação 5).

- [ ] **Passo 9: Commit**

```bash
git add -A src tests
git commit -m "feat: azp, typ e sub conferidos na autenticacao, e o host provado em Production

Depois de a biblioteca validar assinatura, emissor, audiencia e prazo, a
Api confere a forma do token pelo JSON do payload: azp presente na lista
de clients permitidos, typ igual a Bearer e sub como GUID no formato D.
Na autenticacao, e nao numa policy, para valer tambem nas rotas com policy
propria. Lista de clients vazia fecha, com aviso na subida.

Testes com o host em Production, com pedidos: a subida recusa o client de
demonstracao, AllowInsecureHttp e endereco publico em http; com o OIDC
falso em HTTPS, um token de client da lista entra, a audiencia errada leva
401 sem detalhe, o token do demo leva 401, a lista vazia avisa e recusa, e
metadados frios dao 401 em cerca de 5 s com o aviso e sem o token no log.

Mutacoes: sem o OnTokenValidated, lista vazia aceitando tudo, azp sem
caixa, sem typ, typ lido como claim, sem sub, TryParse no sub, sem o
tamanho do sub, sem o aviso, sem o log das chaves, prazo padrao dos
metadados, RequireHttpsMetadata fixo, detalhe do erro, demo fora de
Development e a guarda de http."
```

Se a mutação 15 não foi executada, troque "e a guarda de http" por "(a guarda de http ficou para o autor)" na mensagem.

Run: `git log -1 --format=%B | grep -Eci "co-authored|generated with"`
Expected: `0`.

---

### Tarefa 9: A Api contra o Keycloak real, e o e-mail fora de log e trace

Spec: §5.1 (linhas "Coleção com Keycloak real na API" e "Vazamento do e-mail", D1), §5.2 (casos `K`), §5.3 (as testemunhas `K`), §5.5.

Os testes funcionais validam tokens que o próprio teste forjou. Esta tarefa fecha o que eles não provam: que um token **emitido pelo Keycloak** atravessa a Api, que os tokens que o Keycloak emite para outros fins são recusados, e que a forma do token forjado é a do token real. E estende o teste de vazamento: um token que carregue e-mail não o deixa em corpo, cabeçalho, log nem trace.

Nenhum código de produção muda.

**Arquivos:**
- Modify: `tests/IdentityGateway.Api.FunctionalTests/IdentityGateway.Api.FunctionalTests.csproj`
- Create: `tests/IdentityGateway.Api.FunctionalTests/ApiComKeycloakFactory.cs`, `ColecaoComKeycloak.cs`, `TokensDoKeycloakNaApiTests.cs`
- Create: `tests/IdentityGateway.Api.FunctionalTests/Logs/CapturaDeSpans.cs`, `VazamentoDoEmailNoTokenTests.cs`

**Interfaces:**
- Consome: `KeycloakFixture` (`BaseUrl`, `HostnamePublico`, `Chaves`, `ClientDeConta`, `ClientDeDemonstracao`, `NovoPlatformAdminAsync`, `NovoUsuarioAsync`, `CriarHarness`, `CriarUsuarioComoMasterAsync`, `AtribuirPapelDeRealmComoMasterAsync`, `EmailUnico`), `HarnessDeLogin`, `PayloadDoJwt`, `SenhasDeTeste` (Tarefas 1 a 5); `IdentityGatewayApiFactory.Emissor`, `EmissorDeTeste`, `ColetorDeLogsDaApi` (Tarefa 7); `ServiceAccountTokenCache.ObterAsync(ct)` (Infrastructure, `internal`, visível ao projeto funcional).
- Produz:
  - `public sealed class ApiComKeycloakFactory : WebApplicationFactory<Program>, IAsyncLifetime` — `KeycloakFixture Keycloak { get; }`; `Task<HttpClient> CriarClienteComoAsync(UsuarioDeTeste usuario, CancellationToken ct)` (device flow pelo client de demonstração, token no `Authorization`).
  - `public sealed class ColecaoComKeycloak : ICollectionFixture<ApiComKeycloakFactory>` com `public const string Nome = "Api com Keycloak real"`.
  - `internal sealed class ExportadorDeSpansEmMemoria : BaseExporter<Activity>` com `IReadOnlyList<string> Textos`; `internal static class FormasDeUmSegredo` com `static IReadOnlyList<string> De(string segredo)`.

- [ ] **Passo 1: O projeto funcional passa a usar a biblioteca do Keycloak**

Em `tests/IdentityGateway.Api.FunctionalTests/IdentityGateway.Api.FunctionalTests.csproj`:

1. No `ItemGroup` das referências de projeto:

```xml

    <!-- O fixture do Keycloak e o harness de login: a mesma biblioteca que os testes de integração usam. -->
    <ProjectReference Include="..\IdentityGateway.Testing.Keycloak\IdentityGateway.Testing.Keycloak.csproj" />
```

2. No `ItemGroup` dos pacotes:

```xml

    <!--
      BaseExporter e SimpleActivityExportProcessor, para o teste de vazamento ver os spans que a Api exportaria.
      Vem transitivo pela Api; declarado porque é usado no código.
    -->
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" />
```

- [ ] **Passo 2: A factory com Keycloak real e a coleção**

`tests/IdentityGateway.Api.FunctionalTests/ApiComKeycloakFactory.cs`:

```csharp
using System.Net.Http.Headers;
using IdentityGateway.Infrastructure.Persistence;
using IdentityGateway.Testing.Keycloak;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// A Api inteira contra um Keycloak 26.7.4 de verdade, com o realm do repositório.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sem chave de teste.</b> A <c>IdentityGatewayApiFactory</c> valida tokens assinados por uma chave que o próprio
/// teste gerou. Aqui a Api busca os metadados e as chaves no Keycloak, e os tokens vêm do device flow — o caminho de
/// quem usa a API de fato.
/// </para>
/// <para>
/// <b>Uma por coleção</b>, porque sobe três containers (PostgreSQL, Redis e Keycloak, com o mailpit dele). As classes
/// que a usam ficam na coleção <see cref="ColecaoComKeycloak"/> e rodam em série.
/// </para>
/// <para>
/// <b>A lista de clients aceitos ganha o client de device flow do fixture.</b> É de propósito: o token dele tem o
/// <c>azp</c> aceito e <b>não</b> tem a audiência da Gateway, e por isso o <c>401</c> que ele recebe só pode vir da
/// audiência.
/// </para>
/// </remarks>
public sealed class ApiComKeycloakFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("identitygateway_keycloak")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

    /// <summary>O Keycloak desta coleção.</summary>
    public KeycloakFixture Keycloak { get; } = new();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), Keycloak.InitializeAsync().AsTask());

        using IServiceScope scope = Services.CreateScope();
        AppDbContext contexto = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await contexto.Database.MigrateAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Task.WhenAll(
            _postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask(), Keycloak.DisposeAsync().AsTask());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");
        builder.UseSetting("Database:ConnectionString", _postgres.GetConnectionString());
        builder.UseSetting("Redis:ConnectionString", _redis.GetConnectionString());

        // Desligado na D1: nenhum teste daqui espera o provisionamento. A D2 o liga, para o admin convidado existir.
        builder.UseSetting("Outbox:Enabled", "false");

        // Os mesmos três valores do compose: transporte pela porta mapeada, emissor público pelo KC_HOSTNAME do
        // fixture, e a chave que o realm registrou.
        builder.UseSetting("Keycloak:Admin:BaseUrl", Keycloak.BaseUrl);
        builder.UseSetting("Keycloak:Admin:PublicBaseUrl", KeycloakFixture.HostnamePublico);
        builder.UseSetting("Keycloak:Admin:PrivateKeyPem", Keycloak.Chaves.PemPrivado);

        // O índice 0 é o identity-gateway-demo, do appsettings.Development.json.
        builder.UseSetting("Keycloak:Auth:AllowedClients:1", KeycloakFixture.ClientDeConta);
    }

    /// <summary>
    /// Um cliente HTTP da Api autenticado como o usuário, com um token obtido pelo device flow no client de
    /// demonstração.
    /// </summary>
    public async Task<HttpClient> CriarClienteComoAsync(UsuarioDeTeste usuario, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(usuario);

        using HarnessDeLogin harness = Keycloak.CriarHarness();
        TokensDeUsuario tokens = await harness.TokenPorDispositivoAsync(usuario.Email, usuario.Senha, cancellationToken);

        HttpClient cliente = CreateClient();
        cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        return cliente;
    }
}
```

`tests/IdentityGateway.Api.FunctionalTests/ColecaoComKeycloak.cs`:

```csharp
namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// As classes de teste que usam a Api contra o Keycloak real: uma factory para todas, uma classe de cada vez.
/// </summary>
[CollectionDefinition(Nome)]
public sealed class ColecaoComKeycloak : ICollectionFixture<ApiComKeycloakFactory>
{
    public const string Nome = "Api com Keycloak real";
}
```

- [ ] **Passo 3: Os testes com tokens do Keycloak**

`tests/IdentityGateway.Api.FunctionalTests/TokensDoKeycloakNaApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IdentityGateway.Api.FunctionalTests.Oidc;
using IdentityGateway.Infrastructure.Identity.Keycloak;
using IdentityGateway.Testing.Keycloak;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// Tokens emitidos pelo Keycloak de verdade, atravessando a Api de verdade.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cada <c>401</c> daqui vem acompanhado da forma do token que o recebeu.</b> "A API respondeu 401" sozinho é
/// sobredeterminado — pode ser a audiência, o <c>azp</c>, o emissor, a assinatura. O teste afirma antes o que o token
/// tem e o que não tem, para o <c>401</c> ser o esperado pelo motivo esperado.
/// </para>
/// <para>
/// <b>Um platform-admin por teste</b> (<c>NovoPlatformAdminAsync</c>): o link de ações é de uso único.
/// </para>
/// </remarks>
[Collection(ColecaoComKeycloak.Nome)]
public sealed class TokensDoKeycloakNaApiTests(ApiComKeycloakFactory api)
{
    private const string AudienciaDaGateway = "identity-gateway-api";

    private static readonly string[] SoTenantAdmin = ["tenant-admin"];

    private static readonly string[] SemPapeis = [];

    private static object CorpoDoRegistro() => new
    {
        name = "Acme Corp",
        slug = $"kc-{Guid.NewGuid():N}"[..20],
        planCode = "free",
        initialAdminEmail = KeycloakFixture.EmailUnico(),
    };

    private async Task<HttpResponseMessage> RegistrarComAsync(string accessToken, CancellationToken ct)
    {
        using HttpClient client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await client.PostAsJsonAsync("/api/v1/tenants", CorpoDoRegistro(), ct);
    }

    [Fact]
    public async Task PlatformAdminDoKeycloak_RegistraUmTenant()
    {
        // O critério do M0: um token do Keycloak, obtido pelo device flow, numa rota protegida.
        CancellationToken ct = TestContext.Current.CancellationToken;
        UsuarioDeTeste admin = await api.Keycloak.NovoPlatformAdminAsync(ct);
        using HttpClient client = await api.CriarClienteComoAsync(admin, ct);

        using HttpResponseMessage resposta = await client.PostAsJsonAsync("/api/v1/tenants", CorpoDoRegistro(), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Accepted);
        resposta.Headers.Location.Should().NotBeNull();
    }

    [Fact]
    public async Task TenantAdminDoKeycloak_AutenticaMasNaoRegistraTenant()
    {
        // 403, e não 401: o token é aceito; o que falta é o papel.
        CancellationToken ct = TestContext.Current.CancellationToken;
        UsuarioDeTeste admin = await api.Keycloak.NovoUsuarioAsync(SoTenantAdmin, Guid.NewGuid().ToString(), ct);
        using HttpClient client = await api.CriarClienteComoAsync(admin, ct);

        using HttpResponseMessage resposta = await client.PostAsJsonAsync("/api/v1/tenants", CorpoDoRegistro(), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PonteDeContrato_OsClaimsDoTokenRealTemOsTiposDosDoEmissorDeTeste()
    {
        // Os testes funcionais confiam que o emissor de teste imita o token real. Aqui a imitação é conferida: os
        // mesmos claims, com os mesmos tipos JSON. Um claim a mais no token real (um mapper novo no realm) ou um tipo
        // diferente (aud virando array) reprova — antes de os testes funcionais passarem a provar outra coisa.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string tenant = Guid.NewGuid().ToString();
        UsuarioDeTeste admin = await api.Keycloak.NovoUsuarioAsync(SoTenantAdmin, tenant, ct);
        using HarnessDeLogin harness = api.Keycloak.CriarHarness();
        TokensDeUsuario tokens = await harness.TokenPorDispositivoAsync(admin.Email, admin.Senha, ct);
        using EmissorDeTeste emissor = new("https://irrelevante.test/realms/identity-gateway");

        JsonElement real = PayloadDoJwt.Ler(tokens.AccessToken);
        JsonElement forjado = JsonSerializer.SerializeToElement(emissor.Payload(roles: SoTenantAdmin, tenantId: tenant));

        Dictionary<string, JsonValueKind> tiposDoReal = real.EnumerateObject()
            .ToDictionary(claim => claim.Name, claim => claim.Value.ValueKind);
        Dictionary<string, JsonValueKind> tiposDoForjado = forjado.EnumerateObject()
            .ToDictionary(claim => claim.Name, claim => claim.Value.ValueKind);

        tiposDoForjado.Should().BeEquivalentTo(tiposDoReal);
    }

    [Fact]
    public async Task TokenDoClientDeDeviceFlowDoFixture_AzpAceitoSemAAudiencia_401()
    {
        // O client está na lista de azp desta factory e não tem o scope gateway-api: o 401 só pode vir da audiência.
        // É a testemunha, no Keycloak real, de que ValidateAudience está ligado e de que gateway-api não é default.
        CancellationToken ct = TestContext.Current.CancellationToken;
        UsuarioDeTeste usuario = await api.Keycloak.NovoPlatformAdminAsync(ct);
        using HarnessDeLogin harness = api.Keycloak.CriarHarness(KeycloakFixture.ClientDeConta);
        TokensDeUsuario tokens = await harness.TokenPorDispositivoAsync(usuario.Email, usuario.Senha, ct);
        JsonElement payload = PayloadDoJwt.Ler(tokens.AccessToken);

        payload.GetProperty("azp").GetString().Should().Be(KeycloakFixture.ClientDeConta);
        PayloadDoJwt.Audiencias(payload).Should().NotContain(AudienciaDaGateway);

        using HttpResponseMessage resposta = await RegistrarComAsync(tokens.AccessToken, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TokenDoServiceAccountDaGateway_401()
    {
        // A chave da Gateway abre a Admin API do Keycloak; não pode abrir a própria Gateway. O token do service account
        // não tem a audiência (sem gateway-api) nem azp aceito.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string token = await api.Services.GetRequiredService<ServiceAccountTokenCache>().ObterAsync(ct);
        JsonElement payload = PayloadDoJwt.Ler(token);

        payload.GetProperty("azp").GetString().Should().Be("identity-gateway");
        PayloadDoJwt.Audiencias(payload).Should().NotContain(AudienciaDaGateway);

        using HttpResponseMessage resposta = await RegistrarComAsync(token, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TokenPorSenhaDoAdminCliDoRealm_401()
    {
        // ADR-003: o admin-cli embutido do realm mantém o direct grant (não é declarado no JSON, e o Keycloak o cria
        // assim). O que a Gateway garante é que nenhum token obtido por senha é aceito por ela: o do admin-cli é um
        // token leve, sem audiência, e o azp não está na lista. O usuário aqui até tem o papel platform-admin.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string email = KeycloakFixture.EmailUnico();
        string senha = SenhasDeTeste.Gerar();
        string id = await api.Keycloak.CriarUsuarioComoMasterAsync(
            new
            {
                username = email,
                email,
                emailVerified = true,
                firstName = "Por",
                lastName = "Senha",
                enabled = true,
                credentials = new[] { new { type = "password", value = senha, temporary = false } },
            },
            ct);
        await api.Keycloak.AtribuirPapelDeRealmComoMasterAsync(id, "platform-admin", ct);

        using HttpClient keycloak = new() { BaseAddress = new Uri($"{api.Keycloak.BaseUrl}/") };
        using FormUrlEncodedContent corpo = new(
        [
            new("grant_type", "password"),
            new("client_id", "admin-cli"),
            new("username", email),
            new("password", senha),
        ]);
        using HttpResponseMessage emitido = await keycloak.PostAsync(
            new Uri($"realms/{KeycloakFixture.Realm}/protocol/openid-connect/token", UriKind.Relative), corpo, ct);
        emitido.StatusCode.Should().Be(HttpStatusCode.OK, "controle: o Keycloak emite o token por senha no admin-cli");
        JsonElement resposta = await emitido.Content.ReadFromJsonAsync<JsonElement>(ct);
        string token = resposta.GetProperty("access_token").GetString()!;
        JsonElement payload = PayloadDoJwt.Ler(token);

        payload.GetProperty("azp").GetString().Should().Be("admin-cli");
        PayloadDoJwt.Audiencias(payload).Should().NotContain(AudienciaDaGateway);

        using HttpResponseMessage naApi = await RegistrarComAsync(token, ct);

        naApi.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ApiComOEnderecoPublicoErrado_RecusaTokenRealDoKeycloak()
    {
        // O emissor aceito é o configurado, e não o que o discovery do Keycloak anuncia. Com o PublicBaseUrl errado, os
        // metadados continuam vindo (pelo BaseUrl), a assinatura confere — e o token é recusado pelo emissor. Sem o
        // IssuerValidator estrito, este teste ficaria 202: a biblioteca aceitaria o emissor do discovery.
        CancellationToken ct = TestContext.Current.CancellationToken;
        UsuarioDeTeste admin = await api.Keycloak.NovoPlatformAdminAsync(ct);
        using HarnessDeLogin harness = api.Keycloak.CriarHarness();
        TokensDeUsuario tokens = await harness.TokenPorDispositivoAsync(admin.Email, admin.Senha, ct);
        using WebApplicationFactory<Program> comEmissorErrado = api.WithWebHostBuilder(builder =>
            builder.UseSetting("Keycloak:Admin:PublicBaseUrl", "http://outro-endereco.test:8081"));
        using HttpClient client = comEmissorErrado.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        using HttpResponseMessage resposta = await client.PostAsJsonAsync("/api/v1/tenants", CorpoDoRegistro(), ct);
        using HttpResponseMessage controle = await RegistrarComAsync(tokens.AccessToken, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        controle.StatusCode.Should().Be(HttpStatusCode.Accepted, "o mesmo token entra na Api com o endereço certo");
    }

    [Fact]
    public async Task UsuarioSemPapelDoCatalogo_AutenticaELeva403()
    {
        // O token sai sem o claim roles. A Api trata como "sem papel": 403, não erro.
        CancellationToken ct = TestContext.Current.CancellationToken;
        UsuarioDeTeste usuario = await api.Keycloak.NovoUsuarioAsync(SemPapeis, tenantId: null, ct);
        using HttpClient client = await api.CriarClienteComoAsync(usuario, ct);

        using HttpResponseMessage resposta = await client.PostAsJsonAsync("/api/v1/tenants", CorpoDoRegistro(), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Ready_ComKeycloakPostgresERedisDePe_Responde200()
    {
        // O /health/ready com tudo de verdade: o token do service account sai, com manage-users.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = api.CreateClient();

        using HttpResponseMessage resposta = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
```

- [ ] **Passo 4: Rodar os testes com Keycloak real**

Run: `dotnet build IdentityGateway.slnx`
Expected: `0 Aviso(s)`, `0 Erro(s)`.

Run (Docker ligado): `dotnet test tests/IdentityGateway.Api.FunctionalTests --filter-class "*TokensDoKeycloakNaApiTests"`
Expected: PASS — `total: 9`. A classe leva de 1 a 2 minutos: o Keycloak sobe uma vez, e cada device flow espera 5 s.

Estes testes nascem verdes — o código que eles exercitam é o das Tarefas 2 a 8. O vermelho de cada um é observado por mutação, no Passo 7.

- [ ] **Passo 5: A captura de spans e as formas de um segredo**

`tests/IdentityGateway.Api.FunctionalTests/Logs/CapturaDeSpans.cs`:

```csharp
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
```

- [ ] **Passo 6: O teste de vazamento do e-mail no token**

O token do realm não carrega e-mail (DT11), mas a Api não pode depender disso: um client configurado de outro jeito, ou um token forjado, traz `email` e `preferred_username`. O que a Api recebe num token não pode aparecer em resposta, log nem trace — nem em texto, nem dentro do token codificado.

`tests/IdentityGateway.Api.FunctionalTests/VazamentoDoEmailNoTokenTests.cs`:

```csharp
using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using IdentityGateway.Api.FunctionalTests.Logs;
using IdentityGateway.Api.FunctionalTests.Oidc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// Um token que carrega e-mail não o deixa em corpo, cabeçalho, log nem trace — no sucesso e em cada recusa.
/// </summary>
/// <remarks>
/// <para>
/// <b>Com o log no nível mais falador que a Api teria</b>: <c>Debug</c> em tudo, inclusive em
/// <c>Microsoft.AspNetCore</c>, onde o handler do JwtBearer registra a falha de validação com a exceção. É o cenário
/// de quem está diagnosticando um problema de autenticação em produção — o pior para vazar.
/// </para>
/// <para>
/// <b>Procura quatro formas</b>: o e-mail em texto; o e-mail em base64url, nos três alinhamentos possíveis (ele viaja
/// dentro do payload); o token inteiro; e o segmento do payload.
/// </para>
/// <para>
/// <b>Nenhuma asserção de ausência sem a de presença.</b> Cada caso confere antes que o log e o trace daquele pedido
/// foram capturados (pela rota, que leva um GUID único): sem isso, "não achei o e-mail" passaria com o coletor vazio.
/// </para>
/// <para>
/// Duas exceções declaradas à regra "só configuração" da factory: o processador de spans em memória, registrado por
/// <c>ConfigureTestServices</c>, e o nível de log.
/// </para>
/// </remarks>
public sealed class VazamentoDoEmailNoTokenTests : IClassFixture<IdentityGatewayApiFactory>, IDisposable
{
    private static readonly string[] PlatformAdmin = ["platform-admin"];

    private static readonly RSA ChaveForasteira = RSA.Create(2048);

    private readonly IdentityGatewayApiFactory _factory;
    private readonly WebApplicationFactory<Program> _comCaptura;
    private readonly ExportadorDeSpansEmMemoria _spans = new();
    private readonly string _canal = Guid.NewGuid().ToString("N");

    public VazamentoDoEmailNoTokenTests(IdentityGatewayApiFactory factory)
    {
        _factory = factory;
        _comCaptura = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Serilog:MinimumLevel:Default", "Debug");
            builder.UseSetting("Serilog:MinimumLevel:Override:Microsoft.AspNetCore", "Debug");
            builder.UseSetting("Serilog:WriteTo:9:Args:canal", _canal);
            builder.ConfigureTestServices(services => services.ConfigureOpenTelemetryTracerProvider(
                tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(_spans))));
        });
    }

    public void Dispose()
    {
        _comCaptura.Dispose();
        _spans.Dispose();
    }

    private static long Agora => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static TheoryDataRow<Func<EmissorDeTeste, Dictionary<string, object?>, string>, HttpStatusCode> Caso(
        string rotulo, HttpStatusCode esperado, Func<EmissorDeTeste, Dictionary<string, object?>, string> assinar) =>
        new(assinar, esperado) { Label = rotulo };

    /// <summary>
    /// Cada caso recebe o payload já com o e-mail e decide como estragá-lo e assiná-lo.
    /// </summary>
    public static TheoryData<Func<EmissorDeTeste, Dictionary<string, object?>, string>, HttpStatusCode> Casos => new()
    {
        // 404: autenticado e autorizado; o tenant da rota não existe.
        Caso("sucesso", HttpStatusCode.NotFound, (emissor, payload) => emissor.Assinar(payload)),
        Caso("sem o papel (403)", HttpStatusCode.Forbidden, (emissor, payload) =>
        {
            payload.Remove("roles");
            return emissor.Assinar(payload);
        }),
        Caso("vencido", HttpStatusCode.Unauthorized, (emissor, payload) =>
        {
            payload["exp"] = Agora - 120;
            return emissor.Assinar(payload);
        }),
        Caso("audiência errada", HttpStatusCode.Unauthorized, (emissor, payload) =>
        {
            payload["aud"] = "account";
            return emissor.Assinar(payload);
        }),
        Caso("assinatura inválida", HttpStatusCode.Unauthorized, (emissor, payload) =>
            emissor.Assinar(payload, ChaveForasteira)),
        Caso("azp fora da lista", HttpStatusCode.Unauthorized, (emissor, payload) =>
        {
            payload["azp"] = "outro-client";
            return emissor.Assinar(payload);
        }),
    };

    [Theory]
    [MemberData(nameof(Casos))]
    public async Task TokenComEmail_NaoDeixaOEmailEmRespostaLogNemTrace(
        Func<EmissorDeTeste, Dictionary<string, object?>, string> assinar, HttpStatusCode esperado)
    {
        ArgumentNullException.ThrowIfNull(assinar);
        CancellationToken ct = TestContext.Current.CancellationToken;
        string email = $"segredo{Guid.NewGuid():N}@acme.test";
        string rota = $"/api/v1/tenants/{Guid.NewGuid()}/provisioning";

        Dictionary<string, object?> payload = _factory.Emissor.Payload(roles: PlatformAdmin);
        payload["email"] = email;
        payload["preferred_username"] = email;
        payload["name"] = email;
        string token = assinar(_factory.Emissor, payload);

        using HttpClient client = _comCaptura.CreateClient();
        using HttpRequestMessage pedido = new(HttpMethod.Get, rota);
        pedido.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

        using HttpResponseMessage resposta = await client.SendAsync(pedido, ct);
        string corpo = await resposta.Content.ReadAsStringAsync(ct);
        string cabecalhos = resposta.Headers.ToString();

        resposta.StatusCode.Should().Be(esperado);

        // O span da requisição é exportado quando ela termina de verdade, um instante depois de a resposta chegar.
        for (int tentativa = 0; tentativa < 40 && !DoPedido(_spans.Textos, rota); tentativa++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        }

        IReadOnlyList<string> logs = ColetorDeLogsDaApi.DoCanal(_canal).Textos;
        IReadOnlyList<string> spans = _spans.Textos;

        // Presença primeiro: o log e o trace DESTE pedido foram capturados.
        DoPedido(logs, rota).Should().BeTrue("o log do pedido precisa ter sido capturado");
        DoPedido(spans, rota).Should().BeTrue("o span do pedido precisa ter sido capturado");

        string[] segredos = [.. FormasDeUmSegredo.De(email), token, token.Split('.')[1]];

        foreach (string segredo in segredos)
        {
            corpo.Should().NotContain(segredo);
            cabecalhos.Should().NotContain(segredo);
            logs.Should().NotContain(texto => texto.Contains(segredo, StringComparison.Ordinal));
            spans.Should().NotContain(texto => texto.Contains(segredo, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("a@b.co")]
    [InlineData("segredo0123456789@acme.test")]
    [InlineData("segredo+0123456789ab@acme.test")]
    public void FormasEmBase64_SaoAchadasEmQualquerPosicaoDoPayload(string email)
    {
        // O detector do teste acima, provado: com o e-mail em qualquer deslocamento dentro de um JSON, ao menos uma das
        // três formas aparece no base64url. Sem isto, "não achei o e-mail em base64" poderia ser defeito do detector.
        IReadOnlyList<string> formas = FormasDeUmSegredo.De(email);

        formas.Should().HaveCount(4).And.Contain(email);

        for (int prefixo = 0; prefixo < 7; prefixo++)
        {
            for (int sufixo = 0; sufixo < 4; sufixo++)
            {
                string json = "{\"x\":\"" + new string('p', prefixo) + "\",\"email\":\"" + email + "\""
                              + new string(' ', sufixo) + ",\"y\":1}";
                string codificado = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));

                formas.Skip(1).Any(forma => codificado.Contains(forma, StringComparison.Ordinal))
                    .Should().BeTrue($"prefixo de {prefixo}, sufixo de {sufixo}");
            }
        }
    }

    private static bool DoPedido(IReadOnlyList<string> textos, string rota) =>
        textos.Any(texto => texto.Contains(rota, StringComparison.Ordinal));
}
```

- [ ] **Passo 7: Rodar tudo**

Run (Docker ligado): `dotnet test tests/IdentityGateway.Api.FunctionalTests --filter-class "*VazamentoDoEmailNoTokenTests"`
Expected: PASS — `total: 9` (seis casos da primeira theory e três da segunda).

Se um caso de `TokenComEmail_…` falhar na **presença** ("o span do pedido precisa ter sido capturado"), o defeito é da captura, não da Api: confira se o `ConfigureOpenTelemetryTracerProvider` alcançou o provedor (o `AddTelemetria` da Api precisa ter sido chamado antes — é, pelo `Program.cs`). Se falhar na **ausência**, a mensagem do AwesomeAssertions mostra o texto do log ou do span que carrega o segredo: é um vazamento de verdade, e se corrige na Api, não no teste.

Run (Docker ligado): `dotnet test`
Expected: `falhou: 0`, `ignorados: 0` nos cinco projetos. Anotar o total por projeto.

- [ ] **Passo 8: 🧪 Provas por mutação**

Reverter cada uma com `git checkout <arquivo>` (o código de produção mutado já está commitado desde as Tarefas 2 a 8).

| # | Mutação | Rodar | Vermelho esperado |
|---|---|---|---|
| 1 | Em `ValidacaoDoAccessToken.Configurar`, trocar `IssuerValidator = EmissorEstrito(validacao.Issuer),` por `ValidIssuer = validacao.Issuer,` | `--filter-class "*TokensDoKeycloakNaApiTests"` | `ApiComOEnderecoPublicoErrado_RecusaTokenRealDoKeycloak` (o token entra: `202`) |
| 2 | Em `ValidacaoDoAccessToken.Configurar`, `ValidateAudience = false` | idem | `TokenDoClientDeDeviceFlowDoFixture_AzpAceitoSemAAudiencia_401` |
| 3 | No realm, acrescentar `"defaultDefaultClientScopes": ["gateway-api"]` na raiz (a audiência vira default do realm, e o client de device flow do fixture a herda) | idem | `TokenDoClientDeDeviceFlowDoFixture_AzpAceitoSemAAudiencia_401`, na asserção sobre a forma (o token passa a trazer `identity-gateway-api`) — ou o fixture não sobe, se o import recusar a lista. Anote o que aconteceu: os dois são vermelho. A regra estática `ScopesDaGatewayExistemEForaDosDefaultsDoRealm` também cai |
| 4 | No realm, acrescentar `"gateway-api"` aos `defaultClientScopes` do client `identity-gateway` | idem | `TokenDoServiceAccountDaGateway_401` (a asserção sobre a forma) |
| 5 | No realm, acrescentar um segundo mapper ao scope `gateway-tenant`: `{ "name": "extra", "protocol": "openid-connect", "protocolMapper": "oidc-hardcoded-claim-mapper", "config": { "claim.name": "extra", "claim.value": "x", "jsonType.label": "String", "access.token.claim": "true" } }` | idem | `PonteDeContrato_OsClaimsDoTokenRealTemOsTiposDosDoEmissorDeTeste` (um claim a mais no token real) — e a regra estática `GatewayTenantEmiteUmValorSoSemAgregar`, que exige um mapper só |
| 6 | Em `ValidacaoDoAccessToken.AoFalhar`, registrar o token: trocar `string tipo = contexto.Exception.GetType().Name;` por `string tipo = contexto.Exception.GetType().Name + " " + contexto.Request.Headers.Authorization;` | `--filter-class "*VazamentoDoEmailNoTokenTests"` | Os casos `vencido`, `audiência errada` e `assinatura inválida` (o token no log) |
| 7 | Em `src/IdentityGateway.Api/Middlewares/RequestLoggingMiddleware.cs`, acrescentar o cabeçalho `Authorization` da requisição ao que é registrado (passe `context.Request.Headers.Authorization.ToString()` no lugar do método HTTP na chamada de `ApiLogs.RequisicaoConcluida`) | idem | Os seis casos |

Duas mutações da §5.3 da spec **não têm testemunha nesta coleção, por desenho**, e não são executadas aqui: tirar a checagem do `azp` (os tokens reais recusados aqui caem antes, na audiência — o do `admin-cli` nem tem `aud`) e `IncludeErrorDetails = true` (o detalhe ecoa `iss` e `aud`, não o e-mail). As duas são pegas pela suíte negativa por HTTP, nas Tarefas 7 e 8.

Depois da última reversão: `git status --short` vazio para `src/` e `keycloak/`.

- [ ] **Passo 9: Commit**

```bash
git add tests/IdentityGateway.Api.FunctionalTests
git commit -m "test: a Api contra o Keycloak real e o e-mail do token fora de log e trace

Colecao nova nos testes funcionais, com a Api apontada para um Keycloak
26.7.4 de verdade: um platform-admin obtem o token pelo device flow e
registra um tenant; o token do client de device flow do fixture (azp
aceito, sem a audiencia), o do service account e o obtido por senha no
admin-cli do realm levam 401, cada um com a forma do token afirmada; com
o endereco publico errado, o token real e recusado pelo emissor. A ponte
de contrato confere que o emissor de teste imita os claims do token real,
com os mesmos tipos.

O teste de vazamento passa a cobrir tokens que carregam e-mail, no
sucesso, no 403 e em cada 401: nada no corpo, no cabecalho, no log em
Debug nem nos spans, em texto ou em base64url.

Mutacoes: ValidIssuer no lugar do validador, sem audiencia, gateway-api
como default do realm, gateway-api no service account, mapper extra no
gateway-tenant, token no log da falha e Authorization no log do pedido."
```

Run: `git log -1 --format=%B | grep -Eci "co-authored|generated with"`
Expected: `0`.

---

### Tarefa 10: O convite do primeiro platform-admin no compose

Spec: §4.5 inteira, §4.7 ("Compose"), §3.2 (os fatos do `kcadm`), D-h, D-m, DT8.

O platform-admin nasce no JSON do realm, sem senha (Tarefa 2). Aqui entra o one-shot que manda o convite uma vez só, e a `api` passa a depender dele. O serviço e o script abaixo foram executados, como estão, num projeto isolado do compose ao escrever este plano: primeira subida envia; nova execução e segunda subida dizem "convite já enviado"; `REENVIAR=1` envia de novo, inclusive depois de `down`/`up`; o `keycloak` recusa subir com `PLATFORM_ADMIN_EMAIL` vazio ou com maiúsculas.

**Arquivos:**
- Modify: `docker-compose.yml`
- Test: `tests/IdentityGateway.ArchitectureTests/RegrasDoAmbienteLocalTests.cs`

**Interfaces:**
- Consome: o realm da Tarefa 2 (usuário `${PLATFORM_ADMIN_EMAIL}`, scope `gateway-api`); a subpasta `keycloak` do volume `gateway-keys`, com `admin-password` (existente).
- Produz, no `docker-compose.yml`:
  - a âncora `x-platform-admin` com `PLATFORM_ADMIN_EMAIL: ${PLATFORM_ADMIN_EMAIL:-platform-admin@identity-gateway.local}`;
  - o serviço `platform-admin-invite` (saída `0`/`1`; reenvio com `docker compose run --rm -e REENVIAR=1 platform-admin-invite`); as mensagens que os passos seguintes procuram: `convite enviado (o link vale 4 h)`, `convite já enviado em <data>; nada a fazer`, `convite já concluído; nada a enviar`;
  - a `api` em `127.0.0.1:8080:8080`, dependendo do `platform-admin-invite`; o Jaeger em `127.0.0.1:16686:16686` e `127.0.0.1:4317:4317`; sem `Jwt__SigningKey`.

- [ ] **Passo 1: As regras do compose**

Em `tests/IdentityGateway.ArchitectureTests/RegrasDoAmbienteLocalTests.cs`:

1. Trocar o corpo de `DependenciasComDadoPessoalPublicamSoEmLocalhost` por:

```csharp
        // Há dado pessoal no banco (o e-mail do admin até a ativação) e, em falha, nos logs; os e-mails do mailpit
        // trazem links que trocam senha; e, desde os tokens do Keycloak, circulam access tokens de verdade pela api e
        // URLs de chamadas pelos traces do Jaeger. Nada disso fica exposto à rede local.
        string compose = Compose();

        compose.Should().Contain("\"127.0.0.1:5432:5432\"")
            .And.Contain("\"127.0.0.1:6379:6379\"")
            .And.Contain("\"127.0.0.1:5341:80\"")
            .And.Contain("\"127.0.0.1:8025:8025\"")
            .And.Contain("\"127.0.0.1:8080:8080\"")
            .And.Contain("\"127.0.0.1:8081:8080\"")
            .And.Contain("\"127.0.0.1:16686:16686\"")
            .And.Contain("\"127.0.0.1:4317:4317\"");

        // Toda porta publicada começa por 127.0.0.1: pega a forma entre aspas e a sem aspas.
        PortasPublicadas().Matches(compose).Select(achado => achado.Groups["porta"].Value)
            .Should().OnlyContain(porta => porta.StartsWith("127.0.0.1:", StringComparison.Ordinal));
        compose.Should().NotContain(":1025\"", "o SMTP do mailpit só existe na rede do compose");
```

e acrescentar a regex, junto das outras:

```csharp
    // Um item de lista que é só uma porta: `- "8080:8080"`, `- 127.0.0.1:8080:8080`, com ou sem comentário no fim.
    [GeneratedRegex(@"^\s*-\s*""?(?<porta>[0-9.]*:?[0-9]+:[0-9]+)""?\s*(#.*)?$", RegexOptions.Multiline)]
    private static partial Regex PortasPublicadas();
```

2. Acrescentar os testes:

```csharp
    [Fact]
    public void ComposeNaoTemMaisAChaveJwt()
    {
        // A chave simétrica de desenvolvimento era publicada aqui, no README e na CI. Com os tokens do Keycloak, ela
        // deixa de existir — e uma variável Jwt__* de volta seria o primeiro sinal de que o HS256 voltou.
        Compose().Should().NotContain("Jwt__");
    }

    [Fact]
    public void ApiSoSobeDepoisDoConviteDoPlatformAdmin()
    {
        // O platform-admin-invite também confere que o realm é o desta versão. Sem a dependência, um volume antigo
        // deixaria a api subir e responder 401 a todo token, sem dizer por quê.
        DependenciaDaApiNoConvite().IsMatch(Compose()).Should().BeTrue(
            "a api depende de platform-admin-invite com service_completed_successfully");
    }

    [Fact]
    public void EntrypointDoKeycloakRecusaEmailVazioOuComMaiusculas()
    {
        // Foco de revisão 3. Vazio, o import gravaria o placeholder literal como e-mail. Com maiúsculas, o Keycloak
        // gravaria o e-mail em minúsculas — outro texto, que o platform-admin-invite procuraria em vão.
        string compose = Compose();

        compose.Should().Contain("test -n \"$$PLATFORM_ADMIN_EMAIL\"");
        compose.Should().Contain(
            "test \"$$PLATFORM_ADMIN_EMAIL\" = \"$$(printf '%s' \"$$PLATFORM_ADMIN_EMAIL\" | tr '[:upper:]' '[:lower:]')\"");
    }

    [Fact]
    public void PadraoDoEmailDoPlatformAdminEMinusculo()
    {
        Match padrao = PadraoDoPlatformAdmin().Match(Compose());

        padrao.Success.Should().BeTrue("o compose define PLATFORM_ADMIN_EMAIL com um padrão");
        string email = padrao.Groups["email"].Value;
        email.Should().Be(email.ToLowerInvariant()).And.Contain("@");
    }

    [Fact]
    public void ConviteDoPlatformAdminNaoRecebeSenhaPorVariavelNemAtribuiPapel()
    {
        // A senha do master é lida do arquivo dentro do sh: declarada como variável do serviço, ficaria no docker
        // inspect. E o one-shot só envia e-mail — quem "garantia o papel" em runtime promoveu um tenant-admin a
        // platform-admin (visto ao vivo no design): atribuir papel não é trabalho dele.
        string compose = Compose();

        compose.Should().NotContain("KC_CLI_PASSWORD:").And.NotContain("KC_BOOTSTRAP_ADMIN_PASSWORD:");
        compose.Should().NotContain("add-roles").And.NotContain("role-mappings/realm\" -");
        compose.Should().Contain("lifespan=14400", "o link do platform-admin vale 4 h");
        compose.Should().NotContain("--fields attributes", "com --fields, attributes vem vazio e as travas ficam vacuosas");
    }
```

```csharp
    [GeneratedRegex(
        @"^  api:\s*$.*?^    depends_on:\s*$.*?^      platform-admin-invite:\s*\n\s+condition:\s*service_completed_successfully",
        RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex DependenciaDaApiNoConvite();

    [GeneratedRegex(@"PLATFORM_ADMIN_EMAIL:\s*\$\{PLATFORM_ADMIN_EMAIL:-(?<email>[^}]+)\}")]
    private static partial Regex PadraoDoPlatformAdmin();
```

- [ ] **Passo 2: Rodar e ver falhar**

Run: `dotnet test tests/IdentityGateway.ArchitectureTests --filter-class "*RegrasDoAmbienteLocalTests"`
Expected: FAIL em `DependenciasComDadoPessoalPublicamSoEmLocalhost` (a api e o Jaeger publicam em todas as interfaces), `ComposeNaoTemMaisAChaveJwt`, `ApiSoSobeDepoisDoConviteDoPlatformAdmin`, `EntrypointDoKeycloakRecusaEmailVazioOuComMaiusculas`, `PadraoDoEmailDoPlatformAdminEMinusculo` e `ConviteDoPlatformAdminNaoRecebeSenhaPorVariavelNemAtribuiPapel` (falta o `lifespan=14400`).

- [ ] **Passo 3: O compose**

Em `docker-compose.yml`, nove edições.

1. No cabeçalho, trocar as duas linhas

```yaml
# NÃO É PRODUÇÃO. As senhas estão em texto aberto porque são de um banco descartável em máquina local; a chave
# JWT é fixa pelo mesmo motivo. Em produção, tudo isto vem do cofre do provedor.
```

por

```yaml
# NÃO É PRODUÇÃO. As senhas estão em texto aberto porque são de um banco descartável em máquina local. Em produção,
# tudo isto vem do cofre do provedor.
```

2. No cabeçalho, trocar o parágrafo de quatro linhas que começa em `# QUEM JÁ TINHA VOLUMES DE ANTES DO CONVITE DO ADMIN INICIAL` (até `# vez de deixar o convite falhar por 24 h.`) por:

```yaml
# QUEM JÁ TINHA VOLUMES DE ANTES DOS TOKENS DO KEYCLOAK PRECISA DE `docker compose down -v`, e depois
# `docker compose up -d --build`: o realm só é importado na primeira subida, e num volume antigo faltam os scopes da
# Gateway, o client de demonstração, o catálogo de papéis e o platform-admin. O serviço platform-admin-invite acusa
# isso e sai com 1, e a api não sobe — em vez de subir e responder 401 a todo token. (Volume de antes do convite do
# admin inicial: o /health/ready da Api acusa a falta do manage-users.)
#
# O PRIMEIRO PLATFORM-ADMIN nasce sem senha, no import do realm, com o e-mail de PLATFORM_ADMIN_EMAIL. O serviço
# platform-admin-invite manda o convite UMA vez, para o mailpit (http://localhost:8025); o link vale 4 h. Para mandar
# outro: docker compose run --rm -e REENVIAR=1 platform-admin-invite. Não troque nem apague o admin do master do
# Keycloak: o platform-admin-invite faz login com ele a cada subida. Se mudou, docker compose down -v.
```

3. Depois da âncora `x-database` (a linha `Database__ConnectionString: …`), antes de `services:`, acrescentar:

```yaml

# O e-mail do primeiro platform-admin, compartilhado pelo keycloak (que o grava no import do realm) e pelo
# platform-admin-invite (que o procura). Em minúsculas: o Keycloak grava o e-mail assim, e o entrypoint do keycloak
# recusa outra coisa. Fixado no primeiro import: mudar depois exige docker compose down -v.
x-platform-admin: &platform-admin
  PLATFORM_ADMIN_EMAIL: ${PLATFORM_ADMIN_EMAIL:-platform-admin@identity-gateway.local}
```

4. Na `api`, a porta passa a ser:

```yaml
    ports:
      # Só em 127.0.0.1: agora circulam tokens de verdade por aqui.
      - "127.0.0.1:8080:8080"
```

5. Na `api`, apagar o bloco da chave JWT (o comentário de três linhas que começa em `# Chave de desenvolvimento.`, a linha `Jwt__SigningKey: "chave-de-desenvolvimento-nao-use-em-producao"` e a linha em branco seguinte).

6. Na `api`, trocar o bloco de comentários e variáveis do Keycloak (de `# Transporte: a api chama…` até `Keycloak__Admin__PublicBaseUrl: "http://localhost:8081"`) por:

```yaml
      # Transporte: a api chama o token endpoint e a Admin API, e busca os metadados e as chaves que validam os
      # tokens, pelo nome do serviço, dentro da rede do compose.
      Keycloak__Admin__BaseUrl: "http://keycloak:8080"
      # Endereço público, nunca discado: é o aud do client assertion E o emissor que a api aceita nos tokens. Igual
      # ao KC_HOSTNAME do keycloak (um teste de arquitetura confere); sem ele o emissor seria keycloak:8080, o
      # Keycloak responderia "Invalid token audience" e todo token de usuário levaria 401. A lista de clients aceitos
      # (azp) vem do appsettings.Development.json: só o identity-gateway-demo.
      Keycloak__Admin__PublicBaseUrl: "http://localhost:8081"
```

7. Na `api`, em `depends_on`, depois de `gateway-keys:` e da `condition` dele:

```yaml
      # O convite do primeiro platform-admin. Além de mandar o e-mail na primeira subida, ele confere que o realm é o
      # desta versão: com um volume antigo, sai com 1 e a api não sobe — o erro aparece no `up`, com a causa no log
      # dele, em vez de a api subir e todo token levar 401.
      platform-admin-invite:
        condition: service_completed_successfully
```

8. No `keycloak`: no `entrypoint`, depois da linha dos três `test -n` do SMTP, acrescentar as duas linhas

```yaml
        test -n "$$PLATFORM_ADMIN_EMAIL" &&
        test "$$PLATFORM_ADMIN_EMAIL" = "$$(printf '%s' "$$PLATFORM_ADMIN_EMAIL" | tr '[:upper:]' '[:lower:]')" &&
```

e, em `environment`, antes de `KC_DB: postgres`:

```yaml
      # O ${PLATFORM_ADMIN_EMAIL} do realm. Conferido no entrypoint: vazio, o import gravaria o placeholder literal;
      # com maiúsculas, o Keycloak gravaria em minúsculas um e-mail diferente do configurado.
      <<: *platform-admin
```

9. No `jaeger`, as portas passam a ser:

```yaml
    ports:
      # Só em 127.0.0.1: os traces carregam as URLs das chamadas que a API faz e recebe.
      - "127.0.0.1:16686:16686"  # interface
      - "127.0.0.1:4317:4317"    # OTLP gRPC, que é o que a API usa
```

E o serviço novo, entre o `keycloak` e o `mailpit` (antes do comentário `# E-mail de desenvolvimento, em http://localhost:8025`). Copie como está: `$$` é o `$` literal para o compose, e o script é o que rodou.

```yaml
  # One-shot: manda, UMA vez, o e-mail de convite do primeiro platform-admin — a conta nasce sem senha no import do
  # realm, e é pelo link deste e-mail (4 h) que ela define a senha. Sobe depois do keycloak saudável, e a api depende
  # dele.
  #
  # Reenvio, só por comando explícito: docker compose run --rm -e REENVIAR=1 platform-admin-invite
  # (recusa se o convite já foi concluído; os links anteriores ainda não usados continuam válidos até expirar).
  #
  # A imagem é a do Keycloak, pelo kcadm.sh oficial — ela não tem jq, awk nem python: as respostas são lidas com sed e
  # grep, do JSON indentado (uma chave por linha). Toda leitura de attributes é SEM --fields: com ele, o objeto vem
  # vazio, e as travas do marcador e do tenant_id ficariam vacuosas.
  #
  # A credencial é a do admin do MASTER, lida do arquivo dentro do sh — nunca uma variável declarada aqui, que ficaria
  # no docker inspect. Nunca a chave da Gateway: com o manage-users dela, a criação do platform-admin apareceria nos
  # eventos de administração como obra da Gateway. Sem -x, e nenhuma resposta do kcadm é ecoada.
  #
  # O que ele NUNCA faz: atribuir papel. A conta que não tiver exatamente a forma do bootstrap (só platform-admin, sem
  # papel de client, sem tenant_id, sem grupo) faz o serviço sair com 1.
  #
  # O marcador do envio é um atributo do realm (platformAdminInviteSentAt), gravado ANTES do envio: no máximo um link
  # automático, mesmo que o envio falhe — nesse caso, o convite só sai pelo reenvio.
  platform-admin-invite:
    image: quay.io/keycloak/keycloak:26.7.4
    entrypoint: ["/bin/sh", "-euc"]
    command:
      - |
        kcadm=/opt/keycloak/bin/kcadm.sh
        config=/tmp/kcadm.config
        realm=identity-gateway
        email="$$(printf '%s' "$$PLATFORM_ADMIN_EMAIL" | tr '[:upper:]' '[:lower:]')"
        falhar() { echo "platform-admin-invite: $$1" >&2; exit 1; }
        trap 'rm -f /tmp/kcadm.config' EXIT
        KC_CLI_PASSWORD="$$(cat /keys/admin-password)"; export KC_CLI_PASSWORD
        kc() { "$$kcadm" "$$@" --config "$$config" 2>/dev/null; }

        # 0. Login no master, antes de tudo.
        kc config credentials --server http://keycloak:8080 --realm master --user admin > /dev/null \
          || falhar "credencial do master recusada: o admin do compose foi alterado? Rode: docker compose down -v"

        # 1. O realm é o desta versão?
        kc get client-scopes -r "$$realm" --fields name | grep -q '"name" : "gateway-api"' \
          || falhar "realm anterior aos tokens do Keycloak (o import é IGNORE_EXISTING). Rode: docker compose down -v"

        # 2. O marcador (sem --fields: com ele, attributes viria vazio).
        marcador="$$(kc get "realms/$$realm" | sed -n 's/^ *"platformAdminInviteSentAt" : "\([^"]*\)".*$$/\1/p' | head -n 1)"
        if [ -n "$$marcador" ] && [ "$${REENVIAR:-0}" != "1" ]; then
          echo "platform-admin-invite: convite já enviado em $$marcador; nada a fazer"
          exit 0
        fi

        # 3. Exatamente um usuário com o e-mail, e com a forma do bootstrap.
        ids="$$(kc get users -r "$$realm" -q "email=$$email" -q exact=true --fields id | sed -n 's/^ *"id" : "\([^"]*\)".*$$/\1/p')"
        [ "$$(printf '%s\n' "$$ids" | grep -c .)" = "1" ] || falhar "esperado exatamente um usuário com o e-mail do platform-admin"
        id="$$ids"
        usuario="$$(kc get "users/$$id" -r "$$realm")"
        papeis="$$(kc get "users/$$id/role-mappings" -r "$$realm")"
        grupos="$$(kc get "users/$$id/groups" -r "$$realm")"
        gravado="$$(printf '%s\n' "$$usuario" | sed -n 's/^ *"email" : "\([^"]*\)".*$$/\1/p' | head -n 1 | tr '[:upper:]' '[:lower:]')"
        nomes="$$(printf '%s\n' "$$papeis" | sed -n 's/^ *"name" : "\([^"]*\)".*$$/\1/p')"
        [ "$$gravado" = "$$email" ] || falhar "a conta do platform-admin não tem a forma do bootstrap"
        [ "$$nomes" = "platform-admin" ] || falhar "a conta do platform-admin não tem a forma do bootstrap"
        if printf '%s\n' "$$papeis" | grep -q '"clientMappings"'; then falhar "a conta do platform-admin não tem a forma do bootstrap"; fi
        if printf '%s\n' "$$usuario" | grep -q '"tenant_id"'; then falhar "a conta do platform-admin não tem a forma do bootstrap"; fi
        if printf '%s\n' "$$grupos" | grep -q '"id"'; then falhar "a conta do platform-admin não tem a forma do bootstrap"; fi

        # 4. Convite já concluído?
        if ! printf '%s\n' "$$usuario" | grep '"requiredActions"' | grep -q '"UPDATE_PASSWORD"'; then
          if [ "$${REENVIAR:-0}" = "1" ]; then falhar "o convite já foi concluído; não há o que reenviar"; fi
          echo "platform-admin-invite: convite já concluído; nada a enviar"
          exit 0
        fi

        # 5. Marcador ANTES do envio; depois, o e-mail (4 h).
        agora="$$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        kc update "realms/$$realm" -s "attributes.platformAdminInviteSentAt=$$agora" > /dev/null \
          || falhar "não foi possível gravar o marcador do convite no realm"
        if [ "$${REENVIAR:-0}" = "1" ]; then
          echo "platform-admin-invite: os links anteriores ainda não usados continuam válidos até expirar"
        fi
        kc update "users/$$id/execute-actions-email" -r "$$realm" -q lifespan=14400 -n -b '["UPDATE_PASSWORD","VERIFY_EMAIL"]' > /dev/null \
          || falhar "o envio do convite falhou. Reenvie com: docker compose run --rm -e REENVIAR=1 platform-admin-invite"
        echo "platform-admin-invite: convite enviado (o link vale 4 h)"
    environment:
      <<: *platform-admin
    volumes:
      - type: volume
        source: gateway-keys
        target: /keys
        read_only: true
        volume:
          subpath: keycloak
    depends_on:
      keycloak:
        condition: service_healthy

```

- [ ] **Passo 4: Rodar as regras e validar o YAML**

Run: `dotnet test tests/IdentityGateway.ArchitectureTests --filter-class "*RegrasDoAmbienteLocalTests"`
Expected: PASS — todos, inclusive `ComposeEFixtureUsamAMesmaTagDoKeycloak` (agora há duas linhas `image:` do Keycloak, com a mesma tag) e `ComposeTemKcHostnameIgualAoPublicBaseUrl`.

Run: `docker compose config --quiet && echo valido`
Expected: `valido`.

- [ ] **Passo 5: Verificação ao vivo, num projeto isolado**

Nunca no projeto padrão: os volumes `identitygateway_*` do autor não são tocados. Antes de começar, nada pode estar escutando nas portas do compose:

Run: `docker compose ps -a --format '{{.Service}}'; docker ps --format '{{.Names}}\t{{.Ports}}'`
Expected: nada do projeto `identitygateway` de pé e nenhuma das portas `8080`, `8081`, `8025`, `5432`, `6379` em uso. Se houver, pare e avise o autor em vez de derrubar o ambiente dele.

Os comandos abaixo são para o Git Bash (o mesmo shell do README); `MSYS_NO_PATHCONV=1` é inofensivo no Linux. Rode-os a partir da raiz do repositório, numa sessão só, para as funções e variáveis valerem até o fim.

```bash
export MSYS_NO_PATHCONV=1
contar() { curl -fsS "http://127.0.0.1:8025/api/v1/search?query=to%3A%22platform-admin%40identity-gateway.local%22" | jq '.messages | length' | tr -d '\r'; }
convite() { docker compose -p igverif run --rm -T --no-deps "$@" platform-admin-invite 2>/dev/null; echo "exit=$?"; }

docker compose -p igverif up -d --build --wait --wait-timeout 300 api
curl -fsS --max-time 10 http://127.0.0.1:8080/health/ready; echo
docker compose -p igverif ps -a --format '{{.Service}}\t{{.State}}\t{{.ExitCode}}'
docker compose -p igverif logs --no-log-prefix platform-admin-invite
echo "e-mails: $(contar)"
```

Expected: `Healthy`; `platform-admin-invite` em `exited` com código `0`; no log dele, **só** `platform-admin-invite: convite enviado (o link vale 4 h)` — nenhuma resposta do `kcadm`, nenhum link; `e-mails: 1`.

**O convite sai uma vez só** (com o convite ainda pendente e o mailpit vivo — é o único estado em que a prova não é vacuosa):

```bash
convite
echo "e-mails: $(contar)"
```

Expected: `platform-admin-invite: convite já enviado em <data>; nada a fazer`, `exit=0`, `e-mails: 1`.

**As travas da conta.** O one-shot nunca envia para uma conta que não tenha exatamente a forma do bootstrap. Cada trava é exercitada pelo master e desfeita em seguida (os arquivos temporários ficam no diretório corrente, que é de onde o `curl` do Windows consegue lê-los):

```bash
SENHA=$(docker run --rm -v igverif_gateway-keys:/k alpine cat /k/keycloak/admin-password)
TOKEN=$(curl -fsS -d grant_type=password -d client_id=admin-cli -d username=admin --data-urlencode "password=$SENHA" http://127.0.0.1:8081/realms/master/protocol/openid-connect/token | jq -r .access_token | tr -d '\r')
ADMIN=http://127.0.0.1:8081/admin/realms/identity-gateway
H="Authorization: Bearer $TOKEN"; J="Content-Type: application/json"
ID=$(curl -fsS -H "$H" "$ADMIN/users?email=platform-admin@identity-gateway.local&exact=true" | jq -r '.[0].id' | tr -d '\r')

# 1. um papel de realm a mais
curl -fsS -H "$H" "$ADMIN/roles/tenant-admin" | jq -c '[{id, name}]' > .papel.json
curl -fsS -X POST -H "$H" -H "$J" --data-binary @.papel.json "$ADMIN/users/$ID/role-mappings/realm"
convite -e REENVIAR=1
curl -fsS -X DELETE -H "$H" -H "$J" --data-binary @.papel.json "$ADMIN/users/$ID/role-mappings/realm"

# 2. o atributo tenant_id
curl -fsS -H "$H" "$ADMIN/users/$ID" | jq -c '.attributes = {"tenant_id": ["0199a000-0000-7000-8000-000000000001"]}' > .usuario.json
curl -fsS -X PUT -H "$H" -H "$J" --data-binary @.usuario.json "$ADMIN/users/$ID"
convite -e REENVIAR=1
curl -fsS -H "$H" "$ADMIN/users/$ID" | jq -c '.attributes = {}' > .usuario.json
curl -fsS -X PUT -H "$H" -H "$J" --data-binary @.usuario.json "$ADMIN/users/$ID"

# 3. um grupo
GRUPO=$(curl -fsS -i -X POST -H "$H" -H "$J" -d '{"name":"g-verif"}' "$ADMIN/groups" | grep -i '^location:' | sed 's|.*/||' | tr -d '\r')
curl -fsS -X PUT -H "$H" "$ADMIN/users/$ID/groups/$GRUPO"
convite -e REENVIAR=1
curl -fsS -X DELETE -H "$H" "$ADMIN/groups/$GRUPO"

# 4. um papel de client
CONTA=$(curl -fsS -H "$H" "$ADMIN/clients?clientId=account" | jq -r '.[0].id' | tr -d '\r')
curl -fsS -H "$H" "$ADMIN/clients/$CONTA/roles/view-profile" | jq -c '[{id, name}]' > .papel.json
curl -fsS -X POST -H "$H" -H "$J" --data-binary @.papel.json "$ADMIN/users/$ID/role-mappings/clients/$CONTA"
convite -e REENVIAR=1
curl -fsS -X DELETE -H "$H" -H "$J" --data-binary @.papel.json "$ADMIN/users/$ID/role-mappings/clients/$CONTA"

rm -f .papel.json .usuario.json
echo "e-mails: $(contar)"
```

Expected: em cada um dos quatro `convite -e REENVIAR=1`, `platform-admin-invite: a conta do platform-admin não tem a forma do bootstrap` e `exit=1`. No fim, `e-mails: 1`: nenhuma das quatro tentativas enviou. (Cuidado ao limpar o `tenant_id`: um `PUT` sem a chave `attributes` **mantém** os atributos; é preciso mandar `"attributes": {}`.)

**O reenvio, com a conta de volta à forma do bootstrap:**

```bash
convite -e REENVIAR=1
echo "e-mails: $(contar)"
```

Expected: `os links anteriores ainda não usados continuam válidos até expirar`, `convite enviado (o link vale 4 h)`, `exit=0`, `e-mails: 2`.

**Segunda subida, e o reenvio depois dela** (Foco de revisão 5: o mailpit não tem volume, e quem perdeu o e-mail só recupera o convite pelo reenvio):

```bash
docker compose -p igverif down
docker compose -p igverif up -d --wait --wait-timeout 300 api
docker compose -p igverif logs --no-log-prefix platform-admin-invite
echo "e-mails: $(contar)"
convite -e REENVIAR=1
echo "e-mails: $(contar)"
```

Expected: no log, `convite já enviado em <data>; nada a fazer`; `e-mails: 0` (o mailpit voltou vazio, e nada foi reenviado); depois do reenvio, `exit=0` e `e-mails: 1`.

**O `keycloak` recusa o e-mail vazio e o com maiúsculas** (Foco de revisão 3):

```bash
docker compose -p igverif run --rm -T --no-deps -e PLATFORM_ADMIN_EMAIL= keycloak > /dev/null 2>&1; echo "vazio: exit=$?"
docker compose -p igverif run --rm -T --no-deps -e PLATFORM_ADMIN_EMAIL=Admin@Acme.Test keycloak > /dev/null 2>&1; echo "maiusculas: exit=$?"
```

Expected: `vazio: exit=1` e `maiusculas: exit=1`, cada um em menos de um segundo (o `test` do entrypoint falha antes de o Keycloak começar a subir).

Registrar para o handoff: os horários, as mensagens e as contagens observadas em cada bloco.

- [ ] **Passo 6: 🧪 Provas por mutação ao vivo — o marcador**

Com o `igverif` ainda de pé e **um** e-mail no mailpit (o do reenvio acima). Antes: `git add docker-compose.yml`, para reverter com `git restore docker-compose.yml`.

| # | Mutação no script do `platform-admin-invite` | Rodar | Vermelho esperado |
|---|---|---|---|
| 1 | Trocar `if [ -n "$$marcador" ] && [ "$${REENVIAR:-0}" != "1" ]; then` por `if false; then` | `convite; echo "e-mails: $(contar)"` | `convite enviado`, e a contagem **sobe** para 2: sem o marcador, toda subida reenvia |
| 2 | Na linha do marcador, trocar `kc get "realms/$$realm"` por `kc get "realms/$$realm" --fields attributes` | idem | a contagem sobe de novo: com `--fields`, `attributes` vem vazio, o marcador nunca é lido — e a regra `ConviteDoPlatformAdminNaoRecebeSenhaPorVariavelNemAtribuiPapel` também reprova o `--fields attributes` |
| 3 | Trocar a ordem dos dois últimos comandos do script: o `kc update "users/$$id/execute-actions-email" …` (com o `\|\| falhar` dele) **antes** do `kc update "realms/$$realm" -s "attributes.platformAdminInviteSentAt=$$agora"` | o roteiro abaixo | a data do marcador **não muda** depois de um envio que falhou: o marcador deixou de ser gravado antes do envio |

Roteiro da mutação 3 (o envio é feito falhar parando o SMTP; o que se observa é a data que o one-shot diz ter no marcador):

```bash
convite                                         # anote a data: "convite já enviado em <D1>"
docker compose -p igverif stop mailpit
convite -e REENVIAR=1                           # exit=1: "o envio do convite falhou. Reenvie com: …"
docker compose -p igverif start mailpit
convite                                         # "convite já enviado em <D2>"
```

Com o código certo, `D2` é posterior a `D1`: o marcador foi regravado antes do envio que falhou — é o que garante "no máximo um link automático" mesmo com o SMTP fora. Com a mutação, `D2` é igual a `D1`. Rode o roteiro nas duas versões e registre as quatro datas.

Depois de cada mutação: `git restore docker-compose.yml`. A mutação 2 também é pega sem Docker:

Run: `dotnet test tests/IdentityGateway.ArchitectureTests --filter-method "*ConviteDoPlatformAdminNaoRecebeSenhaPorVariavelNemAtribuiPapel"`
Expected (com a mutação 2 aplicada): FAIL — "com --fields, attributes vem vazio e as travas ficam vacuosas".

Encerrar **só o projeto isolado**:

Run: `docker compose -p igverif down -v`
Expected: remove os contêineres e os volumes `igverif_*`. Os volumes `identitygateway_*` não aparecem na saída.

- [ ] **Passo 7: 🧪 Provas por mutação das regras do compose**

Reverter cada uma com `git restore docker-compose.yml`.

Run (a cada mutação): `dotnet test tests/IdentityGateway.ArchitectureTests --filter-class "*RegrasDoAmbienteLocalTests"`

| # | Mutação | Vermelho esperado |
|---|---|---|
| 1 | A porta da `api` de volta a `"8080:8080"` | `DependenciasComDadoPessoalPublicamSoEmLocalhost` |
| 2 | A porta do Jaeger sem aspas e sem IP: `- 4317:4317` | `DependenciasComDadoPessoalPublicamSoEmLocalhost` (a regex pega a forma sem aspas) |
| 3 | Tirar o `platform-admin-invite` do `depends_on` da `api` | `ApiSoSobeDepoisDoConviteDoPlatformAdmin` |
| 4 | Tirar a linha `test -n "$$PLATFORM_ADMIN_EMAIL" &&` | `EntrypointDoKeycloakRecusaEmailVazioOuComMaiusculas` |
| 5 | Padrão `Platform-Admin@identity-gateway.local` na âncora | `PadraoDoEmailDoPlatformAdminEMinusculo` |
| 6 | Trocar a tag da imagem do `platform-admin-invite` para `26.7.3` | `ComposeEFixtureUsamAMesmaTagDoKeycloak` |
| 7 | Acrescentar `Jwt__SigningKey: "x"` ao ambiente da `api` | `ComposeNaoTemMaisAChaveJwt` |
| 8 | Trocar `lifespan=14400` por `lifespan=86400` | `ConviteDoPlatformAdminNaoRecebeSenhaPorVariavelNemAtribuiPapel` |

- [ ] **Passo 8: Commit**

```bash
git add docker-compose.yml tests/IdentityGateway.ArchitectureTests/RegrasDoAmbienteLocalTests.cs
git commit -m "feat: convite do primeiro platform-admin no compose, uma vez so

O platform-admin nasce sem senha no import do realm. O one-shot
platform-admin-invite (imagem do Keycloak, kcadm, sem jq) confere que o
realm e o desta versao, que a conta tem exatamente a forma do bootstrap
(so platform-admin, sem papel de client, sem tenant_id, sem grupo) e,
com o marcador platformAdminInviteSentAt gravado no realm antes do
envio, manda o e-mail de acoes com link de 4 h. Nunca atribui papel.
Reenvio so por REENVIAR=1. A api passa a depender dele: volume antigo
falha no up, com a causa no log.

O keycloak recusa PLATFORM_ADMIN_EMAIL vazio ou com maiusculas. Sai
Jwt__SigningKey. A api e o Jaeger passam a publicar so em 127.0.0.1.

Verificado num projeto isolado (igverif): um e-mail na primeira subida,
nenhum na segunda execucao nem na segunda subida, reenvio manual, e as
quatro travas da conta saindo com 1. Mutacoes: sem o marcador, marcador
lido com --fields, marcador gravado depois do envio, e oito nas regras
de arquitetura do compose."
```

Run: `git log -1 --format=%B | grep -Eci "co-authored|generated with"`
Expected: `0`.

---

### Tarefa 11: O app da jornada, o job `Compose` e a demonstração do README

Spec: §4.6 ("App `tools/jornada-compose.cs`"), §4.7 ("Job `Compose`" e "README"), §5.3 (mutação do marcador com testemunha na CI), DT7, DT12.

O job `Compose` de hoje monta um token HS256 à mão e confere o convite com `jq`. Depois da Tarefa 7 ele fica vermelho — a API não aceita mais aquele token. Aqui ele é reescrito em cima de um app de arquivo único que usa o mesmo harness dos testes, e o README ganha a demonstração por device flow. A mutação da guarda `https` que o handoff do design listava nesta tarefa já foi feita na Tarefa 8 (mutação 15), junto do teste que a pega.

O app abaixo foi compilado com `#:project` contra a biblioteca, com os `Directory.*.props` do repositório, e executado sem nada no ar ao escrever este plano (saída `10` com "a API do mailpit não respondeu", `40` sem o arquivo de estado, `2` com fase desconhecida). As fases contra a API **não** foram executadas: é o que o Passo 4 faz.

**Arquivos:**
- Create: `tools/jornada-compose.cs`
- Modify: `.github/workflows/ci.yml` (`defaults`, job `build`, job `compose`)
- Modify: `README.md` (seção "Keycloak" e a demonstração)
- Create: `tests/IdentityGateway.ArchitectureTests/RegrasDeFerramentasTests.cs`

**Interfaces:**
- Consome: `HarnessDeLogin` (inclusive `LinkDeAcoesAbreAsync` e `PassosDoUltimoLogin`), `ClienteDoMailpit`, `FalhaDoHarnessException`, `FamiliaDeFalha`, `SenhasDeTeste`, `TokensDeUsuario` (Tarefas 1 e 4); o serviço `platform-admin-invite` e as mensagens dele (Tarefa 10); `POST /api/v1/tenants`, `GET /api/v1/tenants/{id}/provisioning` e `/health/ready` (existentes).
- Produz: `tools/jornada-compose.cs`, com as fases `convites --esperado N`, `jornada`, `antes-de-parar`, `com-keycloak-parado`, `depois-de-voltar`; variáveis `IG_API`, `IG_KEYCLOAK_PUBLICO`, `IG_KEYCLOAK_TRANSPORTE`, `IG_MAILPIT`, `PLATFORM_ADMIN_EMAIL`, `IG_ESTADO`; códigos de saída `0`, `2` (uso) e a família da falha (`10` mailpit, `20` formulário, `30` device flow ou Keycloak, `40` API, `50` prazo).

- [ ] **Passo 1: As regras das ferramentas**

`tests/IdentityGateway.ArchitectureTests/RegrasDeFerramentasTests.cs`:

```csharp
using System.Text.RegularExpressions;

namespace IdentityGateway.ArchitectureTests;

/// <summary>
/// Os apps de arquivo único de <c>tools/</c> seguem as regras de pacote do repositório, e o que eles assumem do
/// ambiente é o que o compose e o README dizem.
/// </summary>
public sealed partial class RegrasDeFerramentasTests
{
    private static string[] Ferramentas()
    {
        string pasta = RaizDoRepositorio.Caminho("tools");

        return Directory.Exists(pasta) ? Directory.GetFiles(pasta, "*.cs") : [];
    }

    [Fact]
    public void FerramentasNaoDeclaramPacotes()
    {
        // Um #:package num app de arquivo único é uma dependência fora do Directory.Packages.props: versão solta, sem
        // a revisão de licença que todo pacote do repositório passa. O que a ferramenta precisa vem pelo #:project.
        Ferramentas().Should().NotBeEmpty("sem ferramentas, a regra passaria vazia");

        foreach (string ferramenta in Ferramentas())
        {
            File.ReadAllText(ferramenta).Should().NotContain("#:package", $"{Path.GetFileName(ferramenta)}");
        }
    }

    [Fact]
    public void AppDaJornadaUsaABibliotecaDoHarnessESemAot()
    {
        // #:project para a biblioteca (um projeto de teste daria dois Main); sem o AOT padrão dos apps de arquivo único,
        // que transformaria o JSON por reflexão em erro de build.
        string app = File.ReadAllText(RaizDoRepositorio.Caminho("tools", "jornada-compose.cs"));

        app.Should().Contain("#:project ../tests/IdentityGateway.Testing.Keycloak");
        app.Should().Contain("#:property PublishAot=false");
    }

    [Fact]
    public void PadraoDoEmailDoPlatformAdminEOMesmoNoComposeNoAppENoReadme()
    {
        // Três lugares escrevem o mesmo padrão. Diferentes, o app procuraria no mailpit um e-mail que o compose não
        // usou — e o README mandaria abrir um convite que não existe.
        string compose = File.ReadAllText(RaizDoRepositorio.Caminho("docker-compose.yml"));
        string app = File.ReadAllText(RaizDoRepositorio.Caminho("tools", "jornada-compose.cs"));
        string readme = File.ReadAllText(RaizDoRepositorio.Caminho("README.md"));

        Match noCompose = PadraoNoCompose().Match(compose);
        Match noApp = PadraoNoApp().Match(app);

        noCompose.Success.Should().BeTrue();
        noApp.Success.Should().BeTrue();

        string email = noCompose.Groups["email"].Value;
        noApp.Groups["email"].Value.Should().Be(email);
        readme.Should().Contain(email);
        email.Should().Be(email.ToLowerInvariant());
    }

    [GeneratedRegex(@"PLATFORM_ADMIN_EMAIL:\s*\$\{PLATFORM_ADMIN_EMAIL:-(?<email>[^}]+)\}")]
    private static partial Regex PadraoNoCompose();

    [GeneratedRegex(@"Ambiente\(""PLATFORM_ADMIN_EMAIL"",\s*""(?<email>[^""]+)""\)")]
    private static partial Regex PadraoNoApp();
}
```

- [ ] **Passo 2: Rodar e ver falhar**

Run: `dotnet test tests/IdentityGateway.ArchitectureTests --filter-class "*RegrasDeFerramentasTests"`
Expected: FAIL nos três — `FerramentasNaoDeclaramPacotes` ("sem ferramentas, a regra passaria vazia") e os outros dois com `DirectoryNotFoundException`/`FileNotFoundException`: `tools/jornada-compose.cs` ainda não existe.

- [ ] **Passo 3: O app**

`tools/jornada-compose.cs` (o diretório `tools/` nasce aqui; fora de `tests/`, onde herdaria o executável do xUnit):

```csharp
#:project ../tests/IdentityGateway.Testing.Keycloak
// Ferramenta de CI, nunca publicada: sem o AOT padrão dos apps de arquivo único, que transformaria o JSON por reflexão
// em erro (TreatWarningsAsErrors).
#:property PublishAot=false

// A jornada do compose, de ponta a ponta, como o job `Compose` da CI a roda — e como qualquer pessoa pode rodar com o
// compose de pé:  dotnet run tools/jornada-compose.cs -- <fase>
//
// Fases, uma por invocação (o estado entre elas vai num arquivo, nunca em memória):
//   convites --esperado N   conta os convites do platform-admin no mailpit: exatamente N
//   jornada                 link do platform-admin → device flow → HS256 antigo recusado → POST /tenants → Active →
//                           convite do admin do tenant no mailpit, com um link que abre
//   antes-de-parar          renova o token e faz um GET autenticado (a api guarda as chaves do Keycloak)
//   com-keycloak-parado     com o Keycloak parado: POST /tenants → 202, e o tenant fica Pending
//   depois-de-voltar        com o Keycloak de volta: renova o token e espera o tenant ficar Active
//
// NUNCA imprime access token, refresh token, código de dispositivo, link de ação, senha nem HTML. As senhas são geradas
// em memória. O arquivo de estado guarda credenciais de um Keycloak descartável (refresh e access token) e é apagado no
// passo de limpeza do job.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IdentityGateway.Testing.Keycloak;

const string Realm = "identity-gateway";
const string ClientDeDemonstracao = "identity-gateway-demo";

Uri api = new(Ambiente("IG_API", "http://127.0.0.1:8080"));
Uri keycloakPublico = new(Ambiente("IG_KEYCLOAK_PUBLICO", "http://localhost:8081"));

// Na CI, sempre 127.0.0.1 no que é discado: `localhost` pode tentar ::1 primeiro, onde ninguém escuta.
Uri keycloakTransporte = new(Ambiente("IG_KEYCLOAK_TRANSPORTE", "http://127.0.0.1:8081"));
Uri mailpitUrl = new(Ambiente("IG_MAILPIT", "http://127.0.0.1:8025"));
string platformAdmin = Ambiente("PLATFORM_ADMIN_EMAIL", "platform-admin@identity-gateway.local").ToLowerInvariant();
string arquivoDeEstado = Ambiente("IG_ESTADO", Path.Combine(Path.GetTempPath(), "ig-jornada-estado.json"));
bool noGitHub = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";

List<(string Etapa, double Segundos)> etapas = [];
string etapaAtual = "início";
var relogio = Stopwatch.StartNew();
using CancellationTokenSource prazo = new(TimeSpan.FromMinutes(6));
CancellationToken ct = prazo.Token;

using HttpClient http = new() { BaseAddress = api, Timeout = TimeSpan.FromSeconds(10) };
using ClienteDoMailpit mailpit = new(mailpitUrl);

try
{
    string fase = args.Length > 0 ? args[0] : string.Empty;

    switch (fase)
    {
        case "convites":
            await ConvitesAsync(LerEsperado(args));
            break;
        case "jornada":
            await JornadaAsync();
            break;
        case "antes-de-parar":
            await AntesDePararAsync();
            break;
        case "com-keycloak-parado":
            await ComKeycloakParadoAsync();
            break;
        case "depois-de-voltar":
            await DepoisDeVoltarAsync();
            break;
        default:
            Console.Error.WriteLine(
                "uso: jornada-compose <convites --esperado N | jornada | antes-de-parar | com-keycloak-parado | depois-de-voltar>");
            return 2;
    }

    Resumir(falha: null);
    return 0;
}
catch (FalhaDoHarnessException falha)
{
    return Falhar(falha.Message, (int)falha.Familia);
}
catch (OperationCanceledException)
{
    return Falhar($"{etapaAtual}: o prazo da fase esgotou.", (int)FamiliaDeFalha.Prazo);
}
catch (HttpRequestException falha)
{
    // O que sobra aqui é o Keycloak: o mailpit e a API já embrulham as próprias falhas de conexão. A mensagem de uma
    // falha HTTP pode carregar a URL inteira; a query string (onde viajam chaves e códigos) é cortada.
    return Falhar(
        $"{etapaAtual}: o Keycloak não respondeu: {SemQueryStrings().Replace(falha.Message, "?…")}",
        (int)FamiliaDeFalha.DeviceFlow);
}

// ───────────────────────────── as fases ─────────────────────────────

async Task ConvitesAsync(int esperado)
{
    Etapa($"convites do platform-admin no mailpit: exatamente {esperado}");
    int achados = 0;

    // Espera até chegar ao número; depois, um intervalo curto antes de afirmar "exatamente" — obrigatório para zero,
    // e é o que pega um segundo e-mail que ainda estivesse a caminho.
    for (int tentativa = 0; tentativa < 30; tentativa++)
    {
        achados = (await mailpit.MensagensParaAsync(platformAdmin, ct)).Count;

        if (achados >= esperado)
        {
            break;
        }

        await Task.Delay(TimeSpan.FromSeconds(1), ct);
    }

    await Task.Delay(TimeSpan.FromSeconds(3), ct);
    achados = (await mailpit.MensagensParaAsync(platformAdmin, ct)).Count;

    if (achados != esperado)
    {
        throw new FalhaDoHarnessException(
            FamiliaDeFalha.Mailpit, etapaAtual, $"esperados {esperado} convites, achados {achados}.");
    }

    if (esperado > 0)
    {
        // Lança se a mensagem não trouxer o link no endereço público. O link em si nunca é impresso.
        await mailpit.LinkDeAcoesAsync(platformAdmin, keycloakPublico, Realm, ct);
    }
}

async Task JornadaAsync()
{
    using HarnessDeLogin harness = NovoHarness();
    string senha = Mascarar(SenhasDeTeste.Gerar());

    Etapa("o platform-admin conclui o convite pelo link do e-mail");
    Uri link = await mailpit.LinkDeAcoesAsync(platformAdmin, keycloakPublico, Realm, ct);
    await harness.ConcluirLinkDeAcoesAsync(link, senha, ct);

    Etapa("o platform-admin obtém o token pelo device flow");
    TokensDeUsuario tokens = await harness.TokenPorDispositivoAsync(platformAdmin, senha, ct);
    Mascarar(tokens.AccessToken);
    Mascarar(tokens.RefreshToken);

    // Antes de existir qualquer Organization, o Keycloak pede usuário e senha numa página só. Dois passos aqui
    // querem dizer que a jornada não começou de um realm limpo.
    if (harness.PassosDoUltimoLogin != 1)
    {
        throw new FalhaDoHarnessException(
            FamiliaDeFalha.Formulario, etapaAtual,
            $"o login levou {harness.PassosDoUltimoLogin} passos, esperado 1 (nenhuma Organization no realm).");
    }

    Etapa("a receita HS256 antiga é recusada com 401");
    await ExigirAsync(HttpStatusCode.Unauthorized, HttpMethod.Post, "/api/v1/tenants", ReceitaHs256Antiga(), corpo: null);

    Etapa("POST /api/v1/tenants com o token do Keycloak responde 202");
    string sufixo = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
    string emailDoAdmin = $"admin+{sufixo}@acme.test";
    Uri localizacao = await RegistrarTenantAsync(tokens.AccessToken, $"acme-ci-{sufixo}", emailDoAdmin);

    Etapa("o tenant chega a Active (até 90 s)");
    await EsperarStatusAsync(localizacao, tokens.AccessToken, "Active", TimeSpan.FromSeconds(90));

    Etapa("o convite do admin do tenant está no mailpit, com um link que abre");

    // Ao menos uma mensagem, nunca exatamente uma: a entrega "pelo menos uma vez" pode mandar duas.
    if ((await mailpit.EsperarMensagensAsync(emailDoAdmin, 1, ct)).Count < 1)
    {
        throw new FalhaDoHarnessException(
            FamiliaDeFalha.Mailpit, etapaAtual, "nenhum convite para o admin do tenant no mailpit.");
    }

    Uri linkDoAdmin = await mailpit.LinkDeAcoesAsync(emailDoAdmin, keycloakPublico, Realm, ct);

    using (HarnessDeLogin navegador = NovoHarness())
    {
        if (!await navegador.LinkDeAcoesAbreAsync(linkDoAdmin, ct))
        {
            throw new FalhaDoHarnessException(
                FamiliaDeFalha.Formulario, etapaAtual, "o link do convite não abriu a página de ações do Keycloak.");
        }
    }

    GravarEstado(new Estado(tokens.RefreshToken, AccessToken: null, localizacao.ToString(), LocalizacaoPendente: null));
}

async Task AntesDePararAsync()
{
    Estado estado = LerEstado();
    using HarnessDeLogin harness = NovoHarness();

    Etapa("renovação do token antes de parar o Keycloak");
    TokensDeUsuario tokens = await harness.RenovarAsync(estado.RefreshToken, ct);
    Mascarar(tokens.AccessToken);

    // O refresh token novo é gravado ANTES de qualquer outro passo: com a rotação, o antigo já não vale, e repetir a
    // renovação com ele derrubaria a sessão.
    estado = estado with { RefreshToken = Mascarar(tokens.RefreshToken), AccessToken = tokens.AccessToken };
    GravarEstado(estado);

    Etapa("um GET autenticado, para a api guardar as chaves do Keycloak");
    await ExigirAsync(HttpStatusCode.OK, HttpMethod.Get, estado.Localizacao, tokens.AccessToken, corpo: null);
}

async Task ComKeycloakParadoAsync()
{
    Estado estado = LerEstado();
    string token = estado.AccessToken
        ?? throw new FalhaDoHarnessException(FamiliaDeFalha.Api, "estado", "falta o access token da fase antes-de-parar.");

    // A pré-condição é o próprio Keycloak não responder, e não o /health/ready: a api guarda o token do service
    // account em memória por alguns minutos, e o ready continua 200 logo depois do stop.
    Etapa("pré-condição: o Keycloak não responde");
    await ExigirKeycloakParadoAsync();

    Etapa("com o Keycloak parado, POST /api/v1/tenants responde 202");
    string sufixo = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
    Uri localizacao = await RegistrarTenantAsync(token, $"acme-parado-{sufixo}", $"admin+parado{sufixo}@acme.test");

    Etapa("o tenant fica Pending enquanto o Keycloak não volta");
    await EsperarStatusAsync(localizacao, token, "Pending", TimeSpan.FromSeconds(10));

    GravarEstado(estado with { LocalizacaoPendente = localizacao.ToString() });
}

async Task DepoisDeVoltarAsync()
{
    Estado estado = LerEstado();
    string pendente = estado.LocalizacaoPendente
        ?? throw new FalhaDoHarnessException(FamiliaDeFalha.Api, "estado", "falta o tenant da fase com-keycloak-parado.");
    using HarnessDeLogin harness = NovoHarness();

    Etapa("renovação do token depois que o Keycloak voltou");
    TokensDeUsuario tokens = await harness.RenovarAsync(estado.RefreshToken, ct);
    Mascarar(tokens.AccessToken);
    GravarEstado(estado with { RefreshToken = Mascarar(tokens.RefreshToken), AccessToken = tokens.AccessToken });

    // O prazo cobre o teto do backoff do Outbox (60 s) mais uma tentativa inteira contra o Keycloak (10 s), com folga.
    Etapa("o tenant registrado com o Keycloak parado chega a Active (até 150 s)");
    await EsperarStatusAsync(new Uri(pendente, UriKind.RelativeOrAbsolute), tokens.AccessToken, "Active", TimeSpan.FromSeconds(150));
}

async Task ExigirKeycloakParadoAsync()
{
    using HttpClient sonda = new() { Timeout = TimeSpan.FromSeconds(3) };

    try
    {
        using HttpResponseMessage resposta = await sonda.GetAsync(
            new Uri(keycloakTransporte, $"/realms/{Realm}/.well-known/openid-configuration"), ct);
    }
    catch (Exception falha) when (falha is HttpRequestException
                                  || (falha is TaskCanceledException && !ct.IsCancellationRequested))
    {
        // Conexão recusada ou sem resposta em 3 s: é o Keycloak parado, que é o que a fase precisa.
        return;
    }

    throw new FalhaDoHarnessException(
        FamiliaDeFalha.DeviceFlow, etapaAtual, "o Keycloak ainda responde; esta fase precisa dele parado.");
}

// ───────────────────────────── a API ─────────────────────────────

async Task<Uri> RegistrarTenantAsync(string token, string slug, string emailDoAdmin)
{
    using HttpResponseMessage resposta = await ExigirAsync(
        HttpStatusCode.Accepted, HttpMethod.Post, "/api/v1/tenants", token,
        JsonContent.Create(new { name = "Acme", slug, planCode = "free", initialAdminEmail = emailDoAdmin }));

    return resposta.Headers.Location
        ?? throw new FalhaDoHarnessException(FamiliaDeFalha.Api, etapaAtual, "o 202 veio sem o cabeçalho Location.");
}

async Task EsperarStatusAsync(Uri localizacao, string token, string esperado, TimeSpan limite)
{
    DateTimeOffset fim = DateTimeOffset.UtcNow + limite;
    string ultimo = "(nenhum)";

    while (DateTimeOffset.UtcNow < fim)
    {
        using HttpResponseMessage resposta = await ExigirAsync(
            HttpStatusCode.OK, HttpMethod.Get, localizacao.ToString(), token, corpo: null);
        JsonElement corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>(ct);
        ultimo = corpo.GetProperty("status").GetString() ?? "(nulo)";

        if (ultimo == esperado)
        {
            return;
        }

        if (ultimo == "ProvisioningFailed")
        {
            throw new FalhaDoHarnessException(FamiliaDeFalha.Api, etapaAtual, "o tenant caiu em ProvisioningFailed.");
        }

        await Task.Delay(TimeSpan.FromSeconds(3), ct);
    }

    throw new FalhaDoHarnessException(
        FamiliaDeFalha.Prazo, etapaAtual, $"o tenant não chegou a {esperado} em {limite.TotalSeconds:0} s; ficou em {ultimo}.");
}

// Toda asserção de status é EXATA. "Diferente de 200" deixaria um 401 por token vencido passar por um 403 esperado.
async Task<HttpResponseMessage> ExigirAsync(
    HttpStatusCode esperado, HttpMethod metodo, string rota, string? token, HttpContent? corpo)
{
    using HttpRequestMessage pedido = new(metodo, new Uri(rota, UriKind.RelativeOrAbsolute)) { Content = corpo };

    if (token is not null)
    {
        pedido.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    HttpResponseMessage resposta;

    try
    {
        resposta = await http.SendAsync(pedido, ct);
    }
    catch (HttpRequestException falha)
    {
        throw new FalhaDoHarnessException(
            FamiliaDeFalha.Api, etapaAtual, $"a API não respondeu em {metodo} {new Uri(api, rota).AbsolutePath}.", falha);
    }

    if (resposta.StatusCode != esperado)
    {
        int veio = (int)resposta.StatusCode;
        resposta.Dispose();

        throw new FalhaDoHarnessException(
            FamiliaDeFalha.Api, etapaAtual,
            $"{metodo} {new Uri(api, rota).AbsolutePath} respondeu {veio}, esperado {(int)esperado}.");
    }

    return resposta;
}

// O token que o README e a CI ensinavam a montar antes dos tokens do Keycloak: HS256 com a chave de desenvolvimento
// publicada. Precisa levar 401 para sempre.
static string ReceitaHs256Antiga()
{
    long agora = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    string cabecalho = Base64Url("""{"alg":"HS256","typ":"JWT"}""");
    string payload = Base64Url(
        $$"""{"sub":"0199a000-0000-7000-8000-000000000001","roles":"platform-admin","iss":"identitygateway","aud":"identitygateway-api","nbf":{{agora}},"exp":{{agora + 3600}}}""");
    byte[] assinatura = HMACSHA256.HashData(
        Encoding.UTF8.GetBytes("chave-de-desenvolvimento-nao-use-em-producao"), Encoding.ASCII.GetBytes($"{cabecalho}.{payload}"));

    return $"{cabecalho}.{payload}.{System.Buffers.Text.Base64Url.EncodeToString(assinatura)}";

    static string Base64Url(string texto) => System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(texto));
}

// ───────────────────────────── o estado entre as fases ─────────────────────────────

void GravarEstado(Estado estado)
{
    File.WriteAllText(arquivoDeEstado, JsonSerializer.Serialize(estado));

    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(arquivoDeEstado, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

Estado LerEstado()
{
    if (!File.Exists(arquivoDeEstado))
    {
        throw new FalhaDoHarnessException(
            FamiliaDeFalha.Api, "estado", "o arquivo de estado não existe: a fase `jornada` precisa rodar antes.");
    }

    return JsonSerializer.Deserialize<Estado>(File.ReadAllText(arquivoDeEstado))
        ?? throw new FalhaDoHarnessException(FamiliaDeFalha.Api, "estado", "o arquivo de estado está vazio.");
}

// ───────────────────────────── a saída ─────────────────────────────

HarnessDeLogin NovoHarness() => new(keycloakPublico, keycloakTransporte, ClientDeDemonstracao, realm: Realm);

void Etapa(string nome)
{
    FecharEtapa();
    etapaAtual = nome;
    relogio.Restart();
}

void FecharEtapa()
{
    if (etapaAtual != "início")
    {
        etapas.Add((etapaAtual, relogio.Elapsed.TotalSeconds));
        Console.WriteLine($"ok  {relogio.Elapsed.TotalSeconds,6:0.0}s  {etapaAtual}");
    }
}

int Falhar(string motivo, int codigo)
{
    Console.WriteLine($"FALHOU  {motivo}");

    if (noGitHub)
    {
        Console.WriteLine($"::error title={etapaAtual}::{motivo}");
    }

    Resumir(falha: motivo);
    return codigo;
}

void Resumir(string? falha)
{
    if (falha is null)
    {
        FecharEtapa();
    }

    if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is not { Length: > 0 } resumo)
    {
        return;
    }

    StringBuilder tabela = new();
    tabela.AppendLine(CultureInfo.InvariantCulture, $"### jornada-compose {string.Join(' ', args)}");
    tabela.AppendLine();
    tabela.AppendLine("| Etapa | Tempo |");
    tabela.AppendLine("|---|---|");

    foreach ((string etapa, double segundos) in etapas)
    {
        tabela.AppendLine(CultureInfo.InvariantCulture, $"| {etapa} | {segundos:0.0} s |");
    }

    if (falha is not null)
    {
        tabela.AppendLine(CultureInfo.InvariantCulture, $"| **falhou** — {falha} | |");
    }

    File.AppendAllText(resumo, tabela.ToString());
}

// Registra o segredo no mascaramento do GitHub Actions — defesa a mais: o app nunca o imprime.
string Mascarar(string segredo)
{
    if (noGitHub)
    {
        Console.WriteLine($"::add-mask::{segredo}");
    }

    return segredo;
}

static string Ambiente(string nome, string padrao) =>
    Environment.GetEnvironmentVariable(nome) is { Length: > 0 } valor ? valor : padrao;

static int LerEsperado(string[] argumentos)
{
    int indice = Array.IndexOf(argumentos, "--esperado");

    return indice >= 0
           && indice + 1 < argumentos.Length
           && int.TryParse(argumentos[indice + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int esperado)
        ? esperado
        : throw new FalhaDoHarnessException(FamiliaDeFalha.Mailpit, "convites", "informe --esperado N.");
}

/// <summary>O que uma fase deixa para a seguinte. Credenciais de um Keycloak descartável.</summary>
internal sealed record Estado(string RefreshToken, string? AccessToken, string Localizacao, string? LocalizacaoPendente);

internal partial class Program
{
    [GeneratedRegex(@"\?[^\s""']+")]
    private static partial Regex SemQueryStrings();
}
```

Quatro coisas do app que não são óbvias:
- **Toda asserção de status é exata.** `ExigirAsync` compara com `==`. Com 5 minutos de token, "diferente de 200" deixaria um `401` por vencimento passar por um `403` esperado.
- **O estado entre as fases vai num arquivo, nunca em memória de outro processo:** a senha morre com a fase que a gerou; o refresh token é regravado a cada renovação, antes de qualquer outro passo.
- **Nenhuma renovação é repetida.** Se `RenovarAsync` falhar, a fase falha: reusar o refresh token derrubaria a sessão, e o conserto é um device flow novo (rodar `jornada` de novo num ambiente limpo).
- **`PassosDoUltimoLogin` igual a 1 é uma asserção sobre o ambiente:** o platform-admin entra antes de existir qualquer Organization. Rodar `jornada` duas vezes sobre os mesmos volumes falha nela — e antes disso no link, que já foi concluído.

- [ ] **Passo 4: Compilar e rodar a jornada inteira num projeto isolado**

Run: `dotnet build -c Release tools/jornada-compose.cs`
Expected: `0 Aviso(s)`, `0 Erro(s)`. (Se aparecer `CS7022`, a biblioteca voltou a ser um executável de teste; se aparecer `IL2026`/`IL3050`, falta o `#:property PublishAot=false`.)

Run: `dotnet test tests/IdentityGateway.ArchitectureTests --filter-class "*RegrasDeFerramentasTests"`
Expected: PASS em `FerramentasNaoDeclaramPacotes` e `AppDaJornadaUsaABibliotecaDoHarnessESemAot`. `PadraoDoEmailDoPlatformAdminEOMesmoNoComposeNoAppENoReadme` ainda FALHA: o README só cita o e-mail no Passo 6.

Agora a mesma sequência do job, no projeto isolado `igverif` (as mesmas pré-condições do Passo 5 da Tarefa 10: nada do projeto padrão de pé, portas livres). Numa sessão só do Git Bash, na raiz do repositório:

```bash
export IG_ESTADO="$(mktemp -d)/ig-jornada-estado.json"
jornada() { dotnet run -c Release tools/jornada-compose.cs -- "$@"; echo "exit=$?"; }

docker compose -p igverif up -d --build --wait --wait-timeout 300 api
curl -fsS --max-time 10 http://127.0.0.1:8080/health/ready; echo

# 1. o convite sai uma vez só
jornada convites --esperado 1
docker compose -p igverif run --rm -T --no-deps platform-admin-invite; echo "exit=$?"
jornada convites --esperado 1

# 2. a jornada
jornada jornada

# 3. Keycloak parado
jornada antes-de-parar
docker compose -p igverif stop keycloak
jornada com-keycloak-parado
docker compose -p igverif start keycloak
for _ in $(seq 1 60); do
  estado=$(docker inspect --format '{{.State.Health.Status}}' "$(docker compose -p igverif ps -q keycloak)" 2>/dev/null || true)
  if [ "$estado" = "healthy" ]; then break; fi
  sleep 3
done
echo "keycloak: $estado"
jornada depois-de-voltar

# 4. segunda subida sobre os mesmos volumes
docker compose -p igverif down
docker compose -p igverif up -d --wait --wait-timeout 300 api
docker compose -p igverif logs --no-log-prefix platform-admin-invite | grep "convite já enviado"
jornada convites --esperado 0
```

Expected: todo `exit=0`; uma linha `ok  <tempo>s  <etapa>` por etapa; `keycloak: healthy`; a linha `platform-admin-invite: convite já enviado em …` no `grep`. Na `jornada`: a etapa do device flow leva de 5 a 7 s, e a do `Active`, poucos segundos. Em `depois-de-voltar`, o `Active` pode levar até um minuto (o teto do backoff do Outbox).

Conferir que **nada secreto** saiu: reveja a saída do terminal e procure por `eyJ` (começo de um JWT), `action-token?key=` e `user_code` — nenhum pode aparecer.

Run: `docker compose -p igverif logs --no-color api | grep -c "eyJ"`
Expected: `0` — a API não registra token.

Se uma etapa falhar, a mensagem diz a peça (o código de saída é a família) e, numa página desconhecida do Keycloak, o título, o id do formulário e os nomes dos campos. Corrija a causa; não afrouxe a asserção.

Registrar para o handoff os tempos de cada etapa. **Não derrube o `igverif` ainda**: o Passo 7 usa.

- [ ] **Passo 5: O workflow**

Em `.github/workflows/ci.yml`:

1. Depois do bloco `env:` do topo (antes de `jobs:`), acrescentar:

```yaml

# bash em todo passo `run`: o GitHub o invoca com -eo pipefail, e um `curl | jq` que falhe no curl passa a derrubar o
# passo — sem isso, o código de saída é o do último comando do pipe.
defaults:
  run:
    shell: bash
```

2. No job `build`, depois do passo `Compilar`:

```yaml

      # O app de arquivo único da jornada fica fora da solução: sem este passo, um erro de compilação nele só
      # apareceria no job Compose, depois de o Docker já ter subido tudo.
      - name: Compilar o app da jornada
        run: dotnet build --configuration Release tools/jornada-compose.cs
```

3. Substituir o job `compose` **inteiro** — do comentário `# ─────────────────────────── compose ───────────────────────────` até o fim do arquivo — por:

```yaml
  # ─────────────────────────── compose ───────────────────────────
  #
  # A promessa do M0 é "git clone + docker compose up funcionam na primeira tentativa", e o critério dele, "primeiro
  # curl com token do Keycloak". Verificados à mão, valem só no dia em que alguém testou; aqui valem a cada PR.
  #
  # Quem percorre a jornada é o app tools/jornada-compose.cs, com o mesmo harness de login dos testes: conclui o
  # convite do platform-admin pelo link do e-mail, obtém o token pelo device flow, registra um tenant e espera o
  # provisionamento — e repete o registro com o Keycloak parado, que é a demonstração nº 1 do README. Ele nunca imprime
  # token, link, código nem senha.
  #
  # `up ... api`, com o nome do serviço: o --wait espera a api ficar saudável e não tropeça nos one-shots, que
  # terminam de propósito. Seq e Jaeger não sobem aqui — a api não depende deles.
  #
  # Sempre 127.0.0.1 nos endereços discados (localhost pode tentar ::1 primeiro); localhost:8081 aparece só como o
  # endereço PÚBLICO do Keycloak, que é o emissor dos tokens.
  compose:
    name: Compose
    runs-on: ubuntu-latest
    needs: build
    timeout-minutes: 20
    env:
      # Lido pelo compose (o e-mail gravado no realm) e pelo app (o destinatário que ele procura no mailpit).
      PLATFORM_ADMIN_EMAIL: platform-admin@identity-gateway.local
      # O que uma fase do app deixa para a seguinte: credenciais de um Keycloak descartável. Apagado no fim.
      IG_ESTADO: ${{ runner.temp }}/ig-jornada-estado.json

    steps:
      - uses: actions/checkout@v4

      - name: Configurar .NET
        uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json

      - name: Cache de pacotes NuGet
        uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: nuget-${{ runner.os }}-${{ hashFiles('**/Directory.Packages.props', '**/*.csproj') }}
          restore-keys: nuget-${{ runner.os }}-

      # Antes do Docker: um erro de compilação no app falha em segundos, e não depois de três minutos de `up`.
      - name: Compilar o app da jornada
        run: dotnet build --configuration Release tools/jornada-compose.cs

      - name: Subir até a API ficar pronta
        timeout-minutes: 8
        run: docker compose up -d --build --wait --wait-timeout 300 api

      - name: Conferir o ready
        run: curl --fail --silent --show-error --max-time 10 http://127.0.0.1:8080/health/ready

      # Com o convite ainda pendente e o mailpit vivo: é o único estado em que "não reenviou" não é vacuoso. Um
      # one-shot que não lesse o marcador mandaria o segundo e-mail aqui, e a segunda contagem daria 2.
      - name: O convite do platform-admin sai uma vez só
        timeout-minutes: 3
        run: |
          dotnet run --configuration Release tools/jornada-compose.cs -- convites --esperado 1
          docker compose run --rm -T --no-deps platform-admin-invite
          dotnet run --configuration Release tools/jornada-compose.cs -- convites --esperado 1

      # O convite do platform-admin concluído pelo link, o token pelo device flow, a receita HS256 antiga recusada, o
      # tenant registrado e provisionado, e o convite do admin do tenant no mailpit com um link que abre.
      - name: A jornada com token do Keycloak
        timeout-minutes: 5
        run: dotnet run --configuration Release tools/jornada-compose.cs -- jornada

      # A demonstração nº 1 do README, com prova automática: a api aceita o tenant com o Keycloak parado (as chaves
      # já estão em memória) e o provisiona quando ele volta. stop/start, e não pause: o stop recusa a conexão na
      # hora, e é a sequência que o README manda fazer. O refresh token precisa sobreviver ao reinício.
      - name: Registrar com o Keycloak parado e provisionar quando ele volta
        timeout-minutes: 8
        run: |
          dotnet run --configuration Release tools/jornada-compose.cs -- antes-de-parar
          docker compose stop keycloak
          dotnet run --configuration Release tools/jornada-compose.cs -- com-keycloak-parado
          docker compose start keycloak

          estado=desconhecido
          for _ in $(seq 1 60); do
            estado=$(docker inspect --format '{{.State.Health.Status}}' "$(docker compose ps -q keycloak)" || true)
            if [ "$estado" = "healthy" ]; then break; fi
            sleep 3
          done
          if [ "$estado" != "healthy" ]; then echo "O Keycloak não voltou a ficar saudável em 180 s."; exit 1; fi

          dotnet run --configuration Release tools/jornada-compose.cs -- depois-de-voltar

      # Derruba SEM apagar volumes e sobe de novo: os one-shots são idempotentes — o migrate sobre um banco já migrado,
      # as chaves sobre o volume que já as tem, e o convite, que não é reenviado. O mailpit não tem volume: a contagem
      # zero prova que nada foi enviado nesta subida, não que o marcador funciona (isso é o passo "uma vez só").
      # O `grep <<<` e não `logs | grep -q`: com pipefail, o grep que fecha o pipe cedo viraria falha por SIGPIPE.
      - name: Subir de novo sobre os mesmos volumes
        timeout-minutes: 8
        run: |
          docker compose down
          docker compose up -d --wait --wait-timeout 300 api
          curl --fail --silent --show-error --max-time 10 http://127.0.0.1:8080/health/ready
          grep -q "convite já enviado" <<< "$(docker compose logs --no-log-prefix platform-admin-invite)"
          dotnet run --configuration Release tools/jornada-compose.cs -- convites --esperado 0

      # Os serviços um a um, e NUNCA o gateway-keys: ele imprime a senha do admin master na primeira subida. Do
      # mailpit, só os metadados — o corpo traz o link, que troca a senha do convidado.
      - name: Diagnóstico em caso de falha
        if: failure()
        run: |
          docker compose ps -a || true
          docker compose logs --no-color platform-admin-invite || true
          docker compose logs --no-color api || true
          docker compose logs --no-color keycloak || true
          echo "── mailpit: mensagens recebidas (destinatário, assunto, horário) ──"
          curl --silent --max-time 10 http://127.0.0.1:8025/api/v1/messages \
            | jq '[.messages[] | {para: [.To[].Address], assunto: .Subject, criado: .Created}]' || true

      - name: Limpar
        if: always()
        run: |
          docker compose down -v
          rm -f "$IG_ESTADO"
```

Run: `grep -n "HS256\|chave-de-desenvolvimento\|localhost:8080\|jq -r '.Text'" .github/workflows/ci.yml`
Expected: nenhuma linha — o token montado à mão, o bloco de `jq` do convite e o `localhost` nos `curl` saíram.

- [ ] **Passo 6: O README**

Em `README.md`:

1. Na tabela da seção `### Keycloak`, depois da linha `| E-mails (mailpit) | …`:

```markdown
| Primeiro platform-admin | `platform-admin@identity-gateway.local` — nasce **sem senha**; o convite está no mailpit |
```

2. Trocar o parágrafo que começa em `**Já tinha subido o compose antes do convite do admin inicial? Rode` (até `para trás do código.`) por:

```markdown
**Já tinha subido o compose antes dos tokens do Keycloak? Rode `docker compose down -v` uma vez.** O realm só é
importado na primeira subida, e num volume antigo faltam os scopes da Gateway, o client de demonstração, o catálogo
de papéis e o platform-admin. O serviço `platform-admin-invite` detecta isso e sai com erro — a API nem sobe, e o
motivo está em `docker compose logs platform-admin-invite`. Depois do `down -v`, `docker compose up -d --build`,
para a imagem da API não ficar para trás do código.

**O primeiro platform-admin.** A conta nasce no import do realm, sem senha, com o e-mail de `PLATFORM_ADMIN_EMAIL`
(padrão `platform-admin@identity-gateway.local`, sempre em minúsculas). Na primeira subida, o serviço
`platform-admin-invite` manda **um** e-mail de convite para o mailpit; o link vale **4 horas**. Algumas coisas que
convém saber:

- **Perdeu o e-mail, ou o link expirou?** `docker compose run --rm -e REENVIAR=1 platform-admin-invite`. O reenvio é
  só por esse comando — subir o compose de novo não reenvia. Se o envio da primeira subida falhou, também é por ele.
- **O mailpit local não tem autenticação.** Quem abre `http://localhost:8025` enquanto um link de convite não expirou
  consegue definir a senha daquela conta. Um link já emitido continua valendo até expirar, mesmo depois de outro ter
  sido usado. Por isso o do platform-admin é curto, e por isso o mailpit só escuta em `127.0.0.1`.
- **Não troque nem apague o admin do master do Keycloak** (o console sugere trocá-lo). O `platform-admin-invite` faz
  login com ele a cada subida; se mudou, `docker compose down -v`.
- **`PLATFORM_ADMIN_EMAIL` fica fixado no primeiro import.** Mudar depois exige `docker compose down -v`.
```

3. Substituir a seção da demonstração inteira — do título `### Demonstração: o tenant é provisionado quando o Keycloak volta, e o admin recebe o convite` até o parágrafo que termina em `e o endereço passa a existir só no Keycloak.` — por:

````markdown
### Demonstração: token do Keycloak, e o tenant provisionado quando o Keycloak volta

A API só aceita access tokens emitidos pelo Keycloak. A senha é digitada **só no Keycloak**: o terminal pede um
código de dispositivo, você entra pelo navegador, e o terminal troca o código pelo token (Device Authorization
Grant, num client público que existe só no ambiente local). Pressupõe o compose de pé (`docker compose up -d --build`,
acima) e `jq`.

**1. Defina a senha do platform-admin.** Abra http://localhost:8025, o e-mail para
`platform-admin@identity-gateway.local`, e clique no link: ele abre o Keycloak em `http://localhost:8081`. Siga,
defina a senha e informe nome e sobrenome.

**2. Peça o token pelo device flow.**

```bash
KC=http://localhost:8081/realms/identity-gateway/protocol/openid-connect

pedido=$(curl -s -X POST "$KC/auth/device" -d client_id=identity-gateway-demo -d scope=openid)
DEVICE_CODE=$(echo "$pedido" | jq -r .device_code | tr -d '\r')
echo "$pedido" | jq -r .verification_uri_complete
# Abra o endereço impresso no navegador, entre com o e-mail do platform-admin e a senha, e aceite o consentimento.
```

Depois de aceitar, espere uns 5 segundos (antes disso a resposta é `slow_down`) e troque o código pelo token:

```bash
tokens=$(curl -s -X POST "$KC/token" \
  -d grant_type=urn:ietf:params:oauth:grant-type:device_code \
  -d client_id=identity-gateway-demo -d device_code="$DEVICE_CODE")
TOKEN=$(echo "$tokens" | jq -r .access_token | tr -d '\r')
REFRESH=$(echo "$tokens" | jq -r .refresh_token | tr -d '\r')
```

O access token vale **5 minutos**. O refresh token vale 30 minutos de inatividade, e **cada renovação devolve um
refresh token novo**: o usado deixa de valer. Não repita uma renovação com o mesmo refresh token — o Keycloak trata o
reuso como roubo e encerra a sessão; se acontecer, refaça o device flow.

**3. Faça um pedido autenticado antes de parar o Keycloak.** A API busca as chaves públicas do Keycloak na primeira
vez que valida um token e as guarda em memória. É esse pedido que faz a demonstração funcionar com o Keycloak parado:

```bash
curl -s -o /dev/null -w '%{http_code}\n' -H "Authorization: Bearer $TOKEN" \
  http://localhost:8080/api/v1/tenants/00000000-0000-0000-0000-000000000000/provisioning
# 404: autenticado e autorizado — esse tenant é que não existe. Um 401 aqui quer dizer token vencido ou errado.
```

**4. Pare o Keycloak e registre um tenant.**

```bash
# E-mail e slug únicos a cada execução: um e-mail já usado por outra conta do Keycloak faz o provisionamento
# falhar de propósito (a mesma pessoa não administra dois tenants), e o slug é único e imutável.
EMAIL="admin+$(date +%s)@acme.test"
SLUG="acme-${EMAIL//[^0-9]/}"

docker compose stop keycloak

curl -si -X POST http://localhost:8080/api/v1/tenants \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d "{\"name\":\"Acme\",\"slug\":\"$SLUG\",\"planCode\":\"free\",\"initialAdminEmail\":\"$EMAIL\"}"
# 202 Accepted, com Location: /api/v1/tenants/{id}/provisioning

curl -s http://localhost:8080/api/v1/tenants/{id}/provisioning -H "Authorization: Bearer $TOKEN"
# {"tenantId":"…","status":"Pending",…}
```

**5. Suba o Keycloak, renove o token e veja o tenant ativo.**

```bash
docker compose start keycloak

# quando o Keycloak responder de novo (uns 30 s), renove — e guarde o refresh token novo:
tokens=$(curl -s -X POST "$KC/token" -d grant_type=refresh_token \
  -d client_id=identity-gateway-demo -d refresh_token="$REFRESH")
TOKEN=$(echo "$tokens" | jq -r .access_token | tr -d '\r')
REFRESH=$(echo "$tokens" | jq -r .refresh_token | tr -d '\r')

# em cerca de um minuto — o teto do backoff do Outbox:
curl -s http://localhost:8080/api/v1/tenants/{id}/provisioning -H "Authorization: Bearer $TOKEN"
# {"tenantId":"…","status":"Active",…}
```

Agora volte ao mailpit: há um e-mail de convite para o endereço de `$EMAIL`, o administrador do tenant. O link dele
vale 7 dias (`Invitations:LinkLifetime`). O e-mail do admin fica guardado no tenant só até a ativação: com o tenant
`Active`, a coluna `tenants.initial_admin_email` volta a ser nula, e o endereço passa a existir só no Keycloak.

**Por que nessa ordem.** O passo 3 vem antes do `stop` porque, sem nenhum token validado, a API ainda não tem as
chaves — e com o Keycloak parado ela não tem de onde buscá-las: todo token levaria `401`. E tudo entre o passo 2 e o
`POST` do passo 4 precisa caber nos 5 minutos do access token.

**O token no histórico do shell.** As variáveis acima ficam na sessão, e os comandos, no histórico — mas não o valor
do token, que só aparece se você o colar numa linha de comando. Se precisar colar um token, use `read -rs TOKEN`.

**A mesma jornada, sem navegador.** É o que o job `Compose` da CI roda a cada PR, com um ambiente recém-criado
(o convite do platform-admin ainda por concluir):

```bash
dotnet run tools/jornada-compose.cs -- jornada
```

O app conclui o convite pelo link do mailpit, faz o device flow submetendo as páginas do Keycloak e percorre os passos
acima, com uma senha gerada em memória. Ele nunca imprime token, link, código nem senha.
````

Run: `grep -n "HS256\|chave-de-desenvolvimento\|b64url\|identitygateway-api" README.md`
Expected: nenhuma linha — a receita do token simétrico saiu.

Run: `dotnet test tests/IdentityGateway.ArchitectureTests --filter-class "*RegrasDeFerramentasTests"`
Expected: PASS nos três.

- [ ] **Passo 7: 🧪 Provas por mutação**

Com o `igverif` do Passo 4 ainda de pé. Como o convite do platform-admin já foi concluído nele, recrie o ambiente para as mutações do marcador: `docker compose -p igverif down -v && docker compose -p igverif up -d --build --wait --wait-timeout 300 api`.

| # | Mutação | Rodar | Vermelho esperado |
|---|---|---|---|
| 1 | No `docker-compose.yml`, no script do `platform-admin-invite`, trocar `if [ -n "$$marcador" ] && [ "$${REENVIAR:-0}" != "1" ]; then` por `if false; then` | a sequência do passo "uma vez só": `jornada convites --esperado 1`, `docker compose -p igverif run --rm -T --no-deps platform-admin-invite`, `jornada convites --esperado 1` | a terceira linha sai com `exit=10`: "esperados 1 convites, achados 2" |
| 2 | Idem, lendo o marcador com `--fields attributes` (como na Tarefa 10) | a mesma sequência, depois de `git restore docker-compose.yml` e de recriar o ambiente | idem: `exit=10`, "achados 2" |
| 3 | Em `tools/jornada-compose.cs`, trocar o padrão do e-mail para `"admin@identity-gateway.local"` | `dotnet test tests/IdentityGateway.ArchitectureTests --filter-class "*RegrasDeFerramentasTests"` | `PadraoDoEmailDoPlatformAdminEOMesmoNoComposeNoAppENoReadme` |
| 4 | Em `tools/jornada-compose.cs`, acrescentar a linha `#:package Humanizer@2.14.1` no topo | idem | `FerramentasNaoDeclaramPacotes` |
| 5 | Em `tools/jornada-compose.cs`, tirar a linha `#:property PublishAot=false` | idem, e `dotnet build -c Release tools/jornada-compose.cs` | `AppDaJornadaUsaABibliotecaDoHarnessESemAot`; anote também o que o build diz (a spec registra `IL2026`/`IL3050`) |

Reverter cada uma (`git restore <arquivo>` depois de `git add` do que a tarefa escreveu) e, no fim:

Run: `docker compose -p igverif down -v`
Expected: remove os contêineres e os volumes `igverif_*`. Os volumes `identitygateway_*` não aparecem na saída.

Run: `rm -f "$IG_ESTADO"`

- [ ] **Passo 8: Commit**

```bash
git add tools .github/workflows/ci.yml README.md tests/IdentityGateway.ArchitectureTests/RegrasDeFerramentasTests.cs
git commit -m "ci: a jornada do compose com token do Keycloak, e a demonstracao por device flow

O job Compose passa a rodar tools/jornada-compose.cs, um app de arquivo
unico que usa o harness de login dos testes: confere que o convite do
platform-admin sai uma vez so, conclui o convite pelo link do e-mail,
obtem o token pelo device flow, confirma que a receita HS256 antiga leva
401, registra um tenant e espera o Active, confere o convite do admin do
tenant, e repete o registro com o Keycloak parado (stop/start), renovando
o token depois. Cada passo longo tem prazo proprio; o diagnostico nunca
inclui o log do gateway-keys nem o corpo dos e-mails. bash com pipefail em
todo passo.

O README troca a receita do token simetrico pela demonstracao com device
flow e curl, na ordem que funciona com o Keycloak parado, e ganha as notas
do convite do platform-admin.

Rodado localmente num projeto isolado (igverif). Mutacoes: one-shot sem o
marcador e com o marcador lido por --fields (a contagem vai a 2), padrao
do e-mail divergente, pacote declarado na ferramenta e AOT ligado."
```

Run: `git log -1 --format=%B | grep -Eci "co-authored|generated with"`
Expected: `0`.

---

> **Estado deste arquivo:** as Tarefas 1 a 11 da parte D1 estão completas acima. A Tarefa 12 (documentação da D1) e a parte D2 (Tarefas 13 a 17) entram nos próximos commits deste plano.
