# Fatia B — Consumidor do provisionamento

> **Data:** 2026-09-25 · **Marco:** M1 · **Status:** design aprovado em conversa, seção por seção; aguardando
> revisão da spec escrita.
> **Referência normativa:** [`especificacao-arquitetural-v2.4.md`](../../especificacao-arquitetural-v2.4.md). Esta
> fatia produz a **v2.5**, com a errata da §11.5 e as mudanças listadas na §8 abaixo.
> **Sucede:** [handoff da fundação Keycloak](2026-09-25-fundacao-keycloak-handoff.md) (fatia A, PR #2, mesclado
> em 2026-09-25).

---

## 1. Por que esta fatia existe

A fatia A entregou `IIdentityProvider.EnsureOrganizationAsync` contra um Keycloak real, mas ninguém a chama: a
mensagem `tenant-registered` que o `POST /tenants` grava no Outbox termina no `LoggingOutboxPublisher`, que só
escreve uma linha de log. O tenant nasce `Pending` e fica `Pending` para sempre.

Esta fatia fecha o provisionamento da §9.1 **sem o passo do convite** (fatia C): consome a mensagem, garante a
Organization, marca o tenant `Active` — ou `ProvisioningFailed`, quando o erro é permanente ou a janela de
retry se esgota — e expõe o status na rota que o `Location` do `202` já anuncia.

**Critério de sucesso:** a primeira demonstração da §16 funciona por `curl`. Com o Keycloak parado, o
`POST /tenants` responde `202` e o `GET .../provisioning` mostra `Pending`; o Keycloak volta, e em cerca de um
minuto o mesmo `GET` mostra `Active`, sem intervenção. Um teste de integração contra PostgreSQL e Keycloak reais
prova o mesmo caminho, com a falha injetada.

## 2. Decisões

| # | Decisão | Alternativa descartada, e por quê |
|---|---|---|
| D1 | **Sem broker nesta fatia.** O Outbox é o transporte, com despacho no próprio processo; o RabbitMQ vira fatia própria | RabbitMQ + MassTransit, como a §11.5 desenha: o objetivo da fatia é o provisionamento confiável, e o broker só se paga com um segundo consumidor. Além disso, a premissa da §11.5 envelheceu — ver §3 |
| D2 | **Despacho pelo Outbox existente** (`DispatchingOutboxPublisher` → command do Mediator) | Worker próprio guiado pelo estado do tenant (`Pending` + `next_attempt_at`): desvia do ADR-006, duplica o retry que o Outbox já tem, exige migration e advisory lock (ADR-010), e vira código morto ou concorrente quando o broker chegar |
| D3 | **Janela longa de provisionamento** (padrão 24h, configurável); esgotada, `ProvisioningFailed` | "Nunca esgota por falha transitória": apagaria o estado `ProvisioningFailed` da §6.2 e deixaria um Keycloak mal configurado (403, `invalid_client`) em `Pending` para sempre. "Janela do Outbox (~5 min) + retry manual": frágil para a demo e amplia o escopo com endpoint, autorização e a transição `Failed → Pending` |
| D4 | **A janela conta desde o registro do tenant** (`Tenant.RegisteredAt`, campo novo) | O `OccurredOn` do evento: é metadado de transporte, e além disso hoje volta errado da desserialização (§4.5) |
| D5 | **A decisão de desistir é do handler**, na Application | Gancho de "mensagem esgotada" no Outbox: poria regra de negócio na Infrastructure e não sobreviveria à troca pelo broker |
| D6 | **A fatia entrega `GET /tenants/{id}/provisioning`**, só com o status | Sem a rota, a demo só se verifica pelo banco ou pelo log; com `GET /tenants/{id}` junto, a superfície cresce sem necessidade agora |

## 3. Fato externo verificado: a licença do MassTransit

A §11.5 da v2.4 desenha o consumidor com MassTransit — retry e redelivery configurados nele, e um consumidor de
`Fault` marcando `ProvisioningFailed`. Verificado em 2026-09-25:

- o **MassTransit v9** é comercial: exige licença paga para uso em aplicação implantada (gratuito só para
  desenvolvimento local e avaliação);
- o **v8** segue Apache 2.0, mas com suporte — correções críticas e de segurança — **até o fim de 2026**, três
  meses a partir desta data.

