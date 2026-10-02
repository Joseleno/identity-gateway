# IdentityGateway — Especificação Arquitetural

> Multi-Tenant Identity & Governance API em .NET 10 com Keycloak
> **Versão 2.7** · Status: aprovada para implementação

---

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

- **E1** (§15): a frase "o JSON escapa o `&` como `&`" tinha perdido a sequência de escape que cita. O certo é "o JSON escapa o `&` como `\u0026`".
- **E2** (§12.1): "o ROPC continua desabilitado também no realm de teste" era falso. O fixture de testes fazia ROPC num client criado em runtime, e o `admin-cli` embutido do realm tem direct grant. O fixture deixou de usar ROPC, e o ADR-003 passa a dizer o alcance da proibição.
- **E3** (§10.1, §11.7): o `PlatformAdminOverrideHandler` que "satisfaz o `SameTenantRequirement`" nunca funcionaria, porque o `Fail()` do requirement veta qualquer `Succeed`. O override vira policy própria (T14), e o "segundo handler" deixa de ser o motivo do `Fail()`.
- **E4** (§11.7): o `RegisterTenantCommand` do endpoint de referência estava sem o e-mail, desatualizado desde a v2.6.
- **E5** (§12.1): "a lista substitui o conjunto padrão do realm" estava errado. Declarar `clientScopes` no JSON desliga a criação dos scopes embutidos, e a correção é o atributo `CreateDefaultClientScopes`.
- **E6** (§12.1): os papéis padrão do realm "não interferem" — eles entravam no claim `roles`. Agora saem.
- **E7** (§9.5, §15): o bootstrap era descrito com os client scopes, o Audience Mapper, o armazenamento de eventos e a remoção do `offline_access`, que o JSON não tinha. Os scopes, o mapper e a remoção entram nesta versão; o armazenamento de eventos continua pendente.
- **E8** (§12.1, §12.2): os testes de referência chamavam métodos que não existem (`GetClientCredentialsTokenAsync`, `GetTokenForUserAsync`) e usavam a rota `/tenants/tenant-a/members`, com um slug no lugar do GUID.

---

## 0.1. O que mudou da v2.5 para a v2.6

Esta versão registra o que a **fatia C (convite do admin inicial)** decidiu e verificou, lendo o código-fonte do
Keycloak na tag **26.7.4**. Design da fatia:
[`2026-09-29-convite-admin-inicial-design.md`](superpowers/specs/2026-09-29-convite-admin-inicial-design.md).
**Nenhum ADR foi revogado.** As duas erratas corrigem a §9.9 e a §10.2, não ADRs, e a exceção do e-mail do admin
inicial é à regra de dados pessoais da §6 e da §10.3.

| # | Mudança | Onde |
|---|---|---|
| C1 | **O e-mail do admin inicial fica temporariamente no tenant** (`InitialAdminEmail`, coluna anulável), fora do evento `TenantRegistered`, e é apagado na transação que ativa o tenant ou que o marca `ProvisioningFailed`. É a exceção declarada e limitada à regra de dados pessoais só no Keycloak: só existe em tenant `Pending` | §5, §6, §6.1, §10.3 |
| C2 | **Ativar e reservar a vaga numa operação de domínio só, num commit só.** `Tenant.CompleteProvisioning` substitui `MarkProvisioned`: ativa, reserva a vaga do admin, cria o `Member` em `Invited` e apaga o e-mail. No Keycloak, o convite acontece antes | §6.1, §9.1, §11.1 |
| C3 | **O convite é feito pela Admin API**, em cinco passos idempotentes: buscar, criar, vincular à Organization, atribuir o papel e enviar `execute-actions-email`. O `invite-user` de Organization não cria usuário para e-mail novo, e o `Member` nasceria sem `sub` | §9.1, §11.6 |
| C4 | **Errata: o convidado nasce habilitado**, com `UPDATE_PASSWORD` e `VERIFY_EMAIL`. A §9.9 dizia que ele nascia desabilitado, e o Keycloak recusa o envio e o clique de usuário desabilitado. A expiração passa a desabilitar o usuário, e o cancelamento desabilita **e** revoga sessões. No primeiro acesso, o convidado informa nome e sobrenome (`VERIFY_PROFILE`) | §9.9 |
| C5 | **O usuário é correlacionado pelo atributo `tenant_id`**, o mesmo que alimenta o claim, declarado no User Profile só para `admin` e sem `unmanagedAttributePolicy`. E-mail em uso por outra conta é falha permanente | §9.1, §12.2, §15 |
| C6 | **O service account recebe `manage-users`**, ao lado de `manage-organizations`: vincular usuário a uma Organization exige as duas. O alcance real fica nomeado, e um teste compara os papéis **efetivos** | §10.2, §19 |
| C7 | **Errata: o `aud` do assertion é o emissor público, e o transporte pode ser interno.** `KC_HOSTNAME` no Keycloak e `Keycloak:Admin:PublicBaseUrl` na Gateway, usado só no `aud`. A regra da v2.5 "`BaseUrl` igual ao `KC_HOSTNAME`" cai. `AllowInsecureHttp` só em Development | §10.2, §15 |
| C8 | **O prazo do link vem da política de convite** (`IInvitationPolicy.LinkLifetime`, `Invitations:LinkLifetime`, padrão 7 dias, no máximo 30), passado ao Keycloak em cada envio. Global nesta versão; por tenant no M2, pela mesma porta | §9.9, §11.3 |
| C9 | **O papel vai na porta:** `EnsureInvitedUserAsync(organizationId, tenantId, InviteData, ct)`, com o `RoleName` no `InviteData` | §11.3 |
| C10 | **O e-mail só é enviado se o convite ainda não foi aceito** (`UPDATE_PASSWORD` pendente): um reenvio depois do aceite trocaria a senha do admin ativo | §9.9, §11.6 |
| C11 | **Sem e-mail ou sem vaga, o provisionamento falha antes de tocar o Keycloak.** `MarkProvisioningFailed` também apaga o e-mail | §11.5 |
| C12 | **O e-mail não vaza** por log, exceção, resposta nem coluna de erro, inclusive em Development | §10.3, §13 |
| C13 | **`Member` mínimo entregue**, com `IMemberRepository` só com `Add` nesta versão. O papel continua no Keycloak, e o admin inicial nasce sem `MemberInvited` | §6.1, §6.3, §11.9 |
| C14 | **E-mail pelo SMTP do Keycloak**, com `mailpit` em desenvolvimento e na CI; o notification-hub fica para depois. `resetPasswordAllowed: false` e eventos de administração ligados no realm | §10.3, §15, §19 |
| C15 | **Compose:** `mailpit` (que a v2.5 listava sem ele existir), SMTP por ambiente, Postgres, Redis e Seq publicados só em `127.0.0.1`; o job da CI confere o e-mail e abre o link. Volume anterior exige `docker compose down -v` | §15 |
| C16 | Fatia C entregue no andamento; o catálogo de papéis ganha o `tenant-admin`. Nenhuma rota nova | §8, §16 |
| C17 | **Duas corridas de entregas concorrentes resolvidas no adaptador**, achadas pelo teste de entrega concorrente contra o Keycloak real. Papel ausente dos disponíveis: **uma** releitura dos atribuídos antes de concluir ausência — sem ela, uma corrida benigna terminava em `ProvisioningFailed`. `400` no vínculo à Organization: **uma** leitura da pertença (`GET /organizations/{id}/members/{userId}`) antes de propagar. Nunca em laço. A corrida do `POST` de papel continua transitória (§19) | §11.6, §19 |
| C18 | **A ordem real do lote do commit do provisionamento** é `INSERT members` → `INSERT outbox_messages` → `UPDATE tenants … WHERE xmin`. A atomicidade se prova fazendo falhar o **último** comando (o `xmin` do tenant muda durante o convite); uma linha pré-inserida em `members` derruba o **primeiro** | §13 |

---

## 0.2. O que mudou da v2.4 para a v2.5

Esta versão registra o que a **fatia B (consumidor do provisionamento)** decidiu e verificou. Design da fatia:
[`2026-09-25-consumidor-provisionamento-design.md`](superpowers/specs/2026-09-25-consumidor-provisionamento-design.md).
**Nenhum ADR foi revogado.**

| # | Mudança | Onde |
|---|---|---|
| B1 | **Transporte em processo até a fatia do RabbitMQ.** O Outbox é o transporte e o `DispatchingOutboxPublisher` despacha cada evento como command do Mediator, num escopo de DI próprio por mensagem | ADR-006, §11.5 |
| B2 | **A §11.5 deixa de assumir MassTransit.** Verificado em 2026-09-25: o MassTransit v9 é comercial, e o v8 (Apache 2.0) tem suporte até o fim de 2026. A escolha da biblioteca fica para a fatia do broker | §11.5 |
| B3 | **A decisão de desistir é do handler**, contada desde `Tenant.RegisteredAt` contra `Provisioning:MaxPendingHours` (24h). O filtro de cancelamento olha o `CancellationToken`, não o tipo da exceção | §9.1, §11.5 |
| B4 | **O handler só age em `Pending`.** Mensagem repetida não reprovisiona um `ProvisioningFailed`; a saída de `Failed` é o retry manual, via `Pending` | §6.2, §11.5 |
| B5 | **A rede do Outbox é validada contra a janela na subida.** Padrões novos: teto de 60s e 1500 tentativas (~25h) | §9.1, §19 |
| B6 | **`OccurredOn` dos eventos precisa voltar do JSON** (`init`). Com só `get`, o evento relido carregava o instante da desserialização | §14.1 |
| B7 | `GET /tenants/{tenantId}/provisioning` entregue: `tenantId`, `status`, `registeredAt` | §8, §16 |
| B8 | **O compose aplica as migrations por um one-shot `migrate`** (a imagem da API com `--migrate`), e a API só sobe depois dele. Achado da fatia B: `docker compose up` num volume novo deixava a API `ready` e o primeiro `POST /tenants` em 500, e o job de compose da CI não via. O job agora registra um tenant e espera `Active` | §15 |

---

## 0.3. O que mudou da v2.3 para a v2.4

Esta versão corrige o que a v2.3 afirmava sobre o Keycloak e que a **fatia A (fundação Keycloak)** verificou
ser falso ou desatualizado. A verificação foi feita em 2026-09-24 lendo o código-fonte do Keycloak na tag
**26.7.4**, a estável corrente, e passou por revisão de seis especialistas (arquitetura, segurança, Keycloak,
testes, DevOps e coerência documental). Design da fatia:
[`2026-09-24-fundacao-keycloak-design.md`](superpowers/specs/2026-09-24-fundacao-keycloak-design.md).
**Nenhum ADR foi revogado.**

**Premissas sobre o Keycloak que estavam erradas:**

| # | A v2.3 dizia | O que é verdade | Onde |
|---|---|---|---|
| K1 | `KC_FEATURES=organization` é obrigatório, senão 404 silencioso | Organizations é padrão desde a 26.0; o obrigatório é `organizationsEnabled: true` no realm | §15, §16 |
| K2 | Busca por atributo via `searchQuery=...&exact=true` | O parâmetro é `q`; `exact` não se aplica; atributos só voltam com `briefRepresentation=false`. O "a confirmar" da §11.6 está resolvido | §11.6, ADR-006 |
| K3 | `aud` do assertion = token endpoint; o issuer daria `invalid_client` | O issuer é aceito, e recomendado desde a 26.2; o erro é `aud` com mais de um valor | §10.2 |
| K4 | Papéis mínimos de `realm-management`, sem nome | `manage-organizations` (26.7+), com o raio de dano documentado | §10.2 |
| K5 | Vida do assertion "preenchida pelo `JsonWebTokenHandler`; ≤ 60s" | O padrão da biblioteca é **60 minutos**; os tempos passam a ser explícitos | §10.2 |
| K6 | — (o `kid` não era mencionado) | O `kid` que o .NET emite com `X509SecurityKey` não bate com o do Keycloak; assinar **sem `kid`** | §10.2 |
| K7 | — (retry do token não era mencionado) | O `jti` é de uso único: o token endpoint não tem retry, e cada tentativa gera assertion novo | §10.2, §11.8 |

**Inconsistências internas e decisões novas:**

| # | Mudança | Onde |
|---|---|---|
| I1 | Assinatura da porta: `EnsureOrganizationAsync(TenantId, TenantSlug, string name, ct)` — a §11.3 e a chamada da §11.5 omitiam o `TenantId`, que o atributo exige | §11.3, §11.5 |
| I2 | `Name` da Organization = **slug**, nome de exibição em `Description`: `name` é único no realm, e o nome do tenant não é | §11.6 |
| I3 | Exceção permanente nomeada: `IdentityProviderInconsistencyException`, na Application; o retry do consumidor a ignora | §11.5, §11.6, §9.1 |
| I4 | Portas em `Application/Common/Abstractions`, convenção do CleanStart; árvore de testes alinhada aos projetos reais | §7, §11.3 |
| I5 | A abstração de segredos **é** o `IConfiguration`; a chave chega como arquivo montado | §10.2 |
| I6 | Chave pública registrada como **certificado**; JWKS fica como evolução para rotação sem downtime | §10.2, §19 |
| I7 | Ordem dos handlers: resiliência por fora, token por dentro; adaptador `Transient` | §11.8 |
| I8 | Health check `ready` do Keycloak **obtém um token** e usa `failureStatus: Unhealthy` | §14 |
| I9 | Teste de não duplicação conta POSTs dentro da resiliência, com controle positivo | §13 |
| I10 | Compose: `gateway-keys` e `keycloak-db` one-shot, senha do admin master gerada, porta só em `127.0.0.1`, **compose verificado na CI**; banco `identitygateway` | §15 |
| I11 | Andamento por fatias e o que do M0 segue pendente | §16 |
| I12 | Limites novos: rotação de chave e `depends_on` como conveniência local | §19 |

**Pendências de decisão registradas** (a fechar antes da fatia C, na §9.1): onde o `initialAdminEmail` vive
entre o `POST` e o convite — hoje ele é validado e **descartado**, ao contrário do que o handoff de
2026-09-22 afirmava —, e a tensão entre reservar vaga no convite e exigir o tenant `Active`.

---

## 0.4. O que mudou da v2.2 para a v2.3

