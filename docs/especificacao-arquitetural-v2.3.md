# IdentityGateway — Especificação Arquitetural

> Multi-Tenant Identity & Governance API em .NET 10 com Keycloak
> **Versão 2.3** · Status: aprovada para implementação

---

## 0. O que mudou da v2.2 para a v2.3

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

## 0.1. O que mudou da v2.1 para a v2.2

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

## 0.2. O que mudou da v2.0 para a v2.1

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
       │     REST + Bearer JWT    │     PKCE / Client Cred.  │     Bearer JWT
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
| (2) | Authorization Code + PKCE (interativo) ou Client Credentials (M2M), direto no Keycloak |
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
5. **Isolamento multi-tenant verificável.** Toda **regra** de isolamento tem um teste negativo correspondente — não apenas as rotas com `{tenantId}` no template. As regras são três: tenant da rota × tenant do token; pertencimento de cada sub-recurso ao tenant da rota; e escopo de client M2M (§10.1, §18).
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

### ADR-004 — Claims vêm exclusivamente de Protocol Mappers

- **Contexto:** o token é assinado pelo Keycloak; a Gateway não pode alterá-lo sem se tornar um segundo Authorization Server.
- **Decisão:** nenhum claim é **emitido ou assinado** fora do Keycloak. Todo claim customizado (`tenant_id`, `roles`, `tier`) é produzido por Protocol Mappers configurados no Keycloak. Dados de negócio que precisam estar no token são sincronizados pela Gateway como atributos — de usuário, no caso de `tenant_id` (§12.2), ou da Organization, no caso de `tier`.
- **Escopo da regra:** a *derivação local* de claims a partir do conteúdo de um token já validado é permitida no Data Plane, e apenas para achatar formato — é o caso da transformação de contingência da §12.1, Solução B. Derivar não é emitir: nada ali cria autoridade que o token não carregasse.
- **Consequências:** nenhum mapper customizado em Java consulta a Gateway durante a emissão do token, o que criaria dependência de runtime no login e derrubaria o ADR-002 junto. Mudanças de plano refletem no token no próximo refresh.

### ADR-005 — Papéis globais no token, permissões finas na Gateway

- **Decisão:** o token carrega apenas `tenant_id` e um catálogo pequeno e fixo de papéis (`platform-admin`, `tenant-admin`, `financial-manager`, `reader`). Permissões finas, customizáveis por tenant (ex.: `invoices:approve`), vivem na Gateway como *Permission Sets* e são resolvidas pelas APIs consumidoras com cache.
- **Consequências:** token pequeno, sem risco de estourar limites de header; revogação de permissão fina tem janela de atraso igual ao TTL do cache (mitigada por evento de invalidação); o access token tem vida curta (5 minutos).

### ADR-006 — Consistência via Outbox, provisionamento idempotente e reconciliação

- **Contexto:** a Admin API do Keycloak não é transacional, e a criação de um tenant envolve o banco da Gateway e o Keycloak.
- **Decisão:** o comando grava o aggregate em estado `Pending` e o evento no **Outbox** na mesma transação. Um consumidor executa o provisionamento em passos idempotentes ("garanta que existe"). Um job periódico reconcilia divergências entre os dois lados.
- **Mecanismo:** o do CleanStart — um interceptor de `SaveChanges` coleta os domain events antes do commit e os grava na tabela de outbox, e um `OutboxWorker` publica depois, de forma assíncrona. O **transporte é RabbitMQ**. O worker é um job de fundo e portanto está sujeito ao ADR-010 (um só executor por vez).
- **Correlação idempotente:** cada Organization criada recebe o atributo `gateway_tenant_id` com o `TenantId` da Gateway, e é por ele que o "consultar antes de criar" localiza recursos preexistentes (§11.6). O `alias` **não** serve para isso: a Admin API busca Organizations por nome ou domínio, não por alias.
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

## 5. Responsabilidades: Keycloak × IdentityGateway

| Componente | Keycloak | IdentityGateway (.NET 10) |
|---|---|---|
| Credenciais | Hash (Argon2/PBKDF2), expiração, reset, MFA/TOTP | Nenhuma. Nunca vê credencial de usuário. Credenciais de client transitam na resposta de criação/rotação, sem serem persistidas (§10.3) |
| Emissão de tokens | Assinatura dos JWT (RS256/ES256), refresh token rotation | Nenhuma. Provisiona os clients que solicitam tokens |
| Claims | Protocol Mappers produzem `tenant_id`, `roles`, `tier` | Mantém sincronizados os atributos que alimentam os mappers |
| Tenants | Organization, domínios e IdPs vinculados | Aggregate `Tenant`: plano, limites, status, ciclo de vida |
| Usuários | Perfil, dados pessoais, sessões | Vínculo (`sub`), status de governança, papéis e permission sets |
| Permissões finas | Nenhuma | Fonte da verdade dos Permission Sets por tenant |
| Step-up auth | Executa o fluxo exigido via `acr_values` e emite o claim `acr` | **Define e verifica** os níveis exigidos por operação sensível (`StepUpRequirement`, §10.1) |
| Auditoria | Eventos de login e eventos administrativos | Trilha de auditoria de toda ação de governança |

---

## 6. Modelo de domínio

O bounded context é **Identity Governance**. Os dados pessoais (nome, e-mail, telefone) ficam no Keycloak. A Gateway guarda apenas o identificador do usuário no Keycloak (`sub`) e os dados de governança, o que limita o impacto de um eventual vazamento do seu banco.

### 6.1. Aggregates

**`Tenant`** (aggregate root)
- Identidade: `TenantId`, `TenantSlug` (vira o *alias* da Organization) e `ExternalOrganizationId` (id no Keycloak, preenchido após o provisionamento).
- Estado: `Plan` (value object com `Tier`, `MaxUsers` e `MaxClients`), `Status`, `OverSubscribed` (bool) e a lista de `EmailDomain`.
- Controle de vagas: um contador de vagas ocupadas, protegido por concorrência otimista (`xmin` do PostgreSQL). Duas reservas simultâneas não ultrapassam o limite do plano; o conflito de versão é reprocessado por retry explícito (§11.10).
- Invariantes:
  - o slug é único e imutável;
  - um tenant fora do status `Active` não aceita novos membros;
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

### 6.3. Domain services e regras transversais

- **`RoleAssignmentPolicy`** impede escalação de privilégio: um ator só atribui papéis do próprio tenant e nunca acima do seu papel mais alto. A hierarquia é `platform-admin` > `tenant-admin` > `financial-manager` > `reader`.
  - A policy é correta, mas **depende da qualidade da sua entrada**: `actor.HighestRole` deriva do claim `roles` do token, portanto sua corretude pressupõe o claim plano da §12.1 e a verificação de tenant da §11.7. Um teste de integração cobre a cadeia inteira, não só a policy isolada.
