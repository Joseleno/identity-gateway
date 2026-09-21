# Handoff — vertical de registro de tenant

> **Data:** 2026-09-21 · **Marco:** M0 · **Status:** brainstorming interrompido no meio, por decisão
> **Onde parou:** seção 1 do design (Application) apresentada e aprovada; seções 2 a 4 não escritas.
>
> Referência normativa: [`especificacao-arquitetural-v2.3.md`](../../especificacao-arquitetural-v2.3.md)
> §8, §9.1, §10.1, §11.4, §11.7 e §12.1.

---

## Estado do repositório

`main` em `86082b7`, sincronizada com o remoto, working tree limpa.

A fatia anterior — o agregado `Tenant` — está entregue e integrada: **170 testes, 0 falhas**, build limpo
com `TreatWarningsAsErrors`. O domínio tem `Tenant` (`Register`, `MarkProvisioned`,
`MarkProvisioningFailed`, `ReserveSeat`, `ReleaseSeat`), os value objects, os 7 estados, 2 domain events
registrados no mapa do Outbox, e a migration `CriacaoDoOutbox`.

**Restam 2 skips de arquitetura**, ambos em `RegrasDeMensageriaTests`: `Handlers_SaoSealed` e
`CommandsEQueries_SaoRecord`. Os dois aguardam exatamente o handler desta fatia. Os 2 testes de `401` em
`SegurancaTests` aguardam um endpoint protegido existir.

## Classificação

**Arquitetural.** A fatia estabelece três camadas de uma vez — Application, Infrastructure e Api — e
nenhuma delas tem fluxo existente no repo para seguir. Pelo processo, exige design em seções, spec escrita
e plano antes de qualquer código.

## Decisões já fechadas

### 1. Escopo: vertical completa, com autenticação mínima

Handler + portas + EF/migration + módulo Carter + a policy `PlatformAdmin`. Destrava os 4 skips restantes:
os 2 de mensageria (passa a haver handler e command) e os 2 de `401` (passa a haver endpoint protegido).

**Descoberta que reduz o trabalho:** a autenticação JWT **já está cabeada** na Api, herdada do template —
`AddAuthentication(JwtBearerDefaults)` + `AddJwtBearer` lendo `JwtOptions`, e `AddAuthorization()` sem
policies nomeadas (`src/IdentityGateway.Api/DependencyInjection.cs:85-101`). Falta apenas declarar a
policy.

### 2. Policy: `RequireClaim("roles", "platform-admin")`

Claim **plano**, não `RequireRole`. A §12.1 documenta que `RequireRole` falha com o Keycloak porque o papel
chega aninhado em `realm_access.roles` — e chama isso de "o ponto que mais gera erro nessa integração".

Usar o claim plano funciona hoje com o `JwtTokenService` dos testes e continua funcionando quando o
Keycloak entrar, desde que o client scope `gateway-roles` esteja configurado — que é um dos 4 itens
críticos do M0 já listados no handoff da fundação. `RequireRole` passaria agora e quebraria depois.

Descartado: requirement com handler próprio lendo as duas formas. Seria código especulativo — a §12.1 já
decidiu que o claim será plano.

### 3. Catálogo de planos: em memória, lido da configuração

`PlanCatalog` na Infrastructure, planos vindos do `appsettings`:

```json
"Plans": {
  "free":       { "tier": "Free",       "maxUsers": 5,   "maxClients": 1 },
  "standard":   { "tier": "Standard",   "maxUsers": 50,  "maxClients": 5 },
  "enterprise": { "tier": "Enterprise", "maxUsers": 500, "maxClients": 50 }
}
```

Mudar limite comercial é editar config e reiniciar, sem deploy nem migration. Plano é dado de catálogo, não
entidade: a §6.1 o trata como **value object dentro do `Tenant`**, sem ciclo de vida nem histórico próprio.

Descartado: tabela no banco (acrescentaria entidade que nada no M0 pede) e dicionário fixo no código
(transformaria decisão comercial em release de engenharia).

## Seção 1 do design — Application (aprovada)

**`RegisterTenantCommand(string Name, string Slug, string PlanCode, string InitialAdminEmail)`**
→ `ICommand<TenantId>`

**O `InitialAdminEmail` é acréscimo deliberado ao que a §11.4 mostra.** A §9.1 e a §8 o exigem no
`POST /tenants`, e a §9.1 explica por quê: sem ele o tenant nasce trancado, já que convidar membros exige
`tenant-admin` daquele tenant, que ainda não existiria. A §11.4 é código de referência anterior a essa
decisão — **o texto normativo prevalece**, como a fatia anterior já registrou.

