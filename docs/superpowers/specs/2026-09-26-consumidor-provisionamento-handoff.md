# Handoff — consumidor do provisionamento entregue

> **Data:** 2026-09-26 · **Marco:** M1 (fatia B de 3) · **Status:** implementada, build e suíte completa verdes,
> verificação ao vivo confirmada, **sem push** — fica para autorização do usuário.
> **Onde parou:** as 7 tarefas do plano estão commitadas em `feat/consumidor-provisionamento`; falta só decidir
> push e PR.
>
> Sucede o [handoff da fundação Keycloak](2026-09-25-fundacao-keycloak-handoff.md). Referência normativa:
> [`especificacao-arquitetural-v2.5.md`](../../especificacao-arquitetural-v2.5.md).

---

## Estado do repositório

| O quê | Estado |
|---|---|
| Branch | `feat/consumidor-provisionamento`, 2 commits de planejamento (design `ef11a18`, plano `a24e7b7`) + **7 commits** das Tasks 1–7 sobre `main` (`3f85462`), mais o commit de documentação desta Task 8 |
| `main` | Não tocada — só recebe o merge quando autorizado |
| Working tree | Limpa após o commit desta task |
| Docker | Rodando; usado nas Tasks 2, 6 (Testcontainers: PostgreSQL e Keycloak reais) e nesta Task 8 (compose verificado ao vivo, localmente) |
| Push / PR | **Não feitos.** Decisão do usuário (Step 7 do plano) |

Duas frentes de commits compõem a branch: **planejamento** (`docs`, 2 — design/plano da fatia B) e **Tasks 1–7**
(7 — 4 `feat`, 1 `fix`, 1 `test`, e a marca `test:` da Task 6 para o E2E), mais a **Task 8**, que acrescenta só
`docs`: a especificação v2.5, a demonstração no README e este handoff.

## O que a fatia entregou

`ProvisionTenantHandler` consome o evento do próprio Outbox (transporte em processo até a fatia do broker),
decide entre repetir a chamada ao Keycloak e desistir pela janela de provisionamento, e
`GET /api/v1/tenants/{tenantId}/provisioning` deixa o estado consultável.

| Camada | Entregue |
|---|---|
| Domain | `OccurredOn` com `init` em `TenantRegistered`/`TenantActivated` (sobrevive à desserialização do Outbox); `Tenant.RegisteredAt`; guarda de arquitetura que exige `init` em todo evento registrado |
| Application | `IProvisioningPolicy` (`MaxPendingDuration`); `ProvisionTenantCommand`/`ProvisionTenantHandler` (só age em `Pending`; erro permanente vai direto a `ProvisioningFailed`; erro transitório dentro da janela propaga e o Outbox repete; janela esgotada marca `ProvisioningFailed`); `ITenantQueries`/`TenantProvisioningView` (leitura sem rastreamento); `GetTenantProvisioningQuery`/`GetTenantProvisioningHandler` |
| Infrastructure | `OutboxBackoff` extraído da fórmula de atraso; `OutboxCobreAJanelaDeProvisionamento` (`IValidateOptions<OutboxOptions>`) recusa, na subida, configuração cuja rede não cubra a janela; novos padrões do Outbox (`MaxRetryDelaySeconds` 60, `MaxAttempts` 1500); `ProvisioningOptions`/`ProvisioningPolicy` (`Provisioning:MaxPendingHours`, padrão 24, faixa 1–720); `DispatchingOutboxPublisher` substitui `LoggingOutboxPublisher` — despacha cada evento como command do Mediator, num `AsyncServiceScope` próprio por mensagem, nunca recebendo `ISender` pelo construtor; `TenantQueries` (`AsNoTracking`) |
| Api | `GET /api/v1/tenants/{tenantId:guid}/provisioning`, policy `PlatformAdmin`, 404 com Problem Details, corpo `{ tenantId, status, registeredAt }` — nunca `ExternalOrganizationId` nem motivo de falha |
| Persistence | Migration `InstanteDeRegistroDoTenant` (`registered_at`, `timestamptz`, sem default após o backfill) |
| Testes | Suíte completa (domínio, application com NSubstitute, arquitetura, integração — incluindo o provisionamento ponta a ponta contra PostgreSQL e Keycloak reais —, funcional) |
| Documentos | Especificação v2.5 (§0 nova, ADR-006, §6.2, §9.1, §11.5, §14.1, §16, §19 atualizados); README com a demonstração do M1 por `curl`, verificada ao vivo; este handoff |

## Suíte completa

`dotnet build IdentityGateway.slnx`: **0 avisos, 0 erros.**

`dotnet test` (solução inteira, Docker rodando): **331 total, 0 falhas, 0 skips.**

| Projeto | Total | Falhas | Skips |
|---|---|---|---|
| `IdentityGateway.Domain.UnitTests` | 113 | 0 | 0 |
| `IdentityGateway.Application.UnitTests` | 49 | 0 | 0 |
| `IdentityGateway.ArchitectureTests` | 29 | 0 | 0 |
| `IdentityGateway.Infrastructure.IntegrationTests` | 114 | 0 | 0 |
| `IdentityGateway.Api.FunctionalTests` | 26 | 0 | 0 |
| **Total** | **331** | **0** | **0** |