- **Domain events:** `TenantRegistered`, `TenantActivated`, `TenantSuspended`, `TenantReactivated`, `TenantTerminated`, `TenantOverSubscribed`, `MemberInvited`, `MemberInviteExpired`, `MemberDeactivated`, `MemberRolesChanged` e `PermissionSetChanged`. São convertidos em eventos de integração e publicados pelo Outbox.

### 6.4. Regra de acesso a sub-recursos

`Member`, `PermissionSet` e `ClientApplication` são aggregate roots próprios que **referenciam** `TenantId`. Isso cria um vetor de acesso indevido que a verificação de tenant da rota não cobre: um ator do tenant A, operando em `/tenants/A/...`, informando o id de um recurso do tenant B.

**Regra:** todo repositório de sub-recurso expõe **exclusivamente** assinaturas que exigem o tenant — `GetAsync(TenantId, MemberId)`, nunca `GetAsync(MemberId)`. A ausência da sobrecarga por id só é o que torna a regra verificável: não há como escrever o acesso inseguro por engano. Um global query filter por tenant complementa como rede.

Um id que não pertence ao tenant da rota resulta em **404**, não 403 — responder 403 confirmaria a existência do recurso em outro tenant.

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
│   │   ├── Abstractions/                     # ICommand, IQuery, handlers, IUnitOfWork
│   │   ├── Abstractions/Identity/            # IIdentityProvider (porta de saída)
│   │   ├── Tenants/Commands/ e Queries/
│   │   ├── Members/Commands/ e Queries/
│   │   ├── Permissions/
│   │   ├── Clients/
│   │   └── Discovery/                        # Resolução de tenant e IdP pelo e-mail
│   │
│   ├── IdentityGateway.Infrastructure/       # Adaptadores
│   │   ├── Persistence/                      # EF Core, configurações, migrations, repositórios
│   │   ├── Identity/Keycloak/                # KeycloakAdminClient, KeycloakIdentityProvider
│   │   ├── Messaging/                        # MassTransit, Outbox, consumidores finos
│   │   ├── Sync/                             # Leitura de eventos do Keycloak e reconciliação
│   │   └── DependencyInjection.cs
│   │
│   ├── IdentityGateway.Api/                  # Presentation: módulos Carter, policies, OpenAPI
│   │   ├── Endpoints/                        # Um arquivo por recurso, sem regra de negócio
│   │   ├── Authorization/                    # Policies, SameTenantHandler
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
│   ├── IdentityGateway.Api.IntegrationTests/ # Testcontainers: Keycloak, PostgreSQL, RabbitMQ
│   └── IdentityGateway.ArchitectureTests/
│
├── keycloak/
│   ├── bootstrap/realm-identity-gateway.json # Import inicial, apenas para ambiente local
│   └── terraform/                            # Configuração evolutiva do realm
│
├── docs/adr/
└── docker-compose.yml
```

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
| | `GET /tenants`, `GET /tenants/{tenantId}` | platform-admin (**único override, auditado** — §10.1); tenant-admin só o próprio |
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

---

## 9. Fluxos principais

### 9.1. Provisionamento de tenant

```
Cliente            Gateway API                PostgreSQL           Consumidor              Keycloak
   │ POST /tenants      │                          │                    │                      │
   │───────────────────►│ Tenant (Pending)         │                    │                      │
   │                    │ + evento no Outbox ─────►│ (mesma transação)  │                      │
   │ 202 + Location     │                          │                    │                      │
   │◄───────────────────│                          │ ── RabbitMQ ──────►│                      │
   │                    │                          │                    │ Organization existe? │
   │                    │                          │                    │─────────────────────►│
   │                    │                          │                    │ não: cria            │
   │                    │                          │                    │─────────────────────►│
   │                    │                          │◄───────────────────│ Tenant → Active      │