Adotar o v8 agora seria entrar numa dependência a três meses do fim do suporte; o v9 poria licença comercial num
projeto de portfólio. A escolha do transporte e da biblioteca fica para a fatia do RabbitMQ, que decide entre
`RabbitMQ.Client` direto, v8 com plano de saída, v9 licenciado ou outra biblioteca. **Esta fatia não depende
dessa escolha:** o handler e o domínio são os mesmos com qualquer transporte.

## 4. Design

### 4.1. Componentes e fluxo

```
POST /tenants ──► RegisterTenantHandler ──► Tenant(Pending) + outbox_messages   (mesma transação — já existe)

OutboxWorker (a cada 5s) ──► OutboxProcessor reserva o lote (SKIP LOCKED — já existe)
        │
        ▼
DispatchingOutboxPublisher : IOutboxPublisher            ← substitui o LoggingOutboxPublisher
   · escopo de DI novo por mensagem
   · TenantRegistered ──► ISender.Send(ProvisionTenantCommand(tenantId))
   · TenantActivated  ──► "sem consumidor": registra e dá como entregue, explicitamente
        │
        ▼
ProvisionTenantHandler  (pipeline normal: logging → validation → transaction)
   · carrega o Tenant ─ EnsureOrganizationAsync ─ MarkProvisioned ─ commit (TenantActivated entra no Outbox)
```

**Peças novas ou alteradas:**

| Camada | Peça |
|---|---|
| Domain | `Tenant.RegisteredAt`; `Register` passa a receber o instante; `OccurredOn` dos eventos com `init` |
| Application | `Tenants/ProvisionTenant/ProvisionTenantCommand` + `ProvisionTenantHandler` (command interno, sem endpoint); `Tenants/GetTenantProvisioning/GetTenantProvisioningQuery` + handler + resposta; `ITenantRepository.GetAsync`; porta `IProvisioningPolicy` (a janela) |
| Infrastructure | `DispatchingOutboxPublisher`; `ProvisioningOptions` + implementação de `IProvisioningPolicy`; validação cruzada Outbox × janela; novos padrões do `OutboxOptions`; leitura sem rastreamento para a query; migration de `registered_at` |
| Api | Rota `GET /api/v1/tenants/{tenantId:guid}/provisioning` no `TenantsModule` |

**Por que uma porta para a janela, e não `IOptions` na Application:** a Application não referencia
`Microsoft.Extensions.Options` hoje, e o precedente do repositório é o `IPlanCatalog` — porta na Application,
implementação na Infrastructure a partir de options validadas. A janela segue o mesmo desenho.

### 4.2. Regras de fronteira

1. **Escopo de DI próprio por mensagem.** O `OutboxProcessor` rastreia as `OutboxMessage` do lote no
   `AppDbContext` do seu escopo e grava o resultado com um `SaveChanges` depois do despacho. Se o handler
   usasse o mesmo contexto, o `SaveChanges` do registro do lote gravaria também o que um handler que falhou
   deixou modificado — um tenant `Active` persistido por um handler que lançou exceção. O publisher abre um
   `AsyncServiceScope` novo e resolve o `ISender` dentro dele.
2. **Contrato handler ↔ publisher.**
   - Falha **transitória** chega ao publisher como **exceção**; o Outbox faz o retry.
   - **Todo desfecho terminal** — provisionado, falha permanente, janela esgotada, mensagem repetida, tenant
     inexistente — é `Result.Success`, e a mensagem sai do Outbox.
   - Um `Result.IsFailure` é erro de programação: o publisher registra e **lança**, para não engolir.
3. **Infrastructure despacha commands da Application** — respeita a direção Infra → App; os testes de
   arquitetura continuam como estão.
4. **Evento sem entrada no mapa do publisher lança.** Um evento novo não pode ser "entregue" sem que alguém
   tenha decidido o que fazer com ele — o mesmo princípio do `OutboxEventTypes.NomeDe`.

**Quando o broker chegar**, o `DispatchingOutboxPublisher` passa a publicar nele, e um consumidor do broker envia
o mesmo `ProvisionTenantCommand`. Nada abaixo do publisher muda.

### 4.3. `ProvisionTenantHandler`

