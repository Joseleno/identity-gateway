# Fatia A — Fundação Keycloak

> **Data:** 2026-09-24 · **Marco:** misto M0/M1 (ver "Onde esta fatia cai no roadmap") · **Status:** design
> aprovado em conversa, revisado por seis especialistas, aguardando revisão da spec escrita.
> **Referência normativa:** [`especificacao-arquitetural-v2.4.md`](../../especificacao-arquitetural-v2.4.md),
> que nasce neste mesmo PR corrigindo a v2.3 com o que esta fatia verificou.
> **Sucede:** [handoff da vertical entregue](2026-09-22-vertical-entregue-handoff.md) (PR #1, mesclado em
> 2026-09-24).

---

## 1. Por que esta fatia existe

O próximo passo depois do registro de tenant é o provisionamento da §9.1: consumir `tenant-registered`,
garantir a Organization no Keycloak, garantir o convite do `initialAdminEmail` com `tenant-admin` e marcar o
tenant `Active`. A exploração do repositório mostrou que **nada disso tem onde se apoiar**:

- não há Keycloak no `docker-compose.yml`, nem JSON de bootstrap do realm, nem Testcontainers de Keycloak;
- não há RabbitMQ nem MassTransit — o Outbox termina no `LoggingOutboxPublisher`;
- o `initialAdminEmail` é validado pelo command e **descartado** pelo handler: `RegisterTenantHandler.cs:52`
  chama `Tenant.Register(command.Name, slug.Value, plano)` sem ele, e `TenantRegistered` carrega só
  `TenantId` e `Slug`. O handoff de 2026-09-22 afirma o contrário, e o comentário de
  `RegisterTenantCommand.cs:16-17` também — ambos errados.

Por isso o provisionamento foi fatiado em três, cada uma com spec, plano e PR próprios:

| Fatia | Entrega | Depende de |
|---|---|---|
| **A · Fundação Keycloak** (esta) | Keycloak no compose e nos testes, service account com `private_key_jwt`, porta `IIdentityProvider` com `EnsureOrganizationAsync` | — |
| **B · Consumidor** | Transporte, `ProvisionTenantHandler`, `MarkProvisioned`, `ProvisioningFailed`; a demonstração do M1 | A |
| **C · Convite do admin inicial** | Onde o e-mail vive, `EnsureInvitedUserAsync`, a tensão vaga × `Active` | A, encaixa em B |

**Critério de sucesso da fatia A:** um teste de integração, contra o Keycloak real importando o mesmo JSON de
realm que o compose usa, cria uma Organization correlacionada por `gateway_tenant_id`, a reencontra sem
duplicar sob repetição, corrida e falha transiente — e `docker compose up` sobe o conjunto inteiro, com o
`/health/ready` da API provando na CI que a chave distribuída autentica no Keycloak.

## 2. Decisões

| # | Decisão | Alternativa descartada, e por quê |
|---|---|---|
| D1 | Realm só com o **lado administrativo**: Organizations, client e service account da Gateway | Bootstrap completo da §15 (client scopes, mappers, platform-admin, JWKS na API) dobraria a fatia e entraria nas armadilhas da §12; vira fatia própria |
| D2 | Service account autenticado por **`private_key_jwt`** (§10.2) | `client_secret` agora e troca depois: desvio da spec e dívida nomeada sem ganho real |
| D3 | Correções da spec numa **nova `especificacao-arquitetural-v2.4.md`** | Editar a v2.3 no lugar quebraria a linha evolutiva; registrar só aqui deixaria a spec vigente afirmando o que é falso |
| D4 | Par de chaves gerado por um **container de init** num volume nomeado | Script manual + `.env` cria um passo antes do `up`; JWKS publicado pela API não é alcançável pelo container nos testes (API em memória) |
| D5 | Adaptador é **cliente HTTP tipado escrito à mão** (ADR-008) | Kiota: volume de código gerado; SDK da comunidade: dependência de terceiro na fronteira que o ADR-008 isola |
| D6 | Porta declara **só** `EnsureOrganizationAsync` | As outras cinco da §11.3 como `NotImplementedException` seriam contrato que mente |
| D7 | `name` da Organization = **slug**; nome de exibição em **`description`** | `name` é único no realm e o nome do tenant não é: dois tenants "Acme" levariam o segundo a `ProvisioningFailed` sem erro do usuário |
| D8 | Compose sobe **na CI** já nesta fatia | Verificação manual vale só no dia em que é feita — e é esta fatia que introduz as peças frágeis |
| D9 | Senha do admin master do Keycloak **gerada no init**, exibida uma vez | Literal num repositório público contraria o espírito da §15; `.env` obrigatório quebra "primeira tentativa" |
| D10 | Chave e realm dessincronizados **falham alto no `ready`** | Um one-shot `kcadm` de re-sincronização acrescenta serviço e falharia do mesmo jeito com a senha gerada |
| D11 | Teste funcional confere **só `live`**; `ready` é coberto por integração e pelo job de compose | Keycloak também na factory funcional custaria 30–60s por execução sem provar nada que os outros dois não provem |