Esta versão fecha as **pendências de arquitetura** que o handoff mantinha em aberto e reconcilia a
spec com o template [CleanStart](https://github.com/Joseleno/CleanStart), verificado em 2026-09-20.
**Nenhum ADR foi revogado**; um novo foi acrescentado (ADR-010).

**Reconciliação com o CleanStart** — o template é a base de todos os projetos do time, e divergir
dele no primeiro uso criaria dívida multiplicada por projeto:

| # | Mudança |
|---|---|
| T1 | Endpoints passam a usar **Carter** (módulos de minimal API), como no template — §11.7, §7 |
| T2 | `docker-compose.yml` **une** os serviços da §15 e os do template — §15 |
| T3 | **RabbitMQ** confirmado como transporte do Outbox do CleanStart (interceptor + tabela + worker) — ADR-006 |
| T4 | **Redis + HybridCache** (L1 em memória + L2 Redis) para o cache de permissões — §9.6, §12 |

**Lacunas de arquitetura, agora fechadas:**

| # | Mudança |
|---|---|
| C14 | **Leader election** por `pg_try_advisory_lock` em todo job de fundo, mais unique constraint como rede final — **ADR-010 novo**, §14 |
| — | **Contrato de eventos de integração**: envelope CloudEvents 1.0 e versão no tipo — §14.1 nova |
| — | **Versionamento de API**: path mantido, com política de depreciação por `Deprecation`/`Sunset` (RFC 8594) — §8 |
| — | **Secrets**: User Secrets no local, variáveis de ambiente nos demais, leitura abstraída — §10.2 |
| — | **Warm-up do `Client.AspNetCore`**: health check `ready` só após resolver permissões uma vez — §9.6, §12 |

---

## 0.5. O que mudou da v2.1 para a v2.2

Esta versão fecha as lacunas encontradas na produção da documentação de negócio
(`documentacao-negocio.md`) e incorpora dois achados da revisão que a v2.1 não havia absorvido.
**Nenhum ADR foi revogado.**

| Origem | Mudança |
|---|---|
| L-1 | Suspensão e encerramento passam a **desabilitar também os clients OIDC** do tenant — §9.7, §9.8, §6.1 |
| L-2 | **Ciclo de vida do convite** especificado: expiração por job, prazo do plano (padrão 7 dias), reenvio e cancelamento — §9.9 nova |
| L-3 | Suspensão em massa é **idempotente e retomável**, com a marca gravada por membro — §9.7 |
| I-1 | `Terminating` passa a constar do diagrama da máquina de estados — §6.2 |
| I-2 | Cancelamento de convite leva ao novo estado **`Revoked`**, distinto de `Expired` e `Erased` — §6.1, §9.9 |
| I-3 | Override do `platform-admin` **restrito à leitura de tenant** e auditado — §10.1 |
| N8 | **Downgrade de plano** abaixo das vagas ocupadas é rejeitado com `409` — §6.1, §8 |
| — | **`MaxClients` passa a ser aplicado** no registro de client — §6.1, §8 |
| A9 | Mecanismo .NET do **client assertion `private_key_jwt`** especificado — §10.2 |
| — | `DELETE` de tenant em `Suspending` responde `409`, como a reativação — §9.7 |
| — | Cancelamento de convite **não revoga sessões**: não há nenhuma, o usuário nunca autenticou — §9.9 |

---

## 0.6. O que mudou da v2.0 para a v2.1

Esta versão incorpora a revisão crítica de `revisao-critica.md` (três frentes independentes: negócio,
arquitetura e adversarial) e as sete decisões tomadas no brainstorm que a seguiu. **Nenhum ADR foi
revogado** — todas as correções couberam dentro do desenho original.

**Correções que mudam desenho:**

| Origem | Mudança |
|---|---|
| C1 | `tenant_id` deixa de vir do mapper de organização (que emite objeto aninhado) e passa a vir de um **User Attribute mapper plano** — §12.2 nova |
| C2 | Todo carregamento de sub-recurso filtra por tenant no repositório; sem sobrecarga só por id — §6.4, §11.9 |
| C3 | `FindOrganizationByAliasAsync` substituído por busca via atributo `gateway_tenant_id` com `searchQuery` — §11.6 |
| C9 | `POST /tenants` passa a exigir `initialAdminEmail`; o tenant nasce com um `tenant-admin` — §8, §9.1 |
| C11 | `SameTenantHandler` chama `context.Fail()` em todo caminho de rejeição — §11.7 |
| C12 | Teste de startup exige `{tenantId}` em toda rota com policy de tenant — §13 |
| CI-1 | Escopo M2M `gateway.permissions.read` ganha modelo de confiança próprio — §10.1 |
| CI-4 | Step-up ganha `StepUpRequirement` e contrato de resposta — §5, §10.1 |
| CI-8 | Critério de pronto cobre **toda regra** de isolamento, não só rotas com `{tenantId}` — §18 |

**Decisões do brainstorm incorporadas:**

1. `initialAdminEmail` obrigatório no registro de tenant (C9)
2. `Invited` **ocupa vaga** — e por isso a expiração de convite passa a ser obrigatória
3. Suspensão de tenant **desabilita os usuários no Keycloak** e revoga sessões
4. Novo estado terminal `Terminated`, **sem remoção física** da Organization
5. Federação (M4) permanece no escopo, após o M3
6. Escopo completo M0–M7 (projeto sem prazo)
7. Demonstração por **README com `curl` reproduzível** — o que promove o M0 a peça crítica

**Correções de redação com consequência:** CI-2 (credencial fora do store de idempotência), CI-3 (SPOF
deslocado para o Keycloak, janela de 5 min), CI-5 (invariante de vagas reescrita), CI-6 (reconciliação
varre todos os estados), CI-7 (escopo do ADR-004), C6 (503 em vez de 403), C8 (`offline_access` removido
do `default-roles`).

**Escopo confirmado — modelo plano.** O `tenant-admin` administra, dentro do próprio tenant, membros,
clients OIDC (aplicações: SPA, mobile, M2M), permission sets, domínios e IdPs. Não há sub-tenancy: um
tenant não possui "clientes de negócio" abaixo dele. Isso contraria o ADR-009 e desmontaria o
`SameTenantRequirement`, cuja comparação é de igualdade.

---

## 1. Sobre este projeto

O **IdentityGateway** é um projeto de portfólio que mostra como construir, em .NET 10, uma API de governança de identidade multi-tenant sobre o Keycloak. É um projeto **API-first**: não existe front-end neste repositório, e toda interação acontece por endpoints REST documentados em OpenAPI.

A API atua como **camada de abstração e orquestração de governança corporativa**. Aplicações clientes (Web, Mobile e serviços M2M) usam a API para descobrir como autenticar seus usuários, gerenciar tenants, usuários, papéis e credenciais de máquina, e consultar permissões. A governança é **centralizada na definição** e **desacoplada na execução**: a Gateway é a fonte da verdade sobre quem pode o quê, mas nenhuma requisição de negócio depende dela para ser autorizada.

O projeto quer demonstrar, com código verificável, quatro competências: integração correta com OAuth 2.0 e OpenID Connect sem atalhos inseguros; Clean Architecture com DDD e CQRS aplicados a um domínio real; consistência entre dois sistemas sem transação distribuída; e isolamento multi-tenant garantido por testes automatizados.

O repositório parte do template [CleanStart](https://github.com/Joseleno/CleanStart), herdando sua estrutura de camadas e convenções.

**Como o template é consumido.** O CleanStart não é um `dotnet new` template: clona-se o repositório, renomeia-se a solution e os namespaces (`CleanStart` → `IdentityGateway`) e **apaga-se a feature `Orders`**, que é referência e não fundação. Preservam-se `Domain/Common`, `Application/Common`, os interceptors e os `ArchitectureTests` — é o que sustenta o resto. O template já traz .NET 10, EF Core 10, Mediator e Mapperly (source-generated), FluentValidation, Serilog, OpenTelemetry, `HybridCache` com Redis, xUnit v3 e o Outbox por interceptor, o que encurta o M0 de forma significativa.

### 1.1. Escopo

| Dentro do escopo | Fora do escopo |
|---|---|
| API REST de governança (tenants, usuários, papéis, permissões, clients M2M) | Qualquer front-end, painel administrativo ou SPA |
| Orquestração do login: descoberta de tenant e IdP a partir do e-mail | Telas de login (são do Keycloak; customização de tema é opcional) |
| Provisionamento de tenants e usuários no Keycloak | Emissão ou reassinatura de tokens pela Gateway |
| Federação por tenant (IdPs externos OIDC/SAML) | Armazenamento de senhas, hashes ou segredos de usuários |
| Biblioteca para as APIs consumidoras validarem tokens e permissões | Cobrança real (o plano é apenas metadata de governança) |
| API de exemplo (`SampleResourceApi`) demonstrando o Data Plane | Alta disponibilidade do Keycloak (documentada, não implementada) |
| Ambiente local completo via Docker Compose | Deploy em nuvem (opcional, fora do núcleo) |

---

## 2. Como a API atende a "autenticar, gerenciar e validar"

Os três verbos do objetivo do projeto têm significados precisos nesta arquitetura. Deixá-los explícitos evita a armadilha mais comum desse tipo de sistema: transformar a Gateway num intermediário obrigatório de cada login e de cada requisição.

**Autenticar é orquestrar, não intermediar.** Credenciais de usuário são digitadas exclusivamente no Keycloak, e os tokens são emitidos pelo Keycloak diretamente para a aplicação cliente. A Gateway contribui antes e em volta desse fluxo: resolve o tenant e o provedor de identidade a partir do e-mail (*discovery*), registra as aplicações clientes como clients OIDC e provisiona as credenciais M2M. Ela nunca vê uma senha e nunca está no caminho do token.

**Gerenciar é o núcleo da API.** Tenants, usuários, papéis, conjuntos de permissões, domínios, IdPs federados e clients M2M são recursos REST da Gateway. Ela é o *Control Plane* do ecossistema.

**Validar permissões acontece em dois níveis.**

- *Nível 1, autenticação e papéis globais:* cada API consumidora valida o JWT localmente com as chaves públicas do Keycloak (JWKS). Não há chamada à Gateway.
- *Nível 2, permissões finas por tenant:* a Gateway é a fonte da verdade dos conjuntos de permissões de cada tenant. A API consumidora obtém as permissões efetivas de um par (usuário, tenant) uma vez, guarda em cache e invalida quando recebe o evento `PermissionsChanged`. Também aqui não há chamada por requisição.

### 2.1. Visão geral

```
┌───────────────────────────────────────────────────────────────────────┐
│               Aplicações Clientes (Web / Mobile / M2M)                │
└──────┬──────────────────────────┬──────────────────────────┬──────────┘
       │ (1) Discovery e gestão   │ (2) Login OIDC           │ (3) Negócio
       │     REST + Bearer JWT    │     PKCE / Client Cred.¹ │     Bearer JWT
       ▼                          ▼                          ▼
┌──────────────────┐     ┌──────────────────┐     ┌──────────────────────┐
│ IdentityGateway  │     │     Keycloak     │     │    Resource APIs     │
│  (Control Plane) │────►│  IdP + AuthZ     │◄────│    (Data Plane)      │
│                  │ (4) │     Server       │ (5) │                      │
└────────┬─────────┘     └──────────────────┘     └──────────┬───────────┘
         │                                                   │
         │    PostgreSQL (governança) · RabbitMQ (eventos)   │
         │                                                   │
         └◄─────────────────────────(6)──────────────────────┘
```

| Seta | O que trafega |
|---|---|
| (1) | Chamadas de gestão e de descoberta, autenticadas com JWT emitido pelo Keycloak |
| (2) | Authorization Code + PKCE (interativo) ou Client Credentials (M2M), direto no Keycloak. ¹ Só no ambiente local, o client de demonstração `identity-gateway-demo` usa o Device Authorization Grant (RFC 8628), também direto no Keycloak (v2.7, ADR-003) |
| (3) | Requisições de negócio com `access_token`; a Gateway não participa |
| (4) | Admin REST API (provisionamento) e leitura de eventos do Keycloak |
| (5) | Metadata OIDC e JWKS, com cache local na API consumidora |
| (6) | Permissões efetivas por tenant, com cache e invalidação por evento; nunca por requisição |

A regra que resume o desenho: **a Gateway nunca está nos caminhos (2) e (3)**. Se ela cair, logins continuam funcionando, e as requisições de negócio **que dependem apenas de papéis globais** (nível 1) também. Permissões finas (nível 2) dependem de cache quente: com o cache frio e a Gateway indisponível, a decisão é negar — ver §9.6 e §19.

**O ponto único de falha não foi eliminado, foi deslocado.** Com o Keycloak fora do ar, nenhum login acontece e nenhum token é renovado; decorridos os 5 minutos de vida do access token, o Data Plane inteiro para, porque a validação local depende de token vivo, não de Keycloak vivo. O ADR-002 remove a Gateway do caminho crítico; ele não torna o sistema resiliente à queda do Keycloak, que permanece dependência crítica de ambos os caminhos.

---

## 3. Princípios

1. **API-first e stateless.** Todo recurso de gestão é um endpoint REST versionado e documentado. A API não mantém sessão; cada requisição carrega seu próprio token.
2. **Control Plane separado do Data Plane.** Gestão centralizada, validação distribuída.
3. **Keycloak como único dono de credenciais e único emissor de tokens.** A Gateway não armazena, transporta nem reassina nada disso.
4. **Fail closed.** Na dúvida (claim ausente, tenant divergente, dependência indisponível em operação sensível), a resposta é negar. *Exceção declarada:* na absorção de usuários criados fora da Gateway (§9.4), registrar e sinalizar é preferível a perder o vínculo — negar ali criaria um usuário autenticado sem membership.
5. **Isolamento multi-tenant verificável.** Toda **regra** de isolamento tem um teste negativo correspondente — não apenas as rotas com `{tenantId}` no template. As regras são três: tenant da rota × tenant do token; pertencimento de cada sub-recurso ao tenant da rota; e escopo de client M2M (§10.1, §18). **Continuam três na v2.7:** nas rotas de governança da Gateway, a primeira é reforçada pela pertença do ator ao tenant no banco (ADR-011), que não é uma quarta regra — é a mesma regra, conferida em duas fontes, o token e o banco (D2, planejado).
6. **Tudo como código.** Configuração do Keycloak, infraestrutura local e decisões arquiteturais versionadas no repositório.

---

## 4. Decisões arquiteturais (ADRs)

Resumo das decisões. O texto completo de cada uma fica em `docs/adr/`.

### ADR-001 — Tenancy com Organizations em realm único

- **Contexto:** o Keycloak oferece dois modelos de isolamento: um realm por tenant ou Organizations dentro de um realm compartilhado.
- **Decisão:** um realm único (`identity-gateway`) com o recurso **Organizations** (Keycloak 26+). Cada tenant é uma Organization.
- **Consequências:** issuer único, o que mantém simples a validação nas APIs consumidoras; escala para milhares de tenants; login *identity-first* com redirecionamento por domínio de e-mail já vem do Keycloak. Em contrapartida, políticas de senha, detecção de força bruta e rotação de refresh token são configurações de realm e, portanto, iguais para todos os tenants (ver seção 19). Realm dedicado para clientes enterprise fica registrado como evolução, com o custo de multi-issuer documentado.

### ADR-002 — Gateway fora do caminho de emissão e validação de tokens

- **Contexto:** "autenticar usuários de forma centralizada" pode levar a Gateway a intermediar o fluxo OIDC ou a validar tokens a cada requisição.
- **Decisão:** a Gateway orquestra (discovery, provisionamento de clients, credenciais M2M), mas os clientes obtêm tokens diretamente do Keycloak e as APIs validam localmente.
- **Consequências:** sem ponto único de falha **adicional** — a Gateway não entra no caminho do login nem do tráfego de negócio, e não há latência adicional por requisição. O Keycloak permanece dependência crítica de ambos os caminhos (§2.1). A queda da Gateway ainda afeta permissões finas com cache frio (§9.6). A Gateway não é um BFF: se alguma aplicação futura precisar de BFF, ele será por aplicação e fora deste repositório.

### ADR-003 — Sem ROPC e sem manipulação de credenciais

- **Decisão:** o fluxo *Resource Owner Password Credentials* é proibido. A criação de usuários não define senha: o Keycloak envia ao usuário um e-mail de ações obrigatórias (`UPDATE_PASSWORD`, `VERIFY_EMAIL`).
- **Consequências:** MFA e políticas do Keycloak valem para todos os fluxos; o banco da Gateway fica fora do escopo mais sensível de compliance.
- **Fluxos permitidos (complemento da v2.7):** Authorization Code com PKCE para aplicações; Client Credentials para M2M; e **Device Authorization Grant (RFC 8628), no realm da aplicação, só no client `identity-gateway-demo`** — público, do ambiente local e fora do Terraform de produção. É o que deixa a demonstração obter um token de usuário pelo terminal com a senha digitada só na página do Keycloak. Nos testes, o `KeycloakFixture` cria em runtime um segundo client de device flow, sem o scope `gateway-api`, usado só para a Account REST API.
- **Alcance da proibição do ROPC (v2.7):** nenhum client declarado no JSON do realm tem direct grant, e uma regra do `RegrasDoRealmTests` exige isso. O `admin-cli` embutido do realm mantém o direct grant, com que o Keycloak o cria; o token que ele emite é leve, sem audiência e sem `sub`, e a Gateway o recusa — há teste com o Keycloak real (§13). A garantia é: **nenhum client do realm emite, por senha, um token aceito pela Gateway.** Ficam fora do ADR-003, e declarados: o `kcadm` do one-shot do compose e o Testcontainers, que usam a senha do admin do realm `master`, infraestrutura fora do realm da aplicação (§15). O harness dos testes e da CI submete o formulário de login do próprio Keycloak com uma senha que ele mesmo definiu pelo link de ações; a credencial nunca passa pela Gateway, e isso não é ROPC.

### ADR-004 — Claims vêm exclusivamente de Protocol Mappers

- **Contexto:** o token é assinado pelo Keycloak; a Gateway não pode alterá-lo sem se tornar um segundo Authorization Server.
- **Decisão:** nenhum claim é **emitido ou assinado** fora do Keycloak. Todo claim customizado (`tenant_id`, `roles`, `tier`) é produzido por Protocol Mappers configurados no Keycloak. Dados de negócio que precisam estar no token são sincronizados pela Gateway como atributos — de usuário, no caso de `tenant_id` (§12.2), ou da Organization, no caso de `tier`.
- **Escopo da regra:** a *derivação local* de claims a partir do conteúdo de um token já validado é permitida no Data Plane, e apenas para achatar formato — é o caso da transformação de contingência da §12.1, Solução B. Derivar não é emitir: nada ali cria autoridade que o token não carregasse.
- **Consequências:** nenhum mapper customizado em Java consulta a Gateway durante a emissão do token, o que criaria dependência de runtime no login e derrubaria o ADR-002 junto. Mudanças de plano refletem no token no próximo refresh.

### ADR-005 — Papéis globais no token, permissões finas na Gateway

- **Decisão:** o token carrega apenas `tenant_id` e um catálogo pequeno e fixo de papéis (`platform-admin`, `tenant-admin`, `financial-manager`, `reader`). Permissões finas, customizáveis por tenant (ex.: `invoices:approve`), vivem na Gateway como *Permission Sets* e são resolvidas pelas APIs consumidoras com cache.
- **Consequências:** token pequeno, sem risco de estourar limites de header; revogação de permissão fina tem janela de atraso igual ao TTL do cache (mitigada por evento de invalidação); o access token tem vida curta: 5 minutos, configurados explicitamente no realm (`accessTokenLifespan: 300`, v2.7). O claim `roles` traz **só** o catálogo: o client scope `gateway-roles` limita o mapper aos quatro papéis, e os papéis padrão do realm ficam fora do token (§12.1, v2.7).

### ADR-006 — Consistência via Outbox, provisionamento idempotente e reconciliação

- **Contexto:** a Admin API do Keycloak não é transacional, e a criação de um tenant envolve o banco da Gateway e o Keycloak.
- **Decisão:** o comando grava o aggregate em estado `Pending` e o evento no **Outbox** na mesma transação. Um consumidor executa o provisionamento em passos idempotentes ("garanta que existe"). Um job periódico reconcilia divergências entre os dois lados.
- **Mecanismo:** o do CleanStart — um interceptor de `SaveChanges` coleta os domain events antes do commit e os grava na tabela de outbox, e um `OutboxWorker` publica depois, de forma assíncrona. O **transporte é RabbitMQ**. O worker é um job de fundo e portanto está sujeito ao ADR-010 (um só executor por vez). **Até a fatia do broker (v2.5), o transporte é em
   processo:** o `OutboxWorker` chama o `OutboxProcessor` a cada ciclo, e é o `DispatchingOutboxPublisher` — que o
   `OutboxProcessor` invoca por mensagem — quem despacha cada evento como command do Mediator; o retry é o do
   próprio Outbox.
- **Correlação idempotente:** cada Organization criada recebe o atributo `gateway_tenant_id` com o `TenantId` da Gateway, e é por ele que o "consultar antes de criar" localiza recursos preexistentes (§11.6). A busca é `GET .../organizations?q=gateway_tenant_id:{id}&briefRepresentation=false` (§11.6). O `alias` **não** serve para isso, embora também seja buscável (`q=alias:...`): uma Organization criada fora da Gateway com o mesmo alias seria confundida com a nossa, enquanto o atributo é escolhido por nós e distingue o que é nosso do que não é.
- **Consequências:** o endpoint de criação responde `202 Accepted` com o recurso de status do provisionamento. Nenhuma falha do Keycloak deixa estado órfão sem ser detectada — **desde que a reconciliação varra todos os estados não terminais**, e não apenas `Active`: o órfão típico nasce justamente antes da ativação, com a Organization criada e a gravação do id falhando (§9.1).

### ADR-007 — Sincronização Keycloak → Gateway por leitura de eventos

- **Contexto:** usuários podem nascer fora da Gateway, por exemplo no primeiro login via IdP federado, o que contornaria o limite de usuários do plano.
- **Decisão:** na v1, um *background service* lê periodicamente os eventos de usuário e os eventos administrativos pela Admin API e os converte em comandos da Application. Um Event Listener SPI em Java, com envio direto ao RabbitMQ, fica como evolução.
- **Deduplicação sem id estável:** os eventos da Admin API **não têm identificador estável**, então a dedup é feita por **tupla** (`time`, `type`, `userId`, `clientId`, hash dos `details`) persistida numa janela móvel. O checkpoint avança por timestamp com **sobreposição deliberada**: relê os últimos N segundos e descarta repetições pela tupla, trocando reprocesso por não-perda.
- **Instância única:** o poller e o job de reconciliação tomam `pg_try_advisory_lock` antes de rodar. Sem isso, N réplicas da API processariam os mesmos eventos em paralelo. Como rede final, `(TenantId, ExternalUserId)` é único no banco.
- **Consequências:** o projeto continua 100% .NET; a latência de sincronização é igual ao intervalo de polling. A sincronização é **at-least-once com perda possível sob downtime prolongado**: se o poller ficar parado mais que o `eventsExpiration` do realm, os eventos do intervalo deixam de existir. Um alarme dispara quando `now - checkpoint > eventsExpiration × 0,5` (§14, §19).

### ADR-008 — Integração com o Keycloak isolada atrás de uma porta

- **Decisão:** a Application conhece apenas a interface `IIdentityProvider`. A implementação fica na Infrastructure, com um cliente HTTP tipado próprio que cobre somente os endpoints usados. Nenhum tipo do Keycloak atravessa essa fronteira, e um teste de arquitetura garante isso.
- **Alternativa considerada:** gerar o cliente completo a partir do OpenAPI da Admin API (Kiota). Rejeitada na v1 pelo volume de código gerado frente aos poucos endpoints usados; pode ser revista se a superfície de integração crescer.

### ADR-009 — Na v1, um usuário pertence a um único tenant

- **Decisão:** cada usuário é membro de exatamente uma Organization.
- **Consequências:** o claim `tenant_id` é único e não ambíguo; papéis de realm podem ser atribuídos ao usuário sem vazar entre tenants. Usuários em múltiplos tenants exigiriam seleção de organização no login e papéis por organização, e ficam registrados como evolução.

---

### ADR-010 — Um só executor por job de fundo, via advisory lock

- **Decisão:** todo `BackgroundService` — poller de eventos do Keycloak (ADR-007), reconciliação (§9.1), expiração de convite (§9.9) e fechamento da janela `OverSubscribed` (§9.4) — adquire um `pg_try_advisory_lock` com uma chave própria antes de executar, e desiste do ciclo se não conseguir. Como rede final, unique constraint em `(TenantId, ExternalUserId)`.
- **Problema:** com N réplicas da API, cada uma roda sua própria cópia de cada job. Três réplicas significam o mesmo checkpoint lido três vezes, `RegisterExternalMember` disparado em triplicata e reconciliações revertendo o trabalho uma da outra. Como os eventos do Keycloak não têm id estável (C7), a dedup do ADR-007 não salva — são duas falhas independentes que se compõem (C14).
- **Alternativa rejeitada:** extrair os jobs para um worker próprio implantado com réplica única. Resolve, mas acrescenta um artefato de deploy e torna esse processo ponto único dos jobs.
- **Custo aceito:** o lock é por conexão e some se a réplica morrer, então o job fica parado até o próximo ciclo de outra réplica — latência adicional de um intervalo, não perda de trabalho. A chave de cada job é constante versionada no código, e colisão entre jobs distintos é erro de programação que um teste cobre.

### ADR-011 — Nas rotas de governança, autorização é token mais pertença no banco

- **Estado:** decidido na v2.7; a implementação chega com a primeira rota de tenant (D2, planejado).
- **Contexto:** o `tenant_id` do token vem de um mapper de atributo de usuário (§12.2). Quando o usuário não tem o atributo, esse mapper recua para o atributo de mesmo nome do primeiro grupo que o tiver, subindo aos pais, e não há configuração que desligue o recuo. O papel `manage-users`, que o service account da Gateway tem (§10.2), cria grupos e mapeia neles qualquer papel que não seja de administração. Verificado ao vivo no Keycloak 26.7.4: com o token do service account, foi criado um grupo com `tenant_id` e com `tenant-admin` e `platform-admin` mapeados, e um usuário novo posto nesse grupo recebeu um token com os dois papéis e o `tenant_id` forjado.
- **Decisão:** nas rotas de governança de tenant, a Gateway não confia só no token. Além do papel e do `tenant_id` igual ao da rota, o `sub` precisa ser `Member` daquele tenant no banco da Gateway, em status `Invited` ou `Active`; todo outro estado nega, inclusive um que o enum ganhe depois. O realm não tem grupos, e os papéis do catálogo nunca são compostos; um teste confere as duas coisas no JSON do realm.
- **Limites, ditos por inteiro:**
  - **O Data Plane não tem a pertença.** As Resource APIs confiam no claim e na regra "nenhum grupo", que é conferida no JSON do bootstrap; um grupo criado em runtime não é visto. Decidir entre uma reconciliação que detecte grupos com `tenant_id` e um mapper que não recue é da fatia do Data Plane (§19).
  - **A pertença não contém quem tem a chave da Gateway.** Com o `manage-users`, ele troca a senha ou o e-mail de um `Member` real e passa com a conta dele. A pertença protege contra o recuo do mapper e contra a forja por grupo, que é silenciosa; tomar a conta de alguém é ruidoso, porque o dono perde o acesso. A trilha desse ataque só existe com os eventos de administração ligados, sem representação (§10.3).
- **Consequências:** a pertença é consultada só nas rotas de governança, e só para quem já passou nas camadas do token — nunca por requisição de negócio. **O ADR-002 continua valendo:** o Data Plane não chama a Gateway para autorizar. A consulta é a última camada da policy (§10.1, §11.7).

## 5. Responsabilidades: Keycloak × IdentityGateway

| Componente | Keycloak | IdentityGateway (.NET 10) |
|---|---|---|
| Credenciais | Hash (Argon2/PBKDF2), expiração, reset, MFA/TOTP | Nenhuma. Nunca vê credencial de usuário. Credenciais de client transitam na resposta de criação/rotação, sem serem persistidas (§10.3) |
| Emissão de tokens | Assinatura dos JWT (RS256/ES256), refresh token rotation | Nenhuma. Provisiona os clients que solicitam tokens. Ao **validar** os tokens que recebe, aceita só o algoritmo do realm, RS256 (v2.7, §10.1) |
| Claims | Protocol Mappers produzem `tenant_id`, `roles`, `tier` | Mantém sincronizados os atributos que alimentam os mappers |
| Tenants | Organization, domínios e IdPs vinculados | Aggregate `Tenant`: plano, limites, status, ciclo de vida |
| Usuários | Perfil, dados pessoais, sessões | Vínculo (`sub`), status de governança, papéis e permission sets; **temporariamente**, o e-mail do admin inicial de um tenant `Pending` (v2.6, §6) |
| Permissões finas | Nenhuma | Fonte da verdade dos Permission Sets por tenant |
| Step-up auth | Executa o fluxo exigido via `acr_values` e emite o claim `acr` | **Define e verifica** os níveis exigidos por operação sensível (`StepUpRequirement`, §10.1) |
| Auditoria | Eventos de login e eventos administrativos | Trilha de auditoria de toda ação de governança |

---

## 6. Modelo de domínio

O bounded context é **Identity Governance**. Os dados pessoais (nome, e-mail, telefone) ficam no Keycloak. A Gateway guarda apenas o identificador do usuário no Keycloak (`sub`) e os dados de governança, o que limita o impacto de um eventual vazamento do seu banco.

**Exceção declarada e limitada (v2.6): o e-mail do admin inicial.** Entre o `POST /tenants` e o convite, o `initialAdminEmail` fica numa coluna anulável do tenant (`InitialAdminEmail`), fora do evento `TenantRegistered` — que o levaria ao Outbox e, com o broker, ao RabbitMQ. Ele só existe em tenant `Pending` e é apagado na transação que ativa o tenant **ou** que o marca `ProvisioningFailed` (§9.1). Cifrá-lo em repouso exigiria gestão de chave para um dado que vive horas; uma tabela própria seria uma segunda fonte de estado do provisionamento. A retenção real do valor apagado e as proteções contra vazamento estão na §10.3.

### 6.1. Aggregates

**`Tenant`** (aggregate root)
- Identidade: `TenantId`, `TenantSlug` (vira o *alias* da Organization) e `ExternalOrganizationId` (id no Keycloak, preenchido após o provisionamento).
- Estado: `Plan` (value object com `Tier`, `MaxUsers` e `MaxClients`), `Status`, `OverSubscribed` (bool) e a lista de `EmailDomain`.
- **`InitialAdminEmail`** (`Email?`, v2.6): o e-mail do admin inicial, exigido por `Register` e guardado só enquanto o tenant está `Pending`. É apagado na mesma operação que ativa o tenant (`CompleteProvisioning`) ou que o marca `ProvisioningFailed`, e nunca vai ao evento `TenantRegistered`. É a exceção declarada à regra de dados pessoais só no Keycloak (§6).
- **`HasSeatAvailable`** (leitura, v2.6): se ainda há vaga livre no plano. O provisionamento a consulta antes de tocar o Keycloak (§11.5).
- Controle de vagas: um contador de vagas ocupadas, protegido por concorrência otimista (`xmin` do PostgreSQL). Duas reservas simultâneas não ultrapassam o limite do plano; o conflito de versão é reprocessado por retry explícito (§11.10).
- Invariantes:
  - o slug é único e imutável;
  - um tenant fora do status `Active` não aceita novos membros. **A única exceção é o admin inicial** (v2.6): a vaga dele e o `Member` nascem na mesma operação de domínio que ativa o tenant (`CompleteProvisioning`, §11.1), gravada num commit só — `ReserveSeat` continua exigindo `Active` para todo outro chamador;
  - **nenhuma operação da API** eleva `OccupiedSeats` acima de `Plan.MaxUsers`. A absorção de usuários criados fora da Gateway (§9.4) pode ultrapassar o limite e marca o tenant como `OverSubscribed` — é a única via pela qual o contador excede o plano, e ela é auditada.
  - **o número de clients OIDC ativos nunca excede `Plan.MaxClients`.** O registro de client (§8) é rejeitado com `409` quando o limite já foi atingido. Sem esta regra, `MaxClients` seria um campo do value object `Plan` que nenhuma operação consulta — limite declarado e sem efeito.
  - **a troca de plano não pode violar as duas regras acima** (N8). `PATCH /tenants/{tenantId}` é rejeitado com `409` e Problem Details quando o plano de destino tem `MaxUsers` abaixo das vagas ocupadas ou `MaxClients` abaixo dos clients ativos, **nomeando quantos membros ou clients precisam sair antes**. Rebaixar o plano de um tenant com 40 vagas ocupadas para um plano de 10 violaria a invariante no instante da troca; recusar preserva a invariante sem destruir dados do cliente.

> **Nota sobre a invariante de vagas.** A v2.0 afirmava que "o número de membros ativos nunca excede `Plan.MaxUsers`", o que o próprio fluxo 9.4 violava. Uma invariante que o sistema sabe violar não sobrevive ao primeiro teste de domínio sério; a formulação acima separa o que a API garante do que a absorção externa pode romper.

**`Member`** (aggregate root, referencia `TenantId`)
- Dados: `ExternalUserId` (o `sub`), `Status` (`Invited`, `Active`, `Deactivated`, `Expired`, `Revoked`, `Erased`), `InvitedAt`, `WasActiveBeforeSuspension` (bool), papéis do catálogo global e referências a Permission Sets.
- **Ocupação de vaga:** a vaga é reservada **no convite** (`Invited` já ocupa) e liberada em `Deactivated`, `Expired`, `Revoked` e `Erased`. Reservar só na ativação permitiria que N convites simultâneos estourassem o plano no aceite — e o aceite chega pelo polling do ADR-007, tarde demais para recusar.
- Invariantes:
  - um membro `Erased` é terminal e não guarda nenhum dado que identifique a pessoa;
  - a liberação de vaga é **consequência de transição de estado efetiva**, nunca chamada solta: desativar um membro já desativado não decrementa o contador de novo.
- `WasActiveBeforeSuspension` existe para que a reativação do tenant (§9.7) restaure apenas quem estava `Active` no momento da suspensão, sem ressuscitar membros desativados individualmente antes disso.

- **O admin inicial é a exceção de ordem na ocupação de vaga** (v2.6): no Keycloak, o convite — e o e-mail — acontece antes da ativação; no domínio, a vaga é reservada na ativação, junto com a criação do `Member`, depois do envio do e-mail. Um tenant sem vaga livre falha antes de tocar o Keycloak (§11.5). Deixar o admin fora do contador criaria um membro grátis e um caso especial eterno nas vagas, no downgrade (N8) e na expiração.

> **Entregue na v2.6 (fatia C), no mínimo que o convite do admin exige.** `Id` (`MemberId`, `readonly record struct` com `Guid.CreateVersion7()`, como o `TenantId`), `TenantId`, `ExternalUserId` (o `sub`), `Status` (`MemberStatus`, com os seis valores acima, persistido como texto) e `InvitedAt` (UTC), na tabela `members`, com índice único em `(tenant_id, external_user_id)` — a rede final que o ADR-007 e o ADR-010 já previam. A fábrica `Member.Invite` é `internal` ao Domain e só `Tenant.CompleteProvisioning` a chama, para que nenhum membro nasça sem vaga reservada; um teste de arquitetura garante que não há construtor nem fábrica públicos. **O papel vive no Keycloak** (ADR-005) e não é duplicado no `Member` nesta versão: os "papéis do catálogo global" acima ficam para o M2, com `PUT .../roles`. `WasActiveBeforeSuspension` e as referências a Permission Sets entram com as fatias que as usam.

**`PermissionSet`** (aggregate root, referencia `TenantId`)
- Dados: nome e conjunto de `Permission` (value object no formato `recurso:ação`, por exemplo `invoices:approve`).
- Invariante: o nome é único dentro do tenant.

**`ClientApplication`** (aggregate root, referencia `TenantId`)
- Dados: `ClientId`, tipo (`Public` para PKCE, `Confidential` para M2M), método de autenticação (`private_key_jwt` preferencial, `client_secret` como alternativa) e status.

### 6.2. Máquina de estados do tenant

```
             Register
  (novo) ──────────────► Pending ──────────► Active ◄────────────────┐
                            │    provisioned    │                    │
                            │                   │ suspend (202)      │ reactivate
                            │                   ▼                    │
               retries      │              Suspending                │
               esgotados    │                   │ consumidor concluiu│
                            ▼                   ▼                    │
                   ProvisioningFailed       Suspended ───────────────┘
                            │                   │
                            │                   │ terminate (202)
                            │                   ▼
                            │              Terminating
                            │                   │ consumidor concluiu
                            │                   ▼
                            │              Terminated  (terminal)
                            └── retry manual ──► Pending
```

`Terminated` é alcançável **somente a partir de `Suspended`**, o que impede o encerramento acidental de um tenant em operação. O encerramento desabilita a Organization, os membros **e os clients OIDC**, mas **não remove dados do Keycloak** — ver §9.8.

`Terminating` é **estado de passagem**: o `DELETE` responde `202` e grava o evento no Outbox, e o efeito no Keycloak acontece depois. Sem ele não haveria como representar "encerramento aceito, ainda processando" — e o `GET` do tenant não teria o que reportar nessa janela. Pelo mesmo motivo existe `Suspending` (§9.7). Nenhum dos dois aceita operação de gestão, e a reativação é recusada em `Suspending`.

**A saída de `ProvisioningFailed` é só o retry manual**, que devolve o tenant a `Pending` antes de reenfileirar — ou a reconciliação. Uma mensagem repetida que encontre o tenant em `ProvisioningFailed` não o reprovisiona (v2.5).

### 6.3. Domain services e regras transversais

- **`RoleAssignmentPolicy`** impede escalação de privilégio: um ator só atribui papéis do próprio tenant e nunca acima do seu papel mais alto. A hierarquia é `platform-admin` > `tenant-admin` > `financial-manager` > `reader`. **Ela é o teto da atribuição, não herança de acesso** (v2.7): estar acima na hierarquia limita quais papéis o ator pode conceder, e não dá a ele o que o papel de baixo acessa. Um `platform-admin` não passa numa policy de `tenant-admin`, e a policy `TenantAdmin` nega quem acumula os dois papéis — separação de funções, §10.1 (D2, planejado).
  - A policy é correta, mas **depende da qualidade da sua entrada**: `actor.HighestRole` deriva do claim `roles` do token, portanto sua corretude pressupõe o claim plano da §12.1 e a verificação de tenant da §11.7. Um teste de integração cobre a cadeia inteira, não só a policy isolada.
- **Domain events:** `TenantRegistered`, `TenantActivated`, `TenantSuspended`, `TenantReactivated`, `TenantTerminated`, `TenantOverSubscribed`, `MemberInvited`, `MemberInviteExpired`, `MemberDeactivated`, `MemberRolesChanged` e `PermissionSetChanged`. São convertidos em eventos de integração e publicados pelo Outbox. `MemberInvited` continua no catálogo, para o convite comum do M2, mas **o admin inicial nasce sem ele** (v2.6): o evento ainda não teria consumidor, e o `Member` do admin é criado dentro de `Tenant.CompleteProvisioning`, que levanta só `TenantActivated`.

### 6.4. Regra de acesso a sub-recursos

`Member`, `PermissionSet` e `ClientApplication` são aggregate roots próprios que **referenciam** `TenantId`. Isso cria um vetor de acesso indevido que a verificação de tenant da rota não cobre: um ator do tenant A, operando em `/tenants/A/...`, informando o id de um recurso do tenant B.

**Regra:** todo repositório de sub-recurso expõe **exclusivamente** assinaturas que exigem o tenant — `GetAsync(TenantId, MemberId)`, nunca `GetAsync(MemberId)`. A ausência da sobrecarga por id só é o que torna a regra verificável: não há como escrever o acesso inseguro por engano. Um global query filter por tenant complementa como rede.

Um id que não pertence ao tenant da rota resulta em **404**, não 403 — responder 403 confirmaria a existência do recurso em outro tenant.

**A leitura da pertença segue a mesma regra (v2.7) (D2, planejado).** A policy de tenant da Gateway confere se o ator é membro do tenant da rota (ADR-011) por uma porta de leitura com o tenant na assinatura: `IMemberQueries.GetStatusAsync(TenantId, ExternalUserId, CancellationToken)`, que devolve `MemberStatus?`. Não existe leitura de membro só pelo `sub`. É uma porta de consulta, no padrão `I*Queries` do repositório, e não um método novo do repositório do agregado: o `IMemberRepository` continua só com `Add` (§11.9).

---

## 7. Organização das camadas

```
IdentityGateway/
├── src/
│   ├── IdentityGateway.Domain/               # Sem dependências externas (apenas System.*)
│   │   ├── Tenants/                          # Tenant, Plan, TenantSlug, EmailDomain, eventos
│   │   ├── Members/                          # Member, RoleName, RoleAssignmentPolicy
│   │   ├── Permissions/                      # PermissionSet, Permission
│   │   ├── Clients/                          # ClientApplication
│   │   └── Common/                           # AggregateRoot, Result, Error, DomainException
│   │
│   ├── IdentityGateway.Application/          # Casos de uso (CQRS) e portas
│   │   ├── Common/Messaging/                 # ICommand, IQuery, handlers
│   │   ├── Common/Abstractions/              # Portas de saída: IUnitOfWork, repositórios, IIdentityProvider
│   │   ├── Tenants/Commands/ e Queries/
│   │   ├── Members/Commands/ e Queries/
│   │   ├── Permissions/
│   │   ├── Clients/
│   │   └── Discovery/                        # Resolução de tenant e IdP pelo e-mail
│   │
│   ├── IdentityGateway.Infrastructure/       # Adaptadores
│   │   ├── Persistence/                      # EF Core, configurações, migrations, repositórios
│   │   ├── Identity/Keycloak/                # KeycloakAdminClient, KeycloakIdentityProvider
│   │   ├── Configuration/                    # Options validadas na subida; AccessTokenValidationOptions (v2.7)
│   │   ├── Messaging/                        # MassTransit, Outbox, consumidores finos
│   │   ├── Sync/                             # Leitura de eventos do Keycloak e reconciliação
│   │   └── DependencyInjection.cs
│   │
│   ├── IdentityGateway.Api/                  # Presentation: módulos Carter, policies, OpenAPI
│   │   ├── Endpoints/                        # Um arquivo por recurso, sem regra de negócio
│   │   ├── Authentication/                   # Validação do access token: JwtBearer, azp, typ e sub (v2.7)
│   │   ├── Authorization/                    # Policies, requirements e o Problem Details de 401 e 403 (v2.7)
│   │   ├── OpenApi/                          # Transformers do documento OpenAPI 3.1
│   │   └── Program.cs                        # Composition root
│   │
│   └── IdentityGateway.Client.AspNetCore/    # Pacote para as APIs consumidoras (Data Plane)
│
├── samples/
│   └── SampleResourceApi/                    # API de negócio de exemplo que valida tokens localmente
│
├── tests/
│   ├── IdentityGateway.Domain.UnitTests/
│   ├── IdentityGateway.Application.UnitTests/
│   ├── IdentityGateway.Infrastructure.IntegrationTests/ # Testcontainers: Keycloak, PostgreSQL, Redis
│   ├── IdentityGateway.Api.FunctionalTests/  # WebApplicationFactory sobre a API inteira
│   ├── IdentityGateway.Testing.Keycloak/     # Suporte (biblioteca): fixture, mailpit e harness de login (v2.7)
│   └── IdentityGateway.ArchitectureTests/
│
├── keycloak/
│   ├── bootstrap/realm-identity-gateway.json # Import inicial, apenas para ambiente local
│   └── terraform/                            # Configuração evolutiva do realm
│
├── tools/
│   └── jornada-compose.cs                    # App de arquivo único: a jornada do compose na CI (v2.7)
│
├── docs/adr/
└── docker-compose.yml
```

**O que a v2.7 acrescenta à árvore.** `Api/Authentication` guarda a configuração do JwtBearer e as checagens além da biblioteca (`ValidacaoDoAccessToken`, `FormaDoAccessToken`, `AutenticacaoLogs`, `AvisoDeClientsPermitidos`). `Api/Authorization` guarda as policies (`Policies`), as respostas de `401` e `403` em Problem Details (`RespostasDeAutorizacao`, `ProblemDetailsDeAutorizacao`) e, com a primeira rota de tenant, os requirements e o `AutorizacaoDaGateway` (D2, planejado). `Infrastructure/Configuration` ganha a `AccessTokenValidationOptions` (seção `Keycloak:Auth`), que o adaptador do Keycloak preenche e a Api só lê (§11.8). `tests/IdentityGateway.Testing.Keycloak` é uma **biblioteca** de suporte, e não um projeto de teste: não referencia `src/`, e é usada pelos projetos de integração e funcional e pelo app de `tools/` (§13, §15). A árvore nunca listou a pasta `Security` do template, que guardava o emissor de JWT simétrico e saiu com ele.

**Regras de dependência** (verificadas por testes de arquitetura):

| Camada | Pode depender de | Não pode depender de |
|---|---|---|
| Domain | Nada além de `System.*` | Qualquer outra camada ou framework |
| Application | Domain | Infrastructure, Api, EF Core, MassTransit, tipos do Keycloak |
| Infrastructure | Application, Domain | Api |
| Api | Application; Infrastructure somente no composition root | Regras de negócio, `DbContext` direto |

Convenções herdadas do padrão de trabalho do projeto: validação de entrada com **Notification Pattern**, mapeamento manual entre camadas (sem AutoMapper) e erros expostos como **Problem Details (RFC 9457)**.

---

## 8. Catálogo de endpoints (v1)

Todos sob `/api/v1`. Endpoints de escrita aceitam o cabeçalho `Idempotency-Key`.

**Versionamento.** A versão vive no path e o roteamento usa `Asp.Versioning.Http`. Quando existir uma `v2`, as duas **convivem**: a `v1` passa a responder com os cabeçalhos `Deprecation` e `Sunset` (RFC 8594) informando a data de encerramento, e só é removida depois dela. Escrever a política antes de precisar dela custa um parágrafo; decidi-la sob pressão, com clientes já integrados, custa bem mais.

| Recurso | Método e rota | Quem pode |
|---|---|---|
| **Discovery** | `POST /auth/discovery` | Anônimo, com rate limit |
| **Tenants** | `POST /tenants` → `202 Accepted` — exige `initialAdminEmail` | platform-admin |
| | `GET /tenants` (listagem) — chega com a auditoria | platform-admin, auditado (§10.1) |
| | `GET /tenants/{tenantId}` (D2, planejado) | tenant-admin do próprio tenant **e** `Member` dele no banco (ADR-011). **Desvio declarado (v2.7):** o platform-admin recebe `403` até existir a policy `TenantReadAccess`, que chega com a auditoria (§10.1) |
| | `GET /tenants/{tenantId}/provisioning` | platform-admin |
| | `PATCH /tenants/{tenantId}` (nome, plano) — **`409` se o plano novo ficar abaixo das vagas ocupadas ou dos clients ativos** (N8) | platform-admin |
| | `POST /tenants/{tenantId}/suspend` → `202` (desabilita membros **e clients**), `/reactivate` (**`409` em `Suspending`**) | platform-admin |
| | `DELETE /tenants/{tenantId}` → `202` (encerramento, só de `Suspended`) | platform-admin + step-up |
| **Domínios e federação** | `POST/DELETE /tenants/{tenantId}/domains` | tenant-admin |
| | `POST/DELETE /tenants/{tenantId}/identity-providers` | tenant-admin |
| **Membros** | `POST /tenants/{tenantId}/members` (convite) | tenant-admin |
| | `GET /tenants/{tenantId}/members`, `GET .../members/{memberId}` | tenant-admin |
| | `POST .../members/{memberId}/resend-invite` (só em `Invited`; **reinicia `InvitedAt`**) | tenant-admin |
| | `DELETE .../members/{memberId}/invite` (cancela convite pendente → **`Revoked`**, libera vaga) | tenant-admin |
| | `POST .../members/{memberId}/deactivate`, `/reactivate` | tenant-admin |
| | `DELETE .../members/{memberId}` (exclusão definitiva, LGPD) | tenant-admin + step-up |
| | `PUT .../members/{memberId}/roles` | tenant-admin, sujeito à `RoleAssignmentPolicy` |
| **Permissões** | `GET/POST/PUT/DELETE /tenants/{tenantId}/permission-sets` | tenant-admin |
| | `GET .../members/{memberId}/effective-permissions` | tenant-admin; **client de plataforma** com escopo `gateway.permissions.read` (§10.1) |
| **Clients OIDC** | `POST /tenants/{tenantId}/clients` (SPA/mobile `Public`, M2M `Confidential`) — **`409` se `Plan.MaxClients` atingido** | tenant-admin |
| | `POST .../clients/{clientId}/rotate-credentials` | tenant-admin + step-up |
| | `DELETE .../clients/{clientId}` | tenant-admin |
| **Catálogo** | `GET /roles` | autenticado |
| **Saúde** | `GET /health/live`, `GET /health/ready` | anônimo (rede interna) |

**Sem rota nova na v2.6; a primeira rota de tenant chega com a v2.7 (abaixo).** O convite do admin inicial acontece dentro do provisionamento (§9.1), e o `initialAdminEmail` do `POST /tenants` passa a ficar guardado no tenant só até a ativação (§6). Nenhuma resposta nem read model expõe esse e-mail, inclusive o `GET .../provisioning`. **Falta uma operação de plataforma para trocar ou reenviar o convite do admin inicial:** o platform-admin não opera rotas de membro (§10.1), e hoje um e-mail digitado errado entrega o tenant a quem o recebe — o runbook provisório é desabilitar o usuário no Keycloak à mão. A operação fica para o M1/M2 (§19).

**A primeira rota de tenant (v2.7) (D2, planejado).** `GET /api/v1/tenants/{tenantId}` devolve o tenant a quem o administra. Responde `200` com **exatamente** estas chaves: `tenantId`, `name`, `slug`, `status`, `plan` (`tier`, `maxUsers`, `maxClients`), `occupiedSeats` e `registeredAt` — nunca o e-mail do admin inicial, e um teste trava o conjunto de chaves. Responde `401` sem token ou com token inválido. Responde `403` em todo o resto: platform-admin, tenant-admin de outro tenant, token sem `tenant-admin`, `sub` que não é `Member` com status aceito e **tenant inexistente** — a policy nega antes do handler, porque não há `Member` num tenant que não existe, e a rota não usa `404`. Todo `403` é o mesmo Problem Details, sem nada que distinga o motivo. Os nomes de `TenantStatus` passam a ser contrato público, e um teste os trava junto com os de `MemberStatus`. O `GET .../provisioning` continua respondendo `200` com o estado, para o platform-admin.

**Autenticação por padrão (v2.7).** A `FallbackPolicy` exige usuário autenticado em todo endpoint que não declare outra coisa. As rotas anônimas — `/health/live`, `/health/ready`, o documento OpenAPI e o Scalar em Development, e o redirect da raiz — levam `AllowAnonymous` explícito, e um teste enumera os endpoints e exige, em cada um, policy nomeada ou anonimato declarado. Efeito visível: um caminho não mapeado, sem token, responde `401`, e não `404`. `401` e `403` saem em Problem Details (§10.1).

---

## 9. Fluxos principais

### 9.1. Provisionamento de tenant

```
Cliente            Gateway API                PostgreSQL           Consumidor              Keycloak
   │ POST /tenants      │                          │                    │                      │
   │───────────────────►│ Tenant (Pending)         │                    │                      │
   │                    │ + e-mail no tenant       │                    │                      │
   │                    │ + evento no Outbox ─────►│ (mesma transação)  │                      │
   │ 202 + Location     │                          │                    │                      │
   │◄───────────────────│                          │ ── Outbox ────────►│                      │
   │                    │                          │                    │ 0. e-mail e vaga?    │
   │                    │                          │                    │ 1. Organization      │
   │                    │                          │                    │─────────────────────►│
   │                    │                          │                    │ 2. usuário, vínculo, │
   │                    │                          │                    │    papel e e-mail    │
   │                    │                          │                    │─────────────────────►│ ──► SMTP (mailpit)
   │                    │                          │◄───────────────────│ 3. um commit só:     │
   │                    │                          │                    │ Active + vaga +      │
   │                    │                          │                    │ Member (Invited) +   │
   │                    │                          │                    │ e-mail apagado       │
```

**O tenant nasce com um administrador.** `POST /tenants` exige `initialAdminEmail`, que fica no tenant até a ativação (§6, v2.6). O consumidor, hoje o `ProvisionTenantHandler` despachado pelo próprio Outbox (§11.5), verifica antes o que repetir não corrige, e depois executa dois passos idempotentes no Keycloak e um no banco:

0. sem e-mail (tenant registrado antes da v2.6) ou sem vaga livre no plano: `ProvisioningFailed`, sem tocar o Keycloak;
1. garante a Organization (com o atributo `gateway_tenant_id`);
2. garante o convite do `initialAdminEmail` com o papel `tenant-admin`: usuário habilitado com `UPDATE_PASSWORD` e `VERIFY_EMAIL` e o atributo `tenant_id`, vínculo à Organization, papel e o e-mail de ações obrigatórias, enviado só se o convite ainda não foi aceito (§11.6);
3. numa operação de domínio e num commit só: marca o tenant `Active`, reserva a vaga do admin, cria o `Member` em `Invited` e apaga o e-mail (§11.1).

Sem isso o tenant nasceria trancado: `POST /tenants` é `platform-admin`, mas convidar membros exige `tenant-admin` **daquele tenant** — que ainda não existiria — e o platform-admin não satisfaz a verificação de tenant da §11.7. A alternativa seria abrir uma exceção de platform-admin no mecanismo de isolamento; criar o admin junto do tenant **elimina** uma exceção em vez de acrescentar outra.

Se o tenant continuar em `Pending` além da **janela de provisionamento** (`Provisioning:MaxPendingHours`, 24h por padrão, contada desde `RegisteredAt`), o handler o marca `ProvisioningFailed` na próxima falha, e ele fica disponível para retry manual. Um erro **permanente** do adaptador (`IdentityProviderInconsistencyException`, §11.6) não espera a janela: vai direto a `ProvisioningFailed`. O Outbox precisa insistir por mais tempo que a janela, e a subida recusa configuração que não insista (v2.5).

**As decisões do passo 2 (v2.6).** A v2.4 deixou duas decisões pendentes para a fatia C; a fatia as fechou e acrescentou uma terceira:

1. **Onde o `initialAdminEmail` vive.** Numa coluna anulável do tenant (`InitialAdminEmail`), fora do evento `TenantRegistered`, que iria ao Outbox e, com o broker, ao RabbitMQ. É apagado na mesma transação que ativa o tenant **ou** que o marca `ProvisioningFailed`, e só existe em tenant `Pending`: é a exceção declarada à regra de dados pessoais só no Keycloak (§6, §10.3). Apagar também na falha evita retenção sem prazo, porque `ProvisioningFailed` não tem saída automática; o retry manual, quando existir, recebe o e-mail de novo no corpo, o que também permite corrigir um e-mail digitado errado. Convidar de forma síncrona no `POST` quebraria o `202` com o Keycloak fora do ar.
2. **Vaga × `Active`.** No Keycloak, o convite acontece antes da ativação; no domínio, ativar, reservar a vaga e criar o `Member` são uma operação só — `Tenant.CompleteProvisioning` (§11.1) —, gravada num commit só. `ReserveSeat` continua exigindo `Active`, e a invariante "tenant fora de `Active` não aceita membros" continua valendo para todo outro chamador (§6.1). Fazer `ReserveSeat` aceitar `Pending` enfraqueceria a invariante para todo chamador futuro.
3. **E-mail já em uso por outra conta.** O usuário é correlacionado pelo atributo de usuário `tenant_id`, o mesmo que alimenta o claim (§12.2). Um usuário com o `tenant_id` deste tenant é uma tentativa anterior e é reaproveitado; um e-mail em uso por qualquer outra conta é `IdentityProviderInconsistencyException`, e o tenant vai a `ProvisioningFailed`. Reaproveitar um usuário sem esse vínculo poderia entregar o tenant ao platform-admin ou a uma conta abandonada. Consequência: a mesma pessoa não administra dois tenants (ADR-009, §19).

A janela de provisionamento e a classificação de erros do parágrafo anterior valem para as duas chamadas ao Keycloak, a da Organization e a do convite (v2.6).

**Reconciliação.** O job compara periodicamente **todos os tenants em estado não terminal** (`Pending`, `ProvisioningFailed` e `Active`) com as Organizations existentes, e também o sentido inverso: Organizations com `gateway_tenant_id` que não correspondem a nenhum tenant conhecido. Varrer apenas `Active` cobriria só o caso em que nada deu errado — o órfão típico nasce antes da ativação, com a Organization criada e a gravação do id falhando.

### 9.2. Login interativo com discovery

1. A aplicação cliente coleta o e-mail e chama `POST /auth/discovery`.
2. A Gateway resolve o tenant pelo domínio e devolve os parâmetros do fluxo: `issuer`, `authorization_endpoint` e, quando o tenant tem IdP federado, o `kc_idp_hint`.
3. A aplicação gera `code_verifier` e `code_challenge` (PKCE) e redireciona o usuário **diretamente ao Keycloak**.
4. O Keycloak autentica o usuário, localmente ou via IdP federado, e devolve o `code` para a aplicação, que o troca pelos tokens no próprio Keycloak.

> **Nota (v2.7).** A demonstração do README obtém o token de usuário pelo Device Authorization Grant, no client `identity-gateway-demo`, que só existe no ambiente local (ADR-003). É um atalho de terminal para quem avalia o repositório, não o fluxo das aplicações: elas usam sempre o Authorization Code com PKCE, descrito acima.

Para não permitir enumeração de tenants, a resposta tem sempre o mesmo formato: um domínio desconhecido recebe os parâmetros de login padrão do realm. Como o recurso Organizations já faz o redirecionamento por domínio sozinho, o discovery é uma conveniência para clientes que precisam resolver o tenant antes do redirect (apps mobile, por exemplo), e não um passo obrigatório.

### 9.3. Comunicação M2M

O tenant-admin registra um client M2M pela Gateway. O método preferencial é `private_key_jwt`: o cliente envia sua chave pública (JWKS) e a chave privada nunca sai dele. Quando `client_secret` é usado, o segredo é devolvido **uma única vez**, na resposta da criação ou da rotação. Depois disso, o serviço obtém tokens diretamente do Keycloak via Client Credentials.

### 9.4. Usuário criado no primeiro login federado

1. O usuário do tenant A faz login pelo IdP corporativo. O Keycloak cria o usuário e o vincula à Organization.
2. O serviço de sincronização lê o evento e dispara o comando `RegisterExternalMember`.
3. A Application tenta reservar a vaga no aggregate `Tenant`.
4. Se o limite do plano foi atingido, o membro é registrado, o tenant é marcado `OverSubscribed`, o evento `TenantOverSubscribed` é publicado e uma entrada de auditoria é gerada. Bloquear o login exigiria um authenticator customizado no Keycloak e fica fora da v1.

**Esta é a exceção declarada ao princípio 4 (fail closed).** O usuário já foi autenticado pelo IdP do cliente; recusar o registro criaria um usuário autenticado sem membership, pior que o excesso. É também a única via pela qual `OccupiedSeats` pode exceder `Plan.MaxUsers` (§6.1).

5. **O limite volta a valer por tolerância, não por bloqueio.** `TenantOverSubscribed` abre uma janela configurável (padrão: 7 dias) registrada no tenant. Vencida a janela sem que o plano seja ajustado nem membros desativados, os excedentes — por ordem de entrada, o mais recente primeiro — são desabilitados via `SetUserEnabledAsync`. Sem isso, "detectamos e não fazemos nada" deixaria o plano sem efeito para qualquer tenant com federação, que é justamente o perfil de cliente maior.

### 9.5. Desativação e exclusão de membro

- **Desativar:** a Gateway desabilita o usuário no Keycloak **e revoga suas sessões ativas**. Sem a revogação, um refresh token emitido antes continuaria funcionando. A vaga do plano é liberada — uma vez só, como consequência da transição efetiva de estado (§6.1).
- **Excluir (direito ao esquecimento, LGPD art. 18):** exclusão definitiva no Keycloak; na Gateway, o membro passa a `Erased` e o `ExternalUserId` é substituído por um valor anônimo. A trilha de auditoria preserva a ação, não a identidade.

**Duas janelas que a revogação não fecha, e que estão declaradas na §19:**

1. **Offline tokens.** `POST .../users/{id}/logout` remove sessões online, mas **não** sessões offline. Como `offline_access` integra o `default-roles` de todo usuário do realm por padrão, qualquer usuário que tenha obtido um offline token continuaria renovando acesso depois da "revogação". Por isso o realm de bootstrap **tira `offline_access` do papel padrão** (§15): o projeto não usa offline tokens, e mantê-los ligados anularia a desativação. **Entregue na v2.7** — até a v2.6 este item descrevia como feito o que o JSON do realm não tinha (errata E7). O realm declara o papel `offline_access` e o client scope de mesmo nome só para tirá-los do padrão, e faz o mesmo com o papel `uma_authorization`; nenhum client oferece o scope, e o papel padrão fica `[manage-account, view-profile]`. Um teste contra o Keycloak real confere.
2. **Access token já emitido.** Ele sobrevive até expirar — no máximo 5 minutos. É o preço da validação stateless do ADR-002, e não há como encurtá-lo sem reintroduzir verificação remota por requisição.

### 9.7. Suspensão e reativação de tenant

Suspender **não é uma flag no banco da Gateway**. O `POST .../suspend` responde `202`, marca o tenant como `Suspending` e grava o evento no Outbox. O consumidor de `TenantSuspended` percorre **membro a membro** e, para cada um que esteja `Active`:

1. marca `WasActiveBeforeSuspension` **na mesma operação** que o desabilita;
2. desabilita o usuário no Keycloak;
3. revoga as sessões ativas dele.

Em seguida faz o mesmo com os **clients OIDC** do tenant (§9.7.1). Concluído tudo, o tenant passa a `Suspended`.

Sem os passos 2 e 3, os usuários do tenant suspenso continuariam logando e usando as Resource APIs normalmente — o token é emitido pelo Keycloak e validado localmente (ADR-002), e nada na suspensão o alcançaria. Um tenant inadimplente suspenso seguiria operando, o que esvaziaria o sentido da operação.

**A marca é gravada por membro, nunca em lote prévio** (L-3). Marcar todos os `Active` numa passada e só depois desabilitá-los cria uma janela em que um consumidor interrompido deixa membros marcados que nunca foram desabilitados — e a reativação, lendo a marca, restauraria gente que continuou ativa o tempo todo. Gravando marca e desabilitação juntas, por membro, um consumidor que cai **retoma de onde parou** sem estado inconsistente: reprocessar um membro já tratado é no-op, porque ele não está mais `Active`.

**A reativação exige suspensão concluída.** `POST .../reactivate` só é aceito com o tenant em `Suspended`; em `Suspending` responde `409`. Pela mesma razão, `DELETE /tenants/{tenantId}` também responde `409` em `Suspending`: o encerramento só parte de `Suspended` (§6.2), e aceitar um `DELETE` com a suspensão a meio caminho faria dois consumidores percorrerem a mesma lista de membros e clients ao mesmo tempo. Ela reabilita apenas os membros com `WasActiveBeforeSuspension = true` e os clients com `WasEnabledBeforeSuspension = true`, sem ressuscitar quem já estava desativado individualmente antes da suspensão, e limpa as duas marcas ao final.

#### 9.7.1. Clients OIDC na suspensão

A suspensão desabilita **também os clients OIDC do tenant**, com a marca `WasEnabledBeforeSuspension` gravada pelo mesmo critério por-client. Sem isso a suspensão teria um furo exatamente no canal que não depende de pessoa: um client M2M `Confidential` continuaria obtendo tokens por Client Credentials diretamente no Keycloak, e as Resource APIs os validariam localmente sem nada que os alcançasse (ADR-002). Um tenant inadimplente seguiria com suas integrações de máquina operando normalmente — a mesma falha que a decisão 3 do brainstorm corrigiu para membros, deixada aberta para serviços.

### 9.8. Encerramento de tenant

Espelha o 9.1, e só é alcançável a partir de `Suspended`:

1. `DELETE /tenants/{tenantId}` (platform-admin + step-up) responde `202`, marca `Terminating` e grava o evento no Outbox;
2. o consumidor desabilita a Organization, todos os membros (revogando sessões) **e todos os clients OIDC do tenant**, pelo mesmo percurso idempotente e retomável da §9.7;
3. o tenant passa a `Terminated`, estado terminal.

O encerramento **não grava marcas de restauração** — `WasActiveBeforeSuspension` e `WasEnabledBeforeSuspension` só existem para a reativação, e de `Terminated` não há volta.

**O encerramento não remove dados do Keycloak.** A remoção física da Organization e dos usuários é operação administrativa fora da API — removê-la aqui agravaria a correlação idempotente (§11.6) e a reconciliação bidirecional (§9.1) em troca de pouco. O `TenantSlug` permanece reservado, pois é imutável e único.

Isto fecha a simetria de compliance: a §9.5 dá direito ao esquecimento **por membro**, e esta seção permite encerrar o contrato de um cliente inteiro — que é o cenário em que a LGPD costuma ser invocada.

### 9.9. Ciclo de vida do convite

`Invited` **ocupa vaga** (§6.1), e é isso que torna a expiração obrigatória em vez de opcional: sem ela, um convite nunca aceito prenderia uma vaga paga para sempre. O ciclo tem quatro saídas.

**Convite.** `POST /tenants/{tenantId}/members` reserva a vaga, cria o usuário no Keycloak **habilitado**, sem senha, com as *required actions* (`UPDATE_PASSWORD`, `VERIFY_EMAIL`) e o atributo `tenant_id`, dispara o e-mail e grava `InvitedAt`. O membro nasce `Invited`. O link do e-mail vale pelo prazo da **política de convite** (`IInvitationPolicy.LinkLifetime`, padrão de 7 dias), passado ao Keycloak em cada envio — o padrão do realm, 12 horas, ficaria desalinhado do ciclo do convite. Nesta versão o prazo é global; o prazo por tenant chega no M2, pela mesma porta (v2.6). O admin inicial é o primeiro convite de todo tenant e percorre este mesmo caminho dentro do provisionamento (§9.1), com uma diferença de ordem: a vaga dele é reservada na ativação, depois do envio do e-mail (§6.1).

> **Errata da v2.6: o convidado nasce habilitado.** A v2.5 dizia que o usuário nascia desabilitado. O Keycloak 26.7.4 recusa `execute-actions-email` para usuário desabilitado (`400 User is disabled`) e recusa também o clique no link, e nada no fluxo o habilita. O que protege a conta antes do aceite é não ter senha: o único caminho de entrada é o link, e o "esqueci a senha" fica desligado no realm (`resetPasswordAllowed: false`, §10.3). Por isso a expiração e o cancelamento, abaixo, passam a desabilitar o usuário.

**Aceite.** O usuário define a senha e informa nome e sobrenome no Keycloak (a ação `VERIFY_PROFILE`, que o perfil padrão do realm já exige), e o evento chega pela sincronização do ADR-007 (v2.6). O membro passa a `Active` e a vaga, já reservada, apenas deixa de ser provisória — **não há nova reserva no aceite**, e é justamente por isso que o aceite não pode falhar por limite de plano (§6.1).

**Expiração.** Um job periódico da Gateway varre os membros `Invited` cujo `InvitedAt` excedeu o prazo, transiciona para `Expired`, libera a vaga e publica `MemberInviteExpired`. O prazo é **configurável por tenant, com padrão de 7 dias**, e o prazo do link de ações é o da política de convite, alinhado a ele. **A expiração também desabilita o usuário no Keycloak** (v2.6): como ele nasce habilitado, um link ainda válido para um membro já `Expired` produziria um aceite sem vaga reservada. **A expiração não pode chegar antes da sincronização do ADR-007** (M4) sem outra forma de saber do aceite: até lá, quem aceitou continua `Invited` na Gateway — é o caso do admin inicial —, e o job expiraria quem já entrou (§19).

> **Por que o job vive na Gateway, e não no Keycloak.** Quem libera a vaga tem de ser quem controla o contador. Derivar a expiração de um evento do Keycloak amarraria uma regra de plano à configuração de realm e dependeria do polling do ADR-007 — que a decisão 2 do brainstorm já considerou tarde demais para o aceite, pelo mesmo motivo.

**Reenvio.** `POST .../members/{memberId}/resend-invite` redispara o e-mail e **reinicia `InvitedAt`**, prorrogando a vaga já ocupada. Só é aceito em `Invited`, e **só envia se o usuário ainda tiver `UPDATE_PASSWORD` pendente** (v2.6): um link enviado depois do aceite trocaria a senha de quem já entrou. Os links emitidos antes continuam válidos até expirar, porque o Keycloak não os revoga por usuário (§19). É o ticket de suporte mais comum de qualquer plataforma multi-tenant (N7), e existe por necessidade do modelo de vagas, não como conveniência.

**Cancelamento.** `DELETE .../members/{memberId}/invite` leva ao estado **`Revoked`**, libera a vaga, desabilita o usuário no Keycloak **e revoga as sessões dele** (v2.6). Só é aceito em `Invited`. A v2.5 dizia que não havia sessão a revogar, porque o usuário nascia desabilitado; ele nasce habilitado, e pode ter definido a senha pelo link antes de o aceite chegar à Gateway pela sincronização do ADR-007. Desabilitado, ele também deixa de conseguir usar um link de ações ainda válido.

> **Por que `Revoked` e não `Expired` ou `Erased`.** Os três são saídas sem aceite, mas significam coisas diferentes para a auditoria: `Expired` é decurso de prazo, `Revoked` é ação humana deliberada do administrador, e `Erased` é apagamento LGPD com anonimização do `ExternalUserId` (§9.5). Colapsá-los perderia a distinção entre "o prazo venceu" e "o admin cancelou" — e aplicar a semântica de anonimização a quem nunca existiu como pessoa ativa seria forte demais.

De `Expired` e `Revoked` um novo convite é um **novo membro**, com nova reserva de vaga. Ambos admitem exclusão definitiva (§9.5), que os leva a `Erased`.

### 9.6. Permissões finas numa API consumidora

1. A requisição chega com o JWT, que é validado localmente.
2. O endpoint exige a permissão `invoices:approve`.
3. A biblioteca `IdentityGateway.Client.AspNetCore` busca as permissões efetivas de (`sub`, `tenant_id`) no cache local. Se não houver entrada, consulta a Gateway uma vez e guarda o resultado com TTL curto.
4. O evento `PermissionsChanged`, recebido via RabbitMQ, invalida a entrada correspondente.
5. Se a Gateway estiver indisponível e não houver cache, a decisão é **negar** (fail closed) — mas a resposta é **503 com `Retry-After`**, não 403.

**Por que 503 e não 403.** Responder 403 a uma falha de dependência faz o sintoma apontar para o lugar errado: o usuário lê "você não tem permissão", o suporte investiga papéis e o plantão procura uma mudança de autorização que não houve. O 503 distingue *"negado"* de *"não sei"*, e permite retry do cliente.

**Degradação e warm-up.** Para que o cenário acima seja raro, o pacote cliente serve cache vencido enquanto a Gateway está fora (*stale-while-revalidate*, com teto de idade configurável) e a Resource API só entra em rotação — health `ready` — depois de falar com a Gateway ao menos uma vez. O `Client.AspNetCore` registra esse health check por conta própria: `ready` só reporta saudável depois de resolver permissões com sucesso ao menos uma vez desde a subida. O cache é `HybridCache` — L1 em memória, L2 em Redis —, de modo que uma instância nova encontra o L2 já quente e o warm-up é uma ida ao Redis, não à Gateway. Sem o warm-up, cada instância nova entra servindo 503 até aquecer, e um deploy coordenado transforma isso em indisponibilidade de todos os endpoints de nível 2 por alguns segundos.

---

## 10. Segurança

### 10.1. Autenticação e autorização da própria Gateway

A Gateway é o alvo mais valioso do sistema: quem a controla cria administradores em qualquer tenant. Por isso ela recebe o mesmo rigor que exige das outras APIs.

- Aceita apenas tokens do realm `identity-gateway` com audiência `identity-gateway-api`. **O `aud` vem do client scope `gateway-api`, que fica fora dos defaults do realm** (v2.7): só os clients que falam com a Gateway o recebem. Num scope default, todo client do realm — o service account, os clients de tenant do M6, qualquer client de teste — emitiria token aceito; foi reproduzido ao vivo, com um token ROPC de um client criado pela Admin API.
- **O que a validação confere (v2.7), na autenticação:** assinatura RS256, e só RS256, com as chaves lidas dos metadados pelo endereço interno; emissor igual ao **emissor público** do realm, por igualdade ordinal, num `IssuerValidator` próprio, porque o `ValidIssuer` não restringe (§12.1); audiência; validade, com `ClockSkew` de 30 s; o **`azp`** numa lista de clients permitidos por ambiente (`Keycloak:Auth:AllowedClients`); o claim **`typ`** igual a `Bearer`, porque o cabeçalho não distingue access token de ID token; e o **`sub`** como GUID no formato `D`. As três últimas ficam em `OnTokenValidated`, e não numa policy: a policy padrão não se soma a uma policy nomeada, e uma rota com `PlatformAdmin` escaparia. `IncludeErrorDetails` é falso em todo ambiente, porque o `WWW-Authenticate` ecoaria o `iss` e o `aud` recusados, que vêm do token; o diagnóstico sai pelo log (§11.8).
- **A lista de `azp` é estática e fica fora do `appsettings.json` base** (v2.7): o `IConfiguration` mescla arrays por índice, e um item do arquivo base sobreviveria à configuração de produção. O `identity-gateway-demo` só entra em Development, e a subida recusa a lista que o contenha fora dele. Lista vazia é permitida e fail-closed: a API sobe, registra um aviso e recusa todo token de usuário. A lista vale para os clients interativos que chamam a Gateway; os clients M2M de tenant não entram nela (abaixo).
- **`401` e `403` em Problem Details (v2.7).** Um `IAuthorizationMiddlewareResultHandler` escreve um Problem Details fixo para cada status, com o `correlationId`. No `401`, mantém o `WWW-Authenticate` do desafio padrão; no `403`, `type`, `title` e `detail` são constantes, sem nada que distinga o motivo da negação.
- **Quatro** famílias de policies: `PlatformAdmin`; `TenantAdmin`; `StepUp`, para operações destrutivas; e escopos de client, tratados abaixo.
- **`TenantAdmin` é a conjunção de quatro requirements** (v2.7) (D2, planejado): papel `tenant-admin` ∧ **não** `platform-admin` ∧ mesmo tenant ∧ `Member` do tenant da rota no banco, em `{Invited, Active}` (ADR-011). Cada um chama `Fail()` em todo caminho que não é sucesso. `InvokeHandlersAfterFailure` é falso, e o handler da pertença só consulta o banco para quem já passou nas três camadas do token — assim o tempo de resposta não vira oráculo do vínculo entre `sub` e tenant. Negar quem acumula `platform-admin` é separação de funções: o `manage-users` do service account atribui esse papel (§10.2), e sem a negação o desvio do platform-admin na leitura de tenant (abaixo) só valeria para a conta que não acumula papéis.
- **`SameTenantRequirement`** compara o `tenantId` da rota com o claim `tenant_id` do token, e **nega explicitamente** (`context.Fail()`) em todo caminho de rejeição — inclusive quando o claim está ausente ou o recurso não é `HttpContext`. Um requirement que apenas deixa de dar `Succeed` não é fail-closed: qualquer outro handler registrado para o mesmo requirement ainda pode satisfazê-lo, e só `Fail()` sobrevive a um `Succeed` alheio. **Na v2.7, a comparação é por `Guid`, e o claim precisa ter valor único** (D2, planejado): há exatamente um claim `tenant_id`, ele é um GUID no formato `D`, o valor da rota é um GUID, e os dois são iguais como `Guid` — a rota com o GUID em maiúsculas é o mesmo tenant, e um token com dois `tenant_id` é recusado, qualquer que seja a ordem.
- **Pertencimento de sub-recurso** (§6.4) é a segunda regra de isolamento, independente da primeira: verificar o tenant da rota não impede que um ator do tenant A informe o id de um recurso do tenant B.
- **`RoleAssignmentPolicy`** impede que um tenant-admin conceda papéis acima do próprio.
- **Override do `platform-admin`: só leitura de tenant, e auditado** (I-3) — **adiado, e como policy própria (v2.7, errata E3).** Até a v2.6, este item dizia que um `PlatformAdminOverrideHandler` satisfazia o `SameTenantRequirement` em `GET /tenants` e `GET /tenants/{tenantId}`. Nunca funcionaria: o `Fail()` do requirement veta qualquer `Succeed`, e a correção "natural" seria afrouxar o `Fail()`. O override passa a ser uma policy própria, **`TenantReadAccess`**, com **um** handler que decide os dois caminhos — platform-admin, com a auditoria gravada fora do handler; ou tenant-admin ∧ ¬platform-admin ∧ mesmo tenant ∧ `Member` —, e é entregue junto com a tabela de auditoria. O `SameTenantRequirement` e a `TenantAdmin` mantêm o `Fail()` incondicional. **Até lá, o platform-admin recebe `403` em `GET /tenants/{tenantId}`**, um desvio declarado em relação ao catálogo da §8: um override sem auditoria seria o único acesso cruzado entre tenants do produto sem trilha. Nas rotas internas do tenant — membros, clients, permission sets, domínios e IdPs — ele não terá acesso, com ou sem o override.

  > O limite é o que sustenta a decisão 1 do brainstorm. Se o platform-admin pudesse operar dentro do tenant, `initialAdminEmail` perderia a razão de existir — o argumento de C9 é precisamente que ele **não** satisfaz a verificação de tenant nas rotas de membros, e por isso o tenant precisa nascer com um admin próprio. Um override amplo reintroduziria a exceção de isolamento que a decisão 1 eliminou.

#### Step-up em operações destrutivas

Exclusão definitiva de membro, rotação de credenciais e encerramento de tenant exigem `acr` de nível elevado. O `StepUpRequirement` compara o claim `acr` com o nível mínimo declarado para a operação.

**Quando o nível falta, a resposta não é 403.** A API devolve `401` com o header `WWW-Authenticate` contendo `error="insufficient_user_authentication"` e o `acr_values` exigido, instruindo o cliente a refazer a autenticação com o nível necessário. Responder 403 deixaria o usuário sem caminho de recuperação: ele *pode* executar a operação, bastando reautenticar-se mais forte.

O nível é derivado do `auth_time` **da sessão**, não da operação — um step-up recente vale para as operações seguintes dentro da janela configurada no realm.

#### Clients de plataforma × clients de tenant

Os escopos de client precisam de um modelo de confiança próprio, porque **um token de Client Credentials não carrega `tenant_id`** — e, portanto, o `SameTenantRequirement` não tem o que comparar.

| Tipo | Emissão | Escopos | Isolamento |
|---|---|---|---|
| **Client de tenant** | `POST /tenants/{tenantId}/clients`, pelo tenant-admin | `gateway.members.write` | Recebe o atributo `tenant_id` do tenant que o criou, emitido como claim plano — **só se o adaptador do M6 anexar o scope `gateway-tenant` ao client** (v2.7): o scope não é default do realm (§12.2). Sujeito ao `SameTenantRequirement` como qualquer ator |
| **Client de plataforma** | Provisionado fora da API, no bootstrap | `gateway.permissions.read` | **Acesso irrestrito por desenho** — é a credencial que as Resource APIs usam para resolver permissões de qualquer tenant (§9.6). Tratado como credencial de infraestrutura: `private_key_jwt` obrigatório, rotação documentada e **auditoria por chamada** |

Essa distinção é necessária porque a Resource API serve todos os tenants: exigir dela um token com `tenant_id` quebraria o fluxo 9.6, e não exigir nada de um client de tenant permitiria que ele lesse a governança de qualquer outro. O anti-pattern 7 (§17) vale para atores de tenant; o client de plataforma é a exceção nomeada, e é auditada justamente por sê-lo.

**A lista de `azp` não cobre clients de tenant (v2.7).** A lista de clients permitidos é estática e vale para os clients interativos que chamam a Gateway. Um client M2M de tenant não entra nela e não recebe o scope `gateway-api`: o token dele não é aceito pela Gateway. Um client de tenant chamando a Gateway, no M6, exige decisão nova — uma consulta ao banco de "este client pertence a este tenant", nunca um prefixo de nome. Fora de Development, a lista fica vazia até existir um client administrativo.

### 10.2. Credenciais da Gateway no Keycloak

- A Gateway usa um client confidencial próprio (`identity-gateway`), autenticado por `private_key_jwt`. A chave privada chega como **arquivo montado**: do cofre de segredos em produção, e de um volume gerado na subida no ambiente local (§15).

**Onde os segredos vivem.** `User Secrets` no ambiente de desenvolvimento; **arquivo montado ou variáveis de ambiente injetadas pelo orquestrador** em qualquer outro ambiente. **A abstração é o `IConfiguration`** (v2.4): Key Vault, Secrets Manager e similares entram como *configuration providers*, e trocar de provedor não toca em código de aplicação — um projeto de portfólio não deve se amarrar a um provedor de nuvem específico. A v2.3 pedia "uma abstração própria"; uma interface a mais só repetiria o que o `IConfiguration` já é. O caminho de arquivo (`PrivateKeyPath`) é o preferido, porque variável de ambiente aparece em `docker inspect`. As options que carregam a chave são `class`, não `record` — o `ToString()` gerado de um record a imprimiria —, e a chave é importada uma vez na subida, sem que a string fique guardada. Nenhum segredo entra em arquivo versionado: um teste de CI falha se o JSON de bootstrap contiver credencial literal (§15).

#### Mecanismo do client assertion (A9)

O `private_key_jwt` é montado com `JsonWebTokenHandler` do `Microsoft.IdentityModel.JsonWebTokens` — o mesmo stack já usado na validação de tokens, sem dependência adicional. O assertion é um JWT curto assinado com a chave privada da Gateway, e a requisição ao token endpoint leva dois parâmetros:

| Parâmetro | Valor |
|---|---|
| `client_assertion_type` | `urn:ietf:params:oauth:client-assertion-type:jwt-bearer` (fixo, RFC 7523) |
| `client_assertion` | o JWT assinado |

Os claims do assertion:

| Claim | Valor |
|---|---|
| `iss` | o `clientId` da Gateway |
| `sub` | o `clientId` da Gateway (igual a `iss`) |
| `aud` | o **emissor público** do realm (`{PublicBaseUrl ?? BaseUrl}/realms/{realm}`, `KeycloakAdminOptions.AssertionAudience`), como **valor único** (v2.6) |
| `jti` | GUID novo a cada assertion — o Keycloak o exige e o recusa se reusado |
| `iat`, `nbf`, `exp` | **explícitos**: agora, agora e agora + 60s, por um `TimeProvider` |

```csharp
var agora = timeProvider.GetUtcNow().UtcDateTime;

var descriptor = new SecurityTokenDescriptor
{
    Issuer = clientId,
    Audience = assertionAudience,      // emissor PÚBLICO (v2.6); string única; nunca também em Claims["aud"]
    Claims = new Dictionary<string, object>
    {
        ["sub"] = clientId,
        ["jti"] = Guid.NewGuid().ToString()
    },
    IssuedAt = agora,
    NotBefore = agora,
    Expires = agora.AddSeconds(60),    // o padrão da biblioteca é 60 MINUTOS
    // RsaSecurityKey SEM KeyId: nenhum `kid` sai no header (ver abaixo).
    SigningCredentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSsaPssSha256)
};

var assertion = new JsonWebTokenHandler().CreateToken(descriptor);
```

**Três armadilhas, todas verificadas no código do Keycloak 26.7.4 (v2.4):**

- **`kid`.** O `JsonWebTokenHandler` põe no header o `KeyId` da chave. Com `X509SecurityKey` ele é o thumbprint SHA-1 do certificado, e o Keycloak espera `Base64Url(SHA-256(SubjectPublicKeyInfo))` — o resultado é `invalid_client` sem explicação útil. **Sem `kid`, o Keycloak usa o certificado padrão do client**; por isso a chave é `RsaSecurityKey` sem `KeyId`, e um teste afirma que o header não traz `kid`. É este, e não o `aud`, o erro mais provável.
- **Vida do assertion.** "Preenchidos pelo `JsonWebTokenHandler`", como a v2.3 dizia, significa **60 minutos**. O Keycloak aceitaria mesmo assim — ele confere só a idade a partir do `iat` (60s por padrão, com 15s de tolerância) —, e um assertion vazado valeria uma hora contra qualquer outro verificador. Os três tempos são explícitos.
- **`aud` e reuso.** A v2.3 afirmava que o issuer produzia `invalid_client`: é falso. O issuer sempre foi aceito, e desde a 26.2 é o **recomendado**; o que falha é `aud` com mais de um valor. E como o `jti` é de uso único, **nenhum retry pode reenviar o mesmo assertion**: o `HttpClient` do token endpoint não tem política de retry, e cada tentativa gera um assertion novo.

**Emissor público e transporte (errata da v2.6).** O Keycloak compara o `aud` com o emissor do realm por igualdade exata de texto, e o emissor é a URL de **frontend**: com `KC_HOSTNAME` definido, o endereço público, mesmo que a requisição chegue pelo endereço interno. A v2.5 dizia que, em produção, o `BaseUrl` precisava ser igual ao `KC_HOSTNAME`; isso só valeria se a Gateway chamasse o Keycloak pelo endereço público. A regra certa: **o `aud` é o emissor público, e o transporte pode ser o endereço interno.** `KeycloakAdminOptions.PublicBaseUrl` — opcional, absoluta, sem query nem fragmento, nunca discada — alimenta só `AssertionAudience = {PublicBaseUrl ?? BaseUrl}/realms/{Realm}`; o token endpoint e a Admin API continuam derivados **só** do `BaseUrl`. Omitido, vale o `BaseUrl`, e nada muda para quem roda a API pela IDE com `BaseUrl=http://localhost:8081`. `127.0.0.1` no lugar de `localhost` quebra o `aud`, porque a comparação é por texto (`Invalid token audience`). O `ValidateOnStart` recusa `BaseUrl` sem https fora de `Development`, e **`AllowInsecureHttp` passa a ser recusado fora de `Development`** (v2.6): "transporte interno" não pode virar convite a `http` em produção com um bearer de `manage-users`.

**O mesmo emissor público é o emissor aceito nos tokens (v2.7).** O `PublicBaseUrl` deixa de alimentar só o `aud` do assertion: `KeycloakAdminOptions.Issuer = {PublicBaseUrl ?? BaseUrl}/realms/{Realm}` é a propriedade única, `AssertionAudience` passa a ser `=> Issuer`, e o adaptador a copia para `AccessTokenValidationOptions.Issuer`, que a Api usa para validar o `iss` dos access tokens (§11.8). É uma derivação só, para as duas contas não divergirem. O `PublicBaseUrl` continua nunca discado: os metadados e as chaves são lidos pelo `BaseUrl`.

Do lado do Keycloak, o client é configurado com **Signed JWT** (`clientAuthenticatorType: "client-jwt"`), algoritmo fixado em **PS256** (`token.endpoint.auth.signing.alg`) e a chave pública registrada como **certificado** no atributo `jwt.credential.certificate` (DER em base64). A v2.3 admitia também JWKS publicado pela Gateway: ele continua sendo a evolução para rotação sem downtime (§19), mas não serve ao ambiente de testes, em que a API roda em memória e o container do Keycloak não a alcança.

- O service account recebe **exatamente** dois papéis do client `realm-management` (v2.6): `manage-organizations` (novo na 26.7.0; cobre criar, listar e ler Organizations — `view-realm` não lê) e `manage-users`, que o convite exige — vincular um usuário a uma Organization pede **as duas** permissões. Um teste de integração compara os papéis **efetivos**, com os compostos expandidos, sem depender de ordem, e afirma a ausência de `impersonation`, `realm-admin`, `manage-realm`, `manage-clients` e `manage-identity-providers`. **Raio de dano:** `manage-organizations` permite alterar e apagar *qualquer* Organization do realm, inclusive reescrever `gateway_tenant_id` e domínios. `manage-users` permite atribuir qualquer papel que não seja de administração — **inclusive `platform-admin`** — e os de administração que o próprio service account tem, trocar senhas e desabilitar usuários, inclusive platform-admins. Quem tiver a chave da Gateway pode criar uma conta própria com esses papéis, e esse acesso sobrevive à rotação da chave (§19). As permissões granulares (v2) não recortam "só os usuários das Organizations da Gateway", e o M2 precisa de `manage-users` de qualquer forma. Cada papel novo continua entrando com o teste que o justifica.
- A Gateway nunca usa o realm `master`. **Quem usa, no ambiente local, é o one-shot `platform-admin-invite` do compose** (v2.7): ele faz login com o admin do `master` para enviar o convite do primeiro platform-admin (§15). Usar a chave da Gateway ali seria pior: o `manage-users` dela atribuiria `platform-admin`, e a criação da conta de plataforma apareceria nos eventos de administração como obra da Gateway.
- **O raio de dano do `manage-users` é maior do que a v2.6 nomeava (v2.7).** Ele gerencia grupos e mapeia neles qualquer papel que não seja de administração: quem tem a chave da Gateway fabrica um `tenant_id` por grupo, que o mapper do claim aceita (§12.2), e toma a conta de um `Member` real, trocando a senha ou o e-mail dele. O primeiro caminho é o que o ADR-011 fecha na Gateway; o segundo, nenhuma checagem de pertença fecha (§19).

### 10.3. Proteções gerais

- **Rate limiting** nativo do ASP.NET Core, particionado por tenant, com limite mais restrito no discovery.
- **Idempotência:** POSTs com `Idempotency-Key` guardam, por 24 horas, **apenas** `(chave, status, Location/identificador do recurso)` — nunca o corpo da resposta. O store é compartilhado (tabela no PostgreSQL, índice em `(key, expires_at)`, job de limpeza), porque um store em memória não protegeria nada num deploy multi-réplica: a chave gravada numa instância não alcançaria as demais.
  - **Nenhum valor de credencial entra no store de idempotência, na auditoria ou no log.** A criação e a rotação de client devolvem o `client_secret` no corpo; guardá-lo faria a Gateway persistir por 24h exatamente o segredo que o anti-pattern 3 (§17) diz não ser guardado, e tornaria falso o "devolvido uma única vez" — a `Idempotency-Key` é escolhida pelo cliente e circula em logs de proxy e coleções de API. No replay dessas rotas, a resposta é `200` sem o campo sensível, indicando que o recurso já existe.
- **Auditoria:** tabela *append-only* com ator (`sub`), tenant, ação, alvo, resultado e `correlationId`. Os eventos administrativos do Keycloak são guardados com retenção configurada, e ficam **ligados no realm** (`adminEventsEnabled: true`, v2.6), para que as atribuições de papel feitas pelo service account fiquem registradas.
- **Validação de entrada** com Notification Pattern, devolvendo todos os erros de uma vez em Problem Details.
- **Transporte:** HTTPS obrigatório, HSTS e CORS apenas para uma lista explícita de origens.
- **Tokens:** access token de 5 minutos (`accessTokenLifespan: 300`, explícito no realm), refresh token com rotação e *backchannel logout* habilitado. **A rotação está entregue desde a v2.7:** `revokeRefreshToken: true` e `refreshTokenMaxReuse: 0`. Um refresh token já usado é recusado, e **reusá-lo derruba a sessão inteira daquele client**: depois do reuso, até o refresh token novo é recusado, por desenho do Keycloak. Quem renova guarda o refresh token novo antes de qualquer outro passo e nunca repete uma renovação; se ela falhar, o caminho é um login novo. O refresh token vale 30 minutos de inatividade, o padrão do realm.
- **O token não carrega e-mail nem nome (v2.7).** Os clients que chamam a Gateway não recebem os scopes `profile` nem `email`, e a validação usa `NameClaimType = "sub"`: o username é o e-mail, e `preferred_username` o levaria a logs, a histórico de shell e a proxies. O teste de vazamento do e-mail cobre tokens forjados que carreguem os dois claims (§13).
- **Cadastro fechado e força bruta (v2.7).** `registrationAllowed: false` e `bruteForceProtected: true`, explícitos no realm e conferidos pelo `RegrasDoRealmTests`.
- **LGPD:** dados pessoais somente no Keycloak; a Gateway guarda vínculos e governança. **Exceção declarada (v2.6):** o e-mail do admin inicial fica na coluna `tenants.initial_admin_email` enquanto o tenant está `Pending`, e é apagado na transação que o ativa ou que o marca `ProvisioningFailed` (§6, §9.1). Nenhuma resposta nem read model o expõe, inclusive o `GET .../provisioning`. **Retenção real:** o apagamento é lógico; WAL, *dead tuples* e backups guardam o valor pela retenção deles (§19). Postgres, Redis e Seq publicam só em `127.0.0.1` no compose (§15).
- **O e-mail não vaza por log, exceção, resposta nem coluna de erro, inclusive em Development (v2.6).** `Email.ToString()` não devolve o endereço (quem precisa do valor usa `Email.Value`); `InviteData` sobrescreve o `ToString` (§11.3); `DomainErrors.Email.Invalido` não ecoa o valor; e as exceções do adaptador levam o `tenantId`, nunca o e-mail — o `OutboxProcessor` grava a mensagem da exceção em `outbox_messages.error`. Em Development, `Database:EnableSensitiveDataLogging` fica `false` e a categoria `Microsoft.EntityFrameworkCore` sobe para `Warning`: sem isso, o EF registraria os parâmetros de um comando que falha e, no caminho feliz, o valor antigo da coluna ao apagá-la. O validador do `POST` e `Email.Of` usam a mesma regra: parte local de até 64 caracteres em `[a-z0-9._+-]` e domínio com rótulos `[a-z0-9-]`. Um teste com todas as categorias em `Trace` e um exporter OpenTelemetry em memória prova a regra (§13).
- **Sem "esqueci a senha" (v2.6).** `resetPasswordAllowed: false` explícito no realm: o convidado nasce habilitado (§9.9), e o reset de senha daria acesso a ele fora do ciclo do convite.

---

## 11. Código de referência

Os exemplos abaixo fixam as convenções. Tipos auxiliares como `Result`, `Error`, `ICommandHandler` e `AggregateRoot` seguem os do CleanStart.

### 11.1. Domain: aggregate `Tenant`

```csharp
namespace IdentityGateway.Domain.Tenants;

/// <summary>
/// Aggregate root do tenant. Guarda somente dados de governança;
/// identidade e credenciais vivem no Keycloak.
/// </summary>
public sealed class Tenant : AggregateRoot<TenantId>
{
    public string Name { get; private set; } = default!;
    public TenantSlug Slug { get; private set; } = default!;      // alias da Organization no Keycloak
    public Plan Plan { get; private set; } = default!;
    public TenantStatus Status { get; private set; }
    public string? ExternalOrganizationId { get; private set; }  // preenchido após o provisionamento
    public int OccupiedSeats { get; private set; }               // protegido por concorrência otimista
    public bool OverSubscribed { get; private set; }             // só a absorção externa (9.4) liga isto
    public DateTimeOffset RegisteredAt { get; private set; }     // de onde conta a janela de provisionamento (v2.5)
    public Email? InitialAdminEmail { get; private set; }        // só em Pending; apagado na ativação ou na falha (v2.6)

    public bool HasSeatAvailable => OccupiedSeats < Plan.MaxUsers;  // o handler consulta antes do Keycloak (v2.6)

    private Tenant() { } // exigido pelo EF Core

    public static Tenant Register(
        string name, TenantSlug slug, Plan plan, Email initialAdminEmail, DateTimeOffset registeredAt)
    {
        var tenant = new Tenant
        {
            Id = TenantId.New(),
            Name = name,
            Slug = slug,
            Plan = plan,
            Status = TenantStatus.Pending,
            // Exceção declarada à regra da §6 (v2.6): o e-mail vive aqui só enquanto o tenant está Pending,
            // e nunca no evento — que iria ao Outbox e, com o broker, ao RabbitMQ.
            InitialAdminEmail = initialAdminEmail,
            RegisteredAt = registeredAt.ToUniversalTime()
        };

        // O evento vira mensagem no Outbox; é ele que dispara o provisionamento. Continua sem o e-mail.
        tenant.Raise(new TenantRegistered(tenant.Id, slug));
        return tenant;
    }

    /// <summary>
    /// Conclui o provisionamento (v2.6): ativa, reserva a vaga do admin inicial, cria o Member em Invited e
    /// apaga o e-mail — numa operação só, gravada num commit só. Substitui o MarkProvisioned da v2.5.
    /// </summary>
    public Member CompleteProvisioning(
        string externalOrganizationId, ExternalUserId adminUserId, DateTimeOffset invitedAt)
    {
        // Valida TUDO antes de mudar qualquer coisa: uma exceção no meio não pode deixar o agregado pela metade.
        // Recusa Active e ProvisioningFailed — o inverso do MarkProvisioned da v2.5, que aceitava os dois.
        EnsureStatusIn(TenantStatus.Pending);

        // O handler já verificou a vaga antes de tocar o Keycloak; faltar aqui é erro de programação.
        if (!HasSeatAvailable)
            throw new DomainInvariantViolation($"Tenant {Id}: sem vaga para o admin inicial.");

        ExternalOrganizationId = externalOrganizationId;
        Status = TenantStatus.Active;
        Raise(new TenantActivated(Id));

        OccupiedSeats++;                  // não passa por ReserveSeat, que continua exigindo Active
        InitialAdminEmail = null;         // o dado pessoal sai na mesma transação que ativa

        // Fábrica internal: nenhum membro nasce sem a vaga reservada.
        return Member.Invite(Id, adminUserId, invitedAt);
    }

    /// <summary>
    /// Marca a falha do provisionamento. Também apaga o e-mail (v2.6): este estado não tem saída automática,
    /// e o retry manual recebe o e-mail de novo.
    /// </summary>
    public void MarkProvisioningFailed()
    {
        if (Status == TenantStatus.ProvisioningFailed)
            return;

        EnsureStatusIn(TenantStatus.Pending);

        Status = TenantStatus.ProvisioningFailed;
        InitialAdminEmail = null;
    }

    /// <summary>
    /// Reserva uma vaga do plano. Duas reservas concorrentes geram conflito de
    /// versão no SaveChanges (xmin); o retry que reprocessa com o valor atual está
    /// no pipeline de comandos (seção 11.10), não aqui — o aggregate não sabe de
    /// persistência.
    /// </summary>
    public Result ReserveSeat()
    {
        if (Status != TenantStatus.Active)
            return TenantErrors.NotActive(Id);

        if (OccupiedSeats >= Plan.MaxUsers)
            return TenantErrors.SeatLimitReached(Plan.MaxUsers);

        OccupiedSeats++;
        return Result.Success();
    }

    /// <summary>
    /// Libera uma vaga. NÃO é idempotente por desenho: deve ser chamado apenas como
    /// consequência de uma transição de estado que de fato ocorreu.
    /// </summary>
    /// <remarks>
    /// A v2.0 usava Math.Max(0, OccupiedSeats - 1). Isso não protegia nada: o clamp
    /// apenas escondia a divergência, impedindo o valor negativo que denunciaria uma
    /// dupla liberação. Um POST de desativação repetido (timeout + retry do cliente)
    /// decrementava duas vezes, e o xmin não detecta — concorrência otimista pega
    /// escrita simultânea, não repetida. Repetido N vezes, o contador chegava a zero
    /// com o tenant cheio, e o limite do plano deixava de existir.
    ///
    /// Agora quem chama é o handler, e só quando Member.Deactivate() reporta que a
    /// transição aconteceu. A exceção é falha de invariante, não erro de negócio:
    /// se disparar, há um chamador errado, e o job de reconciliação de vagas
    /// (seção 9.1) é a rede que detecta divergência já instalada.
    /// </remarks>
    public void ReleaseSeat()
    {
        if (OccupiedSeats == 0)
            throw new DomainInvariantViolation($"Tenant {Id}: liberação de vaga sem vaga ocupada.");

        OccupiedSeats--;
    }
}
```

O `Member` entregue na v2.6 é o mínimo que o convite do admin exige (§6.1):

```csharp
namespace IdentityGateway.Domain.Members;

/// <summary>
/// Vínculo de uma pessoa com um tenant. O papel vive no Keycloak (ADR-005) e não é duplicado aqui nesta versão.
/// </summary>
public sealed class Member : AggregateRoot<MemberId>, IAuditable
{
    // Um construtor só, usado pela fábrica e pelo EF (todos os parâmetros são conversões de valor único).
    private Member(MemberId id, TenantId tenantId, ExternalUserId externalUserId, MemberStatus status, DateTimeOffset invitedAt)
        : base(id)
    {
        TenantId = tenantId;
        ExternalUserId = externalUserId;
        Status = status;
        InvitedAt = invitedAt;
    }

    public TenantId TenantId { get; private set; }
    public ExternalUserId ExternalUserId { get; private set; }   // o sub
    public MemberStatus Status { get; private set; }             // persistido como texto
    public DateTimeOffset InvitedAt { get; private set; }        // UTC
    // CreatedAt, UpdatedAt, CreatedBy e UpdatedBy (IAuditable), preenchidos pelo interceptor de auditoria.

    // internal: só Tenant.CompleteProvisioning a chama, para que nenhum membro nasça sem a vaga reservada. Um teste de
    // arquitetura garante que não há construtor nem fábrica públicos.
    internal static Member Invite(TenantId tenantId, ExternalUserId externalUserId, DateTimeOffset invitedAt)
    {
        if (tenantId.Value == Guid.Empty) throw new ArgumentException("O membro precisa de um tenant.", nameof(tenantId));
        ArgumentNullException.ThrowIfNull(externalUserId);
        return new Member(MemberId.New(), tenantId, externalUserId, MemberStatus.Invited, invitedAt.ToUniversalTime());
    }
}
// Identidade tipada, como TenantId: os ArchitectureTests reprovam Guid cru como identidade de raiz.
public readonly record struct MemberId(Guid Value)
{
    public static MemberId New() => new(Guid.CreateVersion7());
}

public enum MemberStatus { Invited, Active, Deactivated, Expired, Revoked, Erased }
```

### 11.2. Domain: regra contra escalação de privilégio

```csharp
namespace IdentityGateway.Domain.Members;

public static class RoleAssignmentPolicy
{
    /// <summary>
    /// Um ator só atribui papéis dentro do próprio tenant e nunca acima
    /// do seu papel mais alto. platform-admin não é atribuível pela API.
    /// </summary>
    public static Result CanAssign(Actor actor, TenantId targetTenant, IReadOnlySet<RoleName> requested)
    {
        if (!actor.IsPlatformAdmin && actor.TenantId != targetTenant)
            return AuthorizationErrors.CrossTenant;

        if (requested.Contains(RoleName.PlatformAdmin))
            return AuthorizationErrors.RoleNotAssignable(RoleName.PlatformAdmin);

        var aboveCeiling = requested.Where(r => r.Rank > actor.HighestRole.Rank).ToList();

        return aboveCeiling.Count == 0
            ? Result.Success()
            : AuthorizationErrors.EscalationAttempt(aboveCeiling);
    }
}
```

### 11.3. Application: porta de saída para o provedor de identidade

```csharp
namespace IdentityGateway.Application.Common.Abstractions;

/// <summary>
/// Única forma de a Application falar com o provedor de identidade.
/// Nenhum tipo do Keycloak atravessa esta interface, e todas as operações
/// são idempotentes ("garanta que", não "crie").
/// </summary>
/// <remarks>
/// A interface cresce por fatia: cada operação entra quando o caso de uso que a
/// chama entra, e não antes — método declarado sem implementação é contrato que mente.
/// </remarks>
public interface IIdentityProvider
{
    // O TenantId é obrigatório: é ele que vai no atributo gateway_tenant_id (§11.6).
    Task<string> EnsureOrganizationAsync(TenantId tenantId, TenantSlug slug, string name, CancellationToken ct);
    // v2.6: o TenantId vai no atributo tenant_id do usuário (§12.2) e correlaciona tentativas; o papel vai no InviteData.
    Task<ExternalUserId> EnsureInvitedUserAsync(
        string organizationId, TenantId tenantId, InviteData invite, CancellationToken ct);
    Task SetUserEnabledAsync(ExternalUserId userId, bool enabled, CancellationToken ct);
    Task RevokeSessionsAsync(ExternalUserId userId, CancellationToken ct);
    Task ReplaceRolesAsync(ExternalUserId userId, IReadOnlySet<RoleName> roles, CancellationToken ct);
    Task EraseUserAsync(ExternalUserId userId, CancellationToken ct);
}
```

**A assinatura de `EnsureInvitedUserAsync` diverge da v2.5 (v2.6).** Acrescenta o `TenantId`, que é a chave da correlação do usuário (atributo `tenant_id`, §12.2) — o mesmo movimento do item I1 da v2.4 para `EnsureOrganizationAsync` —, e o papel vai no `InviteData`, porque escolher o papel é regra de negócio, e o M2 convida com outros papéis. O contrato de erro é o de `EnsureOrganizationAsync`: só `IdentityProviderInconsistencyException` é permanente, o resto é infraestrutura, e nenhuma exceção carrega o e-mail.

```csharp
namespace IdentityGateway.Application.Common.Abstractions;

public sealed record InviteData(Email Email, RoleName Role, TimeSpan LinkLifetime)
{
    // O ToString gerado do record imprimiria o e-mail em qualquer log que o interpolasse (§10.3).
    public override string ToString() => $"InviteData {{ Role = {Role}, LinkLifetime = {LinkLifetime} }}";
}

/// <summary>
/// Prazo do link de convite. Global nesta versão (Invitations:LinkLifetime, padrão 7.00:00:00, positivo, em
/// segundos inteiros e no máximo 30 dias, validado na subida); por tenant no M2, pela mesma porta.
/// </summary>
public interface IInvitationPolicy
{
    TimeSpan LinkLifetime { get; }
}
```

### 11.4. Application: command de registro de tenant

```csharp
namespace IdentityGateway.Application.Tenants.Commands;

// v2.6: o e-mail do admin inicial entra no command e fica no tenant até a ativação (§9.1). Não vai ao evento.
public sealed record RegisterTenantCommand(
    string Name, string Slug, string PlanCode, string InitialAdminEmail) : ICommand<TenantId>;

internal sealed class RegisterTenantHandler(
    ITenantRepository tenants,
    IPlanCatalog plans,
    IDateTimeProvider relogio) : ICommandHandler<RegisterTenantCommand, TenantId>
{
    public async ValueTask<Result<TenantId>> Handle(RegisterTenantCommand command, CancellationToken ct)
    {
        var slug = TenantSlug.Create(command.Slug);
        if (slug.IsFailure)
            return slug.Error;

        if (await tenants.SlugExistsAsync(slug.Value, ct))
            return TenantErrors.SlugInUse(slug.Value);

        var plan = plans.Find(command.PlanCode);
        if (plan is null)
            return TenantErrors.UnknownPlan(command.PlanCode);

        // A mesma regra do validador do POST (v2.6); o erro não ecoa o valor (§10.3).
        var email = Email.Of(command.InitialAdminEmail);
        if (email.IsFailure)
            return email.Error;

        var tenant = Tenant.Register(command.Name, slug.Value, plan, email.Value, relogio.UtcNow);
        tenants.Add(tenant);

        // Nenhuma chamada ao Keycloak aqui. O INSERT e o evento no Outbox são gravados na mesma transação,
        // pelo TransactionBehavior: se o Keycloak estiver fora do ar, nada fica órfão; o provisionamento
        // apenas acontece mais tarde.
        return tenant.Id;
    }
}
```

### 11.5. Application e Infrastructure: provisionamento idempotente

```csharp
// Application: a regra do provisionamento — inclusive a de desistir.
public sealed class ProvisionTenantHandler(
    ITenantRepository tenants,
    IMemberRepository members,
    IIdentityProvider identidade,
    IProvisioningPolicy politica,
    IInvitationPolicy convites,
    IDateTimeProvider relogio,
    ILogger<ProvisionTenantHandler> logger) : ICommandHandler<ProvisionTenantCommand>
{
    public async ValueTask<Result> Handle(ProvisionTenantCommand command, CancellationToken ct)
    {
        Tenant? tenant = await tenants.GetAsync(command.TenantId, ct);

        // Mensagem repetida, tenant removido, ou Failed (que só o retry manual desfaz): nada a fazer.
        if (tenant is null || tenant.Status != TenantStatus.Pending)
            return Result.Success();

        // Verificação prévia (v2.6): o que repetir não corrige sai antes de tocar o Keycloak.
        // Sem e-mail: tenant registrado antes da v2.6. Sem vaga: Plan com MaxUsers zero, de dado antigo.
        if (tenant.InitialAdminEmail is not { } email || !tenant.HasSeatAvailable)
        {
            tenant.MarkProvisioningFailed();       // também apaga o e-mail
            return Result.Success();
        }

        string organizationId;
        ExternalUserId adminUserId;
        try
        {
            // O mesmo try cobre as duas chamadas: a classificação de erro vale para as duas.
            organizationId = await identidade.EnsureOrganizationAsync(tenant.Id, tenant.Slug, tenant.Name, ct);
            adminUserId = await identidade.EnsureInvitedUserAsync(
                organizationId, tenant.Id, new InviteData(email, RoleName.TenantAdmin, convites.LinkLifetime), ct);
        }
        catch (IdentityProviderInconsistencyException)
        {
            tenant.MarkProvisioningFailed();       // permanente: não espera a janela
            return Result.Success();
        }
        catch (Exception) when (!ct.IsCancellationRequested
                                && relogio.UtcNow >= tenant.RegisteredAt + politica.MaxPendingDuration)
        {
            tenant.MarkProvisioningFailed();       // janela esgotada
            return Result.Success();
        }
        // Dentro da janela, a exceção sobe intacta, o tenant continua Pending e com o e-mail, e o transporte repete.

        Member admin = tenant.CompleteProvisioning(organizationId, adminUserId, relogio.UtcNow);
        members.Add(admin);
        return Result.Success();                   // o commit é do TransactionBehavior: tenant e Member juntos
    }
}

// Infrastructure: até a fatia do broker, o transporte é o próprio Outbox. Um escopo de DI por mensagem —
// sem ele, o SaveChanges que registra o resultado do lote gravaria o que um handler que falhou deixou rastreado.
internal sealed class DispatchingOutboxPublisher(IServiceScopeFactory scopeFactory) : IOutboxPublisher
{
    // Nulo = "sem consumidor nesta versão", dado como entregue. Fora do mapa: lança — nunca entrega em
    // silêncio um evento que ninguém decidiu o que fazer (§4.2).
    private static readonly Dictionary<Type, Func<IDomainEvent, ICommand?>> Destinos = new()
    {
        [typeof(TenantRegistered)] = e => new ProvisionTenantCommand(((TenantRegistered)e).TenantId),
        [typeof(TenantActivated)] = _ => null,
    };

    public async Task PublishAsync(IDomainEvent domainEvent, CancellationToken ct)
    {
        if (!Destinos.TryGetValue(domainEvent.GetType(), out var destino))
            throw new InvalidOperationException($"'{domainEvent.GetType().Name}' sem destino no mapa.");

        ICommand? comando = destino(domainEvent);
        if (comando is null) return;   // sem consumidor nesta versão

        await using AsyncServiceScope escopo = scopeFactory.CreateAsyncScope();
        Result resultado = await escopo.ServiceProvider.GetRequiredService<ISender>().Send(comando, ct);
        if (resultado.IsFailure) throw new InvalidOperationException(resultado.Error.Code);
    }
}
```

**Por que não MassTransit (v2.5).** A v2.4 desenhava retry e redelivery no MassTransit e um consumidor de `Fault` marcando `ProvisioningFailed`. Verificado em 2026-09-25: o v9 exige licença comercial para uso em produção, e o v8 tem suporte até o fim de 2026. A decisão de desistir foi para o handler — sobrevive a qualquer transporte — e a escolha da biblioteca fica para a fatia do broker.

**Verificação prévia, convite e ativação (v2.6).** O handler recusa, antes de tocar o Keycloak, o que repetir não corrige: tenant sem e-mail (registrado antes da v2.6) e tenant sem vaga (um `Plan` com `MaxUsers` zero, vindo de dado antigo — o catálogo passa a recusar `maxUsers < 1` na subida). Deixar a falta de vaga para `CompleteProvisioning` faria cada retry reenviar o convite até esgotar a janela. O mesmo `try` cobre as duas chamadas ao Keycloak, e a classificação não muda. Depois de uma falha transitória no convite, o tenant continua `Pending` e com o e-mail. `CompleteProvisioning` e `IMemberRepository.Add` gravam tenant e `Member` no mesmo commit.

**O e-mail de convite pode sair mais de uma vez (v2.6).** O Keycloak envia dentro da requisição do passo 5 (§11.6). Qualquer exceção depois dele e antes do commit — falha de commit, conflito de `xmin`, violação do índice único de `members` — faz a mensagem voltar, e a nova tentativa reenvia, porque o usuário ainda tem `UPDATE_PASSWORD` pendente. É coerente com a entrega "pelo menos uma vez" do Outbox, e está nos limites (§19).

### 11.6. Infrastructure: adaptador do Keycloak

```csharp
namespace IdentityGateway.Infrastructure.Identity.Keycloak;

internal sealed class KeycloakIdentityProvider(KeycloakAdminClient admin) : IIdentityProvider
{
    // Atributo que correlaciona a Organization ao tenant da Gateway. É a chave da
    // idempotência: estável, escolhida por nós e imune a rename de nome ou alias —
    // e distingue a nossa Organization de outra com o mesmo alias criada fora da Gateway.
    private const string TenantIdAttribute = "gateway_tenant_id";

    // Atributo de USUÁRIO que correlaciona a conta ao tenant (v2.6). É o mesmo que alimenta o claim tenant_id
    // (§12.2), e precisa estar declarado no User Profile do realm, senão o Keycloak o descarta em silêncio.
    private const string UserTenantAttribute = "tenant_id";

    private static readonly string[] InviteActions = ["UPDATE_PASSWORD", "VERIFY_EMAIL"];

    public async Task<string> EnsureOrganizationAsync(
        TenantId tenantId, TenantSlug slug, string name, CancellationToken ct)
    {
        // Procura antes de criar: uma tentativa anterior pode ter criado a
        // Organization e falhado antes de gravar o id no banco da Gateway.
        var existing = await admin.FindOrganizationByAttributeAsync(TenantIdAttribute, tenantId.Value, ct);
        if (existing is not null)
            return existing.Id;

        try
        {
            return await admin.CreateOrganizationAsync(
                new OrganizationRepresentation(
                    // Name é ÚNICO no realm; o nome do tenant não é. Dois tenants "Acme"
                    // (slugs acme e acme-sp) levariam o segundo a 409 para sempre. O slug
                    // já é único e imutável, então ocupa o Name; o nome de exibição vai
                    // em Description (até 4000 caracteres, devolvida em toda leitura).
                    Name: slug.Value,
                    Alias: slug.Value,
                    Description: name,
                    Enabled: true,
                    Attributes: new() { [TenantIdAttribute] = [tenantId.Value.ToString()] }),
                ct);
        }
        catch (KeycloakConflictException)
        {
            // Achou: duas entregas da mesma mensagem concorreram e a outra venceu.
            // Não achou: o 409 é de uma Organization que NÃO é deste tenant (mesmo
            // alias, criada fora da Gateway). Erro permanente — repetir não resolve.
            var created = await admin.FindOrganizationByAttributeAsync(TenantIdAttribute, tenantId.Value, ct);
            return created?.Id
                ?? throw new IdentityProviderInconsistencyException(
                    $"Alias '{slug.Value}' em uso por Organization não correlacionada ao tenant {tenantId}.");
        }
    }

    // v2.6: os cinco passos do convite, cada um idempotente. Nenhuma exceção leva o e-mail; todas levam o tenantId.
    public async Task<ExternalUserId> EnsureInvitedUserAsync(
        string organizationId, TenantId tenantId, InviteData invite, CancellationToken ct)
    {
        string email = invite.Email.Value;
        string tenant = tenantId.Value.ToString();

        // 1. Buscar: exact=true (sem ele, LIKE %x%) e briefRepresentation=false (explícito; a forma resumida não traz
        //    atributos). O e-mail vai escapado na query.
        IReadOnlyList<UserRepresentation> achados = await admin.FindUsersByEmailAsync(email, ct);
        UserRepresentation? user = achados.FirstOrDefault(u => PertenceAo(u, tenant));
        if (achados.Count > 0 && user is null)
            throw new IdentityProviderInconsistencyException(
                $"O e-mail do convite do tenant {tenantId.Value} pertence a um usuário não correlacionado.");

        // 2. Criar, se não achou: habilitado, com as duas ações e o tenant_id.
        if (user is null)
        {
            UserRepresentation novo = new(null, email, email, Enabled: true, [.. InviteActions],
                new() { [UserTenantAttribute] = [tenant] });
            try
            {
                user = novo with { Id = await admin.CreateUserAsync(novo, ct) };
            }
            catch (KeycloakConflictException)
            {
                // UMA nova busca, por e-mail e por username. Nunca em laço: o 409 também sai quando outra conta tem
                // username igual ao nosso e-mail e outro e-mail.
                user = (await admin.FindUsersByEmailAsync(email, ct))
                    .Concat(await admin.FindUsersByUsernameAsync(email, ct))
                    .FirstOrDefault(u => PertenceAo(u, tenant))
                    ?? throw new IdentityProviderInconsistencyException(
                        $"O e-mail do convite do tenant {tenantId.Value} pertence a um usuário não correlacionado.");
            }
        }

        // 3. Vincular à Organization. O 409 (já é membro) conta como sucesso. O vínculo do Keycloak é "consultar e
        //    depois inserir": de dois POST concorrentes, o perdedor recebe 400, não 409. No 400, UMA leitura da
        //    pertença, nunca em laço: membro, o passo está feito; senão, o 400 original sobe (transitório).
        try
        {
            await admin.AddOrganizationMemberAsync(organizationId, user.Id!, ct);
        }
        catch (HttpRequestException excecao) when (excecao.StatusCode == HttpStatusCode.BadRequest)
        {
            if (!await admin.IsOrganizationMemberAsync(organizationId, user.Id!, ct))
                throw;
        }

        // 4. Papel: o id vem das listas do próprio usuário (role-mappings/realm e .../available); GET /roles/{nome}
        //    exigiria view-realm. Ausente das duas listas: o realm não é o que a Gateway espera (permanente).
        if (!(await admin.GetUserRealmRolesAsync(user.Id!, ct)).Any(p => p.Name == invite.Role.Value))
        {
            RoleRepresentation? papel = (await admin.GetAvailableUserRealmRolesAsync(user.Id!, ct))
                .FirstOrDefault(p => p.Name == invite.Role.Value);

            if (papel is not null)
                await admin.AddUserRealmRolesAsync(user.Id!, [papel], ct);
            // "Disponíveis" exclui o que já está atribuído: uma entrega concorrente que atribua o papel entre as duas
            // leituras o tira das duas listas. UMA releitura dos atribuídos separa a corrida da ausência.
            else if (!(await admin.GetUserRealmRolesAsync(user.Id!, ct)).Any(p => p.Name == invite.Role.Value))
                throw new IdentityProviderInconsistencyException(
                    $"O papel de realm '{invite.Role.Value}' do convite do tenant {tenantId.Value} não existe no realm.");
        }

        // 5. E-mail só se o convite ainda não foi aceito: um envio depois do aceite mandaria ao admin ativo um link
        //    que troca a senha dele. 400 (usuário desabilitado à mão) é permanente. O PUT fica fora do retry.
        if (user.RequiredActions?.Contains("UPDATE_PASSWORD") == true)
        {
            try
            {
                await admin.ExecuteActionsEmailAsync(
                    user.Id!, InviteActions, (int)invite.LinkLifetime.TotalSeconds, ct);
            }
            catch (KeycloakBadRequestException excecao)
            {
                throw new IdentityProviderInconsistencyException(
                    $"O Keycloak recusou o envio do convite do tenant {tenantId.Value}.", excecao);
            }
        }

        return ExternalUserId.From(user.Id!);
    }

    // Igual, e não "existe": o tenant_id de outro tenant não é tentativa anterior deste.
    private static bool PertenceAo(UserRepresentation user, string tenant) =>
        user.Attributes?.GetValueOrDefault(UserTenantAttribute) is [var valor] && valor == tenant;
    // ... demais operações seguem o mesmo padrão: consultar, criar se necessário, traduzir erros.
}

internal sealed class KeycloakAdminClient(HttpClient http, IOptions<KeycloakAdminOptions> options)
{
    private readonly string _realm = options.Value.Realm;

    public async Task<string> CreateOrganizationAsync(OrganizationRepresentation organization, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"admin/realms/{_realm}/organizations", organization, ct);

        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new KeycloakConflictException();

        response.EnsureSuccessStatusCode();

        // O Keycloak responde 201 Created com o id do recurso no cabeçalho Location.
        return response.Headers.Location!.Segments[^1];
    }

    /// <summary>
    /// Busca Organization por atributo, via parâmetro q ('chave:valor').
    /// </summary>
    /// <remarks>
    /// O parâmetro HTTP é `q` — `searchQuery` é só o nome da variável Java, e `exact`
    /// vale para `search` (nome ou domínio), não para `q`, que já compara por igualdade.
    /// `briefRepresentation=false` é obrigatório: sem ele os atributos não voltam na
    /// resposta. O valor termina só em espaço, então o GUID dispensa aspas.
    /// </remarks>
    public async Task<OrganizationRepresentation?> FindOrganizationByAttributeAsync(
        string key, Guid value, CancellationToken ct)
    {
        var query = Uri.EscapeDataString($"{key}:{value}");
        var found = await http.GetFromJsonAsync<List<OrganizationRepresentation>>(
            $"admin/realms/{_realm}/organizations?q={query}&briefRepresentation=false&max=2", ct);

        // Mais de um resultado significa corrupção da correlação: falhar alto em vez
        // de escolher um arbitrariamente e provisionar sobre a Organization errada.
        if (found is { Count: > 1 })
            throw new IdentityProviderInconsistencyException($"Mais de uma Organization com {key}={value}.");

        return found?.SingleOrDefault();
    }

    /// <summary>Usuários com o e-mail exato, com atributos (v2.6).</summary>
    /// <remarks>
    /// exact=true compara por igualdade — sem ele, pre.x@acme.test casaria com x@acme.test. briefRepresentation=false
    /// traz os atributos. O e-mail vai escapado, porque o + viraria espaço. Esta URL nunca vai a log nem a exceção.
    /// FindUsersByUsernameAsync é igual, com username= no lugar de email=.
    /// </remarks>
    public async Task<IReadOnlyList<UserRepresentation>> FindUsersByEmailAsync(string email, CancellationToken ct) =>
        await http.GetFromJsonAsync<List<UserRepresentation>>(
            $"admin/realms/{_realm}/users?email={Uri.EscapeDataString(email)}&exact=true&briefRepresentation=false", ct)
        ?? [];

    public async Task ExecuteActionsEmailAsync(
        string userId, IReadOnlyList<string> actions, int lifespanSeconds, CancellationToken ct)
    {
        // lifespan em segundos, por chamada; sem ele, vale o padrão do realm (12 h).
        using var response = await http.PutAsJsonAsync(
            $"admin/realms/{_realm}/users/{userId}/execute-actions-email?lifespan={lifespanSeconds}", actions, ct);

        // 400 ("User is disabled", "User email missing"): o adaptador traduz em inconsistência. Decide pelo status.
        if (response.StatusCode == HttpStatusCode.BadRequest)
            throw new KeycloakBadRequestException("O Keycloak respondeu 400 ao enviar o e-mail de ações.");

        response.EnsureSuccessStatusCode();   // 500 (SMTP fora do ar): HttpRequestException, transitória
    }

    /// <summary>Se o usuário já é membro da Organization (v2.6): 200 → sim, 404 → não; o resto lança.</summary>
    /// <remarks>
    /// GET /organizations/{id}/members/{userId}, conferido no fonte da 26.7.4 (OrganizationMemberResource) e ao vivo:
    /// o service account com manage-organizations e manage-users recebe 404, não 403, para quem não é membro.
    /// </remarks>
    public async Task<bool> IsOrganizationMemberAsync(string organizationId, string userId, CancellationToken ct)
    {
        using var response = await http.GetAsync(
            $"admin/realms/{_realm}/organizations/{organizationId}/members/{userId}", ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;

        response.EnsureSuccessStatusCode();
        return true;
    }

    // CreateUserAsync (409 → KeycloakConflictException), AddOrganizationMemberAsync (409 = sucesso),
    // GetUserRealmRolesAsync, GetAvailableUserRealmRolesAsync e AddUserRealmRolesAsync seguem o mesmo padrão.
    // As demais operações entram com as fatias que as usarem.
}
```

> **Verificado no código do Keycloak 26.7.4 (v2.4), resolvendo o "a confirmar" da v2.3:** a busca por atributo de Organization existe desde a 25.0 pelo parâmetro `q`, com igualdade exata; `name` e `alias` duplicados respondem **409** (`{"errorMessage": ...}` — o adaptador decide pelo status, nunca pelo texto); o 201 vem sem corpo e com o id no `Location`; e `description` é persistida e devolvida. Os testes de integração provam cada um desses pontos contra o Keycloak real, incluindo uma busca **com distrator** — duas Organizations, e o `Find` precisa devolver só a certa —, que é o que pegaria um `q` ignorado.

**Os cinco passos de `EnsureInvitedUserAsync` (v2.6)**, verificados no código do Keycloak 26.7.4:

1. **Buscar** por `GET /users?email=<escapado>&exact=true&briefRepresentation=false`. Achou com o `tenant_id` deste tenant: é o nosso, segue para o passo 3. Achou sem esse valor: `IdentityProviderInconsistencyException`.
2. **Criar** com `POST /users`: username igual ao e-mail, o e-mail, `enabled=true`, `requiredActions=[UPDATE_PASSWORD, VERIFY_EMAIL]` e `attributes.tenant_id`; o id vem do `Location` do `201`. Ações desconhecidas são ignoradas em silêncio, e atributo não declarado no User Profile é descartado em silêncio — por isso os testes leem o usuário cru pelo master. No `409`, **uma** nova busca, por e-mail e por `username=<e-mail>&exact=true`: com o nosso `tenant_id`, segue; senão, inconsistência.
3. **Vincular** com `POST /organizations/{id}/members`. O `409` (já é membro) conta como sucesso. Exige `manage-organizations` **e** `manage-users`. **Corrida de duas entregas (v2.6):** o vínculo do Keycloak consulta e depois insere (`JpaOrganizationProvider.addMember`), e de dois `POST` concorrentes o perdedor recebe `400` (a `ModelException` do `INSERT` repetido, em `OrganizationMemberResource.addMember`), não `409`. No `400`, **uma** leitura da pertença, `GET /organizations/{id}/members/{userId}` — `200` para membro; `404` para não membro, porque o service account pode consultar usuários —, nunca em laço: membro, o passo está feito (log `EventId 2215`); não membro, o `400` original sobe, transitório.
4. **Atribuir o papel**, que exige o id no corpo: `GET /users/{id}/role-mappings/realm`; se o papel não estiver lá, o id vem de `.../realm/available` e o `POST` leva `{id, name}`. Reatribuir é inofensivo. **Corrida de duas entregas (v2.6):** "disponíveis" exclui o que já está atribuído, e uma entrega concorrente que atribua o papel entre as duas leituras o tira das duas listas. Por isso, papel ausente dos disponíveis leva a **uma** releitura dos atribuídos antes de concluir ausência, nunca em laço: atribuído, segue (log `EventId 2214`); ausente das duas listas mesmo assim, inconsistência. Sem a releitura, a corrida benigna terminava em `ProvisioningFailed` — 4 de 48 rodadas concorrentes contra o Keycloak real, antes da correção.
5. **Enviar o e-mail** com `PUT /users/{id}/execute-actions-email?lifespan=<segundos>`, só se o usuário ainda tiver `UPDATE_PASSWORD` pendente. O link sai com a URL de frontend (`KC_HOSTNAME`, §15), leva ao client `account` e abre primeiro uma página de confirmação numa sessão nova, o que neutraliza a pré-busca de scanners de e-mail.

Os logs do convite ficam na faixa `2205`–`2215` do `KeycloakLogs`: `2205`–`2213` para os passos, e `2214` e `2215` para as duas corridas resolvidas. Nenhum leva o e-mail; todos levam o `tenantId`.

**Classes de erro que saem do adaptador.** Nada é engolido; decidir entre repetir e desistir é de quem chama (§11.5):

| Situação | Resultado |
|---|---|
| Keycloak fora do ar, timeout, 5xx | a resiliência repete **só os GETs**; esgotado → `HttpRequestException` / `TimeoutRejectedException` — **transiente** |
| 409 de Organization que não é deste tenant, ou mais de uma com o atributo | `IdentityProviderInconsistencyException` (na Application) — **permanente** |
| Token recusado (`invalid_client`), 403 na Admin API | `HttpRequestException` com o status; o `error_description` do token endpoint vai ao log, truncado |
| 404 "Organizations not enabled for this realm" | erro de configuração do realm; o smoke test da §15 pega |
| E-mail em uso por conta sem o nosso `tenant_id`, ou `409` no `POST /users` sem conta nossa na nova busca (v2.6) | `IdentityProviderInconsistencyException` — **permanente** |
| `400 User is disabled` no `execute-actions-email`: o nosso usuário foi desabilitado à mão (v2.6) | `IdentityProviderInconsistencyException` — **permanente** |
| Papel ausente das duas listas do usuário, **também na releitura dos atribuídos**: o realm não é o que a Gateway espera (v2.6) | `IdentityProviderInconsistencyException` — **permanente** |
| Papel ausente dos disponíveis, mas presente na releitura dos atribuídos: uma entrega concorrente o atribuiu (v2.6) | sucesso, com log `EventId 2214` |
| `500` no `execute-actions-email` (SMTP fora do ar) ou timeout do `PUT` (v2.6) | `HttpRequestException` / `TimeoutRejectedException` — **transiente**. O `PUT` não tem retry automático, e timeout **não** significa "não enviado": o Keycloak envia dentro da requisição |
| `409` no vínculo à Organization (v2.6) | sucesso: o usuário já é membro |
| `400` no vínculo à Organization, e a leitura da pertença responde `200` (v2.6) | sucesso, com log `EventId 2215`: uma entrega concorrente vinculou |
| `400` no vínculo à Organization, e a leitura da pertença responde `404` (v2.6) | o `HttpRequestException` original do `400` — **transiente** |
| `400` no `POST .../role-mappings/realm`: duas entregas concorrentes atribuíram o papel ao mesmo tempo (v2.6) | `HttpRequestException` — **transiente**, sem tratamento próprio: o ciclo seguinte encontra o papel atribuído e segue (§19) |

### 11.7. Presentation: endpoint e isolamento entre tenants

> **Convenção de endpoints: Carter.** Os endpoints são organizados em módulos Carter (`ICarterModule`), como no CleanStart — um módulo por área de recurso, sem controllers. O exemplo abaixo mostra a **lógica de isolamento**, que é o ponto da seção; ela é idêntica em qualquer estilo de roteamento, e o que muda é apenas onde a rota é declarada.

```csharp
// Endpoints/TenantEndpoints.cs: sem regra de negócio, apenas tradução HTTP ↔ comando.
group.MapPost("/", async (RegisterTenantRequest body, ICommandDispatcher dispatcher, CancellationToken ct) =>
    {
        var result = await dispatcher.Send(
            // Errata E4 (v2.7): o e-mail do admin inicial está no command desde a v2.6 (§11.4).
            new RegisterTenantCommand(body.Name, body.Slug, body.PlanCode, body.InitialAdminEmail), ct);

        // 202: o tenant existe, mas o provisionamento no Keycloak é assíncrono.
        return result.Match(
            id => Results.AcceptedAtRoute("GetTenantProvisioning",
                new { tenantId = id.Value },
                new TenantAcceptedResponse(id.Value, "Pending")),
            error => error.ToProblemDetails());
    })
    .RequireAuthorization(Policies.PlatformAdmin)
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

        // O claim, só no formato D, conferido pela ida e volta: Guid.TryParseExact tolera espaço nas pontas e,
        // em cada componente, o prefixo 0x e o sinal de mais. E a comparação com a rota é por Guid, não por
        // texto: a rota com o GUID em maiúsculas é o mesmo tenant.
        return Guid.TryParseExact(claim.Value, "D", out Guid doToken)
            && string.Equals(doToken.ToString("D"), claim.Value, StringComparison.OrdinalIgnoreCase)
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

```csharp
/// <summary>
/// Não existe GetAsync(MemberId). A ausência da sobrecarga é o que torna a regra
/// da seção 6.4 verificável: não há como escrever o acesso inseguro por engano.
/// </summary>
/// <remarks>
/// v2.6 (fatia C): só Add, porque o Member carrega o TenantId e nenhum caso de uso ainda lê membros.
/// GetAsync(TenantId, MemberId), ListAsync(TenantId) e o teste que proíbe a sobrecarga só por id chegam no M2.
/// </remarks>
public interface IMemberRepository
{
    void Add(Member member);

    // M2:
    // Task<Member?> GetAsync(TenantId tenantId, MemberId memberId, CancellationToken ct);
    // Task<IReadOnlyList<Member>> ListAsync(TenantId tenantId, CancellationToken ct);
}
```

Verificar o tenant da rota (§11.7) não cobre este vetor: o ator do tenant A opera em `/tenants/A/...`, o requirement aprova, e o id informado é de um recurso do tenant B. Um id fora do tenant resulta em **404**, nunca 403 — responder 403 confirmaria que o recurso existe em outro tenant.

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

### 11.10. Retry de conflito de concorrência

```csharp
/// <summary>
/// Reprocessa comandos que falharam por conflito de versão (xmin), recarregando o
/// aggregate a cada tentativa. Sem isto, duas reservas de vaga concorrentes fariam
/// a segunda virar 500 — a v2.0 prometia no comentário do aggregate um retry que
/// não existia em lugar nenhum.
/// </summary>
internal sealed class ConcurrencyRetryBehavior<TCommand, TResult>(...) : IPipelineBehavior<TCommand, TResult>
{
    private const int MaxAttempts = 3;

    public async Task<TResult> Handle(TCommand command, RequestHandlerDelegate<TResult> next, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await next();
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                // O escopo do DbContext é renovado pelo dispatcher antes da retentativa.
            }
        }
    }
}
```

Esgotadas as tentativas, a resposta é `409 Conflict` em Problem Details. No caminho do consumidor MassTransit o retry do broker já cobriria o caso, mas o caminho HTTP síncrono (convite de membro) não tem essa rede.

### 11.8. Injeção de dependência

Cada camada expõe um único método de extensão, e o `Program.cs` é o único ponto que conhece todas elas.

```csharp
// Program.cs (composition root)
builder.Services
    .AddApplication()                          // handlers, validadores, políticas de aplicação
    .AddInfrastructure(builder.Configuration)  // EF Core, MassTransit, Keycloak, sync
    .AddPresentation(builder.Configuration);   // autenticação, policies, OpenAPI, rate limiting