```

**O tenant nasce com um administrador.** `POST /tenants` exige `initialAdminEmail`, e o provisionamento executa três passos idempotentes antes de marcar `Active`:

1. garante a Organization (com o atributo `gateway_tenant_id`);
2. garante o convite do `initialAdminEmail` já com o papel `tenant-admin`;
3. marca o tenant `Active`.

Sem isso o tenant nasceria trancado: `POST /tenants` é `platform-admin`, mas convidar membros exige `tenant-admin` **daquele tenant** — que ainda não existiria — e o platform-admin não satisfaz a verificação de tenant da §11.7. A alternativa seria abrir uma exceção de platform-admin no mecanismo de isolamento; criar o admin junto do tenant **elimina** uma exceção em vez de acrescentar outra.

Se os retries se esgotarem, o tenant vai para `ProvisioningFailed` e o evento fica disponível para retry manual.

**Reconciliação.** O job compara periodicamente **todos os tenants em estado não terminal** (`Pending`, `ProvisioningFailed` e `Active`) com as Organizations existentes, e também o sentido inverso: Organizations com `gateway_tenant_id` que não correspondem a nenhum tenant conhecido. Varrer apenas `Active` cobriria só o caso em que nada deu errado — o órfão típico nasce antes da ativação, com a Organization criada e a gravação do id falhando.

### 9.2. Login interativo com discovery

1. A aplicação cliente coleta o e-mail e chama `POST /auth/discovery`.
2. A Gateway resolve o tenant pelo domínio e devolve os parâmetros do fluxo: `issuer`, `authorization_endpoint` e, quando o tenant tem IdP federado, o `kc_idp_hint`.
3. A aplicação gera `code_verifier` e `code_challenge` (PKCE) e redireciona o usuário **diretamente ao Keycloak**.
4. O Keycloak autentica o usuário, localmente ou via IdP federado, e devolve o `code` para a aplicação, que o troca pelos tokens no próprio Keycloak.

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

1. **Offline tokens.** `POST .../users/{id}/logout` remove sessões online, mas **não** sessões offline. Como `offline_access` integra o `default-roles` de todo usuário do realm por padrão, qualquer usuário que tenha obtido um offline token continuaria renovando acesso depois da "revogação". Por isso o realm de bootstrap **remove `offline_access` do `default-roles`** (§15): o projeto não usa offline tokens, e mantê-los ligados anularia a desativação.
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

**Convite.** `POST /tenants/{tenantId}/members` reserva a vaga, cria o usuário no Keycloak desabilitado com as *required actions* (`UPDATE_PASSWORD`, `VERIFY_EMAIL`), dispara o e-mail e grava `InvitedAt`. O membro nasce `Invited`.

**Aceite.** O usuário define a senha no Keycloak e o evento chega pela sincronização do ADR-007. O membro passa a `Active` e a vaga, já reservada, apenas deixa de ser provisória — **não há nova reserva no aceite**, e é justamente por isso que o aceite não pode falhar por limite de plano (§6.1).

**Expiração.** Um job periódico da Gateway varre os membros `Invited` cujo `InvitedAt` excedeu o prazo, transiciona para `Expired`, libera a vaga e publica `MemberInviteExpired`. O prazo é **configurável por tenant, com padrão de 7 dias**, e deve ser alinhado ao prazo do link de ações do Keycloak — um link ainda válido para um membro já `Expired` produziria um aceite sem vaga reservada.

> **Por que o job vive na Gateway, e não no Keycloak.** Quem libera a vaga tem de ser quem controla o contador. Derivar a expiração de um evento do Keycloak amarraria uma regra de plano à configuração de realm e dependeria do polling do ADR-007 — que a decisão 2 do brainstorm já considerou tarde demais para o aceite, pelo mesmo motivo.

**Reenvio.** `POST .../members/{memberId}/resend-invite` redispara o e-mail e **reinicia `InvitedAt`**, prorrogando a vaga já ocupada. Só é aceito em `Invited`. É o ticket de suporte mais comum de qualquer plataforma multi-tenant (N7), e existe por necessidade do modelo de vagas, não como conveniência.

**Cancelamento.** `DELETE .../members/{memberId}/invite` leva ao estado **`Revoked`**, libera a vaga e desabilita o usuário no Keycloak. Só é aceito em `Invited`. **Não há sessões a revogar** — diferente da desativação de membro (§9.5), o usuário nasce desabilitado e nunca chegou a autenticar-se, então não existe sessão nem refresh token em circulação. O link de ações ainda válido deixa de servir porque o usuário está desabilitado.

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

- Aceita apenas tokens do realm `identity-gateway` com audiência `identity-gateway-api`, garantida por um Audience Mapper.
- **Quatro** famílias de policies: `PlatformAdmin`; `TenantAdmin`, sempre combinada com `SameTenantRequirement`; `StepUp`, para operações destrutivas; e escopos de client, tratados abaixo.
- **`SameTenantRequirement`** compara o `tenantId` da rota com o claim `tenant_id` do token, e **nega explicitamente** (`context.Fail()`) em todo caminho de rejeição — inclusive quando o claim está ausente ou o recurso não é `HttpContext`. Um requirement que apenas deixa de dar `Succeed` não é fail-closed: qualquer outro handler registrado para o mesmo requirement ainda pode satisfazê-lo, e só `Fail()` sobrevive a um `Succeed` alheio.
- **Pertencimento de sub-recurso** (§6.4) é a segunda regra de isolamento, independente da primeira: verificar o tenant da rota não impede que um ator do tenant A informe o id de um recurso do tenant B.
- **`RoleAssignmentPolicy`** impede que um tenant-admin conceda papéis acima do próprio.
- **Override do `platform-admin`: só leitura de tenant, e auditado** (I-3). O `PlatformAdminOverrideHandler` satisfaz o `SameTenantRequirement` **exclusivamente** em `GET /tenants` e `GET /tenants/{tenantId}`, e em nenhuma rota interna do tenant — membros, clients, permission sets, domínios e IdPs permanecem inacessíveis a ele. Cada uso do override gera entrada de auditoria, como no client de plataforma.

  > O limite é o que sustenta a decisão 1 do brainstorm. Se o platform-admin pudesse operar dentro do tenant, `initialAdminEmail` perderia a razão de existir — o argumento de C9 é precisamente que ele **não** satisfaz a verificação de tenant nas rotas de membros, e por isso o tenant precisa nascer com um admin próprio. Um override amplo reintroduziria a exceção de isolamento que a decisão 1 eliminou.

#### Step-up em operações destrutivas

Exclusão definitiva de membro, rotação de credenciais e encerramento de tenant exigem `acr` de nível elevado. O `StepUpRequirement` compara o claim `acr` com o nível mínimo declarado para a operação.

**Quando o nível falta, a resposta não é 403.** A API devolve `401` com o header `WWW-Authenticate` contendo `error="insufficient_user_authentication"` e o `acr_values` exigido, instruindo o cliente a refazer a autenticação com o nível necessário. Responder 403 deixaria o usuário sem caminho de recuperação: ele *pode* executar a operação, bastando reautenticar-se mais forte.

O nível é derivado do `auth_time` **da sessão**, não da operação — um step-up recente vale para as operações seguintes dentro da janela configurada no realm.

#### Clients de plataforma × clients de tenant

Os escopos de client precisam de um modelo de confiança próprio, porque **um token de Client Credentials não carrega `tenant_id`** — e, portanto, o `SameTenantRequirement` não tem o que comparar.

| Tipo | Emissão | Escopos | Isolamento |
|---|---|---|---|
| **Client de tenant** | `POST /tenants/{tenantId}/clients`, pelo tenant-admin | `gateway.members.write` | Recebe o atributo `tenant_id` do tenant que o criou, emitido como claim plano. Sujeito ao `SameTenantRequirement` como qualquer ator |
| **Client de plataforma** | Provisionado fora da API, no bootstrap | `gateway.permissions.read` | **Acesso irrestrito por desenho** — é a credencial que as Resource APIs usam para resolver permissões de qualquer tenant (§9.6). Tratado como credencial de infraestrutura: `private_key_jwt` obrigatório, rotação documentada e **auditoria por chamada** |

Essa distinção é necessária porque a Resource API serve todos os tenants: exigir dela um token com `tenant_id` quebraria o fluxo 9.6, e não exigir nada de um client de tenant permitiria que ele lesse a governança de qualquer outro. O anti-pattern 7 (§17) vale para atores de tenant; o client de plataforma é a exceção nomeada, e é auditada justamente por sê-lo.

### 10.2. Credenciais da Gateway no Keycloak

- A Gateway usa um client confidencial próprio, autenticado por `private_key_jwt`, com chave em cofre de segredos.

**Onde os segredos vivem.** `User Secrets` no ambiente de desenvolvimento; **variáveis de ambiente injetadas pelo orquestrador** em qualquer outro ambiente. A leitura fica atrás de uma abstração própria, para que trocar por Key Vault, Secrets Manager ou similar não toque em código de aplicação — um projeto de portfólio não deve se amarrar a um provedor de nuvem específico. Nenhum segredo entra em arquivo versionado: um teste de CI falha se o JSON de bootstrap contiver credencial literal (§15).

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
| `aud` | o **token endpoint** do realm — não o issuer |
| `jti` | GUID novo a cada assertion, para impedir replay |
| `exp`, `nbf`, `iat` | preenchidos pelo `JsonWebTokenHandler`; vida curta (≤ 60s) |

```csharp
var descriptor = new SecurityTokenDescriptor
{
    Claims = new Dictionary<string, object>
    {
        ["iss"] = clientId,
        ["sub"] = clientId,
        ["aud"] = tokenEndpoint,
        ["jti"] = Guid.NewGuid().ToString()
    },
    SigningCredentials = signingCredentials // RSA/EC privada, do cofre
};