## 3. Fatos verificados no código-fonte do Keycloak 26.7.4

Verificados em 2026-09-24 lendo a tag `26.7.4` (via `gh api .../contents/...?ref=26.7.4`), com
documentação oficial e release notes. Quatro deles corrigem premissas da v2.3.

| Fato | Fonte | Consequência |
|---|---|---|
| Última estável: **26.7.4** (2026-09-16); imagem `quay.io/keycloak/keycloak:26.7.4` | GitHub releases, Quay | Versão fixada |
| Organizations é `Type.DEFAULT` desde 26.0 — **`KC_FEATURES=organization` é desnecessário** | `common/.../Profile.java` (25.0.0 × 26.0.0) | Corrige §15/§16 |
| Obrigatório é **`organizationsEnabled: true`** no realm; sem ele, 404 "Organizations not enabled for this realm" | `Organizations.checkEnabled` | O smoke test continua, com o motivo certo |
| Busca por atributo é **`q=chave:valor`** (igualdade exata; `exact` não se aplica a `q`); **atributos só voltam com `briefRepresentation=false`** | `OrganizationsResource`, `SearchQueryUtils`, `JpaOrganizationProvider` | Corrige §11.6; resolve a nota "A confirmar" |
| O valor em `q` termina só em espaço: GUID com hífen dispensa aspas | `SearchQueryUtils.getFields` | URL-encode normal basta |
| `name` e `alias` duplicados → **409** com `{"errorMessage": "..."}`; 201 sem corpo, `Location` com o id | `OrganizationsResource.create`, `JpaOrganizationProvider:116,120` | Decidir pelo status, nunca pelo texto |
| **`description`** existe, é gravada e volta até com `briefRepresentation=true`; `VARCHAR(4000)`; `name` é `VARCHAR(255)` | `OrganizationRepresentation.java:34`, `jpa-changelog-25.0.0.xml` | D7 é viável |
| Papel **`manage-organizations`** (26.7.0+) cobre criar, listar e ler; `view-realm` não lê organizations | `fgap/OrganizationPermissions.java` | Menor privilégio nomeado |
| `manage-organizations` **não** compõe papel de usuário: `GET /users` → 403 | `UsersResource.getUsers`, `RealmManager.addQueryCompositeRoles` | Teste de menor privilégio viável |
| **`aud` do assertion: o issuer é aceito, e recomendado desde 26.2**; o erro é `aud` com mais de um valor | `JWTClientValidator.getExpectedAudiences`, upgrading 26.2.0 | Corrige §10.2 |
| `jti` é **obrigatório e de uso único** ("Token reuse detected") | `AbstractBaseJWTValidator.validateTokenReuse` | Nenhum retry pode reenviar o mesmo assertion |
| Idade máxima padrão do assertion: **60s** a partir do `iat`; tolerância de relógio 15s | `OIDCAdvancedConfigWrapper:398` | Vida curta é obrigação nossa, não padrão da biblioteca |
| Com `kid` no header, ele precisa bater com `Base64Url(SHA-256(SubjectPublicKeyInfo))`; **sem `kid`, usa o certificado padrão do client** | `ClientPublicKeyLoader`, `KeyUtils.createKeyId`, `PublicKeysWrapper` | Assinar sem `kid` |
| Import de realm substitui **`${ENV}`** em qualquer ponto do texto, antes do parse; variável ausente fica literal, sem erro | `AbstractFileBasedImportProvider`, `StringPropertyReplacer` | O entrypoint confere o arquivo antes (`test -s`) |
| Import é `IGNORE_EXISTING`: realm existente não é reimportado | `ExportImportManager` | Base de D10 |
| A imagem tem `sh`, `bash`, `cat` e `grep`; o `ENTRYPOINT` é o `kc.sh` | `quarkus/container/ubi-null.sh` | Entrypoint próprio em `entrypoint:`, não em `command:` |
| Health na porta de gestão **9000**, só com `KC_HEALTH_ENABLED=true` | `docs/guides/observability/health.adoc` | Healthcheck do guia oficial |
| `Testcontainers.Keycloak` 4.15.0 compatível; `.WithRealm`, `.WithEnvironment`, `GetBaseAddress()` | NuGet, tag 4.15.0 | Fixture de teste |