```

```csharp
// Infrastructure/Identity/Keycloak/KeycloakServiceCollectionExtensions.cs
public static IServiceCollection AddKeycloakIdentity(this IServiceCollection services, IConfiguration configuration)
{
    // Configuração inválida derruba a aplicação na subida, não na primeira chamada.
    services.AddOptions<KeycloakAdminOptions>()
        .Bind(configuration.GetSection("Keycloak:Admin"))
        .ValidateDataAnnotations()
        .ValidateOnStart();

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
        // Um item em branco na lista é erro de configuração, e a subida falha. Lista vazia sobe: a API recusa
        // todo token de usuário e registra um aviso (fail-closed).
        .Validate(
            token => token.AllowedClients.All(client => !string.IsNullOrWhiteSpace(client)),
            "Keycloak:Auth:AllowedClients não aceita item vazio.")
        // Fora de Development, o client de demonstração não pode estar na lista. Comparação ordinal, como a do azp.
        .Validate<IHostEnvironment>(
            (token, ambiente) => ambiente.IsDevelopment()
                || !token.AllowedClients.Contains("identity-gateway-demo", StringComparer.Ordinal),
            "Keycloak:Auth:AllowedClients traz um client aceito só no ambiente Development.")
        .ValidateOnStart();

    // A chave é importada para RSA uma vez; o cache do token é singleton porque o
    // IHttpClientFactory recicla os handlers a cada ~2 min, e um cache dentro do
    // handler morreria junto, sem erro.
    services.AddSingleton<GatewaySigningKey>();
    services.AddSingleton<ServiceAccountTokenCache>();
    services.AddTransient<ServiceAccountTokenHandler>();

    // Token endpoint: HttpClient próprio (sem recursão com o client abaixo) e SEM
    // retry — o jti é de uso único, e reenviar o mesmo assertion seria recusado.
    services.AddHttpClient<KeycloakTokenClient>(ConfigurarBaseAddress);

    services.AddHttpClient<KeycloakAdminClient>(ConfigurarBaseAddress)
        // A ORDEM IMPORTA (v2.4): o primeiro handler registrado é o mais externo.
        // Resiliência por fora e token por dentro fazem cada tentativa renovar o token
        // se preciso, e deixam o "401 → invalida e repete uma vez" DENTRO de uma
        // tentativa, contido no timeout total. Na ordem inversa (a da v2.3), o reenvio
        // após 401 entraria na pipeline de resiliência do zero, e uma chamada lógica
        // poderia durar o dobro do orçamento.
        .AddStandardResilienceHandler(resilience =>
        {
            // POST não é idempotente: retry automático só em métodos seguros.
            // A idempotência das escritas é garantida pelo padrão "consultar antes de criar".
            resilience.Retry.DisableForUnsafeHttpMethods();
        })
        .AddHttpMessageHandler<ServiceAccountTokenHandler>();

    services.AddTransient<IIdentityProvider, KeycloakIdentityProvider>();
    return services;
}
```

Critérios de tempo de vida: handlers de comando e repositórios são `Scoped`, porque acompanham o `DbContext`; clientes HTTP tipados são gerenciados pelo `IHttpClientFactory`, e o adaptador do Keycloak é **`Transient`**, como o cliente tipado que envolve — ele não depende de `DbContext`, e a v2.3 o justificava por um motivo que não se aplica; caches, chaves e catálogos imutáveis (como o de planos) são `Singleton`. Não há *service locator* fora do composition root.

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
        JsonElement payload;

        // Payload que não é base64url, ou não é JSON: recusa com motivo fixo — 401, e não exceção.
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

        // Lista vazia: recusa todo token.
        if (string.IsNullOrEmpty(azp) || !clientsPermitidos.Contains(azp, StringComparer.Ordinal))
            return "azp ausente, sem a forma de texto ou fora da lista de clients permitidos";

        // O claim, e não o cabeçalho: o ID token traz "ID".
        if (!string.Equals(Texto(payload, "typ"), "Bearer", StringComparison.Ordinal))
            return "typ diferente de Bearer";

        // A ida e volta, e não só o parse: Guid.TryParseExact tolera espaço nas pontas e, em cada componente, o
        // prefixo 0x e o sinal de mais.
        if (Texto(payload, "sub") is not { } sub
            || !Guid.TryParseExact(sub, "D", out Guid id)
            || !string.Equals(id.ToString("D"), sub, StringComparison.OrdinalIgnoreCase))
            return "sub ausente ou fora do formato de GUID";

        return null;
    }

    // Só texto: array, número, objeto e ausência devolvem null. A guarda do objeto vem primeiro: num payload que é
    // JSON e não é objeto, o TryGetProperty lançaria.
    private static string? Texto(JsonElement payload, string claim) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(claim, out JsonElement valor)
        && valor.ValueKind == JsonValueKind.String
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

---

## 12. Guia para as APIs consumidoras (Data Plane)

O pacote `IdentityGateway.Client.AspNetCore` concentra a configuração correta, para que cada API não precise acertar sozinha os detalhes do Keycloak:

```csharp
builder.Services.AddIdentityGatewayAuthentication(builder.Configuration.GetSection("IdentityGateway"));

