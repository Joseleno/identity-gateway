# IdentityGateway — Especificação Arquitetural

> Multi-Tenant Identity & Governance API em .NET 10 com Keycloak
> Versão 2.0 · Status: proposta para implementação

---

## 1. Sobre este projeto

O **IdentityGateway** é um projeto de portfólio que mostra como construir, em .NET 10, uma API de governança de identidade multi-tenant sobre o Keycloak. É um projeto **API-first**: não existe front-end neste repositório, e toda interação acontece por endpoints REST documentados em OpenAPI.

A API atua como **camada de abstração e orquestração de governança corporativa**. Aplicações clientes (Web, Mobile e serviços M2M) usam a API para descobrir como autenticar seus usuários, gerenciar tenants, usuários, papéis e credenciais de máquina, e consultar permissões. A governança é **centralizada na definição** e **desacoplada na execução**: a Gateway é a fonte da verdade sobre quem pode o quê, mas nenhuma requisição de negócio depende dela para ser autorizada.

O projeto quer demonstrar, com código verificável, quatro competências: integração correta com OAuth 2.0 e OpenID Connect sem atalhos inseguros; Clean Architecture com DDD e CQRS aplicados a um domínio real; consistência entre dois sistemas sem transação distribuída; e isolamento multi-tenant garantido por testes automatizados.