var assertion = new JsonWebTokenHandler().CreateToken(descriptor);
```

Do lado do Keycloak, o client é configurado com **Signed JWT** como método de autenticação, e a chave pública chega por JWKS publicado pela Gateway ou por certificado registrado no client. A rotação troca a chave sem downtime quando o JWKS expõe as duas chaves durante a janela de transição.

**`aud` é o erro mais comum:** apontar para o issuer do realm em vez do token endpoint produz `invalid_client` sem explicação útil. Um teste de integração contra o Keycloak real cobre a obtenção de token por este caminho (§13).
- O service account recebe **somente** os papéis de `realm-management` necessários para as operações implementadas. O conjunto mínimo é validado pelos testes de integração: um papel sem uso é removido.
- A Gateway nunca usa o realm `master`.

### 10.3. Proteções gerais

- **Rate limiting** nativo do ASP.NET Core, particionado por tenant, com limite mais restrito no discovery.
- **Idempotência:** POSTs com `Idempotency-Key` guardam, por 24 horas, **apenas** `(chave, status, Location/identificador do recurso)` — nunca o corpo da resposta. O store é compartilhado (tabela no PostgreSQL, índice em `(key, expires_at)`, job de limpeza), porque um store em memória não protegeria nada num deploy multi-réplica: a chave gravada numa instância não alcançaria as demais.
  - **Nenhum valor de credencial entra no store de idempotência, na auditoria ou no log.** A criação e a rotação de client devolvem o `client_secret` no corpo; guardá-lo faria a Gateway persistir por 24h exatamente o segredo que o anti-pattern 3 (§17) diz não ser guardado, e tornaria falso o "devolvido uma única vez" — a `Idempotency-Key` é escolhida pelo cliente e circula em logs de proxy e coleções de API. No replay dessas rotas, a resposta é `200` sem o campo sensível, indicando que o recurso já existe.
- **Auditoria:** tabela *append-only* com ator (`sub`), tenant, ação, alvo, resultado e `correlationId`. Os eventos administrativos do Keycloak são guardados com retenção configurada.
- **Validação de entrada** com Notification Pattern, devolvendo todos os erros de uma vez em Problem Details.
- **Transporte:** HTTPS obrigatório, HSTS e CORS apenas para uma lista explícita de origens.
- **Tokens:** access token de 5 minutos, refresh token com rotação e *backchannel logout* habilitado.
- **LGPD:** dados pessoais somente no Keycloak; a Gateway guarda vínculos e governança.

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

    private Tenant() { } // exigido pelo EF Core

    public static Tenant Register(string name, TenantSlug slug, Plan plan)
    {
        var tenant = new Tenant
        {
            Id = TenantId.New(),
            Name = name,
            Slug = slug,
            Plan = plan,
            Status = TenantStatus.Pending
        };

        // O evento vira mensagem no Outbox; é ele que dispara o provisionamento.
        tenant.Raise(new TenantRegistered(tenant.Id, slug));
        return tenant;
    }

    public void MarkProvisioned(string externalOrganizationId)
    {
        // Idempotente: a mesma mensagem pode ser entregue mais de uma vez.
        if (Status == TenantStatus.Active && ExternalOrganizationId == externalOrganizationId)
            return;

        // Transição inválida é erro de programação, não de negócio: lança exceção.
        EnsureStatusIn(TenantStatus.Pending, TenantStatus.ProvisioningFailed);

        ExternalOrganizationId = externalOrganizationId;
        Status = TenantStatus.Active;
        Raise(new TenantActivated(Id));
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
namespace IdentityGateway.Application.Abstractions.Identity;

/// <summary>
/// Única forma de a Application falar com o provedor de identidade.
/// Nenhum tipo do Keycloak atravessa esta interface, e todas as operações
/// são idempotentes ("garanta que", não "crie").
/// </summary>
public interface IIdentityProvider
{
    Task<string> EnsureOrganizationAsync(TenantSlug slug, string name, CancellationToken ct);
    Task<ExternalUserId> EnsureInvitedUserAsync(string organizationId, InviteData data, CancellationToken ct);
    Task SetUserEnabledAsync(ExternalUserId userId, bool enabled, CancellationToken ct);
    Task RevokeSessionsAsync(ExternalUserId userId, CancellationToken ct);
    Task ReplaceRolesAsync(ExternalUserId userId, IReadOnlySet<RoleName> roles, CancellationToken ct);
    Task EraseUserAsync(ExternalUserId userId, CancellationToken ct);
}
```

### 11.4. Application: command de registro de tenant

```csharp
namespace IdentityGateway.Application.Tenants.Commands;

public sealed record RegisterTenantCommand(string Name, string Slug, string PlanCode) : ICommand<TenantId>;

internal sealed class RegisterTenantHandler(
    ITenantRepository tenants,
    IPlanCatalog plans,
    IUnitOfWork unitOfWork) : ICommandHandler<RegisterTenantCommand, TenantId>
{
    public async Task<Result<TenantId>> Handle(RegisterTenantCommand command, CancellationToken ct)
    {
        var slug = TenantSlug.Create(command.Slug);
        if (slug.IsFailure)
            return slug.Error;

        if (await tenants.SlugExistsAsync(slug.Value, ct))
            return TenantErrors.SlugInUse(slug.Value);

        var plan = plans.Find(command.PlanCode);
        if (plan is null)
            return TenantErrors.UnknownPlan(command.PlanCode);

        var tenant = Tenant.Register(command.Name, slug.Value, plan);
        tenants.Add(tenant);

        // Nenhuma chamada ao Keycloak aqui. O INSERT e o evento no Outbox são
        // gravados na mesma transação: se o Keycloak estiver fora do ar,
        // nada fica órfão; o provisionamento apenas acontece mais tarde.
        await unitOfWork.SaveChangesAsync(ct);

        return tenant.Id;
    }
}
```

### 11.5. Application e Infrastructure: provisionamento idempotente

```csharp
// Application: a regra do provisionamento.
internal sealed class ProvisionTenantHandler(
    ITenantRepository tenants,
    IIdentityProvider identity,
    IUnitOfWork unitOfWork) : ICommandHandler<ProvisionTenantCommand>
{
    public async Task<Result> Handle(ProvisionTenantCommand command, CancellationToken ct)
    {
        var tenant = await tenants.GetAsync(command.TenantId, ct);

        // Mensagem repetida ou tenant removido: nada a fazer.
        if (tenant is null || tenant.Status == TenantStatus.Active)
            return Result.Success();

        var organizationId = await identity.EnsureOrganizationAsync(tenant.Slug, tenant.Name, ct);

        tenant.MarkProvisioned(organizationId);
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}

// Infrastructure: consumidor fino, só traduz a mensagem em comando.
// Retry e redelivery são configurados no MassTransit; esgotadas as tentativas,
// um consumidor de Fault marca o tenant como ProvisioningFailed.
internal sealed class TenantRegisteredConsumer(ICommandDispatcher dispatcher)
    : IConsumer<TenantRegisteredIntegrationEvent>
{
    public Task Consume(ConsumeContext<TenantRegisteredIntegrationEvent> context) =>
        dispatcher.Send(new ProvisionTenantCommand(context.Message.TenantId), context.CancellationToken);
}
```