app.MapPost("/invoices/{id}/approve", ApproveInvoice)
   .RequirePermission("invoices:approve");   // nível 2: permissão fina com cache
```

Internamente, a configuração de validação é a seguinte:

```csharp
services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = settings.Issuer;        // realm único (ADR-001)
        options.Audience = settings.Audience;       // exige Audience Mapper no client scope
        options.RequireHttpsMetadata = true;

        // Mantém os nomes originais dos claims ("sub", "tenant_id", "roles")
        // em vez de convertê-los para URIs do WS-Federation.
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            // Restringe os algoritmos aceitos: defesa contra algorithm confusion.
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256],
            NameClaimType = "preferred_username",
            RoleClaimType = "roles",                 // claim plano produzido por mapper
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
```

Três pré-requisitos no Keycloak, todos versionados em `keycloak/`:

- **Papéis:** os papéis precisam chegar num claim `roles` plano. É o ponto que mais gera erro nessa integração e está explicado em detalhe na seção 12.1.
- **Audiência:** o `aud` padrão costuma ser `account`. Cada API consumidora precisa de um Audience Mapper no client scope correspondente.
- **Tenant:** o claim `tenant_id` **não vem do mapper de organização** — vem de um User Attribute mapper plano, sincronizado pela Gateway. A razão está na seção 12.2, e é a mesma armadilha de aninhamento da 12.1.

A permissão fina (`RequirePermission`) usa um `IAuthorizationPolicyProvider` que cria policies sob demanda e consulta as permissões efetivas com cache, conforme o fluxo 9.6.

### 12.1. Por que `RequireRole` e `RequireClaim("roles", ...)` falham com o Keycloak

**Sintoma típico.** O usuário tem o papel `tenant-admin` no Keycloak. Você decodifica o token e vê `tenant-admin` lá dentro. Ainda assim, o endpoint protegido por `RequireRole("tenant-admin")` responde **403 Forbidden**. Nenhum erro aparece no log, porque do ponto de vista do .NET a autenticação funcionou: o token é válido, só não contém o papel no formato esperado.

A causa são **duas armadilhas independentes**, uma do lado do Keycloak e outra do lado do .NET. Corrigir só uma não resolve.

#### Armadilha 1 — O Keycloak entrega os papéis aninhados

Sem configuração adicional, o access token emitido pelo Keycloak traz os papéis assim:

```json
{
  "sub": "5f1c0a2e-7c1d-4a55-9b0e-2f6f3c9d1a10",
  "realm_access": {
    "roles": ["default-roles-identity-gateway", "offline_access", "uma_authorization", "tenant-admin"]
  },
  "resource_access": {
    "account": {
      "roles": ["manage-account", "view-profile"]
    }
  }
}
```

Os papéis de realm estão dentro do objeto `realm_access`, e os papéis de cada client, dentro de `resource_access.{clientId}`. Não existe um claim chamado `roles` no primeiro nível. O exemplo deixou de trazer `preferred_username` (v2.7): os clients que chamam a Gateway não recebem o scope `profile`, e o token deles não carrega e-mail nem nome (§10.3).

Esse formato vem do client scope padrão `roles`, que o Keycloak associa a todo client. Ele contém os mappers *realm roles* e *client roles*, cujos nomes de claim são `realm_access.roles` e `resource_access.${client_id}.roles`. **No nome de claim de um mapper do Keycloak, o ponto significa aninhamento**: `realm_access.roles` gera o objeto `realm_access` com a propriedade `roles`. Para um nome com ponto literal, seria preciso escapar com `\.`.

**O que o .NET faz com isso.** O handler de JWT do ASP.NET Core (`JsonWebTokenHandler`, padrão desde o .NET 8) converte cada propriedade do primeiro nível do payload em `Claim`:

- **array** vira **vários claims com o mesmo tipo**, um por elemento;
- **objeto** vira **um único claim** cujo valor é o texto JSON inteiro, com `ValueType` igual a `"JSON"`.

Portanto, o token acima produz um claim `realm_access` com valor `{"roles":["default-roles-identity-gateway",...,"tenant-admin"]}`. Não existe nenhum claim cujo valor seja exatamente `tenant-admin`. Tanto `RequireRole("tenant-admin")` quanto `RequireClaim("roles", "tenant-admin")` comparam valores de claims individuais, e por isso nenhum dos dois encontra o papel.

#### Armadilha 2 — O .NET renomeia claims conhecidos

Mesmo com o Keycloak emitindo um claim `roles` plano, ainda há um passo no .NET. Por padrão, `JwtBearerOptions.MapInboundClaims` é `true`. Com isso, o handler traduz nomes curtos de claims para URIs do padrão WS-Federation. Entre eles:

| Nome no token | Nome no `ClaimsPrincipal` com `MapInboundClaims = true` |
|---|---|
| `roles`, `role` | `http://schemas.microsoft.com/ws/2008/06/identity/claims/role` |
| `sub` | `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier` |
| `email` | `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress` |

