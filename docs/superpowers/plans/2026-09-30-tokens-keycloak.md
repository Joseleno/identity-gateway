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
- **Docker Desktop ligado** nas Tarefas 1 a 5, 9 a 11 e 14 a 16 (Testcontainers e compose) e na suíte completa. Sem ele, `DockerUnavailableException` é ambiente, não regressão.
- **Verificação local do compose sempre num projeto isolado** (`docker compose -p igverif …`), derrubado com `down -v` no fim. Os volumes `identitygateway_*` do autor nunca são tocados.
- 🧪 = passo "Prova por mutação" obrigatório: aplicar a mutação, rodar o teste indicado, ver vermelho por asserção (erro de compilação não conta), **reverter** (conferir com `git diff --stat` que o arquivo voltou), ver verde. Registrar a mutação na mensagem de commit e, depois, na tabela do handoff.

## Verificado ao vivo ao escrever este plano (2026-10-01)

O material da sessão de design ficou num scratchpad efêmero. Antes de escrever as tarefas, três peças foram refeitas e executadas contra um Keycloak 26.7.4 com mailpit v1.31.3, num ambiente descartável:

- **O realm da Tarefa 2 importa** (`Realm 'identity-gateway' imported`), e a leitura pelo master confere: embutidos presentes, `gateway-*` fora dos defaults do realm, papel padrão `[manage-account, view-profile]`, demo com os cinco scopes, service account com `basic` e `roles`.
- **O script do one-shot da Tarefa 10 roda como está:** primeira execução envia (1 e-mail, link com `exp − iat = 14400`), a segunda sai `0` com "convite já enviado em …" (continua 1 e-mail), `REENVIAR=1` envia outro, e o e-mail configurado em maiúsculas é comparado em minúsculas.
- **O `HarnessDeLogin` da Tarefa 4 roda como está:** link de ações em 4 páginas (informação → senha → perfil → informação); device flow em 5,2 s; platform-admin sem Organization no realm entra em **1** passo de login, e um usuário com Organization em **2** (identity-first); segundo login na mesma instância, **0** passos (cookie de SSO); renovação devolve refresh token novo; o refresh reusado leva `invalid_grant` e, depois dele, **o novo também**. Claims do demo iguais aos da §3.1 da spec. O client de device flow do fixture, criado sem scopes declarados (herda os defaults do realm), emite token com `aud: "account"`, sem `identity-gateway-api`, aceito pela Account API (`204` na alteração permitida, `400` no `tenant_id`). O `admin-cli` do realm por ROPC emite token leve, sem `aud` e sem `sub`.

Dois achados dessa execução entraram no plano: **senha errada fazia o harness reenviar o login doze vezes** (o formulário volta igual), o que com `bruteForceProtected` bloquearia a conta — o harness agora falha na primeira volta do formulário, com teste; e **um link de ações já concluído responde `400` com a página de erro**, que o harness reporta na hora. Os doze testes unitários do harness (Tarefa 4) também foram compilados com os analisadores do repositório e executados.

O que **não** foi executado na D1 e fica por conta dos testes de cada tarefa: a integração com o `Program.cs` real e o `WebApplicationFactory` (as peças de autenticação rodaram num protótipo à parte), as fases do app de arquivo único contra a API e o job da CI.

**Revisão por seis especialistas (2026-10-01), antes da execução.** Autenticação, autorização, realm/compose/CI, harness e testes com Keycloak, cobertura da spec e âncoras contra o repositório. Três achados impediam a execução e foram corrigidos: o workflow usava o contexto `runner` no `env` do job (inválido — nenhum job rodaria), três asserções da Tarefa 2 não compilavam (descarte em árvore de expressão) e um teste público da Tarefa 9 expunha um tipo interno. As correções que mexeram em código foram reexecutadas: as regras do realm e do compose no clone, com as mutações; o script do one-shot ao vivo, inclusive os quatro ramos de falha que nunca tinham sido exercitados; o limite do aviso de chaves no protótipo de autenticação; e o app da jornada, recompilado nas duas versões.

**Da D2**, o código de produção e os testes das Tarefas 13 a 15 foram compilados com os analisadores do repositório e executados num clone, com as provas por mutação das tabelas; o detalhe está na abertura da Parte D2. A Tarefa 16 não foi executada.

## Foco de revisão

Cinco condições que a spec implica mas não lista como caso; cada uma tem teste na tarefa dona:

1. **A pessoa clica duas vezes no link do e-mail** (ou abre um link já concluído): o harness e o app falham na hora, com a etapa e "página de erro", sem laço e sem esperar prazo. Tarefa 5 (`LinkJaConcluido_FalhaNaHoraComAPaginaDeErro`).
2. **A pessoa nega o consentimento, ou deixa o código do dispositivo expirar:** falha imediata com o nome do erro (`access_denied`, `expired_token`), nunca espera pelo prazo inteiro nem repete. Tarefa 4 (`ConsentimentoNegado_FalhaNaHora`, `DeviceCodeExpirado_FalhaNaHora`).
3. **`PLATFORM_ADMIN_EMAIL` vazio ou com maiúsculas** no ambiente de quem sobe o compose: o Keycloak não sobe, em vez de importar um placeholder literal ou um e-mail que o one-shot não acha. Tarefa 10 (`EntrypointDoKeycloakRecusaEmailVazioOuComMaiusculas` e o passo ao vivo).
4. **Token forjado com caractere de controle no `iss`:** `401`, nunca `500` (com `IncludeErrorDetails` ligado, o Kestrel recusaria o cabeçalho e o `401` viraria `500`). Tarefa 8 (caso `iss com caractere de controle` da suíte negativa).
5. **Segunda subida depois de perder o e-mail** (o mailpit não tem volume): o one-shot diz "já enviado" e não reenvia; quem ainda não concluiu só recupera o convite pelo comando de reenvio — que precisa funcionar nesse estado. Tarefa 10 (passo ao vivo "reenvio depois do `down`/`up`").

Nota para o revisor: **a D1 troca o mecanismo de autenticação inteiro**, e os 16 testes funcionais que usam `CreateClientAutenticado` continuam verdes sem mudar uma linha. Isso é o desenho (o emissor de teste imita o token real), mas também é o risco: um verde ali não prova nada sobre o Keycloak. Quem prova são a coleção com Keycloak real (Tarefa 9), a ponte de contrato entre o token real e o do emissor de teste, e o job `Compose`.

**Na D2, o que revisar com mais cuidado é o que o HTTP não mostra:** um requirement que faz `return` onde devia fazer `Fail()` responde o mesmo `403`, e um handler da pertença registrado na ordem errada também. Os dois só aparecem nos testes unitários da policy (Tarefas 13 e 14), e é por isso que eles existem. Confira ainda que nenhum `403` da rota distingue "tenant que não existe" de "tenant alheio" — nem pelo corpo, nem pelo status.

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
- **O aviso de chaves indisponíveis é limitado a um por 30 s, e só a falta de chave o dispara** (Tarefa 7). A spec (DT6) pede um `Warning` no `OnAuthenticationFailed` "quando a exceção é de chave ou de configuração". Na prática só um tipo de exceção chega ali (a biblioteca engole a falha da busca), e o mesmo tipo chega para um token de `kid` inventado assinado por outra chave — que qualquer anônimo manda, sem consumir cota. Sem o limite, o alerta do DT6 seria afogado. O limite é um serviço por host (`AvisoDeChavesIndisponiveis`), e não um campo estático, para os hosts de teste não se calarem uns aos outros. Visto no protótipo: com a chave certa e um `kid` desconhecido, o token é aceito (a biblioteca tenta todas as chaves) — não é brecha, e por isso o caso de teste usa outra chave.
- **O aviso da lista de `azp` vazia é um `IHostedService`** (`AvisoDeClientsPermitidos`): é o que roda na subida do host real e do `WebApplicationFactory`, e por isso tem teste.
- **O teste de vazamento do e-mail no token usa a factory do OIDC falso** (Tarefa 9), com um sink do Serilog e um exportador OpenTelemetry em memória, as duas exceções declaradas à regra "só configuração" (§5.5 da spec).
- **A ordem das tarefas da D1 não segue a §6 da spec**, que pede o bootstrap e o app de CI "o mais cedo que a ordem permite", antes da troca da autenticação. Aqui o one-shot é a Tarefa 10 e o app, a 11, depois das Tarefas 6 a 9: é a decomposição fixada no handoff do design. A Tarefa 10 tira `Jwt__SigningKey` do compose e a fase `jornada` do app precisa da API já aceitando tokens do Keycloak — as duas dependem da Tarefa 7. O risco que a §6 queria antecipar (uma premissa falsa sobre o bootstrap) foi reduzido de outro jeito: o script do one-shot foi executado ao vivo ao escrever este plano, inclusive os ramos de falha.
- **O client de device flow do fixture nasce sem scopes declarados** (Tarefa 4), herdando os defaults do realm, e não "com o scope `roles`", como a §4.6 da spec descreve. É o que faz dele a testemunha de que `gateway-api` não é default do realm (§5.1 da spec): um client com scopes escolhidos à mão não provaria nada sobre os defaults. O token dele carrega `profile` e `email`, e por isso só é usado contra a Account API e no teste da audiência.
- **A v2.7 não nomeia a "fatia E"** (Tarefa 12): onde a §9 da spec escreve "na fatia E", o texto da v2.7 diz "chega com a auditoria". A própria spec se contradiz — a decisão D-l manda a v2.7 não nomear as fatias seguintes —, e o plano segue a D-l. Fica para o autor confirmar.
- **D2 — o `tenantId` da rota em qualquer formato de GUID, o claim só no formato `D`** (Tarefa 13). A §4.3 da spec diz formato `D` nos dois; a §5.2 tem como controle a rota no formato `N` respondendo `200`. O plano segue a §5.2 e deixa a divergência para o autor nos dois handoffs.
- **D2 — o claim `tenant_id` é conferido pelo tamanho antes do parse** (Tarefa 13), pelo mesmo motivo do `sub`: `Guid.TryParseExact(…, "D")` aceita espaço nas pontas. Visto no protótipo.
- **D2 — `Policies.DeTenant`** (Tarefa 13): a lista das policies que decidem pelo tenant da rota, lida pelo teste de subida. A spec fala do teste, não de onde ele tira a lista.
- **D2 — o `TenantDetailsView` fica em `ITenantQueries.cs`**, ao lado do `TenantProvisioningView`, e não num arquivo próprio; e a resposta ganha o record `TenantPlanResponse` para o objeto `plan`.
- **D2 — sem teste de "a consulta não rastreia"** (Tarefa 14): numa projeção de um campo, o teste fica verde com ou sem `AsNoTracking()`. Visto no protótipo.
- **D2 — o teste funcional da ordem dos handlers troca a `IMemberQueries` por uma porta que conta** (Tarefa 15), por `WithWebHostBuilder`: a terceira exceção declarada à regra "só configuração", restrita àquela classe.
- **D2 — a regra do e-mail fora da leitura ganha uma classe própria**, `RegrasDeLeituraTests`: os tipos são da Application, e não cabem em `RegrasDeDominioTests`.
- **D2 — na coleção com Keycloak real, as três afirmações da jornada do admin convidado ficam num teste só** (Tarefa 16): o cenário custa dois convites, três device flows e um provisionamento. O ataque do grupo é um teste à parte.
- **D2 — o projeto funcional passa a referenciar a Application** (Tarefa 14): os testes usam a `IMemberQueries` no código, e o `.csproj` pede referência explícita do que é usado.

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
- Create `Common/Abstractions/IMemberQueries.cs`, `Tenants/GetTenant/GetTenantQuery.cs`, `GetTenantHandler.cs`, `TenantDetailsResponse.cs`; modify `Common/Abstractions/ITenantQueries.cs` (o método `GetDetailsAsync` e o record `TenantDetailsView`).

**Api**
- Create `Authentication/ValidacaoDoAccessToken.cs`, `Authentication/AutenticacaoLogs.cs`, `Authentication/AvisoDeChavesIndisponiveis.cs` (Tarefa 7); `Authentication/FormaDoAccessToken.cs`, `Authentication/AvisoDeClientsPermitidos.cs` (Tarefa 8).
- Create `Authorization/Policies.cs`, `Authorization/RespostasDeAutorizacao.cs`, `Authorization/ProblemDetailsDeAutorizacao.cs` (Tarefa 7); D2: `Authorization/RoleRequirement.cs`, `NotPlatformAdminRequirement.cs`, `SameTenantRequirement.cs`, `MemberRequirement.cs`, `MemberRequirementHandler.cs`, `AutorizacaoDaGateway.cs`.
- Modify `DependencyInjection.cs`, `Program.cs`, `Services/HttpCurrentUser.cs`, `Modules/TenantsModule.cs`, `appsettings.json`, `appsettings.Development.json`, `IdentityGateway.Api.csproj`.
- Delete `Security/JwtTokenService.cs`.

**Realm, compose, CI e ferramentas**
- Modify `keycloak/bootstrap/realm-identity-gateway.json`, `docker-compose.yml`, `.github/workflows/ci.yml`.
- Create `tools/jornada-compose.cs`.
- Modify `Directory.Packages.props` (`xunit.v3.extensibility.core`), `IdentityGateway.slnx`.

**Testes**
- Architecture: `RegrasDoRealmTests.cs`, `RegrasDoAmbienteLocalTests.cs`, `RegrasDaApiTests.cs`; create `RegrasDeFerramentasTests.cs`; D2: `RegrasDaApiTests.cs`, `RegrasDeDominioTests.cs`, create `RegrasDeLeituraTests.cs`.
- Integration: create `Identity/Keycloak/KeycloakFixtureExtensions.cs`, `RealmVivoTests.cs`, `HarnessDeLoginTests.cs`, `HarnessContraKeycloakTests.cs`, `FormaDoTokenContraKeycloakTests.cs`, `AccessTokenValidationOptionsTests.cs`; modify `GlobalUsings.cs`, `KeycloakRealTests.cs`, `KeycloakHealthCheckTests.cs`, `DependencyInjectionTests.cs`, `Provisioning/ComposicaoDoProvisionamento.cs`, o `.csproj`; delete `Identity/Keycloak/KeycloakFixture.cs`, `ChavesDeTeste.cs`, `RaizDoRepositorio.cs`. D2: create `Persistence/MemberQueriesTests.cs`, `Persistence/TenantDetailsTests.cs`.
- Functional: create `Oidc/OidcFalso.cs`, `Oidc/EmissorDeTeste.cs`, `Logs/ColetorDeLogsDaApi.cs`, `Logs/CapturaDeSpans.cs`, `ApiEmProducaoFactory.cs`, `EmissorEstritoTests.cs`, `AvisoDeChavesIndisponiveisTests.cs`, `AvisoDeChaveNoLogTests.cs`, `OpcoesDoJwtBearerTests.cs`, `LogsPorHostTests.cs`, `AutenticacaoNegativaTests.cs`, `FormaDoAccessTokenTests.cs`, `HostEmProducaoTests.cs`, `EndpointsDeclaramAutorizacaoTests.cs`, `ApiComKeycloakFactory.cs`, `ColecaoComKeycloak.cs`, `TokensDoKeycloakNaApiTests.cs`, `VazamentoDoEmailNoTokenTests.cs`; rewrite `IdentityGatewayApiFactory.cs`; modify `SegurancaTests.cs`, o `.csproj`. Application (D2): create `Tenants/GetTenant/GetTenantHandlerTests.cs`. D2: create `Autorizacao/MontagemDaAutorizacao.cs`, `Autorizacao/TenantAdminPolicyTests.cs`, `Autorizacao/PertencaFalsa.cs`, `Autorizacao/PertencaNaPolicyTenantAdminTests.cs`, `Autorizacao/OrdemDosHandlersTests.cs`, `Autorizacao/RespostaDaLeituraDeTenantTests.cs`, `LeituraDeTenantTests.cs`, `LeituraDeTenantComKeycloakTests.cs`; modify `EndpointsDeclaramAutorizacaoTests.cs`, `AutenticacaoNegativaTests.cs`, `ApiComKeycloakFactory.cs`, o `.csproj`.

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

Em `IdentityGateway.slnx`, dentro de `<Folder Name="/tests/">`, depois da linha do `IdentityGateway.Infrastructure.IntegrationTests` (mantenha o fim de linha que o arquivo tiver):

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
    [
        "fullScopeAllowed", "directAccessGrantsEnabled", "standardFlowEnabled", "defaultClientScopes",
        "optionalClientScopes",
    ];

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
```

3. Em `NenhumaChaveDeCredencial`, trocar a linha `.. Percorrer(Realm(), "$")` — a que vem logo antes de `.Where(item => ChavesProibidas.Any(chave =>`; o mesmo texto abre o array de outros três testes, e só este muda — por:

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

Em `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakRealTests.cs`, no teste `UsuarioComum_NaoAlteraOTenantIdPelaAccountApi`, trocar as três linhas

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

Run: `grep -n '"password"' tests/IdentityGateway.Testing.Keycloak/KeycloakFixture.cs`
Expected: só as linhas de `CriarClienteMasterAsync` (o `grant_type` e o campo `password` do `admin-cli` do master). Nenhuma outra: o fixture não obtém mais token de usuário por senha. (No `HarnessDeLogin.cs` o texto `"password"` também aparece — é o nome do campo do formulário de login do Keycloak, que o harness preenche.)

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
        // Filtrado fora da asserção: um NotContain com lambda recebe árvore de expressão, que não aceita o descarte do out.
        ClaimsQueNaoPodemSair.Where(claim => payload.TryGetProperty(claim, out _)).Should().BeEmpty();
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

        // Comparado fora da asserção: um NotBe que falhasse imprimiria os dois refresh tokens na mensagem.
        (renovado.RefreshToken == primeiro.RefreshToken).Should().BeFalse("a renovação devolve um refresh token novo");
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
- Create: `src/IdentityGateway.Api/Authentication/ValidacaoDoAccessToken.cs`, `Authentication/AutenticacaoLogs.cs`, `Authentication/AvisoDeChavesIndisponiveis.cs`
- Create: `src/IdentityGateway.Api/Authorization/Policies.cs`, `Authorization/RespostasDeAutorizacao.cs`, `Authorization/ProblemDetailsDeAutorizacao.cs`
- Modify: `src/IdentityGateway.Api/DependencyInjection.cs`, `Program.cs`, `Services/HttpCurrentUser.cs`, `Modules/TenantsModule.cs`, `appsettings.json`, `IdentityGateway.Api.csproj`
- Delete: `src/IdentityGateway.Api/Security/JwtTokenService.cs`, `src/IdentityGateway.Infrastructure/Configuration/JwtOptions.cs`
- Modify: `src/IdentityGateway.Infrastructure/DependencyInjection.cs` (sai o registro de `JwtOptions`), `Directory.Packages.props` (comentário)
- Create (testes funcionais): `Oidc/EmissorDeTeste.cs`, `Oidc/OidcFalso.cs`, `Logs/ColetorDeLogsDaApi.cs`, `EmissorEstritoTests.cs`, `AvisoDeChavesIndisponiveisTests.cs`, `AvisoDeChaveNoLogTests.cs`, `OpcoesDoJwtBearerTests.cs`, `LogsPorHostTests.cs`, `AutenticacaoNegativaTests.cs`, `EndpointsDeclaramAutorizacaoTests.cs`
- Rewrite: `tests/IdentityGateway.Api.FunctionalTests/IdentityGatewayApiFactory.cs`
- Modify: `tests/IdentityGateway.Api.FunctionalTests/SegurancaTests.cs`, `IdentityGateway.Api.FunctionalTests.csproj`
- Modify: `tests/IdentityGateway.ArchitectureTests/RegrasDaApiTests.cs`
- Modify (limpeza das chaves `Jwt:*`): `tests/IdentityGateway.Infrastructure.IntegrationTests/DependencyInjectionTests.cs`, `Provisioning/ComposicaoDoProvisionamento.cs`, `Identity/Keycloak/KeycloakHealthCheckTests.cs`

**Interfaces:**
- Consome: `AccessTokenValidationOptions` (Tarefa 6): `Issuer`, `MetadataAddress`, `RequireHttpsMetadata`, `Audience`, `AllowedClients`; `ICorrelationIdProvider.CorrelationId` (existente).
- Produz, na Api (`internal`, visíveis ao projeto funcional por `InternalsVisibleTo`):
  - `static class ValidacaoDoAccessToken` (namespace `IdentityGateway.Api.Authentication`): `const string Categoria = "IdentityGateway.Api.Authentication"`; `static readonly TimeSpan Tolerancia`, `PrazoDosMetadados`, `IntervaloDeRefresh`; `static void Configurar(JwtBearerOptions jwt, AccessTokenValidationOptions validacao)`; `static IssuerValidator EmissorEstrito(string esperado)`; `static bool EhFalhaDeChaveOuDeMetadados(Exception excecao)`.
  - `sealed class AvisoDeChavesIndisponiveis` (serviço, um por host): `bool PodeAvisar()` — verdadeiro uma vez a cada `IntervaloDeRefresh`.
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
    /// Era por ele que o serviço de emissão do template gerava token. O que a Api precisa de JWT hoje é ler um token já
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
Expected: FAIL em `NenhumaCamadaDeProducaoUsaChaveSimetrica` (IdentityGateway.Api: `DependencyInjection` e `JwtTokenService`) e em `Api_NaoUsaOPacoteJwtLegado` (ao menos `JwtTokenService`; o `DependencyInjection` só usa do pacote uma constante, que não deixa dependência no binário). É o vermelho que prova que as duas regras enxergam o que proíbem. As outras duas passam: travam o que ainda não existe, e por isso só são vistas vermelhas por mutação (Passo 13, mutações 17 e 18).

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

`tests/IdentityGateway.Api.FunctionalTests/AvisoDeChavesIndisponiveisTests.cs` (sem fixture):

```csharp
using IdentityGateway.Api.Authentication;
using Microsoft.IdentityModel.Tokens;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// Quando a falha de autenticação vale um aviso, e quantas vezes.
/// </summary>
/// <remarks>
/// O aviso existe para o provedor de identidade fora do ar não virar <c>401</c> em silêncio. Dois erros o estragariam:
/// tratar qualquer recusa como "chaves indisponíveis" (o alerta deixaria de querer dizer alguma coisa), e avisar a cada
/// pedido (quem manda um token com <c>kid</c> inventado encheria o log sem se autenticar).
/// </remarks>
public sealed class AvisoDeChavesIndisponiveisTests
{
    [Theory]
    [InlineData(typeof(SecurityTokenSignatureKeyNotFoundException), true)]
    [InlineData(typeof(SecurityTokenExpiredException), false)]
    [InlineData(typeof(SecurityTokenInvalidAudienceException), false)]
    [InlineData(typeof(SecurityTokenInvalidIssuerException), false)]
    [InlineData(typeof(SecurityTokenInvalidSignatureException), false)]
    [InlineData(typeof(SecurityTokenMalformedException), false)]
    [InlineData(typeof(InvalidOperationException), false)]
    [InlineData(typeof(HttpRequestException), false)]
    [InlineData(typeof(TaskCanceledException), false)]
    public void SoAFaltaDeChave_EFalhaDeChave(Type tipoDaExcecao, bool esperado)
    {
        ArgumentNullException.ThrowIfNull(tipoDaExcecao);
        var excecao = (Exception)Activator.CreateInstance(tipoDaExcecao)!;

        ValidacaoDoAccessToken.EhFalhaDeChaveOuDeMetadados(excecao).Should().Be(esperado);
    }

    [Fact]
    public void PodeAvisar_UmaVezPorIntervalo()
    {
        AvisoDeChavesIndisponiveis aviso = new();
        long intervalo = (long)ValidacaoDoAccessToken.IntervaloDeRefresh.TotalMilliseconds;

        aviso.PodeAvisar(agoraEmMs: 1_000).Should().BeTrue("o primeiro avisa");
        aviso.PodeAvisar(agoraEmMs: 1_001).Should().BeFalse("o segundo, logo em seguida, não");
        aviso.PodeAvisar(agoraEmMs: 1_000 + intervalo - 1).Should().BeFalse("nem o último do intervalo");
        aviso.PodeAvisar(agoraEmMs: 1_000 + intervalo).Should().BeTrue("passado o intervalo, avisa de novo");
    }
}
```

`tests/IdentityGateway.Api.FunctionalTests/AvisoDeChaveNoLogTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using IdentityGateway.Api.FunctionalTests.Logs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Serilog.Events;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// O aviso de "chave de assinatura não encontrada", visto no log de um host de verdade.
/// </summary>
/// <remarks>
/// Um host derivado, com o próprio canal de log e o próprio limitador do aviso: a contagem não pode depender do que as
/// outras classes — que rodam em paralelo, cada uma com o seu host — já registraram.
/// </remarks>
public sealed class AvisoDeChaveNoLogTests(IdentityGatewayApiFactory factory) : IClassFixture<IdentityGatewayApiFactory>
{
    private static readonly string[] PlatformAdmin = ["platform-admin"];

    private static bool EhOAvisoDeChave(LogEvent evento) =>
        evento.Level == LogEventLevel.Warning
        && evento.MessageTemplate.Text.Contains("chave de assinatura não encontrada", StringComparison.Ordinal);

    private static async Task<HttpStatusCode> RegistrarComAsync(HttpClient client, string token, CancellationToken ct)
    {
        using HttpRequestMessage pedido = new(HttpMethod.Post, "/api/v1/tenants");
        pedido.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage resposta = await client.SendAsync(pedido, ct);

        return resposta.StatusCode;
    }

    [Fact]
    public async Task TokenDeKidDesconhecido_Responde401EAvisaUmaVezSo_ERecusaComumNaoAvisa()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string canal = Guid.NewGuid().ToString("N");
        using WebApplicationFactory<Program> api = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Serilog:WriteTo:9:Args:canal", canal));
        using HttpClient client = api.CreateClient();
        ColetorDeLogsDaApi logs = ColetorDeLogsDaApi.DoCanal(canal);
        using var forasteira = RSA.Create(2048);

        // Uma recusa comum — audiência errada, assinatura certa — não é falta de chave, e não avisa.
        string comAudienciaErrada = factory.Emissor.Emitir(
            roles: PlatformAdmin, ajustar: payload => payload["aud"] = "account");
        (await RegistrarComAsync(client, comAudienciaErrada, ct)).Should().Be(HttpStatusCode.Unauthorized);
        logs.Eventos.Should().NotContain(evento => EhOAvisoDeChave(evento));

        // Três tokens de quem não tem a chave do realm, cada um com um kid inventado: qualquer pessoa consegue mandar
        // isto, sem se autenticar. 401 nos três, e um aviso só.
        for (int i = 0; i < 3; i++)
        {
            string forjado = factory.Emissor.Assinar(
                factory.Emissor.Payload(roles: PlatformAdmin), forasteira, kid: $"kid-inventado-{i}");

            (await RegistrarComAsync(client, forjado, ct)).Should().Be(HttpStatusCode.Unauthorized);
        }

        logs.Eventos.Count(EhOAvisoDeChave).Should().Be(1, "o aviso é limitado a um por intervalo");
    }
}
```

Um detalhe que o protótipo deste plano mostrou: um token assinado com a **chave certa** e um `kid` desconhecido é **aceito** — sem achar o `kid`, a biblioteca tenta todas as chaves do realm, e a assinatura confere. Não é brecha (quem assina com a chave do realm é o realm). O caso que importa é o de cima: `kid` desconhecido **e** chave de fora.

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

    [Fact]
    public void NinguemTransformaClaimsDepoisDaValidacao()
    {
        // A regra de arquitetura só vê as classes da Api. Esta vê o que o contêiner resolve, venha de onde vier: a
        // implementação que não faz nada, que é a que o AddAuthentication registra.
        using IServiceScope escopo = factory.Services.CreateScope();

        escopo.ServiceProvider.GetRequiredService<IClaimsTransformation>()
            .Should().BeOfType<NoopClaimsTransformation>("os claims vêm só do token (ADR-004)");
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
        resposta.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

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
        resposta.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

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
            resposta.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
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

        // Warning uma vez por intervalo; no resto, Debug. Um token com kid inventado também cai aqui, sem autenticação
        // e sem consumir cota: sem o limite, um laço de pedidos encheria o log de avisos.
        if (EhFalhaDeChaveOuDeMetadados(contexto.Exception)
            && contexto.HttpContext.RequestServices.GetRequiredService<AvisoDeChavesIndisponiveis>().PodeAvisar())
        {
            AutenticacaoLogs.ChavesIndisponiveis(logger, tipo);
        }
        else
        {
            AutenticacaoLogs.TokenRecusado(logger, tipo);
        }

        return Task.CompletedTask;
    }

    // Chave de assinatura não encontrada: kid desconhecido, ou nenhuma chave porque os metadados não vieram. A falha
    // da busca em si nunca chega aqui — a biblioteca a engole e reprova o token por falta de chave. Por isso a lista
    // tem um tipo só: incluir InvalidOperationException, por exemplo, rotularia qualquer defeito interno como
    // "chaves indisponíveis".
    internal static bool EhFalhaDeChaveOuDeMetadados(Exception excecao) =>
        excecao is SecurityTokenSignatureKeyNotFoundException;
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
        Message = "Autenticação: token recusado por chave de assinatura não encontrada ({Tipo}). É o sintoma de chaves "
                  + "ou metadados do provedor de identidade indisponíveis, ou de uma troca de chaves; um token com kid "
                  + "desconhecido também o provoca. No máximo um aviso destes a cada 30 s; os demais saem em Debug")]
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

`src/IdentityGateway.Api/Authentication/AvisoDeChavesIndisponiveis.cs`:

```csharp
namespace IdentityGateway.Api.Authentication;

/// <summary>
/// Limita o aviso de "chave de assinatura não encontrada" a um por intervalo.
/// </summary>
/// <remarks>
/// <para>
/// <b>Por que limitar.</b> A falta de chave é o sintoma do provedor de identidade fora do ar, e por isso vale um
/// <c>Warning</c>. Mas qualquer pessoa a provoca sem se autenticar, com um token de <c>kid</c> inventado — e o
/// <c>401</c> não consome cota do limitador de requisições. Sem limite, um laço de pedidos encheria o log de avisos e
/// esconderia o alerta de verdade.
/// </para>
/// <para>
/// <b>Um por instância da Api, e não por processo:</b> é um serviço, e não um campo estático. Nos testes há vários
/// hosts no mesmo processo, e o aviso de um não pode calar o de outro.
/// </para>
/// </remarks>
internal sealed class AvisoDeChavesIndisponiveis
{
    private long _proximoEmMs = long.MinValue;

    /// <summary>Verdadeiro na primeira chamada de cada intervalo; falso nas demais.</summary>
    public bool PodeAvisar() => PodeAvisar(Environment.TickCount64);

    /// <summary>A mesma decisão, com o relógio dado por quem chama — para o teste.</summary>
    internal bool PodeAvisar(long agoraEmMs)
    {
        long proximo = Interlocked.Read(ref _proximoEmMs);
        long seguinte = agoraEmMs + (long)ValidacaoDoAccessToken.IntervaloDeRefresh.TotalMilliseconds;

        // CompareExchange: de dois pedidos simultâneos, só um ganha o intervalo.
        return agoraEmMs >= proximo && Interlocked.CompareExchange(ref _proximoEmMs, seguinte, proximo) == proximo;
    }
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

        // Um por instância da Api: limita o aviso de "chave de assinatura não encontrada" a um por intervalo.
        services.AddSingleton<AvisoDeChavesIndisponiveis>();

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
| 14a | Idem, uma de cada vez, com as outras três formas de fixar a chave: `IssuerSigningKeys = [new RsaSecurityKey(RSA.Create(2048))],`; `SignatureValidator = (token, _) => new JsonWebToken(token),`; `IssuerSigningKeyResolver = (_, _, _, _) => [],` | `AsChaves_VemSoDosMetadados`, nas três |
| 17 | Em `AddApiServices`, acrescentar como primeira linha `Microsoft.IdentityModel.Logging.IdentityModelEventSource.ShowPII = true;` | `dotnet test tests/IdentityGateway.ArchitectureTests --filter-class "*RegrasDaApiTests"`: `NenhumaCamadaLigaPiiDaBibliotecaDeIdentidade` |
| 18 | Criar `src/IdentityGateway.Api/Services/Mutacao.cs` com `internal sealed class Mutacao : IClaimsTransformation { public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal) => Task.FromResult(principal); }` e registrá-la em `AddAutenticacao` (`services.AddTransient<IClaimsTransformation, Mutacao>();`); apagar o arquivo depois | Arquitetura: `Api_NaoTransformaClaims`. Funcional: `NinguemTransformaClaimsDepoisDaValidacao` |
| 19 | Em `AddAutenticacao`, um segundo esquema: `.AddJwtBearer().AddJwtBearer("Outro", _ => { });` | `HaUmEsquemaDeAutenticacaoSo` ("contains 1 item(s) too many") |
| 20 | Em `AoFalhar`, tirar o limite: apagar a linha `&& contexto.HttpContext.RequestServices.GetRequiredService<AvisoDeChavesIndisponiveis>().PodeAvisar())` e fechar o parêntese na linha de cima | `TokenDeKidDesconhecido_Responde401EAvisaUmaVezSo_ERecusaComumNaoAvisa` ("to be 1, but found 3") |
| 21 | Em `EhFalhaDeChaveOuDeMetadados`, devolver `true` sempre | 8 dos 9 casos de `SoAFaltaDeChave_EFalhaDeChave`, e `TokenDeKidDesconhecido_…` (a audiência errada passa a avisar) |

As mutações 17, 18 e 19 cobrem os testes que nascem verdes nesta tarefa (travam o que ainda não existe): sem elas, nenhum teria sido visto vermelho. Elas e as mutações 20 e 21 foram vistas vermelhas ao escrever este plano.

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

    private static TheoryDataRow<Func<IdentityGatewayApiFactory, Dictionary<string, object?>, string>, HttpStatusCode> Caso(
        string rotulo, HttpStatusCode esperado, Func<IdentityGatewayApiFactory, Dictionary<string, object?>, string> assinar) =>
        new(assinar, esperado) { Label = rotulo };

    /// <summary>
    /// Cada caso recebe o payload já com o e-mail e decide como estragá-lo e assiná-lo.
    /// </summary>
    /// <remarks>
    /// O primeiro parâmetro é a factory, e não o emissor: o emissor de teste é <c>internal</c>, e um membro público de
    /// uma classe de teste pública não pode expor tipo interno na assinatura.
    /// </remarks>
    public static TheoryData<Func<IdentityGatewayApiFactory, Dictionary<string, object?>, string>, HttpStatusCode> Casos => new()
    {
        // 404: autenticado e autorizado; o tenant da rota não existe.
        Caso("sucesso", HttpStatusCode.NotFound, (alvo, payload) => alvo.Emissor.Assinar(payload)),
        Caso("sem o papel (403)", HttpStatusCode.Forbidden, (alvo, payload) =>
        {
            payload.Remove("roles");
            return alvo.Emissor.Assinar(payload);
        }),
        Caso("vencido", HttpStatusCode.Unauthorized, (alvo, payload) =>
        {
            payload["exp"] = Agora - 120;
            return alvo.Emissor.Assinar(payload);
        }),
        Caso("audiência errada", HttpStatusCode.Unauthorized, (alvo, payload) =>
        {
            payload["aud"] = "account";
            return alvo.Emissor.Assinar(payload);
        }),
        Caso("assinatura inválida", HttpStatusCode.Unauthorized, (alvo, payload) =>
            alvo.Emissor.Assinar(payload, ChaveForasteira)),
        Caso("azp fora da lista", HttpStatusCode.Unauthorized, (alvo, payload) =>
        {
            payload["azp"] = "outro-client";
            return alvo.Emissor.Assinar(payload);
        }),
    };

    [Theory]
    [MemberData(nameof(Casos))]
    public async Task TokenComEmail_NaoDeixaOEmailEmRespostaLogNemTrace(
        Func<IdentityGatewayApiFactory, Dictionary<string, object?>, string> assinar, HttpStatusCode esperado)
    {
        ArgumentNullException.ThrowIfNull(assinar);
        CancellationToken ct = TestContext.Current.CancellationToken;
        string email = $"segredo{Guid.NewGuid():N}@acme.test";
        string rota = $"/api/v1/tenants/{Guid.NewGuid()}/provisioning";

        Dictionary<string, object?> payload = _factory.Emissor.Payload(roles: PlatformAdmin);
        payload["email"] = email;
        payload["preferred_username"] = email;
        payload["name"] = email;
        string token = assinar(_factory, payload);

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

        // O conjunto EXATO das portas publicadas, lido de toda lista `ports:`. Uma porta nova reprova, e uma destas
        // sem o 127.0.0.1 também — com aspas, sem aspas ou em qualquer outra forma. Publicar uma porta a mais passa a
        // ser uma decisão que mexe neste teste.
        PortasPublicadas(compose).Should().BeEquivalentTo(PortasEmLocalhost);
        compose.Should().NotContain(":1025\"", "o SMTP do mailpit só existe na rede do compose");
```

e acrescentar, junto dos outros membros privados (o campo no topo da classe, o método e a regex no fim):

```csharp
    private static readonly string[] PortasEmLocalhost =
    [
        "127.0.0.1:8080:8080", "127.0.0.1:8081:8080", "127.0.0.1:8025:8025", "127.0.0.1:5432:5432",
        "127.0.0.1:6379:6379", "127.0.0.1:5341:80", "127.0.0.1:16686:16686", "127.0.0.1:4317:4317",
    ];
```

```csharp
    /// <summary>
    /// Todo item de toda lista <c>ports:</c> do compose, sem as aspas e sem o comentário do fim da linha.
    /// </summary>
    /// <remarks>
    /// Lê as listas, e não um padrão de porta: <c>- "8080"</c>, <c>- '8080:8080'</c>, <c>- "[::]:8080:8080"</c>, a forma
    /// longa (<c>- target: 80</c>) e a lista numa linha só (<c>ports: [...]</c>) também são itens — e nenhum deles é
    /// igual a uma das portas esperadas.
    /// </remarks>
    private static List<string> PortasPublicadas(string compose)
    {
        List<string> portas = [];
        bool emPortas = false;

        foreach (string linha in compose.Split('\n'))
        {
            string texto = linha.Trim();

            if (texto.StartsWith("ports:", StringComparison.Ordinal))
            {
                emPortas = true;
                string naMesmaLinha = texto["ports:".Length..].Trim();

                if (naMesmaLinha.Length > 0 && !naMesmaLinha.StartsWith('#'))
                {
                    portas.Add(naMesmaLinha);
                }
            }
            else if (emPortas && texto.StartsWith("- ", StringComparison.Ordinal))
            {
                portas.Add(ItemDeLista().Match(texto).Groups["valor"].Value);
            }
            else if (emPortas && texto.Length > 0 && !texto.StartsWith('#'))
            {
                emPortas = false;
            }
        }

        return portas;
    }

    [GeneratedRegex(@"^-\s*[""']?(?<valor>[^""'#\s]*)")]
    private static partial Regex ItemDeLista();
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
        CamposComAttributes().IsMatch(compose).Should().BeFalse(
            "com --fields (attributes sozinho ou numa lista, como id,attributes), o objeto vem vazio e as travas ficam vacuosas");
    }
```

```csharp
    // Dentro do serviço api, e dentro do depends_on dele: os dois trechos "qualquer coisa" param na primeira linha
    // que abre outro serviço (dois espaços) ou outra chave do serviço (quatro). Sem isso, a dependência declarada num
    // serviço que viesse depois da api satisfaria a regra.
    [GeneratedRegex(
        @"^  api:\s*$(?:(?!^  \S).)*?^    depends_on:\s*$(?:(?!^    \S).)*?^      platform-admin-invite:\s*\n\s+condition:\s*service_completed_successfully",
        RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex DependenciaDaApiNoConvite();

    [GeneratedRegex(@"--fields\s+\S*attributes")]
    private static partial Regex CamposComAttributes();

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

        # Cada leitura vai primeiro para uma variável, com o próprio "|| falhar": num pipe, o status é o do último
        # comando, e uma leitura que falhasse viraria "não achei" — marcador vazio, e um segundo convite enviado.

        # 1. O realm é o desta versão?
        scopes="$$(kc get client-scopes -r "$$realm" --fields name)" \
          || falhar "não foi possível listar os client scopes do realm"
        printf '%s\n' "$$scopes" | grep -q '"name" : "gateway-api"' \
          || falhar "realm anterior aos tokens do Keycloak (o import é IGNORE_EXISTING). Rode: docker compose down -v"

        # 2. O marcador (sem --fields: com ele, attributes viria vazio).
        dados_do_realm="$$(kc get "realms/$$realm")" || falhar "não foi possível ler o realm"
        marcador="$$(printf '%s\n' "$$dados_do_realm" | sed -n 's/^ *"platformAdminInviteSentAt" : "\([^"]*\)".*$$/\1/p' | head -n 1)"
        if [ -n "$$marcador" ] && [ "$${REENVIAR:-0}" != "1" ]; then
          echo "platform-admin-invite: convite já enviado em $$marcador; nada a fazer"
          exit 0
        fi

        # 3. Exatamente um usuário com o e-mail, e com a forma do bootstrap.
        achados="$$(kc get users -r "$$realm" -q "email=$$email" -q exact=true --fields id)" \
          || falhar "não foi possível procurar a conta do platform-admin"
        ids="$$(printf '%s\n' "$$achados" | sed -n 's/^ *"id" : "\([^"]*\)".*$$/\1/p')"
        [ "$$(printf '%s\n' "$$ids" | grep -c .)" = "1" ] || falhar "esperado exatamente um usuário com o e-mail do platform-admin"
        id="$$ids"
        usuario="$$(kc get "users/$$id" -r "$$realm")" || falhar "não foi possível ler a conta do platform-admin"
        papeis="$$(kc get "users/$$id/role-mappings" -r "$$realm")" || falhar "não foi possível ler os papéis da conta"
        grupos="$$(kc get "users/$$id/groups" -r "$$realm")" || falhar "não foi possível ler os grupos da conta"
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
# --progress quiet cala o progresso do compose sem calar o stderr do serviço, que é onde saem as mensagens de falha.
convite() { docker compose --progress quiet -p igverif run --rm -T --no-deps "$@" platform-admin-invite; echo "exit=$?"; }

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
ADMIN=http://127.0.0.1:8081/admin/realms/identity-gateway
J="Content-Type: application/json"

# O access token do master vale 60 segundos, e cada execução do one-shot leva uns 8: o token é obtido de novo antes
# de cada preparo e de cada limpeza. Com um token só, as últimas chamadas falhariam em silêncio — a trava não seria
# montada, e o one-shot ENVIARIA.
entrar() {
  TOKEN=$(curl -fsS -d grant_type=password -d client_id=admin-cli -d username=admin --data-urlencode "password=$SENHA" http://127.0.0.1:8081/realms/master/protocol/openid-connect/token | jq -r .access_token | tr -d '\r')
  H="Authorization: Bearer $TOKEN"
}
feito() { echo "FALHOU: $1 — pare e confira antes de seguir"; }

entrar
ID=$(curl -fsS -H "$H" "$ADMIN/users?email=platform-admin@identity-gateway.local&exact=true" | jq -r '.[0].id' | tr -d '\r')

# 1. um papel de realm a mais
entrar
curl -fsS -H "$H" "$ADMIN/roles/tenant-admin" | jq -c '[{id, name}]' > .papel.json
curl -fsS -X POST -H "$H" -H "$J" --data-binary @.papel.json "$ADMIN/users/$ID/role-mappings/realm" || feito "preparo da trava 1"
convite -e REENVIAR=1
entrar
curl -fsS -X DELETE -H "$H" -H "$J" --data-binary @.papel.json "$ADMIN/users/$ID/role-mappings/realm" || feito "limpeza da trava 1"

# 2. o atributo tenant_id
entrar
curl -fsS -H "$H" "$ADMIN/users/$ID" | jq -c '.attributes = {"tenant_id": ["0199a000-0000-7000-8000-000000000001"]}' > .usuario.json
curl -fsS -X PUT -H "$H" -H "$J" --data-binary @.usuario.json "$ADMIN/users/$ID" || feito "preparo da trava 2"
convite -e REENVIAR=1
entrar
curl -fsS -H "$H" "$ADMIN/users/$ID" | jq -c '.attributes = {}' > .usuario.json
curl -fsS -X PUT -H "$H" -H "$J" --data-binary @.usuario.json "$ADMIN/users/$ID" || feito "limpeza da trava 2"

# 3. um grupo
entrar
GRUPO=$(curl -fsS -i -X POST -H "$H" -H "$J" -d '{"name":"g-verif"}' "$ADMIN/groups" | grep -i '^location:' | sed 's|.*/||' | tr -d '\r')
curl -fsS -X PUT -H "$H" "$ADMIN/users/$ID/groups/$GRUPO" || feito "preparo da trava 3"
convite -e REENVIAR=1
entrar
curl -fsS -X DELETE -H "$H" "$ADMIN/groups/$GRUPO" || feito "limpeza da trava 3"

# 4. um papel de client
entrar
CONTA=$(curl -fsS -H "$H" "$ADMIN/clients?clientId=account" | jq -r '.[0].id' | tr -d '\r')
curl -fsS -H "$H" "$ADMIN/clients/$CONTA/roles/view-profile" | jq -c '[{id, name}]' > .papel.json
curl -fsS -X POST -H "$H" -H "$J" --data-binary @.papel.json "$ADMIN/users/$ID/role-mappings/clients/$CONTA" || feito "preparo da trava 4"
convite -e REENVIAR=1
entrar
curl -fsS -X DELETE -H "$H" -H "$J" --data-binary @.papel.json "$ADMIN/users/$ID/role-mappings/clients/$CONTA" || feito "limpeza da trava 4"

echo "e-mails: $(contar)"
```

Expected: nenhuma linha `FALHOU`; em cada um dos quatro `convite -e REENVIAR=1`, `platform-admin-invite: a conta do platform-admin não tem a forma do bootstrap` e `exit=1`. No fim, `e-mails: 1`: nenhuma das quatro tentativas enviou. (Cuidado ao limpar o `tenant_id`: um `PUT` sem a chave `attributes` **mantém** os atributos; é preciso mandar `"attributes": {}`.) A trava 4 sai pela comparação dos nomes dos papéis, que inclui os de client; a checagem de `clientMappings` do script é uma segunda defesa, e não é ela que dispara aqui.

**Os três ramos que param o one-shot antes de qualquer envio** (executados ao escrever este plano, com estas mensagens):

```bash
# a. convite já concluído: o reenvio é recusado. Um link novo trocaria a senha de uma conta que já está em uso.
entrar
curl -fsS -H "$H" "$ADMIN/users/$ID" | jq -c '.requiredActions = []' > .usuario.json
curl -fsS -X PUT -H "$H" -H "$J" --data-binary @.usuario.json "$ADMIN/users/$ID" || feito "preparo do ramo a"
convite -e REENVIAR=1
entrar
curl -fsS -H "$H" "$ADMIN/users/$ID" | jq -c '.requiredActions = ["UPDATE_PASSWORD","VERIFY_EMAIL"]' > .usuario.json
curl -fsS -X PUT -H "$H" -H "$J" --data-binary @.usuario.json "$ADMIN/users/$ID" || feito "limpeza do ramo a"

# b. realm de antes desta fatia: simulado renomeando o scope gateway-api, que é o que o one-shot procura.
entrar
SCOPE=$(curl -fsS -H "$H" "$ADMIN/client-scopes" | jq -r '.[] | select(.name == "gateway-api") | .id' | tr -d '\r')
curl -fsS -H "$H" "$ADMIN/client-scopes/$SCOPE" | jq -c '.name = "gateway-api-antigo"' > .scope.json
curl -fsS -X PUT -H "$H" -H "$J" --data-binary @.scope.json "$ADMIN/client-scopes/$SCOPE" || feito "preparo do ramo b"
convite
entrar
jq -c '.name = "gateway-api"' .scope.json > .scope-de-volta.json
curl -fsS -X PUT -H "$H" -H "$J" --data-binary @.scope-de-volta.json "$ADMIN/client-scopes/$SCOPE" || feito "limpeza do ramo b"

# c. senha do master recusada: um arquivo com outra senha montado por cima do que o one-shot lê.
printf 'senha-errada' > .senha-errada
docker compose --progress quiet -p igverif run --rm -T --no-deps -v "$PWD/.senha-errada:/keys/admin-password:ro" platform-admin-invite; echo "exit=$?"

rm -f .papel.json .usuario.json .scope.json .scope-de-volta.json .senha-errada
convite
echo "e-mails: $(contar)"
```

Expected: nenhuma linha `FALHOU`; em (a), `o convite já foi concluído; não há o que reenviar` e `exit=1`; em (b), `realm anterior aos tokens do Keycloak (o import é IGNORE_EXISTING). Rode: docker compose down -v` e `exit=1`; em (c), `credencial do master recusada: o admin do compose foi alterado? Rode: docker compose down -v` e `exit=1`. O `convite` do fim confirma que tudo voltou ao lugar: `convite já enviado em <data>; nada a fazer`, `exit=0`, `e-mails: 1`.

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
| 2 | A porta do Jaeger sem aspas e sem IP: `- 4317:4317` | `DependenciasComDadoPessoalPublicamSoEmLocalhost` |
| 2a | **Uma porta a mais**, com as oito no lugar: acrescentar `- "9999:9999"` às portas da `api` | `DependenciasComDadoPessoalPublicamSoEmLocalhost` — é esta que prova que a regra lê as listas `ports:`, e não só procura as oito esperadas |
| 2b | Outras formas sem o IP: `- "8080"` na `api`; `- '8025:8025'` (aspas simples) no `mailpit` | `DependenciasComDadoPessoalPublicamSoEmLocalhost`, nas duas |
| 3 | Tirar o `platform-admin-invite` do `depends_on` da `api` | `ApiSoSobeDepoisDoConviteDoPlatformAdmin` |
| 3a | **A dependência no serviço errado:** tirar as duas linhas do `platform-admin-invite` do `depends_on` da `api` e pô-las no `depends_on` do `migrate` | `ApiSoSobeDepoisDoConviteDoPlatformAdmin` — a regra não aceita a dependência declarada num serviço que vem depois da `api` |
| 4 | Tirar a linha `test -n "$$PLATFORM_ADMIN_EMAIL" &&` | `EntrypointDoKeycloakRecusaEmailVazioOuComMaiusculas` |
| 5 | Padrão `Platform-Admin@identity-gateway.local` na âncora | `PadraoDoEmailDoPlatformAdminEMinusculo` |
| 6 | Trocar a tag da imagem do `platform-admin-invite` para `26.7.3` | `ComposeEFixtureUsamAMesmaTagDoKeycloak` |
| 7 | Acrescentar `Jwt__SigningKey: "x"` ao ambiente da `api` | `ComposeNaoTemMaisAChaveJwt` |
| 8 | Trocar `lifespan=14400` por `lifespan=86400` | `ConviteDoPlatformAdminNaoRecebeSenhaPorVariavelNemAtribuiPapel` |
| 9 | No script, ler o marcador com `kc get "realms/$$realm" --fields id,attributes` | `ConviteDoPlatformAdminNaoRecebeSenhaPorVariavelNemAtribuiPapel` — a regra pega `attributes` em qualquer posição da lista de `--fields` |
| 10 | Acrescentar `KC_CLI_PASSWORD: "x"` ao `environment` do `platform-admin-invite` | `ConviteDoPlatformAdminNaoRecebeSenhaPorVariavelNemAtribuiPapel` |

Todas foram aplicadas ao compose final e vistas vermelhas ao escrever este plano.

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
string fase = args.Length > 0 ? args[0] : string.Empty;

// O prazo de cada fase fica ABAIXO do timeout-minutes do passo que a roda no job. Se estourar, quem encerra é o app,
// dizendo a etapa e gravando a tabela do resumo — e não o runner, que mataria o processo sem dizer onde parou.
using CancellationTokenSource prazo = new(fase switch
{
    "convites" => TimeSpan.FromSeconds(50),
    "jornada" => TimeSpan.FromMinutes(4),
    "depois-de-voltar" => TimeSpan.FromMinutes(3),
    _ => TimeSpan.FromMinutes(1),
});
CancellationToken ct = prazo.Token;

using HttpClient http = new() { BaseAddress = api, Timeout = TimeSpan.FromSeconds(10) };
using ClienteDoMailpit mailpit = new(mailpitUrl);

try
{
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
catch (OperationCanceledException) when (prazo.IsCancellationRequested)
{
    return Falhar($"{etapaAtual}: o prazo da fase esgotou.", (int)FamiliaDeFalha.Prazo);
}
catch (OperationCanceledException)
{
    // Não foi o prazo da fase: foi uma chamada HTTP que não respondeu em 10 s — o HttpClient cancela a própria chamada.
    return Falhar($"{etapaAtual}: uma chamada HTTP ficou sem resposta por 10 s.", (int)FamiliaDeFalha.Prazo);
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

    // Logo depois do 202 todo tenant está Pending, com o Keycloak de pé ou parado: uma leitura só não provaria nada.
    // Quinze segundos cobrem a varredura do Outbox (5 s) e uma tentativa inteira de provisionar; com o Keycloak de pé,
    // o tenant já teria virado Active.
    Etapa("o tenant continua Pending por 15 s, enquanto o Keycloak não volta");
    await ExigirStatusEstavelAsync(localizacao, token, "Pending", TimeSpan.FromSeconds(15));

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

// Ao contrário de EsperarStatusAsync, que sai na primeira leitura igual à esperada: aqui TODA leitura do período
// precisa ser a esperada.
async Task ExigirStatusEstavelAsync(Uri localizacao, string token, string esperado, TimeSpan periodo)
{
    DateTimeOffset fim = DateTimeOffset.UtcNow + periodo;

    do
    {
        using HttpResponseMessage resposta = await ExigirAsync(
            HttpStatusCode.OK, HttpMethod.Get, localizacao.ToString(), token, corpo: null);
        JsonElement corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>(ct);
        string status = corpo.GetProperty("status").GetString() ?? "(nulo)";

        if (status != esperado)
        {
            throw new FalhaDoHarnessException(
                FamiliaDeFalha.Api, etapaAtual,
                $"o tenant passou a {status}; esperado {esperado} durante {periodo.TotalSeconds:0} s.");
        }

        await Task.Delay(TimeSpan.FromSeconds(3), ct);
    }
    while (DateTimeOffset.UtcNow < fim);
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
# Sem IG_ESTADO: o app grava o estado no diretório temporário do usuário, que no Git Bash é o /tmp. Um caminho do
# Git Bash numa variável (como o de mktemp) pode chegar ao .NET sem tradução no Windows.
unset IG_ESTADO
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

    steps:
      - uses: actions/checkout@v4

      # O que uma fase do app deixa para a seguinte: credenciais de um Keycloak descartável, apagadas no fim. Definido
      # num passo, e não no env do job: o contexto `runner` não existe no env do job, e usá-lo ali invalida o workflow
      # inteiro — nenhum job rodaria.
      - name: Definir o arquivo de estado da jornada
        run: echo "IG_ESTADO=$RUNNER_TEMP/ig-jornada-estado.json" >> "$GITHUB_ENV"

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

      # O convite do platform-admin concluído pelo link, o token pelo device flow, a receita simétrica antiga recusada, o
      # tenant registrado e provisionado, e o convite do admin do tenant no mailpit com um link que abre.
      - name: A jornada com token do Keycloak
        timeout-minutes: 5
        run: dotnet run --configuration Release tools/jornada-compose.cs -- jornada

      # A demonstração nº 1 do README, com prova automática: a api aceita o tenant com o Keycloak parado (as chaves
      # já estão em memória) e o provisiona quando ele volta. stop/start, e não pause: o stop recusa a conexão na
      # hora, e é a sequência que o README manda fazer. O refresh token precisa sobreviver ao reinício.
      - name: Registrar com o Keycloak parado e provisionar quando ele volta
        timeout-minutes: 10
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

# espere o Keycloak responder de novo (uns 30 s). Renovar antes disso devolveria uma resposta vazia, e as linhas de
# baixo apagariam o refresh token que você tem:
until curl -sf http://localhost:8081/realms/identity-gateway/.well-known/openid-configuration > /dev/null; do sleep 2; done

# renove — e guarde o refresh token novo:
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

Run: `rm -f /tmp/ig-jornada-estado.json`

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

### Tarefa 12: Especificação v2.7, documento de negócio 1.4, README, CONTRIBUTING e handoff da D1

Spec: §9 (a tabela "Onde / v2.6 / Mudança" e as erratas E1 a E8), §11 (entregáveis), §6 ("Documentos"), §8 (os
limites que a v2.7 registra), D-l (a v2.7 só com o que a fatia implementa, mais as erratas).

A D1 muda o mecanismo de autenticação inteiro, e a especificação, o documento de negócio e o README passam a
afirmar coisas falsas em dezenas de lugares. Esta tarefa as corrige num commit só, de documentação. A v2.7 descreve
o design inteiro da fatia, mas a `main` não pode descrever como existente uma rota que ainda não existe: o que só a
D2 entrega fica marcado, e a Tarefa 17 tira as marcas.

**Arquivos:**
- Create: `docs/especificacao-arquitetural-v2.7.md` (cópia da v2.6, editada abaixo)
- Modify: `docs/documentacao-negocio.md` (versão 1.4)
- Modify: `README.md` (fora das seções `### Keycloak` e `### Demonstração: …`, que são da Tarefa 11)
- Modify: `CONTRIBUTING.md`
- Create: `docs/superpowers/specs/AAAA-MM-DD-tokens-keycloak-d1-handoff.md` (data do dia da entrega, `date +%F`)

**Interfaces:**
- Consome: tudo (Tarefas 1–11).
- Produz: documentação.

**Regra desta tarefa:** todo trecho "a localizar" abaixo existe literalmente no arquivo de origem e aparece **uma
vez só** (conferido com `grep -cF` sobre a v2.6, o documento de negócio, o README e o CONTRIBUTING da branch, e
aplicando o roteiro inteiro, na ordem, a uma cópia dos quatro arquivos). Na v2.7, que nasce como cópia da v2.6, os
trechos são os mesmos, e as referências `v2.6:N` dão a linha **da v2.6** em que cada trecho começa — na v2.7 a linha
desloca conforme as edições avançam, e por isso o `Edit` casa pelo texto, nunca pelo número. Quando o roteiro diz
"a linha que começa com" ou "a linha que contém", o trecho mostrado identifica a linha; leia a linha inteira no
arquivo e use-a como `old_string`. Quando diz "até a linha que contém", a troca vai do começo da primeira linha ao
fim da última, inclusive. Se um `Edit` falhar por não achar o trecho, o arquivo foi alterado fora deste roteiro:
pare e confira, não improvise. Todo texto novo está completo; copie como está. Um texto novo que traz blocos de
código vem numa cerca de **quatro** crases: as cercas de três, dentro dela, fazem parte do texto. Os documentos
levam acento; a mensagem de commit, não.

**O que só a D2 entrega leva a marca `(D2, planejado)`, sempre com esse texto exato**, para a Tarefa 17 achar todas
com um `grep`. Não reescreva a marca ("planejado para a D2", "D-g; D2, planejado"): copie.

**A sequência de escape do "e comercial" nunca passa pelo `Edit` nem pelo `Write`.** Ela é formada por uma barra
invertida seguida de `u0026`, e as ferramentas de edição a decodificam em silêncio e gravam um `&` no lugar — foi o
que estragou a frase da v2.6 que a errata E1 corrige. Neste roteiro ela não aparece escrita em lugar nenhum: onde a
v2.7 precisa dela, o texto novo traz o marcador `%%E1%%`, e o Passo 21 troca o marcador pela sequência **por shell**.
Não troque o marcador à mão.

- [ ] **Passo 1: Criar a v2.7 a partir da v2.6**

Run: `cp docs/especificacao-arquitetural-v2.6.md docs/especificacao-arquitetural-v2.7.md`

Run: `grep -c "(D2, planejado)" docs/especificacao-arquitetural-v2.7.md; grep -c "u0026" docs/especificacao-arquitetural-v2.7.md`
Expected: `0` e `0` — a cópia ainda não tem nenhuma marca nem a sequência de escape.

Na v2.7, cabeçalho (v2.6:4) — localizar:

```markdown
**Versão 2.6** · Status: aprovada para implementação
```

e substituir por:

```markdown
**Versão 2.7** · Status: aprovada para implementação
```

Renumerar as seções 0.x existentes, **de baixo para cima** (os títulos levam as versões, então cada um é único):

| Título atual (localizar a linha inteira) | Título novo |
|---|---|
| `## 0.5. O que mudou da v2.0 para a v2.1` | `## 0.6. O que mudou da v2.0 para a v2.1` |
| `## 0.4. O que mudou da v2.1 para a v2.2` | `## 0.5. O que mudou da v2.1 para a v2.2` |
| `## 0.3. O que mudou da v2.2 para a v2.3` | `## 0.4. O que mudou da v2.2 para a v2.3` |
| `## 0.2. O que mudou da v2.3 para a v2.4` | `## 0.3. O que mudou da v2.3 para a v2.4` |
| `## 0.1. O que mudou da v2.4 para a v2.5` | `## 0.2. O que mudou da v2.4 para a v2.5` |
| `## 0. O que mudou da v2.5 para a v2.6` | `## 0.1. O que mudou da v2.5 para a v2.6` |

Nenhum texto da especificação cita essas seções pelo número (`grep -n "§0\.[0-9]" docs/especificacao-arquitetural-v2.6.md`
não acha nada), então a renumeração não deixa referência quebrada.

Depois, inserir a seção nova **antes** da linha que acabou de ser renumerada — localizar:

```markdown
## 0.1. O que mudou da v2.5 para a v2.6
```

e inserir **antes** dela o texto abaixo, que já termina com a linha `---` e uma linha em branco, como as demais
seções 0.x (o prefixo `T` das mudanças não colide com os nomes dos PRs, D1 e D2; o marcador `%%E1%%` da errata E1
fica como está até o Passo 21):

```markdown
## 0. O que mudou da v2.6 para a v2.7

Esta versão registra o que a **fatia D (tokens do Keycloak)** decidiu e verificou, lendo o código-fonte do Keycloak
na tag **26.7.4** e do ASP.NET Core (`Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.12 e
`Microsoft.IdentityModel` 8.19.2), e exercitando os dois ao vivo. Design da fatia:
[`2026-09-30-tokens-keycloak-design.md`](superpowers/specs/2026-09-30-tokens-keycloak-design.md).
**Nenhum ADR foi revogado:** o ADR-003 ganha um complemento, e entra o **ADR-011**.

A fatia é entregue em dois PRs. A **D1** leva o Keycloak de ponta a ponta até o `POST /tenants`; a **D2** leva a
primeira rota de tenant. Esta versão entra com a D1 e descreve o design inteiro: **o que só a D2 entrega está marcado
"(D2, planejado)"**, e a D2 tira as marcas. A v2.7 registra só o que a fatia implementa, mais as erratas: a sequência
das próximas fatias e as demais propostas do design não são norma.

| # | Mudança | Onde |
|---|---|---|
| T1 | **A API aceita só access tokens do Keycloak.** RS256, validado por metadados lidos pelo endereço interno, sem `Authority`; emissor aceito só o público, por igualdade, num `IssuerValidator` próprio, porque o `ValidIssuer` não restringe; audiência `identity-gateway-api`; `ClockSkew` de 30 s; `IncludeErrorDetails` falso em todo ambiente. O JWT simétrico do template deixa de existir | §5, §10.1, §11.8, §13 |
| T2 | **`AccessTokenValidationOptions`**, pública e neutra quanto ao provedor (seção `Keycloak:Auth`), preenchida pelo adaptador do Keycloak: a Api não conhece o Keycloak. O emissor aceito é a mesma propriedade que alimenta o `aud` do assertion | §7, §10.2, §11.8 |
| T3 | **`azp`, `typ` e `sub` conferidos na autenticação**, e não numa policy: lista de clients permitidos por ambiente, fora do `appsettings.json` base, com o client de demonstração recusado fora de Development; claim `typ` igual a `Bearer`; `sub` GUID | §10.1, §11.8 |
| T4 | **Keycloak fora com metadados frios responde `401`, com log**; o `503` fica registrado como alternativa. `BackchannelTimeout` de 5 s e `RefreshInterval` de 30 s | §11.8, §14, §19 |
| T5 | **`FallbackPolicy` autenticada**, com `AllowAnonymous` explícito nas rotas anônimas, e **Problem Details em `401` e `403`**, um só para cada status | §8, §10.1 |
| T6 | **O realm emite o token da §10.1.** Três client scopes fora dos defaults do realm — `gateway-roles`, `gateway-tenant` e `gateway-api`, este com o Audience Mapper —, o atributo `CreateDefaultClientScopes`, o catálogo de papéis completo e nunca composto, `offline_access` e `uma_authorization` fora do papel padrão, `defaultClientScopes` explícitos por client e nenhum grupo | §9.5, §12.1, §12.2, §15 |
| T7 | **Device Authorization Grant no client de demonstração** `identity-gateway-demo`, público e só do ambiente local. ROPC continua proibido, e o alcance da proibição fica dito. O token não carrega e-mail nem nome, e `NameClaimType` é `sub` | ADR-003, §2.1, §9.2, §10.3, §17 |
| T8 | **Access token de 300 s e rotação do refresh token** (`revokeRefreshToken`, `refreshTokenMaxReuse: 0`): reusar um refresh token derruba a sessão do client. `registrationAllowed` falso e `bruteForceProtected` | ADR-005, §10.3, §19 |
| T9 | **O primeiro platform-admin nasce no JSON do realm, sem senha, e é convidado por e-mail uma vez só**, pelo one-shot `platform-admin-invite`: link de 4 horas e marcador no realm gravado antes do envio. A `api` depende dele. Sai a senha gerada e impressa no log, que a v2.6 previa | §10.2, §15, §16, §19 |
| T10 | **Compose e CI:** `api` e Jaeger só em `127.0.0.1`; sai `Jwt__SigningKey`; o job `Compose` roda a jornada por um app C# de arquivo único (`tools/jornada-compose.cs`) — convite do platform-admin contado exato, device flow, a receita HS256 antiga com `401`, o convite do admin do tenant e o Keycloak parado com `stop`/`start`. Volume anterior exige `docker compose down -v` | §13, §15 |
| T11 | **Projeto de suporte de testes** `tests/IdentityGateway.Testing.Keycloak`, uma biblioteca: o fixture do Keycloak, o cliente do mailpit e o harness de login por device flow, o mesmo nos testes, na CI e na demonstração. O ROPC do fixture sai | §7, §12.1, §13 |
| T12 | **ADR-011: nas rotas de governança, a autorização da Gateway é token mais pertença no banco** (D2, planejado). Policy `TenantAdmin` = `tenant-admin` ∧ ¬`platform-admin` ∧ mesmo tenant ∧ `Member` em `{Invited, Active}`; leitura da pertença por `IMemberQueries`; a hierarquia de papéis é teto de atribuição, não herança de acesso | ADR-011, §6.3, §6.4, §10.1, §11.7, §11.9, §17 |
| T13 | **`GET /tenants/{tenantId}`, a primeira rota de tenant** (D2, planejado): o admin lê o próprio tenant; todo outro caso é `403`, inclusive tenant inexistente e o platform-admin, até existir a auditoria | §8, §16 |
| T14 | **O override do platform-admin passa a ser uma policy própria, `TenantReadAccess`**, entregue com a auditoria; o `Fail()` do `SameTenantRequirement` fica incondicional (errata E3) | §8, §10.1, §11.7 |
| T15 | Fatia D no andamento; pendências do M0 revistas; as duas demonstrações com passos no navegador | §16 |

**Erratas**, que corrigem texto de versões anteriores sem mudar decisão:

- **E1** (§15): a frase "o JSON escapa o `&` como `&`" tinha perdido a sequência de escape que cita. O certo é "o JSON escapa o `&` como `%%E1%%`".
- **E2** (§12.1): "o ROPC continua desabilitado também no realm de teste" era falso. O fixture de testes fazia ROPC num client criado em runtime, e o `admin-cli` embutido do realm tem direct grant. O fixture deixou de usar ROPC, e o ADR-003 passa a dizer o alcance da proibição.
- **E3** (§10.1, §11.7): o `PlatformAdminOverrideHandler` que "satisfaz o `SameTenantRequirement`" nunca funcionaria, porque o `Fail()` do requirement veta qualquer `Succeed`. O override vira policy própria (T14), e o "segundo handler" deixa de ser o motivo do `Fail()`.
- **E4** (§11.7): o `RegisterTenantCommand` do endpoint de referência estava sem o e-mail, desatualizado desde a v2.6.
- **E5** (§12.1): "a lista substitui o conjunto padrão do realm" estava errado. Declarar `clientScopes` no JSON desliga a criação dos scopes embutidos, e a correção é o atributo `CreateDefaultClientScopes`.
- **E6** (§12.1): os papéis padrão do realm "não interferem" — eles entravam no claim `roles`. Agora saem.
- **E7** (§9.5, §15): o bootstrap era descrito com os client scopes, o Audience Mapper, o armazenamento de eventos e a remoção do `offline_access`, que o JSON não tinha. Os scopes, o mapper e a remoção entram nesta versão; o armazenamento de eventos continua pendente.
- **E8** (§12.1, §12.2): os testes de referência chamavam métodos que não existem (`GetClientCredentialsTokenAsync`, `GetTokenForUserAsync`) e usavam a rota `/tenants/tenant-a/members`, com um slug no lugar do GUID.

---
```

- [ ] **Passo 2: §2.1 e §3 — o device flow na seta (2); as regras de isolamento continuam três**

Na v2.7, §2.1, no diagrama (v2.6:240) — localizar a linha dos rótulos das setas (os espaços contam: a marca `¹`
entra no lugar de um dos dois espaços antes da barra, e a largura da caixa não muda):

```text
       │     REST + Bearer JWT    │     PKCE / Client Cred.  │     Bearer JWT
```

e substituir por:

```text
       │     REST + Bearer JWT    │     PKCE / Client Cred.¹ │     Bearer JWT
```

Na tabela logo abaixo do diagrama (v2.6:256), localizar a linha da seta (2):

```markdown
| (2) | Authorization Code + PKCE (interativo) ou Client Credentials (M2M), direto no Keycloak |
```

e substituir por:

```markdown
| (2) | Authorization Code + PKCE (interativo) ou Client Credentials (M2M), direto no Keycloak. ¹ Só no ambiente local, o client de demonstração `identity-gateway-demo` usa o Device Authorization Grant (RFC 8628), também direto no Keycloak (v2.7, ADR-003) |
```

Na §3, princípio 5 (v2.6:274), localizar o fim da linha:

```markdown
e escopo de client M2M (§10.1, §18).
```

e substituir por:

```markdown
e escopo de client M2M (§10.1, §18). **Continuam três na v2.7:** nas rotas de governança da Gateway, a primeira é reforçada pela pertença do ator ao tenant no banco (ADR-011), que não é uma quarta regra — é a mesma regra, conferida em duas fontes, o token e o banco (D2, planejado).
```

- [ ] **Passo 3: §4 — complemento do ADR-003, ADR-005 e o ADR-011 novo**

Na v2.7, §4, ADR-003 (v2.6:298), localizar a linha que começa com:

```markdown
- **Consequências:** MFA e políticas do Keycloak valem para todos os fluxos
```

manter, e inserir logo depois dela, como mais dois itens da mesma lista:

```markdown
- **Fluxos permitidos (complemento da v2.7):** Authorization Code com PKCE para aplicações; Client Credentials para M2M; e **Device Authorization Grant (RFC 8628), no realm da aplicação, só no client `identity-gateway-demo`** — público, do ambiente local e fora do Terraform de produção. É o que deixa a demonstração obter um token de usuário pelo terminal com a senha digitada só na página do Keycloak. Nos testes, o `KeycloakFixture` cria em runtime um segundo client de device flow, sem o scope `gateway-api`, usado só para a Account REST API.
- **Alcance da proibição do ROPC (v2.7):** nenhum client declarado no JSON do realm tem direct grant, e uma regra do `RegrasDoRealmTests` exige isso. O `admin-cli` embutido do realm mantém o direct grant, com que o Keycloak o cria; o token que ele emite é leve, sem audiência e sem `sub`, e a Gateway o recusa — há teste com o Keycloak real (§13). A garantia é: **nenhum client do realm emite, por senha, um token aceito pela Gateway.** Ficam fora do ADR-003, e declarados: o `kcadm` do one-shot do compose e o Testcontainers, que usam a senha do admin do realm `master`, infraestrutura fora do realm da aplicação (§15). O harness dos testes e da CI submete o formulário de login do próprio Keycloak com uma senha que ele mesmo definiu pelo link de ações; a credencial nunca passa pela Gateway, e isso não é ROPC.
```

No ADR-005 (v2.6:310), localizar o fim do item "Consequências":

```markdown
o access token tem vida curta (5 minutos).
```

e substituir por:

```markdown
o access token tem vida curta: 5 minutos, configurados explicitamente no realm (`accessTokenLifespan: 300`, v2.7). O claim `roles` traz **só** o catálogo: o client scope `gateway-roles` limita o mapper aos quatro papéis, e os papéis padrão do realm ficam fora do token (§12.1, v2.7).
```

Inserir o ADR novo no fim da §4, depois do ADR-010 — localizar o título da seção seguinte (v2.6:350):

```markdown
## 5. Responsabilidades: Keycloak × IdentityGateway
```

e inserir **antes** dessa linha, separado dela por uma linha em branco:

```markdown
### ADR-011 — Nas rotas de governança, autorização é token mais pertença no banco

- **Estado:** decidido na v2.7; a implementação chega com a primeira rota de tenant (D2, planejado).
- **Contexto:** o `tenant_id` do token vem de um mapper de atributo de usuário (§12.2). Quando o usuário não tem o atributo, esse mapper recua para o atributo de mesmo nome do primeiro grupo que o tiver, subindo aos pais, e não há configuração que desligue o recuo. O papel `manage-users`, que o service account da Gateway tem (§10.2), cria grupos e mapeia neles qualquer papel que não seja de administração. Verificado ao vivo no Keycloak 26.7.4: com o token do service account, foi criado um grupo com `tenant_id` e com `tenant-admin` e `platform-admin` mapeados, e um usuário novo posto nesse grupo recebeu um token com os dois papéis e o `tenant_id` forjado.
- **Decisão:** nas rotas de governança de tenant, a Gateway não confia só no token. Além do papel e do `tenant_id` igual ao da rota, o `sub` precisa ser `Member` daquele tenant no banco da Gateway, em status `Invited` ou `Active`; todo outro estado nega, inclusive um que o enum ganhe depois. O realm não tem grupos, e os papéis do catálogo nunca são compostos; um teste confere as duas coisas no JSON do realm.
- **Limites, ditos por inteiro:**
  - **O Data Plane não tem a pertença.** As Resource APIs confiam no claim e na regra "nenhum grupo", que é conferida no JSON do bootstrap; um grupo criado em runtime não é visto. Decidir entre uma reconciliação que detecte grupos com `tenant_id` e um mapper que não recue é da fatia do Data Plane (§19).
  - **A pertença não contém quem tem a chave da Gateway.** Com o `manage-users`, ele troca a senha ou o e-mail de um `Member` real e passa com a conta dele. A pertença protege contra o recuo do mapper e contra a forja por grupo, que é silenciosa; tomar a conta de alguém é ruidoso, porque o dono perde o acesso. A trilha desse ataque só existe com os eventos de administração ligados, sem representação (§10.3).
- **Consequências:** a pertença é consultada só nas rotas de governança, e só para quem já passou nas camadas do token — nunca por requisição de negócio. **O ADR-002 continua valendo:** o Data Plane não chama a Gateway para autorizar. A consulta é a última camada da policy (§10.1, §11.7).
```

- [ ] **Passo 4: §5 e §6 — só RS256; a hierarquia é teto; a leitura da pertença com o tenant na assinatura**

Na v2.7, §5 (v2.6:355), localizar a linha da tabela que começa com:

```markdown
| Emissão de tokens | Assinatura dos JWT (RS256/ES256), refresh token rotation |
```

e substituir a linha inteira por:

```markdown
| Emissão de tokens | Assinatura dos JWT (RS256/ES256), refresh token rotation | Nenhuma. Provisiona os clients que solicitam tokens. Ao **validar** os tokens que recebe, aceita só o algoritmo do realm, RS256 (v2.7, §10.1) |
```

Na §6.3 (v2.6:437), no item da `RoleAssignmentPolicy`, localizar:

```markdown
A hierarquia é `platform-admin` > `tenant-admin` > `financial-manager` > `reader`.
```

e substituir por:

```markdown
A hierarquia é `platform-admin` > `tenant-admin` > `financial-manager` > `reader`. **Ela é o teto da atribuição, não herança de acesso** (v2.7): estar acima na hierarquia limita quais papéis o ator pode conceder, e não dá a ele o que o papel de baixo acessa. Um `platform-admin` não passa numa policy de `tenant-admin`, e a policy `TenantAdmin` nega quem acumula os dois papéis — separação de funções, §10.1 (D2, planejado).
```

Na §6.4 (v2.6:447), localizar o último parágrafo da seção, a linha que começa com:

```markdown
Um id que não pertence ao tenant da rota resulta em **404**
```

manter, e inserir logo depois, separado por uma linha em branco:

```markdown
**A leitura da pertença segue a mesma regra (v2.7) (D2, planejado).** A policy de tenant da Gateway confere se o ator é membro do tenant da rota (ADR-011) por uma porta de leitura com o tenant na assinatura: `IMemberQueries.GetStatusAsync(TenantId, ExternalUserId, CancellationToken)`, que devolve `MemberStatus?`. Não existe leitura de membro só pelo `sub`. É uma porta de consulta, no padrão `I*Queries` do repositório, e não um método novo do repositório do agregado: o `IMemberRepository` continua só com `Add` (§11.9).
```

- [ ] **Passo 5: §7 — as pastas novas da Api e da Infrastructure, o projeto de suporte e `tools/`**

A árvore da §7 nunca listou a pasta `Security` do template, e por isso não há o que tirar dela. São quatro
inserções na árvore e um parágrafo depois dela.

Na v2.7, §7, na árvore (v2.6:474), localizar a linha:

```text
│   │   ├── Identity/Keycloak/                # KeycloakAdminClient, KeycloakIdentityProvider
```

manter, e inserir logo depois, na linha seguinte:

```text
│   │   ├── Configuration/                    # Options validadas na subida; AccessTokenValidationOptions (v2.7)
```

Localizar a linha (v2.6:481):

```text
│   │   ├── Authorization/                    # Policies, SameTenantHandler
```

e substituir por estas duas:

```text
│   │   ├── Authentication/                   # Validação do access token: JwtBearer, azp, typ e sub (v2.7)
│   │   ├── Authorization/                    # Policies, requirements e o Problem Details de 401 e 403 (v2.7)
```

Localizar a última linha do bloco `tests/` (v2.6:495):

```text
│   └── IdentityGateway.ArchitectureTests/
```

e inserir **antes** dessa linha:

```text
│   ├── IdentityGateway.Testing.Keycloak/     # Suporte (biblioteca): fixture, mailpit e harness de login (v2.7)
```

Localizar a linha (v2.6:501):

```text
├── docs/adr/
```

e inserir **antes** dela estas três linhas (a terceira é a linha de separação, só com a barra vertical):

```text
├── tools/
│   └── jornada-compose.cs                    # App de arquivo único: a jornada do compose na CI (v2.7)
│
```

Depois da árvore, localizar a linha (v2.6:505):

```markdown
**Regras de dependência** (verificadas por testes de arquitetura):
```

e inserir **antes** dessa linha, separado dela por uma linha em branco:

```markdown
**O que a v2.7 acrescenta à árvore.** `Api/Authentication` guarda a configuração do JwtBearer e as checagens além da biblioteca (`ValidacaoDoAccessToken`, `FormaDoAccessToken`, `AutenticacaoLogs`, `AvisoDeClientsPermitidos`). `Api/Authorization` guarda as policies (`Policies`), as respostas de `401` e `403` em Problem Details (`RespostasDeAutorizacao`, `ProblemDetailsDeAutorizacao`) e, com a primeira rota de tenant, os requirements e o `AutorizacaoDaGateway` (D2, planejado). `Infrastructure/Configuration` ganha a `AccessTokenValidationOptions` (seção `Keycloak:Auth`), que o adaptador do Keycloak preenche e a Api só lê (§11.8). `tests/IdentityGateway.Testing.Keycloak` é uma **biblioteca** de suporte, e não um projeto de teste: não referencia `src/`, e é usada pelos projetos de integração e funcional e pelo app de `tools/` (§13, §15). A árvore nunca listou a pasta `Security` do template, que guardava o emissor de JWT simétrico e saiu com ele.
```

- [ ] **Passo 6: §8 — duas linhas no lugar de uma; a primeira rota de tenant; autenticação por padrão**

Na v2.7, §8, na tabela de endpoints (v2.6:528), localizar a linha que começa com:

```markdown
| | `GET /tenants`, `GET /tenants/{tenantId}` |
```

e substituir a linha inteira por estas duas:

```markdown
| | `GET /tenants` (listagem) — chega com a auditoria | platform-admin, auditado (§10.1) |
| | `GET /tenants/{tenantId}` (D2, planejado) | tenant-admin do próprio tenant **e** `Member` dele no banco (ADR-011). **Desvio declarado (v2.7):** o platform-admin recebe `403` até existir a policy `TenantReadAccess`, que chega com a auditoria (§10.1) |
```

Logo abaixo da tabela (v2.6:550), localizar o início do parágrafo:

```markdown
**Sem rota nova na v2.6.**
```

e substituir por:

```markdown
**Sem rota nova na v2.6; a primeira rota de tenant chega com a v2.7 (abaixo).**
```

No mesmo parágrafo (v2.6:550), localizar o fim dele:

```markdown
A operação fica para o M1/M2 (§19).
```

manter, e inserir logo depois do parágrafo, separados por uma linha em branco, estes dois:

```markdown
**A primeira rota de tenant (v2.7) (D2, planejado).** `GET /api/v1/tenants/{tenantId}` devolve o tenant a quem o administra. Responde `200` com **exatamente** estas chaves: `tenantId`, `name`, `slug`, `status`, `plan` (`tier`, `maxUsers`, `maxClients`), `occupiedSeats` e `registeredAt` — nunca o e-mail do admin inicial, e um teste trava o conjunto de chaves. Responde `401` sem token ou com token inválido. Responde `403` em todo o resto: platform-admin, tenant-admin de outro tenant, token sem `tenant-admin`, `sub` que não é `Member` com status aceito e **tenant inexistente** — a policy nega antes do handler, porque não há `Member` num tenant que não existe, e a rota não usa `404`. Todo `403` é o mesmo Problem Details, sem nada que distinga o motivo. Os nomes de `TenantStatus` passam a ser contrato público, e um teste os trava junto com os de `MemberStatus`. O `GET .../provisioning` continua respondendo `200` com o estado, para o platform-admin.

**Autenticação por padrão (v2.7).** A `FallbackPolicy` exige usuário autenticado em todo endpoint que não declare outra coisa. As rotas anônimas — `/health/live`, `/health/ready`, o documento OpenAPI e o Scalar em Development, e o redirect da raiz — levam `AllowAnonymous` explícito, e um teste enumera os endpoints e exige, em cada um, policy nomeada ou anonimato declarado. Efeito visível: um caminho não mapeado, sem token, responde `401`, e não `404`. `401` e `403` saem em Problem Details (§10.1).
```

- [ ] **Passo 7: §9.2 e §9.5 — a demonstração usa device flow; `offline_access` fora do papel padrão (E7)**

Na v2.7, §9.2 (v2.6:606), localizar o parágrafo depois da lista numerada, a linha que começa com:

```markdown
Para não permitir enumeração de tenants, a resposta tem sempre o mesmo formato
```

e inserir **antes** dessa linha, separado dela por uma linha em branco:

```markdown
> **Nota (v2.7).** A demonstração do README obtém o token de usuário pelo Device Authorization Grant, no client `identity-gateway-demo`, que só existe no ambiente local (ADR-003). É um atalho de terminal para quem avalia o repositório, não o fluxo das aplicações: elas usam sempre o Authorization Code com PKCE, descrito acima.
```

Na §9.5 (v2.6:630), no item 1, "Offline tokens", localizar a última frase:

```markdown
Por isso o realm de bootstrap **remove `offline_access` do `default-roles`** (§15): o projeto não usa offline tokens, e mantê-los ligados anularia a desativação.
```

e substituir por:

```markdown
Por isso o realm de bootstrap **tira `offline_access` do papel padrão** (§15): o projeto não usa offline tokens, e mantê-los ligados anularia a desativação. **Entregue na v2.7** — até a v2.6 este item descrevia como feito o que o JSON do realm não tinha (errata E7). O realm declara o papel `offline_access` e o client scope de mesmo nome só para tirá-los do padrão, e faz o mesmo com o papel `uma_authorization`; nenhum client oferece o scope, e o papel padrão fica `[manage-account, view-profile]`. Um teste contra o Keycloak real confere.
```

- [ ] **Passo 8: §10.1 — o que a validação confere; `TenantAdmin` em quatro camadas; o override como policy própria (E3)**

Na v2.7, §10.1 (v2.6:709), localizar o primeiro item da lista, a linha que começa com:

```markdown
- Aceita apenas tokens do realm `identity-gateway` com audiência `identity-gateway-api`
```

e substituir a linha inteira por estes quatro itens:

```markdown
- Aceita apenas tokens do realm `identity-gateway` com audiência `identity-gateway-api`. **O `aud` vem do client scope `gateway-api`, que fica fora dos defaults do realm** (v2.7): só os clients que falam com a Gateway o recebem. Num scope default, todo client do realm — o service account, os clients de tenant do M6, qualquer client de teste — emitiria token aceito; foi reproduzido ao vivo, com um token ROPC de um client criado pela Admin API.
- **O que a validação confere (v2.7), na autenticação:** assinatura RS256, e só RS256, com as chaves lidas dos metadados pelo endereço interno; emissor igual ao **emissor público** do realm, por igualdade ordinal, num `IssuerValidator` próprio, porque o `ValidIssuer` não restringe (§12.1); audiência; validade, com `ClockSkew` de 30 s; o **`azp`** numa lista de clients permitidos por ambiente (`Keycloak:Auth:AllowedClients`); o claim **`typ`** igual a `Bearer`, porque o cabeçalho não distingue access token de ID token; e o **`sub`** como GUID no formato `D`. As três últimas ficam em `OnTokenValidated`, e não numa policy: a policy padrão não se soma a uma policy nomeada, e uma rota com `PlatformAdmin` escaparia. `IncludeErrorDetails` é falso em todo ambiente, porque o `WWW-Authenticate` ecoaria o `iss` e o `aud` recusados, que vêm do token; o diagnóstico sai pelo log (§11.8).
- **A lista de `azp` é estática e fica fora do `appsettings.json` base** (v2.7): o `IConfiguration` mescla arrays por índice, e um item do arquivo base sobreviveria à configuração de produção. O `identity-gateway-demo` só entra em Development, e a subida recusa a lista que o contenha fora dele. Lista vazia é permitida e fail-closed: a API sobe, registra um aviso e recusa todo token de usuário. A lista vale para os clients interativos que chamam a Gateway; os clients M2M de tenant não entram nela (abaixo).
- **`401` e `403` em Problem Details (v2.7).** Um `IAuthorizationMiddlewareResultHandler` escreve um Problem Details fixo para cada status, com o `correlationId`. No `401`, mantém o `WWW-Authenticate` do desafio padrão; no `403`, `type`, `title` e `detail` são constantes, sem nada que distinga o motivo da negação.
```

Localizar o item das famílias de policies (v2.6:710), a linha que começa com:

```markdown
- **Quatro** famílias de policies:
```

e substituir a linha inteira por estes dois itens:

```markdown
- **Quatro** famílias de policies: `PlatformAdmin`; `TenantAdmin`; `StepUp`, para operações destrutivas; e escopos de client, tratados abaixo.
- **`TenantAdmin` é a conjunção de quatro requirements** (v2.7) (D2, planejado): papel `tenant-admin` ∧ **não** `platform-admin` ∧ mesmo tenant ∧ `Member` do tenant da rota no banco, em `{Invited, Active}` (ADR-011). Cada um chama `Fail()` em todo caminho que não é sucesso. `InvokeHandlersAfterFailure` é falso, e o handler da pertença só consulta o banco para quem já passou nas três camadas do token — assim o tempo de resposta não vira oráculo do vínculo entre `sub` e tenant. Negar quem acumula `platform-admin` é separação de funções: o `manage-users` do service account atribui esse papel (§10.2), e sem a negação o desvio do platform-admin na leitura de tenant (abaixo) só valeria para a conta que não acumula papéis.
```

No item do `SameTenantRequirement` (v2.6:711), localizar o fim da linha:

```markdown
e só `Fail()` sobrevive a um `Succeed` alheio.
```

e substituir por:

```markdown
e só `Fail()` sobrevive a um `Succeed` alheio. **Na v2.7, a comparação é por `Guid`, e o claim precisa ter valor único** (D2, planejado): há exatamente um claim `tenant_id`, ele é um GUID no formato `D`, o valor da rota é um GUID, e os dois são iguais como `Guid` — a rota com o GUID em maiúsculas é o mesmo tenant, e um token com dois `tenant_id` é recusado, qualquer que seja a ordem.
```

Localizar o item do override (v2.6:714), a linha que começa com:

```markdown
- **Override do `platform-admin`: só leitura de tenant, e auditado** (I-3).
```

e substituir a linha inteira por (o bloco de citação logo abaixo dela, "O limite é o que sustenta a decisão 1 do
brainstorm", fica como está):

```markdown
- **Override do `platform-admin`: só leitura de tenant, e auditado** (I-3) — **adiado, e como policy própria (v2.7, errata E3).** Até a v2.6, este item dizia que um `PlatformAdminOverrideHandler` satisfazia o `SameTenantRequirement` em `GET /tenants` e `GET /tenants/{tenantId}`. Nunca funcionaria: o `Fail()` do requirement veta qualquer `Succeed`, e a correção "natural" seria afrouxar o `Fail()`. O override passa a ser uma policy própria, **`TenantReadAccess`**, com **um** handler que decide os dois caminhos — platform-admin, com a auditoria gravada fora do handler; ou tenant-admin ∧ ¬platform-admin ∧ mesmo tenant ∧ `Member` —, e é entregue junto com a tabela de auditoria. O `SameTenantRequirement` e a `TenantAdmin` mantêm o `Fail()` incondicional. **Até lá, o platform-admin recebe `403` em `GET /tenants/{tenantId}`**, um desvio declarado em relação ao catálogo da §8: um override sem auditoria seria o único acesso cruzado entre tenants do produto sem trilha. Nas rotas internas do tenant — membros, clients, permission sets, domínios e IdPs — ele não terá acesso, com ou sem o override.
```

Na tabela "Clients de plataforma × clients de tenant" (v2.6:732), na linha do client de tenant, localizar:

```markdown
Recebe o atributo `tenant_id` do tenant que o criou, emitido como claim plano. Sujeito ao `SameTenantRequirement` como qualquer ator |
```

e substituir por:

```markdown
Recebe o atributo `tenant_id` do tenant que o criou, emitido como claim plano — **só se o adaptador do M6 anexar o scope `gateway-tenant` ao client** (v2.7): o scope não é default do realm (§12.2). Sujeito ao `SameTenantRequirement` como qualquer ator |
```

Localizar o parágrafo que fecha a subseção (v2.6:735), a linha que começa com:

```markdown
Essa distinção é necessária porque a Resource API serve todos os tenants
```

manter, e inserir logo depois, separado por uma linha em branco:

```markdown
**A lista de `azp` não cobre clients de tenant (v2.7).** A lista de clients permitidos é estática e vale para os clients interativos que chamam a Gateway. Um client M2M de tenant não entra nela e não recebe o scope `gateway-api`: o token dele não é aceito pela Gateway. Um client de tenant chamando a Gateway, no M6, exige decisão nova — uma consulta ao banco de "este client pertence a este tenant", nunca um prefixo de nome. Fora de Development, a lista fica vazia até existir um client administrativo.
```

- [ ] **Passo 9: §10.2 e §10.3 — o emissor aceito; o one-shot usa o master; rotação do refresh; token sem e-mail**

Na v2.7, §10.2 (v2.6:790), localizar o parágrafo (uma linha só) que começa com:

```markdown
**Emissor público e transporte (errata da v2.6).**
```

manter, e inserir logo depois, separado por uma linha em branco:

```markdown
**O mesmo emissor público é o emissor aceito nos tokens (v2.7).** O `PublicBaseUrl` deixa de alimentar só o `aud` do assertion: `KeycloakAdminOptions.Issuer = {PublicBaseUrl ?? BaseUrl}/realms/{Realm}` é a propriedade única, `AssertionAudience` passa a ser `=> Issuer`, e o adaptador a copia para `AccessTokenValidationOptions.Issuer`, que a Api usa para validar o `iss` dos access tokens (§11.8). É uma derivação só, para as duas contas não divergirem. O `PublicBaseUrl` continua nunca discado: os metadados e as chaves são lidos pelo `BaseUrl`.
```

Localizar o último item da seção (v2.6:795):

```markdown
- A Gateway nunca usa o realm `master`.
```

e substituir a linha inteira por estes dois itens:

```markdown
- A Gateway nunca usa o realm `master`. **Quem usa, no ambiente local, é o one-shot `platform-admin-invite` do compose** (v2.7): ele faz login com o admin do `master` para enviar o convite do primeiro platform-admin (§15). Usar a chave da Gateway ali seria pior: o `manage-users` dela atribuiria `platform-admin`, e a criação da conta de plataforma apareceria nos eventos de administração como obra da Gateway.
- **O raio de dano do `manage-users` é maior do que a v2.6 nomeava (v2.7).** Ele gerencia grupos e mapeia neles qualquer papel que não seja de administração: quem tem a chave da Gateway fabrica um `tenant_id` por grupo, que o mapper do claim aceita (§12.2), e toma a conta de um `Member` real, trocando a senha ou o e-mail dele. O primeiro caminho é o que o ADR-011 fecha na Gateway; o segundo, nenhuma checagem de pertença fecha (§19).
```

Na §10.3 (v2.6:805), localizar a linha que começa com:

```markdown
- **Tokens:** access token de 5 minutos, refresh token com rotação
```

e substituir a linha inteira por estes três itens:

```markdown
- **Tokens:** access token de 5 minutos (`accessTokenLifespan: 300`, explícito no realm), refresh token com rotação e *backchannel logout* habilitado. **A rotação está entregue desde a v2.7:** `revokeRefreshToken: true` e `refreshTokenMaxReuse: 0`. Um refresh token já usado é recusado, e **reusá-lo derruba a sessão inteira daquele client**: depois do reuso, até o refresh token novo é recusado, por desenho do Keycloak. Quem renova guarda o refresh token novo antes de qualquer outro passo e nunca repete uma renovação; se ela falhar, o caminho é um login novo. O refresh token vale 30 minutos de inatividade, o padrão do realm.
- **O token não carrega e-mail nem nome (v2.7).** Os clients que chamam a Gateway não recebem os scopes `profile` nem `email`, e a validação usa `NameClaimType = "sub"`: o username é o e-mail, e `preferred_username` o levaria a logs, a histórico de shell e a proxies. O teste de vazamento do e-mail cobre tokens forjados que carreguem os dois claims (§13).
- **Cadastro fechado e força bruta (v2.7).** `registrationAllowed: false` e `bruteForceProtected: true`, explícitos no realm e conferidos pelo `RegrasDoRealmTests`.
```

- [ ] **Passo 10: §11.7 — erratas E3 e E4; os quatro requirements no código de referência**

Na v2.7, §11.7, no primeiro bloco de código (v2.6:1474), localizar a linha:

```csharp
            new RegisterTenantCommand(body.Name, body.Slug, body.PlanCode), ct);
```

e substituir por estas duas (errata E4):

```csharp
            // Errata E4 (v2.7): o e-mail do admin inicial está no command desde a v2.6 (§11.4).
            new RegisterTenantCommand(body.Name, body.Slug, body.PlanCode, body.InitialAdminEmail), ct);
```

O segundo bloco de código da seção (o `SameTenantHandler`) é substituído inteiro. Localizar a última linha do
primeiro bloco (v2.6:1484):

```markdown
    .WithName("RegisterTenant");
```

até a linha que contém (inclusive, a linha inteira):

```markdown
### 11.9. Repositório de sub-recurso: a assinatura que impede o erro
```

Substituir tudo — da linha `.WithName("RegisterTenant");` ao título `### 11.9. …`, inclusive os dois — pelo texto
abaixo, que começa com a mesma linha `.WithName(...)` e a cerca que fecha o primeiro bloco, e termina com o mesmo
título:

````markdown
    .WithName("RegisterTenant");
```

**Os quatro requirements da policy `TenantAdmin` (v2.7) (D2, planejado).** Até a v2.6, este trecho mostrava um `SameTenantHandler` cujo comentário justificava o `Fail()` por um "segundo handler" do platform-admin. A errata E3 tira esse motivo: o override é uma policy própria (§10.1), e o `Fail()` vale por si. O código abaixo é a referência da D2.

```csharp
// Authorization/SameTenantRequirement.cs (D2, planejado)

/// <summary>
/// Garante que o tenant do token é o mesmo tenant da rota.
/// Sem esta verificação, qualquer tenant-admin operaria sobre qualquer tenant (BOLA/IDOR).
/// </summary>
/// <remarks>
/// O requirement é o próprio handler. Todo caminho que não é sucesso chama Fail(): em ASP.NET Core, um
/// requirement sem Succeed e sem Fail está só "ainda não satisfeito", e outro handler poderia satisfazê-lo.
/// Fail() veta qualquer Succeed — e é por isso que o override do platform-admin NÃO é um segundo handler
/// deste requirement (errata E3 da v2.7): ele é uma policy própria, TenantReadAccess (seção 10.1).
/// </remarks>
public sealed class SameTenantRequirement : AuthorizationHandler<SameTenantRequirement>, IAuthorizationRequirement
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, SameTenantRequirement requirement)
    {
        if (MesmoTenant(context))
            context.Succeed(requirement);
        else
            context.Fail(new AuthorizationFailureReason(this, "Tenant da rota e do token não conferem."));

        return Task.CompletedTask;
    }

    private static bool MesmoTenant(AuthorizationHandlerContext context)
    {
        // Com endpoint routing, o Resource é o próprio HttpContext.
        if (context.Resource is not HttpContext http)
            return false;

        // GetRouteValue devolve o texto cru da URL, mesmo com a restrição :guid. Rota sem {tenantId} é erro
        // de configuração; o teste de subida da seção 13 impede que chegue aqui.
        if (!Guid.TryParse(http.GetRouteValue("tenantId")?.ToString(), out Guid daRota))
            return false;

        // Exatamente UM claim. O Keycloak nunca emite dois (multivalued=false, seção 12.2); aceitar "o
        // primeiro", "o último" ou "algum" aceitaria um token forjado com o tenant da vítima numa das posições.
        if (context.User.FindAll("tenant_id").Take(2).ToArray() is not [Claim claim])
            return false;

        // O claim, só no formato D. O tamanho antes do parse: Guid.TryParseExact tolera espaço nas pontas, e o
        // formato D tem 36 caracteres. E a comparação é por Guid, não por texto: a rota com o GUID em maiúsculas
        // é o mesmo tenant.
        return claim.Value is { Length: 36 }
            && Guid.TryParseExact(claim.Value, "D", out Guid doToken)
            && doToken == daRota;
    }
}
```

```csharp
// Authorization/RoleRequirement.cs e Authorization/NotPlatformAdminRequirement.cs (D2, planejado)

/// <summary>
/// Exige o papel no claim roles. Não é RequireClaim: ele só deixa de dar Succeed, e não chama Fail() — um
/// handler que aprovasse tudo o satisfaria.
/// </summary>
public sealed class RoleRequirement(string role) : AuthorizationHandler<RoleRequirement>, IAuthorizationRequirement
{
    public string Role { get; } = role;

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RoleRequirement requirement)
    {
        if (context.User.HasClaim("roles", requirement.Role))
            context.Succeed(requirement);
        else
            context.Fail(new AuthorizationFailureReason(this, "Papel exigido ausente."));

        return Task.CompletedTask;
    }
}

/// <summary>Separação de funções: quem traz platform-admin não age como tenant-admin.</summary>
public sealed class NotPlatformAdminRequirement
    : AuthorizationHandler<NotPlatformAdminRequirement>, IAuthorizationRequirement
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, NotPlatformAdminRequirement requirement)
    {
        if (context.User.HasClaim("roles", "platform-admin"))
            context.Fail(new AuthorizationFailureReason(this, "Conta de plataforma não age como tenant-admin."));
        else
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
```

```csharp
// Authorization/MemberRequirement.cs e Authorization/MemberRequirementHandler.cs (D2, planejado)
public sealed class MemberRequirement : IAuthorizationRequirement;

/// <summary>
/// A pertença no banco (ADR-011): o sub é Member do tenant da rota, em Invited ou Active.
/// </summary>
/// <remarks>
/// Usa a porta IMemberQueries, e não o Mediator: a Api só fala com o Mediator dentro dos módulos. É o único dos
/// quatro que precisa de DI — e por isso a ordem de registro importa (seção 11.8).
/// </remarks>
internal sealed class MemberRequirementHandler(IMemberQueries members) : AuthorizationHandler<MemberRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, MemberRequirement requirement)
    {
        // Só consulta o banco para quem já passou nas três camadas do token: o tempo de resposta não pode virar
        // oráculo do vínculo (sub, tenant).
        if (context.HasFailed
            || context.Resource is not HttpContext http
            || !Guid.TryParse(http.GetRouteValue("tenantId")?.ToString(), out Guid tenantId)
            // Exatamente um claim sub, como no tenant_id: "o primeiro" poderia ser um que a autenticação não validou.
            || context.User.FindAll("sub").Take(2).ToArray() is not [Claim { Value: { Length: > 0 } sub }])
        {
            context.Fail(new AuthorizationFailureReason(this, "Pertença não verificável."));
            return;
        }

        // O sub vai como o Keycloak o emite, sem normalizar. O CancellationToken é o da requisição: o
        // AuthorizationHandlerContext não tem um.
        MemberStatus? status = await members.GetStatusAsync(
            new TenantId(tenantId), ExternalUserId.From(sub), http.RequestAborted);

        // Lista fechada: um estado que o enum ganhe depois nega, até alguém decidir.
        if (status is MemberStatus.Invited or MemberStatus.Active)
            context.Succeed(requirement);
        else
            context.Fail(new AuthorizationFailureReason(this, "O ator não é membro do tenant."));
    }
}
```

A rota usa a policy pela constante (`RequireAuthorization(Policies.TenantAdmin)`), e o registro dos quatro requirements, com a ordem que a pertença exige, está na §11.8. No módulo, o tenant não achado pelo handler da query — o que a policy já torna impossível — vira o **mesmo `403`** das demais negações, pela função `RespostasDeAutorizacao.Proibido`, e não um `404`. O teste de subida que o comentário acima cita ("rota sem `{tenantId}`") entra com esta rota: até a v2.6 não havia endpoint com policy de tenant em que ele pudesse falhar (§13).

### 11.9. Repositório de sub-recurso: a assinatura que impede o erro
````

- [ ] **Passo 11: §11.9 e §11.8 — a porta `IMemberQueries`; a validação do access token e o registro da autorização**

Na v2.7, §11.9 (v2.6:1567), localizar o parágrafo depois do bloco de código, a linha que começa com:

```markdown
Verificar o tenant da rota (§11.7) não cobre este vetor
```

manter, e inserir logo depois, separado por uma linha em branco:

````markdown
**A leitura da pertença (v2.7) (D2, planejado).** A policy `TenantAdmin` precisa saber se o ator é membro do tenant da rota (ADR-011). Ela não usa o repositório do agregado, que continua só com `Add`: usa uma porta de consulta, no padrão `I*Queries`, com o tenant na assinatura — a mesma regra da §6.4.

```csharp
namespace IdentityGateway.Application.Common.Abstractions;

/// <summary>
/// Leitura da pertença de um ator a um tenant. Não existe GetStatusAsync(ExternalUserId): o tenant está na
/// assinatura, e o membro de outro tenant não é achado.
/// </summary>
public interface IMemberQueries
{
    /// <summary>O status do membro (tenant, sub), ou null se ele não for membro do tenant.</summary>
    Task<MemberStatus?> GetStatusAsync(TenantId tenantId, ExternalUserId externalUserId, CancellationToken ct);
}
```

A implementação (`Infrastructure/Persistence/Queries/MemberQueries.cs`) lê com `AsNoTracking()` e é registrada ao lado do `ITenantQueries`. Um teste de arquitetura proíbe o requirement de depender da Infrastructure.
````

Antes de editar a §11.8, conferir os nomes contra o código entregue pelas Tarefas 7 e 8:

Run: `grep -n "static " src/IdentityGateway.Api/Authentication/ValidacaoDoAccessToken.cs src/IdentityGateway.Api/Authentication/FormaDoAccessToken.cs src/IdentityGateway.Api/Authentication/AutenticacaoLogs.cs`
Expected: em `ValidacaoDoAccessToken`, `Categoria`, `Tolerancia`, `PrazoDosMetadados`, `IntervaloDeRefresh`,
`SoRs256`, `Configurar`, `AoValidar`, `EmissorEstrito`, `AoFalhar` e `EhFalhaDeChaveOuDeMetadados`; em
`FormaDoAccessToken`, `Recusar` e `Texto`; em `AutenticacaoLogs`, `ChavesIndisponiveis`, `TokenRecusado`,
`FormaRecusada` e `NenhumClientPermitido` — são os nomes que as Tarefas 7 e 8 fixaram, e os blocos abaixo os usam.
Se algum nome divergir, o código foi alterado fora do plano: pare e confira antes de colar.

Os blocos abaixo são **referência**, e não cópia dos arquivos: trazem os tipos, as pastas, os nomes e os valores
finais (RS256, 30 s, 5 s, 30 s), com os comentários XML e as guardas de argumento do código de fora. Copie como
estão.

Na v2.7, §11.8, no bloco do `AddKeycloakIdentity` (v2.6:1623), localizar a linha:

```csharp
    // A chave é importada para RSA uma vez; o cache do token é singleton porque o
```

e inserir **antes** dela, separado dela por uma linha em branco:

```csharp
    // v2.7: a validação dos access tokens é configurada por uma option NEUTRA, que a Api lê sem conhecer o
    // Keycloak. Audience e AllowedClients vêm da seção Keycloak:Auth; o emissor, o endereço dos metadados e
    // RequireHttpsMetadata são derivados aqui, das mesmas KeycloakAdminOptions (setters internal: o binder não
    // os preenche).
    services.AddOptions<AccessTokenValidationOptions>()
        .Bind(configuration.GetSection("Keycloak:Auth"))
        .Configure<IOptions<KeycloakAdminOptions>>((token, keycloak) =>
        {
            token.Issuer = keycloak.Value.Issuer;                    // {PublicBaseUrl ?? BaseUrl}/realms/{Realm}
            token.MetadataAddress = keycloak.Value.MetadataAddress;  // {BaseUrl}/realms/{Realm}/.well-known/openid-configuration
            token.RequireHttpsMetadata = !keycloak.Value.AllowInsecureHttp;
        })
        .Validate(token => !string.IsNullOrWhiteSpace(token.Audience), "Keycloak:Auth:Audience é obrigatório.")
        // Fora de Development, o client de demonstração não pode estar na lista. Lista vazia sobe: a API recusa
        // todo token de usuário e registra um aviso (fail-closed).
        .Validate<IHostEnvironment>(
            (token, ambiente) => ambiente.IsDevelopment() || !token.AllowedClients.Contains("identity-gateway-demo"),
            "Keycloak:Auth:AllowedClients contém um client que só é aceito no ambiente Development.")
        .ValidateOnStart();
```

No fim da §11.8 (v2.6:1654), localizar o parágrafo (uma linha só) que começa com:

```markdown
Critérios de tempo de vida: handlers de comando e repositórios são `Scoped`
```

manter, e inserir logo depois dele, separado por uma linha em branco, o texto abaixo (ele fica antes da linha
`---` que fecha a seção):

````markdown
**Validação dos access tokens na Api (v2.7).** A Api configura o JwtBearer a partir da `AccessTokenValidationOptions`, e nunca das options do Keycloak, que ela não conhece (ADR-008). A configuração entra por `AddOptions<JwtBearerOptions>(scheme).Configure<IOptions<…>>`, que roda antes do `JwtBearerPostConfigureOptions`. O código fica em `Api/Authentication`, e o `DependencyInjection` da Api só o chama.

```csharp
// Api/Authentication/ValidacaoDoAccessToken.cs
internal static class ValidacaoDoAccessToken
{
    internal const string Categoria = "IdentityGateway.Api.Authentication";

    // Os padrões da biblioteca são 5 min, 60 s e 5 min. Cinco minutos de tolerância dobrariam a vida do token.
    // Com o Keycloak fora e sem metadados, cada pedido tenta de novo, em fila: 60 s de timeout empilham os
    // pedidos. E uma segunda rotação de chave dentro do intervalo de refresh ficaria em 401 até o fim dele.
    internal static readonly TimeSpan Tolerancia = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan PrazoDosMetadados = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan IntervaloDeRefresh = TimeSpan.FromSeconds(30);

    // Chamado, no DependencyInjection da Api, por
    //   services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    //       .Configure<IOptions<AccessTokenValidationOptions>>((jwt, validacao) => Configurar(jwt, validacao.Value));
    // que roda antes do JwtBearerPostConfigureOptions.
    internal static void Configurar(JwtBearerOptions jwt, AccessTokenValidationOptions validacao)
    {
        // Metadados pelo endereço INTERNO, sem Authority: o discovery devolve o emissor público, e o jwks_uri no
        // host da requisição.
        jwt.MetadataAddress = validacao.MetadataAddress;
        jwt.RequireHttpsMetadata = validacao.RequireHttpsMetadata;   // !AllowInsecureHttp, validado na subida
        jwt.BackchannelTimeout = PrazoDosMetadados;
        jwt.RefreshInterval = IntervaloDeRefresh;

        // Mantém "sub", "roles" e "tenant_id" com os nomes do token (seção 12.1).
        jwt.MapInboundClaims = false;

        // Falso em todo ambiente: o WWW-Authenticate ecoaria o iss e o aud recusados, que vêm do token. Um iss
        // com caractere de controle faria o Kestrel recusar o cabeçalho, e o 401 viraria 500.
        jwt.IncludeErrorDetails = false;

        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            // ValidIssuer NÃO restringe: com metadados, a biblioteca aceita também o issuer do discovery.
            IssuerValidator = EmissorEstrito(validacao.Issuer),
            ValidateAudience = true,
            ValidAudience = validacao.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = SoRs256,                          // [RS256]: só o algoritmo do realm
            ClockSkew = Tolerancia,
            NameClaimType = "sub",                              // o username é o e-mail; não vai a log
            RoleClaimType = "roles",
        };

        jwt.Events = new JwtBearerEvents
        {
            // azp, typ e sub: o que a biblioteca não confere (abaixo).
            OnTokenValidated = contexto => AoValidar(contexto, validacao.AllowedClients),
            OnAuthenticationFailed = AoFalhar,
        };
    }

    // Fail, e não exceção: o resultado é o mesmo 401 de qualquer token recusado, e o motivo — texto fixo, sem
    // nenhum valor do token — vai para o log em Debug.
    private static Task AoValidar(TokenValidatedContext contexto, IReadOnlyList<string> clientsPermitidos)
    {
        string? motivo = contexto.SecurityToken is JsonWebToken token
            ? FormaDoAccessToken.Recusar(token, clientsPermitidos)
            : "token que não é um JWT";

        if (motivo is not null)
        {
            ILogger logger = contexto.HttpContext.RequestServices
                .GetRequiredService<ILoggerFactory>().CreateLogger(Categoria);

            AutenticacaoLogs.FormaRecusada(logger, motivo);               // EventId 2102
            contexto.Fail(motivo);
        }

        return Task.CompletedTask;
    }

    // Warning quando falta a chave de assinatura — o Keycloak fora do ar chega assim —, no máximo um por intervalo:
    // um token de kid inventado provoca a mesma falha sem autenticação. Nos demais casos, só o tipo da exceção, em
    // Debug. Nunca o token nem a mensagem da exceção.
    private static Task AoFalhar(AuthenticationFailedContext contexto)
    {
        ILogger logger = contexto.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>().CreateLogger(Categoria);
        string tipo = contexto.Exception.GetType().Name;

        if (EhFalhaDeChaveOuDeMetadados(contexto.Exception)
            && contexto.HttpContext.RequestServices.GetRequiredService<AvisoDeChavesIndisponiveis>().PodeAvisar())
            AutenticacaoLogs.ChavesIndisponiveis(logger, tipo);           // EventId 2100
        else
            AutenticacaoLogs.TokenRecusado(logger, tipo);                 // EventId 2101

        return Task.CompletedTask;
    }

    /// <summary>Só o emissor público exato, por igualdade ordinal. Internal para ter teste próprio.</summary>
    internal static IssuerValidator EmissorEstrito(string esperado) => (issuer, _, _) =>
        string.Equals(issuer, esperado, StringComparison.Ordinal)
            ? issuer
            : throw new SecurityTokenInvalidIssuerException("Emissor do token não é o do realm configurado.")
            {
                InvalidIssuer = issuer,
            };

    // EhFalhaDeChaveOuDeMetadados(Exception): só SecurityTokenSignatureKeyNotFoundException. Com os metadados
    // frios, a biblioteca engole a falha de busca, e ela chega como falta de chave. Há teste.
}
```

```csharp
// Api/Authentication/FormaDoAccessToken.cs
internal static class FormaDoAccessToken
{
    /// <summary>
    /// azp na lista, typ igual a Bearer e sub GUID. Devolve null se a forma é aceita; senão, o motivo, em texto
    /// fixo, sem nenhum valor do token. Qualquer motivo é 401.
    /// </summary>
    /// <remarks>
    /// Na autenticação, e não numa policy: a policy padrão não se soma a uma policy nomeada, e a rota com
    /// PlatformAdmin escaparia. Lê o JSON do payload, e não os claims: o ClaimsPrincipal achata um array de um
    /// elemento num claim só, e "typ": ["Bearer"] passaria. (Para azp e sub, a própria biblioteca recusa o token
    /// em que eles não são texto, ao ler o JWT; para typ, não.)
    /// </remarks>
    internal static string? Recusar(JsonWebToken token, IReadOnlyList<string> clientsPermitidos)
    {
        using var documento = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.EncodedPayload));
        JsonElement payload = documento.RootElement;

        string? azp = Texto(payload, "azp");

        // Lista vazia: recusa todo token.
        if (string.IsNullOrEmpty(azp) || !clientsPermitidos.Contains(azp, StringComparer.Ordinal))
            return "azp ausente, sem a forma de texto ou fora da lista de clients permitidos";

        // O claim, e não o cabeçalho: o ID token traz "ID".
        if (!string.Equals(Texto(payload, "typ"), "Bearer", StringComparison.Ordinal))
            return "typ diferente de Bearer";

        // O tamanho antes do parse: Guid.TryParseExact tolera espaço nas pontas, e o formato D tem 36 caracteres.
        if (Texto(payload, "sub") is not { Length: 36 } sub || !Guid.TryParseExact(sub, "D", out _))
            return "sub ausente ou fora do formato de GUID";

        return null;
    }

    // Só texto: array, número, objeto e ausência devolvem null.
    private static string? Texto(JsonElement payload, string claim) =>
        payload.TryGetProperty(claim, out JsonElement valor) && valor.ValueKind == JsonValueKind.String
            ? valor.GetString()
            : null;
}
```

Os quatro logs da autenticação são `LoggerMessage`, como o resto do projeto (`AutenticacaoLogs`): `ChavesIndisponiveis` (2100, `Warning`), quando falta a chave de assinatura — com os metadados frios, a falha de busca chega assim —, **no máximo uma vez a cada 30 s por instância**, porque um token de `kid` inventado provoca a mesma falha sem autenticação e sem consumir cota do limitador; `TokenRecusado` (2101, `Debug`), com só o tipo da exceção, nos demais casos; `FormaRecusada` (2102, `Debug`), quando o `azp`, o `typ` ou o `sub` reprovam; e `NenhumClientPermitido` (2103, `Warning`), na subida, quando a lista de `azp` está vazia — registrado por um `IHostedService`, o `AvisoDeClientsPermitidos`. Nenhum deles leva o token. Sem o primeiro, o Keycloak fora do ar viraria `401` em silêncio, porque a biblioteca avisa só pelo `EventSource` dela (§14).

**O registro da autorização (v2.7).** Desde a D1, a Api registra a `FallbackPolicy` autenticada, a policy `PlatformAdmin` pela constante `Policies.PlatformAdmin` e o `IAuthorizationMiddlewareResultHandler` do Problem Details (§10.1). Com a primeira rota de tenant, um método só passa a registrar tudo, na ordem que a pertença exige (D2, planejado):

```csharp
// Api/Authorization/AutorizacaoDaGateway.cs (D2, planejado)
internal static class AutorizacaoDaGateway
{
    public static IServiceCollection AddAutorizacaoDaGateway(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            // Todo endpoint exige usuário autenticado, a menos que declare AllowAnonymous ou outra policy.
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

            // Global. Com ele falso, um handler que viesse depois de um Fail() não roda — e por isso a negação
            // não pode ser auditada por handler: a auditoria nasce fora deles.
            options.InvokeHandlersAfterFailure = false;

            options.AddPolicy(Policies.PlatformAdmin, policy => policy.RequireClaim("roles", "platform-admin"));

            options.AddPolicy(Policies.TenantAdmin, policy => policy.AddRequirements(
                new RoleRequirement("tenant-admin"),
                new NotPlatformAdminRequirement(),
                new SameTenantRequirement(),
                new MemberRequirement()));
        });

        // A ORDEM IMPORTA. Os três primeiros requirements são o próprio handler e rodam dentro do
        // PassThroughAuthorizationHandler, que o AddAuthorization acabou de registrar. O handler da pertença
        // entra DEPOIS dele: se entrasse antes, rodaria primeiro, com o contexto ainda sem falha, e consultaria
        // o banco para qualquer tenantId.
        services.AddScoped<IAuthorizationHandler, MemberRequirementHandler>();

        return services;
    }
}
```

Produção e testes unitários usam o mesmo método. Um teste funcional, com a DI real e uma porta falsa que conta as chamadas, prova que a pertença não é consultada com o papel ausente, com outro tenant nem com `platform-admin` (§13).
````

- [ ] **Passo 12: §12.1 — o JSON real do realm (E5), só o catálogo no claim (E6), testes pelo harness (E2, E8), diagnóstico**

A abertura da §12 (o código do pacote do Data Plane, com `Authority` e `NameClaimType = "preferred_username"`) **não
muda**: rever a configuração do Data Plane é proposta do design, não decisão desta fatia.

Na v2.7, §12.1, "Armadilha 1", no exemplo de token (v2.6:1718) — localizar as duas linhas:

```json
  "sub": "5f1c0a2e-7c1d-4a55-9b0e-2f6f3c9d1a10",
  "preferred_username": "ana@empresa-a.com",
```

e substituir por uma só (sai o `preferred_username`):

```json
  "sub": "5f1c0a2e-7c1d-4a55-9b0e-2f6f3c9d1a10",
```

No parágrafo logo abaixo do exemplo (v2.6:1731), localizar o fim dele:

```markdown
Não existe um claim chamado `roles` no primeiro nível.
```

e substituir por:

```markdown
Não existe um claim chamado `roles` no primeiro nível. O exemplo deixou de trazer `preferred_username` (v2.7): os clients que chamam a Gateway não recebem o scope `profile`, e o token deles não carrega e-mail nem nome (§10.3).
```

Na "Solução A", na linha do caminho pelo console (v2.6:1778), localizar:

```markdown
(`gateway-roles`, tipo Default)
```

e substituir por:

```markdown
(`gateway-roles`, tipo **None**: desde a v2.7 o scope não é default do realm)
```

O JSON de bootstrap e o parágrafo que o segue são substituídos. Localizar a linha (v2.6:1780):

```markdown
O mesmo, no arquivo de bootstrap do realm:
```

até a linha que contém (inclusive, a linha inteira):

```markdown
O `defaultDefaultClientScopes` é importante para este projeto:
```

Substituir tudo — a linha, o bloco JSON inteiro e o parágrafo "O `defaultDefaultClientScopes` é importante…", que é
uma linha só — por:

````markdown
O mesmo, no arquivo de bootstrap do realm (v2.7):

```json
{
  "attributes": { "CreateDefaultClientScopes": "true" },
  "clientScopes": [
    {
      "name": "gateway-roles",
      "protocol": "openid-connect",
      "attributes": { "include.in.token.scope": "false", "display.on.consent.screen": "false" },
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
    }
  ],
  "scopeMappings": [
    { "clientScope": "gateway-roles", "roles": ["platform-admin", "tenant-admin", "financial-manager", "reader"] }
  ],
  "clients": [
    {
      "clientId": "identity-gateway",
      "fullScopeAllowed": true,
      "defaultClientScopes": ["basic", "roles"],
      "optionalClientScopes": []
    },
    {
      "clientId": "identity-gateway-demo",
      "publicClient": true,
      "fullScopeAllowed": false,
      "defaultClientScopes": ["basic", "acr", "gateway-roles", "gateway-tenant", "gateway-api"],
      "optionalClientScopes": []
    }
  ]
}
```

**Errata da v2.7 (E5): declarar `clientScopes` desliga a criação dos scopes embutidos.** A v2.6 mandava listar o `gateway-roles` em `defaultDefaultClientScopes` ao lado dos scopes padrão, "porque ela substitui o conjunto padrão do realm". O efeito real é outro, e pior: quando o JSON do realm declara `clientScopes`, o import **não cria** os embutidos (`profile`, `email`, `roles`, `basic`, `acr`, `web-origins`…), sem erro — e um token de um client sem o scope `basic` sai **sem `sub`**. A correção é o atributo de realm `"CreateDefaultClientScopes": "true"`, que não aparece na documentação pública e não é persistido, porque o import o consome: a presença dos embutidos é provada contra o Keycloak real, e não no JSON, e precisa ser reverificada a cada troca de tag do Keycloak (§19).

**Os scopes `gateway-*` ficam fora dos defaults do realm, e cada client declara os seus (v2.7).** Nenhum dos três — `gateway-roles`, `gateway-tenant` (§12.2) e `gateway-api`, que carrega o Audience Mapper — está em `defaultDefaultClientScopes`. Num scope default, todo client do realm emitiria token com a audiência da Gateway (§10.1). Todo client do JSON declara `defaultClientScopes` e `optionalClientScopes` explícitos. O service account `identity-gateway` fica com `basic` e `roles` — é do `roles` que vem o `resource_access` que o health check lê —, sem `gateway-*`, e mantém `fullScopeAllowed: true`, porque com `false` a Admin API passa a responder `403`. O client de demonstração fica com `basic`, `acr` e os três `gateway-*`, sem `profile` nem `email`, com `fullScopeAllowed: false`. **Consequência para o M6:** um client criado depois não recebe os scopes sozinho; o adaptador que provisiona clients de tenant anexa explicitamente os que o client deve ter (§12.2).
````

O token de exemplo e os dois parágrafos que o seguem também são substituídos. Localizar a linha (v2.6:1813):

```markdown
Com o mapper ativo, o token passa a ter:
```

até a linha que contém (inclusive, a linha inteira):

```markdown
Os papéis `default-roles-identity-gateway`, `offline_access` e `uma_authorization` aparecem em todos os usuários.
```

Substituir tudo — a linha, o bloco JSON, o parágrafo "O `realm_access` continua existindo…" e o parágrafo "Os
papéis `default-roles-identity-gateway`…", cada um deles uma linha só — por:

````markdown
Com o scope associado ao client, o token passa a ter:

```json
{
  "sub": "5f1c0a2e-7c1d-4a55-9b0e-2f6f3c9d1a10",
  "aud": "identity-gateway-api",
  "azp": "identity-gateway-demo",
  "typ": "Bearer",
  "acr": "1",
  "roles": ["tenant-admin"],
  "tenant_id": "0199a000-0000-7000-8000-00000000000a"
}
```

**Só o catálogo no claim (v2.7, errata E6).** A v2.6 mostrava o claim `roles` com `default-roles-identity-gateway`, `offline_access` e `uma_authorization`, e dizia que eles "não interferem". Eles entravam no claim de todo usuário — e a `RoleAssignmentPolicy` lê esse claim (§6.3). Agora saem, por dois mecanismos: o client de demonstração tem `fullScopeAllowed: false`, e os scope mappings do `gateway-roles` são só os quatro papéis do catálogo. Um usuário sem papel do catálogo recebe o token **sem** o claim `roles`. Os papéis do catálogo **nunca são compostos**: o Keycloak decide o que o `manage-users` pode atribuir pelo nome do papel, e um papel do catálogo composto com papéis de `realm-management` seria atribuível pelo service account.

**O scope `roles` fica fora dos clients de usuário (v2.7).** A v2.6 o mantinha por causa do mapper *audience resolve*. Com o `aud` vindo explícito do `gateway-api`, ele deixa de ser necessário, e o `realm_access` só duplicaria o `roles`: o token do client de demonstração não traz `realm_access` nem `resource_access`.
````

Na "Solução B" (v2.6:1886), localizar o parágrafo que a fecha, a linha que começa com:

```markdown
A Solução A continua sendo a preferida:
```

manter, e inserir logo depois, separado por uma linha em branco:

```markdown
**Nos clients que chamam a Gateway, a Solução B fica inerte (v2.7).** O token deles não traz `realm_access`, e não há o que achatar. Ela continua no pacote do Data Plane, para tokens de ambientes de terceiros.
```

Em "Como verificar", o item 3 e os dois testes são substituídos. Localizar a linha (v2.6:1910):

```markdown
3. **No CI, com teste de integração contra o Keycloak real (Testcontainers):**
```

até a linha que contém (inclusive, a linha inteira):

```markdown
O primeiro teste protege a configuração do Keycloak; o segundo protege a configuração do .NET.
```

Substituir tudo — a linha do item 3, o bloco de código com os dois testes e o parágrafo "O primeiro teste protege…",
que é uma linha só — por:

````markdown
3. **No CI, com teste de integração contra o Keycloak real (Testcontainers), pelo harness de login (v2.7):**

```csharp
[Fact]
public async Task TokenDoDemo_TrazRolesPlanoSoComOCatalogo()
{
    // O harness faz o device flow no client de demonstração: conclui o login e o consentimento nas páginas do
    // próprio Keycloak, com uma senha que ele mesmo definiu pelo link de ações. Não é ROPC (ADR-003).
    TokensDeUsuario tokens = await harness.TokenPorDispositivoAsync(adminDoTenant, senha, ct);

    var jwt = new JsonWebToken(tokens.AccessToken);
    string[] roles = [.. jwt.Claims.Where(c => c.Type == "roles").Select(c => c.Value)];

    roles.Should().BeEquivalentTo(["tenant-admin"]);     // plano, e só o catálogo: nenhum default-roles-*
    jwt.Claims.Should().NotContain(c => c.Type == "realm_access");
}

[Fact]
public async Task PlatformAdminReal_RegistraTenant()
{
    // Atravessa a API com um token do Keycloak real: protege a configuração do .NET (MapInboundClaims,
    // RoleClaimType, emissor, audiência e azp), que o teste acima não vê.
    TokensDeUsuario tokens = await harness.TokenPorDispositivoAsync(platformAdmin, senha, ct);
    client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);

    HttpResponseMessage resposta = await client.PostAsJsonAsync("/api/v1/tenants", novoTenant, ct);

    resposta.StatusCode.Should().Be(HttpStatusCode.Accepted);
}
```

> **Erratas da v2.7 (E2 e E8).** Os testes de referência da v2.6 chamavam um método que nunca existiu (`GetClientCredentialsTokenAsync`), contra a rota `/api/v1/tenants/tenant-a/members`, com um slug no lugar do GUID. E o comentário deles dizia que "o ROPC continua desabilitado também no realm de teste": era falso. O `KeycloakFixture` obtinha um token de usuário por ROPC, num client criado em runtime, e o `admin-cli` embutido do realm tem direct grant. A v2.7 tira o ROPC do fixture — os tokens de usuário dos testes vêm do device flow, pelo mesmo harness que a CI e a demonstração usam — e declara o alcance da proibição no ADR-003.

O primeiro teste protege a configuração do Keycloak; o segundo protege a configuração do .NET. Se alguém remover o mapper ou voltar `MapInboundClaims` para o padrão, um dos dois quebra.
````

Em "Diagnóstico rápido" (v2.6:1945), localizar a primeira linha da tabela, a que começa com:

```markdown
| 403 com token válido; o papel aparece dentro de `realm_access` |
```

e substituir a linha inteira por:

```markdown
| 403 com token válido; o papel aparece dentro de `realm_access` | Mapper plano ausente ou scope não associado ao client | Associar `gateway-roles` aos `defaultClientScopes` do client (v2.7: o scope não é default do realm) |
```

Na mesma tabela (v2.6:1952), localizar a última linha, a que começa com:

```markdown
| Papel recém-atribuído não funciona | Token emitido antes da atribuição |
```

manter, e inserir logo depois dela estas cinco linhas:

```markdown
| Token de outro emissor é aceito, apesar do `ValidIssuer` (v2.7) | Com metadados, a biblioteca aceita o `issuer` anunciado pelo discovery antes de olhar o `ValidIssuer` | `IssuerValidator` próprio, com igualdade ordinal contra o emissor público (§11.8) |
| `401` com token válido; o `aud` é `account` ou não existe (v2.7) | O client não tem o scope `gateway-api`, que não é default do realm | Anexar `gateway-api` aos `defaultClientScopes` do client — só dos que falam com a Gateway |
| `default-roles-identity-gateway` aparece no claim `roles` (v2.7) | `fullScopeAllowed: true` no client, ou scope mappings do `gateway-roles` além do catálogo | `fullScopeAllowed: false`, e scope mappings só com os quatro papéis |
| `profile`, `email`, `roles` e `basic` sumiram do realm depois do import (v2.7) | O JSON declara `clientScopes` sem o atributo `CreateDefaultClientScopes` | `"CreateDefaultClientScopes": "true"` nos atributos do realm, e volume novo |
| `401` com token válido; o token não tem `sub` (v2.7) | O client não tem o scope `basic` | `basic` nos `defaultClientScopes` do client |
```

- [ ] **Passo 13: §12.2 — `gateway-tenant` fora dos defaults; o recuo do mapper para o grupo; teste pelo harness (E8)**

Na v2.7, §12.2, "Solução adotada" (v2.6:2005), localizar o parágrafo (uma linha só) que começa com:

```markdown
O mapper vai no client scope `gateway-tenant`, incluído em `defaultDefaultClientScopes`
```

e substituir a linha inteira por estes dois parágrafos:

```markdown
O mapper vai no client scope `gateway-tenant`, com `multivalued` e `aggregate.attrs` em `"false"`. **O scope fica fora dos defaults do realm (v2.7)**, como o `gateway-roles` e o `gateway-api` (§12.1): cada client recebe só o que declara. O client de demonstração declara os três. **No M6, o adaptador que provisiona clients de tenant anexa o `gateway-tenant` explicitamente** — e o `gateway-api`, só se o client for chamar a Gateway, o que exige decisão nova (§10.1). A v2.6 punha o scope em `defaultDefaultClientScopes`, para todo client criado depois o receber sozinho; com os scopes fora dos defaults, esse automatismo deixa de existir, de propósito.

**O mapper recua para o atributo de grupo (v2.7).** Quando o usuário não tem o atributo `tenant_id`, o `oidc-usermodel-attribute-mapper` usa o atributo de mesmo nome do **primeiro grupo** que o tiver, subindo aos pais, e não há configuração que desligue isso — verificado no código e ao vivo, na 26.7.4. Com `multivalued` em `"false"`, o token nunca traz dois valores. O `manage-users` cria grupos (§10.2): o claim é forjável por quem tem a chave da Gateway. São duas defesas. **O realm não tem nenhum grupo**, e o `RegrasDoRealmTests` reprova `groups` e `defaultGroups` no JSON. E, nas rotas de governança, a Gateway exige também a pertença no banco (ADR-011) (D2, planejado). Um grupo criado em runtime não é visto pela regra do JSON, e o Data Plane continua exposto a ele (§19). Um teste de caracterização contra o Keycloak real afirma o recuo, para avisar se uma versão nova mudar o comportamento. Atributo de Organization não vaza para o claim.
```

Em "Teste que protege esta decisão" (v2.6:2024), localizar as duas linhas:

```csharp
    var token = await _keycloak.GetTokenForUserAsync("ana@empresa-a.com");
    var jwt = new JsonWebToken(token);
```

e substituir por estas três (errata E8):

```csharp
    // Errata E8 (v2.7): GetTokenForUserAsync nunca existiu. O token vem do device flow, pelo harness de login.
    TokensDeUsuario tokens = await harness.TokenPorDispositivoAsync(adminDoTenantA, senha, ct);
    var jwt = new JsonWebToken(tokens.AccessToken);
```

- [ ] **Passo 14: §13 — as linhas novas da tabela de testes; as três regras continuam três**

Na v2.7, §13, na linha "Autorização negativa" da tabela (v2.6:2048), localizar:

```markdown
Mais escalação de papel e token sem audiência correta
```

e substituir por:

```markdown
Mais escalação de papel e token sem audiência correta. **Continuam três (v2.7):** nas rotas de governança da Gateway, a regra (a) é conferida também pela pertença do ator ao tenant no banco (ADR-011), que a reforça e não é uma quarta regra (D2, planejado)
```

Localizar a linha "Configuração de endpoint" (v2.6:2049), a que começa com:

```markdown
| Configuração de endpoint | Teste de startup que varre o `EndpointDataSource`
```

e substituir a linha inteira por:

```markdown
| Configuração de endpoint | Teste de startup que varre o `EndpointDataSource` e falha se um endpoint com policy de tenant **não** tiver `{tenantId}` no template — o inverso do teste acima. **Entra com a primeira policy de tenant, `TenantAdmin`** (v2.7) (D2, planejado): até a v2.6 não havia endpoint em que ele pudesse falhar | `EndpointDataSource` do host de teste |
```

Localizar a última linha da tabela (v2.6:2056), a que começa com:

```markdown
| Convite ponta a ponta, atomicidade e entrega concorrente (v2.6) |
```

manter, e inserir logo depois dela estas nove linhas:

```markdown
| OIDC falso com emissor divergente (v2.7) | Os testes funcionais sobem um Kestrel em loopback que serve discovery e JWKS de uma chave RSA de teste, só por configuração. O discovery anuncia um emissor **diferente** do configurado, e os tokens positivos usam o configurado: é o que prova o `IssuerValidator` estrito, porque com o `ValidIssuer` o emissor do discovery seria aceito. O emissor de teste imita o token real (`aud` texto, `sub` GUID, `typ` `Bearer`, `azp` do demo, `roles` array, `tenant_id` texto, 5 min) e aceita payload livre para os casos malformados | `WebApplicationFactory`, Kestrel em loopback |
| Suíte negativa de autenticação, em tabela (v2.7) | Nas rotas protegidas: sem token, malformado, `Bearer` vazio, esquema `Basic`; vencido há 2 min, `nbf` no futuro, sem `exp`; `aud` errada ou ausente; `iss` forasteiro, interno, com barra final, sufixo ou maiúsculas, e o anunciado pelo discovery; outra chave RSA com o mesmo `kid`, `alg=none`, HS256 com a chave pública como segredo e a receita HS256 antiga; `typ` igual a `ID`; `azp` fora da lista, ausente, vazio, em array ou numérico; sem `sub`, `sub` não-GUID, no formato `N` ou em array. Todos `401`. Um caminho não mapeado, sem token, também `401` — a `FallbackPolicy` | `[Theory]` sobre o OIDC falso |
| Opções do JwtBearer conferidas em execução (v2.7) | As `JwtBearerOptions` resolvidas do esquema têm `IssuerSigningKey` nulo, `IssuerSigningKeys` vazio, `SignatureValidator` e `IssuerSigningKeyResolver` nulos, as quatro validações ligadas, `ValidAlgorithms` igual a `[RS256]` e `IncludeErrorDetails` falso. É teste de execução, e não regra de arquitetura, porque o NetArchTest enxerga tipos, não propriedades | `IOptionsMonitor<JwtBearerOptions>` |
| Host em `Production`, com pedidos (v2.7) | Uma factory em `Production`: a subida falha com o client de demonstração na lista de `azp`, com `BaseUrl` em `http` ou com `AllowInsecureHttp`. E, com pedidos: `RequireHttpsMetadata` verdadeiro e `BackchannelTimeout` de 5 s nas opções resolvidas; `aud` errada leva `401` sem `error_description`; com a lista de `azp` vazia, a API sobe, registra o aviso e recusa um token válido; com o endereço dos metadados num listener que aceita a conexão e nunca responde, o `401` sai em menos de ~7 s, com o `Warning` capturado e sem o token no log | `WebApplicationFactory` em `Production`, HTTPS em loopback |
| Coleção com Keycloak real atravessando a API (v2.7) | Cada teste cria o próprio platform-admin, conclui o link de ações e obtém o token pelo device flow. **Ponte de contrato:** os tipos dos claims de um token real são os do emissor de teste — sem ela, os funcionais ficariam verdes sem provar nada sobre o Keycloak. Platform-admin real → `POST /tenants` → `202`. Token de um client com `azp` aceito e sem `gateway-api` → `401`, que só pode vir da audiência. Token do service account e **token ROPC do `admin-cli` do realm** → `401`, com a forma do token afirmada. `PublicBaseUrl` errado → `401`. Um refresh token já usado é recusado, e depois dele o novo também | Testcontainers, `ICollectionFixture`, harness de login |
| Vazamento do e-mail no token (v2.7) | Tokens de forma Keycloak que carreguem `email` e `preferred_username`, forjados no OIDC falso: sucesso, `403`, expirado, `aud` errada e assinatura inválida, conferindo corpo, `WWW-Authenticate`, **log e trace**. A captura é um sink do Serilog em memória, em `Debug`, e um exportador OpenTelemetry em memória; procura o e-mail em texto e em base64url, nos três alinhamentos, o token cru e o segmento do payload | Serilog em memória, OpenTelemetry InMemory exporter |
| Sem chave simétrica em produção, e as regras novas do realm (v2.7) | Arquitetura: nenhuma camada de produção usa `SymmetricSecurityKey`, e a Api não usa os tipos de `System.IdentityModel.Tokens.Jwt`; sem `ShowPII`, sem `IClaimsTransformation` nem segundo esquema de autenticação. No `RegrasDoRealmTests`: catálogo exato e nunca composto, `CreateDefaultClientScopes`, os três scopes fora dos defaults, o Audience Mapper só no `gateway-api`, todo client com `fullScopeAllowed`, `directAccessGrantsEnabled` falso e scopes explícitos, o demo só com device flow, nenhum grupo, `accessTokenLifespan` 300 e a rotação do refresh token | NetArchTest, leitura do JSON do realm |
| Keycloak parado, na CI (v2.7) | O job `Compose` faz um `GET` autenticado, para o Keycloak com `docker compose stop`, registra um tenant (`202`, `Pending`), religa o Keycloak, renova o token e espera `Active`. É a demonstração nº 1 do README com prova automática, e trava o cache de metadados na topologia real (§15) | `tools/jornada-compose.cs` |
| Autorização da rota de tenant (D2, planejado) | Unitário com a policy `TenantAdmin` real e um handler que aprova tudo: cada caminho de falha dos quatro requirements termina com `FailCalled`. `[Theory]` sobre todos os valores de `MemberStatus`, com a tabela esperada escrita à mão — um estado novo reprova até alguém decidir. Os nomes de `TenantStatus` e de `MemberStatus` travados. Com a DI real e uma porta falsa que conta chamadas, a pertença não é consultada com o papel ausente, com outro tenant nem com `platform-admin`. E o teste de subida da linha "Configuração de endpoint", que ganha objeto com a primeira policy de tenant | xUnit, `IAuthorizationService` montado pelo `AddAutorizacaoDaGateway` |
```

Depois da tabela, no parágrafo "O teste negativo de autorização é o mais importante do projeto" (v2.6:2060),
localizar o fim dele:

```markdown
Assim, um endpoint novo não entra desprotegido por esquecimento.
```

e substituir por:

```markdown
Assim, um endpoint novo não entra desprotegido por esquecimento. A pertença no banco (ADR-011) entra como reforço da primeira regra nas rotas de governança, sem virar uma quarta (v2.7) (D2, planejado).
```

- [ ] **Passo 15: §14 — Keycloak fora do ar responde `401`, com log**

Na v2.7, §14 (v2.6:2071), localizar o último item da lista, a linha que começa com:

```markdown
- **Health checks:** `live` verifica apenas o processo;
```

manter, e inserir logo depois dela, como mais um item da lista:

```markdown
- **Keycloak fora do ar e a autenticação (v2.7).** Com os metadados ainda não carregados e o Keycloak inalcançável, a API sobe, e um pedido com token responde **`401`** — nunca `500`. A biblioteca engole a falha de busca e reprova o token por falta de chave, avisando só pelo `EventSource` dela; sem um log próprio, o Keycloak fora viraria `401` em silêncio. Por isso `OnAuthenticationFailed` registra um `Warning` quando falta a chave de assinatura (`AutenticacaoLogs`, EventIds 2100 a 2103), limitado a um por intervalo de 30 s, e nunca o token. O mesmo aviso sai para um token de `kid` desconhecido assinado por outra chave: o texto dele diz as duas causas. Com os metadados já carregados, a API continua validando tokens de `kid` conhecido com o Keycloak parado. **Alternativa registrada:** `503` com `Retry-After` na falha de configuração, coerente com o argumento da §9.6; não entrou porque exige distinguir, no evento de falha, configuração indisponível de token inválido. O `/health/ready` cobre o Keycloak inteiro fora do ar, mas não um `jwks_uri` inalcançável (§19).
```

- [ ] **Passo 16: §15 — a tabela de serviços e o que a fatia D acrescentou ao bootstrap (E7)**

Na v2.7, §15, na tabela de serviços, linha do `keycloak` (v2.6:2103), localizar o fim da linha:

```markdown
conferidos por `test -n` no entrypoint); sobe depois do `mailpit` saudável (v2.6) |
```

e substituir pelo texto abaixo, que fecha a linha do `keycloak` e acrescenta, na linha seguinte, a do one-shot:

```markdown
conferidos por `test -n` no entrypoint); sobe depois do `mailpit` saudável (v2.6). Recebe também `PLATFORM_ADMIN_EMAIL` (padrão `platform-admin@identity-gateway.local`), que o import põe no usuário do bootstrap; o entrypoint recusa valor vazio ou com maiúsculas (v2.7) |
| `platform-admin-invite` | One-shot (v2.7): a imagem do Keycloak, com o `kcadm.sh`, depois do `keycloak` saudável. Envia **uma vez** o e-mail de ações do primeiro platform-admin, com link de 4 horas, e grava antes o marcador `platformAdminInviteSentAt` no realm; nas subidas seguintes, sai com `0` sem reenviar. Num volume anterior à v2.7, sai com `1` e manda rodar `docker compose down -v`. Reenvio só por comando: `docker compose run --rm -e REENVIAR=1 platform-admin-invite` |
```

Na mesma tabela (v2.6:2109), localizar a linha do `jaeger`:

```markdown
| `jaeger` | Traces do OpenTelemetry |
```

e substituir por:

```markdown
| `jaeger` | Traces do OpenTelemetry; portas publicadas só em `127.0.0.1` (v2.7: agora circulam tokens reais, e um trace pode carregar a URL de uma chamada) |
```

Na mesma tabela (v2.6:2110), localizar a linha da `api`, a que começa com:

```markdown
| `api` | A API, com `Keycloak__Admin__PublicBaseUrl=http://localhost:8081`
```

e substituir a linha inteira por:

```markdown
| `api` | A API, com `Keycloak__Admin__PublicBaseUrl=http://localhost:8081`, que alimenta o `aud` do assertion (§10.2, v2.6) **e o emissor aceito nos access tokens** (v2.7). Publicada só em `127.0.0.1:8080`; sobe depois do `migrate` e do `platform-admin-invite` concluídos; sem `Jwt__SigningKey`, que saiu com o JWT simétrico; a lista de `azp` vem do `appsettings.Development.json` (v2.7) |
```

No parágrafo "O arquivo de bootstrap contém o mínimo para o ambiente local" (v2.6:2117), localizar:

```markdown
client scopes `gateway-roles` (§12.1) e `gateway-tenant` (§12.2) em `defaultDefaultClientScopes`, Audience Mapper, catálogo de papéis e armazenamento de eventos ativado.
```

e substituir por:

```markdown
os client scopes `gateway-roles` (§12.1), `gateway-tenant` (§12.2) e `gateway-api`, este com o Audience Mapper, **fora** dos defaults do realm, o catálogo de papéis, o client de demonstração e o usuário do primeiro platform-admin (v2.7). **Errata da v2.7 (E7):** até a v2.6, esta frase dava como presentes os scopes em `defaultDefaultClientScopes`, o Audience Mapper, o catálogo e o armazenamento de eventos, que o JSON não tinha. Os três primeiros entraram com a fatia D, na forma descrita abaixo; **o armazenamento de eventos do realm continua pendente** (§16).
```

Localizar o parágrafo (uma linha só) que começa com (v2.6:2119):

```markdown
**O que a fatia C acrescentou ao bootstrap (v2.6):**
```

manter, e inserir logo depois, separado por uma linha em branco:

```markdown
**O que a fatia D acrescentou ao bootstrap (v2.7):** o catálogo de papéis completo — `platform-admin`, `tenant-admin`, `financial-manager` e `reader`, nunca compostos —, mais `offline_access` e `uma_authorization`, declarados só para sair do papel padrão; os três client scopes `gateway-roles`, `gateway-tenant` e `gateway-api`, nenhum deles default do realm, com os scope mappings do `gateway-roles` iguais ao catálogo; o atributo de realm `CreateDefaultClientScopes` igual a `"true"`, sem o qual declarar `clientScopes` apaga os scopes embutidos (§12.1); `defaultClientScopes` e `optionalClientScopes` explícitos em todo client, o service account com `basic` e `roles`; o client `identity-gateway-demo`, público, só com Device Authorization Grant, com `basic`, `acr` e os três `gateway-*`, `fullScopeAllowed: false` e código de dispositivo de 300 s, descrito como de demonstração local e fora do Terraform de produção; `accessTokenLifespan: 300`, `registrationAllowed: false`, `bruteForceProtected: true`, `revokeRefreshToken: true` e `refreshTokenMaxReuse: 0`; e o usuário do primeiro platform-admin, com `username` e `email` iguais a `${PLATFORM_ADMIN_EMAIL}`, só o papel `platform-admin`, sem credencial, sem atributos e sem grupos, com `UPDATE_PASSWORD` e `VERIFY_EMAIL`. As descrições são texto puro: as chaves de i18n do Keycloak (`${...}`) reprovariam a regra de placeholders. O `RegrasDoRealmTests` ganhou uma regra para cada item, cada uma provada por mutação, e o `NenhumaChaveDeCredencial` passou a percorrer também o JSON embutido do User Profile.
```

Localizar o parágrafo (uma linha só) que começa com (v2.6:2121):

```markdown
**Placeholder de SMTP sem valor derruba o import (v2.6).**
```

manter, e inserir logo depois, separado por uma linha em branco:

```markdown
**`PLATFORM_ADMIN_EMAIL` vazio ou com maiúsculas não sobe (v2.7).** Uma variável ausente ficaria gravada como texto literal no `username` e no `email` do usuário do bootstrap, sem erro; e o import grava o e-mail em minúsculas, de modo que um valor com maiúsculas não seria achado depois por uma comparação exata. O entrypoint do `keycloak` confere as duas coisas antes de subir, e o one-shot ainda compara o e-mail em minúsculas. O padrão, `platform-admin@identity-gateway.local`, é o mesmo no compose, no app da CI e no README, e um teste de arquitetura confere que é igual nos três e minúsculo.
```

No parágrafo "Endereço público do Keycloak (v2.6)" (v2.6:2123), localizar:

```markdown
que alimenta só o `aud` do assertion (§10.2). A porta `8081` aparece em três lugares
```

e substituir por:

```markdown
que alimenta o `aud` do assertion e, desde a v2.7, o emissor aceito nos access tokens (§10.2). A porta `8081` aparece em três lugares
```

Em "Credenciais do ambiente local" (v2.6:2130), localizar o item que começa com:

```markdown
- **Admin master do Keycloak.**
```

e substituir a linha inteira por:

```markdown
- **Admin master do Keycloak.** A senha também é gerada pelo `gateway-keys` e exibida **uma vez** no log; o entrypoint a exporta para `KC_BOOTSTRAP_ADMIN_PASSWORD`. Com a porta publicada só em `127.0.0.1`, o console não fica exposto à rede local. **A senha ganhou um segundo consumidor (v2.7):** o one-shot `platform-admin-invite` monta, só para leitura, a subpasta do volume que tem o `/keys/admin-password`, e faz login no `master` com ela a cada subida, antes de qualquer outro passo. A senha nunca vira variável declarada no compose, que apareceria no `docker inspect`. **Consequência:** trocar a senha ou apagar o admin do `master` do compose — o console da 26.x o chama de temporário e sugere a troca — quebra toda subida seguinte, e a `api` não sobe; o one-shot sai com uma mensagem própria, e a saída é `docker compose down -v` (§19). O log do `gateway-keys` imprime essa senha, e por isso nunca entra no log da CI.
```

No item "Chave e realm andam juntos" (v2.6:2132), localizar o fim dele:

```markdown
o `docker compose down -v` fica a cargo de quem lê, só no ambiente local.
```

e substituir por:

```markdown
o `docker compose down -v` fica a cargo de quem lê, só no ambiente local. **Um volume anterior à v2.7 falha ainda mais cedo (v2.7):** o one-shot `platform-admin-invite` confere se o realm tem o scope `gateway-api`; sem ele, sai com `1` e a instrução do `down -v`. Como a `api` depende do one-shot, o `docker compose up` falha com a causa no log, em vez de a API subir e todo token dar `401`. O `KeycloakHealthCheck` não muda: ele pega o volume de antes da v2.6, e o one-shot, o de antes da v2.7.
```

- [ ] **Passo 17: §15 — a CI com o app C# (E1), `offline_access` (E7), o bootstrap do platform-admin e o Scalar**

O parágrafo da CI é o que traz a frase da errata E1. O texto novo leva o marcador `%%E1%%` no lugar da sequência de
escape; ele é trocado por shell no Passo 21.

Na v2.7, §15 (v2.6:2136), localizar o parágrafo (uma linha só) que começa com:

```markdown
**O compose sobe na CI.**
```

e substituir a linha inteira por estes dois parágrafos:

```markdown
**O compose sobe na CI.** Um job próprio executa `docker compose up --wait` até a API ficar `ready` — o que, pelo health check da §14, prova chave, realm e `private_key_jwt` —, registra um tenant e espera o provisionamento chegar a `Active` — o que prova as migrations e o Outbox, que o `ready` não olha —, derruba sem apagar volumes e sobe de novo, provando a idempotência dos one-shots. A promessa "funciona na primeira tentativa" do M0 passa a ser verificada a cada PR, não só no dia em que alguém a testou à mão. **Desde a v2.6, o job também confere o convite do admin do tenant:** depois do `Active`, consulta o mailpit pelo destinatário exato, exigindo ao menos uma mensagem, nunca exatamente uma — a entrega "pelo menos uma vez" pode mandar duas (§11.5) —, lê o campo `Text` da mensagem (porque o JSON escapa o `&` como `%%E1%%` e o HTML traz `&amp;`), exige o prefixo `http://localhost:8081/realms/identity-gateway/login-actions/action-token?key=` e faz um `GET` no link, exigindo `200` com a página de ações, sem a de erro — o que prova que o link abre, e não só o formato dele. O e-mail e o slug são únicos por execução (`admin+<timestamp>@acme.test`), porque e-mail em uso por outra conta é falha permanente (§9.1).

**Desde a v2.7, quem roda a jornada é um app C#, e o token vem do Keycloak.** O job deixou de montar à mão um JWT simétrico e de ler o mailpit com `jq`: chama `tools/jornada-compose.cs`, um app de arquivo único que usa o mesmo harness de login dos testes (§13) — o device flow no client de demonstração, pelas páginas do próprio Keycloak. As fases, na ordem: (1) **o convite do platform-admin sai uma vez só** — a contagem de mensagens para o e-mail dele é **exata**, uma; o one-shot é executado de novo com o convite ainda pendente (`docker compose run --rm --no-deps platform-admin-invite`, saída `0`), e a contagem continua uma. Contar zero depois de um `down` e um `up` seria vacuoso, porque o mailpit volta vazio; (2) **a jornada** — o link do platform-admin, o device flow com login num passo só, a receita HS256 antiga respondendo `401`, `POST /tenants` → `202` → `Active` e o convite do admin do tenant, como acima; (3) **o Keycloak parado** — um `GET` autenticado, `docker compose stop keycloak`, `POST /tenants` → `202` e `Pending`, `docker compose start keycloak`, a renovação do token e a espera do `Active`. É a demonstração nº 1 do README (§16), e trava o cache de metadados na topologia real; fica `stop` e `start`, e não `pause`, porque é a sequência que o README manda fazer; (4) **a segunda subida** sobre os mesmos volumes, com o one-shot saindo `0` e dizendo que o convite já foi enviado. Toda asserção de status é exata, nunca "diferente de `200`": com 5 minutos de token, um `401` por vencimento viraria verde. O workflow roda com `pipefail`, cada passo longo tem o próprio prazo, o app compila antes de o Docker subir, e os endereços discados usam `127.0.0.1` — `localhost` fica só como endereço público do Keycloak. Em falha, o job grava os logs do one-shot, da `api` e do `keycloak` — nunca o do `gateway-keys`, que imprime a senha do master — e, do mailpit, só metadados. O app nunca imprime token, código de dispositivo, link, senha nem HTML.
```

Localizar o parágrafo (uma linha só) que começa com (v2.6:2138):

```markdown
**`offline_access` é removido do `default-roles` do realm.**
```

e substituir a linha inteira por:

```markdown
**`offline_access` fica fora do papel padrão do realm.** O papel vem ligado por padrão para todo usuário, e sessões offline **não** são encerradas por `POST .../users/{id}/logout` — o que permitiria a um usuário desativado continuar renovando acesso depois da revogação (§9.5). O projeto não usa offline tokens. **Entregue na v2.7 (errata E7):** até a v2.6, este parágrafo descrevia a remoção como feita, e o JSON não a tinha. E não é "hardening de uma linha": o import recria o papel no padrão se faltar o papel **ou** o client scope de mesmo nome. O JSON declara os dois — o papel, e o scope sem mappers, fora dos defaults e sem nenhum client que o ofereça —, e faz o mesmo com o papel `uma_authorization`. O papel padrão fica `[manage-account, view-profile]`, e um teste contra o Keycloak real confere que ele não voltou.
```

Localizar o parágrafo (uma linha só) que começa com (v2.6:2140):

```markdown
**Bootstrap do primeiro `platform-admin`.**
```

e substituir a linha inteira por estes dois parágrafos:

```markdown
**Bootstrap do primeiro `platform-admin`: sem senha, por convite (v2.7).** O papel não é atribuível pela API (§11.2) e é exigido por `POST /tenants`, então precisa nascer no bootstrap — mas o `realm-identity-gateway.json` é versionado num repositório público. **O usuário nasce no próprio JSON do realm, sem credencial nenhuma:** `username` e `email` iguais a `${PLATFORM_ADMIN_EMAIL}`, só o papel `platform-admin`, e as ações `UPDATE_PASSWORD` e `VERIFY_EMAIL`. A senha é definida pela própria pessoa, no Keycloak, pelo link de um e-mail de ações — o mesmo mecanismo do convite de qualquer membro (ADR-003). A v2.6 previa gerar uma senha aleatória e exibi-la uma vez no log: uma credencial num log contraria o próprio ADR-003, e isso nunca foi implementado.

Quem dispara o e-mail é o one-shot `platform-admin-invite`, **uma vez só**. Os passos: (0) login no `master`, antes de tudo; (1) o realm tem o scope `gateway-api`? Senão, é um volume anterior à v2.7, e ele sai com `1`; (2) o realm tem o atributo `platformAdminInviteSentAt`? Então o convite já saiu, e ele sai com `0`; (3) busca o usuário pelo e-mail, com `exact=true`, exige exatamente um e confere a forma do bootstrap — só o papel `platform-admin`, nenhum papel de client, nenhum `tenant_id`, nenhum grupo, o e-mail igual ao configurado, em minúsculas; qualquer divergência sai com `1`; (4) sem `UPDATE_PASSWORD` pendente, o convite já foi concluído, e ele sai com `0`; (5) **grava o marcador no realm antes de enviar**, e só então manda o `execute-actions-email`, com `lifespan=14400` (4 horas). **O one-shot nunca atribui papel nem cria usuário:** a primeira versão dele "garantia o papel", e promoveu um `tenant-admin` a `platform-admin`, ao vivo. O marcador é um atributo do realm, e não um arquivo num volume: o arquivo falhou por permissão depois do envio, e cada subida reenviava o convite — o equivalente a um reset periódico de senha. As respostas do `kcadm` são lidas sem `--fields`: com ele, os objetos aninhados vêm vazios, e as travas ficariam vacuosas. O link vale 4 horas porque um link de ações continua trocando a senha da conta até expirar, mesmo depois de o convite ter sido aceito por outro link (§19). O reenvio é só por comando explícito (`docker compose run --rm -e REENVIAR=1 platform-admin-invite`), que pula só a checagem do marcador e recusa rodar se o convite já foi concluído. Um teste de CI falha se o JSON de bootstrap contiver qualquer credencial literal. **Isto é o bootstrap do ambiente local**, como o resto do compose; o bootstrap de produção não está decidido nesta versão.
```

Localizar o último parágrafo da seção (v2.6:2142), a linha que começa com:

```markdown
A documentação interativa usa o suporte nativo a OpenAPI 3.1 do .NET 10
```

e substituir a linha inteira por:

```markdown
A documentação interativa usa o suporte nativo a OpenAPI 3.1 do .NET 10 e a interface Scalar. **O esquema de segurança OAuth2 no documento (Authorization Code com PKCE e Client Credentials) segue pendente (v2.7)**, com destino no M7': até lá, o Scalar não obtém token sozinho, e a demonstração usa o device flow pelo terminal.
```

- [ ] **Passo 18: §16 — a fatia D no andamento; as pendências do M0 revistas; as demonstrações**

Na v2.7, §16, na linha do M0 da tabela de marcos (v2.6:2152), localizar:

```markdown
bootstrap do realm com os dois client scopes e sem `offline_access`, platform-admin com senha gerada,
```

e substituir por:

```markdown
bootstrap do realm com os três client scopes (`gateway-roles`, `gateway-tenant` e `gateway-api`, v2.7) e sem `offline_access`, platform-admin convidado por e-mail, sem senha gerada (v2.7),
```

Na linha do M2 (v2.6:2154), localizar o fim dela:

```markdown
| Suíte de autorização negativa verde **nas três regras de isolamento** |
```

e substituir por:

```markdown
| Suíte de autorização negativa verde **nas três regras de isolamento** — continuam três: na Gateway, a pertença no banco reforça a primeira (v2.7) (D2, planejado) |
```

Localizar (v2.6:2161):

```markdown
**Andamento do M0 e do M1 (v2.6).**
```

e substituir por:

```markdown
**Andamento do M0 e do M1 (v2.7).**
```

Na tabela de fatias (v2.6:2168), localizar a linha da fatia C, a que começa com:

```markdown
| **C · Convite do admin inicial**:
```

e substituir a linha inteira por estas duas (a da fatia C ganha o número do PR; a da D é nova — o número do PR da
D1 entra quando o PR for aberto, não inventar):

```markdown
| **C · Convite do admin inicial**: `EnsureInvitedUserAsync`, o `Member` mínimo, a vaga do admin na ativação, o e-mail pelo SMTP do Keycloak e o `mailpit` no compose | M1 | Entregue (PR #5) |
| **D · Tokens do Keycloak**, em dois PRs. **D1:** a API aceita só access tokens do Keycloak (RS256, emissor, audiência, `azp`, `typ` e `sub`), o realm emite o token da §10.1, o primeiro platform-admin é convidado por e-mail, e a demonstração e a CI obtêm o token pelo device flow. **D2:** `GET /tenants/{tenantId}`, com a policy `TenantAdmin` e a pertença no banco (ADR-011) | M0 + M1 | D1 entregue; D2 (D2, planejado) |
```

Localizar o parágrafo (uma linha só) que começa com (v2.6:2170):

```markdown
**Pendente do M0 depois da fatia A:**
```

e substituir a linha inteira por:

```markdown
**Pendente do M0 depois da fatia D (v2.7):** a tabela de auditoria — que o M0 promete "desde já" —, o armazenamento de eventos do realm e o RabbitMQ. Saíram da lista, entregues pela D1: os client scopes `gateway-roles`, `gateway-tenant` e `gateway-api`, o Audience Mapper, o catálogo de papéis completo, o `offline_access` fora do papel padrão, o primeiro platform-admin (sem senha gerada, por convite), a rotação do refresh token e a API validando tokens do Keycloak. **O critério "primeiro `curl`" do M0, com token do Keycloak, está fechado:** o JWT simétrico do template não existe mais. O armazenamento de eventos custa outro `docker compose down -v` quando entrar, e os eventos de login guardariam o username, que é o e-mail: a retenção é decidida junto.
```

No parágrafo "Pendente do M1 depois da fatia C (v2.6)" (v2.6:2172), localizar o fim dele:

```markdown
e a fatia C deixa prontos para ele o `Member`, `EnsureInvitedUserAsync` com o papel na porta e a `IInvitationPolicy`.
```

e substituir por:

```markdown
e a fatia C deixa prontos para ele o `Member`, `EnsureInvitedUserAsync` com o papel na porta e a `IInvitationPolicy`. Do M1, a fatia D entrega a leitura do próprio tenant, `GET /tenants/{tenantId}` (v2.7) (D2, planejado); a listagem e o override do platform-admin chegam com a auditoria.
```

As demonstrações (v2.6:2176) — localizar a linha que começa com:

```markdown
**A demonstração é por README com `curl`**
```

até a linha que contém (inclusive, a linha inteira):

```markdown
2. **Isolamento multi-tenant:** com token do tenant A
```

Substituir tudo — o parágrafo, a linha em branco e os dois itens numerados — por:

```markdown
**A demonstração é por README com `curl`**, o que promove o M0 a peça crítica — é o primeiro contato do avaliador, e uma falha ali encerra a leitura antes dos ADRs. **Desde a v2.7, ela tem passos no navegador**, e isso é consequência do próprio ADR-003: a senha só é digitada no Keycloak. O avaliador abre o mailpit, conclui o convite pelo link e aprova o código do device flow numa página do Keycloak; todas as chamadas à API continuam sendo `curl`. Duas demonstrações que o README deve conter, porque provam competências difíceis em poucos comandos:

1. **Consistência sem transação distribuída:** obter o token e fazer **um `GET` autenticado antes de parar o Keycloak** — a API guarda as chaves do realm ao validar o primeiro token, e sem isso o pedido seguinte responderia `401` — → `docker compose stop keycloak` → `POST /tenants` responde `202` normalmente, **dentro dos 5 minutos de vida do token** → `docker compose start keycloak` → renovar o token → o tenant vira `Active` sozinho. O job `Compose` da CI roda a mesma sequência (§15).
2. **Isolamento multi-tenant:** com token do tenant A, tentar a rota do tenant B (403), e tentar um `memberId` do tenant B dentro da rota do tenant A (404). **O `403` já é demonstrável com a primeira rota de tenant** (v2.7) (D2, planejado): o admin convidado lê o próprio tenant (`200`) e recebe `403` em qualquer outro. O `404` de sub-recurso chega com as rotas de membro, no M2.
```

- [ ] **Passo 19: §17 e §18 — a exceção nomeada do device flow; a pertença no anti-pattern 7 e no critério de pronto**

Na v2.7, §17, anti-pattern 1 (v2.6:2185), localizar:

```markdown
O login interativo usa sempre Authorization Code com PKCE.
```

e substituir por:

```markdown
O login interativo das aplicações usa sempre Authorization Code com PKCE. **Exceção nomeada (v2.7):** o client de demonstração `identity-gateway-demo`, só do ambiente local, usa o Device Authorization Grant — que também não é ROPC: a senha continua digitada só na página do Keycloak (ADR-003).
```

No anti-pattern 7 (v2.6:2197), localizar o fim dele:

```markdown
cujo acesso é irrestrito por desenho e auditado por chamada.
```

e substituir por:

```markdown
cujo acesso é irrestrito por desenho e auditado por chamada. **Na Gateway, o claim e a rota iguais também não bastam (v2.7) (D2, planejado):** nas rotas de governança, o ator precisa ser `Member` do tenant no banco, porque o `tenant_id` do token é forjável por grupo (ADR-011).
```

Na §18 (v2.6:2210), no item do teste negativo, localizar o fim dele:

```markdown
essa formulação deixava passar sub-recursos, rotas sem `{tenantId}` e clients de plataforma;
```

e substituir por:

```markdown
essa formulação deixava passar sub-recursos, rotas sem `{tenantId}` e clients de plataforma. As regras continuam três: nas rotas de governança da Gateway, o teste da primeira cobre também o ator que tem o claim certo e não é `Member` do tenant no banco (v2.7) (D2, planejado);
```

- [ ] **Passo 20: §19 — os limites da fatia D**

Três itens que já existem são **reescritos**, para não duplicar; os demais entram no fim da lista.

Na v2.7, §19 (v2.6:2235), localizar o item que começa com:

```markdown
- **O `depends_on` do Keycloak na API é conveniência do ambiente local**
```

e substituir a linha inteira por:

```markdown
- **O `depends_on` do Keycloak na API é conveniência do ambiente local**, para que o primeiro `curl` funcione. Ele não é garantia de disponibilidade: a demonstração do M1 exige que a API responda com o Keycloak parado, e o provisionamento é que espera por ele (§9.1). **Desde a v2.7, a `api` depende de um one-shot que depende do Keycloak saudável:** `docker compose up` com o Keycloak parado não sobe a API. A demonstração nº 1 para o Keycloak **depois** do `up`, e não quebra.
```

Localizar o item que começa com (v2.6:2248):

```markdown
- **Um link de convite duplicado continua válido depois do aceite**
```

e substituir a linha inteira por:

```markdown
- **Um link de ações anterior continua válido depois do aceite, e troca a senha de uma conta ativa** (v2.6; confirmado ao vivo e estendido na v2.7). Cada link é de uso único, mas os outros emitidos para o mesmo usuário valem até expirar: com cinco e-mails e o quinto link concluído, o primeiro ainda levou ao formulário de senha, e a senha nova passou a valer. Não reenviar depois do aceite (§11.6, passo 5) não invalida um link enviado antes. Vale para o convite do admin do tenant — link de 7 dias, mais os reenvios do Outbox — **e para o do platform-admin** — no máximo um link automático, de 4 horas, mais os reenvios manuais. Depois do aceite, **revogam de fato:** remover o usuário (no compose, `down -v`), ou desabilitar ou remover a chave HMAC antiga do realm, o que derruba também todos os refresh tokens; rebaixá-la a passiva não basta, porque chave passiva continua verificando. **Só bloqueiam enquanto durarem, e são reversíveis:** desabilitar o usuário (reabilitado, todo link antigo volta a funcionar) e trocar o e-mail (voltar ao antigo ressuscita os links). **O link de 7 dias do convite do admin do tenant é risco aceito nesta versão;** a operação de plataforma que trocar ou reenviar esse convite trata a revogação e avalia encurtar o prazo.
```

Localizar o item que começa com (v2.6:2252):

```markdown
- **E-mail digitado errado entrega o tenant a um estranho**
```

e substituir a linha inteira por:

```markdown
- **E-mail digitado errado entrega o tenant a um estranho**, e não há revogação pela API: o platform-admin não opera rotas de membro (§10.1). O runbook provisório é desabilitar o usuário no Keycloak; uma operação de plataforma para trocar ou reenviar o convite do admin inicial fica para o M1/M2 (§8). **Agravado na v2.7:** com a primeira rota de tenant, o destinatário errado passa a ler o tenant pela API, porque a pertença no banco o reconhece como `Member` (D2, planejado).
```

Localizar o último item da lista, que é também a última linha do arquivo (v2.6:2260), a que começa com:

```markdown
- **Quem perde a corrida de slug no `POST /tenants` recebe `500`, e não `409`**
```

manter, e inserir logo depois dela estes dezessete itens:

```markdown
- **O platform-admin recebe `403` na leitura de tenant** (v2.7) (D2, planejado), até o override virar a policy `TenantReadAccess`, com a auditoria (§10.1). É desvio declarado em relação ao catálogo da §8.
- **O Data Plane continua exposto ao `tenant_id` por grupo** (v2.7, ADR-011). A regra "nenhum grupo" é conferida no JSON do bootstrap; um grupo criado em runtime, inclusive por quem tem a chave da Gateway, não é visto. Decidir entre uma reconciliação que detecte grupos com `tenant_id` e um mapper que não recue é da fatia do Data Plane.
- **A pertença não contém quem tem a chave da Gateway** (v2.7, ADR-011): com o `manage-users`, ele troca a senha ou o e-mail de um `Member` real e passa na policy com a conta dele. A trilha do ataque só existe com os eventos de administração ligados, sem representação.
- **`Invited` passa na checagem de pertença** (v2.7) (D2, planejado), porque o aceite do convite só chega à Gateway pela sincronização do ADR-007. Sai da lista de status aceitos quando o aceite for detectado.
- **O device flow força o consentimento, e é o vetor clássico de phishing de código de dispositivo** (v2.7). Por isso o client público de demonstração fica só no ambiente local, e só na lista de `azp` de Development.
- **`CreateDefaultClientScopes` não é documentado** (v2.7). É o atributo que mantém os scopes embutidos quando o JSON declara `clientScopes` (§12.1); reverificar a cada troca de tag do Keycloak.
- **O `ValidIssuer` do JwtBearer não restringe** (v2.7): com metadados, a biblioteca aceita o emissor anunciado pelo discovery. A Gateway usa um `IssuerValidator` próprio, e o caso está na tabela de diagnóstico da §12.1.
- **Rotação de chave do realm: cada réplica recusa o primeiro pedido com o `kid` novo** (v2.7), porque a recarga dos metadados roda em segundo plano. Runbook: publicar a chave nova como passiva, esperar as réplicas recarregarem (o intervalo automático, 12 horas por padrão, ou um reinício), ativá-la, e manter a antiga habilitada por pelo menos a vida do token mais o `ClockSkew`.
- **Keycloak fora com metadados frios responde `401`** (v2.7), o que aponta o sintoma para o token, e não para a dependência, contra o argumento da §9.6; o `503` fica registrado como alternativa (§14). O `/health/ready` cobre o Keycloak inteiro fora do ar, mas não um `jwks_uri` inalcançável.
- **O one-shot do convite faz login no `master` a cada subida, antes de ler o marcador** (v2.7): trocar a senha ou apagar o admin do `master` do compose quebra toda subida seguinte, e a `api` não sobe, mesmo com o convite concluído há semanas. Não trocar nem apagar esse admin; se mudou, `docker compose down -v` (§15).
- **`PLATFORM_ADMIN_EMAIL` fica fixado no primeiro import** (v2.7, `IGNORE_EXISTING`). Sem "esqueci a senha" no realm, a recuperação do platform-admin só existe pelo console do `master`. O bootstrap de produção não está decidido.
- **O marcador do convite vive no realm** (v2.7): se o usuário for recriado à mão num realm que já tem o marcador, só o reenvio manual envia. O mesmo vale para um envio que falhou — o marcador já estava gravado, a subida seguinte sai `0` sem e-mail, e o convite só sai pelo comando de reenvio.
- **A lista de `azp` é estática** (v2.7): vale para os clients interativos que chamam a Gateway. Clients M2M de tenant não entram nela nem recebem `gateway-api`, e um client de tenant chamando a Gateway, no M6, exige decisão nova (§10.1). Fora de Development, a lista fica vazia até existir um client administrativo.
- **Reusar um refresh token derruba a sessão do client** (v2.7): depois do reuso, até o refresh token novo é recusado. Nenhuma renovação pode ser repetida automaticamente; se falhar, o caminho é um login novo (§10.3).
- **Volume do compose anterior à v2.7 exige `docker compose down -v`**, pela quarta vez na história do projeto: scopes, clients, catálogo e o usuário do bootstrap só entram no primeiro import (§15).
- **O mailpit local não tem autenticação** (v2.7), em `127.0.0.1:8025`: o link do platform-admin toma a conta enquanto não expira, 4 horas.
- **Não verificado na v2.7:** o cache de metadados do JwtBearer além de ~9 minutos com o Keycloak fora — o que foi observado: tokens de `kid` conhecido aceitos durante 8 min 8 s de Keycloak pausado, e depois com ele parado —; e se o mailpit valida o cabeçalho `Host` contra *DNS rebinding*.
```

- [ ] **Passo 21: Errata E1 — gravar a sequência de escape por shell**

Os Passos 1 e 17 deixaram dois marcadores `%%E1%%` na v2.7: um na lista de erratas da §0 e um no parágrafo da CI,
na §15. Trocar os dois pela sequência de escape (barra invertida seguida de `u0026`) **sem passar pelo `Edit`**: o
comando abaixo monta a barra invertida com `chr(92)`, e por isso a sequência não aparece escrita nem no comando.

Run: `grep -c '%%E1%%' docs/especificacao-arquitetural-v2.7.md`
Expected: `2`.

Run:

```bash
perl -pi -e 'BEGIN { $e = chr(92) . "u0026" } s/%%E1%%/$e/g' docs/especificacao-arquitetural-v2.7.md
```

Run: `grep -c '%%E1%%' docs/especificacao-arquitetural-v2.7.md`
Expected: `0`.

Run: `grep -c 'u0026' docs/especificacao-arquitetural-v2.7.md`
Expected: `2`.

Run: `grep -cF -- "$(printf '\134u0026')" docs/especificacao-arquitetural-v2.7.md`
Expected: `2` — as duas linhas trazem a barra invertida antes do `u0026` (o `printf '\134'` escreve a barra, de novo
sem que a sequência passe por uma ferramenta de edição).

Run: `grep -cF -- "$(printf '\134u0026')" docs/superpowers/specs/2026-09-30-tokens-keycloak-design.md`
Expected: `1` — a linha da errata E1, na §9 da spec de design. Se der `0`, a sequência se perdeu lá também: corrigir
a linha da errata E1 pelo mesmo caminho (um `Edit` que ponha o marcador `%%E1%%` no lugar do `&` solto, depois o
`perl`), e incluir o arquivo no commit. O `.github/workflows/ci.yml` tinha a sequência num comentário do bloco em
`jq`, que a Tarefa 11 tirou do YAML: lá, a contagem pode ser `0`, e não é erro.

Run: `git diff --stat -- docs/especificacao-arquitetural-v2.6.md`
Expected: vazio. A v2.6 fica intocada, com a frase errada: a correção é da v2.7.

Daqui em diante, **nenhum `Edit` nas duas linhas que têm a sequência** (a da errata E1, na §0, e o parágrafo "O
compose sobe na CI", na §15): um `Edit` que as reescreva decodifica a sequência de novo. Se for preciso mexer nelas,
repita o caminho — marcador e `perl`.

- [ ] **Passo 22: Conferir a v2.7**

Run: `grep -n "^## 0" docs/especificacao-arquitetural-v2.7.md`
Expected, nesta ordem: `## 0. O que mudou da v2.6 para a v2.7`, `## 0.1. … v2.5 para a v2.6`,
`## 0.2. … v2.4 para a v2.5`, `## 0.3. … v2.3 para a v2.4`, `## 0.4. … v2.2 para a v2.3`,
`## 0.5. … v2.1 para a v2.2`, `## 0.6. … v2.0 para a v2.1`.

Run: `grep -c "(D2, planejado)" docs/especificacao-arquitetural-v2.7.md`
Expected: `33` — é a lista que a Tarefa 17 vai fechar. Um número menor é marca reescrita ou edição pulada: achar
com `grep -n "planejad" docs/especificacao-arquitetural-v2.7.md` e corrigir.

Run: `grep -n "é removido do .default-roles.\|com senha gerada\|Pendente do M0 depois da fatia A\|Andamento do M0 e do M1 (v2.6)\|único override, auditado\|tipo Default\|com os dois client scopes\|O login interativo usa sempre" docs/especificacao-arquitetural-v2.7.md`
Expected: nenhuma linha. (Cobre o `offline_access` descrito como já removido, o platform-admin com senha gerada, as
pendências antigas do M0, a marca "(v2.6)" do andamento, a linha única do catálogo da §8, o scope criado como
default, os "dois client scopes" do M0 e o "sempre PKCE" sem a exceção.)

Run: `grep -n "senha gerada\|senha aleatória" docs/especificacao-arquitetural-v2.7.md`
Expected: quatro linhas, todas dizendo que a senha gerada **saiu** — o item T9 da §0, o parágrafo "Bootstrap do
primeiro `platform-admin`" da §15, a linha do M0 e o parágrafo "Pendente do M0" da §16.

Run: `grep -n "PlatformAdminOverrideHandler\|GetClientCredentialsTokenAsync\|GetTokenForUserAsync\|tenant-a/members" docs/especificacao-arquitetural-v2.7.md`
Expected: cinco linhas, todas de errata — E3 e E8 na §0, o item do override na §10.1, a citação "Erratas da v2.7 (E2
e E8)" na §12.1 e o comentário "Errata E8" no teste da §12.2. Nenhuma chamada a esses métodos e nenhum
`PlatformAdminOverrideHandler` em código.

Run: `grep -n "defaultDefaultClientScopes" docs/especificacao-arquitetural-v2.7.md`
Expected: quatro linhas, todas dizendo que os scopes ficam **fora** dele ou citando o que a v2.6 dizia (E5 e o
parágrafo seguinte, na §12.1; o parágrafo do `gateway-tenant`, na §12.2; a errata E7, na §15). Nenhum JSON com a
chave.

Run: `grep -n "fatia E\|fatia F\|fatia G\|fatia H" docs/especificacao-arquitetural-v2.7.md`
Expected: nenhuma linha. A v2.7 não nomeia as fatias seguintes: a sequência é proposta do design, não norma.

Run: `grep -c "especificacao-arquitetural-v2" docs/especificacao-arquitetural-v2.7.md`
Expected: `0` — a especificação não aponta para outra versão dela.

Run: `grep -c "v2\.7" docs/especificacao-arquitetural-v2.7.md`
Expected: `121` linhas (cada mudança marcada). Um número diferente é edição pulada ou repetida.

Se algum grep mostrar sobra, corrigir com o texto da v2.7 deste roteiro e repetir.

- [ ] **Passo 23: Documento de negócio 1.4, alinhado à v2.7**

Arquivo: `docs/documentacao-negocio.md`. Os números de linha são os da versão atual da branch e servem só de
referência; o `Edit` casa pelo texto. Os blocos Mermaid seguem o estilo do documento: texto entre aspas, sem acento.
O que só a D2 entrega leva a marca `(D2, planejado)`, como na v2.7. O documento explica nove ADRs e não ganha ficha
para o ADR-010 nem para o ADR-011: as regras novas citam o ADR-011 pelo número, como já citam os demais.

**23.1 — Cabeçalho, sumário e rastreabilidade.**

Localizar (linha 3):

```markdown
> **Versão:** 1.3 · **Data:** 2026-09-30
```

e substituir por (com a data do dia, `date +%F`, no lugar de `AAAA-MM-DD`):

```markdown
> **Versão:** 1.4 · **Data:** AAAA-MM-DD
```

Localizar (linha 4):

```markdown
> **Fonte da verdade:** [`especificacao-arquitetural-v2.6.md`](especificacao-arquitetural-v2.6.md)
```

e substituir por:

```markdown
> **Fonte da verdade:** [`especificacao-arquitetural-v2.7.md`](especificacao-arquitetural-v2.7.md)
```

Localizar a linha que começa com (linha 5):

```markdown
> **Estado do projeto:** implementação em andamento
```

e substituir a linha inteira por:

```markdown
> **Estado do projeto:** implementação em andamento — registro de tenant, fundação Keycloak, consumidor do provisionamento, convite do admin inicial e tokens do Keycloak (primeira parte, D1) entregues.
```

Localizar a linha que começa com (linha 7):

```markdown
> **Nota da versão 1.3.**
```

e inserir **antes** dela (a última linha do trecho é a linha de citação vazia, só com `>`, que separa as duas notas):

```markdown
> **Nota da versão 1.4.** Alinha à v2.7 os trechos que a fatia D (tokens do Keycloak) tornou falsos: a API passa a
> aceitar só tokens do Keycloak; o primeiro platform-admin nasce **sem senha**, convidado por e-mail, e não com
> senha gerada e exibida em log; a demonstração obtém o token pelo device flow, com passos no navegador; e o
> override do platform-admin na leitura de tenant fica adiado, como autorização própria. Entram duas regras, a
> RN-028 (pertença do ator ao tenant) e a RN-029 (separação de funções). O que só a segunda parte da fatia
> entrega — a rota `GET /tenants/{tenantId}` e as regras que a protegem — está marcado **"(D2, planejado)"**. As
> citações `§N` continuam válidas.
>
```

Localizar (linha 24):

```markdown
remete a uma seção da spec vigente (v2.6),
```

e substituir por:

```markdown
remete a uma seção da spec vigente (v2.7),
```

No sumário (linha 50), localizar:

```markdown
27 regras transversais (RN-001..RN-027)
```

e substituir por:

```markdown
29 regras transversais (RN-001..RN-029)
```

**23.2 — Personas: o platform-admin e o tenant-admin.**

Na persona do platform-admin, em "O que faz" (linha 122), localizar:

```markdown
consulta qualquer tenant e o status do provisionamento;
```

e substituir por:

```markdown
consulta o status do provisionamento de qualquer tenant (a leitura do tenant em si, `GET /tenants/{tenantId}`, responde `403` a ele **temporariamente**, até existir o override auditado — §10.1) (D2, planejado);
```

Em "O que NÃO pode" (linha 128), localizar o item que começa com:

```markdown
- Ser criado pela API: o papel **não é atribuível**
```

e substituir a linha inteira por estes dois itens:

```markdown
- Ser criado pela API: o papel **não é atribuível** pela `RoleAssignmentPolicy` e nasce no bootstrap do realm, **sem senha** — a pessoa recebe um e-mail de convite, uma vez, e define a própria senha no Keycloak pelo link, que vale 4 horas. Nenhuma senha é gerada nem exibida em log (§11.2, §15, achado C13; v2.7).
- **Agir como tenant-admin.** Uma conta que traga `platform-admin` é negada nas rotas de tenant, mesmo que acumule o papel `tenant-admin`: é a separação de funções (RN-029) (D2, planejado).
```

Na persona do tenant-admin (linha 134), localizar o parágrafo (uma linha só) que começa com:

```markdown
**O que faz, sempre dentro do próprio tenant:** convida membros,
```

manter, e inserir logo depois, separado por uma linha em branco:

```markdown
**A primeira rota dele já tem dono (v2.7) (D2, planejado).** `GET /tenants/{tenantId}` devolve ao administrador o próprio tenant — nome, slug, status, plano, vagas ocupadas e data de registro, nunca o e-mail. Quatro condições a protegem, e todas precisam valer: o token traz o papel `tenant-admin`; **não** traz `platform-admin`; o `tenant_id` do token é o da rota; e a pessoa é membro daquele tenant no banco da Gateway (RN-001, RN-028, RN-029). Qualquer outra combinação recebe `403`.
```

**23.3 — F-02: sem o "segundo handler"; `403` no lugar do `404`.**

Na ficha F-02 (linha 537), localizar a linha que começa com:

```markdown
| **Ator** | `platform-admin` (lista completa e `GET /tenants/{tenantId}/provisioning`)
```

e substituir a linha inteira por:

```markdown
| **Ator** | `platform-admin`: o status do provisionamento (`GET /tenants/{tenantId}/provisioning`); a lista completa chega com a auditoria. `tenant-admin`: apenas o próprio tenant, por `GET /tenants/{tenantId}` (§8) (D2, planejado). |
```

Localizar a linha que começa com (linha 539):

```markdown
| **Regras de negócio** | O `tenant-admin` só enxerga o próprio tenant
```

e substituir a linha inteira por:

```markdown
| **Regras de negócio** | O `tenant-admin` só enxerga o próprio tenant, e quatro condições precisam valer juntas: o papel `tenant-admin`, a ausência de `platform-admin` (RN-029), o tenant do token igual ao da rota (RN-001) e a pertença do ator ao tenant no banco da Gateway (RN-028) (D2, planejado). **O `platform-admin` recebe `403` nesta rota, temporariamente** (v2.7): o acesso dele à carteira de clientes será uma autorização própria, entregue junto com a trilha de auditoria — e não uma segunda verificação que "aprova por cima" da de tenant, que nunca funcionaria, porque a negação explícita da RN-001 veta qualquer aprovação alheia (§10.1). A resposta traz exatamente os campos do contrato, e nunca o e-mail do administrador inicial. |
```

Localizar a linha que começa com (linha 541):

```markdown
| **Erros de negócio** | Tenant inexistente ou fora do escopo do ator
```

e substituir a linha inteira por:

```markdown
| **Erros de negócio** | Tenant de outro cliente, **tenant inexistente**, ator sem o papel, ator que não é membro e `platform-admin`: todos `403`, com a mesma resposta, sem nada que distinga o motivo (v2.7) (D2, planejado). A rota não usa `404`: não há membro num tenant que não existe, e a negação acontece antes de qualquer consulta ao tenant — uma resposta diferente para "não existe" diria, a quem não é do tenant, quais identificadores existem. O `404` continua valendo para **sub-recurso** de outro tenant (RN-003). |
```

**23.4 — Regras: RN-001, as duas novas (RN-028 e RN-029), RN-010, RN-011, RN-019 e RN-025.**

Na abertura da seção 4.1, "Isolamento entre tenants — as três regras" (linha 966), localizar a última linha do
parágrafo:

```markdown
princípio que ela deveria provar é pior que nenhuma suíte, porque produz confiança.
```

manter, e inserir logo depois, separado por uma linha em branco:

```markdown
As regras continuam três na v2.7. A pertença do ator ao tenant no banco (RN-028) reforça, na
Gateway, a primeira delas — não é uma quarta regra (D2, planejado).
```

Na RN-001 (linha 975), localizar as duas últimas linhas:

```markdown
apenas deixa de aprovar não é *fail closed*: o projeto registra um segundo handler para o
`platform-admin`, e só uma negação explícita sobrevive a uma aprovação alheia (C11).
```

e substituir por estas quatro:

```markdown
apenas deixa de aprovar não é *fail closed*: outra verificação registrada para o mesmo requisito
poderia aprová-lo, e só uma negação explícita sobrevive a uma aprovação alheia (C11). Na v2.7, a
comparação é por identificador, não por texto, e o token precisa trazer **um** `tenant_id` só:
dois valores são recusados, qualquer que seja a ordem (§10.1) (D2, planejado).
```

No fim da RN-004 (linha 1004), localizar a última linha dela:

```markdown
`private_key_jwt`, rotação documentada e **auditoria por chamada**.
```

manter, e inserir logo depois dela, separadas por uma linha em branco, as duas regras novas (elas ficam antes da
linha `---` que fecha a seção 4.1):

```markdown
**RN-028 — O ator precisa ser membro do tenant no banco da Gateway.** (D2, planejado)
Nas rotas de governança de tenant, o token certo não basta: quem chama precisa ser `Member`
daquele tenant no banco da Gateway, em status `Invited` ou `Active` (ADR-011, §10.1). Todo outro
status nega — `Deactivated`, `Expired`, `Revoked`, `Erased` e qualquer um que venha a existir. O
motivo: o `tenant_id` do token pode ser fabricado no Keycloak, por um grupo com esse atributo,
por quem tiver a chave da Gateway; a pertença no banco não depende do Keycloak. **Não é uma
quarta regra de isolamento:** é a RN-001 conferida em duas fontes, o token e o banco. A pertença
só é consultada nas rotas de governança, nunca por requisição de negócio (RN-025). `Invited` é
aceito porque o aceite do convite ainda não chega à Gateway (ADR-007).
*Quando violada:* `403 Forbidden`, igual ao de qualquer outra negação da rota. *Limite declarado:*
a regra não contém quem tem a chave da Gateway e toma a conta de um membro real (§19).

**RN-029 — Separação de funções: conta de plataforma não age como tenant-admin.** (D2, planejado)
Um token que traga o papel `platform-admin` é negado nas rotas de tenant, mesmo que traga também
`tenant-admin` e o `tenant_id` certo (§10.1). Sem a regra, o `403` temporário do platform-admin
na leitura de tenant só valeria para a conta que não acumula papéis.
*Quando violada:* `403 Forbidden`.
```

Na RN-010 (linha 1071), localizar a linha:

```markdown
A hierarquia é `platform-admin` > `tenant-admin` > `financial-manager` > `reader` (§6.3, §11.2).
```

e substituir por estas três:

```markdown
A hierarquia é `platform-admin` > `tenant-admin` > `financial-manager` > `reader` (§6.3, §11.2).
**Ela é teto de atribuição, não herança de acesso** (v2.7): estar acima limita o que o ator pode
conceder, e não lhe dá o que o papel de baixo acessa (RN-029).
```

Na RN-011 (linha 1075), localizar a linha:

```markdown
Ele nasce exclusivamente no bootstrap do realm, com senha gerada aleatoriamente, exibida uma
```

até a linha que contém (inclusive, a linha inteira):

```markdown
de bootstrap contiver qualquer credencial literal.
```

Substituir as três linhas por estas seis:

```markdown
Ele nasce exclusivamente no bootstrap do realm, **sem senha** (v2.7): o usuário vem no arquivo do
realm só com o papel, e a pessoa recebe **um** e-mail de convite, com link de 4 horas, pelo qual
define a senha no Keycloak — o mesmo mecanismo do convite de qualquer membro (§15, C13). Nenhuma
senha é gerada nem exibida em log, e o processo que envia o convite nunca atribui o papel a uma
conta que já exista. Um teste de CI falha se o JSON de bootstrap contiver qualquer credencial
literal.
```

Na RN-019 (linha 1145), localizar a linha:

```markdown
retenção deles. O e-mail também não vai a log, mensagem de erro nem resposta.
```

e substituir por estas três:

```markdown
retenção deles. O e-mail também não vai a log, mensagem de erro nem resposta. **O token também
não carrega e-mail nem nome** (v2.7): as aplicações que chamam a Gateway recebem um token só com o
identificador (`sub`), os papéis e o tenant, e é o `sub` que aparece nos logs (§10.3).
```

Na RN-025 (linha 1186), localizar o fim da terceira linha:

```markdown
ADR-002, anti-pattern 2).
```

e substituir por (o texto continua na linha seguinte):

```markdown
ADR-002, anti-pattern 2). A pertença no banco (RN-028) não muda isso: ela é consultada só nas
rotas de governança da própria Gateway, nunca por uma API de negócio (ADR-011) (D2, planejado).
```

**23.5 — Matriz de permissões: F-02 do platform-admin e as notas 1 e 9.**

Na matriz (linha 1198), localizar a linha da F-02:

```markdown
| **F-02** Consultar tenant / status de provisionamento | ✅ | ⚠️ ¹ | ❌ | ❌ |
```

e substituir por:

```markdown
| **F-02** Consultar tenant / status de provisionamento | ⚠️ ⁹ | ⚠️ ¹ | ❌ | ❌ |
```

Nas notas (linha 1224), localizar a nota 1:

```markdown
1. ⚠️ **Somente o próprio tenant** (§8). Garantido pela RN-001.
```

e substituir por estas duas linhas:

```markdown
1. ⚠️ **Somente o próprio tenant** (§8). Garantido pela RN-001 e, na Gateway, pela pertença do
   ator ao tenant no banco (RN-028) e pela separação de funções (RN-029) (D2, planejado).
```

Localizar a última linha da nota 8 (linha 1244):

```markdown
   sempre o mesmo formato, para não permitir enumeração de tenants (§9.2).
```

manter, e inserir logo depois dela a nota 9:

```markdown
9. ⚠️ **Temporário** (v2.7) (D2, planejado): o `platform-admin` consulta o status do provisionamento
   de qualquer tenant, mas a leitura do tenant (`GET /tenants/{tenantId}`) responde `403` a ele até
   existir o override auditado, que chega com a trilha de auditoria (§10.1).
```

**23.6 — Roadmap, demonstrações e a decisão 7.7.**

No roadmap (linha 1280), localizar a linha:

```markdown
**As duas demonstrações que o README precisa conter**, porque provam competências difíceis em
```

até a linha que contém (inclusive, a linha inteira):

```markdown
   RN-003, lado a lado — a segunda é a que a maioria dos projetos não testa.
```

Substituir tudo — o parágrafo e os dois itens numerados — por:

```markdown
**As duas demonstrações que o README precisa conter**, porque provam competências difíceis em
poucos comandos (§16). Desde a v2.7, elas têm **passos no navegador** — abrir o e-mail de convite,
definir a senha e aprovar o código do dispositivo no Keycloak —, porque a senha só é digitada lá
(ADR-003); as chamadas à API continuam sendo `curl`.

1. **Consistência sem transação distribuída.** Obter o token e fazer **uma chamada autenticada
   antes** de parar o Keycloak (a API guarda as chaves do realm no primeiro token que valida) →
   parar o Keycloak → `POST /tenants` responde `202` normalmente, dentro dos 5 minutos de vida do
   token → subir o Keycloak → renovar o token → o tenant vira `Active` sozinho.
2. **Isolamento multi-tenant.** Com token do tenant A, tentar a rota do tenant B (`403`); e
   tentar um `memberId` do tenant B **dentro da rota do tenant A** (`404`). São a RN-001 e a
   RN-003, lado a lado — a segunda é a que a maioria dos projetos não testa. O `403` já é
   demonstrável com a primeira rota de tenant (D2, planejado); o `404` chega com as rotas de
   membro, no M2.
```

Na decisão 7.7 (linha 471), localizar a linha que começa com:

```markdown
| **Decisão** | O cenário de demonstração é um README com comandos `curl`
```

e substituir a linha inteira por:

```markdown
| **Decisão** | O cenário de demonstração é um README que qualquer avaliador percorre sozinho: as chamadas à API são comandos `curl`, e a identidade é provada no Keycloak, pelo navegador — abrir o e-mail de convite, definir a senha e aprovar o código do dispositivo (§0, §16; v2.7) |
```

Localizar a linha que começa com (linha 473):

```markdown
| **Por quê** | `curl` é verificável por quem lê
```

e substituir a linha inteira por:

```markdown
| **Por quê** | `curl` é verificável por quem lê, sem depender do autor estar presente. Vídeo e conversa não são auditáveis. Os passos no navegador não enfraquecem isso: são consequência do ADR-003 — a senha só é digitada no Keycloak —, e a alternativa seria um atalho de login por senha, que o projeto proíbe. O job de CI percorre a mesma jornada a cada PR |
```

Localizar a linha que começa com (linha 475):

```markdown
| **O que o README precisa provar** |
```

e substituir a linha inteira por:

```markdown
| **O que o README precisa provar** | Duas demonstrações, porque provam competências difíceis em poucos comandos: (1) **consistência sem transação distribuída** — um `GET` autenticado, `docker compose stop keycloak` → `POST /tenants` responde `202` normalmente, dentro dos 5 minutos do token → `docker compose start keycloak` → o tenant vira `Active` sozinho; (2) **isolamento multi-tenant** — com token do tenant A, rota do tenant B responde 403, e um `memberId` do tenant B dentro da rota do tenant A responde 404. O 403 já é demonstrável com a primeira rota de tenant (D2, planejado), e o 404 chega no M2 (§16) |
```

**23.7 — A seta (2) dos diagramas ganha o device flow de demonstração.**

No diagrama de contexto C4 da Parte I (linha 194), localizar:

```text
    APP -->|"2. login OIDC: PKCE ou Client Credentials"| KC
```

e substituir por:

```text
    APP -->|"2. login OIDC: PKCE ou Client Credentials; device flow so na demonstracao local"| KC
```

No diagrama de contexto da Parte III (linha 1411), localizar:

```text
    APP -->|"(2) Login OIDC: PKCE ou Client Credentials"| KC
```

e substituir por:

```text
    APP -->|"(2) Login OIDC: PKCE ou Client Credentials; device flow so na demonstracao local"| KC
```

Na tabela das setas, logo abaixo (linha 1425), localizar a linha que começa com:

```markdown
| (2) | Authorization Code + PKCE (interativo) ou Client Credentials (M2M), **direto no Keycloak** |
```

e substituir a linha inteira por:

```markdown
| (2) | Authorization Code + PKCE (interativo) ou Client Credentials (M2M), **direto no Keycloak**. Só no ambiente local, a demonstração usa o Device Authorization Grant, num client próprio — também direto no Keycloak (v2.7, ADR-003) | A Gateway não vê senha nem token |
```

Depois do diagrama de containers da Parte IV (linha 2342), localizar:

```markdown
É essa ausência que o ADR-002 protege.
```

e substituir por:

```markdown
É essa ausência que o ADR-002 protege. Na demonstração local, a seta **2** é o device flow de um client de demonstração, também direto no Keycloak (v2.7).
```

**23.8 — Limites: a seção 7.3 e o resumo "Limites conhecidos (§19)".** O resumo fica na Parte IV, em "Qualidade e
verificabilidade", e não no apêndice.

No fim da seção 7.3 (linha 1377), localizar a última linha do item "Do encerramento":

```markdown
  permanece reservado.
```

manter, e inserir logo depois dela, separados por uma linha em branco, estes dois grupos (eles ficam antes da linha
`---` que fecha a Parte II):

```markdown
**Do provisionamento e do convite (§19)**

- **`ProvisioningFailed` não tem saída automática:** até existirem o retry manual e a
  reconciliação, sair dele exige intervenção.
- **O e-mail de convite pode sair mais de uma vez**, quando algo falha entre o envio e a gravação
  no banco — é o preço da entrega "pelo menos uma vez".
- **O administrador inicial fica `Invited` na Gateway mesmo depois de aceitar o convite**, até a
  sincronização de eventos do Keycloak existir (M4). A expiração de convite não pode chegar antes
  dela.
- **Admin órfão:** se o e-mail sai e o provisionamento desiste antes de gravar, fica no Keycloak
  um usuário com o papel de administrador e link válido, de um tenant que falhou.
- **A mesma pessoa não administra dois tenants**, e um tenant registrado antes do convite por
  e-mail só sai da falha pelo retry manual.
- **Quem tem a chave da Gateway tem muito poder no Keycloak:** atribui papéis, inclusive o de
  plataforma, e troca senhas. O acesso que ele criar sobrevive à rotação da chave.
- **Rotacionar a chave da Gateway causa indisponibilidade**, até a chave ser publicada por JWKS.
- **O e-mail apagado do banco não some de imediato do disco:** cópias de segurança e registros
  internos guardam o valor pela retenção deles (RN-019).

**Da autenticação e do bootstrap (v2.7, §19)**

- **O `platform-admin` não lê um tenant pela API, por enquanto.** `GET /tenants/{tenantId}`
  responde `403` a ele até existir o override auditado (D2, planejado).
- **A pertença no banco tem um limite declarado** (RN-028): ela não contém quem tem a chave da
  Gateway e toma a conta de um membro real, trocando a senha ou o e-mail dele. E as APIs de
  negócio não têm essa checagem: confiam no token e na regra de que o realm não tem grupos.
- **Um link de convite antigo continua valendo até expirar, mesmo depois do aceite**, e troca a
  senha da conta. Por isso o link do platform-admin vale só 4 horas e é enviado uma vez; o link
  de 7 dias do administrador do tenant é risco aceito, a tratar pela operação que trocar ou
  reenviar esse convite.
- **E-mail digitado errado no registro do tenant** entrega o tenant a quem o recebe, e agora essa
  pessoa também lê o tenant pela API (D2, planejado).
- **Com o Keycloak fora do ar e a API recém-iniciada, um pedido com token responde `401`**, e não
  `503`: o sintoma aponta para o token, não para a dependência. Depois de validar o primeiro
  token, a API segue validando com o Keycloak parado.
- **Renovar o acesso duas vezes com o mesmo refresh token derruba a sessão:** o segundo uso é
  recusado, e depois dele o token novo também. O caminho é um login novo.
- **O ambiente local depende do administrador do Keycloak criado pelo compose:** trocar a senha
  dele ou apagá-lo impede a subida seguinte. E um ambiente criado antes desta versão precisa ser
  recriado (`docker compose down -v`).
- **O client de demonstração só existe no ambiente local.** O device flow é o vetor clássico de
  phishing de código de dispositivo, e por isso a API só aceita esse client em desenvolvimento.
```

No resumo "Limites conhecidos (§19) — a seção que mais credibilidade dá" (linha 2751), localizar o parágrafo (uma
linha só) que começa com:

```markdown
A spec declara onze limites reais
```

e substituir a linha inteira por:

```markdown
A §19 da spec declara os limites reais, e a lista cresce a cada fatia, porque cada uma devolve a ela o que a execução encontrou. Entre eles: política de senha igual para todos os tenants; um usuário pertence a um único tenant, com o custo comercial nomeado; a queda do Keycloak degrada o Data Plane em até 5 minutos; a desativação de um membro não invalida o token já emitido; a sincronização pode perder eventos sob indisponibilidade prolongada; há uma janela sem `tenant_id` no primeiro login federado; um link de convite antigo continua trocando a senha até expirar; quem tem a chave da Gateway pode tomar a conta de um membro real, e a checagem de pertença não o contém; o platform-admin fica, por enquanto, sem ler um tenant pela API (D2, planejado); e, com o Keycloak fora e a API recém-iniciada, a resposta é `401`, e não `503` (v2.7).
```

**23.9 — ADR-003, "Por que a Gateway nunca vê uma senha" e a checagem adversarial.**

Na ficha do ADR-003 (linha 2380), localizar a linha que começa com:

```markdown
| **Alternativa rejeitada** | Um `POST /auth/login` "de conveniência" na Gateway.
```

e substituir a linha inteira por:

```markdown
| **Alternativa rejeitada** | Um `POST /auth/login` "de conveniência" na Gateway. Rejeitado inclusive **nos testes** — e a revisão registrou isso como o ponto que faz a decisão valer, porque o ROPC costuma voltar pela porta dos testes. **Dito com precisão (v2.7):** até a v2.6, a suíte obtinha um token de usuário por senha, num client criado só para um teste; isso saiu. Hoje nenhum client declarado no realm aceita login por senha, e o único que o Keycloak cria sozinho com essa opção emite um token que a Gateway recusa — há teste |
```

Localizar a linha que começa com (linha 2381):

```markdown
| **Custo aceito** | Nenhum fluxo de login pode ser simplificado para demonstração
```

e substituir a linha inteira por:

```markdown
| **Custo aceito** | Nenhum fluxo de login pode ser simplificado **a ponto de a senha passar pela API**: a demonstração tem passos no navegador, porque a senha só é digitada no Keycloak. Toda aplicação integra pelo Authorization Code com PKCE. **Exceção de demonstração (v2.7):** um client público, só do ambiente local, usa o device flow — o avaliador aprova um código numa página do Keycloak, e o terminal recebe o token. Os testes e a CI fazem o mesmo caminho, por um programa que preenche as páginas do próprio Keycloak com uma senha que ele mesmo definiu: a credencial nunca passa pela Gateway, e isso não é ROPC |
```

Em "Por que a Gateway nunca vê uma senha" (linha 2497), localizar a última frase do parágrafo:

```markdown
O ROPC, único fluxo que colocaria a senha dentro da API, é o anti-pattern nº 1 (§17) e está proibido inclusive no realm de testes.
```

e substituir por:

```markdown
O ROPC, único fluxo que colocaria a senha dentro da API, é o anti-pattern nº 1 (§17). **Com precisão (v2.7):** nenhum client declarado no realm aceita login por senha; o único que o Keycloak cria sozinho com essa opção emite um token que a Gateway recusa; e a suíte de testes, que até a v2.6 usava esse atalho num client temporário, deixou de usá-lo. A demonstração e os testes obtêm o token pelo device flow, em que a senha continua sendo digitada só no Keycloak.
```

Em "O que a revisão NÃO conseguiu atacar" (linha 2602), localizar o item que começa com:

```markdown
- **ROPC (ADR-003):** procurados os três caminhos
```

e substituir a linha inteira por:

```markdown
- **ROPC (ADR-003):** procurados os três caminhos por onde uma senha encostaria na Gateway. Nenhum existe. O terceiro — realm de teste com ROPC ligado "para facilitar a suíte" — é o que importa, porque é por onde o ROPC volta na prática, e a spec o fecha explicitamente. *"Antecipar o atalho de teste é o que faz a decisão valer."* **A execução mostrou que a frase precisava ser mais exata (v2.7):** a suíte chegou a obter um token de usuário por senha, num client criado em runtime só para um teste, e o realm tem um client embutido com essa opção. Nenhum dos dois encostava uma senha na Gateway — o que a revisão procurou continua não existindo —, mas "ROPC desligado no realm de teste" era falso. A v2.7 tirou o atalho da suíte e passou a provar, por teste, que nenhum token obtido por senha é aceito pela Gateway.
```

**23.10 — Modelo de segurança: a pertença como reforço da regra 1; o override adiado, com a negação intacta.**

Em "Como o isolamento multi-tenant é garantido — e verificado" (linha 2509), localizar a linha:

```markdown
**O override do `platform-admin` é estreito e auditado** (§10.1, I-3).
```

até a linha que contém (inclusive, a linha inteira):

```markdown
entrada de auditoria.
```

Substituir o parágrafo inteiro — as cinco linhas, de "**O override do `platform-admin`…" a "entrada de
auditoria." — por estes dois (o parágrafo seguinte, "O limite não é zelo excessivo…", fica como está):

```markdown
**Na Gateway, a regra 1 é conferida em duas fontes (v2.7) (D2, planejado).** O `tenant_id` do token pode
ser fabricado no Keycloak por quem tiver a chave da Gateway — por um grupo com esse atributo. Por isso,
nas rotas de governança, além do token, o ator precisa ser membro do tenant no banco da Gateway (RN-028),
e uma conta de plataforma não age como tenant-admin (RN-029). As regras continuam três: a pertença
reforça a primeira, não é uma quarta.

**O override do `platform-admin` é estreito e auditado — e está adiado** (§10.1, I-3; v2.7). O provedor
da plataforma precisa enxergar a própria carteira de clientes, mas **só** em `GET /tenants` e
`GET /tenants/{tenantId}`, e **em nenhuma rota interna do tenant**: membros, clients, permission sets,
domínios e IdPs permanecem inacessíveis a ele. A spec previa um segundo handler de autorização que o
dispensaria da regra 1. Não funcionaria: a negação explícita da regra 1 veta qualquer aprovação alheia —
que é exatamente a propriedade que a correção C11 quis —, e o conserto "natural" seria afrouxar a negação.
A v2.7 corrige o desenho: o override será uma **autorização própria**, entregue junto com a trilha de
auditoria, e a negação da regra 1 fica intacta. Até lá, o platform-admin recebe `403` na leitura de
tenant (D2, planejado) — um acesso cruzado entre tenants sem trilha seria pior que a espera.
```

**23.11 — "Ambiente como código" e o fluxo 9.1 sem RabbitMQ nem MassTransit** (o fluxo é dívida da fatia C: o
transporte do provisionamento é em processo desde a v2.5).

Em "Ambiente como código (§15)" (linha 2724), localizar o parágrafo (uma linha só) que começa com:

```markdown
Um `docker compose up` sobe o ambiente inteiro:
```

e substituir a linha inteira por:

```markdown
Um `docker compose up` sobe o ambiente inteiro: Keycloak com Organizations habilitado no realm, PostgreSQL com os dois bancos, Redis, um capturador de e-mails (mailpit), Seq, Jaeger e a API. **O RabbitMQ e a API de exemplo ainda não estão no compose** (v2.7): entram com a fatia do broker e com o Data Plane. O realm é importado de arquivo versionado; a evolução da configuração usa Terraform, **porque arquivos de export não produzem diffs revisáveis nem aplicam mudanças incrementais**.
```

Na lista "Três detalhes" (linha 2729), localizar o item que começa com:

```markdown
2. **`offline_access` é removido do realm.**
```

e substituir a linha inteira por:

```markdown
2. **`offline_access` fica fora do papel padrão do realm.** O papel vem ligado por padrão, e sessões offline **não** são encerradas pelo logout — um usuário desativado continuaria renovando acesso depois da "revogação". Entregue na v2.7: o realm declara o papel e o scope só para tirá-los do padrão, com teste contra o Keycloak real garantindo que não voltaram (achado C8).
```

Localizar o item que começa com (linha 2730):

```markdown
3. **Nenhuma credencial literal no repositório.**
```

e substituir a linha inteira por:

```markdown
3. **Nenhuma credencial literal no repositório — e nenhuma no log.** O primeiro `platform-admin` precisa nascer no bootstrap, mas o arquivo é versionado publicamente. Ele nasce **sem senha**: o `docker compose up` envia, uma vez, um e-mail de convite — que cai no capturador de e-mails local —, e a pessoa define a senha no Keycloak pelo link, que vale 4 horas (v2.7). A versão anterior previa gerar uma senha e exibi-la no log, o que contrariava o próprio ADR-003. Um teste de CI falha se o arquivo contiver qualquer credencial literal (achado C13).
```

No fluxo 9.1, em "Atores e sistemas" (linha 1446), localizar a segunda linha:

```markdown
· PostgreSQL · RabbitMQ · Keycloak.
```

e substituir por:

```markdown
· PostgreSQL · Keycloak. O evento é despachado em processo, pelo próprio Outbox; o RabbitMQ entra com a fatia do
broker (v2.5).
```

No diagrama do fluxo 9.1 (linha 1452), localizar as três linhas:

```text
    participant PG as "PostgreSQL"
    participant MQ as "RabbitMQ"
    participant CONS as "Consumidor de provisionamento"
```

e substituir por estas duas (sai o participante `MQ`):

```text
    participant PG as "PostgreSQL"
    participant CONS as "Consumidor de provisionamento"
```

No mesmo diagrama (linha 1461), localizar as duas linhas:

```text
    PG->>MQ: "Publicacao do evento pelo Outbox"
    MQ->>CONS: "TenantRegistered"
```

e substituir por uma só:

```text
    PG->>CONS: "O Outbox despacha TenantRegistered, em processo"
```

Na tabela "Pontos de falha" do fluxo 9.1 (linha 1512), localizar a linha que começa com:

```markdown
| **RabbitMQ** (após o commit) |
```

e substituir a linha inteira por:

```markdown
| **O despacho do evento** (após o commit) | O evento **já está no Outbox**, dentro do banco, e hoje é despachado em processo, pelo próprio Outbox — não há broker no caminho do provisionamento (v2.5). Se o processo cair, o evento é despachado quando ele voltar. Nenhuma perda; só atraso. É exatamente o problema que o Outbox existe para resolver (ADR-006). O RabbitMQ entra com a fatia do broker. |
```

Localizar a linha que começa com (linha 1513):

```markdown
| **Keycloak** (durante os três passos) |
```

e substituir a linha inteira por:

```markdown
| **Keycloak** (durante os três passos) | O consumidor falha, e o Outbox repete a entrega, com espera crescente. Persistindo a falha além da janela de provisionamento (24 horas por padrão), `ProvisioningFailed` + retry manual. O tenant fica visivelmente incompleto, nunca silenciosamente quebrado. |
```

Em "Consistência eventual", "O problema que o Outbox resolve" (linha 2205), localizar as duas linhas:

```markdown
**A solução do ADR-006:** o comando grava o aggregate em estado `Pending` **e** o evento no Outbox do
MassTransit **na mesma transação**. Um consumidor executa o provisionamento em passos idempotentes
```

e substituir por:

```markdown
**A solução do ADR-006:** o comando grava o aggregate em estado `Pending` **e** o evento no Outbox,
**na mesma transação**. Um consumidor executa o provisionamento em passos idempotentes
```

**23.12 — Linha evolutiva: a v2.7 é a vigente.**

No diagrama da linha evolutiva (linha 2652), localizar:

```text
    G --> H["Especificação v2.6<br/>VIGENTE"]
```

e substituir por estas duas linhas:

```text
    G --> H["Especificação v2.6<br/>convite do admin inicial"]
    H --> I["Especificação v2.7<br/>VIGENTE"]
```

No mesmo diagrama (linha 2660), localizar a linha que termina com:

```text
o convidado nasce habilitado"| H
```

manter, e inserir logo depois, na linha seguinte:

```text
    H -.->|"a fatia D troca a validacao do token:<br/>so tokens do Keycloak, e oito erratas"| I
```

Em "O que mudou em cada salto" (linha 2686), localizar o parágrafo (uma linha só) que termina com:

```markdown
além de uma exceção declarada e limitada à regra de dados pessoais (RN-019). **Nenhum ADR foi revogado.**
```

manter, e inserir logo depois, separado por uma linha em branco:

```markdown
**v2.6 → v2.7: a API passa a aceitar só tokens do Keycloak.** A v2.7 registrou a fatia D. O token de acesso passou a ser o do Keycloak, validado pelas chaves públicas do realm, com emissor, audiência, aplicação de origem e forma conferidos, e o JWT simétrico do template deixou de existir. O primeiro platform-admin passou a nascer sem senha, convidado por e-mail. Entrou o ADR-011 — nas rotas de governança, a Gateway exige, além do token, que o ator seja membro do tenant no banco — e saíram oito erratas, a mais séria delas sobre o override do platform-admin, que como estava descrito nunca funcionaria. **Nenhum ADR foi revogado**; o ADR-003 ganhou um complemento, com o device flow como exceção de demonstração.
```

Localizar o parágrafo (uma linha só) que começa com (linha 2688):

```markdown
**Como ler os documentos.** A **v2.6 é a fonte da verdade**
```

e substituir a linha inteira por:

```markdown
**Como ler os documentos.** A **v2.7 é a fonte da verdade** — é a única aprovada para implementação. Da v2.0 à v2.6, as versões são preservadas como estavam, **intocadas**, porque o valor delas agora é mostrar a evolução; e a revisão crítica é o registro do que produziu a v2.1. O documento de origem mostra de onde tudo partiu.
```

Conferir o documento de negócio:

Run: `grep -n "especificacao-arquitetural-v2.6\|(v2.6),\|27 regras\|consulta qualquer tenant\|gerada aleatoriamente\|PlatformAdminOverrideHandler\|onze limites\|v2.6 é a fonte\|MassTransit\|inclusive no realm de testes" docs/documentacao-negocio.md`
Expected: nenhuma linha.

Run: `grep -c "(D2, planejado)" docs/documentacao-negocio.md`
Expected: `21`.

Run: `grep -c "RN-028\|RN-029" docs/documentacao-negocio.md`
Expected: `14` linhas — as duas regras, a nota da versão, as personas, a F-02, a RN-010, a RN-025, a matriz, os limites e o
modelo de segurança.

Run: `grep -n "AAAA-MM-DD" docs/documentacao-negocio.md`
Expected: nenhuma linha — a data do cabeçalho foi trocada pela do dia.

**Fora do escopo desta tarefa, registrar no handoff:** o documento de negócio continua descrevendo o RabbitMQ como
destino da arquitetura fora do fluxo 9.1 (diagramas de contexto e de containers, a tabela de infraestrutura, os
fluxos 9.4 a 9.8 e 9.6), o que está certo como destino e não como estado; e a frase "sem código escrito até esta
versão", da nota "Natureza do artefato", está desatualizada desde a vertical de registro.

- [ ] **Passo 24: README**

As seções `### Keycloak` e `### Demonstração: …` já foram reescritas pela Tarefa 11: **não editar dentro delas**. Os
trechos abaixo ficam todos fora das duas. Se algum deles passar a aparecer duas vezes por causa daquela reescrita,
a ocorrência a editar é a que está fora das duas seções.

**24.1 — Estado do projeto** (linha 17). Localizar a linha:

```markdown
**M0/M1 em andamento — vertical de registro, fundação Keycloak, consumidor do provisionamento e convite do
```

até a linha que contém (inclusive, a linha inteira):

```markdown
[handoff do convite do admin inicial](docs/superpowers/specs/2026-09-30-convite-admin-inicial-handoff.md).
```

Substituir tudo — o título em negrito, a linha em branco e o parágrafo inteiro, até o link do handoff — por (trocar
`AAAA-MM-DD` pela data do handoff do Passo 27):

```markdown
**M0/M1 em andamento — vertical de registro, fundação Keycloak, consumidor do provisionamento, convite do
admin inicial e tokens do Keycloak (D1) entregues.**

O repositório parte do template [CleanStart](https://github.com/Joseleno/CleanStart) e já traz a fundação
funcionando — Clean Architecture em quatro camadas, Outbox transacional, cache de dois níveis, middlewares
de correlação e segurança, testes de arquitetura e CI. **O domínio do IdentityGateway já existe:** o
agregado `Tenant` e `POST /api/v1/tenants` (PR #1) gravam o tenant e publicam `TenantRegistered` no
Outbox; a fundação Keycloak (PR #2) acrescentou o realm `identity-gateway` com Organizations e a autenticação
`private_key_jwt` do service account; o consumidor do provisionamento (PR #3) consome o evento pelo próprio
Outbox e decide entre repetir e desistir pela janela de provisionamento; o convite do admin inicial (PR #5)
fechou o provisionamento da §9.1 — o tenant só fica `Active` depois que o admin é convidado no Keycloak —; e
a primeira parte da fatia D trocou a autenticação: **a API aceita só access tokens do Keycloak** (RS256, com emissor, audiência,
client de origem e forma conferidos), o JWT simétrico do template deixou de existir, o primeiro platform-admin
nasce sem senha e é convidado por e-mail, e a demonstração obtém o token pelo device flow. Fecha o critério do
M0 "primeiro `curl` com token do Keycloak". **Próximo passo:** a D2, a primeira rota de tenant
(`GET /api/v1/tenants/{tenantId}`), em que o admin convidado lê o próprio tenant.
O roadmap está em [`docs/especificacao-arquitetural-v2.7.md`](docs/especificacao-arquitetural-v2.7.md) §16
(referência normativa atual — as anteriores ficam como registro histórico), e o estado detalhado no
[handoff da D1](docs/superpowers/specs/AAAA-MM-DD-tokens-keycloak-d1-handoff.md).
```

**24.2 — Contagem de testes** (linha 53). Localizar:

```bash
# Toda a suíte — 491 testes, 0 skips (160 domínio, 70 application, 41 arquitetura, 189 integração, 31 funcional)
```

e substituir por (com os números reais do Passo 26, no mesmo formato; exemplo do formato final:
`# Toda a suíte — NNN testes, 0 skips (NNN domínio, NN application, NN arquitetura, NNN integração, NN funcional)`):

```bash
# Toda a suíte — <números reais do passo da suíte>
```

**24.3 — O convite do platform-admin também pela IDE** (linha 104). Com só as dependências de pé, ninguém envia o
convite do platform-admin: o one-shot precisa entrar no comando. Localizar:

```powershell
docker compose up -d postgres redis mailpit keycloak
```

e substituir por:

```powershell
docker compose up -d postgres redis mailpit keycloak platform-admin-invite
```

**24.4 — Tabela "Por onde começar a ler"** (linha 180). Localizar a linha que começa com:

```markdown
| [**Especificação arquitetural v2.6**](docs/especificacao-arquitetural-v2.6.md) |
```

e substituir a linha inteira por:

```markdown
| [**Especificação arquitetural v2.7**](docs/especificacao-arquitetural-v2.7.md) | A referência de implementação: domínio, endpoints, ADRs, código de referência |
```

**24.5 — ADRs: de dez para onze** (linha 208). Localizar:

```markdown
Dez ADRs, com o texto completo na [especificação §4](docs/especificacao-arquitetural-v2.6.md#4-decisões-arquiteturais-adrs).
```

e substituir por:

```markdown
Onze ADRs, com o texto completo na [especificação §4](docs/especificacao-arquitetural-v2.7.md#4-decisões-arquiteturais-adrs).
```

Na tabela logo abaixo (linha 221), localizar a última linha:

```markdown
| 010 | Um só executor por job de fundo, via advisory lock |
```

manter, e inserir logo depois, na linha seguinte:

```markdown
| 011 | Nas rotas de governança, autorização é token mais pertença no banco (decidido; a rota que o usa chega com a D2) |
```

**24.6 — Linha evolutiva** (linha 232). Localizar:

```text
ideia → v2.0 → [revisão crítica: 33 achados] → v2.1 → [documentação de negócio] → v2.2 → v2.3 → v2.4 → v2.5 → v2.6
```

e substituir por:

```text
ideia → v2.0 → [revisão crítica: 33 achados] → v2.1 → [documentação de negócio] → v2.2 → v2.3 → v2.4 → v2.5 → v2.6 → v2.7
```

Localizar a última linha do item da v2.6 (linha 248):

```markdown
  client assertion é o endereço público do Keycloak, não o de transporte.
```

manter, e inserir logo depois dela o item da v2.7:

```markdown
- **v2.7** registrou a fatia D, os tokens do Keycloak: a API valida só tokens RS256 do realm, o primeiro
  platform-admin é convidado por e-mail, e entram o ADR-011 e oito erratas — entre elas a do override do
  platform-admin, que, como estava descrito, nunca funcionaria.
```

**24.7 — Stack: o RabbitMQ como pendência** (linha 262). Localizar:

```markdown
**Entra no M0:** RabbitMQ
```

e substituir por:

```markdown
**Pendente do M0:** RabbitMQ — a especificação o mantém entre as pendências do M0. O design da fatia D propõe, sem
decidir, levá-lo para antes do M5, quando existir o primeiro consumidor fora do processo
([§7 do design](docs/superpowers/specs/2026-09-30-tokens-keycloak-design.md)).
```

Conferir:

Run: `grep -n "v2\.6\|491 testes\|Dez ADRs\|Entra no M0\|convite-admin-inicial-handoff" README.md`
Expected: só a linha evolutiva (`→ v2.6 → v2.7`) e o item `- **v2.6** registrou a fatia C…`.

Run: `grep -n "v2\.7\|Onze ADRs\|| 011 |\|platform-admin-invite\|Pendente do M0" README.md`
Expected: o parágrafo de estado (duas vezes o caminho da v2.7), a tabela "Por onde começar a ler", "Onze ADRs" com o
link, a linha `| 011 |`, a linha evolutiva e o item da v2.7, "Pendente do M0", o comando da IDE com o
`platform-admin-invite` — e as ocorrências de `platform-admin-invite` que a Tarefa 11 pôs nas seções `Keycloak` e
`Demonstração`.

Run: `grep -c "chave-de-desenvolvimento-nao-use-em-producao\|HS256" README.md`
Expected: `0` — a receita HS256 saiu com a Tarefa 11. Se der outro número, a Tarefa 11 não foi concluída: parar.

- [ ] **Passo 25: CONTRIBUTING**

Localizar (linha 108):

```markdown
A [especificação arquitetural v2.6](docs/especificacao-arquitetural-v2.6.md) descreve as camadas, os
```

e substituir por:

```markdown
A [especificação arquitetural v2.7](docs/especificacao-arquitetural-v2.7.md) descreve as camadas, os
```

Na tabela "Teste no nível certo" (linha 75), localizar a última linha:

```markdown
| Estrutura ou convenção | `ArchitectureTests` |
```

manter, e inserir logo depois dela a linha do projeto de suporte:

```markdown
| Infraestrutura de teste contra o Keycloak, usada por mais de um projeto | `tests/IdentityGateway.Testing.Keycloak` — biblioteca de suporte, sem testes próprios |
```

Logo abaixo da tabela (linha 77), localizar a primeira linha da nota:

```markdown
> **O provider InMemory do EF Core é proibido** em teste de integração.
```

e inserir **antes** dessa linha, separado dela por uma linha em branco:

```markdown
> **`tests/IdentityGateway.Testing.Keycloak` é biblioteca, não projeto de teste.** Guarda o que mais de um projeto
> precisa para falar com um Keycloak real — o fixture do Keycloak, o cliente do mailpit e o harness de login por
> device flow —, não referencia `src/`, e é usada pelos projetos de integração e funcional e pelo app de CI em
> `tools/`. Teste novo vai num dos cinco projetos acima; para lá, só infraestrutura de teste compartilhada.
```

Run: `grep -n "especificacao-arquitetural\|Testing.Keycloak" CONTRIBUTING.md`
Expected: três linhas — o link, apontando `docs/especificacao-arquitetural-v2.7.md`, a linha nova da tabela e a nota.

- [ ] **Passo 26: Suíte completa**

Docker Desktop ligado (sem ele, integração e funcionais falham com `DockerUnavailableException` — ambiente, não
regressão). A suíte roda **depois** das edições dos documentos, e não antes: há regras de arquitetura que leem o
`README.md` (o padrão de `PLATFORM_ADMIN_EMAIL` igual no compose, no app e no README), e uma edição de texto pode
reprová-las.

Run: `dotnet build IdentityGateway.slnx`
Expected: `0 Aviso(s)`, `0 Erro(s)`.

Run: `dotnet build -c Release tools/jornada-compose.cs`
Expected: compila, sem avisos — é o que o job `Compose` faz antes de subir o Docker.

Run: `dotnet test`
Expected: 0 falhas, 0 skips em todos os projetos. Anotar o total por projeto — são **cinco** projetos de teste
(`IdentityGateway.Domain.UnitTests`, `IdentityGateway.Application.UnitTests`, `IdentityGateway.ArchitectureTests`,
`IdentityGateway.Infrastructure.IntegrationTests`, `IdentityGateway.Api.FunctionalTests`) — e o total geral: entram
no README (Passo 24, item 24.2) e no handoff (Passo 27). O `IdentityGateway.Testing.Keycloak` é biblioteca e não
aparece na lista: se aparecer como projeto de teste, com zero testes, o `IsTestProject=false` da Tarefa 1 se perdeu.
Se algum teste falhar, parar e diagnosticar; não commitar documentação sobre suíte vermelha.

Voltar ao README e trocar `<números reais do passo da suíte>` pelos números anotados, no formato
`NNN testes, 0 skips (NNN domínio, NN application, NN arquitetura, NNN integração, NN funcional)`.

Run: `grep -n "<números reais" README.md`
Expected: nenhuma linha.

Run: `dotnet test tests/IdentityGateway.ArchitectureTests`
Expected: verde de novo, depois da troca dos números no README.

- [ ] **Passo 27: Handoff da D1**

Run: `date +%F` — a data do dia é o prefixo do arquivo.

Criar `docs/superpowers/specs/AAAA-MM-DD-tokens-keycloak-d1-handoff.md` com o conteúdo abaixo, no formato do
[handoff da fatia C](../specs/2026-09-30-convite-admin-inicial-handoff.md). Preencher cada `<…>` com o que foi
**observado** — commits (`git log --oneline main..HEAD`), números da suíte do Passo 26, o resultado de cada mutação
registrado nas mensagens de commit das Tarefas 1–11, e a verificação ao vivo das Tarefas 10 e 11. Nada de valor
esperado no lugar de valor observado; o que não foi feito fica escrito como não feito. Trocar também `AAAA-MM-DD` no
link do README (Passo 24, item 24.1) e no cabeçalho do documento de negócio (Passo 23, item 23.1) pela data real.

**A seção "Pendente para o autor" só fica se a mutação da guarda `HttpSoEmDesenvolvimento` não foi observada
vermelha na Tarefa 8 (mutação 15).** Se foi, apagar a seção inteira (do título à linha `---` que a fecha) e preencher a linha
correspondente da tabela de mutações com o teste e a mensagem observados.

````markdown
# Handoff — tokens do Keycloak, parte D1 entregue

> **Data:** AAAA-MM-DD · **Marco:** M0 + M1 (fatia D, primeira parte) · **Status:** implementada, build e suíte
> completa verdes, verificação ao vivo confirmada. Push e PR aguardam autorização do autor.
> **Onde parou:** as 12 tarefas da D1 estão commitadas na branch `feat/tokens-keycloak`; falta a revisão final da
> branch, enviar a branch, abrir o PR contra `main`, acompanhar a CI e mesclar. A D2 (Tarefas 13–17) começa depois
> do merge, a partir da `main`.
>
> Sucede o [handoff do design da fatia D](2026-10-01-tokens-keycloak-design-handoff.md) e o
> [handoff do convite do admin inicial](2026-09-30-convite-admin-inicial-handoff.md) (fatia C, PR #5). Design da
> fatia: [`2026-09-30-tokens-keycloak-design.md`](2026-09-30-tokens-keycloak-design.md). Plano:
> [`2026-09-30-tokens-keycloak.md`](../plans/2026-09-30-tokens-keycloak.md). Referência normativa:
> [`especificacao-arquitetural-v2.7.md`](../../especificacao-arquitetural-v2.7.md).

---

## Pendente para o autor: uma prova por mutação que não foi executada

**A mutação da guarda `HttpSoEmDesenvolvimento` não foi provada, pela segunda vez.** A guarda recusa
`AllowInsecureHttp` e `PublicBaseUrl` em `http` fora de `Development`. Na fatia C, a proteção automática do ambiente
de execução negou rodar os testes com a verificação de https enfraquecida; na Tarefa 8 desta fatia, <o que
aconteceu: a mensagem da recusa, colada>. Ninguém a contornou, e ela **não** é critério de aceite da D1. A guarda
commitada está correta — conferida por leitura: só `IsDevelopment()` passa. Roteiro para fechar, à mão:

1. Em `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakServiceCollectionExtensions.cs`, pôr `return true;`
   na primeira linha do corpo de `HttpSoEmDesenvolvimento`.
2. `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter-class "*KeycloakAdminOptionsTests"`
   — ver vermelhos `AllowInsecureHttpForaDeDevelopment_FalhaAoValidar` e
   `PublicBaseUrlHttpForaDeDevelopment_FalhaAoValidar` (o controle positivo `HttpsEmProducao_Aceita` continua verde).
3. `dotnet test tests/IdentityGateway.Api.FunctionalTests --filter-class "*HostEmProducaoTests"` — ver vermelho o
   teste da subida em `Production` com `BaseUrl` em `http` ou com `AllowInsecureHttp`: a subida deixa de falhar.
4. Reverter e conferir `git diff --stat` vazio.

---

## Estado do repositório

| O quê | Estado |
|---|---|
| Branch | `feat/tokens-keycloak`, 4 commits de planejamento sobre `main` (`8537d76`, `833a9cc`, `0294b90`, `677adba`), os commits do plano (`<hashes>`, de `git log --oneline -- docs/superpowers/plans/2026-09-30-tokens-keycloak.md`) e **<N> commits** das Tarefas 1–12 sobre `main` (`5b0113c`, o merge do PR #5) |
| `main` | Não tocada — recebe o merge pelo PR |
| Working tree | Limpa depois do commit desta tarefa |
| Docker | Rodando; usado nas tarefas com Testcontainers (PostgreSQL, Redis, Keycloak 26.7.4 e mailpit reais), na verificação ao vivo das Tarefas 10 e 11 (projeto isolado `igverif`, derrubado com `down -v` no fim) e na suíte completa da Tarefa 12. Os volumes `identitygateway_*` do autor não foram tocados |
| Push / PR | **Pendentes de autorização.** Nada foi enviado |

<Uma linha por frente de commits, como no handoff da fatia C: planejamento, Tarefas 1–11 (quantos `feat`, `fix`,
`test`, `refactor`, `chore`, `ci`), Tarefa 12 (`docs`).>

**Aviso para quem já tem volumes do compose: rode `docker compose down -v` uma vez, depois
`docker compose up -d --build`. É a quarta vez que o projeto pede isso.** O realm só é importado na primeira subida
(`IGNORE_EXISTING`), e num volume antigo faltam os client scopes `gateway-roles`, `gateway-tenant` e `gateway-api`, o
client de demonstração, o catálogo de papéis e o usuário do primeiro platform-admin. O one-shot
`platform-admin-invite` detecta o volume antigo e sai com `1`, mandando rodar o `down -v`; como a `api` depende dele,
o `docker compose up` falha com a causa no log, em vez de a API subir e todo token dar `401`. O README e o corpo do
PR repetem o aviso. **O mesmo vale se a senha do admin do `master` do compose foi trocada, ou se ele foi apagado:** o
one-shot faz login com ele a cada subida.

## O que a D1 entregou

A API passou a aceitar só access tokens do Keycloak, e o JWT simétrico do template deixou de existir. O realm emite
o token que a §10.1 da especificação descreve, o primeiro platform-admin nasce sem senha e é convidado por e-mail
uma única vez, e a demonstração, os testes e a CI obtêm o token pelo device flow. Fecha o critério do M0 "primeiro
`curl` com token do Keycloak". A rota de tenant e a policy `TenantAdmin` são da D2.

| Camada | Entregue |
|---|---|
| Projeto de suporte de testes | `tests/IdentityGateway.Testing.Keycloak`, biblioteca (`IsTestProject=false`, sem referência a `src/`): `KeycloakFixture`, `ChavesDeTeste` e `RaizDoRepositorio` (movidos do projeto de integração), `FamiliaDeFalha`, `FalhaDoHarnessException`, `ClienteDoMailpit`, `HarnessDeLogin`, `TokensDeUsuario`, `UsuarioDeTeste`, `SenhasDeTeste`; `xunit.v3.extensibility.core` no `Directory.Packages.props`; o ROPC do fixture removido |
| Realm | Catálogo `platform-admin`, `tenant-admin`, `financial-manager` e `reader`, nunca compostos, mais `offline_access` e `uma_authorization` fora do papel padrão; scopes `gateway-roles`, `gateway-tenant` e `gateway-api`, nenhum default do realm; `CreateDefaultClientScopes`; `identity-gateway` com `basic` e `roles`; `identity-gateway-demo` público, só device flow; `accessTokenLifespan` 300; `registrationAllowed` falso; `bruteForceProtected`; rotação do refresh token; o usuário do platform-admin com `${PLATFORM_ADMIN_EMAIL}`, sem credencial; `RegrasDoRealmTests` com as regras novas |
| Infrastructure | `AccessTokenValidationOptions` (seção `Keycloak:Auth`: `Audience`, `AllowedClients`; `Issuer`, `MetadataAddress` e `RequireHttpsMetadata` derivados pelo adaptador); `KeycloakAdminOptions.Issuer` e `MetadataAddress`, com `AssertionAudience => Issuer`; a recusa do client de demonstração fora de Development; `JwtOptions` removido |
| Api | `Authentication/ValidacaoDoAccessToken` e `AvisoDeChavesIndisponiveis` (JwtBearer por metadados internos, `IssuerValidator` estrito, RS256, `ClockSkew` 30 s, `BackchannelTimeout` 5 s, `RefreshInterval` 30 s, `IncludeErrorDetails` falso), `FormaDoAccessToken` (`azp`, `typ`, `sub`), `AutenticacaoLogs` (EventIds 2100–2103), `AvisoDeClientsPermitidos`; `Authorization/Policies`, `RespostasDeAutorizacao` e `ProblemDetailsDeAutorizacao` (Problem Details em `401` e `403`); `FallbackPolicy` autenticada e `AllowAnonymous` explícito nas rotas anônimas; `HttpCurrentUser` com `"sub"`; `Security/JwtTokenService` e a seção `Jwt` dos appsettings removidos; `AllowedClients` só no `appsettings.Development.json` |
| Compose | One-shot `platform-admin-invite` (`kcadm`, marcador `platformAdminInviteSentAt` no realm antes do envio, link de 4 h, saída `0` ou `1`, reenvio por `REENVIAR=1`); a `api` depende dele; `PLATFORM_ADMIN_EMAIL` com padrão e recusa de vazio ou maiúsculas; `api` e Jaeger só em `127.0.0.1`; sem `Jwt__SigningKey` |
| CI e ferramentas | `tools/jornada-compose.cs` (app de arquivo único, `#:project`, sem AOT) com as fases `convites`, `jornada`, `antes-de-parar`, `com-keycloak-parado` e `depois-de-voltar`; job `Compose` com `pipefail`, prazo por passo, o convite contado exato, a receita HS256 antiga com `401`, o convite do admin do tenant e o Keycloak parado com `stop`/`start` |
| Testes | OIDC falso com emissor divergente na `IdentityGatewayApiFactory`; suíte negativa de autenticação; opções do JwtBearer conferidas em execução; `ApiEmProducaoFactory` com pedidos; coleção com Keycloak real atravessando a API e a ponte de contrato; vazamento do e-mail no token, com log e trace; regras de arquitetura novas (`RegrasDaApiTests`, `RegrasDoAmbienteLocalTests`, `RegrasDeFerramentasTests`) |
| Documentos | Especificação v2.7 (§0 nova, com T1–T15 e as erratas E1–E8, o ADR-011 e as seções que a fatia tocou; o que é da D2 marcado "(D2, planejado)"); documento de negócio 1.4; README (andamento, v2.7, contagem, onze ADRs, linha evolutiva; a demonstração por device flow e as notas fixas, da Tarefa 11); CONTRIBUTING apontando a v2.7 e o projeto de suporte; este handoff |

## O que mudou em relação ao plano

<Uma entrada numerada por desvio que a execução encontrou, como no handoff da fatia C: o que o plano supunha, o que
a execução achou, o commit e onde a v2.7 registra. Se nada mudou, dizer "nada".>

## Suíte completa

`dotnet build IdentityGateway.slnx`: **<0> avisos, <0> erros.** `dotnet build -c Release tools/jornada-compose.cs`:
**<resultado>.**

`dotnet test` (solução inteira, Docker rodando, HEAD `<hash>`, antes do commit desta tarefa, que só muda
documentação): **<total> total, <0> falhas, <0> skips.**

| Projeto | Total | Falhas | Skips |
|---|---|---|---|
| `IdentityGateway.Domain.UnitTests` | <n> | 0 | 0 |
| `IdentityGateway.Application.UnitTests` | <n> | 0 | 0 |
| `IdentityGateway.ArchitectureTests` | <n> | 0 | 0 |
| `IdentityGateway.Infrastructure.IntegrationTests` | <n> | 0 | 0 |
| `IdentityGateway.Api.FunctionalTests` | <n> | 0 | 0 |
| **Total** | **<n>** | **0** | **0** |

`IdentityGateway.Testing.Keycloak` é biblioteca de suporte e não tem testes. A fatia C terminou com 491 testes; o
design estimava de 50 a 55 novos na D1. Depois das edições desta tarefa, `IdentityGateway.ArchitectureTests` rodou
de novo: <n>/<n>.

## Prova por mutação

Toda mutação executada foi aplicada, confirmada vermelha (erro de compilação não conta), revertida byte a byte
(`git diff --stat` vazio) e reconfirmada verde antes do commit. As linhas são as da §5.3 do design marcadas D1; a
coluna "Resultado" traz o observado nas mensagens de commit das tarefas — o teste que ficou vermelho e a mensagem —,
ou "não executada", com o motivo. `F` = funcional com o OIDC falso; `K` = Keycloak real; `P` = host em `Production`.

| Mutação | Deve ser pega por | Resultado |
|---|---|---|
| Remover o `IssuerValidator` (manter `ValidIssuer`) | F: `iss` do discovery ≠ configurado (entre os F, só ele); K: `PublicBaseUrl` errado | <preencher> |
| Validador frouxo: comparar com o `BaseUrl`, `StartsWith` ou `OrdinalIgnoreCase` | F: todos os sucessos; barra final, sufixo e maiúsculas | <preencher> |
| `ValidateAudience=false` | F: `aud` errada; K: token do client de device flow do fixture, com `azp` aceito e sem `identity-gateway-api`; opções resolvidas | <preencher> |
| `ClockSkew` padrão, ou `ValidateLifetime=false` | F: vencido há 2 min | <preencher> |
| Tirar a checagem do `azp`, ou aceitar qualquer `azp` com a lista vazia | F: `azp` fora da lista; P: lista vazia com token válido | <preencher> |
| Ler a forma pelos claims, e não pelo JSON (aceita o array de um elemento) | F: `typ` em array. Para o `azp`, a mutação é equivalente e **não foi executada**: a biblioteca recusa, ao ler o token, o `azp` que não é texto, e o caso `azp` em array leva `401` com ou sem a checagem | <preencher> |
| Aceitar o demo fora de Development (tirar a recusa do `ValidateOnStart`) | P | <preencher> |
| Tirar a checagem do `typ`, ou a do `sub`; `TryParse` no lugar de `TryParseExact("D")` | F: `typ` = `ID`; sem `sub`; `sub` no formato `N` | <preencher> |
| `SymmetricSecurityKey` de volta em produção | Arquitetura, e só ela | <preencher> |
| `IssuerSigningKey`, `IssuerSigningKeys`, `SignatureValidator` ou `IssuerSigningKeyResolver` fixos | Configuração: opções resolvidas em execução | <preencher> |
| `RequireHttpsMetadata=false` fixo | P: opções resolvidas em `Production` | <preencher> |
| A guarda `HttpSoEmDesenvolvimento` sempre verdadeira | P: a subida com `BaseUrl` `http` deixa de falhar | <preencher, ou "não executada — ver o destaque no topo"> |
| `IncludeErrorDetails=true` | Configuração: opções resolvidas; P: `error_description` no `WWW-Authenticate` | <preencher> |
| `BackchannelTimeout` padrão | P: metadados frios passam de ~7 s | <preencher> |
| Tirar o log do `OnAuthenticationFailed`, ou logar o token | P: metadados frios sem o `Warning`; vazamento: o token no log | <preencher> |
| `MapInboundClaims=true` | F: todos os `202` | <preencher> |
| Tirar a `FallbackPolicy` | F: caminho não mapeado sem token deixa de levar `401` | <preencher> |
| `aud` num scope default do realm, ou `gateway-*` nos defaults do service account | Regras do realm; K: o token do client de device flow do fixture sem `identity-gateway-api`, e a forma do token do service account | <preencher> |
| Tirar `CreateDefaultClientScopes` | Regra do realm; K: scopes embutidos presentes e `sub` no token | <preencher> |
| `fullScopeAllowed=true` no demo | Regra do realm; K: `roles` ⊆ catálogo, com o usuário que tem `default-roles-*` | <preencher> |
| `revokeRefreshToken` falso, ou `refreshTokenMaxReuse: 1` | Regra do realm; K: refresh token reusado recusado | <preencher> |
| Tirar o scope `offline_access` do JSON | Regra do realm; K: papel padrão `[manage-account, view-profile]` | <preencher> |
| One-shot sem o marcador, ou lendo o marcador com `--fields attributes` | CI: `convites --esperado 1` depois do `run`; ao vivo, no `igverif` | <preencher> |

**Demais mutações executadas nas tarefas**, além das da §5.3:

| Tarefa | Mutação | Resultado |
|---|---|---|
| <T1…T11> | <uma linha por mutação registrada nas mensagens de commit: as regras novas do realm, as do harness, as do compose e as das travas do one-shot (`tenant_id`, grupo ou papel extra no usuário → saída `1`)> | <o teste vermelho e a mensagem> |

## Verificação ao vivo

**Antes da execução, ao escrever o plano (2026-10-01), contra um Keycloak 26.7.4 com mailpit v1.31.3, num ambiente
descartável:**

- O realm final importa, e a leitura pelo master confere: scopes embutidos presentes, `gateway-*` fora dos defaults
  do realm, papel padrão `[manage-account, view-profile]`, o client de demonstração com os cinco scopes e o service
  account com `basic` e `roles`.
- O script do one-shot roda como está: a primeira execução envia um e-mail, com link de `exp − iat = 14400`; a
  segunda sai `0` com "convite já enviado em …" e continua um e-mail; `REENVIAR=1` envia outro; e o e-mail
  configurado em maiúsculas é comparado em minúsculas.
- O harness de login roda como está: o link de ações em 4 páginas; o device flow em 5,2 s; o platform-admin, sem
  Organization no realm, entra em **1** passo de login, e um usuário com Organization, em **2**; um segundo login na
  mesma instância, 0 passos (cookie de SSO); a renovação devolve um refresh token novo; e **o refresh token reusado
  leva `invalid_grant` e, depois dele, o novo também** — o reuso derruba a sessão do client.
- **O spike do marcador** (fim da §3 do design): `platformAdminInviteSentAt` como atributo do realm funciona — o
  `kcadm update -s` só acrescenta o atributo, a segunda e a terceira execução saem `0` sem e-mail, o marcador
  sobrevive a `stop`/`start` e não aparece em nenhum token, no userinfo nem no discovery.

**Na execução — o one-shot e o compose (Tarefa 10), no projeto isolado `igverif`** (`docker compose -p igverif …`,
volumes próprios, sem tocar os do projeto padrão):

| Passo | Horário | Resultado |
|---|---|---|
| `docker compose -p igverif up -d --build --wait api` | <hh:mm:ss> | <serviços `healthy`; o one-shot com saída `0`; `/health/ready` → `Healthy`> |
| E-mails para o `PLATFORM_ADMIN_EMAIL` no mailpit | <hh:mm:ss> | <quantos — o esperado é 1> |
| `docker compose -p igverif run --rm --no-deps platform-admin-invite`, com o convite pendente | <hh:mm:ss> | <saída e mensagem; quantos e-mails depois> |
| Volume antigo (realm sem o scope `gateway-api`) | <hh:mm:ss> | <saída `1` e a mensagem> |
| `PLATFORM_ADMIN_EMAIL` vazio e com maiúsculas | <hh:mm:ss> | <o `keycloak` não sobe; a mensagem> |
| Reenvio depois de `down` e `up` (`-e REENVIAR=1`) | <hh:mm:ss> | <enviado; o aviso dos links anteriores> |
| Travas do usuário (`tenant_id`, grupo, papel extra) | <hh:mm:ss> | <saída `1` em cada uma> |
| `docker compose -p igverif down -v` | <hh:mm:ss> | <volumes do projeto isolado removidos> |

**Na execução — a jornada do job `Compose` rodada localmente (Tarefa 11):**

| Fase | Horário | Resultado |
|---|---|---|
| `convites --esperado 1`, o `run` do one-shot e `convites --esperado 1` | <hh:mm:ss> | <observado> |
| `jornada` | <hh:mm:ss> | <passos do login do platform-admin (o esperado é 1); a receita HS256 antiga → `401`; `POST /tenants` → `202`; `Active` em quantos segundos; o convite do admin do tenant e o `GET` do link → `200`> |
| `antes-de-parar`, `stop`, `com-keycloak-parado`, `start`, `depois-de-voltar` | <hh:mm:ss> | <`GET` autenticado; `202` e `Pending` com o Keycloak parado; `Active` depois de voltar, em quantos segundos> |
| Segunda subida (`down`, `up --wait api`) | <hh:mm:ss> | <o one-shot com saída `0` e "já enviado"; `convites --esperado 0`> |
| Tempo total do job na CI | — | <observado no primeiro PR; a estimativa do design é de cerca de 5 min 30 s> |

**Não verificado**, e registrado na §19 da v2.7: o cache de metadados além de ~9 minutos com o Keycloak fora; se o
mailpit valida o `Host` contra *DNS rebinding*; e, herdado da fatia C, se a troca de e-mail pela account console
exige verificação na 26.7.4. <Acrescentar o que mais a execução deixou sem verificar.>

## Decisões tomadas durante a execução

| Decisão | Custo se errado |
|---|---|
| <uma linha por decisão das Tarefas 1–12 que não estava no plano: ajustes de analisador, snippets do plano que não compilaram, comandos corrigidos na verificação ao vivo> | <…> |

## Pendências

**Para o autor decidir:**
- <A prova por mutação da guarda `HttpSoEmDesenvolvimento`, se continuar pendente — roteiro no topo.>
- **Uma divergência interna do design, a fechar antes da D2:** a §4.3 diz que o `tenantId` da rota e o claim
  `tenant_id` são, os dois, GUID no formato `D`; a §5.2 tem como controle positivo o próprio tenant com o GUID da
  rota **no formato `N`** respondendo `200`. A v2.7 (§10.1 e §11.7) ficou com a leitura da §5.2 — o claim só no
  formato `D`, a rota em qualquer formato de GUID —, e a Tarefa 13 do plano implementa essa leitura. Se o autor
  preferir a outra, mudam a Tarefa 13 (o `Guid.TryParse` da rota vira `TryParseExact`, e o controle positivo do
  formato `N` vira caso de `403`) e, na Tarefa 17, a v2.7.

**Limites registrados na v2.7 (§19), que continuam abertos:**
- **O platform-admin recebe `403` na leitura de tenant** até o override virar a policy `TenantReadAccess`, com a
  auditoria.
- **O Data Plane continua exposto ao `tenant_id` por grupo** (ADR-011): a regra "nenhum grupo" é conferida só no
  JSON do bootstrap.
- **A pertença não contém quem tem a chave da Gateway**, que toma a conta de um `Member` real.
- **Links de ações antigos trocam a senha de uma conta ativa.** O do platform-admin vale 4 horas; **o link de 7 dias
  do convite do admin do tenant é risco aceito**, com dono na operação que trocar ou reenviar esse convite.
- **E-mail digitado errado:** com a D2, o destinatário passa a ler o tenant pela API.
- **Device flow:** consentimento forçado, e o client público é o vetor clássico de phishing de código de
  dispositivo; por isso fica só no ambiente local e só na lista de `azp` de Development.
- **`CreateDefaultClientScopes` não é documentado:** reverificar a cada troca de tag do Keycloak.
- **Rotação de chave do realm:** cada réplica recusa o primeiro pedido com o `kid` novo; o runbook está na §19.
- **Keycloak fora com metadados frios responde `401`**, e não `503`; o `/health/ready` não cobre um `jwks_uri`
  inalcançável.
- **A `api` depende de um one-shot que depende do Keycloak saudável**, e o one-shot faz login no `master` a cada
  subida: não trocar nem apagar o admin do `master` do compose.
- **`PLATFORM_ADMIN_EMAIL` fica fixado no primeiro import**, e a recuperação do platform-admin só existe pelo
  console do `master`. O bootstrap de produção não está decidido.
- **O marcador do convite vive no realm:** usuário recriado à mão, ou envio que falhou, só saem pelo reenvio manual.
- **A lista de `azp` é estática**, e fica vazia fora de Development até existir um client administrativo.
- **Reusar um refresh token derruba a sessão do client:** nenhuma renovação pode ser repetida automaticamente.
- **Mailpit sem autenticação** em `127.0.0.1:8025`.

**Dívidas herdadas, que seguem:** e-mail duplicado entre o envio e o commit; admin órfão; tenants anteriores à v2.6;
a mesma pessoa em dois tenants; retenção real do e-mail apagado; notification-hub; vínculo federado no M4; corrida
residual do `POST` de papel; slug perdedor com `500`; a troca de e-mail sem verificação, não verificada na 26.7.4.

**Não entrou, e segue pendente do M0:** a tabela de auditoria, o armazenamento de eventos do realm (custa outro
`docker compose down -v` quando entrar, e a retenção dos eventos de login, que guardam o e-mail, é decidida junto) e
o RabbitMQ.

**Propostas do design (§7), sem decisão:** a sequência das próximas fatias; a suspensão cobrindo também o admin
`Invited`; a revisão da §12 da especificação, do Data Plane; o bootstrap de produção do primeiro platform-admin; e o
RabbitMQ fora da lista do M0. Nenhuma está na v2.7.

**Documento de negócio:** o RabbitMQ continua descrito como destino da arquitetura fora do fluxo 9.1, o que está
certo como destino; a frase "sem código escrito até esta versão", da nota "Natureza do artefato", está desatualizada
desde a vertical de registro; e o documento explica nove ADRs, sem ficha para o ADR-010 nem para o ADR-011.

<Pendências menores levantadas nas revisões das tarefas: cobertura ausente, comentários, nomes.>

## Próximo passo

1. Revisão final da branch inteira, com a lista de pendências acima.
2. Autorizar o push e abrir o PR contra `main`, com o aviso de `docker compose down -v` no corpo.
3. Acompanhar a CI (`gh pr checks <n>`) — `Build`, `Testes`, `Imagem Docker` e `Compose`, que agora roda a jornada
   pelo app C#, com o Keycloak parado. Se algo falhar, corrigir na mesma branch.
4. Mesclar. Depois: `git checkout main && git pull --ff-only`, apagar a branch local e a remota, e acrescentar o
   número do PR na linha da fatia D da §16 da v2.7.
5. Começar a **D2** (Tarefas 13–17 do plano) numa branch nova, a partir da `main`: os requirements e a policy
   `TenantAdmin`, a porta `IMemberQueries`, a rota `GET /api/v1/tenants/{tenantId}`, a coleção real estendida e o
   fechamento dos itens "(D2, planejado)" — `grep -n "(D2, planejado)" docs/especificacao-arquitetural-v2.7.md docs/documentacao-negocio.md`
   lista todos. A D2 não mexe no realm, no compose nem no one-shot.

## Como retomar

Docker Desktop costuma estar desligado ao abrir a sessão: sem ele, os testes de integração e funcionais falham com
`DockerUnavailableException` (ambiente, não regressão). Quem já tinha o compose de pé antes desta fatia precisa de
`docker compose down -v` uma vez. O token da demonstração vale 5 minutos, e o refresh token, 30 minutos de
inatividade: cada renovação devolve um refresh token novo, e o usado não serve mais.
````

Run: `grep -n "<\|AAAA-MM-DD" docs/superpowers/specs/*-tokens-keycloak-d1-handoff.md`
Expected: uma linha só — `gh pr checks <n>`, no "Próximo passo", que fica como está até o PR ter número. Toda outra
lacuna do modelo começa com `<`: se sobrou alguma, preencher com o observado ou apagar a linha.

Run: `ls docs/superpowers/plans/2026-09-30-tokens-keycloak.md`
Expected: o arquivo existe — o cabeçalho do handoff aponta para ele. Se o plano foi gravado com outro nome, corrigir
o link.

Run: `grep -n "AAAA-MM-DD" README.md docs/documentacao-negocio.md`
Expected: nenhuma linha.

- [ ] **Passo 28: Commit**

Run: `git status --short`
Expected: exatamente estes cinco caminhos — `??` (ou `A`) para `docs/especificacao-arquitetural-v2.7.md` e para o
handoff da D1, e `M` para `docs/documentacao-negocio.md`, `README.md` e `CONTRIBUTING.md`. Nada em `src/`, `tests/`,
`tools/`, `keycloak/`, `.github/` nem `docker-compose.yml`. (Um sexto caminho, a spec de design, só se o Passo 21 a
corrigiu.)

Run: `grep -cF -- "$(printf '\134u0026')" docs/especificacao-arquitetural-v2.7.md`
Expected: `2` — a sequência de escape continua lá, depois de todas as edições.

```bash
git add docs README.md CONTRIBUTING.md
git commit -m "docs: especificacao v2.7, documento de negocio 1.4, README e handoff da D1

A v2.7 registra os tokens do Keycloak: a API valida so access tokens
RS256 do realm, com emissor, audiencia, azp, typ e sub conferidos; o
realm emite o token com os scopes gateway-* fora dos defaults; o primeiro
platform-admin nasce sem senha e e convidado por e-mail uma vez; e a
demonstracao e a CI usam o device flow. Entra o ADR-011 e saem oito
erratas, a E1 gravada por shell. O que so a D2 entrega fica marcado
(D2, planejado). O documento de negocio vai a 1.4, o README e o
CONTRIBUTING apontam a v2.7, e o handoff traz a suite, a tabela de
mutacoes e a verificacao ao vivo."
```

Sem nenhum trailer de coautoria nem linha de atribuição de ferramenta.

Run: `git log -1 --format=%B | grep -Eci "co-authored|generated with"`
Expected: `0`.

Run: `git show --stat --format= HEAD`
Expected: os cinco caminhos do `git status` acima, e só eles.

Push e PR ficam para autorização do autor.

---

# PARTE D2 — a primeira rota de tenant (um PR)

A D2 começa **depois do merge da D1**, numa branch nova a partir da `main`:

```bash
git checkout main && git pull --ff-only
git checkout -b feat/leitura-do-tenant
dotnet build IdentityGateway.slnx && dotnet test
```

Expected: build sem avisos e suíte verde, com os totais do handoff da D1. Anote o total de cada projeto: os passos "ver passar" abaixo dizem quantos testes cada tarefa acrescenta.

**A D2 não toca `keycloak/`, `docker-compose.yml` nem o one-shot.** Tudo o que ela consome do realm (o scope `gateway-tenant`, o catálogo, o client de demonstração) entrou na D1. Se uma tarefa daqui parecer pedir mudança no realm, pare: é erro de leitura, e custaria outro `docker compose down -v`.

**Verificado ao escrever este plano (2026-10-01).** O código de produção e os testes das Tarefas 13 a 15 foram compilados com os analisadores do repositório e executados num clone descartável, sobre a `main` de hoje com a autorização da Tarefa 7 aplicada por cima (as policies e o Problem Details), e os tokens assinados pelo JWT simétrico do template no lugar do OIDC falso: 43 testes unitários da policy, 31 por HTTP (26 da rota e 5 da ordem dos handlers), as consultas contra o PostgreSQL e as regras de arquitetura, todos verdes; e cada mutação das tabelas 🧪 foi aplicada e vista vermelha, com as contagens que as tabelas trazem. O que **não** foi executado: a extensão da suíte negativa de autenticação à rota nova (Tarefa 15, Passo 7), que depende do OIDC falso, e a Tarefa 16 inteira (Keycloak real, app e CI).

Docker Desktop ligado nas Tarefas 14 a 16.

---

### Tarefa 13: Os três requirements do token, a policy `TenantAdmin` e o `AddAutorizacaoDaGateway`

Spec: §4.3 (a tabela dos requirements, os três primeiros; "A ordem de registro é explícita"), §5.1 (linha "Unitário da autorização"), §5.2 (casos `U` da D2), §5.3 (as mutações "`return` no lugar de `Fail()`", "comparar o tenant como texto" e "tirar o `NotPlatformAdminRequirement`"), D-g.

A policy `TenantAdmin` nasce com as três camadas que só leem o token: o papel, a separação de funções e o tenant da rota. A quarta — a pertença no banco — entra na Tarefa 14, e a rota que usa a policy, na 15. Nenhuma rota muda aqui.

O ponto que os testes desta tarefa existem para provar: **cada negação é um `Fail()`**, e não a falta de um `Succeed`. Por HTTP as duas coisas respondem o mesmo `403`; a diferença só aparece quando outro handler aprova o requirement — e é por isso que os testes põem no contêiner um handler que aprova tudo.

**Arquivos:**
- Create: `src/IdentityGateway.Api/Authorization/RoleRequirement.cs`, `NotPlatformAdminRequirement.cs`, `SameTenantRequirement.cs`, `AutorizacaoDaGateway.cs`
- Modify: `src/IdentityGateway.Api/Authorization/Policies.cs`, `src/IdentityGateway.Api/DependencyInjection.cs`
- Create (testes): `tests/IdentityGateway.Api.FunctionalTests/Autorizacao/MontagemDaAutorizacao.cs`, `Autorizacao/TenantAdminPolicyTests.cs`

**Interfaces:**
- Consome: `Policies.PlatformAdmin`, `ProblemDetailsDeAutorizacao` e o registro da autorização em `AddAutenticacao` (Tarefa 7).
- Produz:
  - `Policies.TenantAdmin` (`"TenantAdmin"`) e `Policies.DeTenant` (`IReadOnlyList<string>`, hoje só com `TenantAdmin`).
  - `internal sealed class RoleRequirement(string role)`, `NotPlatformAdminRequirement` e `SameTenantRequirement` — cada um é requirement **e** handler (`AuthorizationHandler<T>, IAuthorizationRequirement`). `SameTenantRequirement.ParametroDaRota` (`"tenantId"`).
  - `internal static class AutorizacaoDaGateway` com `IServiceCollection AddAutorizacaoDaGateway(this IServiceCollection services)`: policies, `FallbackPolicy`, `InvokeHandlersAfterFailure = false`.
  - Nos testes: `MontagemDaAutorizacao.Montar(Action<IServiceCollection>? ajustar = null)`, `.Usuario(roles, tenantIds, sub)`, `.Pedido(tenantIdDaRota)` e `HandlerQueAprovaTudo`.

- [ ] **Passo 1: A montagem da autorização fora do HTTP**

Os testes unitários de autorização ficam no projeto funcional — o único com `InternalsVisibleTo` da Api —, em classes **sem fixture**: não sobem host, banco nem container.

`tests/IdentityGateway.Api.FunctionalTests/Autorizacao/MontagemDaAutorizacao.cs`:

```csharp
using System.Security.Claims;
using IdentityGateway.Api.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Api.FunctionalTests.Autorizacao;

/// <summary>
/// Monta a autorização da Gateway fora do HTTP: o mesmo <c>AddAutorizacaoDaGateway</c> da produção, num contêiner só
/// com ele.
/// </summary>
/// <remarks>
/// Sem host, sem banco e sem container: estes testes rodam em milissegundos e exercitam o que o teste por HTTP não
/// distingue — por HTTP, um requirement que só deixa de aprovar e um que veta respondem o mesmo <c>403</c>.
/// </remarks>
internal static class MontagemDaAutorizacao
{
    /// <summary>O serviço de autorização, com as policies e a ordem de handlers da produção.</summary>
    /// <param name="ajustar">O que o teste acrescenta ao contêiner <b>depois</b> do registro da produção.</param>
    public static ServiceProvider Montar(Action<IServiceCollection>? ajustar = null)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddAutorizacaoDaGateway();
        ajustar?.Invoke(services);

        return services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>Um usuário autenticado com os claims que o token do Keycloak traria.</summary>
    public static ClaimsPrincipal Usuario(
        IEnumerable<string> roles, IEnumerable<string> tenantIds, string? sub = "5f1c0a2e-7c1d-4a55-9b0e-2f6f3c9d1a10")
    {
        List<Claim> claims = [.. roles.Select(role => new Claim("roles", role))];
        claims.AddRange(tenantIds.Select(tenantId => new Claim("tenant_id", tenantId)));

        if (sub is not null)
        {
            claims.Add(new Claim("sub", sub));
        }

        return new ClaimsPrincipal(
            new ClaimsIdentity(claims, authenticationType: "teste", nameType: "sub", roleType: "roles"));
    }

    /// <summary>O <c>HttpContext</c> de um pedido a uma rota com <c>{tenantId}</c>, como o roteamento o entrega.</summary>
    /// <param name="tenantIdDaRota">O texto cru do parâmetro, ou nulo para uma rota sem ele.</param>
    public static DefaultHttpContext Pedido(string? tenantIdDaRota)
    {
        DefaultHttpContext contexto = new();

        if (tenantIdDaRota is not null)
        {
            contexto.Request.RouteValues[SameTenantRequirement.ParametroDaRota] = tenantIdDaRota;
        }

        return contexto;
    }
}

/// <summary>
/// Um handler que aprova todo requirement ainda pendente — o pior vizinho que um requirement pode ter.
/// </summary>
/// <remarks>
/// É o que torna o <c>Fail()</c> observável: com ele no contêiner, um requirement que só deixasse de dar
/// <c>Succeed</c> seria satisfeito aqui, e a policy passaria. Só o <c>Fail()</c> sobrevive a ele.
/// </remarks>
internal sealed class HandlerQueAprovaTudo : IAuthorizationHandler
{
    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        foreach (IAuthorizationRequirement requirement in context.PendingRequirements.ToList())
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
```

- [ ] **Passo 2: Os testes da policy**

`tests/IdentityGateway.Api.FunctionalTests/Autorizacao/TenantAdminPolicyTests.cs`:

```csharp
using System.Security.Claims;
using IdentityGateway.Api.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Api.FunctionalTests.Autorizacao;

/// <summary>
/// A policy <c>TenantAdmin</c> de verdade, fora do HTTP: cada caminho de negação termina num <c>Fail()</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Por que não basta o teste por HTTP.</b> Um requirement que faz <c>return</c> onde devia fazer <c>Fail()</c>
/// responde <c>403</c> do mesmo jeito — enquanto não houver outro handler que o aprove. No dia em que houver (uma
/// policy de leitura para o platform-admin, um handler de recurso), o <c>return</c> vira acesso. Aqui há um handler que
/// aprova tudo, de propósito: só o <c>Fail()</c> sobrevive a ele.
/// </para>
/// <para>
/// <b>Uma causa por caso.</b> Cada caso reprova em exatamente um requirement e passa nos outros: se dois reprovassem,
/// o <c>Fail()</c> de um esconderia o <c>return</c> do outro.
/// </para>
/// </remarks>
public sealed class TenantAdminPolicyTests
{
    private const string Tenant = "0199a000-0000-7000-8000-00000000000a";

    private const string OutroTenant = "0199a000-0000-7000-8000-00000000000b";

    private static readonly string[] SoTenantAdmin = ["tenant-admin"];

    private static readonly string[] SoOTenant = [Tenant];

    private static async Task<AuthorizationResult> AutorizarAsync(
        ClaimsPrincipal usuario, object? recurso, bool comHandlerQueAprovaTudo)
    {
        await using ServiceProvider provider = MontagemDaAutorizacao.Montar(services =>
        {
            if (comHandlerQueAprovaTudo)
            {
                services.AddSingleton<IAuthorizationHandler, HandlerQueAprovaTudo>();
            }
        });
        await using AsyncServiceScope escopo = provider.CreateAsyncScope();

        return await escopo.ServiceProvider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(usuario, recurso, Policies.TenantAdmin);
    }

    private static void DeveTerVetado<TQuemVeta>(AuthorizationResult resultado, string caso)
    {
        resultado.Succeeded.Should().BeFalse($"{caso}: a policy não pode passar, nem com um handler que aprova tudo");
        resultado.Failure!.FailCalled.Should().BeTrue($"{caso}: a negação precisa ser um Fail(), e não a falta de Succeed");

        // Quem vetou. Depois do primeiro Fail() nenhum outro handler roda, e por isso há um motivo só — o do
        // requirement do caso. Se ele deixasse de vetar, outro vetaria no lugar dele mais adiante, e é esta asserção
        // que diria qual.
        resultado.Failure.FailureReasons.Select(motivo => motivo.Handler.GetType())
            .Should().Equal([typeof(TQuemVeta)], $"{caso}: quem veta é o requirement do caso");
    }

    [Theory]
    [InlineData("próprio tenant", Tenant, Tenant)]
    [InlineData("rota em maiúsculas", "0199A000-0000-7000-8000-00000000000A", Tenant)]
    [InlineData("rota no formato N", "0199a00000007000800000000000000a", Tenant)]
    [InlineData("claim em maiúsculas", Tenant, "0199A000-0000-7000-8000-00000000000A")]
    public async Task TenantAdminDoTenantDaRota_Passa(string caso, string rota, string claim)
    {
        // Controles positivos, SEM o handler que aprova tudo: são os próprios requirements que aprovam. A comparação
        // é por Guid — a mesma identidade escrita de outro jeito é o mesmo tenant.
        ClaimsPrincipal usuario = MontagemDaAutorizacao.Usuario(SoTenantAdmin, [claim]);

        AuthorizationResult resultado = await AutorizarAsync(
            usuario, MontagemDaAutorizacao.Pedido(rota), comHandlerQueAprovaTudo: false);

        resultado.Succeeded.Should().BeTrue(caso);
    }

    [Theory]
    [InlineData("papel de outro nível", new[] { "reader" })]
    [InlineData("papel com outra caixa", new[] { "Tenant-Admin" })]
    [InlineData("sem o claim roles", new string[0])]
    public async Task SemOPapelTenantAdmin_Veta(string caso, string[] roles)
    {
        ClaimsPrincipal usuario = MontagemDaAutorizacao.Usuario(roles, SoOTenant);

        AuthorizationResult resultado = await AutorizarAsync(
            usuario, MontagemDaAutorizacao.Pedido(Tenant), comHandlerQueAprovaTudo: true);

        DeveTerVetado<RoleRequirement>(resultado, caso);
    }

    [Fact]
    public async Task PlatformAdminQueTambemETenantAdminDoProprioTenant_Veta()
    {
        // Separação de funções: tem o papel, tem o tenant, e mesmo assim não entra. Sem este veto, somar tenant-admin
        // e um tenant_id a uma conta de plataforma contornaria o "platform-admin não lê tenant sem auditoria".
        ClaimsPrincipal usuario = MontagemDaAutorizacao.Usuario(["platform-admin", "tenant-admin"], SoOTenant);

        AuthorizationResult resultado = await AutorizarAsync(
            usuario, MontagemDaAutorizacao.Pedido(Tenant), comHandlerQueAprovaTudo: true);

        DeveTerVetado<NotPlatformAdminRequirement>(resultado, "platform-admin + tenant-admin");
    }

    [Theory]
    [InlineData("tenant_id de outro tenant", new[] { OutroTenant })]
    [InlineData("tenant_id ausente", new string[0])]
    [InlineData("tenant_id vazio", new[] { "" })]
    [InlineData("tenant_id que não é GUID", new[] { "acme" })]
    [InlineData("tenant_id com espaço antes", new[] { " " + Tenant })]
    [InlineData("tenant_id com espaço depois", new[] { Tenant + " " })]
    [InlineData("tenant_id entre chaves", new[] { "{" + Tenant + "}" })]
    [InlineData("tenant_id no formato N", new[] { "0199a00000007000800000000000000a" })]
    [InlineData("dois tenant_id: o próprio e outro", new[] { Tenant, OutroTenant })]
    [InlineData("dois tenant_id: outro e o próprio", new[] { OutroTenant, Tenant })]
    [InlineData("dois tenant_id iguais ao próprio", new[] { Tenant, Tenant })]
    public async Task TenantDoTokenQueNaoEODaRota_Veta(string caso, string[] tenantIds)
    {
        ClaimsPrincipal usuario = MontagemDaAutorizacao.Usuario(SoTenantAdmin, tenantIds);

        AuthorizationResult resultado = await AutorizarAsync(
            usuario, MontagemDaAutorizacao.Pedido(Tenant), comHandlerQueAprovaTudo: true);

        DeveTerVetado<SameTenantRequirement>(resultado, caso);
    }

    [Theory]
    [InlineData("rota sem tenantId", null)]
    [InlineData("tenantId da rota que não é GUID", "acme")]
    [InlineData("tenantId da rota vazio", "")]
    public async Task RotaSemUmTenantIdUtilizavel_Veta(string caso, string? rota)
    {
        // Rota com policy de tenant e sem {tenantId} é erro de configuração; o teste de subida a reprova antes. Se
        // chegar aqui, nega.
        ClaimsPrincipal usuario = MontagemDaAutorizacao.Usuario(SoTenantAdmin, SoOTenant);

        AuthorizationResult resultado = await AutorizarAsync(
            usuario, MontagemDaAutorizacao.Pedido(rota), comHandlerQueAprovaTudo: true);

        DeveTerVetado<SameTenantRequirement>(resultado, caso);
    }

    [Fact]
    public async Task RecursoQueNaoEHttpContext_Veta()
    {
        // Quem chamar a policy à mão, com outro recurso (ou nenhum), não tem rota de onde ler o tenant.
        ClaimsPrincipal usuario = MontagemDaAutorizacao.Usuario(SoTenantAdmin, SoOTenant);

        AuthorizationResult semRecurso = await AutorizarAsync(usuario, recurso: null, comHandlerQueAprovaTudo: true);
        AuthorizationResult outroRecurso = await AutorizarAsync(usuario, new object(), comHandlerQueAprovaTudo: true);

        DeveTerVetado<SameTenantRequirement>(semRecurso, "sem recurso");
        DeveTerVetado<SameTenantRequirement>(outroRecurso, "recurso que não é HttpContext");
    }
}
```

Quatro coisas sobre os casos:
- **Cada veto diz quem vetou** (`DeveTerVetado<TQuemVeta>`). Com `InvokeHandlersAfterFailure` desligado há um motivo só, o do primeiro `Fail()`. Sem essa asserção, quando a Tarefa 14 puser a pertença na policy, um `SameTenantRequirement` que deixasse de vetar passaria despercebido nos casos de rota e de recurso: o handler da pertença vetaria no lugar dele, pelos mesmos motivos.
- **`tenant_id` com espaço** está na lista porque `Guid.TryParseExact(…, "D")` aceita espaço nas pontas. É o mesmo achado do `sub`, na Tarefa 8.
- **Um array de um elemento** (`"tenant_id": ["…"]`) não está: o `ClaimsPrincipal` o entrega como um claim só, indistinguível do texto, e é o mesmo tenant — não há ambiguidade a recusar.
- **O claim em maiúsculas passa**: é o formato `D`, e a comparação é por `Guid`.

- [ ] **Passo 3: Rodar e ver falhar**

Run: `dotnet build tests/IdentityGateway.Api.FunctionalTests`
Expected: FAIL de compilação — `AddAutorizacaoDaGateway`, `Policies.TenantAdmin` e `SameTenantRequirement` não existem. (É a única falha possível antes de os tipos existirem; o vermelho por asserção vem no Passo 7, por mutação.)

- [ ] **Passo 4: As policies e os três requirements**

Em `src/IdentityGateway.Api/Authorization/Policies.cs`, depois da constante `PlatformAdmin`:

```csharp

    /// <summary>
    /// Quem administra o tenant da rota: <c>tenant-admin</c>, sem <c>platform-admin</c>, com o <c>tenant_id</c> do token
    /// igual ao da rota.
    /// </summary>
    public const string TenantAdmin = "TenantAdmin";

    /// <summary>
    /// As policies que decidem pelo tenant da rota. Toda rota que usa uma delas precisa ter <c>{tenantId}</c> no
    /// template: sem o parâmetro, o <see cref="SameTenantRequirement"/> nega sempre. Um teste de subida confere.
    /// </summary>
    public static IReadOnlyList<string> DeTenant { get; } = [TenantAdmin];
```

`src/IdentityGateway.Api/Authorization/RoleRequirement.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;

namespace IdentityGateway.Api.Authorization;

/// <summary>
/// Exige um papel do catálogo no claim <c>roles</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Um handler próprio, e não <c>RequireClaim</c>.</b> O <c>RequireClaim</c> só deixa de dar <c>Succeed</c> quando o
/// claim falta — ele não chama <c>Fail()</c>. Em ASP.NET Core, um requirement sem <c>Succeed</c> e sem <c>Fail</c> está
/// só "ainda não satisfeito": qualquer outro handler que o aprove o satisfaz. <c>Fail()</c> veta, e nada o desfaz.
/// </para>
/// <para>
/// <b>O requirement é o próprio handler.</b> Não precisa de serviço nenhum, e assim roda dentro do
/// <c>PassThroughAuthorizationHandler</c>, na ordem em que a policy o declara (ver <see cref="AutorizacaoDaGateway"/>).
/// </para>
/// </remarks>
internal sealed class RoleRequirement(string role) : AuthorizationHandler<RoleRequirement>, IAuthorizationRequirement
{
    /// <summary>O papel exigido, como o realm o escreve.</summary>
    public string Role { get; } = role;

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RoleRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (context.User.HasClaim("roles", requirement.Role))
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(new AuthorizationFailureReason(this, "O papel exigido não está no token."));
        }

        return Task.CompletedTask;
    }
}
```

`src/IdentityGateway.Api/Authorization/NotPlatformAdminRequirement.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;

namespace IdentityGateway.Api.Authorization;

/// <summary>
/// Separação de funções: quem traz <c>platform-admin</c> não age como administrador de tenant.
/// </summary>
/// <remarks>
/// <para>
/// A hierarquia de papéis é teto de atribuição, não herança de acesso: estar acima de <c>tenant-admin</c> limita o
/// que o platform-admin pode conceder, e não lhe dá o que o <c>tenant-admin</c> acessa.
/// </para>
/// <para>
/// <b>Por que negar quem acumula os dois papéis.</b> O service account da Gateway atribui <c>platform-admin</c>
/// (o <c>manage-users</c> o permite). Sem esta negação, o "platform-admin não lê tenant sem auditoria" só valeria
/// para a conta que não acumula papéis — bastaria somar <c>tenant-admin</c> e um <c>tenant_id</c> para contorná-lo.
/// </para>
/// </remarks>
internal sealed class NotPlatformAdminRequirement
    : AuthorizationHandler<NotPlatformAdminRequirement>, IAuthorizationRequirement
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, NotPlatformAdminRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (context.User.HasClaim("roles", "platform-admin"))
        {
            context.Fail(new AuthorizationFailureReason(this, "Conta de plataforma não age como administrador de tenant."));
        }
        else
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
```

`src/IdentityGateway.Api/Authorization/SameTenantRequirement.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace IdentityGateway.Api.Authorization;

/// <summary>
/// Garante que o tenant do token é o tenant da rota.
/// </summary>
/// <remarks>
/// <para>
/// Sem esta verificação, qualquer administrador de tenant operaria sobre qualquer tenant: bastaria trocar o id na URL
/// (BOLA/IDOR).
/// </para>
/// <para>
/// <b>Todo caminho que não é sucesso chama <c>Fail()</c>.</b> Um requirement sem <c>Succeed</c> e sem <c>Fail</c> fica
/// só "ainda não satisfeito", e outro handler poderia satisfazê-lo. É por isso que o acesso do platform-admin à
/// leitura de tenant <b>não</b> é um segundo handler deste requirement: o <c>Fail()</c> daqui o vetaria. Ele será
/// uma policy própria.
/// </para>
/// </remarks>
internal sealed class SameTenantRequirement : AuthorizationHandler<SameTenantRequirement>, IAuthorizationRequirement
{
    /// <summary>O nome do parâmetro de rota que toda rota com policy de tenant precisa ter. Há teste de subida.</summary>
    internal const string ParametroDaRota = "tenantId";

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, SameTenantRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (MesmoTenant(context))
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(new AuthorizationFailureReason(this, "O tenant da rota e o do token não conferem."));
        }

        return Task.CompletedTask;
    }

    private static bool MesmoTenant(AuthorizationHandlerContext context)
    {
        // Com endpoint routing, o Resource é o próprio HttpContext.
        if (context.Resource is not HttpContext http)
        {
            return false;
        }

        // GetRouteValue devolve o texto cru da URL, mesmo com a restrição :guid — que aceita os mesmos formatos do
        // Guid.TryParse. Rota sem {tenantId} é erro de configuração, e o teste de subida impede que chegue aqui.
        if (!Guid.TryParse(http.GetRouteValue(ParametroDaRota)?.ToString(), out Guid daRota))
        {
            return false;
        }

        // Exatamente UM claim. O Keycloak nunca emite dois (o mapper não é multivalorado); aceitar "o primeiro", "o
        // último" ou "algum" aceitaria um token forjado com o tenant da vítima numa das posições.
        if (context.User.FindAll("tenant_id").Take(2).ToArray() is not [Claim claim])
        {
            return false;
        }

        // O claim, só no formato D, que é como o Keycloak o emite. O tamanho antes do parse: Guid.TryParseExact tolera
        // espaço nas pontas, e o formato D tem exatamente 36 caracteres. A comparação é por Guid, e não por texto: a
        // rota com o GUID em maiúsculas é o mesmo tenant.
        return claim.Value is { Length: 36 }
            && Guid.TryParseExact(claim.Value, "D", out Guid doToken)
            && doToken == daRota;
    }
}
```

**A rota aceita qualquer formato de GUID; o claim, só o `D`.** A §4.3 da spec diz "os dois no formato `D`", e a §5.2 tem como controle positivo a rota no formato `N` respondendo `200`. O plano fica com a §5.2: a restrição `:guid` da rota já aceita os formatos do `Guid.TryParse`, e recusar aqui um formato que a rota aceitou daria `403` ao dono do tenant por causa de hifens. O claim é emitido pelo Keycloak, sempre no formato `D`; qualquer outra forma é token forjado ou atributo adulterado. O handoff da D1 registra a divergência para o autor.

- [ ] **Passo 5: O registro da autorização num método só**

`src/IdentityGateway.Api/Authorization/AutorizacaoDaGateway.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;

namespace IdentityGateway.Api.Authorization;

/// <summary>
/// O registro da autorização da Gateway: as policies, a policy de fallback e a ordem dos handlers.
/// </summary>
/// <remarks>
/// <b>Um método só, usado pela produção e pelos testes unitários.</b> A ordem em que os handlers são registrados é
/// parte da segurança (abaixo), e um teste que montasse a autorização por conta própria provaria outra ordem.
/// </remarks>
internal static class AutorizacaoDaGateway
{
    public static IServiceCollection AddAutorizacaoDaGateway(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthorization(options =>
        {
            // Todo endpoint exige usuário autenticado, a menos que declare AllowAnonymous ou outra policy.
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

            // Depois do primeiro Fail(), nenhum outro handler roda. É global: vale para todas as policies. Por isso a
            // negação não pode ser auditada por um handler — a auditoria nasce no ProblemDetailsDeAutorizacao, que vê
            // todas.
            options.InvokeHandlersAfterFailure = false;

            // Claim plano, não RequireRole: o papel chega no claim "roles", com esse nome (MapInboundClaims desligado).
            options.AddPolicy(Policies.PlatformAdmin, policy => policy.RequireClaim("roles", "platform-admin"));

            // A ordem dos requirements é a ordem em que rodam: os que só leem o token primeiro.
            options.AddPolicy(Policies.TenantAdmin, policy => policy.AddRequirements(
                new RoleRequirement("tenant-admin"),
                new NotPlatformAdminRequirement(),
                new SameTenantRequirement()));
        });

        return services;
    }
}
```

Em `src/IdentityGateway.Api/DependencyInjection.cs`, no método `AddAutenticacao`, trocar o bloco inteiro do `services.AddAuthorization(options => { … });` — as duas linhas de dentro, a do `AddPolicy` e a do `FallbackPolicy`, saem com ele — por:

```csharp
        // As policies, a policy de fallback e a ordem dos handlers: um método só, que os testes unitários também usam.
        services.AddAutorizacaoDaGateway();
```

As duas linhas acima dele (`AddProblemDetails` e o `AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemDetailsDeAutorizacao>`) ficam. No comentário XML do método, o parágrafo "Claim plano, não `RequireRole`" continua valendo.

`InvokeHandlersAfterFailure = false` é novo e **global**: vale também para a policy `PlatformAdmin` e para a de fallback. Nenhuma delas tem mais de um handler, e nada muda para elas.

- [ ] **Passo 6: Rodar e ver passar**

Run: `dotnet build IdentityGateway.slnx`
Expected: `0 Aviso(s)`, `0 Erro(s)`.

Run: `dotnet test tests/IdentityGateway.Api.FunctionalTests --filter-class "*TenantAdminPolicyTests"`
Expected: `total: 23`, `falhou: 0`, em poucos segundos — nenhum container sobe.

Run: `dotnet test tests/IdentityGateway.Api.FunctionalTests`
Expected: verde, com 23 testes a mais que na `main`. São os funcionais da D1 que provam que mover o registro não mudou nada: o caminho não mapeado sem token continua `401` (a `FallbackPolicy`), e `TodoEndpoint_TemPolicyNomeadaOuAnonimatoDeclarado` continua verde.

- [ ] **Passo 7: 🧪 Provas por mutação**

Antes da primeira: `git add -A src tests`. Reverter cada uma com `git restore src`.

Run (a cada mutação): `dotnet test tests/IdentityGateway.Api.FunctionalTests --filter-class "*TenantAdminPolicyTests"`

| # | Mutação | Vermelho esperado |
|---|---|---|
| 1 | Em `RoleRequirement`, apagar o `else { context.Fail(…); }` | 3: os três casos de `SemOPapelTenantAdmin_Veta` ("a policy não pode passar, nem com um handler que aprova tudo") |
| 2 | Em `NotPlatformAdminRequirement`, trocar o `context.Fail(…);` por `return Task.CompletedTask;` | 1: `PlatformAdminQueTambemETenantAdminDoProprioTenant_Veta` |
| 3 | Em `SameTenantRequirement`, apagar o `else { context.Fail(…); }` | 15: os onze de `TenantDoTokenQueNaoEODaRota_Veta`, os três de `RotaSemUmTenantIdUtilizavel_Veta` e `RecursoQueNaoEHttpContext_Veta` |
| 4 | Usar o **primeiro** claim: `if (context.User.FindFirst("tenant_id") is not Claim claim)` | 2: "o próprio e outro" e "iguais ao próprio" |
| 5 | Usar o **último**: `if (context.User.FindAll("tenant_id").LastOrDefault() is not Claim claim)` | 2: "outro e o próprio" e "iguais ao próprio" |
| 5a | Aceitar **algum** claim: tirar o `if` do claim único e devolver `context.User.FindAll("tenant_id").Any(claim => claim.Value is { Length: 36 } && Guid.TryParseExact(claim.Value, "D", out Guid doToken) && doToken == daRota)` | 3: "o próprio e outro", "outro e o próprio" e "iguais ao próprio" |
| 6 | Comparar como texto: trocar `&& doToken == daRota` por `&& string.Equals(claim.Value, http.GetRouteValue(ParametroDaRota)?.ToString(), StringComparison.Ordinal)` | 3: os controles "rota em maiúsculas", "rota no formato N" e "claim em maiúsculas" |
| 7 | Tirar o `claim.Value is { Length: 36 } &&` | 2: "espaço antes" e "espaço depois" |
| 8 | Trocar o retorno inteiro por `return Guid.TryParse(claim.Value, out Guid doToken) && doToken == daRota;` | 4: os dois de espaço, "entre chaves" e "formato N" |
| 9 | Em `AutorizacaoDaGateway`, tirar a linha `new NotPlatformAdminRequirement(),` da policy | 1: `PlatformAdminQueTambemETenantAdminDoProprioTenant_Veta` |

As mutações 1 a 3 são as da §5.3 da spec ("`return` no lugar de `Fail()`"), e **só estes testes as pegam**: por HTTP, sem outro handler, a rota continuaria respondendo `403`. As contagens valem também no fim da D2, com a pertença já na policy (a mutação 3 continua em 15 nesta classe, por causa da asserção de quem vetou) — conferido ao escrever este plano.

- [ ] **Passo 8: Commit**

```bash
git add src/IdentityGateway.Api tests/IdentityGateway.Api.FunctionalTests
git commit -m "feat: policy TenantAdmin com papel, separacao de funcoes e tenant da rota

Tres requirements que sao o proprio handler: RoleRequirement (tenant-admin
no claim roles), NotPlatformAdminRequirement (quem traz platform-admin nao
age como admin de tenant) e SameTenantRequirement (exatamente um claim
tenant_id, no formato D, igual como Guid ao tenantId da rota). Todo caminho
que nao e sucesso chama Fail(). O registro da autorizacao passa a um metodo
so, AddAutorizacaoDaGateway, com a policy de fallback e
InvokeHandlersAfterFailure desligado. Nenhuma rota usa a policy ainda.

Testes unitarios com a policy real e um handler que aprova tudo. Mutacoes:
return no lugar de Fail() em cada requirement, primeiro e ultimo claim,
comparacao como texto, parse sem o tamanho e sem o formato D, e a policy
sem o NotPlatformAdminRequirement."
```

Run: `git log -1 --format=%B | grep -Eci "co-authored|generated with"`
Expected: `0`.

---

### Tarefa 14: A pertença no banco — `IMemberQueries` e o `MemberRequirement`

Spec: §4.3 (linha `MemberRequirement` da tabela; "A ordem de registro é explícita"; "A pertença é lida por uma porta da Application"), §5.1 (linhas "Unitário da autorização", "Integração, PostgreSQL" e a de arquitetura da D2), §5.3 ("Registrar o handler do `MemberRequirement` antes do `AddAuthorization`", "Tirar o `MemberRequirement`, ou aceitar qualquer status"), D-f, D-j, ADR-011.

O `tenant_id` do token é forjável por quem tem a chave da Gateway: o mapper do claim recua para o atributo de um grupo do Keycloak, e o `manage-users` cria grupos (Tarefa 5, teste de caracterização). A quarta camada da policy confere no banco da Gateway que o `sub` é `Member` do tenant da rota.

Duas propriedades precisam de teste, e são elas que organizam a tarefa: **quais estados do membro passam** (só `Invited` e `Active`; um estado novo no enum nega até alguém decidir) e **quando o banco é consultado** (só para quem já passou nas três camadas do token — senão o tempo de resposta diria a qualquer token autenticado se um `sub` é membro de um tenant).

**Arquivos:**
- Create: `src/IdentityGateway.Application/Common/Abstractions/IMemberQueries.cs`
- Create: `src/IdentityGateway.Infrastructure/Persistence/Queries/MemberQueries.cs`
- Modify: `src/IdentityGateway.Infrastructure/DependencyInjection.cs`
- Create: `src/IdentityGateway.Api/Authorization/MemberRequirement.cs`, `MemberRequirementHandler.cs`
- Modify: `src/IdentityGateway.Api/Authorization/AutorizacaoDaGateway.cs`, `Policies.cs`
- Create (testes): `tests/IdentityGateway.Infrastructure.IntegrationTests/Persistence/MemberQueriesTests.cs`; `tests/IdentityGateway.Api.FunctionalTests/Autorizacao/PertencaFalsa.cs`, `Autorizacao/PertencaNaPolicyTenantAdminTests.cs`
- Modify (testes): `tests/IdentityGateway.Api.FunctionalTests/Autorizacao/TenantAdminPolicyTests.cs`, `IdentityGateway.Api.FunctionalTests.csproj`; `tests/IdentityGateway.ArchitectureTests/RegrasDaApiTests.cs`, `RegrasDeDominioTests.cs`

**Interfaces:**
- Consome: `AutorizacaoDaGateway.AddAutorizacaoDaGateway`, `SameTenantRequirement.ParametroDaRota`, `MontagemDaAutorizacao`, `HandlerQueAprovaTudo` (Tarefa 13); `Member`, `MemberStatus`, `ExternalUserId.From(string)`, `TenantId`, `AppDbContext.Members` (existentes).
- Produz:
  - `public interface IMemberQueries` (Application): `Task<MemberStatus?> GetStatusAsync(TenantId tenantId, ExternalUserId externalUserId, CancellationToken cancellationToken = default)`.
  - `internal sealed class MemberQueries(AppDbContext context) : IMemberQueries` (Infrastructure), registrada `Scoped`.
  - `internal sealed class MemberRequirement : IAuthorizationRequirement` e `internal sealed class MemberRequirementHandler(IMemberQueries members) : AuthorizationHandler<MemberRequirement>` (Api).
  - Nos testes: `internal sealed class PertencaFalsa(MemberStatus? status) : IMemberQueries`, com `int Consultas` e `(TenantId TenantId, ExternalUserId ExternalUserId)? Ultima`.

- [ ] **Passo 1: A porta e o teste contra o PostgreSQL**

`src/IdentityGateway.Application/Common/Abstractions/IMemberQueries.cs`:

```csharp
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Application.Common.Abstractions;

/// <summary>
/// Leitura da pertença de um ator a um tenant.
/// </summary>
/// <remarks>
/// <para>
/// <b>O tenant está na assinatura (§6.4).</b> Não existe leitura de membro só pelo <c>sub</c>: quem pergunta diz de
/// qual tenant, e o membro de outro tenant não é achado. É a mesma regra do repositório de sub-recurso, aplicada à
/// consulta.
/// </para>
/// <para>
/// <b>Porta de consulta, e não método novo do <see cref="IMemberRepository"/>.</b> O repositório devolve o agregado
/// rastreado, para ser alterado; aqui é uma projeção sem rastreamento, de um campo, para a autorização decidir. O
/// repositório continua só com <c>Add</c>.
/// </para>
/// </remarks>
public interface IMemberQueries
{
    /// <summary>O status do membro <c>(tenant, sub)</c>, ou nulo se o ator não for membro do tenant.</summary>
    Task<MemberStatus?> GetStatusAsync(
        TenantId tenantId, ExternalUserId externalUserId, CancellationToken cancellationToken = default);
}
```

`tests/IdentityGateway.Infrastructure.IntegrationTests/Persistence/MemberQueriesTests.cs`:

```csharp
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.Persistence;
using IdentityGateway.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;

namespace IdentityGateway.Infrastructure.IntegrationTests.Persistence;

/// <summary>
/// A leitura da pertença, contra o PostgreSQL: pelo tenant <b>e</b> pelo <c>sub</c>, nunca só por um deles.
/// </summary>
/// <remarks>
/// É a consulta de que a policy <c>TenantAdmin</c> depende (ADR-011). O erro que estes testes existem para pegar é o
/// filtro que esquece o tenant: ele acharia o membro de outro tenant e diria "é membro".
/// </remarks>
public sealed class MemberQueriesTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static TenantSlug SlugUnico() => TenantSlug.Create($"mq-{Guid.NewGuid():N}"[..18]).Value;

    private static ExternalUserId SubUnico() => ExternalUserId.From(Guid.NewGuid().ToString());

    /// <summary>Registra um tenant e o ativa pelo caminho de domínio, com o admin dado como membro.</summary>
    private async Task<TenantId> TenantComAdminAsync(ExternalUserId sub, CancellationToken ct)
    {
        var tenant = Tenant.Register(
            "Consultas", SlugUnico(), new Plan(PlanTier.Free, 5, 1), PostgresFixture.EmailDoAdmin(), PostgresFixture.Agora);

        await using AppDbContext contexto = postgres.CriarContexto();
        contexto.Tenants.Add(tenant);
        await contexto.SaveChangesAsync(ct);

        Member admin = tenant.CompleteProvisioning($"org-{Guid.NewGuid():N}", sub, PostgresFixture.Agora);
        contexto.Members.Add(admin);
        await contexto.SaveChangesAsync(ct);

        return tenant.Id;
    }

    private async Task<MemberStatus?> ConsultarAsync(TenantId tenant, ExternalUserId sub, CancellationToken ct)
    {
        // Contexto novo: a consulta lê do banco, e não do que o contexto de escrita ainda tem rastreado.
        await using AppDbContext contexto = postgres.CriarContexto();

        return await new MemberQueries(contexto).GetStatusAsync(tenant, sub, ct);
    }

    [Fact]
    public async Task MembroDoTenant_DevolveOStatusGravado()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ExternalUserId sub = SubUnico();
        TenantId tenant = await TenantComAdminAsync(sub, ct);

        (await ConsultarAsync(tenant, sub, ct)).Should().Be(MemberStatus.Invited);

        // O status vem da coluna, e não de um valor fixo: muda no banco, muda na resposta.
        await using (AppDbContext contexto = postgres.CriarContexto())
        {
            await contexto.Database.ExecuteSqlAsync(
                $"UPDATE members SET status = 'Deactivated' WHERE tenant_id = {tenant.Value}", ct);
        }

        (await ConsultarAsync(tenant, sub, ct)).Should().Be(MemberStatus.Deactivated);
    }

    [Fact]
    public async Task MembroDeOutroTenant_NaoEAchado()
    {
        // O mesmo sub é membro do tenant A. Perguntar por ele no tenant B responde nulo — e não o status dele em A.
        CancellationToken ct = TestContext.Current.CancellationToken;
        ExternalUserId sub = SubUnico();
        TenantId tenantA = await TenantComAdminAsync(sub, ct);
        TenantId tenantB = await TenantComAdminAsync(SubUnico(), ct);

        (await ConsultarAsync(tenantB, sub, ct)).Should().BeNull();
        (await ConsultarAsync(tenantA, sub, ct)).Should().Be(MemberStatus.Invited, "controle: no tenant dele, é achado");
    }

    [Fact]
    public async Task OutroSubNoMesmoTenant_NaoEAchado()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        TenantId tenant = await TenantComAdminAsync(SubUnico(), ct);

        (await ConsultarAsync(tenant, SubUnico(), ct)).Should().BeNull();
    }

    [Fact]
    public async Task TenantQueNaoExiste_DevolveNulo()
    {
        // Não há Member de um tenant que não existe: é por isso que a rota responde 403, e não 404, sem consultar o
        // tenant antes de autorizar.
        CancellationToken ct = TestContext.Current.CancellationToken;

        (await ConsultarAsync(TenantId.New(), SubUnico(), ct)).Should().BeNull();
    }
}
```

Não há teste de "a consulta não rastreia nada": uma projeção de um campo escalar nunca rastreia, com ou sem `AsNoTracking()`, e o teste ficaria verde sem o método (visto ao escrever este plano). O `AsNoTracking()` fica por convenção, como nas outras consultas.

Run: `dotnet build tests/IdentityGateway.Infrastructure.IntegrationTests`
Expected: FAIL de compilação — `MemberQueries` não existe.

- [ ] **Passo 2: A implementação e o registro**

`src/IdentityGateway.Infrastructure/Persistence/Queries/MemberQueries.cs`:

```csharp
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace IdentityGateway.Infrastructure.Persistence.Queries;

/// <summary>
/// Implementa <see cref="IMemberQueries"/> com uma projeção sem rastreamento.
/// </summary>
internal sealed class MemberQueries(AppDbContext context) : IMemberQueries
{
    /// <inheritdoc />
    /// <remarks>
    /// O filtro leva o tenant <b>e</b> o <c>sub</c>, e é o índice único <c>(tenant_id, external_user_id)</c> que
    /// responde: no máximo uma linha. O cast para o tipo anulável é o que faz "nenhuma linha" virar nulo, e não o
    /// primeiro valor do enum.
    /// </remarks>
    public Task<MemberStatus?> GetStatusAsync(
        TenantId tenantId, ExternalUserId externalUserId, CancellationToken cancellationToken) =>
        context.Members
            .AsNoTracking()
            .Where(membro => membro.TenantId == tenantId && membro.ExternalUserId == externalUserId)
            .Select(membro => (MemberStatus?)membro.Status)
            .SingleOrDefaultAsync(cancellationToken);
}
```

Em `src/IdentityGateway.Infrastructure/DependencyInjection.cs`, depois da linha `services.AddScoped<ITenantQueries, TenantQueries>();`:

```csharp
        services.AddScoped<IMemberQueries, MemberQueries>();
```

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter-class "*MemberQueriesTests"`
Expected: `total: 4`, `falhou: 0`.

- [ ] **Passo 3: 🧪 Mutações da consulta**

`git add -A src tests` antes; `git restore src` depois de cada uma.

| # | Mutação em `MemberQueries.GetStatusAsync` | Vermelho esperado |
|---|---|---|
| 1 | Tirar o tenant do filtro (`membro.TenantId == tenantId && `) | `MembroDeOutroTenant_NaoEAchado` — responde `Invited`, o status do membro no outro tenant |
| 2 | Tirar o `sub` do filtro (` && membro.ExternalUserId == externalUserId`) | `OutroSubNoMesmoTenant_NaoEAchado` e `MembroDeOutroTenant_NaoEAchado` |

A mutação 1 é o defeito que esta porta existe para impedir: a consulta "pelo `sub`" que esquece o tenant.

- [ ] **Passo 4: A pertença falsa e os testes da quarta camada**

Em `tests/IdentityGateway.Api.FunctionalTests/IdentityGateway.Api.FunctionalTests.csproj`, no `ItemGroup` das referências de projeto, depois da linha da Infrastructure (a `IMemberQueries` é usada no código dos testes, e o comentário do arquivo pede a referência explícita):

```xml
    <ProjectReference Include="..\..\src\IdentityGateway.Application\IdentityGateway.Application.csproj" />
```

`tests/IdentityGateway.Api.FunctionalTests/Autorizacao/PertencaFalsa.cs`:

```csharp
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Api.FunctionalTests.Autorizacao;

/// <summary>
/// Uma <see cref="IMemberQueries"/> de teste: responde o status combinado e conta quantas vezes foi consultada.
/// </summary>
/// <remarks>
/// A contagem é o que prova a ordem dos handlers: a pertença só pode ser consultada para quem já passou nas camadas
/// do token. Uma consulta com o papel ausente é uma consulta ao banco que qualquer token autenticado conseguiria
/// provocar, para qualquer tenant.
/// </remarks>
internal sealed class PertencaFalsa(MemberStatus? status) : IMemberQueries
{
    private int _consultas;

    /// <summary>Quantas vezes a porta foi consultada.</summary>
    public int Consultas => _consultas;

    /// <summary>O tenant e o <c>sub</c> da última consulta.</summary>
    public (TenantId TenantId, ExternalUserId ExternalUserId)? Ultima { get; private set; }

    public Task<MemberStatus?> GetStatusAsync(
        TenantId tenantId, ExternalUserId externalUserId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _consultas);
        Ultima = (tenantId, externalUserId);

        return Task.FromResult(status);
    }
}
```

`tests/IdentityGateway.Api.FunctionalTests/Autorizacao/PertencaNaPolicyTenantAdminTests.cs`:

```csharp
using System.Security.Claims;
using IdentityGateway.Api.Authorization;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Api.FunctionalTests.Autorizacao;

/// <summary>
/// A quarta camada da policy <c>TenantAdmin</c>: a pertença no banco (ADR-011), pela porta <c>IMemberQueries</c>.
/// </summary>
/// <remarks>
/// Aqui a porta é falsa e conta as consultas. O que se prova: quais estados do membro passam, que quem não é membro é
/// vetado, e que <b>a porta só é consultada para quem já passou nas três camadas do token</b>.
/// </remarks>
public sealed class PertencaNaPolicyTenantAdminTests
{
    private const string Tenant = "0199a000-0000-7000-8000-00000000000a";

    private const string OutroTenant = "0199a000-0000-7000-8000-00000000000b";

    private const string Sub = "5f1c0a2e-7c1d-4a55-9b0e-2f6f3c9d1a10";

    private static readonly string[] SoTenantAdmin = ["tenant-admin"];

    private static readonly string[] SoOTenant = [Tenant];

    /// <summary>
    /// A decisão para cada estado do membro, escrita à mão. Um estado novo no enum não está aqui — e o teste reprova
    /// até alguém decidir, em vez de herdar uma resposta.
    /// </summary>
    private static readonly Dictionary<MemberStatus, bool> PassaNaPertenca = new()
    {
        [MemberStatus.Invited] = true,
        [MemberStatus.Active] = true,
        [MemberStatus.Deactivated] = false,
        [MemberStatus.Expired] = false,
        [MemberStatus.Revoked] = false,
        [MemberStatus.Erased] = false,
    };

    public static TheoryData<MemberStatus> TodosOsEstados => [.. Enum.GetValues<MemberStatus>()];

    private static async Task<AuthorizationResult> AutorizarAsync(
        ClaimsPrincipal usuario, string? rota, PertencaFalsa pertenca, bool comHandlerQueAprovaTudo)
    {
        await using ServiceProvider provider = MontagemDaAutorizacao.Montar(services =>
        {
            services.AddSingleton<IMemberQueries>(pertenca);

            if (comHandlerQueAprovaTudo)
            {
                services.AddSingleton<IAuthorizationHandler, HandlerQueAprovaTudo>();
            }
        });
        await using AsyncServiceScope escopo = provider.CreateAsyncScope();

        return await escopo.ServiceProvider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(usuario, MontagemDaAutorizacao.Pedido(rota), Policies.TenantAdmin);
    }

    [Theory]
    [MemberData(nameof(TodosOsEstados))]
    public async Task CadaEstadoDoMembro_PassaOuEVetadoComoATabela(MemberStatus estado)
    {
        PassaNaPertenca.Should().ContainKey(
            estado, "estado novo no enum: decida se ele passa na pertença e acrescente à tabela deste teste");
        bool esperado = PassaNaPertenca[estado];
        ClaimsPrincipal usuario = MontagemDaAutorizacao.Usuario(SoTenantAdmin, SoOTenant);

        AuthorizationResult sozinho = await AutorizarAsync(
            usuario, Tenant, new PertencaFalsa(estado), comHandlerQueAprovaTudo: false);
        AuthorizationResult comVizinho = await AutorizarAsync(
            usuario, Tenant, new PertencaFalsa(estado), comHandlerQueAprovaTudo: true);

        sozinho.Succeeded.Should().Be(esperado);
        comVizinho.Succeeded.Should().Be(esperado, "um handler que aprova tudo não muda a decisão da pertença");

        if (!esperado)
        {
            comVizinho.Failure!.FailCalled.Should().BeTrue("a negação precisa ser um Fail()");
        }
    }

    [Fact]
    public async Task QuemNaoEMembroDoTenant_EVetado()
    {
        // O caso do ataque: papel certo, tenant_id certo (forjado por um grupo no Keycloak), e nenhum Member no banco.
        ClaimsPrincipal usuario = MontagemDaAutorizacao.Usuario(SoTenantAdmin, SoOTenant);
        PertencaFalsa pertenca = new(status: null);

        AuthorizationResult resultado = await AutorizarAsync(usuario, Tenant, pertenca, comHandlerQueAprovaTudo: true);

        resultado.Succeeded.Should().BeFalse();
        resultado.Failure!.FailCalled.Should().BeTrue();
        pertenca.Consultas.Should().Be(1, "controle: a negação veio da pertença, e não de uma camada anterior");
    }

    [Theory]
    [InlineData(Tenant)]
    [InlineData("0199A000-0000-7000-8000-00000000000A")]
    [InlineData("0199a00000007000800000000000000a")]
    public async Task APertenca_EConsultadaUmaVezComOTenantDaRotaEOSubDoToken(string rota)
    {
        ClaimsPrincipal usuario = MontagemDaAutorizacao.Usuario(SoTenantAdmin, SoOTenant, Sub);
        PertencaFalsa pertenca = new(MemberStatus.Active);

        AuthorizationResult resultado = await AutorizarAsync(usuario, rota, pertenca, comHandlerQueAprovaTudo: false);

        resultado.Succeeded.Should().BeTrue();
        pertenca.Consultas.Should().Be(1);
        pertenca.Ultima.Should().Be((new TenantId(Guid.Parse(Tenant)), ExternalUserId.From(Sub)));
    }

    [Theory]
    [InlineData("sem o papel tenant-admin", new[] { "reader" }, new[] { Tenant })]
    [InlineData("platform-admin que também é tenant-admin", new[] { "platform-admin", "tenant-admin" }, new[] { Tenant })]
    [InlineData("tenant-admin de outro tenant", new[] { "tenant-admin" }, new[] { OutroTenant })]
    [InlineData("tenant-admin sem tenant_id", new[] { "tenant-admin" }, new string[0])]
    public async Task QuemNaoPassaNasCamadasDoToken_NaoProvocaConsultaAoBanco(
        string caso, string[] roles, string[] tenantIds)
    {
        // O tempo de resposta não pode dizer se um sub é membro de um tenant: a pertença só é lida para quem já provou,
        // pelo token, que é tenant-admin daquele tenant. É a ordem de registro dos handlers que garante.
        ClaimsPrincipal usuario = MontagemDaAutorizacao.Usuario(roles, tenantIds);
        PertencaFalsa pertenca = new(MemberStatus.Active);

        AuthorizationResult resultado = await AutorizarAsync(usuario, Tenant, pertenca, comHandlerQueAprovaTudo: false);

        resultado.Succeeded.Should().BeFalse(caso);
        pertenca.Consultas.Should().Be(0, caso);
    }

    [Theory]
    [InlineData("sem sub", null)]
    [InlineData("sub vazio", "")]
    [InlineData("sub só com espaços", "   ")]
    public async Task TokenSemSubUtilizavel_EVetadoSemConsultar(string caso, string? sub)
    {
        // A autenticação já recusa token sem sub (401). Se um chegar aqui, não há de quem conferir a pertença.
        ClaimsPrincipal usuario = MontagemDaAutorizacao.Usuario(SoTenantAdmin, SoOTenant, sub);
        PertencaFalsa pertenca = new(MemberStatus.Active);

        AuthorizationResult resultado = await AutorizarAsync(usuario, Tenant, pertenca, comHandlerQueAprovaTudo: true);

        resultado.Succeeded.Should().BeFalse(caso);
        resultado.Failure!.FailCalled.Should().BeTrue(caso);
        pertenca.Consultas.Should().Be(0, caso);
    }

    [Theory]
    [InlineData("SUB", "sub")]
    [InlineData("sub", "SUB")]
    [InlineData("sub", "sub")]
    public async Task TokenComDoisClaimsDeSub_EVetadoSemConsultar(string primeiro, string segundo)
    {
        // O ClaimsPrincipal acha claims sem olhar a caixa do nome, e a autenticação só validou o "sub" do JSON. Com um
        // "SUB" ao lado, ler "o primeiro" conferiria a pertença de um sub que ninguém validou — e o membro seria outro.
        ClaimsPrincipal usuario = MontagemDaAutorizacao.Usuario(SoTenantAdmin, SoOTenant, sub: null);
        ((ClaimsIdentity)usuario.Identity!).AddClaims(
            [new Claim(primeiro, Sub), new Claim(segundo, "0199a000-0000-7000-8000-0000000000aa")]);
        PertencaFalsa pertenca = new(MemberStatus.Active);

        AuthorizationResult resultado = await AutorizarAsync(usuario, Tenant, pertenca, comHandlerQueAprovaTudo: true);

        resultado.Succeeded.Should().BeFalse();
        resultado.Failure!.FailCalled.Should().BeTrue();
        pertenca.Consultas.Should().Be(0);
    }
}
```

Em `tests/IdentityGateway.Api.FunctionalTests/Autorizacao/TenantAdminPolicyTests.cs`, a policy passa a precisar da porta. Acrescentar os dois `using`:

```csharp
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Members;
```

e, em `AutorizarAsync`, como primeiras linhas do lambda do `Montar` (antes do `if (comHandlerQueAprovaTudo)`):

```csharp
            // Nesta classe o ator é sempre membro ativo: o que se prova aqui são as três camadas do token.
            services.AddSingleton<IMemberQueries>(new PertencaFalsa(MemberStatus.Active));

```

Aqui o vermelho é de execução, e não de compilação: tudo o que os testes usam já existe, e o que falta é a policy consultar a pertença.

Run: `dotnet test tests/IdentityGateway.Api.FunctionalTests --filter-class "*PertencaNaPolicyTenantAdminTests"`
Expected: FAIL em 14 dos 20 — os quatro estados que deviam ser vetados (`Deactivated`, `Expired`, `Revoked`, `Erased`), `QuemNaoEMembroDoTenant_EVetado`, os três de `TokenSemSubUtilizavel_EVetadoSemConsultar`, os três de `TokenComDoisClaimsDeSub_EVetadoSemConsultar` e os três de `APertenca_EConsultadaUmaVez…` (`Consultas` é `0`): a policy ainda não consulta a pertença.

- [ ] **Passo 5: O requirement, o handler e a ordem de registro**

`src/IdentityGateway.Api/Authorization/MemberRequirement.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;

namespace IdentityGateway.Api.Authorization;

/// <summary>
/// A pertença no banco (ADR-011): o ator é <c>Member</c> do tenant da rota.
/// </summary>
/// <remarks>
/// O <c>tenant_id</c> do token é forjável por quem tem a chave da Gateway (um grupo do Keycloak com o atributo dá o
/// claim a qualquer usuário posto nele). Nas rotas de governança, o token não basta: o banco da Gateway confirma. Ao
/// contrário dos outros três requirements da policy, este não é o próprio handler — precisa de um serviço, e quem o
/// atende é o <see cref="MemberRequirementHandler"/>.
/// </remarks>
internal sealed class MemberRequirement : IAuthorizationRequirement;
```

`src/IdentityGateway.Api/Authorization/MemberRequirementHandler.cs`:

```csharp
using System.Security.Claims;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using Microsoft.AspNetCore.Authorization;

namespace IdentityGateway.Api.Authorization;

/// <summary>
/// Confere no banco se o <c>sub</c> do token é membro do tenant da rota, em <c>Invited</c> ou <c>Active</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Só consulta o banco para quem já passou nas camadas do token.</b> Se o contexto já falhou — papel ausente,
/// conta de plataforma, outro tenant —, sai sem consultar: o tempo de resposta não pode virar oráculo do vínculo entre
/// um <c>sub</c> e um tenant. Isso depende de este handler rodar <b>depois</b> dos outros três, e é o registro que
/// garante (ver <see cref="AutorizacaoDaGateway"/>).
/// </para>
/// <para>
/// <b>Pela porta <see cref="IMemberQueries"/>, e não pelo Mediator:</b> a Api só fala com o Mediator dentro dos
/// módulos. Os behaviors do pipeline não se aplicam a esta leitura.
/// </para>
/// <para>
/// <b><c>Invited</c> passa.</b> O aceite do convite acontece no Keycloak, e a Gateway ainda não o detecta: quem
/// concluiu o convite e entrou continua <c>Invited</c> aqui. Sai da lista quando o aceite for sincronizado.
/// </para>
/// </remarks>
internal sealed class MemberRequirementHandler(IMemberQueries members) : AuthorizationHandler<MemberRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, MemberRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        // Exatamente UM claim sub, como no tenant_id. O ClaimsPrincipal acha claims sem olhar a caixa do nome, e a
        // autenticação validou o "sub" do JSON: com um "SUB" ao lado, "o primeiro" poderia ser o que ninguém validou.
        if (context.HasFailed
            || context.Resource is not HttpContext http
            || !Guid.TryParse(http.GetRouteValue(SameTenantRequirement.ParametroDaRota)?.ToString(), out Guid tenantId)
            || context.User.FindAll("sub").Take(2).ToArray() is not [Claim { Value: var sub }]
            || string.IsNullOrWhiteSpace(sub))
        {
            context.Fail(new AuthorizationFailureReason(this, "A pertença não pôde ser verificada."));
            return;
        }

        // O sub vai como o Keycloak o emite, sem normalizar. O CancellationToken é o da requisição: o contexto de
        // autorização não tem um.
        MemberStatus? status = await members.GetStatusAsync(
            new TenantId(tenantId), ExternalUserId.From(sub), http.RequestAborted);

        // Lista fechada: um estado que o enum ganhe depois nega, até alguém decidir.
        if (status is MemberStatus.Invited or MemberStatus.Active)
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(new AuthorizationFailureReason(this, "O ator não é membro do tenant."));
        }
    }
}
```

`src/IdentityGateway.Api/Authorization/AutorizacaoDaGateway.cs` passa a ser, inteiro:

```csharp
using Microsoft.AspNetCore.Authorization;

namespace IdentityGateway.Api.Authorization;

/// <summary>
/// O registro da autorização da Gateway: as policies, a policy de fallback e a ordem dos handlers.
/// </summary>
/// <remarks>
/// <b>Um método só, usado pela produção e pelos testes unitários.</b> A ordem em que os handlers são registrados é
/// parte da segurança (abaixo), e um teste que montasse a autorização por conta própria provaria outra ordem.
/// </remarks>
internal static class AutorizacaoDaGateway
{
    public static IServiceCollection AddAutorizacaoDaGateway(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthorization(options =>
        {
            // Todo endpoint exige usuário autenticado, a menos que declare AllowAnonymous ou outra policy.
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

            // Depois do primeiro Fail(), nenhum outro handler roda. É global: vale para todas as policies. Por isso a
            // negação não pode ser auditada por um handler — a auditoria nasce no ProblemDetailsDeAutorizacao, que vê
            // todas.
            options.InvokeHandlersAfterFailure = false;

            // Claim plano, não RequireRole: o papel chega no claim "roles", com esse nome (MapInboundClaims desligado).
            options.AddPolicy(Policies.PlatformAdmin, policy => policy.RequireClaim("roles", "platform-admin"));

            // Os três primeiros só leem o token e rodam nesta ordem. O da pertença lê o banco e roda por último — o que
            // não vem desta lista, e sim da ordem de registro dos handlers, logo abaixo.
            options.AddPolicy(Policies.TenantAdmin, policy => policy.AddRequirements(
                new RoleRequirement("tenant-admin"),
                new NotPlatformAdminRequirement(),
                new SameTenantRequirement(),
                new MemberRequirement()));
        });

        // A ORDEM IMPORTA. Os três primeiros requirements são o próprio handler, e rodam dentro do
        // PassThroughAuthorizationHandler, que o AddAuthorization acabou de registrar. O handler da pertença entra
        // DEPOIS dele: se entrasse antes, rodaria primeiro, com o contexto ainda sem falha, e consultaria o banco
        // para qualquer tenantId da URL — com qualquer token autenticado.
        services.AddScoped<IAuthorizationHandler, MemberRequirementHandler>();

        return services;
    }
}
```

Em `src/IdentityGateway.Api/Authorization/Policies.cs`, o comentário de `TenantAdmin` passa a dizer as quatro camadas:

```csharp
    /// <summary>
    /// Quem administra o tenant da rota: <c>tenant-admin</c>, sem <c>platform-admin</c>, com o <c>tenant_id</c> do token
    /// igual ao da rota e <c>Member</c> desse tenant no banco, em <c>Invited</c> ou <c>Active</c> (ADR-011).
    /// </summary>
    public const string TenantAdmin = "TenantAdmin";
```

**Exatamente um claim `sub`.** A autenticação (Tarefa 8) valida a propriedade `sub` do JSON do token. O `ClaimsPrincipal`, porém, acha claims **sem olhar a caixa do nome**: num token com `"SUB"` e `"sub"`, `FindFirst("sub")` devolve o que vier primeiro — que pode ser o que ninguém validou (conferido pelo revisor de autorização, em memória, com o IdentityModel 8.19.2). O Keycloak não emite isso sem um mapper novo, que o `manage-users` não cria; mas a pertença é a única defesa contra o `tenant_id` forjado, e fica com a mesma regra do `tenant_id`: um claim só, ou veto.

**Por que a ordem de registro importa.** O `AddAuthorization` registra o `PassThroughAuthorizationHandler`, que roda os requirements que são o próprio handler — os três da Tarefa 13 —, na ordem da policy, e para no primeiro `Fail()` (com `InvokeHandlersAfterFailure` desligado). O `MemberRequirementHandler` vem do contêiner, e os handlers do contêiner rodam na ordem em que foram registrados. Registrado **depois** do `AddAuthorization`, ele roda depois do `PassThrough` — ou nem roda, se alguém já falhou. Registrado antes, rodaria primeiro, com o contexto ainda sem falha, e consultaria o banco para qualquer token autenticado.

Duas defesas, e cada uma esconde a falta da outra: o `InvokeHandlersAfterFailure = false` impede o handler de rodar depois de uma falha, e o `context.HasFailed` do handler o faz sair sem consultar se rodar. Ficam as duas, de propósito.

- [ ] **Passo 6: As duas regras de arquitetura**

Em `tests/IdentityGateway.ArchitectureTests/RegrasDaApiTests.cs`, logo **depois** do teste `Api_NaoReferenciaEfCore` (antes do comentário `/// <summary>` de "A Api não alcança repositório direto."):

```csharp
    /// <summary>
    /// A autorização da Api decide pelo token e pelas portas da Application — nunca pela Infrastructure.
    /// </summary>
    /// <remarks>
    /// O requirement da pertença precisa do banco, e o atalho é injetar o <c>DbContext</c> ou a classe de consulta
    /// direto no handler. A regra do EF Core acima pega o primeiro; esta pega o segundo. O caminho é a porta
    /// <c>IMemberQueries</c>, que tem o tenant na assinatura.
    /// </remarks>
    [Fact]
    public void AutorizacaoDaApi_NaoDependeDaInfrastructure()
    {
        const string autorizacao = "IdentityGateway.Api.Authorization";

        Types.InAssembly(Api).That().ResideInNamespace(autorizacao).GetTypes()
            .Should().NotBeEmpty("sem tipos no namespace, a regra passaria vazia");

        ArchTestResult resultado = Types.InAssembly(Api)
            .That()
            .ResideInNamespace(autorizacao)
            .Should()
            .NotHaveDependencyOn("IdentityGateway.Infrastructure")
            .GetResult();

        resultado.Should().NaoTerViolacao(
            "policy que alcança a Infrastructure lê o banco sem o tenant na assinatura; a pertença vem da porta "
            + "IMemberQueries, da Application");
    }

```

Em `tests/IdentityGateway.ArchitectureTests/RegrasDeDominioTests.cs`, acrescentar `using IdentityGateway.Domain.Tenants;` e, depois do teste `Member_NaoTemConstrutorNemFabricaPublicos` (antes de `private static bool EhEntidade`):

```csharp
    /// <summary>
    /// Os nomes dos estados de tenant e de membro são contrato: vão para o banco e para a API como texto.
    /// </summary>
    /// <remarks>
    /// Renomear um estado quebra as linhas já gravadas e quem lê o <c>status</c> da API. Acrescentar um exige decidir
    /// o que a pertença faz com ele (a policy <c>TenantAdmin</c> só aceita <c>Invited</c> e <c>Active</c>) — e este
    /// teste fica vermelho até a lista daqui ser atualizada, de propósito.
    /// </remarks>
    [Fact]
    public void NomesDosEstadosDeTenantEDeMembro_SaoContrato()
    {
        Enum.GetNames<TenantStatus>().Should().BeEquivalentTo(
            "Pending", "Active", "Suspending", "Suspended", "Terminating", "Terminated", "ProvisioningFailed");

        Enum.GetNames<MemberStatus>().Should().BeEquivalentTo(
            "Invited", "Active", "Deactivated", "Expired", "Revoked", "Erased");
    }

```

- [ ] **Passo 7: Rodar e ver passar**

Run: `dotnet build IdentityGateway.slnx`
Expected: `0 Aviso(s)`, `0 Erro(s)`.

Run: `dotnet test tests/IdentityGateway.Api.FunctionalTests --filter-namespace "*Autorizacao"`
Expected: `total: 43`, `falhou: 0` — os 23 da Tarefa 13 e os 20 desta.

Run: `dotnet test tests/IdentityGateway.ArchitectureTests`
Expected: verde, com 2 testes a mais.

Run: `dotnet test tests/IdentityGateway.Api.FunctionalTests`
Expected: verde, com 20 testes a mais que no fim da Tarefa 13.

- [ ] **Passo 8: 🧪 Provas por mutação**

`git add -A src tests` antes; `git restore src` depois de cada uma.

Run (mutações 1 a 9): `dotnet test tests/IdentityGateway.Api.FunctionalTests --filter-namespace "*Autorizacao"`

| # | Mutação | Vermelho esperado |
|---|---|---|
| 1 | Em `AutorizacaoDaGateway`, mover a linha `services.AddScoped<IAuthorizationHandler, MemberRequirementHandler>();` para **antes** do `services.AddAuthorization(` | 4: os quatro casos de `QuemNaoPassaNasCamadasDoToken_NaoProvocaConsultaAoBanco` (`Consultas` é `1`) |
| 2 | No handler, aceitar qualquer status: `if (status is not null)` | 4: `CadaEstadoDoMembro_…` para `Deactivated`, `Expired`, `Revoked` e `Erased` |
| 3 | No handler, só `Active`: `if (status is MemberStatus.Active)` | 1: `CadaEstadoDoMembro_…(Invited)` |
| 4 | Tirar `new MemberRequirement()` da policy (o `SameTenantRequirement` volta a fechar a lista) | 14: os mesmos do "ver falhar" do Passo 4 |
| 5 | No handler, apagar o `context.Fail(…)` da guarda (fica só o `return;`) | 6: os três de `TokenSemSubUtilizavel_EVetadoSemConsultar` e os três de `TokenComDoisClaimsDeSub_EVetadoSemConsultar` |
| 5a | No handler, ler **o primeiro** `sub`: trocar a linha do `FindAll("sub")` por `\|\| context.User.FindFirst("sub")?.Value is not { } sub` | 3: `TokenComDoisClaimsDeSub_EVetadoSemConsultar` — a pertença seria conferida contra um `sub` que a autenticação não validou |
| 6 | No handler, apagar o `else { context.Fail(…); }` do fim | 5: os quatro estados vetados e `QuemNaoEMembroDoTenant_EVetado` |
| 7 | `options.InvokeHandlersAfterFailure = true;`, **sozinha** | **nenhum** — equivalente: o `HasFailed` do handler cobre |
| 8 | Tirar o `context.HasFailed \|\|` da guarda do handler, **sozinha** | **nenhum** — equivalente: o `InvokeHandlersAfterFailure` cobre |
| 9 | As mutações 7 e 8 **juntas** | 4: os mesmos da mutação 1 |
| 10 | Em `MemberRequirementHandler`, acrescentar `private static readonly Type Vazamento = typeof(IdentityGateway.Infrastructure.Configuration.DatabaseOptions);` e `internal static string Nome => Vazamento.Name;` | `dotnet test tests/IdentityGateway.ArchitectureTests`: `AutorizacaoDaApi_NaoDependeDaInfrastructure` |
| 11 | Em `src/IdentityGateway.Domain/Members/MemberStatus.cs`, acrescentar um valor `Paused` | `dotnet test tests/IdentityGateway.ArchitectureTests`: `NomesDosEstadosDeTenantEDeMembro_SaoContrato`; e, no projeto funcional, `CadaEstadoDoMembro_…(Paused)` ("estado novo no enum: decida se ele passa na pertença…") |

As mutações 7 e 8 ficam registradas como executadas e **verdes**: são as "que se mascaram" da §5.3 da spec, e a 9 é a prova de que o par é pego.

- [ ] **Passo 9: Commit**

```bash
git add src tests
git commit -m "feat: pertenca no banco como quarta camada da policy TenantAdmin (ADR-011)

IMemberQueries.GetStatusAsync(tenant, sub) e a porta de consulta, com o
tenant na assinatura; MemberQueries a implementa com uma projecao sem
rastreamento. O MemberRequirementHandler aceita so Invited e Active, veta
todo o resto com Fail(), e so consulta o banco para quem ja passou nas tres
camadas do token: e registrado depois do AddAuthorization, e sai sem
consultar se o contexto ja falhou.

Testes: os estados do membro numa tabela escrita a mao, a contagem de
consultas com uma porta falsa, a consulta contra o PostgreSQL sem achar o
membro de outro tenant, a autorizacao da Api sem dependencia da
Infrastructure e os nomes dos estados travados. Mutacoes: handler
registrado antes do AddAuthorization, qualquer status, so Active, policy
sem o requirement, return no lugar de Fail(), o filtro sem o tenant e sem
o sub. InvokeHandlersAfterFailure ligado e o handler sem o HasFailed, cada
um sozinho, ficam verdes; juntos, vermelhos."
```

Run: `git log -1 --format=%B | grep -Eci "co-authored|generated with"`
Expected: `0`.

---

### Tarefa 15: `GET /api/v1/tenants/{tenantId}` — a rota, o read model e a suíte negativa de autorização

Spec: §4.3 ("`GET /api/v1/tenants/{tenantId:guid}`", inteiro), §5.1 (linhas da D2: "Funcional, OIDC falso", "Funcional, ordem dos handlers", "Integração, PostgreSQL", "Vazamento do e-mail" e a de arquitetura), §5.2 (casos `F` da D2), §5.3 (mutações da D2), D-b, D-k.

A primeira rota de tenant: o administrador lê o próprio tenant. É também a primeira vez que a regra de isolamento número um ("o tenant do token é o tenant da rota") tem uma rota onde falhar — e por isso esta tarefa traz o teste de subida que a v2.6 prometia e nunca teve objeto.

O contrato, que os testes travam:
- `200` com **exatamente** as chaves `tenantId`, `name`, `slug`, `status`, `plan` (`tier`, `maxUsers`, `maxClients`), `occupiedSeats` e `registeredAt`. Nunca o e-mail.
- `401` sem token ou com token inválido.
- `403` em todo o resto, **sempre com o mesmo corpo**: platform-admin, outro tenant, papel errado, quem não é membro — e tenant que não existe. A rota não tem `404`.

Um detalhe que não é exceção a essa regra: `GET /api/v1/tenants/acme` (um id que não é GUID) responde `404` a quem está autenticado. A restrição `:guid` tira o pedido da rota, e ele vira um caminho não mapeado; não diz nada sobre tenant nenhum. Há teste que afirma isso, para ninguém "consertar".

**Arquivos:**
- Create: `src/IdentityGateway.Application/Tenants/GetTenant/GetTenantQuery.cs`, `GetTenantHandler.cs`, `TenantDetailsResponse.cs`
- Modify: `src/IdentityGateway.Application/Common/Abstractions/ITenantQueries.cs` (o método e o record `TenantDetailsView`), `src/IdentityGateway.Infrastructure/Persistence/Queries/TenantQueries.cs`, `src/IdentityGateway.Api/Modules/TenantsModule.cs`
- Create (testes): `tests/IdentityGateway.Application.UnitTests/Tenants/GetTenant/GetTenantHandlerTests.cs`; `tests/IdentityGateway.Infrastructure.IntegrationTests/Persistence/TenantDetailsTests.cs`; `tests/IdentityGateway.ArchitectureTests/RegrasDeLeituraTests.cs`; `tests/IdentityGateway.Api.FunctionalTests/LeituraDeTenantTests.cs`, `Autorizacao/OrdemDosHandlersTests.cs`, `Autorizacao/RespostaDaLeituraDeTenantTests.cs`
- Modify (testes): `tests/IdentityGateway.Api.FunctionalTests/EndpointsDeclaramAutorizacaoTests.cs`, `AutenticacaoNegativaTests.cs`

**Interfaces:**
- Consome: `Policies.TenantAdmin`, `Policies.DeTenant`, `SameTenantRequirement.ParametroDaRota` (Tarefa 13); `IMemberQueries`, `PertencaFalsa` (Tarefa 14); `RespostasDeAutorizacao.Proibido(HttpContext)` e as constantes `TipoDoProibido`, `TituloDoProibido`, `DetalheDoProibido`; `IdentityGatewayApiFactory.Emissor` (`Emitir(sub, roles, tenantId, ajustar)`) e `ComEscopoAsync` (Tarefa 7); `Tenant.Register`, `Tenant.CompleteProvisioning`, `TenantErrors.NotFound`, `Result<T>.Match` (existentes).
- Produz:
  - `public sealed record GetTenantQuery(TenantId TenantId) : IQuery<TenantDetailsResponse>` e `GetTenantHandler`.
  - `public sealed record TenantDetailsResponse(Guid TenantId, string Name, string Slug, string Status, TenantPlanResponse Plan, int OccupiedSeats, DateTimeOffset RegisteredAt)` e `TenantPlanResponse(string Tier, int MaxUsers, int MaxClients)`.
  - `ITenantQueries.GetDetailsAsync(TenantId, CancellationToken)` → `TenantDetailsView?`; `public sealed record TenantDetailsView(TenantId TenantId, string Name, TenantSlug Slug, TenantStatus Status, Plan Plan, int OccupiedSeats, DateTimeOffset RegisteredAt)`.
  - `GET /api/v1/tenants/{tenantId:guid}` (nome `ConsultarTenant`) e `internal static IResult TenantsModule.ParaRespostaDoTenant(Result<TenantDetailsResponse>, HttpContext)`.

- [ ] **Passo 1: Os testes do handler e da projeção**

`tests/IdentityGateway.Application.UnitTests/Tenants/GetTenant/GetTenantHandlerTests.cs`:

```csharp
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Tenants.GetTenant;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;
using NSubstitute;

namespace IdentityGateway.Application.UnitTests.Tenants.GetTenant;

public sealed class GetTenantHandlerTests
{
    private readonly ITenantQueries _consultas = Substitute.For<ITenantQueries>();

    [Fact]
    public async Task TenantExistente_DevolveOsCamposComStatusETierEmTexto()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var tenant = TenantId.New();
        DateTimeOffset registro = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        _consultas.GetDetailsAsync(tenant, Arg.Any<CancellationToken>())
            .Returns(new TenantDetailsView(
                tenant,
                "Acme Corp",
                TenantSlug.Create("acme").Value,
                TenantStatus.Suspended,
                new Plan(PlanTier.Enterprise, 500, 20),
                7,
                registro));

        Result<TenantDetailsResponse> resultado = await new GetTenantHandler(_consultas)
            .Handle(new GetTenantQuery(tenant), ct);

        resultado.Value.Should().Be(new TenantDetailsResponse(
            tenant.Value, "Acme Corp", "acme", "Suspended", new TenantPlanResponse("Enterprise", 500, 20), 7, registro));
    }

    [Fact]
    public async Task TenantInexistente_DevolveNotFound()
    {
        // O handler segue o padrão e diz "não encontrado". Quem decide que isso vira 403, e não 404, é o módulo da Api.
        CancellationToken ct = TestContext.Current.CancellationToken;

        Result<TenantDetailsResponse> resultado = await new GetTenantHandler(_consultas)
            .Handle(new GetTenantQuery(TenantId.New()), ct);

        resultado.IsFailure.Should().BeTrue();
        resultado.Error.Code.Should().Be("Tenant.NaoEncontrado");
    }
}
```

`tests/IdentityGateway.Infrastructure.IntegrationTests/Persistence/TenantDetailsTests.cs`:

```csharp
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.Persistence;
using IdentityGateway.Infrastructure.Persistence.Queries;

namespace IdentityGateway.Infrastructure.IntegrationTests.Persistence;

/// <summary>
/// A projeção de leitura do tenant, contra o PostgreSQL.
/// </summary>
/// <remarks>
/// O que só o banco real prova: que o <c>Plan</c>, um tipo complexo achatado em três colunas, volta inteiro numa
/// projeção com <c>Select</c>, e que o slug e o status, gravados como texto, voltam como os tipos do domínio.
/// </remarks>
public sealed class TenantDetailsTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private async Task<Tenant> RegistrarAsync(Plan plano, CancellationToken ct)
    {
        var tenant = Tenant.Register(
            "  Leitura Ltda  ",
            TenantSlug.Create($"td-{Guid.NewGuid():N}"[..18]).Value,
            plano,
            PostgresFixture.EmailDoAdmin(),
            PostgresFixture.Agora);

        await using AppDbContext contexto = postgres.CriarContexto();
        contexto.Tenants.Add(tenant);
        await contexto.SaveChangesAsync(ct);

        return tenant;
    }

    private async Task<TenantDetailsView?> LerAsync(TenantId tenant, CancellationToken ct)
    {
        await using AppDbContext contexto = postgres.CriarContexto();

        return await new TenantQueries(contexto).GetDetailsAsync(tenant, ct);
    }

    [Fact]
    public async Task TenantPorProvisionar_VoltaComOPlanoInteiroEStatusPending()
    {
        // Em Pending, o e-mail do admin inicial ainda está na linha. A projeção não o traz: o tipo nem tem onde pôr.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = await RegistrarAsync(new Plan(PlanTier.Standard, 25, 3), ct);

        TenantDetailsView? lido = await LerAsync(tenant.Id, ct);

        lido.Should().Be(new TenantDetailsView(
            tenant.Id,
            "Leitura Ltda",
            tenant.Slug,
            TenantStatus.Pending,
            new Plan(PlanTier.Standard, 25, 3),
            OccupiedSeats: 0,
            PostgresFixture.Agora));
    }

    [Fact]
    public async Task TenantAtivado_VoltaActiveComAVagaDoAdmin()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = await RegistrarAsync(new Plan(PlanTier.Free, 5, 1), ct);

        await using (AppDbContext contexto = postgres.CriarContexto())
        {
            Tenant rastreado = contexto.Tenants.Single(item => item.Id == tenant.Id);
            Member admin = rastreado.CompleteProvisioning(
                $"org-{Guid.NewGuid():N}", ExternalUserId.From(Guid.NewGuid().ToString()), PostgresFixture.Agora);
            contexto.Members.Add(admin);
            await contexto.SaveChangesAsync(ct);
        }

        TenantDetailsView? lido = await LerAsync(tenant.Id, ct);

        lido!.Status.Should().Be(TenantStatus.Active);
        lido.OccupiedSeats.Should().Be(1);
        lido.Plan.Should().Be(new Plan(PlanTier.Free, 5, 1));
    }

    [Fact]
    public async Task TenantQueNaoExiste_DevolveNulo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        (await LerAsync(TenantId.New(), ct)).Should().BeNull();
    }
}
```

Run: `dotnet build IdentityGateway.slnx`
Expected: FAIL de compilação — `GetTenantQuery`, `GetTenantHandler`, `TenantDetailsResponse`, `TenantDetailsView` e `GetDetailsAsync` não existem.

- [ ] **Passo 2: A query, o handler, a resposta e a projeção**

`src/IdentityGateway.Application/Tenants/GetTenant/GetTenantQuery.cs`:

```csharp
using IdentityGateway.Application.Common.Messaging;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Application.Tenants.GetTenant;

/// <summary>O tenant, para quem o administra.</summary>
/// <remarks>
/// A query não carrega quem pergunta: a autorização — papel, tenant do token e pertença no banco — acontece antes, na
/// policy da rota. Quem enviar esta query por outro caminho precisa autorizar antes.
/// </remarks>
/// <param name="TenantId">Tenant consultado.</param>
public sealed record GetTenantQuery(TenantId TenantId) : IQuery<TenantDetailsResponse>;
```

`src/IdentityGateway.Application/Tenants/GetTenant/TenantDetailsResponse.cs`:

```csharp
namespace IdentityGateway.Application.Tenants.GetTenant;

/// <summary>
/// O que a leitura de um tenant devolve.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sem o e-mail do admin inicial, de propósito.</b> Ele é dado pessoal, só existe enquanto o tenant está por
/// provisionar e some na ativação; não é atributo do tenant para quem o lê. Um teste trava o conjunto de chaves da
/// resposta, e outro, por reflexão, recusa qualquer propriedade de e-mail aqui.
/// </para>
/// <para>
/// Sem o id da Organization: é detalhe interno do Keycloak, como na resposta do provisionamento.
/// </para>
/// </remarks>
/// <param name="TenantId">Identidade do tenant.</param>
/// <param name="Name">Nome de exibição.</param>
/// <param name="Slug">Slug, único e imutável.</param>
/// <param name="Status">Nome do <c>TenantStatus</c>.</param>
/// <param name="Plan">O plano contratado.</param>
/// <param name="OccupiedSeats">Vagas ocupadas, contando convites pendentes.</param>
/// <param name="RegisteredAt">Quando foi registrado, em UTC.</param>
public sealed record TenantDetailsResponse(
    Guid TenantId,
    string Name,
    string Slug,
    string Status,
    TenantPlanResponse Plan,
    int OccupiedSeats,
    DateTimeOffset RegisteredAt);

/// <summary>O plano do tenant, na leitura.</summary>
/// <param name="Tier">Nome do <c>PlanTier</c>.</param>
/// <param name="MaxUsers">Limite de membros.</param>
/// <param name="MaxClients">Limite de clients M2M.</param>
public sealed record TenantPlanResponse(string Tier, int MaxUsers, int MaxClients);
```

`src/IdentityGateway.Application/Tenants/GetTenant/GetTenantHandler.cs`:

```csharp
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Common.Messaging;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Application.Tenants.GetTenant;

/// <summary>Responde <see cref="GetTenantQuery"/>.</summary>
public sealed class GetTenantHandler(ITenantQueries consultas) : IQueryHandler<GetTenantQuery, TenantDetailsResponse>
{
    public async ValueTask<Result<TenantDetailsResponse>> Handle(
        GetTenantQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        TenantDetailsView? tenant = await consultas.GetDetailsAsync(query.TenantId, cancellationToken);

        if (tenant is null)
        {
            return Result.Failure<TenantDetailsResponse>(TenantErrors.NotFound(query.TenantId));
        }

        return new TenantDetailsResponse(
            tenant.TenantId.Value,
            tenant.Name,
            tenant.Slug.Value,
            tenant.Status.ToString(),
            new TenantPlanResponse(tenant.Plan.Tier.ToString(), tenant.Plan.MaxUsers, tenant.Plan.MaxClients),
            tenant.OccupiedSeats,
            tenant.RegisteredAt);
    }
}
```

`src/IdentityGateway.Application/Common/Abstractions/ITenantQueries.cs` passa a ser, inteiro (o método novo, o record novo no fim, e a última frase do `<remarks>` da interface, que dizia "Três campos não pedem o `Tenant` montado"):

```csharp
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Application.Common.Abstractions;

/// <summary>
/// Leituras de tenant que não precisam do agregado.
/// </summary>
/// <remarks>
/// Separado do <see cref="ITenantRepository"/>: o repositório devolve o agregado rastreado, para ser alterado; aqui
/// são projeções sem rastreamento, para responder consulta. Ler um tenant não pede o agregado montado.
/// </remarks>
public interface ITenantQueries
{
    /// <summary>Estado do provisionamento, ou nulo se o tenant não existir.</summary>
    Task<TenantProvisioningView?> GetProvisioningAsync(TenantId tenantId, CancellationToken cancellationToken = default);

    /// <summary>O tenant, sem o e-mail do admin inicial, ou nulo se ele não existir.</summary>
    Task<TenantDetailsView?> GetDetailsAsync(TenantId tenantId, CancellationToken cancellationToken = default);
}

/// <summary>Projeção do estado de provisionamento de um tenant.</summary>
/// <param name="TenantId">Identidade do tenant.</param>
/// <param name="Status">Estado no ciclo de vida.</param>
/// <param name="RegisteredAt">Quando foi registrado, em UTC.</param>
public sealed record TenantProvisioningView(TenantId TenantId, TenantStatus Status, DateTimeOffset RegisteredAt);

/// <summary>Projeção de um tenant para leitura.</summary>
/// <remarks>
/// <b>Sem o e-mail do admin inicial.</b> A projeção não o seleciona, e por isso ele nem sai do banco. Serve à leitura
/// de um tenant e, depois, à listagem.
/// </remarks>
/// <param name="TenantId">Identidade do tenant.</param>
/// <param name="Name">Nome de exibição.</param>
/// <param name="Slug">Slug.</param>
/// <param name="Status">Estado no ciclo de vida.</param>
/// <param name="Plan">O plano contratado.</param>
/// <param name="OccupiedSeats">Vagas ocupadas.</param>
/// <param name="RegisteredAt">Quando foi registrado, em UTC.</param>
public sealed record TenantDetailsView(
    TenantId TenantId,
    string Name,
    TenantSlug Slug,
    TenantStatus Status,
    Plan Plan,
    int OccupiedSeats,
    DateTimeOffset RegisteredAt);
```

O `TenantDetailsView` fica no mesmo arquivo da interface, como o `TenantProvisioningView` já fica.

Em `src/IdentityGateway.Infrastructure/Persistence/Queries/TenantQueries.cs`, depois de `GetProvisioningAsync` (antes da chave que fecha a classe):

```csharp

    /// <inheritdoc />
    public Task<TenantDetailsView?> GetDetailsAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        context.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => new TenantDetailsView(
                tenant.Id,
                tenant.Name,
                tenant.Slug,
                tenant.Status,
                tenant.Plan,
                tenant.OccupiedSeats,
                tenant.RegisteredAt))
            .SingleOrDefaultAsync(cancellationToken);
```

O `tenant.Plan` é um tipo complexo achatado em três colunas, e o EF Core o projeta inteiro dentro do `Select` (conferido contra o PostgreSQL ao escrever este plano). A coluna do e-mail não aparece na projeção e não sai do banco.

Run: `dotnet test tests/IdentityGateway.Application.UnitTests --filter-class "*GetTenantHandlerTests"`
Expected: `total: 2`, `falhou: 0`.

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter-class "*TenantDetailsTests"`
Expected: `total: 3`, `falhou: 0`.

- [ ] **Passo 3: A regra do e-mail fora da leitura**

`tests/IdentityGateway.ArchitectureTests/RegrasDeLeituraTests.cs`:

```csharp
using System.Reflection;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Tenants.GetTenant;
using IdentityGateway.Domain.ValueObjects;

namespace IdentityGateway.ArchitectureTests;

/// <summary>
/// Regras sobre o que as leituras expõem.
/// </summary>
public sealed class RegrasDeLeituraTests
{
    /// <summary>
    /// A leitura de um tenant não carrega e-mail: nem na projeção que sai do banco, nem na resposta da API.
    /// </summary>
    /// <remarks>
    /// O e-mail do admin inicial é dado pessoal e fica na linha do tenant só até a ativação. Um
    /// <c>InitialAdminEmail</c> acrescentado ao read model "para a listagem" passaria a sair em toda leitura. O teste
    /// olha o tipo e o nome de cada propriedade, inclusive as dos tipos aninhados.
    /// </remarks>
    [Fact]
    public void LeituraDeTenant_NaoCarregaEmail()
    {
        Type[] tipos = [typeof(TenantDetailsView), typeof(TenantDetailsResponse), typeof(TenantPlanResponse)];

        string[] comEmail =
        [
            .. tipos
                .SelectMany(tipo => tipo.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Select(propriedade => (Tipo: tipo, Propriedade: propriedade)))
                .Where(item => item.Propriedade.PropertyType == typeof(Email)
                    || item.Propriedade.Name.Contains("Email", StringComparison.OrdinalIgnoreCase)
                    || item.Propriedade.Name.Contains("Mail", StringComparison.OrdinalIgnoreCase))
                .Select(item => $"{item.Tipo.Name}.{item.Propriedade.Name}"),
        ];

        tipos.SelectMany(tipo => tipo.GetProperties()).Should().NotBeEmpty("sem propriedades, a regra passaria vazia");
        comEmail.Should().BeEmpty("o e-mail do admin inicial não é atributo do tenant para quem o lê");
    }
}
```

Run: `dotnet test tests/IdentityGateway.ArchitectureTests --filter-class "*RegrasDeLeituraTests"`
Expected: PASS. A regra nasce verde; quem prova que ela pega é a mutação 5 do Passo 8.

- [ ] **Passo 4: Os testes da rota**

`tests/IdentityGateway.Api.FunctionalTests/Autorizacao/RespostaDaLeituraDeTenantTests.cs` (sem fixture):

```csharp
using IdentityGateway.Api.Authorization;
using IdentityGateway.Api.Modules;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Tenants.GetTenant;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Api.FunctionalTests.Autorizacao;

/// <summary>
/// A tradução do resultado da leitura de tenant em resposta HTTP, sem HTTP.
/// </summary>
/// <remarks>
/// O ramo de falha é inalcançável por HTTP com a porta real — a policy nega antes, porque não há membro de um tenant
/// que não existe. Por isso é testado aqui, na função: se um dia for alcançado, "não encontrado" responde o mesmo
/// <c>403</c> das negações, e não o <c>404</c> que o <c>ParaOk</c> daria.
/// </remarks>
public sealed class RespostaDaLeituraDeTenantTests
{
    private static readonly string[] SoACorrelacao = ["correlationId"];

    private static DefaultHttpContext Contexto()
    {
        ServiceCollection services = new();
        services.AddSingleton<ICorrelationIdProvider>(new CorrelacaoFixa());

        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }

    [Fact]
    public void TenantNaoEncontrado_ViraOMesmo403DaAutorizacao()
    {
        var falha = Result.Failure<TenantDetailsResponse>(TenantErrors.NotFound(TenantId.New()));

        IResult resposta = TenantsModule.ParaRespostaDoTenant(falha, Contexto());

        ProblemHttpResult problema = resposta.Should().BeOfType<ProblemHttpResult>().Subject;
        problema.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        problema.ProblemDetails.Type.Should().Be(RespostasDeAutorizacao.TipoDoProibido);
        problema.ProblemDetails.Title.Should().Be(RespostasDeAutorizacao.TituloDoProibido);
        problema.ProblemDetails.Detail.Should().Be(RespostasDeAutorizacao.DetalheDoProibido);

        // Nem o código do erro de domínio, que diria "Tenant.NaoEncontrado".
        problema.ProblemDetails.Extensions.Keys.Should().BeEquivalentTo(SoACorrelacao);
    }

    [Fact]
    public void TenantEncontrado_Vira200ComOTenant()
    {
        TenantDetailsResponse tenant = new(
            Guid.NewGuid(), "Acme", "acme", "Active", new TenantPlanResponse("Free", 5, 1), 1, DateTimeOffset.UtcNow);

        IResult resposta = TenantsModule.ParaRespostaDoTenant(tenant, Contexto());

        resposta.Should().BeOfType<Ok<TenantDetailsResponse>>().Which.Value.Should().Be(tenant);
    }

    private sealed class CorrelacaoFixa : ICorrelationIdProvider
    {
        public string CorrelationId => "correlacao-de-teste";
    }
}
```

`tests/IdentityGateway.Api.FunctionalTests/LeituraDeTenantTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IdentityGateway.Api.Authorization;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// <c>GET /api/v1/tenants/{tenantId}</c>: o administrador lê o próprio tenant, e mais ninguém lê nada.
/// </summary>
/// <remarks>
/// <para>
/// <b>A regra de isolamento número um, por HTTP.</b> Cada caso de <c>403</c> tem tudo certo menos uma coisa — o papel,
/// a conta de plataforma, o tenant do token, a pertença no banco —, e todos respondem o mesmo Problem Details: quem
/// chama não aprende por que foi negado, nem se o tenant existe.
/// </para>
/// <para>
/// <b>Não há <c>404</c> nesta rota.</b> Tenant que não existe é <c>403</c>: a policy nega antes de qualquer consulta ao
/// tenant, porque não há membro de um tenant que não existe.
/// </para>
/// <para>
/// Os tokens levam <c>sub</c> único, e o limitador de requisições particiona por <c>sub</c>: a classe fica longe do
/// limite.
/// </para>
/// </remarks>
public sealed class LeituraDeTenantTests(IdentityGatewayApiFactory factory) : IClassFixture<IdentityGatewayApiFactory>
{
    private const string OutroTenant = "0199a000-0000-7000-8000-0000000000ff";

    private static readonly string[] SoTenantAdmin = ["tenant-admin"];

    private static readonly string[] SoPlatformAdmin = ["platform-admin"];

    private static readonly string[] PlatformAdminETenantAdmin = ["platform-admin", "tenant-admin"];

    private static readonly string[] SoReader = ["reader"];

    private static readonly string[] ChavesDoTenant =
        ["tenantId", "name", "slug", "status", "plan", "occupiedSeats", "registeredAt"];

    private static readonly string[] ChavesDoPlano = ["tier", "maxUsers", "maxClients"];

    private static string Rota(Guid tenant) => $"/api/v1/tenants/{tenant}";

    /// <summary>Grava um tenant ativo com um admin membro, pelo caminho de domínio, e devolve os dois ids.</summary>
    private async Task<(Guid Tenant, Guid Admin, string Slug)> TenantComAdminAsync(CancellationToken ct)
    {
        var admin = Guid.NewGuid();
        string slug = $"lt-{Guid.NewGuid():N}"[..20];
        var tenant = Tenant.Register(
            "Acme Corp",
            TenantSlug.Create(slug).Value,
            new Plan(PlanTier.Standard, 25, 3),
            Email.Of($"admin+{Guid.NewGuid():N}@acme.test").Value,
            DateTimeOffset.UtcNow);

        await factory.ComEscopoAsync(async contexto =>
        {
            contexto.Tenants.Add(tenant);
            await contexto.SaveChangesAsync(ct);

            // O sub como o Keycloak o emite: o GUID em minúsculas, formato D.
            Member membro = tenant.CompleteProvisioning(
                $"org-{Guid.NewGuid():N}", ExternalUserId.From(admin.ToString()), DateTimeOffset.UtcNow);
            contexto.Members.Add(membro);
            await contexto.SaveChangesAsync(ct);
        });

        return (tenant.Id.Value, admin, slug);
    }

    private async Task<HttpResponseMessage> LerAsync(string rota, string? token, CancellationToken ct)
    {
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage pedido = new(HttpMethod.Get, rota);

        if (token is not null)
        {
            pedido.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(pedido, ct);
    }

    /// <summary>O <c>403</c> único da Gateway: os mesmos quatro campos fixos, qualquer que seja o motivo.</summary>
    private static async Task DeveSerOProibidoPadraoAsync(HttpResponseMessage resposta, string caso, CancellationToken ct)
    {
        resposta.StatusCode.Should().Be(HttpStatusCode.Forbidden, caso);
        resposta.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json", caso);

        JsonElement corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>(ct);
        corpo.GetProperty("status").GetInt32().Should().Be(403, caso);
        corpo.GetProperty("type").GetString().Should().Be(RespostasDeAutorizacao.TipoDoProibido, caso);
        corpo.GetProperty("title").GetString().Should().Be(RespostasDeAutorizacao.TituloDoProibido, caso);
        corpo.GetProperty("detail").GetString().Should().Be(RespostasDeAutorizacao.DetalheDoProibido, caso);
    }

    [Fact]
    public async Task AdminDoTenant_LeOProprioTenantComExatamenteAsChavesDoContrato()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Guid tenant, Guid admin, string slug) = await TenantComAdminAsync(ct);
        string token = factory.Emissor.Emitir(admin, SoTenantAdmin, tenant.ToString());

        using HttpResponseMessage resposta = await LerAsync(Rota(tenant), token, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>(ct);

        // O conjunto EXATO de chaves: uma a mais reprova, mesmo nula. É o que impede o e-mail do admin inicial (ou o
        // id da Organization) de aparecer aqui por uma propriedade nova no DTO.
        corpo.EnumerateObject().Select(chave => chave.Name).Should().BeEquivalentTo(ChavesDoTenant);
        corpo.GetProperty("plan").EnumerateObject().Select(chave => chave.Name).Should().BeEquivalentTo(ChavesDoPlano);

        corpo.GetProperty("tenantId").GetGuid().Should().Be(tenant);
        corpo.GetProperty("name").GetString().Should().Be("Acme Corp");
        corpo.GetProperty("slug").GetString().Should().Be(slug);
        corpo.GetProperty("status").GetString().Should().Be("Active");
        corpo.GetProperty("plan").GetProperty("tier").GetString().Should().Be("Standard");
        corpo.GetProperty("plan").GetProperty("maxUsers").GetInt32().Should().Be(25);
        corpo.GetProperty("plan").GetProperty("maxClients").GetInt32().Should().Be(3);
        corpo.GetProperty("occupiedSeats").GetInt32().Should().Be(1, "o admin convidado ocupa uma vaga");
        corpo.GetProperty("registeredAt").GetDateTimeOffset()
            .Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
    }

    [Theory]
    [InlineData("D")]
    [InlineData("N")]
    public async Task ProprioTenantComOGuidDaRotaEscritoDeOutroJeito_Responde200(string formato)
    {
        // Controles: a comparação entre a rota e o token é por Guid, e não por texto. Em maiúsculas ou sem hifens, é o
        // mesmo tenant.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Guid tenant, Guid admin, _) = await TenantComAdminAsync(ct);
        string token = factory.Emissor.Emitir(admin, SoTenantAdmin, tenant.ToString());
        string rota = $"/api/v1/tenants/{tenant.ToString(formato).ToUpperInvariant()}";

        using HttpResponseMessage resposta = await LerAsync(rota, token, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.OK, rota);
    }

    private static TheoryDataRow<Func<IdentityGatewayApiFactory, Guid, Guid, string>> Caso(
        string rotulo, Func<IdentityGatewayApiFactory, Guid, Guid, string> token) => new(token) { Label = rotulo };

    /// <summary>O token do admin membro do tenant, com o claim <c>tenant_id</c> trocado pelo que o caso disser.</summary>
    private static TheoryDataRow<Func<IdentityGatewayApiFactory, Guid, Guid, string>> ComTenantId(
        string rotulo, Func<Guid, object?> tenantId) =>
        Caso(rotulo, (alvo, tenant, admin) => alvo.Emissor.Emitir(admin, SoTenantAdmin, ajustar: payload =>
        {
            if (tenantId(tenant) is { } valor)
            {
                payload["tenant_id"] = valor;
            }
        }));

    /// <summary>
    /// Tokens do <b>membro</b> do tenant (o <c>sub</c> está no banco) em que só uma coisa está errada. Recebem a
    /// factory, o tenant e o admin, e devolvem o token.
    /// </summary>
    public static TheoryData<Func<IdentityGatewayApiFactory, Guid, Guid, string>> TokensNegados => new()
    {
        Caso("platform-admin sem tenant_id",
            (alvo, _, admin) => alvo.Emissor.Emitir(admin, SoPlatformAdmin)),
        Caso("platform-admin com o tenant_id do tenant",
            (alvo, tenant, admin) => alvo.Emissor.Emitir(admin, SoPlatformAdmin, tenant.ToString())),
        Caso("platform-admin que também é tenant-admin do próprio tenant",
            (alvo, tenant, admin) => alvo.Emissor.Emitir(admin, PlatformAdminETenantAdmin, tenant.ToString())),
        Caso("papel de outro nível (reader)",
            (alvo, tenant, admin) => alvo.Emissor.Emitir(admin, SoReader, tenant.ToString())),
        Caso("sem o claim roles",
            (alvo, tenant, admin) => alvo.Emissor.Emitir(admin, tenantId: tenant.ToString())),
        ComTenantId("tenant_id ausente", _ => null),
        ComTenantId("tenant_id vazio", _ => string.Empty),
        ComTenantId("tenant_id que não é GUID", _ => "acme"),
        ComTenantId("tenant_id com espaço antes", tenant => $" {tenant}"),
        ComTenantId("tenant_id com espaço depois", tenant => $"{tenant} "),
        ComTenantId("tenant_id entre chaves", tenant => tenant.ToString("B")),
        ComTenantId("tenant_id no formato N", tenant => tenant.ToString("N")),
        ComTenantId("tenant_id de outro tenant", _ => OutroTenant),
        ComTenantId("tenant_id em array: o próprio e outro", tenant => new[] { tenant.ToString(), OutroTenant }),
        ComTenantId("tenant_id em array: outro e o próprio", tenant => new[] { OutroTenant, tenant.ToString() }),
        ComTenantId("tenant_id em array: o próprio, duas vezes", tenant => new[] { tenant.ToString(), tenant.ToString() }),
    };

    [Theory]
    [MemberData(nameof(TokensNegados))]
    public async Task MembroDoTenantComUmDefeitoNoToken_Responde403(Func<IdentityGatewayApiFactory, Guid, Guid, string> token)
    {
        ArgumentNullException.ThrowIfNull(token);
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Guid tenant, Guid admin, _) = await TenantComAdminAsync(ct);

        using HttpResponseMessage resposta = await LerAsync(Rota(tenant), token(factory, tenant, admin), ct);

        await DeveSerOProibidoPadraoAsync(resposta, "token com defeito", ct);
    }

    [Fact]
    public async Task AdminDeOutroTenantQueExiste_Responde403()
    {
        // Os dois tenants existem, e cada admin é membro do seu. O token de A, na rota de B.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Guid tenantA, Guid adminA, _) = await TenantComAdminAsync(ct);
        (Guid tenantB, _, _) = await TenantComAdminAsync(ct);
        string token = factory.Emissor.Emitir(adminA, SoTenantAdmin, tenantA.ToString());

        using HttpResponseMessage resposta = await LerAsync(Rota(tenantB), token, ct);

        await DeveSerOProibidoPadraoAsync(resposta, "tenant alheio", ct);
    }

    [Fact]
    public async Task TenantQueNaoExiste_Responde403ENao404()
    {
        // Duas formas de "não existe": o tenant da rota não existe e o token é de outro tenant; e o tenant da rota não
        // existe e o token diz que é o dele. Nas duas, 403 — a resposta não distingue "não existe" de "não é seu".
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Guid tenantA, Guid adminA, _) = await TenantComAdminAsync(ct);
        var inexistente = Guid.NewGuid();
        string tokenDeA = factory.Emissor.Emitir(adminA, SoTenantAdmin, tenantA.ToString());
        string tokenDoInexistente = factory.Emissor.Emitir(adminA, SoTenantAdmin, inexistente.ToString());

        using HttpResponseMessage deOutro = await LerAsync(Rota(inexistente), tokenDeA, ct);
        using HttpResponseMessage doProprio = await LerAsync(Rota(inexistente), tokenDoInexistente, ct);

        await DeveSerOProibidoPadraoAsync(deOutro, "tenant inexistente, token de outro tenant", ct);
        await DeveSerOProibidoPadraoAsync(doProprio, "tenant inexistente, token com o tenant_id dele", ct);
    }

    [Fact]
    public async Task TokenCertoDeQuemNaoEMembro_Responde403()
    {
        // O ataque que a pertença fecha: papel certo e tenant_id certo no token (forjável por um grupo no Keycloak),
        // de um sub que o banco da Gateway não conhece como membro do tenant.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Guid tenant, _, _) = await TenantComAdminAsync(ct);
        string token = factory.Emissor.Emitir(Guid.NewGuid(), SoTenantAdmin, tenant.ToString());

        using HttpResponseMessage resposta = await LerAsync(Rota(tenant), token, ct);

        await DeveSerOProibidoPadraoAsync(resposta, "sub sem Member", ct);
    }

    [Fact]
    public async Task MembroDesativado_PerdeOAcessoNoPedidoSeguinte()
    {
        // A pertença é lida a cada pedido: o token ainda vale 5 minutos, e o acesso acaba quando o status muda.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Guid tenant, Guid admin, _) = await TenantComAdminAsync(ct);
        string token = factory.Emissor.Emitir(admin, SoTenantAdmin, tenant.ToString());

        using HttpResponseMessage antes = await LerAsync(Rota(tenant), token, ct);
        await factory.ComEscopoAsync(contexto => contexto.Database.ExecuteSqlAsync(
            $"UPDATE members SET status = 'Deactivated' WHERE tenant_id = {tenant}", ct));
        using HttpResponseMessage depois = await LerAsync(Rota(tenant), token, ct);

        antes.StatusCode.Should().Be(HttpStatusCode.OK, "controle: antes da desativação, o mesmo token lê");
        await DeveSerOProibidoPadraoAsync(depois, "membro desativado", ct);
    }

    [Fact]
    public async Task Todo403DaRota_TemOMesmoCorpoEOsMesmosCabecalhos()
    {
        // Cinco motivos diferentes — três num tenant que existe, dois num que não existe —, comparados campo a campo:
        // fora o correlationId e o traceId, que são do pedido, as respostas são idênticas. Quem chama não aprende por
        // que foi negado, nem se o tenant existe.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (Guid tenant, Guid admin, _) = await TenantComAdminAsync(ct);
        var inexistente = Guid.NewGuid();

        (string Rota, string Token)[] pedidos =
        [
            (Rota(tenant), factory.Emissor.Emitir(admin, SoReader, tenant.ToString())),
            (Rota(tenant), factory.Emissor.Emitir(admin, SoTenantAdmin, OutroTenant)),
            (Rota(tenant), factory.Emissor.Emitir(Guid.NewGuid(), SoTenantAdmin, tenant.ToString())),
            (Rota(inexistente), factory.Emissor.Emitir(admin, SoTenantAdmin, tenant.ToString())),
            (Rota(inexistente), factory.Emissor.Emitir(admin, SoTenantAdmin, inexistente.ToString())),
        ];

        List<string> corpos = [];
        List<string> cabecalhos = [];

        foreach ((string rota, string token) in pedidos)
        {
            using HttpResponseMessage resposta = await LerAsync(rota, token, ct);
            JsonElement corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>(ct);

            corpos.Add(string.Join(
                " | ",
                corpo.EnumerateObject()
                    .Where(campo => campo.Name is not ("correlationId" or "traceId"))
                    .Select(campo => $"{campo.Name}={campo.Value.GetRawText()}")));
            cabecalhos.Add(string.Join(
                ", ",
                resposta.Headers.Concat(resposta.Content.Headers)
                    .Select(cabecalho => cabecalho.Key)
                    .Order(StringComparer.OrdinalIgnoreCase)));
        }

        corpos.Distinct().Should().ContainSingle("o 403 não pode dizer por que negou");
        corpos[0].Should().Contain("status=403");
        cabecalhos.Distinct().Should().ContainSingle("nem pelos cabeçalhos");
    }

    [Fact]
    public async Task SemToken_Responde401()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using HttpResponseMessage resposta = await LerAsync(Rota(Guid.NewGuid()), token: null, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TenantIdQueNaoEGuid_NaoCasaARota()
    {
        // A restrição :guid tira o pedido da rota antes da policy: é um caminho não mapeado, e responde 404 a quem está
        // autenticado. Não diz nada sobre tenant nenhum — um id malformado não pode existir.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string token = factory.Emissor.Emitir(Guid.NewGuid(), SoTenantAdmin, OutroTenant);

        using HttpResponseMessage resposta = await LerAsync("/api/v1/tenants/acme", token, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
```

O tenant e o membro são gravados pelo caminho de domínio (`Tenant.Register`, `CompleteProvisioning`), pelo `ComEscopoAsync` da factory: nesta suíte o Outbox está desligado e não há Keycloak que provisione. Quem prova a rota com um tenant provisionado de verdade é a Tarefa 16.

`tests/IdentityGateway.Api.FunctionalTests/Autorizacao/OrdemDosHandlersTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Members;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Api.FunctionalTests.Autorizacao;

/// <summary>
/// Na composição real da Api, a pertença só é consultada para quem já passou nas camadas do token.
/// </summary>
/// <remarks>
/// <para>
/// O teste unitário prova a ordem no contêiner que ele mesmo monta. Aqui é o contêiner do <c>Program.cs</c>: se outro
/// registro passar a pôr um handler de autorização antes do <c>AddAutorizacaoDaGateway</c>, é este teste que vê.
/// </para>
/// <para>
/// <b>Exceção declarada à regra "só configuração" da factory:</b> a porta <c>IMemberQueries</c> é trocada por uma que
/// conta as consultas. É a única troca, vale só para esta classe, e o que está sob teste — a ordem dos handlers — não
/// é tocado por ela.
/// </para>
/// </remarks>
public sealed class OrdemDosHandlersTests(IdentityGatewayApiFactory factory) : IClassFixture<IdentityGatewayApiFactory>
{
    private const string Tenant = "0199a000-0000-7000-8000-00000000000a";

    private const string OutroTenant = "0199a000-0000-7000-8000-00000000000b";

    private static readonly string[] SoTenantAdmin = ["tenant-admin"];

    private async Task<(HttpStatusCode Status, int Consultas)> LerAsync(
        IReadOnlyCollection<string> roles, string? tenantIdDoToken, CancellationToken ct)
    {
        PertencaFalsa pertenca = new(MemberStatus.Active);

        await using WebApplicationFactory<Program> api = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddScoped<IMemberQueries>(_ => pertenca)));
        using HttpClient client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.Emissor.Emitir(roles: roles, tenantId: tenantIdDoToken));

        using HttpResponseMessage resposta = await client.GetAsync(
            new Uri($"/api/v1/tenants/{Tenant}", UriKind.Relative), ct);

        return (resposta.StatusCode, pertenca.Consultas);
    }

    [Theory]
    [InlineData("sem o papel tenant-admin", new[] { "reader" }, Tenant)]
    [InlineData("platform-admin que também é tenant-admin", new[] { "platform-admin", "tenant-admin" }, Tenant)]
    [InlineData("tenant-admin de outro tenant", new[] { "tenant-admin" }, OutroTenant)]
    [InlineData("tenant-admin sem tenant_id", new[] { "tenant-admin" }, null)]
    public async Task QuemNaoPassaNasCamadasDoToken_Recebe403SemConsultaAPertenca(
        string caso, string[] roles, string? tenantIdDoToken)
    {
        (HttpStatusCode status, int consultas) = await LerAsync(
            roles, tenantIdDoToken, TestContext.Current.CancellationToken);

        status.Should().Be(HttpStatusCode.Forbidden, caso);
        consultas.Should().Be(0, caso);
    }

    [Fact]
    public async Task QuemPassaNasCamadasDoToken_ProvocaUmaConsulta()
    {
        // Controle: sem ele, "zero consultas" também seria verdade se a porta falsa nem estivesse ligada. A pertença
        // falsa responde Active, a policy passa, e o tenant — que não existe no banco — vira o mesmo 403 no módulo.
        (HttpStatusCode status, int consultas) = await LerAsync(
            SoTenantAdmin, Tenant, TestContext.Current.CancellationToken);

        consultas.Should().Be(1);
        status.Should().Be(HttpStatusCode.Forbidden);
    }
}
```

Em `tests/IdentityGateway.Api.FunctionalTests/EndpointsDeclaramAutorizacaoTests.cs`: acrescentar `using IdentityGateway.Api.Authorization;` e trocar o teste `AsRotasDeTenant_ExigemPlatformAdmin` inteiro — do `[Fact]` dele até a chave que fecha a classe — por:

```csharp
    [Fact]
    public void AsRotasDeTenant_TemAPolicyEsperada()
    {
        // Controle do teste acima: cada rota de tenant tem a policy nomeada certa — e não, por engano, AllowAnonymous,
        // nem a policy de outra rota.
        IReadOnlyList<Endpoint> endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        var policyPorRota = endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/api/v1/tenants", StringComparison.Ordinal))
            .ToDictionary(
                endpoint => Rotulo(endpoint),
                endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Single().Policy!);

        policyPorRota.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["POST /api/v1/tenants"] = Policies.PlatformAdmin,
            ["GET /api/v1/tenants/{tenantId:guid}/provisioning"] = Policies.PlatformAdmin,
            ["GET /api/v1/tenants/{tenantId:guid}"] = Policies.TenantAdmin,
        });
    }

    private static string Rotulo(RouteEndpoint endpoint) =>
        $"{endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods[0]} {endpoint.RoutePattern.RawText}";

    [Fact]
    public void TodaRotaComPolicyDeTenant_TemTenantIdNoTemplate()
    {
        // O SameTenantRequirement lê o tenant do parâmetro {tenantId}. Uma rota com policy de tenant e sem o parâmetro
        // nega sempre — e alguém, para "consertar", afrouxaria o requirement. O erro aparece aqui, na subida.
        IReadOnlyList<Endpoint> endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        RouteEndpoint[] comPolicyDeTenant =
        [
            .. endpoints.OfType<RouteEndpoint>()
                .Where(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                    .Any(dado => dado.Policy is not null && Policies.DeTenant.Contains(dado.Policy))),
        ];

        comPolicyDeTenant.Should().NotBeEmpty("sem rota com policy de tenant, a regra passaria vazia");
        comPolicyDeTenant
            .Where(endpoint => endpoint.RoutePattern.GetParameter(SameTenantRequirement.ParametroDaRota) is null)
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Should().BeEmpty("rota com policy de tenant precisa do parâmetro tenantId no template");
    }
}
```

Run: `dotnet build tests/IdentityGateway.Api.FunctionalTests`
Expected: FAIL de compilação — `TenantsModule.ParaRespostaDoTenant` não existe.

- [ ] **Passo 5: A rota**

Em `src/IdentityGateway.Api/Modules/TenantsModule.cs`:

1. Acrescentar `using IdentityGateway.Application.Tenants.GetTenant;`.

2. Em `AddRoutes`, depois da rota do provisionamento:

```csharp

        app.MapGet("/api/v1/tenants/{tenantId:guid}", ConsultarAsync)
            .RequireAuthorization(Policies.TenantAdmin)
            .WithName("ConsultarTenant")
            .WithSummary("O tenant, para quem o administra.")
            .Produces<TenantDetailsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
```

3. O comentário de `ConsultarProvisionamentoAsync` dizia que `GET /tenants/{id}` "ainda não existe". Passa a ser:

```csharp
    // Sempre 200 com o status, também quando Active, e não um 303 para o recurso do tenant: quem acompanha o
    // provisionamento é o platform-admin, e GET /tenants/{id} responde 403 a ele. A restrição :guid na rota faz um id
    // malformado responder 404 sem chegar ao handler.
```

4. No fim da classe, depois de `ConsultarProvisionamentoAsync`:

```csharp

    /// <remarks>
    /// Quem chega aqui já passou pela policy <c>TenantAdmin</c>: é administrador deste tenant, pelo token e pelo banco.
    /// A rota não tem <c>404</c>: para quem não é membro, um tenant que não existe e um tenant alheio são a mesma
    /// resposta, e a policy nega os dois antes de qualquer consulta ao tenant.
    /// </remarks>
    private static async Task<IResult> ConsultarAsync(
        Guid tenantId,
        ISender sender,
        HttpContext contexto,
        CancellationToken cancellationToken)
    {
        Result<TenantDetailsResponse> resultado = await sender.Send(
            new GetTenantQuery(new TenantId(tenantId)), cancellationToken);

        return ParaRespostaDoTenant(resultado, contexto);
    }

    /// <summary>
    /// Traduz o resultado da leitura do tenant: <c>200</c> com o tenant, ou o mesmo <c>403</c> da autorização.
    /// </summary>
    /// <remarks>
    /// <b>Não usa o <c>ParaOk</c>,</b> que traduziria "tenant não encontrado" em <c>404</c>. A policy torna esse
    /// caminho inalcançável — não há <c>Member</c> de um tenant que não existe —, mas se um dia ele for alcançado (uma
    /// corrida, um tenant removido à mão), a resposta não pode passar a distinguir "não existe" de "não é seu".
    /// </remarks>
    internal static IResult ParaRespostaDoTenant(Result<TenantDetailsResponse> resultado, HttpContext contexto)
    {
        ArgumentNullException.ThrowIfNull(resultado);

        return resultado.Match(
            onSuccess: tenant => Results.Ok(tenant),
            onFailure: _ => RespostasDeAutorizacao.Proibido(contexto));
    }
```

- [ ] **Passo 6: Rodar e ver passar**

Run: `dotnet build IdentityGateway.slnx`
Expected: `0 Aviso(s)`, `0 Erro(s)`.

Run: `dotnet test tests/IdentityGateway.Api.FunctionalTests --filter-class "*.LeituraDeTenantTests"`
Expected: `total: 26`, `falhou: 0`.

Run: `dotnet test tests/IdentityGateway.Api.FunctionalTests --filter-class "*OrdemDosHandlersTests"`
Expected: `total: 5`, `falhou: 0`.

Run: `dotnet test tests/IdentityGateway.Api.FunctionalTests --filter-class "*RespostaDaLeituraDeTenantTests"`
Expected: `total: 2`, `falhou: 0`.

Run: `dotnet test tests/IdentityGateway.Api.FunctionalTests --filter-class "*EndpointsDeclaramAutorizacaoTests"`
Expected: `total: 3`, `falhou: 0`.

- [ ] **Passo 7: A suíte negativa de autenticação também na rota nova**

Os casos de autenticação (Tarefas 7 e 8) rodam em toda rota protegida. Em `tests/IdentityGateway.Api.FunctionalTests/AutenticacaoNegativaTests.cs`:

1. Em `ConferirRecusaAsync`, o array das rotas ganha a terceira:

```csharp
        (HttpMethod Metodo, string Rota)[] rotas =
        [
            (HttpMethod.Post, "/api/v1/tenants"),
            (HttpMethod.Get, RotaDeConsulta()),
            (HttpMethod.Get, $"/api/v1/tenants/{Guid.NewGuid()}"),
        ];
```

2. No teste dos tokens aceitos, acrescentar o controle da rota nova — depois da linha que envia a `consulta`:

```csharp
        using HttpResponseMessage leitura = await EnviarAsync(
            HttpMethod.Get, $"/api/v1/tenants/{Guid.NewGuid()}", token(factory), ct);
```

e, depois das duas asserções:

```csharp

        // Na leitura de tenant, o platform-admin passa da autenticação e para na autorização: 403, e não 401.
        leitura.StatusCode.Should().Be(HttpStatusCode.Forbidden);
```

3. Os três métodos de teste trocam `NasDuasRotas` por `NasRotasProtegidas` no nome (são três rotas agora): `TokenQueAValidacaoRecusa_Responde401SemDetalheNasRotasProtegidas`, `TokenComAFormaErrada_Responde401SemDetalheNasRotasProtegidas` e `TokenAceito_PassaDaAutenticacaoNasRotasProtegidas`.

Run: `dotnet test tests/IdentityGateway.Api.FunctionalTests --filter-class "*AutenticacaoNegativaTests"`
Expected: verde, com o mesmo total de antes — os casos são os mesmos, e cada um passa a percorrer três rotas.

Run: `dotnet test`
Expected: verde em todos os projetos. Em relação ao fim da Tarefa 14: `Application.UnitTests` +2, `Infrastructure.IntegrationTests` +3, `ArchitectureTests` +1, `Api.FunctionalTests` +34 (26 da rota, 5 da ordem dos handlers, 2 do mapeamento e 1 a mais na classe dos endpoints).

- [ ] **Passo 8: 🧪 Provas por mutação**

`git add -A src tests` antes; `git restore src` depois de cada uma.

Run (a cada mutação): `dotnet test tests/IdentityGateway.Api.FunctionalTests` (e `dotnet test tests/IdentityGateway.ArchitectureTests` na 5)

| # | Mutação | Vermelho esperado |
|---|---|---|
| 1 | **Policy de tenant numa rota sem `{tenantId}`:** em `TenantsModule`, trocar a policy do `POST /api/v1/tenants` por `Policies.TenantAdmin` | `TodaRotaComPolicyDeTenant_TemTenantIdNoTemplate` e `AsRotasDeTenant_TemAPolicyEsperada` (e os testes do `POST`, que passam a levar `403`) |
| 2 | **A falha vira `404`:** em `ParaRespostaDoTenant`, `onFailure: _ => Results.NotFound()` | `TenantNaoEncontrado_ViraOMesmo403DaAutorizacao` e `QuemPassaNasCamadasDoToken_ProvocaUmaConsulta` |
| 3 | **Sem o `NotPlatformAdminRequirement`** na policy | 4, entre eles o caso "platform-admin que também é tenant-admin do próprio tenant" de `MembroDoTenantComUmDefeitoNoToken_Responde403` (responde `200`) e o de `QuemNaoPassaNasCamadasDoToken_Recebe403SemConsultaAPertenca` |
| 4 | **Sem o `MemberRequirement`** na policy | 15, entre eles `TokenCertoDeQuemNaoEMembro_Responde403` e `MembroDesativado_PerdeOAcessoNoPedidoSeguinte` (os dois respondem `200`) |
| 4a | **As mutações 2 e 4 juntas** — é a "consultar o tenant antes de autorizar" da §5.3 da spec: sem a pertença, a query roda para quem só tem o token; sem a tradução para `403`, o que ela não acha responde outra coisa | `TenantQueNaoExiste_Responde403ENao404`: `404` no caso "token com o `tenant_id` dele". Sozinha, nenhuma das duas o deixa vermelho |
| 5 | **E-mail na resposta:** em `TenantDetailsResponse`, transformar a declaração num record com corpo e acrescentar `public string? InitialAdminEmail { get; init; }` | `AdminDoTenant_LeOProprioTenantComExatamenteAsChavesDoContrato` (uma chave a mais, mesmo nula) e, na arquitetura, `LeituraDeTenant_NaoCarregaEmail` |
| 5a | **E-mail no read model, pelo tipo e não pelo nome:** em `TenantDetailsView`, record com corpo e `public IdentityGateway.Domain.ValueObjects.Email? AdminInicial { get; init; }` | Na arquitetura, `LeituraDeTenant_NaoCarregaEmail` — o nome não tem "Email"; quem pega é o tipo da propriedade |
| 6 | **Handler da pertença antes do `AddAuthorization`** (a mutação 1 da Tarefa 14) | 8: os quatro de `QuemNaoPassaNasCamadasDoToken_Recebe403SemConsultaAPertenca`, aqui na composição real, e os quatro unitários |
| 7 | **Tenant comparado como texto** (a mutação 6 da Tarefa 13) | 7, entre eles os dois de `ProprioTenantComOGuidDaRotaEscritoDeOutroJeito_Responde200` |
| 7a | **O primeiro claim** e **o último claim** (as mutações 4 e 5 da Tarefa 13), agora por HTTP | 2 cada: os casos "`tenant_id` em array" de `MembroDoTenantComUmDefeitoNoToken_Responde403` — "o próprio e outro" e "o próprio, duas vezes" com o primeiro; "outro e o próprio" e "o próprio, duas vezes" com o último |
| 8 | **O handler troca dois campos:** em `GetTenantHandler`, `new TenantPlanResponse(tenant.Plan.Tier.ToString(), tenant.Plan.MaxClients, tenant.Plan.MaxUsers)` | `dotnet test tests/IdentityGateway.Application.UnitTests --filter-class "*GetTenantHandlerTests"`: `TenantExistente_DevolveOsCamposComStatusETierEmTexto` |
| 9 | **A projeção perde um campo:** em `TenantQueries.GetDetailsAsync`, `0` no lugar de `tenant.OccupiedSeats` | `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter-class "*TenantDetailsTests"`: `TenantAtivado_VoltaActiveComAVagaDoAdmin` |

As mutações 8 e 9 existem porque os testes do Passo 1 só tinham sido vistos vermelhos por erro de compilação, que não conta.

Todas as linhas da tabela foram aplicadas e vistas vermelhas no clone ao escrever este plano, com os testes e as contagens que a tabela traz.

**Um limite que esta rota traz, para o handoff:** o limitador de requisições roda **depois** da autorização (`Program.cs`). Um token que passa nas três camadas do token e não é de um membro ativo — o do ataque do grupo, ou o de um membro desativado — recebe `403` depois de uma consulta ao banco, e esse `403` não consome cota. Não é leitura indevida; é consulta sem limite por token. Mover o limitador para antes da autorização faria os `401` consumirem cota por IP, e é decisão do autor.

- [ ] **Passo 9: Commit**

```bash
git add src tests
git commit -m "feat: GET /api/v1/tenants/{tenantId}, a leitura do tenant por quem o administra

A primeira rota de tenant, com a policy TenantAdmin. Responde 200 com
tenantId, name, slug, status, plan (tier, maxUsers, maxClients),
occupiedSeats e registeredAt, e nunca o e-mail do admin inicial; 401 sem
token valido; 403 em todo o resto, com o mesmo Problem Details, inclusive
para tenant que nao existe. GetTenantQuery e o handler leem a projecao
TenantDetailsView, que nao seleciona o e-mail; no modulo, a falha do
handler vira o mesmo 403 da autorizacao, e nao 404.

Testes: a suite negativa de autorizacao por HTTP, o conjunto exato de
chaves do 200, a ordem dos handlers na composicao real com uma porta que
conta consultas, o teste de subida que exige tenantId em toda rota com
policy de tenant, a projecao contra o PostgreSQL e a regra que recusa
e-mail na leitura. A suite negativa de autenticacao passa a percorrer a
rota nova. Mutacoes: policy de tenant em rota sem tenantId, falha como
404, policy sem o NotPlatformAdmin e sem a pertenca, e-mail na resposta,
handler da pertenca antes do AddAuthorization e tenant comparado como
texto."
```

Run: `git log -1 --format=%B | grep -Eci "co-authored|generated with"`
Expected: `0`.

---

### Tarefa 16: O admin convidado lê o próprio tenant — Keycloak real, app da jornada, CI e README

Spec: §4.6 (a fase `jornada`, o passo da D2), §4.7 ("o passo da D2" do README), §5.1 (segunda linha "Coleção com Keycloak real na API"; linha "CI"), §5.2 (casos `K` da D2), §5.3 ("Tirar o `MemberRequirement`…: K: ataque do grupo"; "Outbox desligado na `ApiComKeycloakFactory`").

Até aqui a rota foi provada com tokens que o próprio teste forjou e com membros gravados à mão. Esta tarefa fecha a volta inteira: o platform-admin registra o tenant, o Outbox o provisiona no Keycloak, o admin convidado conclui o convite, entra pelo device flow e lê o tenant. É onde aparece uma divergência entre o `sub` que o provisionamento gravou e o `sub` que o Keycloak emite — ou entre o `tenant_id` do atributo e o id do tenant.

E reproduz, contra o Keycloak real, o ataque que justifica a pertença: um `tenant_id` herdado de um grupo.

**Nada desta tarefa foi executado ao escrever o plano**, além da compilação do app com as edições do Passo 4. Depende do código da D1 rodando.

**Arquivos:**
- Modify: `tests/IdentityGateway.Api.FunctionalTests/ApiComKeycloakFactory.cs`
- Create: `tests/IdentityGateway.Api.FunctionalTests/LeituraDeTenantComKeycloakTests.cs`
- Modify: `tools/jornada-compose.cs`, `.github/workflows/ci.yml`, `README.md`

**Interfaces:**
- Consome: `ApiComKeycloakFactory` (`Keycloak`, `CriarClienteComoAsync`), `ColecaoComKeycloak.Nome` (Tarefa 9); `KeycloakFixture.NovoPlatformAdminAsync`, `NovoUsuarioAsync`, `LinkDoConviteAsync`, `CriarHarness`, `EmailUnico`, `CriarGrupoComoMasterAsync`, `PorNoGrupoComoMasterAsync`, `ApagarGrupoComoMasterAsync` (Tarefas 4 e 5); `HarnessDeLogin.ConcluirLinkDeAcoesAsync` e `TokenPorDispositivoAsync`; `PayloadDoJwt.Ler`; `SenhasDeTeste.Gerar`; a rota da Tarefa 15.
- Produz: nada que outra tarefa consuma.

- [ ] **Passo 1: O Outbox ligado na coleção com Keycloak real**

Em `tests/IdentityGateway.Api.FunctionalTests/ApiComKeycloakFactory.cs`, em `ConfigureWebHost`, trocar o comentário e a linha do `Outbox:Enabled`:

```csharp
        // Desligado na D1: nenhum teste daqui espera o provisionamento. A D2 o liga, para o admin convidado existir.
        builder.UseSetting("Outbox:Enabled", "false");
```

por:

```csharp
        // Ligado, ao contrário da factory do OIDC falso: aqui o provisionamento precisa acontecer, para o admin
        // convidado existir no Keycloak e como Member no banco. O motivo de desligá-lo lá — a corrida com asserções
        // sobre a tabela do Outbox — não vale para esta coleção, que roda em série e tem banco próprio. Um segundo de
        // intervalo, o mínimo que a option aceita, para o teste não esperar os cinco do padrão.
        builder.UseSetting("Outbox:Enabled", "true");
        builder.UseSetting("Outbox:PollingIntervalSeconds", "1");
```

Efeito nos testes da D1 que já estão na coleção: o tenant que `PlatformAdminDoKeycloak_RegistraUmTenant` registra passa a ser provisionado em segundo plano — cria uma Organization e manda um convite ao mailpit do fixture. Nenhum deles afirma nada sobre isso. Um detalhe a saber: depois da primeira Organization, o login do Keycloak passa a ter dois passos; o harness lida com os dois casos.

**O host derivado do teste do `PublicBaseUrl` errado não pode herdar o Outbox.** Em `tests/IdentityGateway.Api.FunctionalTests/TokensDoKeycloakNaApiTests.cs`, o teste que sobe uma segunda Api com o `PublicBaseUrl` trocado (`WithWebHostBuilder`) herdaria o Outbox ligado: seria um segundo despachante sobre o mesmo banco, com uma credencial que o Keycloak recusa (o `aud` do assertion sai do `PublicBaseUrl`), reservando mensagens que só voltam à fila uns dez segundos depois. No lambda do `WithWebHostBuilder` desse teste, acrescentar:

```csharp
            builder.UseSetting("Outbox:Enabled", "false");
```

- [ ] **Passo 2: Os testes**

`tests/IdentityGateway.Api.FunctionalTests/LeituraDeTenantComKeycloakTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Domain.ValueObjects;
using IdentityGateway.Infrastructure.Persistence;
using IdentityGateway.Testing.Keycloak;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// A leitura de tenant com tudo de verdade: o Keycloak emite o token, o Outbox provisiona, e o banco diz quem é membro.
/// </summary>
/// <remarks>
/// <para>
/// <b>O que os testes com o OIDC falso não provam:</b> que o admin convidado pelo provisionamento real recebe do
/// Keycloak um token com <c>tenant-admin</c> e o <c>tenant_id</c> do tenant dele, e que o <c>sub</c> desse token é o
/// que o provisionamento gravou como <c>Member</c>. Se uma das duas pontas divergir, a rota responde <c>403</c> ao
/// dono do tenant — e só aqui isso aparece.
/// </para>
/// <para>
/// <b>Cada <c>403</c> vem com a premissa afirmada antes:</b> o que o token traz, lido do próprio token. Um
/// <c>403</c> sozinho pode ser de qualquer camada.
/// </para>
/// </remarks>
[Collection(ColecaoComKeycloak.Nome)]
public sealed class LeituraDeTenantComKeycloakTests(ApiComKeycloakFactory api)
{
    private static readonly string[] SoTenantAdmin = ["tenant-admin"];

    private static readonly string[] ChavesDoTenant =
        ["tenantId", "name", "slug", "status", "plan", "occupiedSeats", "registeredAt"];

    private static readonly string[] ChavesDoPlano = ["tier", "maxUsers", "maxClients"];

    private static readonly TimeSpan PrazoDoProvisionamento = TimeSpan.FromSeconds(90);

    private static string Rota(Guid tenant) => $"/api/v1/tenants/{tenant}";

    private static async Task<(Guid Tenant, Uri Acompanhamento)> RegistrarAsync(
        HttpClient comoPlatformAdmin, string emailDoAdmin, CancellationToken ct)
    {
        using HttpResponseMessage resposta = await comoPlatformAdmin.PostAsJsonAsync(
            "/api/v1/tenants",
            new
            {
                name = "Acme Corp",
                slug = $"kc-{Guid.NewGuid():N}"[..20],
                planCode = "free",
                initialAdminEmail = emailDoAdmin,
            },
            ct);
        resposta.StatusCode.Should().Be(HttpStatusCode.Accepted);
        JsonElement corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>(ct);

        return (corpo.GetProperty("tenantId").GetGuid(), resposta.Headers.Location!);
    }

    /// <summary>Espera o Outbox provisionar o tenant no Keycloak. Com prazo: sem o Outbox ligado, ele nunca chega.</summary>
    private static async Task EsperarAtivoAsync(HttpClient comoPlatformAdmin, Uri acompanhamento, CancellationToken ct)
    {
        DateTimeOffset fim = DateTimeOffset.UtcNow + PrazoDoProvisionamento;
        string? status = null;

        while (DateTimeOffset.UtcNow < fim)
        {
            using HttpResponseMessage resposta = await comoPlatformAdmin.GetAsync(acompanhamento, ct);
            resposta.StatusCode.Should().Be(HttpStatusCode.OK);
            status = (await resposta.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("status").GetString();

            if (status is "Active")
            {
                return;
            }

            status.Should().NotBe("ProvisioningFailed", "o provisionamento não pode desistir neste cenário");
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        status.Should().Be("Active", $"o tenant precisa ser provisionado em {PrazoDoProvisionamento.TotalSeconds:0} s");
    }

    private async Task<HttpResponseMessage> LerComAsync(string accessToken, Guid tenant, CancellationToken ct)
    {
        using HttpClient client = api.CreateClient();
        using HttpRequestMessage pedido = new(HttpMethod.Get, Rota(tenant));
        pedido.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await client.SendAsync(pedido, ct);
    }

    private static string[] Roles(JsonElement payload) =>
        payload.TryGetProperty("roles", out JsonElement roles)
            ? [.. roles.EnumerateArray().Select(role => role.GetString()!)]
            : [];

    [Fact]
    public async Task AdminConvidado_LeOProprioTenant_ENaoLeOutro_EOPlatformAdminNaoLeNenhum()
    {
        // Uma jornada só, porque o cenário é caro (dois convites, três device flows e um provisionamento). Cada
        // asserção diz qual das três afirmações falhou.
        CancellationToken ct = TestContext.Current.CancellationToken;
        UsuarioDeTeste operador = await api.Keycloak.NovoPlatformAdminAsync(ct);
        using HttpClient comoOperador = await api.CriarClienteComoAsync(operador, ct);
        string emailDoAdmin = KeycloakFixture.EmailUnico();

        (Guid tenant, Uri acompanhamento) = await RegistrarAsync(comoOperador, emailDoAdmin, ct);
        (Guid outroTenant, _) = await RegistrarAsync(comoOperador, KeycloakFixture.EmailUnico(), ct);
        await EsperarAtivoAsync(comoOperador, acompanhamento, ct);

        // O admin conclui o convite pelo link do e-mail, como faria no navegador, e entra pelo device flow.
        string senha = SenhasDeTeste.Gerar();

        using (HarnessDeLogin navegador = api.Keycloak.CriarHarness())
        {
            await navegador.ConcluirLinkDeAcoesAsync(await api.Keycloak.LinkDoConviteAsync(emailDoAdmin, ct), senha, ct);
        }

        using HarnessDeLogin harness = api.Keycloak.CriarHarness();
        TokensDeUsuario doAdmin = await harness.TokenPorDispositivoAsync(emailDoAdmin, senha, ct);
        JsonElement payload = PayloadDoJwt.Ler(doAdmin.AccessToken);

        // Premissas, lidas do token que o Keycloak emitiu: o papel e o tenant vêm do provisionamento.
        Roles(payload).Should().BeEquivalentTo(SoTenantAdmin);
        payload.GetProperty("tenant_id").GetString().Should().Be(tenant.ToString());
        payload.GetProperty("tenant_id").GetString().Should().NotBe(outroTenant.ToString());

        using HttpResponseMessage doProprio = await LerComAsync(doAdmin.AccessToken, tenant, ct);
        using HttpResponseMessage doOutro = await LerComAsync(doAdmin.AccessToken, outroTenant, ct);
        using HttpResponseMessage peloOperador = await comoOperador.GetAsync(new Uri(Rota(tenant), UriKind.Relative), ct);

        doProprio.StatusCode.Should().Be(HttpStatusCode.OK, "o admin convidado lê o próprio tenant");
        JsonElement corpo = await doProprio.Content.ReadFromJsonAsync<JsonElement>(ct);
        corpo.EnumerateObject().Select(chave => chave.Name).Should().BeEquivalentTo(ChavesDoTenant);
        corpo.GetProperty("plan").EnumerateObject().Select(chave => chave.Name).Should().BeEquivalentTo(ChavesDoPlano);
        corpo.GetProperty("tenantId").GetGuid().Should().Be(tenant);
        corpo.GetProperty("status").GetString().Should().Be("Active");
        corpo.GetProperty("occupiedSeats").GetInt32().Should().Be(1);

        doOutro.StatusCode.Should().Be(HttpStatusCode.Forbidden, "o tenant de outro id existe, e não é dele");
        peloOperador.StatusCode.Should().Be(HttpStatusCode.Forbidden, "o platform-admin registra, mas não lê o tenant");
    }

    [Fact]
    public async Task TenantIdHerdadoDeUmGrupo_NaoBasta_SemSerMembroNoBanco()
    {
        // O ataque que a pertença fecha (ADR-011), reproduzido como quem tem a chave da Gateway o faria: um usuário
        // SEM o atributo tenant_id, com o papel tenant-admin, posto num grupo que tem o tenant_id de um tenant que
        // existe. O mapper do Keycloak recua para o atributo do grupo, e o token sai com o tenant da vítima.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Guid vitima = await TenantAtivoNoBancoAsync(ct);
        UsuarioDeTeste atacante = await api.Keycloak.NovoUsuarioAsync(SoTenantAdmin, tenantId: null, ct);
        string grupo = await api.Keycloak.CriarGrupoComoMasterAsync(
            $"ataque-{Guid.NewGuid():N}",
            new Dictionary<string, string[]> { ["tenant_id"] = [vitima.ToString()] },
            ct);

        try
        {
            await api.Keycloak.PorNoGrupoComoMasterAsync(atacante.Id, grupo, ct);
            using HarnessDeLogin harness = api.Keycloak.CriarHarness();
            TokensDeUsuario tokens = await harness.TokenPorDispositivoAsync(atacante.Email, atacante.Senha, ct);
            JsonElement payload = PayloadDoJwt.Ler(tokens.AccessToken);

            // Premissas: o token passa nas três camadas do token. Sem elas afirmadas, o 403 poderia vir de qualquer uma.
            Roles(payload).Should().BeEquivalentTo(SoTenantAdmin);
            payload.GetProperty("tenant_id").GetString().Should().Be(
                vitima.ToString(), "o token precisa trazer o tenant_id herdado do grupo, ou o teste não prova a pertença");

            using HttpResponseMessage resposta = await LerComAsync(tokens.AccessToken, vitima, ct);

            resposta.StatusCode.Should().Be(HttpStatusCode.Forbidden, "o sub do atacante não é Member do tenant");
        }
        finally
        {
            // O realm não tem grupos, e outros testes afirmam isso.
            await api.Keycloak.ApagarGrupoComoMasterAsync(grupo, CancellationToken.None);
        }
    }

    /// <summary>
    /// Um tenant já ativo, gravado direto no banco, com outro membro como admin.
    /// </summary>
    /// <remarks>
    /// Registrado e ativado em memória e gravado num commit só: o tenant já nasce <c>Active</c>, e o consumidor do
    /// provisionamento — que aqui está ligado — ignora a mensagem de registro em vez de tentar provisioná-lo.
    /// </remarks>
    private async Task<Guid> TenantAtivoNoBancoAsync(CancellationToken ct)
    {
        var tenant = Tenant.Register(
            "Vítima",
            TenantSlug.Create($"vt-{Guid.NewGuid():N}"[..20]).Value,
            new Plan(PlanTier.Free, 5, 1),
            Email.Of(KeycloakFixture.EmailUnico()).Value,
            DateTimeOffset.UtcNow);
        Member admin = tenant.CompleteProvisioning(
            $"org-{Guid.NewGuid():N}", ExternalUserId.From(Guid.NewGuid().ToString()), DateTimeOffset.UtcNow);

        using IServiceScope escopo = api.Services.CreateScope();
        AppDbContext contexto = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
        contexto.Tenants.Add(tenant);
        contexto.Members.Add(admin);
        await contexto.SaveChangesAsync(ct);

        return tenant.Id.Value;
    }
}
```

O tenant do segundo teste é gravado já `Active`, num commit só (conferido ao escrever o plano que o EF aceita o tenant e o membro novos no mesmo `SaveChanges`). Se fosse gravado `Pending` e ativado depois, o Outbox — ligado aqui — poderia pegar a mensagem de registro entre os dois commits e tentar provisioná-lo.

- [ ] **Passo 3: Rodar e ver passar**

Run: `dotnet test tests/IdentityGateway.Api.FunctionalTests --filter-class "*LeituraDeTenantComKeycloakTests"`
Expected: `total: 2`, `falhou: 0`. O primeiro leva de 30 a 60 s (dois convites concluídos, três device flows e a espera do `Active`).

Se o primeiro falhar em `payload.GetProperty("tenant_id")…`, o token do admin convidado não traz o tenant: confira que o provisionamento grava o atributo `tenant_id` no usuário e que o client de demonstração tem o scope `gateway-tenant` (Tarefa 2). **Não** é caso de mexer no realm nesta tarefa — se o realm estiver errado, o defeito é da D1, e o conserto volta para lá.

Run: `dotnet test tests/IdentityGateway.Api.FunctionalTests`
Expected: a suíte funcional inteira verde, com 2 testes a mais. Os testes da D1 na mesma coleção continuam verdes com o Outbox ligado.

- [ ] **Passo 4: O app da jornada**

Em `tools/jornada-compose.cs`, quatro edições.

1. No cabeçalho, a descrição da fase `jornada`:

```csharp
//   jornada                 link do platform-admin → device flow → HS256 antigo recusado → POST /tenants → Active →
//                           o admin do tenant conclui o convite, entra e lê o próprio tenant (200); outro tenant e o
//                           platform-admin recebem 403
```

2. Depois da constante `ClientDeDemonstracao`:

```csharp

// O contrato do 200 da leitura de tenant: exatamente estas chaves. Uma a mais (o e-mail do admin, por exemplo) reprova.
string[] chavesDoTenant = ["tenantId", "name", "slug", "status", "plan", "occupiedSeats", "registeredAt"];
string[] chavesDoPlano = ["tier", "maxUsers", "maxClients"];
```

3. Em `JornadaAsync`, o bloco `using (HarnessDeLogin navegador = NovoHarness()) { … }` — o que só conferia se o link abria — sai, e no lugar dele, entre a linha do `Uri linkDoAdmin = …` e a do `GravarEstado(…)`, entra:

```csharp

    // Um harness novo é uma janela anônima: sem o cookie de sessão do platform-admin, que faria o device flow seguinte
    // sair com a conta dele — e o 403 pareceria defeito.
    using HarnessDeLogin navegadorDoAdmin = NovoHarness();
    string senhaDoAdmin = Mascarar(SenhasDeTeste.Gerar());

    Etapa("o admin do tenant conclui o convite pelo link e obtém o token pelo device flow");
    await navegadorDoAdmin.ConcluirLinkDeAcoesAsync(linkDoAdmin, senhaDoAdmin, ct);
    TokensDeUsuario tokensDoAdmin = await navegadorDoAdmin.TokenPorDispositivoAsync(emailDoAdmin, senhaDoAdmin, ct);
    Mascarar(tokensDoAdmin.AccessToken);
    Mascarar(tokensDoAdmin.RefreshToken);

    // O Location do 202 é /api/v1/tenants/{id}/provisioning; a leitura do tenant é o mesmo caminho sem o sufixo.
    string rotaDoTenant = localizacao.ToString().Replace("/provisioning", string.Empty, StringComparison.Ordinal);

    Etapa("o admin lê o próprio tenant: 200, Active, com exatamente as chaves do contrato");
    using (HttpResponseMessage leitura = await ExigirAsync(
               HttpStatusCode.OK, HttpMethod.Get, rotaDoTenant, tokensDoAdmin.AccessToken, corpo: null))
    {
        JsonElement tenant = await leitura.Content.ReadFromJsonAsync<JsonElement>(ct);
        ExigirChaves(tenant, chavesDoTenant, "tenant");
        ExigirChaves(tenant.GetProperty("plan"), chavesDoPlano, "plan");

        if (tenant.GetProperty("status").GetString() != "Active")
        {
            throw new FalhaDoHarnessException(FamiliaDeFalha.Api, etapaAtual, "o tenant lido pelo admin não está Active.");
        }
    }

    Etapa("o admin recebe 403 ao ler outro tenant");
    await ExigirAsync(
        HttpStatusCode.Forbidden, HttpMethod.Get, $"/api/v1/tenants/{Guid.NewGuid()}", tokensDoAdmin.AccessToken, corpo: null);

    // O platform-admin registra e acompanha o provisionamento, mas não lê o tenant: sem auditoria, seria o único acesso
    // entre tenants sem trilha. O token dele é do começo da jornada, e de lá para cá houve a espera do Active e o
    // convite do admin: é renovado antes deste uso tardio. O refresh token novo é o que a fase grava no fim.
    Etapa("o platform-admin renova o token e recebe 403 ao ler o tenant");
    tokens = await harness.RenovarAsync(tokens.RefreshToken, ct);
    Mascarar(tokens.AccessToken);
    Mascarar(tokens.RefreshToken);
    await ExigirAsync(HttpStatusCode.Forbidden, HttpMethod.Get, rotaDoTenant, tokens.AccessToken, corpo: null);

```

Concluir o link prova mais do que abri-lo, e por isso a checagem `LinkDeAcoesAbreAsync` sai da jornada. O método continua na biblioteca.

4. Antes do comentário `// Toda asserção de status é EXATA.` (acima de `ExigirAsync`):

```csharp
void ExigirChaves(JsonElement objeto, string[] esperadas, string nome)
{
    string[] vieram = [.. objeto.EnumerateObject().Select(chave => chave.Name).Order(StringComparer.Ordinal)];

    if (!vieram.SequenceEqual(esperadas.Order(StringComparer.Ordinal), StringComparer.Ordinal))
    {
        throw new FalhaDoHarnessException(
            FamiliaDeFalha.Api, etapaAtual,
            $"{nome}: esperadas as chaves [{string.Join(", ", esperadas)}], vieram [{string.Join(", ", vieram)}].");
    }
}

```

Run: `dotnet build -c Release tools/jornada-compose.cs`
Expected: `0 Aviso(s)`, `0 Erro(s)`.

Run: `dotnet test tests/IdentityGateway.ArchitectureTests --filter-class "*RegrasDeFerramentasTests"`
Expected: PASS nos três.

- [ ] **Passo 5: Rodar a jornada num projeto isolado**

As mesmas pré-condições da Tarefa 11: nada do projeto padrão de pé, portas livres, Git Bash na raiz do repositório.

```bash
# Sem IG_ESTADO: o app grava o estado no diretório temporário do usuário, que no Git Bash é o /tmp. Um caminho do
# Git Bash numa variável (como o de mktemp) pode chegar ao .NET sem tradução no Windows.
unset IG_ESTADO
docker compose -p igverif up -d --build --wait --wait-timeout 300 api
dotnet run -c Release tools/jornada-compose.cs -- jornada; echo "exit=$?"
```

Expected: `exit=0`, com as quatro etapas novas na saída — `ok … o admin do tenant conclui o convite pelo link e obtém o token pelo device flow`, `ok … o admin lê o próprio tenant: 200, Active, com exatamente as chaves do contrato`, `ok … o admin recebe 403 ao ler outro tenant` e `ok … o platform-admin renova o token e recebe 403 ao ler o tenant`. O login do admin do tenant leva dois passos (já existe uma Organization); o harness não afirma o número de passos dele.

Reveja a saída: nenhum `eyJ`, nenhum `action-token?key=`, nenhum `user_code`.

Anote os tempos das etapas para o handoff. **Não derrube o `igverif`**: o Passo 8 usa.

- [ ] **Passo 6: O workflow**

Em `.github/workflows/ci.yml`, no job `compose`, o comentário do passo `A jornada com token do Keycloak` passa a ser:

```yaml
      # O convite do platform-admin concluído pelo link, o token pelo device flow, a receita simétrica antiga recusada e
      # o tenant registrado e provisionado. Depois, o admin do tenant: conclui o convite pelo link, entra por outro
      # device flow e lê o próprio tenant (200, com exatamente as chaves do contrato); outro tenant e o
      # platform-admin levam 403.
```

O comando e o `timeout-minutes: 5` do passo não mudam: a fase tem o mesmo nome, e as etapas novas somam cerca de 15 s.

- [ ] **Passo 7: O README**

Em `README.md`, na seção `### Demonstração: token do Keycloak, e o tenant provisionado quando o Keycloak volta`, logo depois do parágrafo que termina em `e o endereço passa a existir só no Keycloak.` (antes de `**Por que nessa ordem.**`):

````markdown

**6. O administrador do tenant lê o próprio tenant — e só ele.** Abra o link do convite numa **janela anônima** do
navegador, defina a senha e informe nome e sobrenome. Tem que ser janela anônima: na janela normal, a sessão do
platform-admin continua aberta no Keycloak, e o device flow abaixo sairia com a conta dele — e o `403` que ele recebe
pareceria defeito.

```bash
pedido=$(curl -s -X POST "$KC/auth/device" -d client_id=identity-gateway-demo -d scope=openid)
DEVICE_CODE=$(echo "$pedido" | jq -r .device_code | tr -d '\r')
echo "$pedido" | jq -r .verification_uri_complete
# Abra o endereço na MESMA janela anônima, entre com o e-mail de $EMAIL e a senha nova, e aceite o consentimento.
```

Depois de aceitar, espere uns 5 segundos e troque o código pelo token do administrador:

```bash
TOKEN_ADMIN=$(curl -s -X POST "$KC/token" \
  -d grant_type=urn:ietf:params:oauth:grant-type:device_code \
  -d client_id=identity-gateway-demo -d device_code="$DEVICE_CODE" | jq -r .access_token | tr -d '\r')

curl -s http://localhost:8080/api/v1/tenants/{id} -H "Authorization: Bearer $TOKEN_ADMIN"
# {"tenantId":"…","name":"Acme","slug":"acme-…","status":"Active",
#  "plan":{"tier":"Free","maxUsers":5,"maxClients":1},"occupiedSeats":1,"registeredAt":"…"}

curl -s -o /dev/null -w '%{http_code}\n' -H "Authorization: Bearer $TOKEN_ADMIN" \
  http://localhost:8080/api/v1/tenants/00000000-0000-0000-0000-000000000000
# 403: não é o tenant dele. A resposta é a mesma para um tenant de outra pessoa e para um que não existe.

curl -s -o /dev/null -w '%{http_code}\n' -H "Authorization: Bearer $TOKEN" \
  http://localhost:8080/api/v1/tenants/{id}
# 403: o platform-admin registra o tenant e acompanha o provisionamento, mas não o lê.
```

**Por que o platform-admin leva `403`.** A leitura de tenant pelo operador da plataforma é o único acesso entre
tenants do produto, e só entra junto com a auditoria que registra cada um. Até lá, ele não lê. E a leitura do
administrador não confia só no token: além do papel e do `tenant_id`, a API confere no próprio banco que ele é
membro daquele tenant.

Se o último comando responder `401`, o token do platform-admin venceu (5 minutos): renove-o como no passo 5 e repita.
````

Run: `dotnet test tests/IdentityGateway.ArchitectureTests`
Expected: verde — as regras que leem o README (o padrão do e-mail do platform-admin) continuam valendo.

- [ ] **Passo 8: 🧪 Provas por mutação**

`git add -A src tests tools` antes; `git restore` do arquivo depois de cada uma.

| # | Mutação | Rodar | Vermelho esperado |
|---|---|---|---|
| 1 | **Outbox desligado:** em `ApiComKeycloakFactory`, `builder.UseSetting("Outbox:Enabled", "false");` | `dotnet test tests/IdentityGateway.Api.FunctionalTests --filter-class "*LeituraDeTenantComKeycloakTests"` | `AdminConvidado_LeOProprioTenant_…`, depois de 90 s: "o tenant precisa ser provisionado em 90 s" (ficou `Pending`) |
| 2 | **Sem a pertença:** em `AutorizacaoDaGateway`, tirar `new MemberRequirement()` da policy | idem | `TenantIdHerdadoDeUmGrupo_NaoBasta_SemSerMembroNoBanco`: `200` no lugar de `403`. As premissas do teste passam — o token traz o `tenant_id` herdado do grupo |
| 3 | **A jornada pega a chave a mais:** em `TenantDetailsResponse`, acrescentar `public string? InitialAdminEmail { get; init; }` (a mutação 5 da Tarefa 15), reconstruir a imagem (`docker compose -p igverif up -d --build --wait --wait-timeout 300 api`) | num ambiente novo (`docker compose -p igverif down -v` e `up` de novo, porque o convite do platform-admin já foi concluído), `dotnet run -c Release tools/jornada-compose.cs -- jornada` | `exit=40`: "tenant: esperadas as chaves […], vieram […, initialAdminEmail, …]" |

Depois da mutação 3, reverter o arquivo e reconstruir a imagem não é necessário: o ambiente é derrubado em seguida.

Run: `docker compose -p igverif down -v`
Expected: remove os contêineres e os volumes `igverif_*`. Os volumes `identitygateway_*` não aparecem na saída.

Run: `rm -f /tmp/ig-jornada-estado.json`

- [ ] **Passo 9: Commit**

```bash
git add tests tools .github/workflows/ci.yml README.md
git commit -m "test: o admin convidado le o proprio tenant, do Keycloak real a jornada da CI

A colecao com Keycloak real liga o Outbox e percorre a volta inteira: o
platform-admin registra o tenant, o provisionamento convida o admin, ele
conclui o convite, entra pelo device flow e le o proprio tenant com
exatamente as chaves do contrato; outro tenant e o platform-admin levam
403. O ataque do tenant_id herdado de um grupo e reproduzido pelo master,
com o claim afirmado no token antes do 403.

O app da jornada ganha as mesmas etapas, no lugar da checagem de que o
link do convite abre, e o README, o passo do administrador em janela
anonima, com o motivo do 403 do platform-admin.

Rodado localmente num projeto isolado (igverif). Mutacoes: Outbox
desligado na factory, policy sem a pertenca e uma chave a mais na
resposta."
```

Run: `git log -1 --format=%B | grep -Eci "co-authored|generated with"`
Expected: `0`.

---

### Tarefa 17: Fechar as marcas "(D2, planejado)", README e handoff da D2

Spec: §6 ("Documentos": a D2 fecha os itens marcados), §9, §11 (entregáveis).

A v2.7 e o documento de negócio entraram com a D1 descrevendo o design inteiro, com o que só a D2 entregaria marcado `(D2, planejado)`. A rota existe agora: as marcas saem. O que a execução da D2 tiver desmentido do texto entra como errata, e não como reescrita silenciosa.

**Arquivos:**
- Modify: `docs/especificacao-arquitetural-v2.7.md`, `docs/documentacao-negocio.md`, `README.md`
- Create: `docs/superpowers/specs/AAAA-MM-DD-tokens-keycloak-d2-handoff.md` (data do dia, `date +%F`)

**Interfaces:**
- Consome: tudo (Tarefas 13–16) e os documentos da Tarefa 12.
- Produz: documentação.

**Regra desta tarefa, a mesma da Tarefa 12:** todo trecho "a localizar" existe literalmente no arquivo e aparece uma vez só. Se um `Edit` não achar o trecho, o arquivo mudou fora do roteiro: pare e confira. **As duas linhas da v2.7 que trazem a sequência de escape do "e comercial" não são tocadas por nenhum passo daqui** — e o Passo 3 confere que ela continua lá.

- [ ] **Passo 1: O ponto de partida**

Run: `grep -c "(D2, planejado)" docs/especificacao-arquitetural-v2.7.md docs/documentacao-negocio.md README.md`
Expected: `33`, `21` e `0`. São os números que a Tarefa 12 conferiu. Outro número quer dizer que alguém mexeu nas marcas entre os dois PRs: liste com `grep -n "planejad"` e entenda antes de seguir.

- [ ] **Passo 2: v2.7 — as quatro marcas que não saem só por apagar**

Em `docs/especificacao-arquitetural-v2.7.md`:

1. Na §0, localizar (são duas linhas; a quebra fica entre `marcado` e a marca):

```markdown
primeira rota de tenant. Esta versão entra com a D1 e descreve o design inteiro: **o que só a D2 entrega está marcado
"(D2, planejado)"**, e a D2 tira as marcas. A v2.7 registra só o que a fatia implementa, mais as erratas: a sequência
```

e substituir por:

```markdown
primeira rota de tenant. Esta versão entrou com a D1, descrevendo o design inteiro, com o que só a D2 entregaria
marcado como planejado; **a D2 entregou a rota e tirou as marcas**. A v2.7 registra só o que a fatia implementa, mais as erratas: a sequência
```

2. No ADR-011, localizar:

```markdown
- **Estado:** decidido na v2.7; a implementação chega com a primeira rota de tenant (D2, planejado).
```

e substituir por:

```markdown
- **Estado:** decidido na v2.7 e implementado com a primeira rota de tenant, `GET /tenants/{tenantId}`.
```

3. Na §13, na tabela de testes, localizar o começo da última linha:

```markdown
| Autorização da rota de tenant (D2, planejado) |
```

e substituir por:

```markdown
| Autorização da rota de tenant (v2.7) |
```

4. Na §16, na tabela de fatias, localizar o fim da linha da fatia D:

```markdown
| M0 + M1 | D1 entregue; D2 (D2, planejado) |
```

e substituir por (o número do PR da D1 sai de `git log --oneline --merges main | head`; o da D2 entra depois do merge, como o da D1 entrou):

```markdown
| M0 + M1 | Entregue (D1: PR #<número>; D2: nesta entrega) |
```

5. Na §8, um complemento que a execução mostrou necessário. Localizar, no parágrafo "A primeira rota de tenant":

```markdown
e a rota não usa `404`.
```

e substituir por:

```markdown
e a rota não usa `404`. Um `tenantId` que não é GUID não casa a rota (restrição `:guid`): é um caminho não mapeado, que responde `404` a quem está autenticado e não diz nada sobre tenant nenhum.
```

- [ ] **Passo 3: v2.7 — as demais marcas, de uma vez**

Todas as outras ocorrências são a marca solta depois de uma frase, de um título de linha ou de um comentário de código; apagar ` (D2, planejado)`, com o espaço da frente, deixa o texto certo.

Run: `sed -i 's/ (D2, planejado)//g' docs/especificacao-arquitetural-v2.7.md`

Run: `grep -c "D2, planejado" docs/especificacao-arquitetural-v2.7.md`
Expected: `0`.

Run: `grep -cF -- "$(printf '\134u0026')" docs/especificacao-arquitetural-v2.7.md`
Expected: `2` — a sequência de escape da errata E1 continua nas duas linhas. Se der menos, um `Edit` a decodificou: refaça essas linhas pelo caminho do Passo 21 da Tarefa 12 (marcador e `perl`).

Run: `git diff --stat -- docs/especificacao-arquitetural-v2.7.md`
Expected: cerca de 33 linhas alteradas, nenhuma inserida nem removida além das do Passo 2.

Leia o diff inteiro (`git diff -- docs/especificacao-arquitetural-v2.7.md`): cada linha alterada tem que continuar sendo uma frase. Atenção às três do código de referência da §11.7 e à da §11.8, que são comentários (`// Authorization/SameTenantRequirement.cs`).

**O que a execução desmentiu.** Compare o código entregue pelas Tarefas 13 a 15 com o texto da v2.7 nas §6.4, §8, §10.1, §11.7, §11.8, §11.9 e §13. Para cada divergência de **decisão** (não de redação nem de nome de variável), acrescente uma errata no fim da lista de erratas da §0, numerada a partir de `E9`, no formato das demais ("**E9** (§N): o que o texto dizia, e o que vale"), e corrija o trecho. Divergências já conhecidas ao escrever este plano: nenhuma — o código de referência da §11.7 já traz a checagem do tamanho do claim, e o complemento do `404` de rota entrou no Passo 2. Se não houver nenhuma, não acrescente nada.

- [ ] **Passo 4: Documento de negócio**

Em `docs/documentacao-negocio.md`:

1. No cabeçalho, localizar:

```markdown
convite do admin inicial e tokens do Keycloak (primeira parte, D1) entregues.
```

e substituir por:

```markdown
convite do admin inicial e tokens do Keycloak (D1 e D2) entregues.
```

2. Na "Nota da versão 1.4", localizar as três últimas linhas da nota:

```markdown
> RN-028 (pertença do ator ao tenant) e a RN-029 (separação de funções). O que só a segunda parte da fatia
> entrega — a rota `GET /tenants/{tenantId}` e as regras que a protegem — está marcado **"(D2, planejado)"**. As
> citações `§N` continuam válidas.
```

e substituir por:

```markdown
> RN-028 (pertença do ator ao tenant) e a RN-029 (separação de funções). O que só a segunda parte da fatia
> entregava — a rota `GET /tenants/{tenantId}` e as regras que a protegem — ficou marcado como planejado até a D2
> ser entregue, quando as marcas saíram. As citações `§N` continuam válidas.
```

3. As demais, de uma vez:

Run: `sed -i 's/ (D2, planejado)//g' docs/documentacao-negocio.md`

Run: `grep -c "D2, planejado" docs/documentacao-negocio.md`
Expected: `0`.

Leia o diff (`git diff -- docs/documentacao-negocio.md`): cerca de 21 linhas, cada uma ainda uma frase. A matriz de permissões e as RN-028 e RN-029 deixam de dizer "planejado" e passam a descrever o que a rota faz — confira que o texto delas bate com o comportamento: `403` para o platform-admin, pertença em `Invited` ou `Active`, e quem acumula `platform-admin` e `tenant-admin` negado.

- [ ] **Passo 5: README**

Em `README.md` (a demonstração já ganhou o passo 6 na Tarefa 16):

1. No parágrafo de estado, localizar o título:

```markdown
admin inicial e tokens do Keycloak (D1) entregues.**
```

e substituir por:

```markdown
admin inicial e tokens do Keycloak (D1 e D2) entregues.**
```

2. No mesmo parágrafo, localizar:

```markdown
M0 "primeiro `curl` com token do Keycloak". **Próximo passo:** a D2, a primeira rota de tenant
(`GET /api/v1/tenants/{tenantId}`), em que o admin convidado lê o próprio tenant.
```

e substituir por:

```markdown
M0 "primeiro `curl` com token do Keycloak". A segunda parte acrescentou a primeira rota de tenant,
`GET /api/v1/tenants/{tenantId}`: o admin convidado lê o próprio tenant, e a autorização confere o token **e** a
pertença no banco (ADR-011). **Próximo passo:** a decidir — a proposta do design é a auditoria, que destrava a
leitura de tenant pelo platform-admin.
```

3. No fim do mesmo parágrafo, o link do handoff passa a ser o da D2. Localizar:

```markdown
[handoff da D1](docs/superpowers/specs/
```

e substituir a linha inteira por (com a data real do Passo 7 no lugar de `AAAA-MM-DD`):

```markdown
[handoff da D2](docs/superpowers/specs/AAAA-MM-DD-tokens-keycloak-d2-handoff.md).
```

4. Na tabela de ADRs, localizar:

```markdown
| 011 | Nas rotas de governança, autorização é token mais pertença no banco (decidido; a rota que o usa chega com a D2) |
```

e substituir por:

```markdown
| 011 | Nas rotas de governança, autorização é token mais pertença no banco |
```

5. A contagem de testes (a linha que começa com `# Toda a suíte —`) recebe os números do Passo 6.

- [ ] **Passo 6: Suíte completa**

Docker Desktop ligado.

Run: `dotnet build IdentityGateway.slnx`
Expected: `0 Aviso(s)`, `0 Erro(s)`.

Run: `dotnet build -c Release tools/jornada-compose.cs`
Expected: compila, sem avisos.

Run: `dotnet test`
Expected: 0 falhas, 0 skips nos cinco projetos. Em relação ao handoff da D1, a D2 acrescenta: `Application.UnitTests` +2, `ArchitectureTests` +3, `Infrastructure.IntegrationTests` +7, `Api.FunctionalTests` +79 (23 da Tarefa 13, 20 da 14, 34 da 15 e 2 da 16). Um total diferente não é erro por si — mas explique a diferença no handoff.

Atualizar a linha `# Toda a suíte —` do README com os números observados, no mesmo formato, e rodar de novo `dotnet test tests/IdentityGateway.ArchitectureTests` (há regras que leem o README).

- [ ] **Passo 7: Handoff da D2**

Criar `docs/superpowers/specs/AAAA-MM-DD-tokens-keycloak-d2-handoff.md` (data de `date +%F`), no formato do handoff da D1, com estas seções e o que foi **observado** em cada uma — nada de valor esperado no lugar de valor observado:

````markdown
# Handoff — tokens do Keycloak, parte D2 entregue

> **Data:** AAAA-MM-DD · **Marco:** M0 + M1 (fatia D, segunda parte) · **Status:** implementada, build e suíte
> completa verdes. Push e PR aguardam autorização do autor.
> **Onde parou:** as Tarefas 13 a 17 estão commitadas na branch `feat/leitura-do-tenant`; falta a revisão final da
> branch, enviar, abrir o PR contra `main`, acompanhar a CI e mesclar.
>
> Sucede o handoff da D1. Design: [`2026-09-30-tokens-keycloak-design.md`](2026-09-30-tokens-keycloak-design.md).
> Plano: [`2026-09-30-tokens-keycloak.md`](../plans/2026-09-30-tokens-keycloak.md). Referência normativa:
> [`especificacao-arquitetural-v2.7.md`](../../especificacao-arquitetural-v2.7.md).

---

## Estado do repositório

| O quê | Estado |
|---|---|
| Branch | `feat/leitura-do-tenant`, <N> commits sobre `main` (`<hash do merge da D1>`) |
| Working tree | Limpa depois do commit desta tarefa |
| Realm, compose e one-shot | **Não tocados.** `git diff --stat main -- keycloak docker-compose.yml` vazio: quem já subiu o compose depois da D1 não precisa de `down -v` |
| Push / PR | **Pendentes de autorização** |

## O que a D2 entregou

| Camada | Entregue |
|---|---|
| Api | `Authorization/RoleRequirement`, `NotPlatformAdminRequirement`, `SameTenantRequirement`, `MemberRequirement` e `MemberRequirementHandler`; `AutorizacaoDaGateway.AddAutorizacaoDaGateway` (policies, fallback, `InvokeHandlersAfterFailure` falso, handler da pertença registrado depois do `AddAuthorization`); `Policies.TenantAdmin` e `Policies.DeTenant`; `GET /api/v1/tenants/{tenantId}` no `TenantsModule`, com a falha do handler traduzida no mesmo `403` |
| Application | `IMemberQueries`; `GetTenantQuery`, `GetTenantHandler`, `TenantDetailsResponse`, `TenantPlanResponse`; `ITenantQueries.GetDetailsAsync` e `TenantDetailsView` |
| Infrastructure | `MemberQueries`; `TenantQueries.GetDetailsAsync` |
| Testes | Unitários da policy com um handler que aprova tudo; a pertença por estado, numa tabela escrita à mão; a ordem dos handlers, no contêiner de teste e na composição real; a suíte negativa de autorização por HTTP; o conjunto exato de chaves do `200`; o teste de subida das policies de tenant; as consultas contra o PostgreSQL; a volta inteira com Keycloak real e o ataque do grupo; as regras de arquitetura novas |
| CI e demonstração | A fase `jornada` do app com o admin convidado lendo o próprio tenant; o passo 6 da demonstração do README |
| Documentos | As marcas "(D2, planejado)" fechadas na v2.7 e no documento de negócio; README; este handoff |

## O que mudou em relação ao plano

<Uma entrada numerada por desvio: o que o plano supunha, o que a execução achou, o commit, e onde a v2.7 registra
(errata E9 em diante, se houve). Se nada mudou, "nada".>

## Suíte completa

<A tabela por projeto, como no handoff da D1, com os totais observados e a diferença em relação à D1.>

## Prova por mutação

<Uma linha por mutação das tabelas das Tarefas 13 a 16: a mutação, o teste que ficou vermelho e a mensagem
observada. As duas mutações que ficam verdes sozinhas (Tarefa 14, 7 e 8) entram como "verde, equivalente; vermelha
em par".>

## Verificação ao vivo

<A jornada no projeto isolado `igverif` (Tarefa 16, Passo 5): as etapas e os tempos. E, depois do PR aberto, o tempo
do job `Compose`.>

## Pendências

**Para o autor decidir:**
- A leitura do `tenantId` da rota: o plano implementou "qualquer formato de GUID na rota, só o formato `D` no claim"
  (a §5.2 do design); a §4.3 do design dizia formato `D` nos dois.
- <O que mais a execução levantou.>

**Limites que continuam abertos** (v2.7, §19): o platform-admin recebe `403` na leitura de tenant até existir a
auditoria; `Invited` passa na pertença até o aceite do convite ser sincronizado; o Data Plane continua exposto ao
`tenant_id` por grupo; a pertença não contém quem tem a chave da Gateway; e-mail digitado errado dá a leitura do
tenant ao destinatário errado.

**Um limite novo, da D2, para o autor decidir:** o limitador de requisições roda depois da autorização. O `403` de um
token que passa nas três camadas do token e não é de um membro ativo custa uma consulta ao banco e não consome cota.
Mover o limitador para antes da autorização faria os `401` consumirem cota por IP.

**Segue pendente do M0:** a tabela de auditoria, o armazenamento de eventos do realm e o RabbitMQ.

## Próximo passo

1. Revisão final da branch.
2. Autorizar o push e abrir o PR contra `main`. Não há aviso de `down -v` desta vez.
3. Acompanhar a CI e mesclar; depois, acrescentar o número do PR na linha da fatia D da §16 da v2.7.
4. Decidir a próxima fatia (o design propõe a auditoria, que destrava o `TenantReadAccess`).
````

Run: `grep -n "<\|AAAA-MM-DD" docs/superpowers/specs/*-tokens-keycloak-d2-handoff.md README.md`
Expected: nenhuma linha — toda lacuna do modelo começa com `<`.

Run: `git diff --stat main -- keycloak docker-compose.yml`
Expected: vazio. É a afirmação "a D2 não toca o realm nem o compose", conferida.

- [ ] **Passo 8: Commit**

Run: `git status --short`
Expected: `M` para `docs/especificacao-arquitetural-v2.7.md`, `docs/documentacao-negocio.md` e `README.md`, e `??` para o handoff da D2. Nada em `src/`, `tests/`, `tools/`, `keycloak/`, `.github/` nem `docker-compose.yml`.

```bash
git add docs README.md
git commit -m "docs: fecha os itens planejados da v2.7 com a D2 entregue, README e handoff

A rota GET /tenants/{tenantId}, a policy TenantAdmin e a pertenca no banco
existem: saem as marcas de planejado da especificacao v2.7 e do documento
de negocio, o ADR-011 passa a implementado, a linha da fatia D da secao 16
fica entregue, e a secao 8 registra que um tenantId que nao e GUID nao
casa a rota. O README atualiza o andamento e a contagem de testes, e o
handoff traz a suite, a tabela de mutacoes e a jornada rodada ao vivo."
```

Run: `git log -1 --format=%B | grep -Eci "co-authored|generated with"`
Expected: `0`.

Push e PR ficam para autorização do autor.

---

## Autorrevisão do plano

Feita ao fechar a redação, contra a spec:

- **Cobertura.** §4.2 → Tarefas 6 a 8. §4.3 → Tarefas 13 a 15. §4.4 → Tarefas 2 e 3. §4.5 → Tarefa 10. §4.6 → Tarefas 4, 5, 11 e 16. §4.7 → Tarefas 10, 11 e 16. §5.1 e §5.2 → cada linha tem tarefa dona, citada no cabeçalho "Spec:" da tarefa. §5.3 → as tabelas 🧪; as três mutações que o plano trata como equivalentes estão ditas onde aparecem (o `azp` lido por `FindFirst`, na Tarefa 8; `InvokeHandlersAfterFailure` e o `HasFailed`, cada um sozinho, na Tarefa 14; "consultar o tenant antes de autorizar", decomposta em duas, na Tarefa 15). §9 e §11 → Tarefas 12 e 17.
- **Sem lacunas de redação.** Os únicos `<…>` do plano estão nos modelos dos dois handoffs e na contagem de testes do README, que só existem depois da execução; cada um tem um passo que confere que foram preenchidos.
- **Consistência de nomes.** Os nomes produzidos por uma tarefa e consumidos por outra estão no bloco "Interfaces" das duas. Conferidos na redação: `Policies.TenantAdmin` e `Policies.DeTenant`; `SameTenantRequirement.ParametroDaRota`; `AddAutorizacaoDaGateway`; `IMemberQueries.GetStatusAsync`; `PertencaFalsa`; `MontagemDaAutorizacao`; `TenantsModule.ParaRespostaDoTenant`; `IdentityGatewayApiFactory.Emissor.Emitir(sub, roles, tenantId, ajustar)`; `ApiComKeycloakFactory.CriarClienteComoAsync`; os métodos do `KeycloakFixture` e do `HarnessDeLogin`.
- **Uma divergência da spec, deliberada e registrada para o autor:** o `tenantId` da rota em qualquer formato de GUID (Tarefa 13).