```
tenant = await tenants.GetAsync(command.TenantId, ct)
if tenant is null            → Success   (log Warning: mensagem para tenant inexistente)
if tenant.Status != Pending  → Success   (repetição; Failed só sai por retry manual, via Pending)

try   orgId = await identity.EnsureOrganizationAsync(tenant.Id, tenant.Slug, tenant.Name, ct)
catch IdentityProviderInconsistencyException           → MarkProvisioningFailed; log Error; Success
catch Exception when (!ct.IsCancellationRequested
                      && clock.UtcNow >= tenant.RegisteredAt + policy.MaxPendingDuration)
                                                       → MarkProvisioningFailed; log Error; Success
// dentro da janela, a exceção sobe intacta e o Outbox repete

tenant.MarkProvisioned(orgId) → Success        (o commit é do TransactionBehavior)
```

**Só age em `Pending`.** A §11.5 ignorava apenas `Active`; mas uma mensagem repetida que encontrasse o tenant em
`ProvisioningFailed` o reprovisionaria em silêncio, desfazendo uma decisão registrada. A saída de `Failed` é o
retry manual (fatia futura), que devolve o tenant a `Pending` antes de reenfileirar. `MarkProvisioned` continua
aceitando `Failed` como origem — o domínio não muda.

**O filtro de cancelamento olha o `ct`, não o tipo da exceção.** Um timeout da resiliência chega como
`TaskCanceledException`, que **é** um `OperationCanceledException`; o filtro `ex is not OperationCanceledException`
tiraria da janela justamente os timeouts — o sintoma mais comum de Keycloak lento. Só o desligamento do host
(`ct` cancelado) fica fora, e a mensagem volta no próximo ciclo.

**Queda entre o Keycloak e o banco.** Se a Organization foi criada e o commit falhou, o retry chama
`EnsureOrganizationAsync` de novo; a busca por `gateway_tenant_id` a reencontra e devolve o mesmo id. Sem
duplicata — idempotência provada na fatia A.

**Logs.** Todo desfecho terminal registra `tenantId` e o desfecho; as falhas registram o tipo da exceção. Nunca
o nome do tenant nem dados pessoais (§14). Os `Error` de `ProvisioningFailed` são o único registro do motivo —
ver §4.6.

### 4.4. A rede do Outbox precisa ser maior que a janela

A janela é do handler, mas quem traz a mensagem de volta é o Outbox. Se o Outbox esgotasse as tentativas antes da
janela, a mensagem pararia e o tenant ficaria `Pending` para sempre, sem ninguém para marcá-lo `Failed`.

- **Validação cruzada na subida.** A soma **mínima** dos atrasos de backoff em `MaxAttempts` tentativas — sem o
  jitter, que só alonga — precisa exceder `Provisioning:MaxPendingDuration`; senão a subida falha com
  `OptionsValidationException` nomeando os dois valores. A soma usa **a mesma função** que o `OutboxProcessor`
  usa para calcular o atraso, extraída para ser compartilhada — duas cópias da fórmula derivariam.
- **Novos padrões:**

  | Opção | Antes | Depois | Por quê |
  |---|---|---|---|
  | `Outbox:MaxRetryDelaySeconds` | 300 | **60** | O teto é a latência entre o Keycloak voltar e o tenant ficar `Active`; na demo, ~1 min e não ~5 |
  | `Outbox:MaxAttempts` | 5 | **1500** | Com teto de 60s, ≈ 25h de rede — cobre a janela de 24h com folga |
  | Limite de validação de `MaxAttempts` | 50 | **10 000** | Acompanha |
  | `Provisioning:MaxPendingDuration` | — | **24h** | D3 |

- **Custo:** uma mensagem envenenada de outro tipo — não há nenhuma hoje — repetiria por ~25h, uma vez por minuto,
  com log a cada tentativa. Quando o broker chegar, o Outbox volta a só entregar ao broker, e estes números são
  revistos.

**O laço fica ocupado com o Keycloak lento.** Cada mensagem pode levar até o timeout total da resiliência (30s).
Com o Keycloak **parado**, a conexão é recusada e a falha é imediata; **pendurado**, o circuit breaker abre após
algumas falhas e as seguintes falham na hora. Com o circuito aberto, a mensagem conta tentativa e segue dentro da
janela.

**Não muda:** a reserva com `SKIP LOCKED`, as duas transações com a publicação no meio, a retenção, o
`OutboxWorker`.

### 4.5. O `OccurredOn` dos eventos volta errado da desserialização