O resultado é contraintuitivo: `RequireRole("tenant-admin")` **funciona**, porque o `RoleClaimType` padrão é justamente essa URI; mas `RequireClaim("roles", "tenant-admin")` **falha**, porque o claim `roles` não existe mais com esse nome. Foi por isso que o exemplo da primeira versão desta especificação não funcionaria.

#### Resumo das combinações

| Formato no token | Configuração no .NET | `RequireRole(...)` | `RequireClaim("roles", ...)` |
|---|---|---|---|
| Aninhado (padrão do Keycloak) | Qualquer uma | ❌ | ❌ |
| Plano (`roles: [...]`) | Padrão (`MapInboundClaims = true`) | ✅ | ❌ |
| Plano (`roles: [...]`) | `MapInboundClaims = false` e `RoleClaimType = "roles"` | ✅ | ✅ |

**Este projeto adota a última linha.** Os nomes dos claims no `ClaimsPrincipal` passam a ser idênticos aos do token decodificado, o que torna a depuração direta (o que você vê no JSON é o que o código recebe), `sub` continua se chamando `sub`, e as duas formas de exigir papel se comportam da mesma maneira.

#### Solução A (adotada) — Mapper que emite `roles` plano

A correção é feita por configuração no Keycloak, sem código. Cria-se um client scope dedicado, `gateway-roles`, com um mapper do tipo **User Realm Role**:

| Campo do mapper | Valor | Por quê |
|---|---|---|
| Token Claim Name | `roles` | Sem ponto, portanto sem aninhamento |
| Multivalued | ON | Com OFF, o Keycloak emite apenas um papel, como texto |
| Claim JSON Type | String | Cada elemento do array é um texto |
| Add to access token | ON | É o token que as APIs recebem |
| Add to ID token | OFF | O ID token é para o cliente, não para autorização |
| Add to userinfo | OFF | Não é necessário para este fluxo |
| Add to token introspection | ON | Mantém a introspecção coerente com o token |

No console do Keycloak: *Client scopes* → *Create client scope* (`gateway-roles`, tipo **None**: desde a v2.7 o scope não é default do realm) → aba *Mappers* → *Add mapper* → *By configuration* → *User Realm Role*.

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

#### Solução B (contingência) — Transformação de claims no .NET

Quando não é possível alterar o Keycloak (um ambiente de terceiros, por exemplo), a API pode ler o claim `realm_access` e gerar os claims planos. O pacote `IdentityGateway.Client.AspNetCore` inclui essa transformação como rede de segurança. Ela **só age quando o claim `roles` plano não existe**, então não duplica papéis quando a Solução A está ativa.

```csharp
/// <summary>
/// Converte realm_access.roles (formato padrão do Keycloak) em claims "roles" planos.
/// Contingência para quando o mapper da Solução A não está configurado.
/// </summary>
internal sealed class KeycloakRealmRolesTransformation : IClaimsTransformation
{
    private const string RolesClaim = "roles";

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity)
            return Task.FromResult(principal);

        // O ASP.NET Core pode chamar a transformação mais de uma vez na mesma requisição.
        // Se o claim plano já existe (mapper ou execução anterior), não há nada a fazer.
        if (identity.HasClaim(c => c.Type == RolesClaim))
            return Task.FromResult(principal);

        var realmAccess = identity.FindFirst("realm_access")?.Value;
        if (string.IsNullOrWhiteSpace(realmAccess))
            return Task.FromResult(principal);

        try
        {
            using var json = JsonDocument.Parse(realmAccess);
            if (!json.RootElement.TryGetProperty("roles", out var roles) ||
                roles.ValueKind != JsonValueKind.Array)
            {
                return Task.FromResult(principal);
            }

            // Trabalha sobre uma cópia: o principal original não deve ser alterado.
            var clone = principal.Clone();
            var cloneIdentity = (ClaimsIdentity)clone.Identity!;

            foreach (var role in roles.EnumerateArray())
            {
                if (role.ValueKind == JsonValueKind.String && role.GetString() is { Length: > 0 } name)
                    cloneIdentity.AddClaim(new Claim(RolesClaim, name));
            }

            return Task.FromResult(clone);
        }
        catch (JsonException)
        {
            // Fail closed: JSON malformado significa nenhum papel concedido.
            return Task.FromResult(principal);
        }
    }
}

// Registro (feito pelo AddIdentityGatewayAuthentication):
services.AddTransient<IClaimsTransformation, KeycloakRealmRolesTransformation>();
```

A Solução A continua sendo a preferida: o token fica autoexplicativo para qualquer consumidor, em qualquer linguagem, e não depende de cada API lembrar de registrar a transformação.

**Nos clients que chamam a Gateway, a Solução B fica inerte (v2.7).** O token deles não traz `realm_access`, e não há o que achatar. Ela continua no pacote do Data Plane, para tokens de ambientes de terceiros.

#### Papéis de realm × papéis de client

Este projeto usa **papéis de realm** para o catálogo global (ADR-005 e ADR-009). O Keycloak também permite papéis por client, que ficam em `resource_access.{clientId}.roles`. Se uma API precisar de papéis próprios, use um mapper *User Client Role* restrito àquele client e com **outro nome de claim** (por exemplo, `api_roles`). Juntar papéis de realm e de client no mesmo claim `roles` tornaria ambíguo de onde veio cada papel, e um papel homônimo de outro client poderia conceder acesso indevido.

#### Como verificar

1. **No Keycloak, sem gerar token manualmente:** *Clients* → escolha o client → aba *Client scopes* → *Evaluate* → selecione um usuário → *Generated access token*. O claim `roles` deve aparecer como array no primeiro nível.
2. **No .NET, vendo o que o código realmente recebe:** a `SampleResourceApi` expõe, apenas em ambiente de desenvolvimento, um endpoint que lista os claims do usuário autenticado:

```csharp
if (app.Environment.IsDevelopment())
{
    // Mostra os claims exatamente como o ClaimsPrincipal os recebe,
    // incluindo o ValueType: "JSON" indica um objeto que não foi achatado.
    app.MapGet("/debug/claims", (ClaimsPrincipal user) =>
            user.Claims.Select(c => new { c.Type, c.Value, c.ValueType }))
        .RequireAuthorization();
}
```

Evite colar tokens de ambientes reais em decodificadores online: um access token válido é uma credencial.

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

#### Diagnóstico rápido

| Sintoma | Causa provável | Correção |
|---|---|---|
| 403 com token válido; o papel aparece dentro de `realm_access` | Mapper plano ausente ou scope não associado ao client | Associar `gateway-roles` aos `defaultClientScopes` do client (v2.7: o scope não é default do realm) |
| O mapper existe, mas o token continua só com `realm_access` | Scope criado como *Optional* ou associado a outro client | Associar como *Default* ao client que emite o token |
| `RequireRole` passa, `RequireClaim("roles", ...)` falha | `MapInboundClaims` no padrão (`true`) | `MapInboundClaims = false` e `RoleClaimType = "roles"` |
| `User.IsInRole(...)` sempre `false` com claim `roles` presente | `RoleClaimType` diferente de `"roles"` | Ajustar `RoleClaimType` |
| Apenas um papel no token, como texto e não array | Multivalued desligado | Multivalued ON |
| Claim aparece como `realm_access` mesmo após criar o mapper | Nome do claim com ponto (ex.: `realm_access.roles`) | Usar nome sem ponto: `roles` |
| Papel aparece no ID token, mas não no access token | *Add to access token* desligado | Ligar *Add to access token* |
| Papel recém-atribuído não funciona | Token emitido antes da atribuição | Aguardar o refresh ou fazer novo login (access token de 5 min) |
| Token de outro emissor é aceito, apesar do `ValidIssuer` (v2.7) | Com metadados, a biblioteca aceita o `issuer` anunciado pelo discovery antes de olhar o `ValidIssuer` | `IssuerValidator` próprio, com igualdade ordinal contra o emissor público (§11.8) |
| `401` com token válido; o `aud` é `account` ou não existe (v2.7) | O client não tem o scope `gateway-api`, que não é default do realm | Anexar `gateway-api` aos `defaultClientScopes` do client — só dos que falam com a Gateway |
| `default-roles-identity-gateway` aparece no claim `roles` (v2.7) | `fullScopeAllowed: true` no client, ou scope mappings do `gateway-roles` além do catálogo | `fullScopeAllowed: false`, e scope mappings só com os quatro papéis |
| `profile`, `email`, `roles` e `basic` sumiram do realm depois do import (v2.7) | O JSON declara `clientScopes` sem o atributo `CreateDefaultClientScopes` | `"CreateDefaultClientScopes": "true"` nos atributos do realm, e volume novo |
| `401` com token válido; o token não tem `sub` (v2.7) | O client não tem o scope `basic` | `basic` nos `defaultClientScopes` do client |

---

### 12.2. O claim `tenant_id`: a mesma armadilha, no campo mais sensível

A seção anterior dissecou o aninhamento de `realm_access.roles`. **A v2.0 caiu exatamente nessa armadilha em `tenant_id`** — e ali o efeito é pior, porque `tenant_id` é a peça de isolamento entre tenants.

#### O que o mapper nativo de organização realmente emite

A v2.0 afirmava que "o claim `tenant_id` é produzido pelo mapper de organização, configurado para emitir o id da Organization com esse nome". Isso não corresponde ao comportamento do Keycloak. O mapper embutido — `OrganizationMembershipMapper` — emite um claim chamado `organization`, e o valor **não é um escalar**: é um objeto aninhado chaveado pelo *alias*.

```json
{
  "organization": {
    "acme-corp": { "id": "f8d3c4e1-...", "groups": ["/Engineering/Backend"] }
  }
}
```

A inclusão do `id` foi acrescentada no Keycloak 26; antes vinha apenas o nome, que é mutável — motivo pelo qual correlacionar por nome ou alias é frágil (o mesmo raciocínio do atributo `gateway_tenant_id` na §11.6).

**Pela regra da §12.1, objeto vira um único claim com o JSON inteiro como valor e `ValueType: "JSON"`.** Logo:

```csharp
context.User.FindFirst("tenant_id")   // → null. Não existe claim com esse nome.
```

O `SameTenantHandler` nunca encontraria o claim, nunca daria `Succeed`, e — com a correção de `Fail()` da §11.7 — **todas as rotas com `{tenantId}` passariam a negar**. Sem a correção, seria pior: a omissão deixaria a decisão para outro handler.

#### Por que a correção ingênua é perigosa

O sintoma ("tudo nega") pressiona por uma correção rápida no handler de isolamento, com a suíte vermelha — a pior condição possível para editar esse código. As três tentativas naturais falham, e a terceira é explorável:

| Tentativa | O que acontece |
|---|---|
| `organization.EnumerateObject().First().Name` | devolve o *alias*; a rota carrega o `TenantId` (GUID) → nunca casa → 403 persiste |
| `.Any(p => p.Name == routeTenant)` | concede acesso a **qualquer** organização do usuário. O ADR-009 é convenção da Gateway; nada no Keycloak impede que alguém adicione o usuário a uma segunda Organization pelo console |
| `organizationJson.Contains(routeTenant)` | substring sobre o JSON bruto: o slug `acme` casa dentro de `acme-corp`. **E o slug vem no corpo do `POST /tenants`** — basta escolher um que seja prefixo de outro tenant |

#### Solução adotada: User Attribute mapper plano

O `tenant_id` passa a ser um **atributo do usuário**, gravado pela Gateway e emitido por um mapper plano — o mesmo mecanismo que o ADR-004 já prevê para o `tier`.

| Campo do mapper | Valor | Por quê |
|---|---|---|
| Mapper Type | User Attribute | Lê atributo do usuário, não a associação de organização |
| User Attribute | `tenant_id` | Gravado pela Gateway no convite (§11.6) e declarado no User Profile do realm (v2.6) |
| Token Claim Name | `tenant_id` | Sem ponto, portanto sem aninhamento |
| Claim JSON Type | String | Escalar: `ValueType` será `String`, não `JSON` |
| Multivalued | OFF | Um usuário pertence a um único tenant (ADR-009) |
| Add to access token | ON | É o token que as APIs recebem |

O mapper vai no client scope `gateway-tenant`, com `multivalued` e `aggregate.attrs` em `"false"`. **O scope fica fora dos defaults do realm (v2.7)**, como o `gateway-roles` e o `gateway-api` (§12.1): cada client recebe só o que declara. O client de demonstração declara os três. **No M6, o adaptador que provisiona clients de tenant anexa o `gateway-tenant` explicitamente** — e o `gateway-api`, só se o client for chamar a Gateway, o que exige decisão nova (§10.1). A v2.6 punha o scope em `defaultDefaultClientScopes`, para todo client criado depois o receber sozinho; com os scopes fora dos defaults, esse automatismo deixa de existir, de propósito.

**O mapper recua para o atributo de grupo (v2.7).** Quando o usuário não tem o atributo `tenant_id`, o `oidc-usermodel-attribute-mapper` usa o atributo de mesmo nome do **primeiro grupo** que o tiver, subindo aos pais, e não há configuração que desligue isso — verificado no código e ao vivo, na 26.7.4. Com `multivalued` em `"false"`, o token nunca traz dois valores. O `manage-users` cria grupos (§10.2): o claim é forjável por quem tem a chave da Gateway. São duas defesas. **O realm não tem nenhum grupo**, e o `RegrasDoRealmTests` reprova `groups` e `defaultGroups` no JSON. E, nas rotas de governança, a Gateway exige também a pertença no banco (ADR-011) (D2, planejado). Um grupo criado em runtime não é visto pela regra do JSON, e o Data Plane continua exposto a ele (§19). Um teste de caracterização contra o Keycloak real afirma o recuo, para avisar se uma versão nova mudar o comportamento. Atributo de Organization não vaza para o claim.

**Onde a Gateway grava o atributo.** Em dois pontos, e ambos precisam existir:

1. no convite do membro, junto da criação do usuário — inclusive no do admin inicial, dentro do provisionamento (§9.1, §11.6). **Na v2.6, o atributo também é a chave de correlação do convite:** um usuário com o `tenant_id` deste tenant é uma tentativa anterior e é reaproveitado, e um e-mail em uso por conta sem esse valor é falha permanente. Um segundo atributo só para correlação duplicaria este, que precisa ser gravado de qualquer forma;
2. na absorção de usuário criado fora da Gateway (§9.4) — aqui há uma **janela real**: entre o primeiro login federado e o processamento do evento pelo poller, o usuário tem token sem `tenant_id` e recebe 403 nas rotas de tenant. A janela é igual ao intervalo de polling, e está declarada na §19.

O claim `organization` continua sendo emitido e é útil em auditoria, mas **não é fonte de autorização**.

**Declarado no User Profile, e só para `admin` (v2.6).** Com a `unmanagedAttributePolicy` nula — o padrão, e o que o realm tem —, o Keycloak 26.7.4 **descarta em silêncio** atributo que o User Profile não declare. O realm declara `tenant_id` num componente `declarative-user-profile`, com `view` e `edit` só para `admin`. A `unmanagedAttributePolicy` fica desligada, e `ENABLED` — o conserto que qualquer busca sugere — é proibido: com ele, o próprio usuário editaria o `tenant_id` pela account console e sequestraria a correlação e o isolamento. Uma regra do `RegrasDoRealmTests` confere o JSON do realm, e um teste de integração prova que um usuário comum não altera o atributo pela Account REST API. O atributo da Organization continua `gateway_tenant_id` (§11.6).

#### Teste que protege esta decisão

Espelha o de `roles` da §12.1, e verifica o que de fato importa — que o claim é escalar:

```csharp
[Fact]
public async Task Access_token_traz_tenant_id_como_claim_plano()
{
    // Errata E8 (v2.7): GetTokenForUserAsync nunca existiu. O token vem do device flow, pelo harness de login.
    TokensDeUsuario tokens = await harness.TokenPorDispositivoAsync(adminDoTenantA, senha, ct);
    var jwt = new JsonWebToken(tokens.AccessToken);

    var tenantId = jwt.Claims.Single(c => c.Type == "tenant_id");

    // O que quebra se alguém trocar o mapper pelo de organização:
    Assert.NotEqual("JSON", tenantId.ValueType);
    Assert.Equal(_tenantA.Id.ToString(), tenantId.Value);
}
```

---

## 13. Estratégia de testes

Todos os testes usam **xUnit**.