### 11.6. Infrastructure: adaptador do Keycloak

```csharp
namespace IdentityGateway.Infrastructure.Identity.Keycloak;

internal sealed class KeycloakIdentityProvider(KeycloakAdminClient admin) : IIdentityProvider
{
    // Atributo que correlaciona a Organization ao tenant da Gateway. É a chave da
    // idempotência: estável, escolhida por nós e imune a rename de nome ou alias.
    private const string TenantIdAttribute = "gateway_tenant_id";

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
                    Name: name,
                    Alias: slug.Value,
                    Enabled: true,
                    Attributes: new() { [TenantIdAttribute] = [tenantId.Value.ToString()] }),
                ct);
        }
        catch (KeycloakConflictException)
        {
            // Duas entregas da mesma mensagem concorreram e a outra venceu.
            var created = await admin.FindOrganizationByAttributeAsync(TenantIdAttribute, tenantId.Value, ct);
            return created?.Id
                ?? throw new IdentityProviderException($"Organization do tenant {tenantId} em estado inconsistente.");
        }
    }

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
    /// Busca Organization por atributo, via searchQuery ('chave:valor').
    /// </summary>
    /// <remarks>
    /// Não existe busca por alias na Admin API: GET /organizations aceita search +
    /// exact, mas o match exato é sobre o NOME da organização ou um de seus DOMÍNIOS.
    /// Buscar por nome também não serve — Name é texto livre e editável, enquanto
    /// Slug é normalizado, então os dois divergem. Daí o atributo próprio.
    /// </remarks>
    public async Task<OrganizationRepresentation?> FindOrganizationByAttributeAsync(
        string key, Guid value, CancellationToken ct)
    {
        var query = Uri.EscapeDataString($"{key}:{value}");
        var found = await http.GetFromJsonAsync<List<OrganizationRepresentation>>(
            $"admin/realms/{_realm}/organizations?searchQuery={query}&exact=true&max=2", ct);

        // Mais de um resultado significa corrupção da correlação: falhar alto em vez
        // de escolher um arbitrariamente e provisionar sobre a Organization errada.
        if (found is { Count: > 1 })
            throw new IdentityProviderException($"Mais de uma Organization com {key}={value}.");

        return found?.SingleOrDefault();
    }

    // InviteUserAsync, SetUserEnabledAsync, RevokeSessionsAsync, etc.
}
```

> **A confirmar na implementação:** que `searchQuery` filtre atributos de *Organization* (e não apenas de usuário) na versão do Keycloak fixada. Caso não filtre, a alternativa é manter o mapeamento `TenantId → ExternalOrganizationId` exclusivamente no banco da Gateway e tratar o 409 de criação como sinal de reconciliação pendente, em vez de tentar recuperar o id pela busca. **Um teste de integração contra o Keycloak real decide isso no M1**, antes de o ADR-006 depender da resposta.

### 11.7. Presentation: endpoint e isolamento entre tenants

> **Convenção de endpoints: Carter.** Os endpoints são organizados em módulos Carter (`ICarterModule`), como no CleanStart — um módulo por área de recurso, sem controllers. O exemplo abaixo mostra a **lógica de isolamento**, que é o ponto da seção; ela é idêntica em qualquer estilo de roteamento, e o que muda é apenas onde a rota é declarada.

```csharp
// Endpoints/TenantEndpoints.cs: sem regra de negócio, apenas tradução HTTP ↔ comando.
group.MapPost("/", async (RegisterTenantRequest body, ICommandDispatcher dispatcher, CancellationToken ct) =>
    {
        var result = await dispatcher.Send(
            new RegisterTenantCommand(body.Name, body.Slug, body.PlanCode), ct);

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

```csharp
// Authorization/SameTenantHandler.cs
public sealed class SameTenantRequirement : IAuthorizationRequirement;