**Não verificado:** se o convite de membro exige SMTP e se o e-mail de convite mostra o `name` da
Organization (que agora é o slug). Ambos são da fatia C, e ficam registrados lá.

## 4. Design

### 4.1. Componentes e fronteiras

**Application** — nenhum tipo do Keycloak:

- `Common/Abstractions/IIdentityProvider.cs`:
  `Task<string> EnsureOrganizationAsync(TenantId tenantId, TenantSlug slug, string name, CancellationToken ct)`.
  Devolve o id externo. Fica em `Common/Abstractions`, junto das demais portas do repositório.
- `Common/Abstractions/IdentityProviderInconsistencyException.cs` — erro **permanente**: 409 com Organization
  que não é deste tenant, ou mais de uma Organization com o mesmo `gateway_tenant_id`. Exception e não
  `Result` porque o repositório reserva exception para falha de infraestrutura (`Result.cs:9-13`) e porque o
  retry do consumidor opera sobre exceções. **Contrato para a fatia B:** o retry precisa ignorar este tipo
  (`Ignore<IdentityProviderInconsistencyException>()` ou equivalente) — senão um erro permanente é repetido
  até esgotar, por configuração e não por decisão.

**Infrastructure** — `Identity/Keycloak/`, único namespace que conhece o Keycloak:

| Componente | Responsabilidade |
|---|---|
| `KeycloakAdminOptions` | `BaseUrl`, `Realm`, `ClientId`, `PrivateKeyPath` **ou** `PrivateKeyPem` (exatamente um). `class`, não `record` — o `ToString()` gerado de um record imprimiria a chave. Validado na subida; mensagens nunca contêm o valor. `BaseUrl` precisa ser https fora de `Development` |
| `GatewaySigningKey` | Singleton: importa a chave para `RSA` **uma vez** na subida e não guarda a string |
| `ClientAssertionFactory` | Monta o assertion. Detalhe em 4.2 |
| `ServiceAccountTokenCache` | Singleton; guarda o token até 30s antes de expirar; single-flight com `SemaphoreSlim`; relógio por `TimeProvider` |
| `ServiceAccountTokenHandler` | `DelegatingHandler`: põe o token; num 401, invalida o cache e repete **uma** vez |
| `KeycloakTokenClient` | `HttpClient` próprio para o token endpoint, **sem resiliência** (ver 4.2) |
| `KeycloakAdminClient` | Cliente tipado com dois endpoints. `CreateOrganizationAsync`: 201 → id do `Location`; 409 → `KeycloakConflictException` (interna). `FindOrganizationByAttributeAsync`: `q=gateway_tenant_id:{id}&briefRepresentation=false&max=2`; mais de um → inconsistência |
| `KeycloakIdentityProvider` | Implementa a porta (fluxo em 4.3). **Transient**, como o cliente tipado que envolve — não Scoped: não depende de `DbContext` |
| `KeycloakLogs` | `LoggerMessage`, EventIds 2200–2299 |
| `AddKeycloakIdentity` | Registro. Ordem dos handlers em 4.2 |