Nesta fatia o campo é **validado e carregado até o evento**, mas nada cria o convite: isso é trabalho do
consumidor do provisionamento (passo 2 dos três da §9.1). Sem carregá-lo, o endpoint aceitaria um campo
obrigatório e o descartaria em silêncio.

**`RegisterTenantHandler`** segue a §11.4: valida o slug, checa unicidade, resolve o plano, registra,
persiste. Nenhuma chamada ao Keycloak — o `INSERT` e o evento no Outbox na mesma transação, que é o que
garante que ou os dois acontecem ou nenhum.

**Duas portas**, em `Application/Common/Abstractions/`:

| Porta | Assinatura |
|---|---|
| `ITenantRepository` | `void Add(Tenant)`, `Task<bool> SlugExistsAsync(TenantSlug, CancellationToken)` |
| `IPlanCatalog` | `Plan? Find(string planCode)` |

> **Divergência de caminho, deliberada.** A §7 desenha `Application/Abstractions/`, mas o repositório já
> tem `Application/Common/Abstractions/` vindo do template, com as 8 abstrações da fundação. Seguir o repo
> evita duas pastas de abstração com o mesmo papel. Mesma classe de decisão que a fatia anterior tomou
> sobre `RaiseDomainEvent` e o construtor de `Entity<TId>`.

**`RegisterTenantValidator`** (FluentValidation, registrado um a um em `AddValidators` — o template não faz
varredura de assembly de propósito): nome obrigatório e ≤ 200, slug obrigatório, `planCode` obrigatório,
e-mail obrigatório e com forma de e-mail.

A validação de **forma** do slug não é duplicada aqui: ela vive em `TenantSlug.Create`, que devolve
`Result`. O validator cobre presença; o value object cobre forma.

Erro de negócio não lança em nenhum ponto desta camada — o handler devolve `Result<TenantId>`.

## O que falta decidir (seções 2 a 4 do design)

| Seção | Perguntas em aberto |
|---|---|
| **2 · Infrastructure** | Mapeamento EF do `Tenant`: como persistir `TenantId` (conversor de valor), `TenantSlug` e `Plan` (owned type ou colunas planas)? O índice único do slug é global ou tem filtro? O `Tenant` precisa mesmo do ctor sem parâmetro (ver pendência abaixo)? Concorrência otimista por `xmin`, que a §6.1 exige para `OccupiedSeats`. |
| **3 · Api** | Forma do `RegisterTenantRequest` e do `TenantAcceptedResponse`; `202` com `Location` apontando para `GET /tenants/{id}/provisioning` (§8) — que não existe nesta fatia; versionamento `/api/v1` com `Asp.Versioning.Http` (§8). |
| **4 · Testes** | O que é unitário com fake, o que é integração com Testcontainers, e o que é funcional pela API. Em especial: como provar que o evento foi gravado no Outbox **na mesma transação** do `INSERT`. |

## Pendência herdada, a resolver na seção 2

**`Tenant` não tem construtor sem parâmetro.** A §11.1 tem `private Tenant() { }` comentado como "exigido
pelo EF Core", e o agregado entregue não o tem — `Entity<TId>` exige o id no construtor, então ele só
compilaria como `base(default(TenantId))` com três `null!`, um agregado momentaneamente inválido para
agradar o ORM.

A decisão foi **adiada para esta fatia**, com a razão registrada: o EF Core sabe mapear construtor
parametrizado, e decidir a forma sem a configuração EF na frente seria adivinhar. Agora a configuração
está na frente. Custo, se for necessário: uma linha.

## Regra de desenho fixada na fatia anterior

**Payload de evento é contrato de fio.** Carregue primitivo, nunca value object com construtor privado.

`TenantRegistered` nasceu carregando `TenantSlug` e lançava `NotSupportedException` ao voltar do Outbox —
toda mensagem iria a dead-letter e **nenhum tenant seria provisionado**. A regra
`RegrasDoOutboxTests.TodoEventoRegistrado_SobreviveAoRoundTripDoOutbox` agora pega isso no build.

Vale para o `InitialAdminEmail` quando ele entrar em algum evento: `string`, não um value object `Email`.

## Como retomar

O brainstorming parou entre a seção 1 e a 2. Retomar significa apresentar a seção 2 (Infrastructure),
seguir para 3 e 4, escrever o spec em `docs/superpowers/specs/`, e então o plano. As três decisões acima
não precisam ser reabertas.