O repositório parte do template [CleanStart](https://github.com/Joseleno/CleanStart), herdando sua estrutura de camadas e convenções.

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

A regra que resume o desenho: **a Gateway nunca está nos caminhos (2) e (3)**. Se ela cair, logins e requisições de negócio continuam funcionando; apenas operações de gestão ficam indisponíveis.

---

## 3. Princípios

1. **API-first e stateless.** Todo recurso de gestão é um endpoint REST versionado e documentado. A API não mantém sessão; cada requisição carrega seu próprio token.
2. **Control Plane separado do Data Plane.** Gestão centralizada, validação distribuída.
3. **Keycloak como único dono de credenciais e único emissor de tokens.** A Gateway não armazena, transporta nem reassina nada disso.
4. **Fail closed.** Na dúvida (claim ausente, tenant divergente, dependência indisponível em operação sensível), a resposta é negar.
5. **Isolamento multi-tenant verificável.** Toda regra de isolamento tem um teste negativo correspondente.
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
- **Consequências:** sem ponto único de falha no login nem no tráfego de negócio; sem latência adicional por requisição. A Gateway não é um BFF: se alguma aplicação futura precisar de BFF, ele será por aplicação e fora deste repositório.

### ADR-003 — Sem ROPC e sem manipulação de credenciais

- **Decisão:** o fluxo *Resource Owner Password Credentials* é proibido. A criação de usuários não define senha: o Keycloak envia ao usuário um e-mail de ações obrigatórias (`UPDATE_PASSWORD`, `VERIFY_EMAIL`).
- **Consequências:** MFA e políticas do Keycloak valem para todos os fluxos; o banco da Gateway fica fora do escopo mais sensível de compliance.

### ADR-004 — Claims vêm exclusivamente de Protocol Mappers

- **Contexto:** o token é assinado pelo Keycloak; a Gateway não pode alterá-lo sem se tornar um segundo Authorization Server.
- **Decisão:** todo claim customizado (`tenant_id`, `roles`, `tier`) é produzido por Protocol Mappers configurados no Keycloak. Dados de negócio que precisam estar no token (como `tier`) são sincronizados pela Gateway como atributos da Organization.
- **Consequências:** nenhum mapper customizado em Java consulta a Gateway durante a emissão do token, o que evitaria uma dependência de runtime no login. Mudanças de plano refletem no token no próximo refresh.

### ADR-005 — Papéis globais no token, permissões finas na Gateway

- **Decisão:** o token carrega apenas `tenant_id` e um catálogo pequeno e fixo de papéis (`platform-admin`, `tenant-admin`, `financial-manager`, `reader`). Permissões finas, customizáveis por tenant (ex.: `invoices:approve`), vivem na Gateway como *Permission Sets* e são resolvidas pelas APIs consumidoras com cache.
- **Consequências:** token pequeno, sem risco de estourar limites de header; revogação de permissão fina tem janela de atraso igual ao TTL do cache (mitigada por evento de invalidação); o access token tem vida curta (5 minutos).

### ADR-006 — Consistência via Outbox, provisionamento idempotente e reconciliação

- **Contexto:** a Admin API do Keycloak não é transacional, e a criação de um tenant envolve o banco da Gateway e o Keycloak.
- **Decisão:** o comando grava o aggregate em estado `Pending` e o evento no **Outbox do MassTransit** na mesma transação. Um consumidor executa o provisionamento em passos idempotentes ("garanta que existe"). Um job periódico reconcilia divergências entre os dois lados.
- **Consequências:** o endpoint de criação responde `202 Accepted` com o recurso de status do provisionamento; nenhuma falha do Keycloak deixa estado órfão sem ser detectada.

### ADR-007 — Sincronização Keycloak → Gateway por leitura de eventos

- **Contexto:** usuários podem nascer fora da Gateway, por exemplo no primeiro login via IdP federado, o que contornaria o limite de usuários do plano.
- **Decisão:** na v1, um *background service* lê periodicamente os eventos de usuário e os eventos administrativos pela Admin API, com checkpoint e deduplicação, e os converte em comandos da Application. Um Event Listener SPI em Java, com envio direto ao RabbitMQ, fica como evolução.
- **Consequências:** o projeto continua 100% .NET; a latência de sincronização é igual ao intervalo de polling.

### ADR-008 — Integração com o Keycloak isolada atrás de uma porta

- **Decisão:** a Application conhece apenas a interface `IIdentityProvider`. A implementação fica na Infrastructure, com um cliente HTTP tipado próprio que cobre somente os endpoints usados. Nenhum tipo do Keycloak atravessa essa fronteira, e um teste de arquitetura garante isso.
- **Alternativa considerada:** gerar o cliente completo a partir do OpenAPI da Admin API (Kiota). Rejeitada na v1 pelo volume de código gerado frente aos poucos endpoints usados; pode ser revista se a superfície de integração crescer.

### ADR-009 — Na v1, um usuário pertence a um único tenant

- **Decisão:** cada usuário é membro de exatamente uma Organization.
- **Consequências:** o claim `tenant_id` é único e não ambíguo; papéis de realm podem ser atribuídos ao usuário sem vazar entre tenants. Usuários em múltiplos tenants exigiriam seleção de organização no login e papéis por organização, e ficam registrados como evolução.

---

## 5. Responsabilidades: Keycloak × IdentityGateway

| Componente | Keycloak | IdentityGateway (.NET 10) |
|---|---|---|
| Credenciais | Hash (Argon2/PBKDF2), expiração, reset, MFA/TOTP | Nenhuma. Nunca manipula senhas |
| Emissão de tokens | Assinatura dos JWT (RS256/ES256), refresh token rotation | Nenhuma. Provisiona os clients que solicitam tokens |
| Claims | Protocol Mappers produzem `tenant_id`, `roles`, `tier` | Mantém sincronizados os atributos que alimentam os mappers |
| Tenants | Organization, domínios e IdPs vinculados | Aggregate `Tenant`: plano, limites, status, ciclo de vida |
| Usuários | Perfil, dados pessoais, sessões | Vínculo (`sub`), status de governança, papéis e permission sets |
| Permissões finas | Nenhuma | Fonte da verdade dos Permission Sets por tenant |
| Step-up auth | Executa o fluxo exigido via `acr_values` e emite o claim `acr` | Documenta os níveis exigidos por operação sensível |
| Auditoria | Eventos de login e eventos administrativos | Trilha de auditoria de toda ação de governança |

---

## 6. Modelo de domínio

O bounded context é **Identity Governance**. Os dados pessoais (nome, e-mail, telefone) ficam no Keycloak. A Gateway guarda apenas o identificador do usuário no Keycloak (`sub`) e os dados de governança, o que limita o impacto de um eventual vazamento do seu banco.

### 6.1. Aggregates

**`Tenant`** (aggregate root)
- Identidade: `TenantId`, `TenantSlug` (vira o *alias* da Organization) e `ExternalOrganizationId` (id no Keycloak, preenchido após o provisionamento).
- Estado: `Plan` (value object com `Tier`, `MaxUsers` e `MaxClients`), `Status` e a lista de `EmailDomain`.
- Controle de vagas: um contador de membros ativos, protegido por concorrência otimista (`xmin` do PostgreSQL). Duas reservas simultâneas não ultrapassam o limite do plano.
- Invariantes: o slug é único e imutável; um tenant fora do status `Active` não aceita novos membros; o número de membros ativos nunca excede `Plan.MaxUsers`.

**`Member`** (aggregate root, referencia `TenantId`)
- Dados: `ExternalUserId` (o `sub`), `Status` (`Invited`, `Active`, `Deactivated`, `Erased`), papéis do catálogo global e referências a Permission Sets.
- Invariante: um membro `Erased` é terminal e não guarda nenhum dado que identifique a pessoa.

**`PermissionSet`** (aggregate root, referencia `TenantId`)
- Dados: nome e conjunto de `Permission` (value object no formato `recurso:ação`, por exemplo `invoices:approve`).
- Invariante: o nome é único dentro do tenant.

**`ClientApplication`** (aggregate root, referencia `TenantId`)
- Dados: `ClientId`, tipo (`Public` para PKCE, `Confidential` para M2M), método de autenticação (`private_key_jwt` preferencial, `client_secret` como alternativa) e status.

### 6.2. Máquina de estados do tenant

```
             Register
  (novo) ──────────────► Pending ──────────► Active ◄──────────┐
                            │    provisioned    │    reactivate│
                            │                   ▼              │
               retries      │               Suspended ─────────┘
               esgotados    ▼
                   ProvisioningFailed ── retry manual ──► Pending
```

### 6.3. Domain services e regras transversais

- **`RoleAssignmentPolicy`** impede escalação de privilégio: um ator só atribui papéis do próprio tenant e nunca acima do seu papel mais alto. A hierarquia é `platform-admin` > `tenant-admin` > `financial-manager` > `reader`.
- **Domain events:** `TenantRegistered`, `TenantActivated`, `TenantSuspended`, `MemberInvited`, `MemberDeactivated`, `MemberRolesChanged` e `PermissionSetChanged`. São convertidos em eventos de integração e publicados pelo Outbox.

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
│   ├── IdentityGateway.Api/                  # Presentation: Minimal APIs, policies, OpenAPI
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

| Recurso | Método e rota | Quem pode |
|---|---|---|
| **Discovery** | `POST /auth/discovery` | Anônimo, com rate limit |
| **Tenants** | `POST /tenants` → `202 Accepted` | platform-admin |
| | `GET /tenants`, `GET /tenants/{tenantId}` | platform-admin; tenant-admin só o próprio |
| | `GET /tenants/{tenantId}/provisioning` | platform-admin |
| | `PATCH /tenants/{tenantId}` (nome, plano) | platform-admin |
| | `POST /tenants/{tenantId}/suspend`, `/reactivate` | platform-admin |
| **Domínios e federação** | `POST/DELETE /tenants/{tenantId}/domains` | tenant-admin |
| | `POST/DELETE /tenants/{tenantId}/identity-providers` | tenant-admin |
| **Membros** | `POST /tenants/{tenantId}/members` (convite) | tenant-admin |
| | `GET /tenants/{tenantId}/members`, `GET .../members/{memberId}` | tenant-admin |
| | `POST .../members/{memberId}/deactivate`, `/reactivate` | tenant-admin |
| | `DELETE .../members/{memberId}` (exclusão definitiva, LGPD) | tenant-admin |
| | `PUT .../members/{memberId}/roles` | tenant-admin, sujeito à `RoleAssignmentPolicy` |
| **Permissões** | `GET/POST/PUT/DELETE /tenants/{tenantId}/permission-sets` | tenant-admin |
| | `GET .../members/{memberId}/effective-permissions` | tenant-admin; clients M2M com escopo `gateway.permissions.read` |
| **Clients M2M** | `POST /tenants/{tenantId}/clients` | tenant-admin |
| | `POST .../clients/{clientId}/rotate-credentials`, `DELETE ...` | tenant-admin |
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

Se os retries se esgotarem, o tenant vai para `ProvisioningFailed` e o evento fica disponível para retry manual. O job de reconciliação compara periodicamente os tenants `Active` com as Organizations existentes e registra divergências.

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
4. Se o limite do plano foi atingido, o membro é registrado, o tenant é marcado como acima do limite e uma entrada de auditoria é gerada. Bloquear o login exigiria um authenticator customizado no Keycloak e fica fora da v1.

### 9.5. Desativação e exclusão de membro

- **Desativar:** a Gateway desabilita o usuário no Keycloak **e revoga suas sessões ativas**. Sem a revogação, um refresh token emitido antes continuaria funcionando. A vaga do plano é liberada.
- **Excluir (direito ao esquecimento, LGPD art. 18):** exclusão definitiva no Keycloak; na Gateway, o membro passa a `Erased` e o `ExternalUserId` é substituído por um valor anônimo. A trilha de auditoria preserva a ação, não a identidade.

### 9.6. Permissões finas numa API consumidora

1. A requisição chega com o JWT, que é validado localmente.
2. O endpoint exige a permissão `invoices:approve`.
3. A biblioteca `IdentityGateway.Client.AspNetCore` busca as permissões efetivas de (`sub`, `tenant_id`) no cache local. Se não houver entrada, consulta a Gateway uma vez e guarda o resultado com TTL curto.
4. O evento `PermissionsChanged`, recebido via RabbitMQ, invalida a entrada correspondente.
5. Se a Gateway estiver indisponível e não houver cache, a decisão é **negar** (fail closed).

---

## 10. Segurança

### 10.1. Autenticação e autorização da própria Gateway

A Gateway é o alvo mais valioso do sistema: quem a controla cria administradores em qualquer tenant. Por isso ela recebe o mesmo rigor que exige das outras APIs.

- Aceita apenas tokens do realm `identity-gateway` com audiência `identity-gateway-api`, garantida por um Audience Mapper.
- Três famílias de policies: `PlatformAdmin`; `TenantAdmin`, sempre combinada com `SameTenantRequirement`; e escopos para clients M2M (`gateway.members.write`, `gateway.permissions.read`).
- **`SameTenantRequirement`** compara o `tenantId` da rota com o claim `tenant_id` do token. Esta é a defesa contra BOLA/IDOR entre tenants, e cada endpoint com `{tenantId}` tem um teste negativo que tenta acessar outro tenant.
- **`RoleAssignmentPolicy`** impede que um tenant-admin conceda papéis acima do próprio.
- Operações destrutivas (exclusão de membro, rotação de credenciais) exigem **step-up**: o token precisa ter `acr` de nível elevado, obtido via `acr_values` no login.

### 10.2. Credenciais da Gateway no Keycloak

- A Gateway usa um client confidencial próprio, autenticado por `private_key_jwt`, com chave em cofre de segredos. User Secrets só no ambiente local.
- O service account recebe **somente** os papéis de `realm-management` necessários para as operações implementadas. O conjunto mínimo é validado pelos testes de integração: um papel sem uso é removido.
- A Gateway nunca usa o realm `master`.

### 10.3. Proteções gerais

- **Rate limiting** nativo do ASP.NET Core, particionado por tenant, com limite mais restrito no discovery.
- **Idempotência:** POSTs com `Idempotency-Key` guardam a resposta por 24 horas.
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
    public int ActiveMembers { get; private set; }               // protegido por concorrência otimista

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
    /// versão no SaveChanges (xmin), e a segunda é reprocessada com o valor atual.
    /// </summary>
    public Result ReserveSeat()
    {
        if (Status != TenantStatus.Active)
            return TenantErrors.NotActive(Id);

        if (ActiveMembers >= Plan.MaxUsers)
            return TenantErrors.SeatLimitReached(Plan.MaxUsers);

        ActiveMembers++;
        return Result.Success();
    }

    public void ReleaseSeat() => ActiveMembers = Math.Max(0, ActiveMembers - 1);
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
    public async Task<string> EnsureOrganizationAsync(TenantSlug slug, string name, CancellationToken ct)
    {
        // Procura antes de criar: uma tentativa anterior pode ter criado a
        // Organization e falhado antes de gravar o id no banco da Gateway.
        var existing = await admin.FindOrganizationByAliasAsync(slug.Value, ct);
        if (existing is not null)
            return existing.Id;

        try
        {
            return await admin.CreateOrganizationAsync(
                new OrganizationRepresentation(Name: name, Alias: slug.Value, Enabled: true), ct);
        }
        catch (KeycloakConflictException)
        {
            // Duas entregas da mesma mensagem concorreram e a outra venceu.
            var created = await admin.FindOrganizationByAliasAsync(slug.Value, ct);
            return created?.Id
                ?? throw new IdentityProviderException($"Organization '{slug.Value}' em estado inconsistente.");
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

    // FindOrganizationByAliasAsync, InviteUserAsync, etc.
}
```

### 11.7. Presentation: endpoint e isolamento entre tenants

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
public sealed class SameTenantHandler : AuthorizationHandler<SameTenantRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, SameTenantRequirement requirement)
    {
        // Com endpoint routing, o Resource é o próprio HttpContext.
        if (context.Resource is not HttpContext http)
            return Task.CompletedTask;

        var routeTenant = http.GetRouteValue("tenantId")?.ToString();
        var tokenTenant = context.User.FindFirst("tenant_id")?.Value;

        // Fail closed: se qualquer lado estiver ausente, o acesso é negado.
        if (!string.IsNullOrEmpty(routeTenant) &&
            string.Equals(routeTenant, tokenTenant, StringComparison.Ordinal))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
```

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
- **Tenant:** o claim `tenant_id` é produzido pelo mapper de organização, configurado para emitir o id da Organization com esse nome.

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

## 13. Estratégia de testes

Todos os testes usam **xUnit**.

| Nível | O que cobre | Ferramentas |
|---|---|---|
| Unitário (Domain) | Invariantes do `Tenant`, limite de vagas, máquina de estados, `RoleAssignmentPolicy`, value objects | xUnit, sem mocks |
| Unitário (Application) | Handlers com `IIdentityProvider` e repositórios falsos; erros de validação | xUnit, fakes escritos à mão |
| Integração | Endpoints reais contra Keycloak, PostgreSQL e RabbitMQ em contêineres; os mappers de verdade (`roles`, `aud`, `tenant_id`) | `WebApplicationFactory`, Testcontainers (incluindo o módulo de Keycloak) |
| Formato dos claims | Token do Keycloak real traz `roles` plano; endpoint com `RequireRole` aceita esse token (seção 12.1) | Testcontainers, `JsonWebToken` |
| Autorização negativa | Acesso ao tenant B com token do tenant A, em **todo** endpoint com `{tenantId}`; tentativa de escalação de papel; token sem audiência correta | Teste parametrizado gerado a partir da tabela de rotas |
| Idempotência e consistência | Mensagem entregue duas vezes; falha do Keycloak no meio do provisionamento; reconciliação detectando divergência | Testcontainers, com a falha simulada por um `DelegatingHandler` |
| Arquitetura | Regras de dependência da seção 7; nenhum tipo do Keycloak fora de `Infrastructure/Identity` | NetArchTest ou ArchUnitNET |
| Contrato | Documento OpenAPI gerado comparado com a versão aprovada, o que evita *breaking changes* acidentais | Snapshot do documento |

O teste negativo de autorização é o mais importante do projeto: ele percorre as rotas registradas e falha se aparecer um endpoint com `{tenantId}` sem cobertura. Assim, um endpoint novo não entra desprotegido por esquecimento.

---

## 14. Observabilidade

- **Logs estruturados** com Serilog, sempre com `tenantId`, `correlationId` e `sub` do ator. Nunca com tokens ou dados pessoais.
- **Traces** com OpenTelemetry cobrindo a requisição HTTP, o Outbox, o consumidor e as chamadas à Admin API.
- **Métricas:** duração e falhas de provisionamento, latência das chamadas ao Keycloak, divergências encontradas pela reconciliação, atraso da sincronização de eventos e rejeições por rate limit.
- **Health checks:** `live` verifica apenas o processo; `ready` verifica PostgreSQL, RabbitMQ e a metadata OIDC do Keycloak.

---

## 15. Ambiente local e configuração como código

O `docker-compose.yml` sobe:

| Serviço | Papel |
|---|---|
| `keycloak` | Versão 26.x fixada; importa `realm-identity-gateway.json` na primeira subida |
| `postgres` | Um servidor com dois bancos: `keycloak` e `identity_gateway` |
| `rabbitmq` | Mensageria, com a interface de gerenciamento habilitada |
| `mailpit` | Captura os e-mails de convite e de ações obrigatórias do Keycloak |
| `identity-gateway-api` | A API |
| `sample-resource-api` | A API de exemplo do Data Plane |

O arquivo de bootstrap contém o mínimo para o ambiente local: realm com Organizations habilitado, client e service account da Gateway, client scope com os mappers (`roles`, `tenant_id`, audiência), catálogo de papéis e armazenamento de eventos ativado. A evolução da configuração do realm é feita com o provider Terraform do Keycloak, porque arquivos de export não produzem diffs revisáveis nem aplicam mudanças incrementais.

A documentação interativa usa o suporte nativo a OpenAPI 3.1 do .NET 10, com esquema de segurança OAuth2 (Authorization Code com PKCE e Client Credentials) e interface Scalar.

---

## 16. Roadmap

Cada marco termina com algo demonstrável e testado.

| Marco | Entrega | Resultado demonstrável |
|---|---|---|
| **M0 · Fundação** | Repositório a partir do CleanStart, Docker Compose, bootstrap do realm, health checks, CI com testes de arquitetura | `docker compose up` sobe o ecossistema inteiro |
| **M1 · Tenants** | Registro, provisionamento via Outbox, status, suspensão, reconciliação | Tenant criado com o Keycloak fora do ar é provisionado quando ele volta |
| **M2 · Membros e papéis** | Convite com e-mail de ações obrigatórias, desativação com revogação de sessões, exclusão LGPD, papéis com `RoleAssignmentPolicy`, limite de vagas | Suíte de autorização negativa verde |
| **M3 · Data Plane** | `IdentityGateway.Client.AspNetCore` e `SampleResourceApi` | Requisição de negócio autorizada sem nenhuma chamada à Gateway |
| **M4 · Federação e discovery** | Domínios, IdP externo por tenant, endpoint de discovery, sincronização de eventos do Keycloak | Login federado cria o membro e respeita o limite do plano |
| **M5 · Permissões finas** | Permission Sets, permissões efetivas, cache com invalidação por evento | Permissão revogada deixa de valer sem novo login |
| **M6 · M2M** | Clients com `private_key_jwt`, rotação de credenciais | Serviço parceiro chamando a Sample API com Client Credentials |
| **M7 · Hardening** | Step-up em operações destrutivas, rate limiting, auditoria, OpenTelemetry, README com o passo a passo da arquitetura | Repositório pronto para apresentação |

---

## 17. Anti-patterns proibidos

**1. Resource Owner Password Credentials (ROPC).** Receber usuário e senha na API e repassar ao Keycloak. O fluxo foi removido no OAuth 2.1, expõe credenciais ao backend e anula o MFA. O login interativo usa sempre Authorization Code com PKCE.

**2. Validar tokens chamando a Gateway.** Transformaria a Gateway em gargalo e em ponto único de falha, criando um monólito distribuído. A validação é local, com JWKS e `Microsoft.AspNetCore.Authentication.JwtBearer`.

**3. Armazenar senhas, hashes ou segredos de usuário no banco da Gateway.** Viola o princípio de responsabilidade única do Keycloak e amplia o escopo de compliance (LGPD). A exceção são segredos de clients M2M, que também não são guardados: são devolvidos uma única vez e ficam apenas no Keycloak.

**4. Chamar a Admin API do Keycloak fora da Infrastructure.** Controllers, endpoints e handlers conhecem apenas `IIdentityProvider`. A regra é verificada por teste de arquitetura.

**5. Reassinar ou enriquecer tokens na Gateway.** Criaria um segundo Authorization Server. Claims vêm somente de Protocol Mappers (ADR-004).

**6. Chamar o Keycloak dentro da transação de um comando.** A escrita local e o evento no Outbox são atômicos; o efeito externo acontece depois, de forma idempotente (ADR-006).

**7. Confiar apenas na presença do claim `tenant_id`.** Todo acesso a recurso de um tenant compara o tenant da rota com o do token (`SameTenantRequirement`).

**8. Retry automático em POST para a Admin API.** Pode criar recursos duplicados. A idempotência vem do padrão "consultar antes de criar", não do retry.

---

## 18. Critérios de pronto por funcionalidade

Uma funcionalidade só é considerada pronta quando tem:

- endpoint documentado no OpenAPI, com exemplos de requisição e de erro;
- teste unitário das regras de domínio envolvidas;
- teste de integração do caminho feliz contra o Keycloak real;
- teste negativo de autorização, se a rota tiver `{tenantId}`;
- entrada de auditoria para ações de escrita;
- ADR, se a funcionalidade introduzir uma decisão nova.

---

## 19. Limites conhecidos

Registrar os limites faz parte do projeto: eles mostram onde a arquitetura escolhe simplicidade de forma consciente.

- **Políticas de senha, força bruta e rotação de refresh token são por realm**, portanto iguais para todos os tenants (consequência do ADR-001).
- **Um usuário pertence a um único tenant** (ADR-009).
- **Permissões finas têm janela de atraso** igual ao TTL do cache quando o evento de invalidação não chega.
- **A sincronização Keycloak → Gateway tem latência** igual ao intervalo de polling (ADR-007).
- **O limite de usuários não bloqueia o primeiro login federado**; o excesso é detectado, auditado e sinalizado (fluxo 9.4).
- **O Keycloak roda em instância única** no ambiente local; clustering e multi-site ficam documentados, não implementados.