**Api:** health check `keycloak` com tag `ready` e `failureStatus: Unhealthy` (4.4).

**Segredos:** o `IConfiguration` **é** a abstração que a §10.2 pede — Key Vault, Secrets Manager e similares
entram como *configuration providers* sem tocar em código de aplicação. Nenhuma interface extra.
`PrivateKeyPath` é o preferido (segredo montado como arquivo); `PrivateKeyPem` existe para user-secrets e
testes, sabendo que variável de ambiente aparece em `docker inspect`.

**Nesta fatia ninguém em produção chama `EnsureOrganizationAsync`** — só os testes. Mesmo assim a porta é
registrada, e o `ValidateOnStart` passa a exigir a configuração do Keycloak em qualquer host da API.

### 4.2. Token do service account

**O assertion** (`ClientAssertionFactory`):

| Campo | Valor | Por quê |
|---|---|---|
| `iss`, `sub` | clientId | RFC 7523 |
| `aud` | issuer do realm, `{BaseUrl sem barra final}/realms/{realm}`, **string única** | Recomendado desde 26.2; array com mais de um valor é recusado |
| `jti` | GUID novo a cada assertion | O Keycloak recusa reuso |
| `iat`, `nbf`, `exp` | explícitos: agora, agora, agora + 60s, pelo `TimeProvider` | O padrão do `JsonWebTokenHandler` é **60 minutos**, não segundos — o Keycloak aceitaria mesmo assim, e um assertion vazado valeria uma hora |
| Chave | `RsaSecurityKey` **sem `KeyId`** | Com `X509SecurityKey`, o .NET põe o thumbprint SHA-1 no `kid`, que não bate com o SHA-256 que o Keycloak calcula: `invalid_client` sem explicação |
| Algoritmo | **PS256**, fixado também no client do realm (`token.endpoint.auth.signing.alg`) | Sem o atributo o Keycloak aceita qualquer assimétrico; fixar fecha a porta |

**A obtenção** (`ServiceAccountTokenCache`):

1. Token em cache com mais de 30s de vida → usado.
2. Senão, **uma** requisição busca token novo, e as demais esperam por ela (single-flight). Um assertion novo
   é gerado **a cada tentativa**, e o `KeycloakTokenClient` não tem retry: reenviar o mesmo corpo seria
   recusado por reuso de `jti`.
3. Falha na obtenção **não** fica em cache — a próxima chamada tenta de novo. O cancelamento de quem disparou
   a busca não cancela a espera dos demais.
4. O cache é singleton porque o `IHttpClientFactory` recicla handlers a cada ~2 minutos: um cache dentro do
   handler morreria junto, sem erro.

**A ordem dos handlers** no `KeycloakAdminClient`: `AddStandardResilienceHandler` **primeiro** (externo),
`ServiceAccountTokenHandler` **depois** (interno). Assim:

- cada tentativa da resiliência passa pelo token handler, que renova o token se preciso;
- o "401 → invalida e repete uma vez" acontece **dentro** de uma tentativa, e fica contido no
  `TotalTimeoutSeconds` — na ordem inversa (a da §11.8 da v2.3) o reenvio após 401 entraria na pipeline de
  resiliência do zero, e uma chamada lógica poderia durar o dobro do orçamento;
- a resiliência liga `DisableForUnsafeHttpMethods()`: retry automático só em GET. Repetir aqui é seguro até
  num POST porque 401 significa que nada foi executado.

O `KeycloakAdminClient` usa o `HttpResilienceOptions` que o template já trouxe e que hoje nada consome.

**Logs:** nunca token, assertion, chave nem corpo de requisição ou resposta (o form do token endpoint leva o
`client_assertion`); logging estendido do `HttpClient` fica desligado. O `error_description` do token
endpoint é genérico e pode ir ao log, truncado.

### 4.3. `EnsureOrganizationAsync(tenantId, slug, name)`