| Nível | O que cobre | Ferramentas |
|---|---|---|
| Unitário (Domain) | Invariantes do `Tenant`, limite de vagas, máquina de estados, `RoleAssignmentPolicy`, value objects | xUnit, sem mocks |
| Unitário (Application) | Handlers com `IIdentityProvider` e repositórios falsos; erros de validação | xUnit, fakes escritos à mão |
| Integração | Endpoints reais contra Keycloak, PostgreSQL e RabbitMQ em contêineres; os mappers de verdade (`roles`, `aud`, `tenant_id`) | `WebApplicationFactory`, Testcontainers (incluindo o módulo de Keycloak) |
| Formato dos claims | Token do Keycloak real traz `roles` plano (§12.1) **e `tenant_id` plano, com `ValueType` diferente de `JSON`** (§12.2); endpoint com `RequireRole` aceita esse token | Testcontainers, `JsonWebToken` |
| Não duplicação no Keycloak | `DelegatingHandler` que **conta** chamadas à Admin API e injeta a falha, pendurado **dentro** da resiliência na composição real: o POST chega ao Keycloak e a resposta se perde → **exatamente um POST** e a exceção transiente propagada. Controle positivo: um GET com 503 injetado é repetido (sem ele, "resiliência não registrada" também passaria). Afirmar só "continua existindo uma Organization" seria vacuoso — o 409 do Keycloak garantiria isso sozinho (v2.4). Protege o anti-pattern 8 contra a possibilidade de o `DisableForUnsafeHttpMethods()` não surtir efeito | Testcontainers, `DelegatingHandler` contador |
| Autorização negativa | As **três** regras de isolamento (§3, princípio 5), cada uma parametrizada a partir da tabela de rotas: (a) tenant da rota × token — acesso ao tenant B com token do tenant A; (b) **pertencimento de sub-recurso** — token de A, rota de A, id de membro/client/permission-set de B, esperando 404; (c) **escopo de client** — client de tenant tentando ler governança de outro tenant. Mais escalação de papel e token sem audiência correta. **Continuam três (v2.7):** nas rotas de governança da Gateway, a regra (a) é conferida também pela pertença do ator ao tenant no banco (ADR-011), que a reforça e não é uma quarta regra (D2, planejado) | Teste parametrizado gerado a partir da tabela de rotas |
| Configuração de endpoint | Teste de startup que varre o `EndpointDataSource` e falha se um endpoint com policy de tenant **não** tiver `{tenantId}` no template — o inverso do teste acima. **Entra com a primeira policy de tenant, `TenantAdmin`** (v2.7) (D2, planejado): até a v2.6 não havia endpoint em que ele pudesse falhar | `EndpointDataSource` do host de teste |
| Idempotência e consistência | Mensagem entregue duas vezes; falha do Keycloak no meio do provisionamento; reconciliação detectando divergência | Testcontainers, com a falha simulada por um `DelegatingHandler` |
| Arquitetura | Regras de dependência da seção 7; nenhum tipo do Keycloak fora de `Infrastructure/Identity` | NetArchTest ou ArchUnitNET |
| Contrato | Documento OpenAPI gerado comparado com a versão aprovada, o que evita *breaking changes* acidentais | Snapshot do documento |
| Convite contra o Keycloak real (v2.6) | O `KeycloakFixture` sobe o Keycloak com um **mailpit** numa rede Testcontainers e `KC_HOSTNAME=http://keycloak.test:8081` com o backchannel dinâmico, e toda composição de teste passa o `PublicBaseUrl` correspondente: **todo** teste de Keycloak exercita a separação entre emissor público e transporte (§10.2). Leituras cruas pelo master (atributo `tenant_id`, `enabled`, as duas ações, o vínculo, o papel), idempotência, retomada parcial, as inconsistências da §11.6 (inclusive `pre.{x}` para o `exact=true`), o `409` sem laço, o link lido no mailpit (`exp − iat ≈ LinkLifetime`) e aberto, e as duas corridas do adaptador (o `400` do vínculo com a leitura da pertença, contra o Keycloak real; a releitura dos papéis, na unidade). E-mails de teste únicos e com `+`, e o mailpit compartilhado sempre filtrado por destinatário | Testcontainers (Keycloak, mailpit), `HttpClient` cru |
| Papéis efetivos do service account (v2.6) | Os papéis **efetivos**, com compostos expandidos, são exatamente `manage-organizations` e `manage-users`, sem depender de ordem; ausência de `impersonation`, `realm-admin`, `manage-realm`, `manage-clients` e `manage-identity-providers`; `403` em `GET .../clients` e no `PUT` do realm. Um token sem `manage-users` deixa o health check em `503` | Testcontainers |
| Vazamento do e-mail (v2.6) | Composição real com todas as categorias em `Trace`, o log de dados sensíveis do EF ligado e um exporter OpenTelemetry em memória; caminho feliz e falhas injetadas (inconsistência, `409`, `400 disabled`, `500`, falha no commit). Exige que houve registros das categorias do `HttpClient` com `users` — o canal foi capturado — e que nenhum log, span, `outbox_messages.error` nem Problem Details contém o e-mail. Na Application, um logger de captura passa por todos os ramos | Testcontainers, OpenTelemetry InMemory exporter |
| Convite ponta a ponta, atomicidade e entrega concorrente (v2.6) | Postgres, Keycloak e mailpit: `POST` → `Active`, com o `Member` gravado, `initial_admin_email` nulo e o e-mail no mailpit. **Commit perdido:** o segundo ciclo termina com um usuário, uma linha em `members` e **exatamente dois** e-mails para o destinatário — o duplo envio declarado na §11.5. **Atomicidade:** o `xmin` do tenant muda por outra conexão durante o convite, e o `UPDATE tenants … WHERE xmin`, **último** comando do lote, casa zero linhas (`DbUpdateConcurrencyException`); o teste exige o tenant `Pending`, com o e-mail, zero vagas, **zero linhas em `members`** — o `INSERT` do `Member` executou e foi desfeito — e nenhum `tenant-activated` no Outbox. **Colisão no índice:** uma linha pré-inserida em `members` com o mesmo `(tenant_id, sub)` derruba o **primeiro** comando, e o tenant não ativa — prova só que a colisão não ativa, não o rollback. **Entrega concorrente:** duas entregas da mesma mensagem, sincronizadas no Keycloak, terminam, depois de mais um ciclo do Outbox, com o tenant `Active`, uma vaga, um `Member` e um usuário | Testcontainers |
| OIDC falso com emissor divergente (v2.7) | Os testes funcionais sobem um Kestrel em loopback que serve discovery e JWKS de uma chave RSA de teste, só por configuração. O discovery anuncia um emissor **diferente** do configurado, e os tokens positivos usam o configurado: é o que prova o `IssuerValidator` estrito, porque com o `ValidIssuer` o emissor do discovery seria aceito. O emissor de teste imita o token real (`aud` texto, `sub` GUID, `typ` `Bearer`, `azp` do demo, `roles` array, `tenant_id` texto, 5 min) e aceita payload livre para os casos malformados | `WebApplicationFactory`, Kestrel em loopback |
| Suíte negativa de autenticação, em tabela (v2.7) | Nas rotas protegidas: sem token, malformado, `Bearer` vazio, esquema `Basic`; vencido há 2 min, `nbf` no futuro, sem `exp`; `aud` errada ou ausente; `iss` forasteiro, interno, com barra final, sufixo ou maiúsculas, e o anunciado pelo discovery; outra chave RSA com o mesmo `kid`, `alg=none`, HS256 com a chave pública como segredo e a receita HS256 antiga; `typ` igual a `ID`; `azp` fora da lista, ausente, vazio, em array ou numérico; sem `sub`, `sub` não-GUID, no formato `N` ou em array. Todos `401`. Um caminho não mapeado, sem token, também `401` — a `FallbackPolicy` | `[Theory]` sobre o OIDC falso |
| Opções do JwtBearer conferidas em execução (v2.7) | As `JwtBearerOptions` resolvidas do esquema têm `IssuerSigningKey` nulo, `IssuerSigningKeys` vazio, `SignatureValidator` e `IssuerSigningKeyResolver` nulos, as quatro validações ligadas, `ValidAlgorithms` igual a `[RS256]` e `IncludeErrorDetails` falso. É teste de execução, e não regra de arquitetura, porque o NetArchTest enxerga tipos, não propriedades | `IOptionsMonitor<JwtBearerOptions>` |
| Host em `Production`, com pedidos (v2.7) | Uma factory em `Production`: a subida falha com o client de demonstração na lista de `azp`, com `BaseUrl` em `http` ou com `AllowInsecureHttp`. E, com pedidos: `RequireHttpsMetadata` verdadeiro e `BackchannelTimeout` de 5 s nas opções resolvidas; `aud` errada leva `401` sem `error_description`; com a lista de `azp` vazia, a API sobe, registra o aviso e recusa um token válido; com o endereço dos metadados num listener que aceita a conexão e nunca responde, o `401` sai em menos de ~7 s, com o `Warning` capturado e sem o token no log | `WebApplicationFactory` em `Production`, HTTPS em loopback |
| Coleção com Keycloak real atravessando a API (v2.7) | Cada teste cria o próprio platform-admin, conclui o link de ações e obtém o token pelo device flow. **Ponte de contrato:** os tipos dos claims de um token real são os do emissor de teste — sem ela, os funcionais ficariam verdes sem provar nada sobre o Keycloak. Platform-admin real → `POST /tenants` → `202`. Token de um client com `azp` aceito e sem `gateway-api` → `401`, que só pode vir da audiência. Token do service account e **token ROPC do `admin-cli` do realm** → `401`, com a forma do token afirmada. `PublicBaseUrl` errado → `401`. Um refresh token já usado é recusado, e depois dele o novo também | Testcontainers, `ICollectionFixture`, harness de login |
| Vazamento do e-mail no token (v2.7) | Tokens de forma Keycloak que carreguem `email` e `preferred_username`, forjados no OIDC falso: sucesso, `403`, expirado, `aud` errada e assinatura inválida, conferindo corpo, `WWW-Authenticate`, **log e trace**. A captura é um sink do Serilog em memória, em `Debug`, e um exportador OpenTelemetry em memória; procura o e-mail em texto e em base64url, nos três alinhamentos, o token cru e o segmento do payload | Serilog em memória, OpenTelemetry InMemory exporter |
| Sem chave simétrica em produção, e as regras novas do realm (v2.7) | Arquitetura: nenhuma camada de produção usa `SymmetricSecurityKey`, e a Api não usa os tipos de `System.IdentityModel.Tokens.Jwt`; sem `ShowPII`, sem `IClaimsTransformation` nem segundo esquema de autenticação. No `RegrasDoRealmTests`: catálogo exato e nunca composto, `CreateDefaultClientScopes`, os três scopes fora dos defaults, o Audience Mapper só no `gateway-api`, todo client com `fullScopeAllowed`, `directAccessGrantsEnabled` falso e scopes explícitos, o demo só com device flow, nenhum grupo, `accessTokenLifespan` 300 e a rotação do refresh token | NetArchTest, leitura do JSON do realm |
| Keycloak parado, na CI (v2.7) | O job `Compose` faz um `GET` autenticado, para o Keycloak com `docker compose stop`, registra um tenant (`202`, `Pending`), religa o Keycloak, renova o token e espera `Active`. É a demonstração nº 1 do README com prova automática, e trava o cache de metadados na topologia real (§15) | `tools/jornada-compose.cs` |
| Autorização da rota de tenant (D2, planejado) | Unitário com a policy `TenantAdmin` real e um handler que aprova tudo: cada caminho de falha dos quatro requirements termina com `FailCalled`. `[Theory]` sobre todos os valores de `MemberStatus`, com a tabela esperada escrita à mão — um estado novo reprova até alguém decidir. Os nomes de `TenantStatus` e de `MemberStatus` travados. Com a DI real e uma porta falsa que conta chamadas, a pertença não é consultada com o papel ausente, com outro tenant nem com `platform-admin`. E o teste de subida da linha "Configuração de endpoint", que ganha objeto com a primeira policy de tenant | xUnit, `IAuthorizationService` montado pelo `AddAutorizacaoDaGateway` |

**A ordem real do lote do commit do provisionamento (v2.6).** O `SaveChanges` do provisionamento é um lote ordenado por tabela: `INSERT INTO members` → `INSERT INTO outbox_messages` (o `tenant-activated`) → `UPDATE tenants … WHERE id = @p AND xmin = @p`. O design da fatia C supunha que o `INSERT` do `Member` era o último comando, e que uma linha pré-inserida em `members` faria falhar o último comando do commit; na verdade ela derruba o **primeiro**, e o PostgreSQL não executa o resto do lote, o que não prova rollback de nada. A prova de atomicidade passou a ser a mudança do `xmin` durante o convite, que faz falhar o último comando depois de o `INSERT` do `Member` ter executado. Consequência na entrega concorrente: quem derruba a segunda entrega é o **índice único de `members`**, e não o `xmin` — sem o `IsConcurrencyToken()` do `xmin`, o teste concorrente continua verde, e só o de atomicidade fica vermelho.

O teste negativo de autorização é o mais importante do projeto: ele percorre as rotas registradas e falha se aparecer um endpoint sem cobertura para **cada uma das três regras** que a rota toca. Assim, um endpoint novo não entra desprotegido por esquecimento. A pertença no banco (ADR-011) entra como reforço da primeira regra nas rotas de governança, sem virar uma quarta (v2.7) (D2, planejado).

**Por que as três, e não só a primeira.** A suíte da v2.0 cobria apenas "token do tenant A → rota do tenant B" — o vetor que o `SameTenantRequirement` já bloqueia. Ela ficaria verde enquanto três vetores reais passavam: id de sub-recurso de outro tenant (§6.4), rota sem `{tenantId}` que opera sobre dados de tenant, e escopo de client sem vínculo de tenant (§10.1). Um gate mais estreito que o princípio que ele deveria provar é pior que nenhum gate, porque produz confiança.

---

## 14. Observabilidade

- **Logs estruturados** com Serilog, sempre com `tenantId`, `correlationId` e `sub` do ator. Nunca com tokens ou dados pessoais.
- **Traces** com OpenTelemetry cobrindo a requisição HTTP, o Outbox, o consumidor e as chamadas à Admin API.
- **Métricas:** duração e falhas de provisionamento, latência das chamadas ao Keycloak, divergências encontradas pela reconciliação, atraso da sincronização de eventos e rejeições por rate limit.
- **Health checks:** `live` verifica apenas o processo; `ready` verifica PostgreSQL, RabbitMQ e o Keycloak **obtendo um token do service account** pelo cache — custo zero por sonda enquanto o token vale. Obter o token, e não só ler a metadata OIDC como a v2.3 previa, é o que faz o `ready` provar a chave, o realm e o `private_key_jwt`. Os checks usam `failureStatus: Unhealthy`: o `MapHealthChecks` responde **200 para `Degraded`**, e um check degradado nunca tiraria a instância do balanceador.
- **Keycloak fora do ar e a autenticação (v2.7).** Com os metadados ainda não carregados e o Keycloak inalcançável, a API sobe, e um pedido com token responde **`401`** — nunca `500`. A biblioteca engole a falha de busca e reprova o token por falta de chave, avisando só pelo `EventSource` dela; sem um log próprio, o Keycloak fora viraria `401` em silêncio. Por isso `OnAuthenticationFailed` registra um `Warning` quando falta a chave de assinatura (`AutenticacaoLogs`, EventIds 2100 a 2103), limitado a um por intervalo de 30 s, e nunca o token. O mesmo aviso sai para um token de `kid` desconhecido assinado por outra chave: o texto dele diz as duas causas. Com os metadados já carregados, a API continua validando tokens de `kid` conhecido com o Keycloak parado. **Alternativa registrada:** `503` com `Retry-After` na falha de configuração, coerente com o argumento da §9.6; não entrou porque exige distinguir, no evento de falha, configuração indisponível de token inválido. O `/health/ready` cobre o Keycloak inteiro fora do ar, mas não um `jwks_uri` inalcançável (§19).

---

### 14.1. Contrato dos eventos de integração

Os eventos publicados pelo Outbox atravessam fronteira de processo — consumidores internos e Resource APIs — e por isso são contrato público, com as mesmas obrigações de compatibilidade de um endpoint REST.

**Envelope: CloudEvents 1.0.** Formato padronizado, com `id` estável (o que também dá à dedup do ADR-007 a chave que os eventos do Keycloak não oferecem — C7), `source`, `type`, `time` e `data`.

**Versão no `type`:** `identitygateway.tenant.registered.v1`. A regra de evolução é a usual de contrato:

| Mudança | Como fazer |
|---|---|
| Acrescentar campo **opcional** | Compatível. Mesma versão, consumidores antigos ignoram o campo |
| Remover campo, renomear, mudar tipo ou semântica | **Incompatível.** Publicar `.v2` **em paralelo** ao `.v1` até todos os consumidores migrarem, e só então aposentar o `.v1` |

Consumidor que recebe `type` desconhecido registra e descarta, em vez de falhar — um consumidor antigo não pode quebrar porque a Gateway passou a publicar um evento novo.

**O `OccurredOn` é parte do contrato e precisa voltar do JSON** (`init`, não só `get`); uma regra de arquitetura verifica todo evento do mapa do Outbox (v2.5).

---

## 15. Ambiente local e configuração como código

O `docker-compose.yml` sobe:

| Serviço | Papel |
|---|---|
| `gateway-keys` | One-shot: gera, na primeira subida, o par de chaves da Gateway e a senha do admin master do Keycloak num volume |
| `keycloak-db` | One-shot: cria o banco `keycloak` se ele não existir |
| `migrate` | One-shot: a imagem da API com `--migrate` aplica as migrations pendentes e encerra; a API só sobe depois que ele termina com sucesso. É o mesmo passo que, em produção, roda uma vez no deploy — a API nunca migra ao subir, porque várias réplicas tentariam migrar o mesmo banco ao mesmo tempo |
| `keycloak` | Versão **26.7.4** fixada; importa `keycloak/bootstrap/realm-identity-gateway.json` na primeira subida; porta publicada só em `127.0.0.1`; `KC_HOSTNAME=http://localhost:8081` com `KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true`, e o SMTP por ambiente (`SMTP_HOST=mailpit`, `SMTP_PORT=1025`, `SMTP_FROM=convites@identity-gateway.local`, conferidos por `test -n` no entrypoint); sobe depois do `mailpit` saudável (v2.6). Recebe também `PLATFORM_ADMIN_EMAIL` (padrão `platform-admin@identity-gateway.local`), que o import põe no usuário do bootstrap; o entrypoint recusa valor vazio ou com maiúsculas (v2.7) |
| `platform-admin-invite` | One-shot (v2.7): a imagem do Keycloak, com o `kcadm.sh`, depois do `keycloak` saudável. Envia **uma vez** o e-mail de ações do primeiro platform-admin, com link de 4 horas, e grava antes o marcador `platformAdminInviteSentAt` no realm; nas subidas seguintes, sai com `0` sem reenviar. Num volume anterior à v2.7, sai com `1` e manda rodar `docker compose down -v`. Reenvio só por comando: `docker compose run --rm -e REENVIAR=1 platform-admin-invite` |
| `postgres` | Um servidor com dois bancos: `keycloak` e `identitygateway`; porta publicada só em `127.0.0.1` (v2.6: agora há dado pessoal no banco) |
| `rabbitmq` | Mensageria, com a interface de gerenciamento habilitada |
| `mailpit` | `axllent/mailpit:v1.31.3` fixado (release de 2026-09-27): captura os e-mails de convite e de ações obrigatórias do Keycloak. Interface e API só em `127.0.0.1:8025`; o SMTP (1025) só na rede interna; healthcheck `["CMD", "/mailpit", "readyz"]`, o da própria imagem. **Existe desde a v2.6** — a v2.5 o listava sem ele estar no compose |
| `redis` | L2 do `HybridCache` — permissões efetivas do §9.6; porta publicada só em `127.0.0.1` (v2.6) |
| `seq` | Logs estruturados do Serilog, com interface de consulta; porta publicada só em `127.0.0.1` (v2.6: em falha, os logs podem carregar dado pessoal) |
| `jaeger` | Traces do OpenTelemetry; portas publicadas só em `127.0.0.1` (v2.7: agora circulam tokens reais, e um trace pode carregar a URL de uma chamada) |
| `api` | A API, com `Keycloak__Admin__PublicBaseUrl=http://localhost:8081`, que alimenta o `aud` do assertion (§10.2, v2.6) **e o emissor aceito nos access tokens** (v2.7). Publicada só em `127.0.0.1:8080`; sobe depois do `migrate` e do `platform-admin-invite` concluídos; sem `Jwt__SigningKey`, que saiu com o JWT simétrico; a lista de `azp` vem do `appsettings.Development.json` (v2.7) |
| `sample-resource-api` | A API de exemplo do Data Plane |

> **Hoje o compose ainda não tem `rabbitmq` nem `sample-resource-api`:** as duas linhas acima descrevem o destino da arquitetura, e entram no arquivo com o ADR-010 e com o Data Plane.

Os três últimos serviços de infraestrutura (`redis`, `seq`, `jaeger`) vêm do CleanStart e não são acréscimo gratuito: o `redis` sustenta o cache de permissões do §9.6, e `seq`/`jaeger` são o que torna verificável a exigência do M0 de **auditoria e observabilidade desde já** (§16).

O arquivo de bootstrap contém o mínimo para o ambiente local: realm com Organizations habilitado, client e service account da Gateway, os client scopes `gateway-roles` (§12.1), `gateway-tenant` (§12.2) e `gateway-api`, este com o Audience Mapper, **fora** dos defaults do realm, o catálogo de papéis, o client de demonstração e o usuário do primeiro platform-admin (v2.7). **Errata da v2.7 (E7):** até a v2.6, esta frase dava como presentes os scopes em `defaultDefaultClientScopes`, o Audience Mapper, o catálogo e o armazenamento de eventos, que o JSON não tinha. Os três primeiros entraram com a fatia D, na forma descrita abaixo; **o armazenamento de eventos do realm continua pendente** (§16). A evolução da configuração do realm é feita com o provider Terraform do Keycloak, porque arquivos de export não produzem diffs revisáveis nem aplicam mudanças incrementais.

**O que a fatia C acrescentou ao bootstrap (v2.6):** o papel de realm `tenant-admin`, o primeiro do catálogo a existir; `manage-users` no service account, ao lado de `manage-organizations` (§10.2); um componente de User Profile (`org.keycloak.userprofile.UserProfileProvider`, provider `declarative-user-profile`) que declara o atributo `tenant_id` só para `admin`, sem `unmanagedAttributePolicy` (§12.2) — o texto da configuração não usa `${...}`, que o import substituiria; o `smtpServer` com placeholders puros (`${SMTP_HOST}`, `${SMTP_PORT}`, `${SMTP_FROM}`, sem o prefixo `KC_`, que o Keycloak leria como opção dele), `auth`, `ssl` e `starttls` como `"false"` literais e os timeouts `connectionTimeout` 2000, `timeout` 3000 e `writeTimeout` 3000 (milissegundos), abaixo do `AttemptTimeout` de 10 s da Gateway — chaves conferidas em `DefaultEmailSenderProvider.java` L149-151 da 26.7.4; `resetPasswordAllowed: false`; e `adminEventsEnabled: true`. O `RegrasDoRealmTests` aceita `components` só com o provider de User Profile — provedores de chave continuam proibidos, que era o motivo da regra — e exige o atributo declarado só com `admin`, nenhuma `unmanagedAttributePolicy`, `resetPasswordAllowed` falso e o service account com exatamente os dois papéis.

**O que a fatia D acrescentou ao bootstrap (v2.7):** o catálogo de papéis completo — `platform-admin`, `tenant-admin`, `financial-manager` e `reader`, nunca compostos —, mais `offline_access` e `uma_authorization`, declarados só para sair do papel padrão; os três client scopes `gateway-roles`, `gateway-tenant` e `gateway-api`, nenhum deles default do realm, com os scope mappings do `gateway-roles` iguais ao catálogo; o atributo de realm `CreateDefaultClientScopes` igual a `"true"`, sem o qual declarar `clientScopes` apaga os scopes embutidos (§12.1); `defaultClientScopes` e `optionalClientScopes` explícitos em todo client, o service account com `basic` e `roles`; o client `identity-gateway-demo`, público, só com Device Authorization Grant, com `basic`, `acr` e os três `gateway-*`, `fullScopeAllowed: false` e código de dispositivo de 300 s, descrito como de demonstração local e fora do Terraform de produção; `accessTokenLifespan: 300`, `registrationAllowed: false`, `bruteForceProtected: true`, `revokeRefreshToken: true` e `refreshTokenMaxReuse: 0`; e o usuário do primeiro platform-admin, com `username` e `email` iguais a `${PLATFORM_ADMIN_EMAIL}`, só o papel `platform-admin`, sem credencial, sem atributos e sem grupos, com `UPDATE_PASSWORD` e `VERIFY_EMAIL`. As descrições são texto puro: as chaves de i18n do Keycloak (`${...}`) reprovariam a regra de placeholders. O `RegrasDoRealmTests` ganhou uma regra para cada item, cada uma provada por mutação, e o `NenhumaChaveDeCredencial` passou a percorrer também o JSON embutido do User Profile.

**Placeholder de SMTP sem valor derruba o import (v2.6).** Ao contrário do `${GATEWAY_CLIENT_CERT}`, que ficaria gravado como texto literal sem erro (abaixo), o `${SMTP_FROM}` literal **faz o import falhar**: o Keycloak 26.7.4 valida o remetente como endereço de e-mail ao importar o realm (`ERROR: Invalid sender address '${SMTP_FROM}'`), e o container sai com código 1. Por isso o entrypoint do `keycloak` confere `test -n` nas três variáveis `SMTP_*` antes de subir — a falta de uma delas encerra o container com uma causa legível, em vez de um import quebrado —, e o `KeycloakFixture` dos testes de integração define as mesmas três variáveis, apontando para o mailpit da rede Testcontainers. Sem elas, todo teste contra o Keycloak real cai junto com o fixture.

**`PLATFORM_ADMIN_EMAIL` vazio ou com maiúsculas não sobe (v2.7).** Uma variável ausente ficaria gravada como texto literal no `username` e no `email` do usuário do bootstrap, sem erro; e o import grava o e-mail em minúsculas, de modo que um valor com maiúsculas não seria achado depois por uma comparação exata. O entrypoint do `keycloak` confere as duas coisas antes de subir, e o one-shot ainda compara o e-mail em minúsculas. O padrão, `platform-admin@identity-gateway.local`, é o mesmo no compose, no app da CI e no README, e um teste de arquitetura confere que é igual nos três e minúsculo.

**Endereço público do Keycloak (v2.6).** Sem `KC_HOSTNAME`, o link do e-mail sairia com o host interno (`http://keycloak:8080`), e trocar o host à mão não funciona, porque o clique é validado contra o emissor gravado no token. Com `KC_HOSTNAME=http://localhost:8081` e `KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true`, o link, o `iss` e o discovery usam `localhost:8081`, e a Admin API continua respondendo por `keycloak:8080`, sem redirecionar. A API recebe `Keycloak__Admin__PublicBaseUrl=http://localhost:8081`, que alimenta o `aud` do assertion e, desde a v2.7, o emissor aceito nos access tokens (§10.2). A porta `8081` aparece em três lugares — a porta publicada, o `KC_HOSTNAME` e o `PublicBaseUrl` —, e um teste de arquitetura lê o `docker-compose.yml` e exige que os dois últimos sejam iguais. O SMTP vem do ambiente: plugar outro servidor, ou o notification-hub quando ele aceitar SMTP, é trocar configuração, sem código na Gateway.

**Organizations precisa estar ligado no realm.** A v2.3 afirmava que era *feature flag* de build (`KC_FEATURES=organization`): era verdade até a 25.x, quando o recurso era *preview*, e deixou de ser na 26.0, quando virou recurso padrão. O que continua obrigatório é **`organizationsEnabled: true` no JSON do realm** — sem ele, o import passa sem erro e `POST /admin/realms/{realm}/organizations` responde **404** "Organizations not enabled for this realm", falha silenciosa que só apareceria no primeiro provisionamento. Um *smoke test* chama esse endpoint e falha explicitamente em 404, para que o erro apareça na subida e não na primeira demonstração.

**Credenciais do ambiente local (v2.4).** Nada de credencial literal, também fora do JSON do realm:

- **Chave da Gateway.** O `gateway-keys` gera um RSA 2048 e um certificado autoassinado com validade explícita. A chave privada fica numa subpasta do volume que **só a API monta**, com dono igual ao usuário não-root da imagem e modo `0400`; o certificado, em base64 numa linha só, fica na subpasta do Keycloak. O entrypoint do Keycloak confere que o arquivo existe (`test -s`) e o exporta para `GATEWAY_CLIENT_CERT`, que o import do realm substitui no placeholder `${GATEWAY_CLIENT_CERT}` — uma variável ausente ficaria gravada como texto literal, sem erro, e só falharia na autenticação.
- **Admin master do Keycloak.** A senha também é gerada pelo `gateway-keys` e exibida **uma vez** no log; o entrypoint a exporta para `KC_BOOTSTRAP_ADMIN_PASSWORD`. Com a porta publicada só em `127.0.0.1`, o console não fica exposto à rede local. **A senha ganhou um segundo consumidor (v2.7):** o one-shot `platform-admin-invite` monta, só para leitura, a subpasta do volume que tem o `/keys/admin-password`, e faz login no `master` com ela a cada subida, antes de qualquer outro passo. A senha nunca vira variável declarada no compose, que apareceria no `docker inspect`. **Consequência:** trocar a senha ou apagar o admin do `master` do compose — o console da 26.x o chama de temporário e sugere a troca — quebra toda subida seguinte, e a `api` não sobe; o one-shot sai com uma mensagem própria, e a saída é `docker compose down -v` (§19). O log do `gateway-keys` imprime essa senha, e por isso nunca entra no log da CI.
- **Geração atômica e idempotente.** Tudo é gerado num diretório temporário e movido no fim, com um marcador gravado por último; sem o marcador, a geração recomeça. Os scripts são **inline** no `docker-compose.yml`: o `.editorconfig` do repositório define fim de linha CRLF, e um `.sh` montado por bind mount chegaria ao container com `\r`.
- **Chave e realm andam juntos.** O import é `IGNORE_EXISTING`: se só o volume das chaves for apagado, o certificado registrado no realm deixa de bater com a chave nova. Isso **falha alto**: o `ready` da API obtém token e responde 503 com `invalid_client` no log, e o README manda `docker compose down -v`, que apaga os dois volumes juntos. **O `IGNORE_EXISTING` vale também para o que a v2.6 acrescentou** (v2.6): o papel, o `manage-users`, o User Profile e o SMTP só entram no primeiro import, e um volume antigo fica sem eles. Para isso falhar alto, e não virar 24 h de retry, o `KeycloakHealthCheck` confere que o token do service account traz `manage-users` em `resource_access.realm-management.roles`; sem ele, o `ready` responde 503 com uma descrição neutra do que falta (o token sem `manage-users`) e a indicação do README; o `docker compose down -v` fica a cargo de quem lê, só no ambiente local. **Um volume anterior à v2.7 falha ainda mais cedo (v2.7):** o one-shot `platform-admin-invite` confere se o realm tem o scope `gateway-api`; sem ele, sai com `1` e a instrução do `down -v`. Como a `api` depende do one-shot, o `docker compose up` falha com a causa no log, em vez de a API subir e todo token dar `401`. O `KeycloakHealthCheck` não muda: ele pega o volume de antes da v2.6, e o one-shot, o de antes da v2.7.
- **O banco do Keycloak** é criado por um one-shot idempotente, e não por script de `initdb` — este só roda com volume vazio, e quebraria quem já tem o volume do Postgres.
- **Nada disto é produção.** `start-dev`, `http://` e chave em volume são de desenvolvimento, e o cabeçalho do compose diz isso (§10.2).