/// <summary>
/// Garante que o tenant do token é o mesmo tenant da rota.
/// Sem esta verificação, qualquer tenant-admin operaria sobre qualquer tenant (BOLA/IDOR).
/// </summary>
/// <remarks>
/// Todo caminho de rejeição chama Fail() explicitamente. Na v2.0 os caminhos apenas
/// retornavam sem decidir, o que NÃO é negar: em ASP.NET Core, um requirement sem
/// Succeed e sem Fail está só "ainda não satisfeito", e qualquer outro handler
/// registrado para o mesmo requirement pode satisfazê-lo. Só Fail() sobrevive a um
/// Succeed alheio — e este projeto de fato registra um segundo handler, para
/// platform-admin (ver PlatformAdminOverrideHandler, seção 8).
/// </remarks>
public sealed class SameTenantHandler : AuthorizationHandler<SameTenantRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, SameTenantRequirement requirement)
    {
        // Com endpoint routing, o Resource é o próprio HttpContext.
        if (context.Resource is not HttpContext http)
        {
            context.Fail(new AuthorizationFailureReason(this, "Contexto da requisição indisponível."));
            return Task.CompletedTask;
        }

        var routeTenant = http.GetRouteValue("tenantId")?.ToString();
        var tokenTenant = context.User.FindFirst("tenant_id")?.Value;

        if (string.IsNullOrEmpty(routeTenant))
        {
            // Endpoint com policy de tenant e sem {tenantId} no template: erro de
            // configuração. O teste de startup da seção 13 impede que chegue aqui.
            context.Fail(new AuthorizationFailureReason(this, "Rota sem tenantId."));
            return Task.CompletedTask;
        }

        if (string.IsNullOrEmpty(tokenTenant))
        {
            // Claim ausente. Se acontecer em produção, o mapper de tenant_id
            // (seção 12.2) não está configurado — e negar é o comportamento certo.
            context.Fail(new AuthorizationFailureReason(this, "Token sem tenant_id."));
            return Task.CompletedTask;
        }

        if (!string.Equals(routeTenant, tokenTenant, StringComparison.Ordinal))
        {
            context.Fail(new AuthorizationFailureReason(this, "Tenant da rota diverge do token."));
            return Task.CompletedTask;
        }

        context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
```

### 11.9. Repositório de sub-recurso: a assinatura que impede o erro

```csharp
/// <summary>
/// Não existe GetAsync(MemberId). A ausência da sobrecarga é o que torna a regra
/// da seção 6.4 verificável: não há como escrever o acesso inseguro por engano.
/// </summary>
public interface IMemberRepository
{
    Task<Member?> GetAsync(TenantId tenantId, MemberId memberId, CancellationToken ct);
    Task<IReadOnlyList<Member>> ListAsync(TenantId tenantId, CancellationToken ct);
}
```

Verificar o tenant da rota (§11.7) não cobre este vetor: o ator do tenant A opera em `/tenants/A/...`, o requirement aprova, e o id informado é de um recurso do tenant B. Um id fora do tenant resulta em **404**, nunca 403 — responder 403 confirmaria que o recurso existe em outro tenant.

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

    // Obtém e mantém em cache o token do service account (client credentials).
    // Usa um HttpClient próprio para não entrar em recursão com o client abaixo.
    services.AddTransient<ServiceAccountTokenHandler>();

    services.AddHttpClient<KeycloakAdminClient>((sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<KeycloakAdminOptions>>().Value;
            http.BaseAddress = new Uri(options.BaseUrl);
        })
        .AddHttpMessageHandler<ServiceAccountTokenHandler>()
        .AddStandardResilienceHandler(resilience =>
        {
            // POST não é idempotente: retry automático só em métodos seguros.
            // A idempotência das escritas é garantida pelo padrão "consultar antes de criar".
            resilience.Retry.DisableForUnsafeHttpMethods();
        });

    services.AddScoped<IIdentityProvider, KeycloakIdentityProvider>();
    return services;
}
```

Critérios de tempo de vida: handlers, repositórios e o adaptador do Keycloak são `Scoped`, porque acompanham o `DbContext`; clientes HTTP tipados são gerenciados pelo `IHttpClientFactory`; caches e catálogos imutáveis (como o de planos) são `Singleton`. Não há *service locator* fora do composition root.

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
  "preferred_username": "ana@empresa-a.com",
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

Os papéis de realm estão dentro do objeto `realm_access`, e os papéis de cada client, dentro de `resource_access.{clientId}`. Não existe um claim chamado `roles` no primeiro nível.

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

No console do Keycloak: *Client scopes* → *Create client scope* (`gateway-roles`, tipo Default) → aba *Mappers* → *Add mapper* → *By configuration* → *User Realm Role*.

O mesmo, no arquivo de bootstrap do realm:

```json
{
  "clientScopes": [
    {
      "name": "gateway-roles",
      "protocol": "openid-connect",
      "attributes": { "include.in.token.scope": "false" },
      "protocolMappers": [
        {
          "name": "realm-roles-flat",
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
  "defaultDefaultClientScopes": ["basic", "profile", "email", "roles", "web-origins", "acr", "gateway-roles"]
}
```

O `defaultDefaultClientScopes` é importante para este projeto: faz com que **todo client criado depois**, incluindo os clients M2M provisionados pela Gateway, receba o scope automaticamente. Sem isso, cada client novo voltaria a emitir apenas o formato aninhado. A lista precisa incluir também os scopes padrão que devem continuar existindo (`basic`, `profile`, `email`, `roles`, `web-origins`, `acr`), porque ela substitui o conjunto padrão do realm em vez de somar a ele.

Com o mapper ativo, o token passa a ter:

```json
{
  "roles": ["default-roles-identity-gateway", "offline_access", "uma_authorization", "tenant-admin"],
  "realm_access": { "roles": ["..."] }
}
```

O `realm_access` continua existindo porque o scope padrão `roles` segue associado. Mantê-lo é a escolha segura: esse scope também contém o mapper *audience resolve*, e removê-lo altera o `aud` do token. Papéis compostos são expandidos pelo mapper, então um papel que inclui outros aparece junto com os que ele contém.

Os papéis `default-roles-identity-gateway`, `offline_access` e `uma_authorization` aparecem em todos os usuários. Eles não interferem nas policies deste projeto, que exigem papéis específicos do catálogo (seção 6.3).

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

3. **No CI, com teste de integração contra o Keycloak real (Testcontainers):**

```csharp
[Fact]
public async Task Access_token_traz_roles_como_claims_planos()
{
    // Client de teste cujo service account tem o papel tenant-admin.
    // Os testes usam Client Credentials: o ROPC continua desabilitado
    // também no realm de teste, para o bootstrap não carregar configuração insegura.
    var token = await _keycloak.GetClientCredentialsTokenAsync("test-tenant-a-admin");

    var jwt = new JsonWebToken(token);
    var roles = jwt.Claims.Where(c => c.Type == "roles").Select(c => c.Value);

    Assert.Contains("tenant-admin", roles);
}

[Fact]
public async Task Endpoint_protegido_por_papel_aceita_token_do_keycloak()
{
    var token = await _keycloak.GetClientCredentialsTokenAsync("test-tenant-a-admin");
    _client.DefaultRequestHeaders.Authorization = new("Bearer", token);

    var response = await _client.GetAsync("/api/v1/tenants/tenant-a/members");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
}
```

O primeiro teste protege a configuração do Keycloak; o segundo protege a configuração do .NET. Se alguém remover o mapper ou voltar `MapInboundClaims` para o padrão, um dos dois quebra.

#### Diagnóstico rápido

| Sintoma | Causa provável | Correção |
|---|---|---|
| 403 com token válido; o papel aparece dentro de `realm_access` | Mapper plano ausente ou scope não associado ao client | Associar `gateway-roles` ao client; conferir `defaultDefaultClientScopes` |
| O mapper existe, mas o token continua só com `realm_access` | Scope criado como *Optional* ou associado a outro client | Associar como *Default* ao client que emite o token |
| `RequireRole` passa, `RequireClaim("roles", ...)` falha | `MapInboundClaims` no padrão (`true`) | `MapInboundClaims = false` e `RoleClaimType = "roles"` |
| `User.IsInRole(...)` sempre `false` com claim `roles` presente | `RoleClaimType` diferente de `"roles"` | Ajustar `RoleClaimType` |
| Apenas um papel no token, como texto e não array | Multivalued desligado | Multivalued ON |
| Claim aparece como `realm_access` mesmo após criar o mapper | Nome do claim com ponto (ex.: `realm_access.roles`) | Usar nome sem ponto: `roles` |
| Papel aparece no ID token, mas não no access token | *Add to access token* desligado | Ligar *Add to access token* |
| Papel recém-atribuído não funciona | Token emitido antes da atribuição | Aguardar o refresh ou fazer novo login (access token de 5 min) |

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
| User Attribute | `tenant_id` | Gravado pela Gateway no provisionamento |
| Token Claim Name | `tenant_id` | Sem ponto, portanto sem aninhamento |
| Claim JSON Type | String | Escalar: `ValueType` será `String`, não `JSON` |
| Multivalued | OFF | Um usuário pertence a um único tenant (ADR-009) |
| Add to access token | ON | É o token que as APIs recebem |

O mapper vai no client scope `gateway-tenant`, incluído em `defaultDefaultClientScopes` ao lado de `gateway-roles`, para que **todo client criado depois** — inclusive os clients de tenant provisionados pela Gateway — o receba automaticamente.

**Onde a Gateway grava o atributo.** Em dois pontos, e ambos precisam existir:

1. no provisionamento do membro (§9.1 e convite), junto da criação do usuário;
2. na absorção de usuário criado fora da Gateway (§9.4) — aqui há uma **janela real**: entre o primeiro login federado e o processamento do evento pelo poller, o usuário tem token sem `tenant_id` e recebe 403 nas rotas de tenant. A janela é igual ao intervalo de polling, e está declarada na §19.

O claim `organization` continua sendo emitido e é útil em auditoria, mas **não é fonte de autorização**.

#### Teste que protege esta decisão

Espelha o de `roles` da §12.1, e verifica o que de fato importa — que o claim é escalar:

```csharp
[Fact]
public async Task Access_token_traz_tenant_id_como_claim_plano()
{
    var token = await _keycloak.GetTokenForUserAsync("ana@empresa-a.com");
    var jwt = new JsonWebToken(token);

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
| Não duplicação no Keycloak | `DelegatingHandler` que **conta** chamadas à Admin API: um POST de criação sob falha transiente não pode virar duas Organizations. Protege o anti-pattern 8 contra a possibilidade de o `DisableForUnsafeHttpMethods()` não surtir efeito | Testcontainers, `DelegatingHandler` contador |
| Autorização negativa | As **três** regras de isolamento (§3, princípio 5), cada uma parametrizada a partir da tabela de rotas: (a) tenant da rota × token — acesso ao tenant B com token do tenant A; (b) **pertencimento de sub-recurso** — token de A, rota de A, id de membro/client/permission-set de B, esperando 404; (c) **escopo de client** — client de tenant tentando ler governança de outro tenant. Mais escalação de papel e token sem audiência correta | Teste parametrizado gerado a partir da tabela de rotas |
| Configuração de endpoint | Teste de startup que varre o `EndpointDataSource` e falha se um endpoint com policy de tenant **não** tiver `{tenantId}` no template — o inverso do teste acima | `EndpointDataSource`, sem servidor |
| Idempotência e consistência | Mensagem entregue duas vezes; falha do Keycloak no meio do provisionamento; reconciliação detectando divergência | Testcontainers, com a falha simulada por um `DelegatingHandler` |
| Arquitetura | Regras de dependência da seção 7; nenhum tipo do Keycloak fora de `Infrastructure/Identity` | NetArchTest ou ArchUnitNET |
| Contrato | Documento OpenAPI gerado comparado com a versão aprovada, o que evita *breaking changes* acidentais | Snapshot do documento |

O teste negativo de autorização é o mais importante do projeto: ele percorre as rotas registradas e falha se aparecer um endpoint sem cobertura para **cada uma das três regras** que a rota toca. Assim, um endpoint novo não entra desprotegido por esquecimento.

**Por que as três, e não só a primeira.** A suíte da v2.0 cobria apenas "token do tenant A → rota do tenant B" — o vetor que o `SameTenantRequirement` já bloqueia. Ela ficaria verde enquanto três vetores reais passavam: id de sub-recurso de outro tenant (§6.4), rota sem `{tenantId}` que opera sobre dados de tenant, e escopo de client sem vínculo de tenant (§10.1). Um gate mais estreito que o princípio que ele deveria provar é pior que nenhum gate, porque produz confiança.

---

## 14. Observabilidade

- **Logs estruturados** com Serilog, sempre com `tenantId`, `correlationId` e `sub` do ator. Nunca com tokens ou dados pessoais.
- **Traces** com OpenTelemetry cobrindo a requisição HTTP, o Outbox, o consumidor e as chamadas à Admin API.
- **Métricas:** duração e falhas de provisionamento, latência das chamadas ao Keycloak, divergências encontradas pela reconciliação, atraso da sincronização de eventos e rejeições por rate limit.
- **Health checks:** `live` verifica apenas o processo; `ready` verifica PostgreSQL, RabbitMQ e a metadata OIDC do Keycloak.

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

---

## 15. Ambiente local e configuração como código

O `docker-compose.yml` sobe:

| Serviço | Papel |
|---|---|
| `keycloak` | Versão 26.x fixada, **com `KC_FEATURES=organization`**; importa `realm-identity-gateway.json` na primeira subida |
| `postgres` | Um servidor com dois bancos: `keycloak` e `identity_gateway` |
| `rabbitmq` | Mensageria, com a interface de gerenciamento habilitada |
| `mailpit` | Captura os e-mails de convite e de ações obrigatórias do Keycloak |
| `redis` | L2 do `HybridCache` — permissões efetivas do §9.6 |
| `seq` | Logs estruturados do Serilog, com interface de consulta |
| `jaeger` | Traces do OpenTelemetry |
| `identity-gateway-api` | A API |
| `sample-resource-api` | A API de exemplo do Data Plane |

Os três últimos serviços de infraestrutura (`redis`, `seq`, `jaeger`) vêm do CleanStart e não são acréscimo gratuito: o `redis` sustenta o cache de permissões do §9.6, e `seq`/`jaeger` são o que torna verificável a exigência do M0 de **auditoria e observabilidade desde já** (§16).

O arquivo de bootstrap contém o mínimo para o ambiente local: realm com Organizations habilitado, client e service account da Gateway, client scopes `gateway-roles` (§12.1) e `gateway-tenant` (§12.2) em `defaultDefaultClientScopes`, Audience Mapper, catálogo de papéis e armazenamento de eventos ativado. A evolução da configuração do realm é feita com o provider Terraform do Keycloak, porque arquivos de export não produzem diffs revisáveis nem aplicam mudanças incrementais.

**`organization` é feature flag de build, não apenas toggle de realm.** Sem `KC_FEATURES=organization` no container, o Keycloak sobe normalmente, o import do realm passa sem erro e `POST /admin/realms/{realm}/organizations` responde **404** — falha silenciosa que só aparece no primeiro provisionamento. O M0 inclui um *smoke test* que chama esse endpoint e falha explicitamente em 404, para que o erro apareça na subida e não na primeira demonstração.

**`offline_access` é removido do `default-roles` do realm.** O papel vem ligado por padrão para todo usuário, e sessões offline **não** são encerradas por `POST .../users/{id}/logout` — o que permitiria a um usuário desativado continuar renovando acesso depois da revogação (§9.5). O projeto não usa offline tokens; removê-lo é hardening de uma linha, e um teste de integração verifica que ele não voltou.

**Bootstrap do primeiro `platform-admin`.** O papel não é atribuível pela API (§11.2) e é exigido por `POST /tenants`, então precisa nascer no bootstrap — mas o `realm-identity-gateway.json` é versionado num repositório público. A senha **não** é literal no arquivo: o `docker compose up` gera uma senha aleatória, exibe-a uma única vez no log e cria o usuário com a required action `UPDATE_PASSWORD`. Um teste de CI falha se o JSON de bootstrap contiver qualquer credencial literal.

A documentação interativa usa o suporte nativo a OpenAPI 3.1 do .NET 10, com esquema de segurança OAuth2 (Authorization Code com PKCE e Client Credentials) e interface Scalar.

---

## 16. Roadmap

Cada marco termina com algo demonstrável e testado.

| Marco | Entrega | Resultado demonstrável |
|---|---|---|
| **M0 · Fundação** | Clone do CleanStart renomeado (feature `Orders` removida, `Domain/Common`, `Application/Common`, interceptors e `ArchitectureTests` preservados), Docker Compose unificado (com `KC_FEATURES=organization`), bootstrap do realm com os dois client scopes e sem `offline_access`, platform-admin com senha gerada, health checks, CI com testes de arquitetura, **auditoria e observabilidade desde já** | `git clone` + `docker compose up` + primeiro `curl` funcionam na primeira tentativa |
| **M1 · Tenants** | Registro com `initialAdminEmail`, provisionamento via Outbox com correlação por atributo, status, suspensão **com efeito no Keycloak** (membros e clients, idempotente e retomável), encerramento, reconciliação bidirecional, **downgrade de plano rejeitado** (N8) | Tenant criado com o Keycloak fora do ar é provisionado quando ele volta — demonstrável por `curl` |
| **M2 · Membros e papéis** | Convite com ciclo de vida completo (§9.9: expira por job, reenvia, cancela → `Revoked`), desativação com revogação de sessões, exclusão LGPD, papéis com `RoleAssignmentPolicy`, vagas com regra explícita | Suíte de autorização negativa verde **nas três regras de isolamento** |
| **M3 · Data Plane** | `IdentityGateway.Client.AspNetCore` e `SampleResourceApi` | Requisição de negócio autorizada sem nenhuma chamada à Gateway |
| **M4 · Federação e discovery** | Domínios, IdP externo por tenant, endpoint de discovery, sincronização de eventos do Keycloak | Login federado cria o membro e respeita o limite do plano |
| **M5 · Permissões finas** | Permission Sets, permissões efetivas, cache com invalidação por evento | Permissão revogada deixa de valer sem novo login |
| **M6 · M2M** | Clients com `private_key_jwt` (§10.2), rotação de credenciais, **limite `MaxClients`**, clients desabilitados na suspensão do tenant | Serviço parceiro chamando a Sample API com Client Credentials; tenant suspenso derruba também o M2M |
| **M7 · Hardening** | Step-up com `StepUpRequirement` e resposta `insufficient_user_authentication`, rate limiting, README detalhado com `curl` reproduzível | Repositório pronto para apresentação |

**Ponto de "apresentável" declarado:** ao fim do **M3 + M7'** (step-up, rate limiting e README), o repositório está completo e defensável — ADRs, testes, limites honestos e demonstração reproduzível. M4 a M6 são incrementos sobre uma base que já pode ser mostrada, não pré-requisitos para mostrá-la. Num projeto sem prazo, esse marco é a defesa contra o modo de falha mais comum: seis marcos pela metade e nada apresentável.

**A demonstração é por README com `curl`**, o que promove o M0 a peça crítica — é o primeiro contato do avaliador, e uma falha ali encerra a leitura antes dos ADRs. Duas demonstrações que o README deve conter, porque provam competências difíceis em poucos comandos:

1. **Consistência sem transação distribuída:** `docker compose stop keycloak` → `POST /tenants` responde `202` normalmente → `docker compose start keycloak` → o tenant vira `Active` sozinho.
2. **Isolamento multi-tenant:** com token do tenant A, tentar a rota do tenant B (403), e tentar um `memberId` do tenant B dentro da rota do tenant A (404).

---

## 17. Anti-patterns proibidos

**1. Resource Owner Password Credentials (ROPC).** Receber usuário e senha na API e repassar ao Keycloak. O fluxo foi removido no OAuth 2.1, expõe credenciais ao backend e anula o MFA. O login interativo usa sempre Authorization Code com PKCE.

**2. Validar tokens chamando a Gateway.** Transformaria a Gateway em gargalo e em ponto único de falha, criando um monólito distribuído. A validação é local, com JWKS e `Microsoft.AspNetCore.Authentication.JwtBearer`.

**3. Armazenar senhas, hashes ou segredos de usuário no banco da Gateway.** Viola o princípio de responsabilidade única do Keycloak e amplia o escopo de compliance (LGPD). A exceção são segredos de clients M2M, que também não são guardados: são devolvidos uma única vez e ficam apenas no Keycloak.

**4. Chamar a Admin API do Keycloak fora da Infrastructure.** Controllers, endpoints e handlers conhecem apenas `IIdentityProvider`. A regra é verificada por teste de arquitetura.

**5. Reassinar ou enriquecer tokens na Gateway.** Criaria um segundo Authorization Server. Claims vêm somente de Protocol Mappers (ADR-004).

**6. Chamar o Keycloak dentro da transação de um comando.** A escrita local e o evento no Outbox são atômicos; o efeito externo acontece depois, de forma idempotente (ADR-006).

**7. Confiar apenas na presença do claim `tenant_id`.** Todo acesso de um ator de tenant compara o tenant da rota com o do token (`SameTenantRequirement`) **e** verifica que cada sub-recurso da rota pertence a esse tenant (§6.4) — as duas coisas, porque a primeira sozinha não impede informar o id de um recurso alheio. A exceção nomeada é o **client de plataforma** (§10.1), cujo acesso é irrestrito por desenho e auditado por chamada.

**8. Retry automático em POST para a Admin API.** Pode criar recursos duplicados. A idempotência vem do padrão "consultar antes de criar", não do retry.

---

## 18. Critérios de pronto por funcionalidade

Uma funcionalidade só é considerada pronta quando tem:

- endpoint documentado no OpenAPI, com exemplos de requisição e de erro;
- teste unitário das regras de domínio envolvidas;
- teste de integração do caminho feliz contra o Keycloak real;
- **teste negativo para cada regra de isolamento que a funcionalidade toca** — tenant da rota, pertencimento de cada sub-recurso ao tenant, e escopo de client M2M. Não é "se a rota tiver `{tenantId}`": essa formulação deixava passar sub-recursos, rotas sem `{tenantId}` e clients de plataforma;
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