1. `FindOrganizationByAttribute("gateway_tenant_id", tenantId)` → achou: devolve o id. É o que torna a
   repetição da mensagem inofensiva.
2. Senão, `CreateOrganization` com `name = slug`, `alias = slug`, `description = name`, `enabled = true`,
   `attributes.gateway_tenant_id = [tenantId]` → 201: devolve o id do `Location`.
3. 409 → busca de novo pelo atributo:
   - achou: duas entregas da mesma mensagem concorreram e a outra venceu — devolve o id;
   - não achou: o conflito é com uma Organization que **não é deste tenant** →
     `IdentityProviderInconsistencyException`, permanente.

**Classes de erro que saem do adaptador** — nada é engolido; decidir entre repetir e desistir é da fatia B:

| Situação | Resultado |
|---|---|
| Keycloak fora do ar, timeout, 5xx | retry só nos GETs; esgotado → `HttpRequestException` / `TimeoutRejectedException` — **transiente** |
| 409 não nosso, ou mais de uma Organization com o atributo | `IdentityProviderInconsistencyException` — **permanente** |
| Token recusado (`invalid_client`) | `HttpRequestException` com status e `error_description` no log |
| 403 na Admin API | `HttpRequestException` 403 |
| 404 "Organizations not enabled" | erro de configuração do realm; o smoke test pega |

### 4.4. Health check

O check `keycloak` **obtém um token pelo cache** (custo zero por sonda enquanto o token vale) e responde
`Unhealthy` se não conseguir. Obter o token, e não só ler a metadata OIDC, é o que faz o `ready` provar a
distribuição da chave e o `private_key_jwt` — inclusive o caso de D10: chave e realm dessincronizados falham
alto com `invalid_client` no log, em vez de só na primeira chamada de negócio.

`failureStatus: Unhealthy` porque o `MapHealthChecks` responde **200 para `Degraded`** (`Program.cs:102` não
altera `ResultStatusCodes`): um check degradado nunca tiraria a instância do balanceador.

`SegurancaTests.OsHealthChecks_ContinuamAbertos` passa a conferir só o `/health/live` (D11). O `ready` com
Keycloak é coberto pelos testes de integração e pelo job de compose.

### 4.5. Realm e compose

**`keycloak/bootstrap/realm-identity-gateway.json`** (caminho da §7 da spec):

- realm `identity-gateway`, `organizationsEnabled: true`;
- client `identity-gateway`: confidencial, `serviceAccountsEnabled: true`, sem fluxos interativos,
  `clientAuthenticatorType: "client-jwt"`, `jwt.credential.certificate: "${GATEWAY_CLIENT_CERT}"`,
  `token.endpoint.auth.signing.alg: "PS256"`;
- usuário `service-account-identity-gateway` com `serviceAccountClientId` e
  `clientRoles.realm-management: ["manage-organizations"]` — e nada mais.

**Serviços novos e alterados no `docker-compose.yml`.** Todos os scripts são **inline** no YAML, com `$$` no
lugar de `$`: o `.editorconfig` define `end_of_line = crlf`, e um `.sh` montado por bind mount chegaria ao
container com `\r`.