**O compose sobe na CI.** Um job próprio executa `docker compose up --wait` até a API ficar `ready` — o que, pelo health check da §14, prova chave, realm e `private_key_jwt` —, registra um tenant e espera o provisionamento chegar a `Active` — o que prova as migrations e o Outbox, que o `ready` não olha —, derruba sem apagar volumes e sobe de novo, provando a idempotência dos one-shots. A promessa "funciona na primeira tentativa" do M0 passa a ser verificada a cada PR, não só no dia em que alguém a testou à mão. **Desde a v2.6, o job também confere o convite do admin do tenant:** depois do `Active`, consulta o mailpit pelo destinatário exato, exigindo ao menos uma mensagem, nunca exatamente uma — a entrega "pelo menos uma vez" pode mandar duas (§11.5) —, lê o campo `Text` da mensagem (porque o JSON escapa o `&` como `\u0026` e o HTML traz `&amp;`), exige o prefixo `http://localhost:8081/realms/identity-gateway/login-actions/action-token?key=` e faz um `GET` no link, exigindo `200` com a página de ações, sem a de erro — o que prova que o link abre, e não só o formato dele. O e-mail e o slug são únicos por execução (`admin+<timestamp>@acme.test`), porque e-mail em uso por outra conta é falha permanente (§9.1).

**Desde a v2.7, quem roda a jornada é um app C#, e o token vem do Keycloak.** O job deixou de montar à mão um JWT simétrico e de ler o mailpit com `jq`: chama `tools/jornada-compose.cs`, um app de arquivo único que usa o mesmo harness de login dos testes (§13) — o device flow no client de demonstração, pelas páginas do próprio Keycloak. As fases, na ordem: (1) **o convite do platform-admin sai uma vez só** — a contagem de mensagens para o e-mail dele é **exata**, uma; o one-shot é executado de novo com o convite ainda pendente (`docker compose run --rm --no-deps platform-admin-invite`, saída `0`), e a contagem continua uma. Contar zero depois de um `down` e um `up` seria vacuoso, porque o mailpit volta vazio; (2) **a jornada** — o link do platform-admin, o device flow com login num passo só, a receita HS256 antiga respondendo `401`, `POST /tenants` → `202` → `Active` e o convite do admin do tenant, como acima; (3) **o Keycloak parado** — um `GET` autenticado, `docker compose stop keycloak`, `POST /tenants` → `202` e `Pending`, `docker compose start keycloak`, a renovação do token e a espera do `Active`. É a demonstração nº 1 do README (§16), e trava o cache de metadados na topologia real; fica `stop` e `start`, e não `pause`, porque é a sequência que o README manda fazer; (4) **a segunda subida** sobre os mesmos volumes, com o one-shot saindo `0` e dizendo que o convite já foi enviado. Toda asserção de status é exata, nunca "diferente de `200`": com 5 minutos de token, um `401` por vencimento viraria verde. O workflow roda com `pipefail`, cada passo longo tem o próprio prazo, o app compila antes de o Docker subir, e os endereços discados usam `127.0.0.1` — `localhost` fica só como endereço público do Keycloak. Em falha, o job grava os logs do one-shot, da `api` e do `keycloak` — nunca o do `gateway-keys`, que imprime a senha do master — e, do mailpit, só metadados. O app nunca imprime token, código de dispositivo, link, senha nem HTML.

**`offline_access` fica fora do papel padrão do realm.** O papel vem ligado por padrão para todo usuário, e sessões offline **não** são encerradas por `POST .../users/{id}/logout` — o que permitiria a um usuário desativado continuar renovando acesso depois da revogação (§9.5). O projeto não usa offline tokens. **Entregue na v2.7 (errata E7):** até a v2.6, este parágrafo descrevia a remoção como feita, e o JSON não a tinha. E não é "hardening de uma linha": o import recria o papel no padrão se faltar o papel **ou** o client scope de mesmo nome. O JSON declara os dois — o papel, e o scope sem mappers, fora dos defaults e sem nenhum client que o ofereça —, e faz o mesmo com o papel `uma_authorization`. O papel padrão fica `[manage-account, view-profile]`, e um teste contra o Keycloak real confere que ele não voltou.

**Bootstrap do primeiro `platform-admin`: sem senha, por convite (v2.7).** O papel não é atribuível pela API (§11.2) e é exigido por `POST /tenants`, então precisa nascer no bootstrap — mas o `realm-identity-gateway.json` é versionado num repositório público. **O usuário nasce no próprio JSON do realm, sem credencial nenhuma:** `username` e `email` iguais a `${PLATFORM_ADMIN_EMAIL}`, só o papel `platform-admin`, e as ações `UPDATE_PASSWORD` e `VERIFY_EMAIL`. A senha é definida pela própria pessoa, no Keycloak, pelo link de um e-mail de ações — o mesmo mecanismo do convite de qualquer membro (ADR-003). A v2.6 previa gerar uma senha aleatória e exibi-la uma vez no log: uma credencial num log contraria o próprio ADR-003, e isso nunca foi implementado.

Quem dispara o e-mail é o one-shot `platform-admin-invite`, **uma vez só**. Os passos: (0) login no `master`, antes de tudo; (1) o realm tem o scope `gateway-api`? Senão, é um volume anterior à v2.7, e ele sai com `1`; (2) o realm tem o atributo `platformAdminInviteSentAt`? Então o convite já saiu, e ele sai com `0`; (3) busca o usuário pelo e-mail, com `exact=true`, exige exatamente um e confere a forma do bootstrap — só o papel `platform-admin`, nenhum papel de client, nenhum `tenant_id`, nenhum grupo, o e-mail igual ao configurado, em minúsculas; qualquer divergência sai com `1`; (4) sem `UPDATE_PASSWORD` pendente, o convite já foi concluído, e ele sai com `0`; (5) **grava o marcador no realm antes de enviar**, e só então manda o `execute-actions-email`, com `lifespan=14400` (4 horas). **O one-shot nunca atribui papel nem cria usuário:** a primeira versão dele "garantia o papel", e promoveu um `tenant-admin` a `platform-admin`, ao vivo. O marcador é um atributo do realm, e não um arquivo num volume: o arquivo falhou por permissão depois do envio, e cada subida reenviava o convite — o equivalente a um reset periódico de senha. As respostas do `kcadm` são lidas sem `--fields`: com ele, os objetos aninhados vêm vazios, e as travas ficariam vacuosas. O link vale 4 horas porque um link de ações continua trocando a senha da conta até expirar, mesmo depois de o convite ter sido aceito por outro link (§19). O reenvio é só por comando explícito (`docker compose run --rm -e REENVIAR=1 platform-admin-invite`), que pula só a checagem do marcador e recusa rodar se o convite já foi concluído. Um teste de CI falha se o JSON de bootstrap contiver qualquer credencial literal. **Isto é o bootstrap do ambiente local**, como o resto do compose; o bootstrap de produção não está decidido nesta versão.

A documentação interativa usa o suporte nativo a OpenAPI 3.1 do .NET 10 e a interface Scalar. **O esquema de segurança OAuth2 no documento (Authorization Code com PKCE e Client Credentials) segue pendente (v2.7)**, com destino no M7': até lá, o Scalar não obtém token sozinho, e a demonstração usa o device flow pelo terminal.

---

## 16. Roadmap

Cada marco termina com algo demonstrável e testado.

| Marco | Entrega | Resultado demonstrável |
|---|---|---|
| **M0 · Fundação** | Clone do CleanStart renomeado (feature `Orders` removida, `Domain/Common`, `Application/Common`, interceptors e `ArchitectureTests` preservados), Docker Compose unificado (Keycloak com `organizationsEnabled` no realm, e o compose verificado na CI), bootstrap do realm com os três client scopes (`gateway-roles`, `gateway-tenant` e `gateway-api`, v2.7) e sem `offline_access`, platform-admin convidado por e-mail, sem senha gerada (v2.7), health checks, CI com testes de arquitetura, **auditoria e observabilidade desde já** | `git clone` + `docker compose up` + primeiro `curl` funcionam na primeira tentativa |
| **M1 · Tenants** | Registro com `initialAdminEmail`, provisionamento via Outbox com correlação por atributo, status, suspensão **com efeito no Keycloak** (membros e clients, idempotente e retomável), encerramento, reconciliação bidirecional, **downgrade de plano rejeitado** (N8) | Tenant criado com o Keycloak fora do ar é provisionado quando ele volta — demonstrável por `curl` |
| **M2 · Membros e papéis** | Convite com ciclo de vida completo (§9.9: expira por job, reenvia, cancela → `Revoked`), desativação com revogação de sessões, exclusão LGPD, papéis com `RoleAssignmentPolicy`, vagas com regra explícita | Suíte de autorização negativa verde **nas três regras de isolamento** — continuam três: na Gateway, a pertença no banco reforça a primeira (v2.7) (D2, planejado) |
| **M3 · Data Plane** | `IdentityGateway.Client.AspNetCore` e `SampleResourceApi` | Requisição de negócio autorizada sem nenhuma chamada à Gateway |
| **M4 · Federação e discovery** | Domínios, IdP externo por tenant, endpoint de discovery, sincronização de eventos do Keycloak | Login federado cria o membro e respeita o limite do plano |
| **M5 · Permissões finas** | Permission Sets, permissões efetivas, cache com invalidação por evento | Permissão revogada deixa de valer sem novo login |
| **M6 · M2M** | Clients com `private_key_jwt` (§10.2), rotação de credenciais, **limite `MaxClients`**, clients desabilitados na suspensão do tenant | Serviço parceiro chamando a Sample API com Client Credentials; tenant suspenso derruba também o M2M |
| **M7 · Hardening** | Step-up com `StepUpRequirement` e resposta `insufficient_user_authentication`, rate limiting, README detalhado com `curl` reproduzível | Repositório pronto para apresentação |

**Andamento do M0 e do M1 (v2.7).** A implementação avança em fatias verticais, e uma fatia pode cruzar marcos:

| Fatia | Marco | Estado |
|---|---|---|
| Registro de tenant (`POST /tenants` → `Pending` + Outbox) | M1 | Entregue (PR #1) |
| **A · Fundação Keycloak**: Keycloak no compose e na CI, lado administrativo do realm, service account com `private_key_jwt`, `EnsureOrganizationAsync` | M0 + M1 | Entregue (PR #2) |
| **B · Consumidor**: transporte, provisionamento, `ProvisioningFailed` | M1 | Entregue (PR #3) |
| **C · Convite do admin inicial**: `EnsureInvitedUserAsync`, o `Member` mínimo, a vaga do admin na ativação, o e-mail pelo SMTP do Keycloak e o `mailpit` no compose | M1 | Entregue (PR #5) |
| **D · Tokens do Keycloak**, em dois PRs. **D1:** a API aceita só access tokens do Keycloak (RS256, emissor, audiência, `azp`, `typ` e `sub`), o realm emite o token da §10.1, o primeiro platform-admin é convidado por e-mail, e a demonstração e a CI obtêm o token pelo device flow. **D2:** `GET /tenants/{tenantId}`, com a policy `TenantAdmin` e a pertença no banco (ADR-011) | M0 + M1 | D1 entregue; D2 (D2, planejado) |

**Pendente do M0 depois da fatia D (v2.7):** a tabela de auditoria — que o M0 promete "desde já" —, o armazenamento de eventos do realm e o RabbitMQ. Saíram da lista, entregues pela D1: os client scopes `gateway-roles`, `gateway-tenant` e `gateway-api`, o Audience Mapper, o catálogo de papéis completo, o `offline_access` fora do papel padrão, o primeiro platform-admin (sem senha gerada, por convite), a rotação do refresh token e a API validando tokens do Keycloak. **O critério "primeiro `curl`" do M0, com token do Keycloak, está fechado:** o JWT simétrico do template não existe mais. O armazenamento de eventos custa outro `docker compose down -v` quando entrar, e os eventos de login guardariam o username, que é o e-mail: a retenção é decidida junto.

**Pendente do M1 depois da fatia C (v2.6):** suspensão, encerramento, reconciliação, retry manual de `ProvisioningFailed` e downgrade de plano. O ciclo completo do convite (expiração, reenvio, cancelamento, `POST /members`) é o M2, e a fatia C deixa prontos para ele o `Member`, `EnsureInvitedUserAsync` com o papel na porta e a `IInvitationPolicy`. Do M1, a fatia D entrega a leitura do próprio tenant, `GET /tenants/{tenantId}` (v2.7) (D2, planejado); a listagem e o override do platform-admin chegam com a auditoria.

**Ponto de "apresentável" declarado:** ao fim do **M3 + M7'** (step-up, rate limiting e README), o repositório está completo e defensável — ADRs, testes, limites honestos e demonstração reproduzível. M4 a M6 são incrementos sobre uma base que já pode ser mostrada, não pré-requisitos para mostrá-la. Num projeto sem prazo, esse marco é a defesa contra o modo de falha mais comum: seis marcos pela metade e nada apresentável.

**A demonstração é por README com `curl`**, o que promove o M0 a peça crítica — é o primeiro contato do avaliador, e uma falha ali encerra a leitura antes dos ADRs. **Desde a v2.7, ela tem passos no navegador**, e isso é consequência do próprio ADR-003: a senha só é digitada no Keycloak. O avaliador abre o mailpit, conclui o convite pelo link e aprova o código do device flow numa página do Keycloak; todas as chamadas à API continuam sendo `curl`. Duas demonstrações que o README deve conter, porque provam competências difíceis em poucos comandos:

1. **Consistência sem transação distribuída:** obter o token e fazer **um `GET` autenticado antes de parar o Keycloak** — a API guarda as chaves do realm ao validar o primeiro token, e sem isso o pedido seguinte responderia `401` — → `docker compose stop keycloak` → `POST /tenants` responde `202` normalmente, **dentro dos 5 minutos de vida do token** → `docker compose start keycloak` → renovar o token → o tenant vira `Active` sozinho. O job `Compose` da CI roda a mesma sequência (§15).
2. **Isolamento multi-tenant:** com token do tenant A, tentar a rota do tenant B (403), e tentar um `memberId` do tenant B dentro da rota do tenant A (404). **O `403` já é demonstrável com a primeira rota de tenant** (v2.7) (D2, planejado): o admin convidado lê o próprio tenant (`200`) e recebe `403` em qualquer outro. O `404` de sub-recurso chega com as rotas de membro, no M2.

---

## 17. Anti-patterns proibidos

**1. Resource Owner Password Credentials (ROPC).** Receber usuário e senha na API e repassar ao Keycloak. O fluxo foi removido no OAuth 2.1, expõe credenciais ao backend e anula o MFA. O login interativo das aplicações usa sempre Authorization Code com PKCE. **Exceção nomeada (v2.7):** o client de demonstração `identity-gateway-demo`, só do ambiente local, usa o Device Authorization Grant — que também não é ROPC: a senha continua digitada só na página do Keycloak (ADR-003).

**2. Validar tokens chamando a Gateway.** Transformaria a Gateway em gargalo e em ponto único de falha, criando um monólito distribuído. A validação é local, com JWKS e `Microsoft.AspNetCore.Authentication.JwtBearer`.

**3. Armazenar senhas, hashes ou segredos de usuário no banco da Gateway.** Viola o princípio de responsabilidade única do Keycloak e amplia o escopo de compliance (LGPD). A exceção são segredos de clients M2M, que também não são guardados: são devolvidos uma única vez e ficam apenas no Keycloak.

**4. Chamar a Admin API do Keycloak fora da Infrastructure.** Controllers, endpoints e handlers conhecem apenas `IIdentityProvider`. A regra é verificada por teste de arquitetura.

**5. Reassinar ou enriquecer tokens na Gateway.** Criaria um segundo Authorization Server. Claims vêm somente de Protocol Mappers (ADR-004).

**6. Chamar o Keycloak dentro da transação de um comando.** A escrita local e o evento no Outbox são atômicos; o efeito externo acontece depois, de forma idempotente (ADR-006).

**7. Confiar apenas na presença do claim `tenant_id`.** Todo acesso de um ator de tenant compara o tenant da rota com o do token (`SameTenantRequirement`) **e** verifica que cada sub-recurso da rota pertence a esse tenant (§6.4) — as duas coisas, porque a primeira sozinha não impede informar o id de um recurso alheio. A exceção nomeada é o **client de plataforma** (§10.1), cujo acesso é irrestrito por desenho e auditado por chamada. **Na Gateway, o claim e a rota iguais também não bastam (v2.7) (D2, planejado):** nas rotas de governança, o ator precisa ser `Member` do tenant no banco, porque o `tenant_id` do token é forjável por grupo (ADR-011).

**8. Retry automático em POST para a Admin API.** Pode criar recursos duplicados. A idempotência vem do padrão "consultar antes de criar", não do retry.

---

## 18. Critérios de pronto por funcionalidade

Uma funcionalidade só é considerada pronta quando tem:

- endpoint documentado no OpenAPI, com exemplos de requisição e de erro;
- teste unitário das regras de domínio envolvidas;
- teste de integração do caminho feliz contra o Keycloak real;
- **teste negativo para cada regra de isolamento que a funcionalidade toca** — tenant da rota, pertencimento de cada sub-recurso ao tenant, e escopo de client M2M. Não é "se a rota tiver `{tenantId}`": essa formulação deixava passar sub-recursos, rotas sem `{tenantId}` e clients de plataforma. As regras continuam três: nas rotas de governança da Gateway, o teste da primeira cobre também o ator que tem o claim certo e não é `Member` do tenant no banco (v2.7) (D2, planejado);
- entrada de auditoria para ações de escrita, **sem valores de credencial** (§10.3);
- ADR, se a funcionalidade introduzir uma decisão nova.

O quarto item é o gate que sustenta o princípio 5. Um critério mais estreito que o princípio produz suíte verde com isolamento furado — foi o que a revisão da v2.0 encontrou.

---

## 19. Limites conhecidos

Registrar os limites faz parte do projeto: eles mostram onde a arquitetura escolhe simplicidade de forma consciente.

- **Políticas de senha, força bruta e rotação de refresh token são por realm**, portanto iguais para todos os tenants (consequência do ADR-001). Um cliente corporativo com política própria de compliance — expiração a cada 90 dias, por exemplo — exigiria realm dedicado. Na prática o impacto é menor do que parece: esse perfil de cliente costuma usar IdP federado, e então a política de senha é do IdP dele, não deste realm.
- **Um usuário pertence a um único tenant** (ADR-009). *Consequência de negócio:* um consultor que atende dois clientes precisa de duas contas com e-mails diferentes, e um MSP não consegue usar a plataforma como um único usuário. A evolução exigiria papéis por Organization, seleção de organização no login e um `SameTenantRequirement` que validasse contra uma lista em vez de igualdade.
- **Permissões finas têm janela de atraso** igual ao TTL do cache quando o evento de invalidação não chega.
- **A suspensão de tenant não invalida tokens já emitidos**, de membro ou de client M2M: eles sobrevivem até expirar, no máximo 5 minutos. Desabilitar usuários e clients impede a **emissão** de novos tokens, não o uso dos que já circulam — é a mesma janela de §9.5, aplicada ao tenant inteiro.
- **A queda da Gateway nega permissões finas com cache frio** (§9.6), respondendo 503. Endpoints de nível 1 seguem funcionando. Mitigado por stale-while-revalidate e warm-up, não eliminado.
- **A queda do Keycloak degrada o Data Plane em até 5 minutos** — o tempo de vida do access token. Passada essa janela, nenhuma requisição de negócio é autorizada, porque a validação local depende de token vivo (§2.1).
- **A desativação de membro não invalida o access token já emitido**, que sobrevive até 5 minutos. É o preço da validação stateless do ADR-002.
- **A sincronização Keycloak → Gateway tem latência** igual ao intervalo de polling (ADR-007), e é **at-least-once com perda possível**: se o poller ficar parado mais que o `eventsExpiration` do realm, os eventos do intervalo deixam de existir. Um alarme sinaliza a aproximação desse limite.
- **Há uma janela sem `tenant_id` no primeiro login federado** (§12.2): entre o login e o processamento do evento pelo poller, o usuário recebe 403 nas rotas de tenant.
- **O limite de usuários não bloqueia o primeiro login federado**; o excesso é detectado, auditado e sinalizado, e o limite volta a valer por janela de tolerância (fluxo 9.4). Sem federação, o limite é estrito.
- **O encerramento de tenant não remove dados do Keycloak** (§9.8); desabilita e marca `Terminated`. A remoção física é operação administrativa fora da API, e o `TenantSlug` permanece reservado.
- **O Keycloak roda em instância única** no ambiente local; clustering e multi-site ficam documentados, não implementados.
- **Rotacionar a chave da Gateway causa indisponibilidade** (v2.4). Com o certificado registrado no atributo do client, trocar a chave invalida o anterior no mesmo instante. A evolução é o client ler a chave de um JWKS (`use.jwks.url`) que exponha as duas chaves durante a janela de transição (§10.2).
- **O `depends_on` do Keycloak na API é conveniência do ambiente local**, para que o primeiro `curl` funcione. Ele não é garantia de disponibilidade: a demonstração do M1 exige que a API responda com o Keycloak parado, e o provisionamento é que espera por ele (§9.1). **Desde a v2.7, a `api` depende de um one-shot que depende do Keycloak saudável:** `docker compose up` com o Keycloak parado não sobe a API. A demonstração nº 1 para o Keycloak **depois** do `up`, e não quebra.
- **`ProvisioningFailed` não tem saída automática** até existirem o retry manual e a reconciliação (v2.5); até lá, sair dele exige intervenção no banco.
- **Com N réplicas e Keycloak lento, duas réplicas podem processar a mesma mensagem do Outbox em paralelo.** O
  atraso da tentativa (o lease implícito da reserva) vai de 10 a 72s, e a publicação de uma mensagem pode levar
  até o teto de 30s da resiliência multiplicado pelo `BatchSize` (20) se o lote inteiro travar no Keycloak — o
  lease pode expirar, e outra réplica reservar e reprocessar a mesma mensagem antes da primeira terminar. O
  resultado converge (o segundo `EnsureOrganizationAsync` acha a Organization que o primeiro criou; o segundo
  `SaveChanges` do tenant recebe `409`/`DbUpdateConcurrencyException` pelo `xmin` e é reprocessado — com o `Member` da v2.6, quem falha primeiro é o índice único de `members`, §13), mas o log
  fica ruidoso com o conflito de concorrência. Fica sem correção dedicada até o ADR-010 cobrir o `OutboxWorker`
  (v2.5).
- **Os números do Outbox estão dimensionados para o provisionamento em processo** (teto de 60s, 1500 tentativas): uma mensagem envenenada de outro tipo repetiria por ~25h. A revisar quando o broker chegar (v2.5).
- **O e-mail de convite pode sair mais de uma vez** (v2.6). Qualquer falha entre o envio pelo Keycloak e o commit — commit perdido, conflito de `xmin`, violação do índice de `members` — faz a mensagem voltar, e o convite é reenviado (§11.5). **Duas entregas concorrentes da mesma mensagem também geram dois convites:** as duas passam pelo passo 5 da §11.6 antes de qualquer commit, e o usuário ainda tem `UPDATE_PASSWORD` pendente nas duas. É o caso das duas réplicas do item das réplicas; com a fatia C, quem derruba a segunda entrega no commit é o índice único de `members`, e não o `xmin`, porque o `INSERT` do `Member` é o primeiro comando do lote (§13).
- **Corrida residual na atribuição do papel** (v2.6). Quando duas entregas concorrentes encontram o papel entre os disponíveis, as duas fazem `POST .../role-mappings/realm`, e a perdedora recebe `400` (`RoleMapperResource`, 26.7.4: a `ModelException` do mapeamento repetido vira `BAD_REQUEST`). É **transitória**: sobe como `HttpRequestException`, não leva a `ProvisioningFailed`, e o ciclo seguinte encontra o papel atribuído e completa. A correção cabe no molde da do vínculo (§11.6, passo 3) — no `400`, uma releitura dos atribuídos —, e fica para quem tocar o adaptador de novo.
- **Um link de ações anterior continua válido depois do aceite, e troca a senha de uma conta ativa** (v2.6; confirmado ao vivo e estendido na v2.7). Cada link é de uso único, mas os outros emitidos para o mesmo usuário valem até expirar: com cinco e-mails e o quinto link concluído, o primeiro ainda levou ao formulário de senha, e a senha nova passou a valer. Não reenviar depois do aceite (§11.6, passo 5) não invalida um link enviado antes. Vale para o convite do admin do tenant — link de 7 dias, mais os reenvios do Outbox — **e para o do platform-admin** — no máximo um link automático, de 4 horas, mais os reenvios manuais. Depois do aceite, **revogam de fato:** remover o usuário (no compose, `down -v`), ou desabilitar ou remover a chave HMAC antiga do realm, o que derruba também todos os refresh tokens; rebaixá-la a passiva não basta, porque chave passiva continua verificando. **Só bloqueiam enquanto durarem, e são reversíveis:** desabilitar o usuário (reabilitado, todo link antigo volta a funcionar) e trocar o e-mail (voltar ao antigo ressuscita os links). **O link de 7 dias do convite do admin do tenant é risco aceito nesta versão;** a operação de plataforma que trocar ou reenviar esse convite trata a revogação e avalia encurtar o prazo.
- **O `Member` do admin inicial fica `Invited` até a sincronização do ADR-007** (M4). A expiração de convite do M2 **não pode chegar antes dela** sem outra forma de saber do aceite, senão expira um admin que já entrou (§9.9).
- **Admin órfão** (v2.6): se o e-mail sai e a janela de provisionamento esgota antes do commit, o tenant fica `ProvisioningFailed` com um usuário habilitado, com `tenant-admin` e com link válido. O retry manual e a reconciliação precisam tratar esse caso, desabilitando o usuário ou reaproveitando-o.
- **Tenants registrados antes da v2.6 vão para `ProvisioningFailed`**, porque não têm o e-mail guardado, e só saem pelo retry manual, informando o e-mail.
- **E-mail digitado errado entrega o tenant a um estranho**, e não há revogação pela API: o platform-admin não opera rotas de membro (§10.1). O runbook provisório é desabilitar o usuário no Keycloak; uma operação de plataforma para trocar ou reenviar o convite do admin inicial fica para o M1/M2 (§8). **Agravado na v2.7:** com a primeira rota de tenant, o destinatário errado passa a ler o tenant pela API, porque a pertença no banco o reconhece como `Member` (D2, planejado).
- **A mesma pessoa não administra dois tenants**: o segundo `POST /tenants` com o mesmo e-mail termina em `ProvisioningFailed`. É consequência do ADR-009 e da unicidade de e-mail no realm.
- **SMTP, papel `tenant-admin`, User Profile e permissões do realm só entram no primeiro import** (`IGNORE_EXISTING`, §15). Um volume anterior à v2.6 exige `docker compose down -v`; o `ready` avisa, mas não corrige.
- **O e-mail apagado não some de imediato do disco.** O apagamento é lógico: WAL, *dead tuples* e backups guardam o valor pela retenção deles (§10.3).
- **Alcance do `manage-users`** (v2.6): o service account atribui qualquer papel que não seja de administração, inclusive `platform-admin`, e os de administração que ele mesmo tem; troca senhas e desabilita usuários, inclusive platform-admins. Quem tiver a chave da Gateway pode criar uma conta própria com esses papéis, e o acesso **sobrevive à rotação da chave** (§10.2).
- **O convite não sai pelo notification-hub** (v2.6). O Keycloak só envia e-mail por SMTP, trocar o transporte exige um provider num SPI interno, e o hub só recebe HTTP. A integração depende de o hub aceitar SMTP ou de uma extensão Java no Keycloak; como o SMTP vem do ambiente, plugar o hub depois é trocar configuração, sem código na Gateway.
- **No M4, a conta convidada sem senha é alvo de vínculo automático no primeiro login federado.** Nunca ligar vínculo de conta sem verificação.
- **Um membro existente pode tomar de antemão o e-mail do futuro admin**, se a troca de e-mail no Keycloak não exigir verificação, e a correlação por `tenant_id` derruba o provisionamento (§9.1). Se a troca de e-mail vem ligada por padrão na 26.7.4 não foi verificado.
- **Quem perde a corrida de slug no `POST /tenants` recebe `500`, e não `409`** (herdado da vertical de registro): o índice único fecha a janela entre a checagem e o `INSERT`, mas a violação ainda não é traduzida em `409`.
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