## Prova por mutação

Toda mutação foi aplicada, confirmada vermelha, revertida byte-a-byte e reconfirmada verde antes do commit.

| Task | Mutação | Resultado |
|---|---|---|
| 1 | `TenantActivated.OccurredOn`: `{ get; init; }` → `{ get; }` | Vermelho (`OccurredOn_VoltaComOValorGravado[TenantActivated]` e a regra de arquitetura `TodoEventoRegistrado_TemOccurredOnRestauravel`) |
| 3 | Comentada a linha que registra `OutboxCobreAJanelaDeProvisionamento` como `IValidateOptions<OutboxOptions>` (sem validação cruzada) | Vermelho (`OutboxComOsNumerosAntigos_FalhaAoValidarNomeandoTentativasEJanela` — exceção esperada não foi lançada) |
| 4 | Filtro por tipo de exceção (`excecao is not OperationCanceledException`) no lugar de `!cancellationToken.IsCancellationRequested` | Vermelho (`TimeoutDaResilienciaDepoisDaJanela_MarcaFailed`) |
| 4 | Tratar `ProvisioningFailed` como elegível para reprovisionar (`tenant.Status is not (Pending or ProvisioningFailed)`) | Vermelho (`TenantEmProvisioningFailed_NaoEReprovisionado` — status virou `Active` em vez de `ProvisioningFailed`) |
| 4 | `>` no lugar de `>=` na fronteira da janela | Vermelho (`FalhaNaFronteiraExataDaJanela_MarcaFailed`) |
| 5 | Removida a entrada `[typeof(TenantActivated)] = _ => null` do mapa `Destinos` (sem `TenantActivated` no mapa) | Vermelho (`TodoEventoDoOutbox_TemDestinoNoPublisher` e `TenantActivated_EEntregueSemResolverNada`) |
| 6 | `DispatchingOutboxPublisher` recebendo `ISender` pelo construtor (do escopo do processador) em vez de abrir um `AsyncServiceScope` novo por mensagem | Vermelho (`CommitPerdidoDepoisDeCriarAOrganizacao_ProximoCicloReencontraSemDuplicar` — status ficou `Active` em vez de `Pending`; o `SaveChanges` do lote persistiu o que o handler do escopo compartilhado havia deixado rastreado) |

**Nenhum defeito de produção apareceu na Task 6** — primeiro teste a exercitar `OutboxProcessor.ProcessarLoteAsync`
de ponta a ponta contra banco e Keycloak reais, e ele se comportou conforme a documentação nos três cenários
(reserva com `SKIP LOCKED` e backoff, isolamento de escopo do publisher, idempotência `EnsureOrganizationAsync`).

## Verificação ao vivo (Step 4)

`docker compose up -d --build`: os seis serviços de dependência ficaram `healthy`/`started`, e `api` subiu
`(healthy)`. `curl -s http://localhost:8080/health/ready` respondeu `Healthy`.

**Achado durante a verificação, corrigido só para rodar a demonstração (não editei compose nem API):** com
volumes novos, `docker compose up` não aplica as migrations — `StartupTasks` só migra sob a flag `--migrate`,
deliberadamente (comentário do próprio código: migrar automaticamente a cada arranque é perigoso com várias
réplicas subindo ao mesmo tempo). O primeiro `POST /tenants` falhou com `500` (`relation "tenants" does not
exist`). Contornado com `docker compose run --rm api --migrate` antes de repetir o roteiro — não é o cenário
"token recusado" que o brief previa, então registrei em vez de alterar README ou API; ver "Pendências menores".

Roteiro do README, executado exatamente, com um tenant novo (o primeiro `POST`, feito antes de o Keycloak ser
parado, não conta — foi refeito na ordem certa):

| Passo | Horário | Resultado |
|---|---|---|
| `docker compose stop keycloak` | 12:12:32 | — |
| `POST /api/v1/tenants` | 12:12:39 | `202 Accepted`, `Location: /api/v1/tenants/01a0de46-8cb2-780b-8d50-10fd9c13036d/provisioning` |
| `GET .../provisioning` (1º) | 12:13:01 | `{"tenantId":"01a0de46-8cb2-780b-8d50-10fd9c13036d","status":"Pending","registeredAt":"2026-09-26T15:12:40.626285+00:00"}` |
| `docker compose start keycloak` | 12:13:05 | — |
| `GET .../provisioning` (1º `Active`) | 12:13:14 | `{"tenantId":"01a0de46-8cb2-780b-8d50-10fd9c13036d","status":"Active","registeredAt":"2026-09-26T15:12:40.626285+00:00"}` |