| Serviço | O que faz |
|---|---|
| `gateway-keys` (alpine fixado na menor vigente, one-shot) | Se `/keys/.complete` não existe: `apk add openssl`, gera em diretório temporário um RSA 2048, um certificado autoassinado com `-days` explícito e a senha do admin master; move para o lugar e grava `.complete` por último (geração atômica). Layout e donos abaixo. Exibe a senha do admin **uma vez** no log |
| `keycloak-db` (`postgres:17-alpine`, one-shot) | `CREATE DATABASE keycloak` só se não existir — idempotente com o volume em qualquer estado. Substitui o `initdb`, que só roda com volume vazio e quebraria quem já tem `postgres-data` |
| `keycloak` (`quay.io/keycloak/keycloak:26.7.4`) | `entrypoint: ["/bin/sh","-c","test -s … && export GATEWAY_CLIENT_CERT=$$(cat …) KC_BOOTSTRAP_ADMIN_PASSWORD=$$(cat …) && exec /opt/keycloak/bin/kc.sh start-dev --import-realm"]`; `KC_DB=postgres`, banco `keycloak`; `KC_HEALTH_ENABLED=true`; `KC_BOOTSTRAP_ADMIN_USERNAME=admin`; porta **`127.0.0.1:8081:8080`**; healthcheck do guia oficial em `:9000/health/ready`, `start_period` 90s; `depends_on` `gateway-keys` e `keycloak-db` com `service_completed_successfully` |
| `api` | `Keycloak__Admin__BaseUrl=http://keycloak:8080`, `Realm`, `ClientId`, `PrivateKeyPath=/keys/private.pem`; `depends_on` `keycloak: service_healthy` e `gateway-keys: service_completed_successfully` explícitos; healthcheck real em `/health/ready` por `bash` + `/dev/tcp` (a imagem `aspnet` é Debian) no lugar do `dotnet --info`, que não verifica nada |
| `seq`, `jaeger` | Saem de `latest` para versão fixada — o job de CI passa a depender deles subirem na primeira tentativa |

**Layout do volume `gateway-keys`** — cada consumidor monta só a sua subpasta, em `:ro`:

| Caminho | Dono / modo | Quem monta |
|---|---|---|
| `api/private.pem` | `1654:1654` (usuário `app` do `Dockerfile:53`), `0400` | `api` |
| `keycloak/cert.b64` (DER em base64, uma linha) | `1000`, `0444` | `keycloak` |
| `keycloak/admin-password` | `1000`, `0400` | `keycloak` |

**`depends_on` do Keycloak na API é conveniência de desenvolvimento**, para o primeiro `curl`. A demonstração
do M1 (fatia B) exige que a API funcione com o Keycloak parado; isso fica registrado na v2.4.

**Rodar a API pela IDE** (README): subir `postgres redis keycloak` pelo compose, copiar a chave do volume para
os user-secrets com uma linha documentada, e apontar `Keycloak__Admin__BaseUrl=http://localhost:8081` — o
`aud` confere porque o issuer é calculado da URL da requisição.

**Nunca apagar só um dos dois volumes** (`gateway-keys` e o do banco do Keycloak): `docker compose down -v`
apaga os dois juntos. Se só o das chaves for apagado, o `ready` falha com `invalid_client` (D10) e o README
diz o que fazer.

### 4.6. CI

**Job novo `compose`**, em paralelo ao `test`, `needs: build`:

1. `docker compose up -d --build --wait --wait-timeout 300 api` — com o nome do serviço, o `--wait` não
   tropeça nos one-shots que já terminaram;
2. `curl -f http://localhost:8080/health/ready` — obtém token, logo prova chave, realm e `private_key_jwt`;
3. `docker compose down` (sem `-v`) e `up` de novo, repetindo o passo 2 — prova a idempotência dos one-shots;
4. `docker compose logs` com `if: failure()`.

Custo estimado: 3–5 minutos, grátis em repositório público.

## 5. Testes

🧪 = recebe **prova por mutação** antes do PR: o código é quebrado de propósito e o teste precisa ficar
vermelho. É a lição da vertical de registro — verde sem teste que exercite o caminho não é evidência.

Todos em `tests/IdentityGateway.Infrastructure.IntegrationTests`, que já enxerga os `internal`
(`InternalsVisibleTo` em `Infrastructure.csproj:43`). Pacotes novos: `Testcontainers.Keycloak` 4.15.0 e
`Microsoft.Extensions.TimeProvider.Testing`.

### 5.1. Sem container

- 🧪 **Assertion:** `iss` = `sub` = clientId; `aud` é **string JSON** (não array) igual a um issuer **literal
  fixo** escrito no teste — não recalculado pela fórmula do código —, inclusive com `BaseUrl` terminando em
  `/`; `jti` difere entre duas chamadas; `exp - iat == 60s` pelo `FakeTimeProvider`; header **sem `kid`**;
  `alg` = PS256; a assinatura confere com o certificado público.
- 🧪 **Single-flight:** o token endpoint falso segura a resposta num `TaskCompletionSource` até as 20
  chamadas entrarem; resultado: **1** requisição de token. Mutação: remover a trava.