Os eventos declaram `public DateTimeOffset OccurredOn { get; } = DateTimeOffset.UtcNow;`. O `System.Text.Json`
não atribui propriedade só com `get`, então o evento relido do Outbox **carrega o instante da desserialização**,
não o da ocorrência. Nada o lê hoje — a coluna `occurred_on` da tabela está correta, gravada antes da
serialização —, mas o primeiro consumidor que o lesse receberia um dado falso sem erro nenhum.

**Correção:** `{ get; init; } = DateTimeOffset.UtcNow`. O inicializador continua valendo na criação, e o
`System.Text.Json` passa a restaurar o valor original.

### 4.6. `GET /api/v1/tenants/{tenantId}/provisioning`

- **Autorização:** `PlatformAdmin`, como o catálogo da §8 define.
- **Rota:** `{tenantId:guid}` — id malformado nem chega ao handler (404).
- **Application:** `GetTenantProvisioningQuery(TenantId)`; o `TransactionBehavior` já deixa consultas sem
  transação, e a leitura é sem rastreamento — três campos não pedem o agregado montado.
- **200:** `{ "tenantId": "…", "status": "…", "registeredAt": "…" }`, com `status` sendo o nome do
  `TenantStatus` — nesta fatia, só `Pending`, `Active` ou `ProvisioningFailed` são alcançáveis.
- **404:** Problem Details quando o tenant não existe.

**Fora, de propósito:**

- **`ExternalOrganizationId`** — detalhe interno do Keycloak que ninguém consome.
- **O motivo da falha** — guardar mensagem de exceção no banco arrisca expor detalhes internos pela API. O motivo
  fica no log `Error`, com `tenantId` e `correlationId`. Se houver demanda, entra depois como código fechado
  (`inconsistencia`, `janela-esgotada`), nunca como texto de exceção.
- **`303 See Other` quando `Active`** — o recurso `GET /tenants/{id}` ainda não existe; sempre `200` com o status
  mantém o contrato simples até lá.

Com a rota, o `Location` do `202` deixa de apontar para o vazio — fecha o custo 5 que o PR #1 declarou.

### 4.7. `Tenant.RegisteredAt`

- `Tenant.Register(name, slug, plan, registeredAt)`; o `RegisterTenantHandler` passa `IDateTimeProvider.UtcNow`.
  O domínio continua sem relógio próprio.
- Coluna `registered_at timestamptz not null`. A migration preenche linhas existentes com `now()` — só há dados de
  desenvolvimento, e a alternativa (coluna anulável) espalharia um `?` pelo domínio para sempre por causa de um
  estado que nunca existirá em produção.

## 5. Testes

### 5.1. Por nível

| Nível | O que cobre |
|---|---|
| Domain | `Register` preenche `RegisteredAt` com o instante recebido |
| Application (fakes à mão, relógio falso) | Os sete caminhos do handler: (1) tenant inexistente; (2) fora de `Pending`, **sem chamar o provedor**; (3) sucesso → `Active` com o id; (4) inconsistência → `Failed` **antes** da janela; (5) transitória dentro da janela → exceção sobe, tenant segue `Pending`; (6) transitória depois da janela → `Failed`; (7) `ct` cancelado depois da janela → exceção sobe, não vira `Failed`. Mais a query: encontrado e inexistente |
| Integração (PostgreSQL + Keycloak reais) | **A demo do M1 como teste**, com a mesma composição de DI da aplicação: `DelegatingHandler` injeta falha no primeiro ciclo do `OutboxProcessor` → tenant `Pending`, mensagem com tentativa 1; tira-se a falha e roda-se o ciclo seguinte → `Active`, `ExternalOrganizationId` igual ao id da Organization no Keycloak, e um `tenant-activated` no Outbox. **Mensagem repetida:** duas entregas → uma Organization, nenhum erro. **Isolamento de escopo:** ver §5.2. **Validação cruzada:** rede menor que a janela derruba a subida. **`OccurredOn`:** ida e volta de todos os eventos de `OutboxEventTypes.Registrados`. **Cobertura do mapa:** todo evento registrado tem ação no publisher |
| Funcional | `GET .../provisioning`: 200, 404, 401, 403; o `Location` do `POST` resolve para 200 |

### 5.2. Prova por mutação

Cada mutação é aplicada, confirmada vermelha, revertida e reconfirmada verde antes do commit. Verde só é aceito
registrado com o motivo, como na fatia A.