Log da API confirma a sequência: `EventId 2002` (`FalhaAoDespachar`, tentativa 1 de 1500, `Name or service not
known (keycloak:8080)`) às 15:13:06 UTC, seguido de `EventId 1102` (`Provisionado`) às 15:13:14.431 UTC — o
handler repetiu a chamada dentro da janela e teve sucesso assim que o Keycloak voltou a responder ao DNS interno
do compose, bem dentro do teto de 60s do backoff.

`docker compose down` executado ao final, sem `-v` — volumes preservados.

## Decisões tomadas durante a execução

| Ruling | Custo se errado |
|---|---|
| Task 2: referência temporária a `Microsoft.EntityFrameworkCore.Design` no `.csproj` da Api, revertida antes do commit, só para destravar `dotnet ef migrations add` | Nenhum no código commitado |
| Task 2: migration de conferência (`Up`/`Down` vazios) removida apagando os arquivos manualmente, porque `dotnet ef migrations remove` exige conexão de design-time com um Postgres residente que este ambiente não tem | Nenhum — nada havia sido aplicado a nenhum banco |
| Task 3: o snippet do plano para a mensagem de erro (`string.Create` com interpolações concatenadas por `+`) não compila (`CS1620`); reescrito como duas chamadas `string.Create` separadas, concatenadas depois como strings já materializadas — mesmo conteúdo e cultura | Nenhum comportamental, só forma da mensagem |
| Tasks 4/5: ajustes pedidos pelos analisadores do repositório sob `TreatWarningsAsErrors` — `TestContext.Current.CancellationToken` no lugar de `default` (`xUnit1051`), `var` em vez de tipo explícito (`IDE0007`), e `#pragma warning disable/restore CA2012` escopado às duas linhas de `Send(...).Returns(ValueTask...)` do NSubstitute (falso positivo documentado) | Nenhum comportamental |
| Task 6: nenhum defeito de produção encontrado no `OutboxProcessor` — primeiro teste a exercitá-lo de ponta a ponta contra banco e Keycloak reais | — |
| Task 8: `docker compose up -d --build` com volumes novos não aplica migrations (por desenho — `StartupTasks` exige `--migrate`); rodei `docker compose run --rm api --migrate` uma vez para destravar a demonstração, sem editar compose, `Dockerfile` ou README | Sem o passo, o primeiro `curl` do M0 falha com `500` em qualquer clone novo — ver pendências |

## Pendências menores

**Cobertura de teste ausente** (herdadas dos relatórios de task, não resolvidas nesta fatia)
- Cliente do token: ramo de sucesso sem `access_token` no corpo, e corpo de erro `null` (Task 3 da fatia A).
- Cache do token: cancelamento em fila, fronteira dos 30s, corrida em `Invalidar`, `Dispose` com busca em voo (Task 4 da fatia A).
- `IProvisioningPolicy`/`ProvisioningPolicy` sem teste com valor não padrão (ex.: 48h); `ThrowIfNull` de `AtrasoSemVariacao` chamado 1499× por `CoberturaMinima`; exemplo do `remarks` de `OutboxOptions` desconectado do padrão 1500 (Task 3).
- `ThrowIfNull(command)` no `ProvisionTenantHandler` sem teste (padrão do repo); log `{Horas}` como `double` (Task 4).
- Logs `2100`/`2101` do `DispatchingOutboxPublisher` não levam `tenantId`; conferir que `Error.Message` do `Result.IsFailure` nunca carrega PII antes de logar (Task 5).
- Os 3 testes E2E da Task 6 dividem a tabela `outbox_messages` — cabem no `BatchSize=20` hoje, mas a folga é implícita; `LiberarAsync` usa SQL cru acoplado a nomes de coluna do Outbox (helper de teste).

**Achado desta task**
- `docker compose up -d --build` **não aplica migrations automaticamente** em volumes novos — é desenho deliberado (`StartupTasks`, flag `--migrate`), mas o README não documenta o passo, e a promessa do M0 ("`git clone` + `docker compose up` + primeiro `curl` funcionam na primeira tentativa", spec §16) está quebrada para quem clona e sobe pela primeira vez. Não corrigi README nem compose porque o brief desta task só autorizava ajustar o snippet em caso de token recusado — registrando para decisão explícita: ou um serviço `migrate` one-shot no compose (padrão dos outros one-shots do arquivo), ou uma linha no README antes da demonstração.

## Próximo passo

**Fatia C — o convite do admin inicial.** Começa por brainstorming, como as fatias A e B começaram, com as duas
pendências que a §9.1 da spec mantém em aberto: (1) onde o `initialAdminEmail` vive entre o `POST` e o convite —
hoje é validado e descartado, e pô-lo no evento o levaria ao Outbox contra a regra de dados pessoais só no
Keycloak; (2) a tensão entre `ReserveSeat` exigir o tenant `Active` e o fluxo de nascimento convidar o admin
**antes** de ativar.

## Como retomar

1. Decidir push e PR de `feat/consumidor-provisionamento` para `main` — não feito nesta sessão, por instrução
   explícita.
2. Decidir o que fazer com o achado da migration ausente no compose (pendências menores, acima) antes ou depois
   do merge.
3. Depois do merge, abrir o brainstorming da fatia C.