- 🧪 **Borda do cache:** com `FakeTimeProvider`, 31s restantes → reusa; 29s → busca de novo. Mutação: 30 → 0.
- 🧪 **Falha não fica em cache:** primeira obtenção falha, a segunda chamada tenta de novo e obtém.
- **Cancelamento:** cancelar a chamada que disparou a busca não cancela as que esperam.
- 🧪 **Assertion novo por tentativa:** duas tentativas de token geram `jti` diferentes.
- 🧪 **401:** invalida e repete uma vez; segundo 401 sobe como erro, sem laço.
- 🧪 **`KeycloakIdentityProvider` com HTTP falso:** achou → nenhum POST; 409 + achou → id; 409 + vazio →
  inconsistência; mais de um → inconsistência.
- **`ValidateOnStart`** (em `DependencyInjectionTests`): seção ausente; nenhuma chave; as duas chaves;
  arquivo inexistente; `BaseUrl` http fora de `Development`. `IIdentityProvider` entra no `[Theory]` de
  resolubilidade, e a `ConfiguracaoValida` ganha valores do Keycloak.

### 5.2. Contra o Keycloak real

Um container para o assembly inteiro (`[assembly: AssemblyFixture(typeof(KeycloakFixture))]`), imagem
26.7.4, importando **o mesmo** `keycloak/bootstrap/realm-identity-gateway.json` do compose. A fixture gera o
par de chaves em memória e passa o certificado por `.WithEnvironment("GATEWAY_CLIENT_CERT", …)` — nenhum
`.pem` de teste é versionado (o secret scanning de repositório público dispararia). Isolamento entre testes
por **slug único** (`$"x-{Guid.NewGuid():N}"[..18]`, padrão já usado no repositório); nenhum teste afirma
sobre a contagem global de Organizations. Leituras de verificação são feitas pelo **admin master em JSON
cru**, nunca pelo DTO do próprio adaptador.

- 🧪 **Token:** obtido por `private_key_jwt` com `aud` = issuer; assertion assinado com **outra** chave →
  `invalid_client` (prova que o primeiro não passa por acaso).
- 🧪 **Criação:** a releitura crua mostra `name` = slug, `description` e o literal `gateway_tenant_id`.
- 🧪 **Busca com distrator:** Organizations dos tenants X e Y; `Find(Y)` devolve só Y. Pega o `q` ignorado,
  que devolveria todas.
- 🧪 **Idempotência:** duas chamadas em sequência → mesmo id, uma Organization.
- 🧪 **Corrida determinística:** uma barreira no GET só libera quando as duas chamadas chegaram; afirma o
  mesmo id, uma Organization, e que **um POST recebeu 409**. Mutação: remover a reconsulta pós-409.
- 🧪 **Conflito com Organization de fora:** o master cria uma com o mesmo alias e sem o atributo →
  inconsistência.