| Mutação | Precisa ficar vermelho |
|---|---|
| Despachar no escopo do processador, sem escopo novo | Teste de isolamento: com `IIdentityProvider` falso que devolve sucesso e um interceptor de `SaveChanges` que **lança uma vez**, o commit do handler falha depois de `MarkProvisioned`. Com escopo próprio, o tenant segue `Pending`; com o escopo compartilhado, o `SaveChanges` do registro do lote grava o tenant `Active` que o handler deixou rastreado |
| Filtro `ex is not OperationCanceledException` no lugar do `ct` | Timeout (`TaskCanceledException`) depois da janela vira `Failed` |
| Handler tratando `ProvisioningFailed` como `Pending` | Tenant `Failed` não é reprovisionado |
| `OccurredOn` de volta a `{ get; }` | Ida e volta do `OccurredOn` |
| Remover a validação cruzada Outbox × janela | Teste de subida |
| Tirar `TenantActivated` do mapa do publisher | Cobertura do mapa |
| Janela contada com `>` em vez de `>=` | Teste na fronteira exata da janela |

### 5.3. Verificação ao vivo

Antes do PR, o roteiro da demo roda de verdade sobre o `docker compose` e o resultado fica no handoff:
`docker compose stop keycloak` → `POST /tenants` → `202` → `GET` mostra `Pending` → `docker compose start keycloak`
→ em ~1 min o `GET` mostra `Active`.

O job `compose` da CI **não** ganha este roteiro: exigiria emitir token de platform-admin na CI, e o teste de
integração da §5.1 já cobre o caminho com a falha injetada.

## 6. Onde esta fatia cai no roadmap

Fecha a linha "B · Consumidor" do andamento do M1 (§16) e a primeira demonstração do README. O M1 continua com:
convite do admin inicial (fatia C, que encaixa um passo entre `EnsureOrganizationAsync` e `MarkProvisioned`),
RabbitMQ, reconciliação e retry manual, suspensão, encerramento e downgrade de plano.

## 7. Dívidas e questões abertas registradas

- **Saída de `ProvisioningFailed`.** Só por retry manual ou reconciliação, ambos fora desta fatia. Até lá, um
  tenant `Failed` é visível no `GET .../provisioning` e no log, e sair de lá exige intervenção no banco.
- **Motivo da falha não persistido** (§4.6).
- **Números do Outbox dimensionados para o provisionamento** (§4.4), a rever quando o broker chegar.
- **Escolha da biblioteca de mensageria** (§3), para a fatia do RabbitMQ.
- **Handler não conhece o convite.** A fatia C insere `EnsureInvitedUserAsync` entre a Organization e o
  `MarkProvisioned`, e precisa resolver as duas pendências da §9.1 (onde vive o `initialAdminEmail`; vaga ×
  `Active`).

## 8. Mudanças na especificação (v2.5)

Arquivo novo `docs/especificacao-arquitetural-v2.5.md`, seguindo a linha evolutiva, com a seção "O que mudou da
v2.4 para a v2.5":

| Onde | Mudança |
|---|---|
| ADR-006 | Nota: transporte no próprio processo, pelo Outbox, até a fatia do RabbitMQ |
| §6.2 | A saída de `ProvisioningFailed` é só por retry manual (via `Pending`) ou reconciliação; mensagem repetida não reprovisiona |
| §9.1 | Janela de provisionamento contada desde o registro; `ProvisioningFailed` por janela esgotada ou erro permanente |
| §11.5 | Errata: o código de referência deixa de assumir MassTransit; a decisão de desistir é do handler; o filtro de cancelamento olha o `ct`; o motivo, com o fato da licença (§3 desta spec) |
| §14 / §14.1 | O `OccurredOn` do evento precisa sobreviver à desserialização |
| §16 | Andamento: fatia B entregue |
| §19 | Limites: `Failed` sem saída automática até a reconciliação; números do Outbox provisórios |

## 9. Fora do escopo

RabbitMQ e a escolha da biblioteca de mensageria; convite do admin inicial (fatia C); retry manual e
reconciliação; `GET /tenants` e `GET /tenants/{id}`; motivo da falha persistido; qualquer mudança no adaptador
do Keycloak.

## 10. Entregáveis

- Código e testes descritos na §4 e na §5, com a tabela de mutações preenchida no handoff.
- Migration de `registered_at`.
- `docs/especificacao-arquitetural-v2.5.md`.
- README: seção da demonstração do provisionamento com `curl`.
- Handoff da fatia ao final, com o resultado da verificação ao vivo.