- **Inconsistência real:** o master cria duas com o mesmo `gateway_tenant_id` → inconsistência.
- 🧪 **Sem duplicação sob falha transiente (§13):** um handler que **conta** e injeta falha, pendurado
  **dentro** da resiliência na composição real (`AddKeycloakIdentity` + `.AddHttpMessageHandler` no teste).
  Primeiro POST: deixa chegar ao Keycloak e lança `HttpRequestException` → afirma **POST == 1** e a exceção
  transiente propagada. Controle positivo: GET com 503 injetado → **GET > 1** (sem ele, "resiliência não
  registrada" também passaria). Mutação: remover `DisableForUnsafeHttpMethods()`.
- 🧪 **401 real:** o cache é semeado com token lixo e uma Organization é criada → sucesso, uma Organization,
  **2** requisições de token. Prova que o corpo do POST é reenviado.
- 🧪 **Menor privilégio:** o service account recebe 403 em `GET /users`, e o master confirma que os papéis
  dele são **exatamente** `{manage-organizations}`.
- **Smoke:** o endpoint de organizations não responde 404.
- 🧪 **Health check:** `ready` 200 com o Keycloak de pé; **503** com `BaseUrl` inválida.

### 5.3. Arquitetura e CI

- 🧪 Tipos de `Identity.Keycloak` não são usados fora desse namespace — e o teste afirma que o namespace tem
  ao menos um tipo, senão a regra passaria vazia.
- 🧪 **Realm sem credencial literal:** parse do JSON e percurso da árvore; todo valor que se pareça com
  credencial precisa casar `^\$\{[A-Z0-9_]+\}$` (placeholder **sem** default); asserções estruturais: nenhum
  `credentials`, nenhum `secret`, nenhum `secretData`, nenhum `components` de KeyProvider, nenhum
  `smtpServer.password`, `clientSecret` ou `bindCredential`. Mutações: inserir `credentials`,
  `${GATEWAY_CLIENT_CERT:MIIC…}` e `smtpServer.password`.
- Job `compose` (4.6).

## 6. Onde esta fatia cai no roadmap

A §16 põe container, bootstrap do realm, health checks e smoke test no **M0**, e porta, adaptador e correlação
por atributo no **M1**. A fatia A é **mista**. Do M0 continuam pendentes depois dela, e a v2.4 os lista:
client scopes `gateway-roles` e `gateway-tenant`, Audience Mapper, catálogo de papéis, eventos do realm,
remoção do `offline_access`, platform-admin com senha gerada, a API validando tokens do Keycloak (o critério
"primeiro `curl`" do M0 com o Keycloak segue em aberto enquanto a API usar JWT simétrico) e o RabbitMQ.

## 7. Dívidas e questões abertas registradas

- **`initialAdminEmail` descartado.** A fatia A corrige o comentário falso de `RegisterTenantCommand.cs` e o
  próximo handoff leva uma errata do anterior, sem reescrever o histórico. **Onde o e-mail vive é questão
  aberta da fatia C** e entra na v2.4: pô-lo no evento o levaria ao Outbox e ao RabbitMQ, contra a regra de
  dados pessoais só no Keycloak (§6, §10.3).
- **Tensão vaga × `Active`** (fatia C): `Tenant.ReserveSeat()` exige `Active` e a invariante da §6.1 diz que
  tenant fora de `Active` não aceita membros, mas a §9.1 convida **antes** de ativar.
- **Raio de dano do `manage-organizations`:** permite alterar e apagar **qualquer** Organization, inclusive
  reescrever `gateway_tenant_id` e domínios. Não escala para usuários (adicionar membro exige também
  `manage-users`) nem para IdPs (`manage-identity-providers`). Registrado na v2.4; a fatia C reavalia, porque
  o convite faz o papel crescer.
- **Rotação de chave:** com certificado no atributo do client, rotacionar causa indisponibilidade. Limite
  conhecido (§19 da v2.4); a evolução é `use.jwks.url` com as duas chaves publicadas durante a transição.
- **Produção:** `start-dev`, `http://` e chave em volume são de desenvolvimento. Em produção o `BaseUrl`
  precisa ser igual ao `KC_HOSTNAME` (o issuer é calculado da URL), a chave vem do cofre como arquivo
  montado, e o `ValidateOnStart` recusa `BaseUrl` sem https fora de `Development`. O cabeçalho do compose diz
  isso.
- Da vertical anterior, seguem abertas: o `500` em vez de `409` na corrida de slug e os três itens adiáveis
  do handoff de 2026-09-22.

## 8. Fora do escopo

Consumidor e transporte (fatia B); convite e `EnsureInvitedUserAsync` (fatia C); lado de tokens do realm e a
API validando tokens do Keycloak (fatia própria); RabbitMQ; reconciliação; as demais operações da
`IIdentityProvider`.

## 9. Entregáveis

- `docs/especificacao-arquitetural-v2.4.md`, com a seção "O que mudou da v2.3 para a v2.4";
- `docs/documentacao-negocio.md` alinhado à v2.4 nos pontos que repetiam as premissas corrigidas;
- código, testes, realm, compose, CI e README conforme as seções 4 e 5 — detalhados no plano;
- correção do comentário de `RegisterTenantCommand.cs`.
