# Handoff — consumidor do provisionamento entregue

> **Data:** 2026-09-26 · **Marco:** M1 (fatia B de 3) · **Status:** implementada, build e suíte completa verdes,
> verificação ao vivo confirmada, revisão da branch inteira aplicada, **PR #3 aberto** contra `main` em 2026-09-26
> (https://github.com/Joseleno/identity-gateway/pull/3).
> **Onde parou:** as 8 tarefas do plano e a onda de correção da revisão final estão commitadas e enviadas; falta
> acompanhar a CI do PR #3, revisar e mesclar.
>
> Sucede o [handoff da fundação Keycloak](2026-09-25-fundacao-keycloak-handoff.md). Referência normativa:
> [`especificacao-arquitetural-v2.5.md`](../../especificacao-arquitetural-v2.5.md).

---

## Estado do repositório

| O quê | Estado |
|---|---|
| Branch | `feat/consumidor-provisionamento`, 2 commits de planejamento (design `ef11a18`, plano `a24e7b7`) + **7 commits** das Tasks 1–7 sobre `main` (`3f85462`), mais os 2 commits de documentação da Task 8 (`fd30d02`, `3a8c980`) e a onda de correção final (revisão do branch completo, abaixo) |
| `main` | Não tocada — recebe o merge pelo PR #3 |
| Working tree | Limpa |
| Docker | Rodando; usado nas Tasks 2, 6 (Testcontainers: PostgreSQL e Keycloak reais), na Task 8 (compose verificado ao vivo, localmente) e na onda de correção final (novo teste de integração do `OutboxProcessor`) |
| Push / PR | **Feitos em 2026-09-26:** branch enviada e [PR #3](https://github.com/Joseleno/identity-gateway/pull/3) aberto contra `main`. CI do PR ainda não acompanhada no momento deste registro |

Três frentes de commits compõem a branch: **planejamento** (`docs`, 2 — design/plano da fatia B), **Tasks 1–7**
(7 — **5** `feat`, 1 `fix`, 1 `test`, e a marca `test:` da Task 6 para o E2E), a **Task 8** (**2** `docs`: `fd30d02`
especificação v2.5/demonstração/handoff, `3a8c980` passo de migration no README e marca de versão), e a **onda de
correção final** pós-revisão do branch completo (`c4ed858` `fix` + 1 `docs` que acrescenta este próprio commit,
abaixo).

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

`dotnet test` (solução inteira, Docker rodando): **332 total, 0 falhas, 0 skips** — os 331 da Task 8 mais
`OutboxProcessorTests.TaskCanceledSemCancelamentoDoLote_MarcaErroEEntregaAsDemais`, da onda de correção final.

| Projeto | Total | Falhas | Skips |
|---|---|---|---|
| `IdentityGateway.Domain.UnitTests` | 113 | 0 | 0 |
| `IdentityGateway.Application.UnitTests` | 49 | 0 | 0 |
| `IdentityGateway.ArchitectureTests` | 29 | 0 | 0 |
| `IdentityGateway.Infrastructure.IntegrationTests` | 115 | 0 | 0 |
| `IdentityGateway.Api.FunctionalTests` | 26 | 0 | 0 |
| **Total** | **332** | **0** | **0** |

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
| Onda de correção final | Filtro por tipo de exceção (`excecao is not OperationCanceledException`) no catch por mensagem do `OutboxProcessor` (sem olhar o `cancellationToken`) | Vermelho (`TaskCanceledSemCancelamentoDoLote_MarcaErroEEntregaAsDemais` — a `TaskCanceledException` escapava do `foreach` sem passar por `RegistrarResultadoAsync`) |

**Nenhum defeito de produção apareceu na Task 6** — primeiro teste a exercitar `OutboxProcessor.ProcessarLoteAsync`
de ponta a ponta contra banco e Keycloak reais, e ele se comportou conforme a documentação nos três cenários
(reserva com `SKIP LOCKED` e backoff, isolamento de escopo do publisher, idempotência `EnsureOrganizationAsync`).

## Onda de correção final (revisão do branch completo)

Revisão do branch inteiro (`3f85462..3a8c980`) contra a spec e as convenções do repositório, com uma onda única
de correção sobre os achados.

**Achado importante, corrigido por TDD.** `OutboxProcessor.ProcessarLoteAsync` filtrava o catch por mensagem por
**tipo** de exceção (`excecao is not OperationCanceledException`) — o mesmo padrão que a própria fatia B havia
rejeitado no `ProvisionTenantHandler`, em favor de filtrar pelo `CancellationToken`. Um `TaskCanceledException`
que não fosse desligamento do host (por exemplo, o timeout cru do `HttpClient.Timeout`, sem relação com o
cancelamento do lote) escapava do `foreach` e abortava o `RegistrarResultadoAsync` do lote inteiro: mensagens já
entregues ficariam sem `ProcessedOn` (redelivery inofensiva), e o resto do lote já teria a tentativa contabilizada
na reserva sem nunca ter sido tentado — se fosse a tentativa 1500, o tenant ficaria `Pending` para sempre.
Corrigido para `excecao is not OperationCanceledException || !cancellationToken.IsCancellationRequested`, que
deixa escapar só o desligamento real do host — o mesmo filtro do `OutboxWorker`.

**Premissa verificada antes de tocar em comentário ou spec.** A revisão levantou a hipótese de que o timeout do
`AddStandardResilienceHandler` (Polly v8) chega como `TimeoutRejectedException`, e que `TaskCanceledException` vem
do `HttpClient.Timeout` cru (ex.: o cliente do token endpoint, sem resiliência). Confirmado por reflexão sobre
`Polly.Core 8.4.2`: `Polly.Timeout.TimeoutRejectedException` deriva de `Polly.ExecutionRejectedException` →
`System.Exception` — **não** de `OperationCanceledException`. E `KeycloakServiceCollectionExtensions` registra
`admin.AddStandardResilienceHandler()` no cliente da Admin API (o que `EnsureOrganizationAsync` usa). Ou seja, a
premissa da revisão era exatamente o contrário do que os comentários antigos afirmavam ("o timeout da resiliência
chega como `TaskCanceledException`"). Corrigidos: o comentário do `ProvisionTenantHandler`, o comentário do teste
`TimeoutDaResilienciaDepoisDaJanela_MarcaFailed` (sem renomear o teste). A v2.5 §11.5/§0 (B3) já não continha essa
premissa — a tabela de classes de erro (§11.5) já listava `TimeoutRejectedException` corretamente; nenhuma
alteração foi necessária ali.

**Demais achados (minor), aplicados na mesma onda:** comentário desatualizado do `Location` em `TenantsModule`
(§2 dos achados); atribuição do despacho corrigida na ADR-006 (`DispatchingOutboxPublisher`, chamado pelo
`OutboxProcessor` — não o `OutboxWorker` diretamente); snippet da §11.5 alinhado à regra "evento fora do mapa
lança" (§4.2); uma frase do design da fatia B e o texto desta seção corrigidos sobre o `correlationId` do log de
motivo (o despacho em background tem `correlationId` próprio por escopo, não o do `POST`; `tenantId` já basta
para correlacionar); caminho de upgrade das mensagens `tenant-registered` órfãs documentado acima, com o SQL de
rearme; limite novo na §19 sobre processamento concorrente da mesma mensagem por réplicas diferentes sob Keycloak
lento.

Build: `dotnet build IdentityGateway.slnx` — 0 avisos, 0 erros. Suíte completa ao final desta onda, abaixo.

## Verificação ao vivo (Step 4)

`docker compose up -d --build`: os seis serviços de dependência ficaram `healthy`/`started`, e `api` subiu
`(healthy)`. `curl -s http://localhost:8080/health/ready` respondeu `Healthy`.

**Achado durante a verificação, corrigido no fix round desta task:** com volumes novos, `docker compose up` não
aplica as migrations — `StartupTasks` só migra sob a flag `--migrate`, deliberadamente (comentário do próprio
código: migrar automaticamente a cada arranque é perigoso com várias réplicas subindo ao mesmo tempo). O
primeiro `POST /tenants` falhou com `500` (`relation "tenants" does not exist`). Contornado, na primeira
passada, com `docker compose run --rm api --migrate` antes de repetir o roteiro; não era o cenário "token
recusado" que o brief original previa, então o passo ficou registrado sem tocar em README ou API. No fix
round, o mesmo comando entrou no README ("Rodando local", logo depois de `docker compose up -d`), então o
roteiro abaixo agora é reproduzível por qualquer clone novo sem esse tropeço — ver "Pendências menores" para o
que ainda falta (automatizar via compose, se algum dia se quiser).

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
| Task 8: `docker compose up -d --build` com volumes novos não aplica migrations (por desenho — `StartupTasks` exige `--migrate`); rodei `docker compose run --rm api --migrate` uma vez para destravar a demonstração. No fix round, documentei o mesmo comando no README ("Rodando local") em vez de mexer em compose/`Dockerfile`/API | Sem o passo documentado, o primeiro `curl` do M0 falharia com `500` em qualquer clone novo |

## Pendências menores

**Cobertura de teste ausente** (herdadas dos relatórios de task, não resolvidas nesta fatia)
- Cliente do token: ramo de sucesso sem `access_token` no corpo, e corpo de erro `null` (Task 3 da fatia A).
- Cache do token: cancelamento em fila, fronteira dos 30s, corrida em `Invalidar`, `Dispose` com busca em voo (Task 4 da fatia A).
- `IProvisioningPolicy`/`ProvisioningPolicy` sem teste com valor não padrão (ex.: 48h); `ThrowIfNull` de `AtrasoSemVariacao` chamado 1499× por `CoberturaMinima`; exemplo do `remarks` de `OutboxOptions` desconectado do padrão 1500 (Task 3).
- `ThrowIfNull(command)` no `ProvisionTenantHandler` sem teste (padrão do repo); log `{Horas}` como `double` (Task 4).
- Logs `2100`/`2101` do `DispatchingOutboxPublisher` não levam `tenantId`; conferir que `Error.Message` do `Result.IsFailure` nunca carrega PII antes de logar (Task 5).
- Os 3 testes E2E da Task 6 dividem a tabela `outbox_messages` — cabem no `BatchSize=20` hoje, mas a folga é implícita; `LiberarAsync` usa SQL cru acoplado a nomes de coluna do Outbox (helper de teste).

**Caminho de upgrade não documentado (achado da revisão final)**
- Antes desta fatia, o `LoggingOutboxPublisher` marcava **todo** `tenant-registered` como processado, sem
  provisionar nada. Tenants que ficaram `Pending` em ambientes de dev anteriores a esta fatia têm a mensagem já
  com `processed_on` preenchido — nunca serão reprocessados pelo `OutboxProcessor` (que só lê `processed_on IS
  NULL`), então nunca virão a `Active` nem a `ProvisioningFailed`. Para rearmar essas mensagens, se desejado:
  ```sql
  UPDATE outbox_messages om
     SET processed_on = NULL, attempts = 0, next_attempt_on = now(), error = NULL
   WHERE om.type = 'tenant-registered'
     AND EXISTS (
           SELECT 1
             FROM tenants t
            WHERE t.id = (om.content ->> 'tenantId')::uuid
              AND t.status = 'Pending'
         );
  ```
  Nomes de coluna conferidos em `OutboxMessageConfiguration`/`TenantConfiguration`. Sem consumidor real em
  produção ainda (fatia B só tem transporte em processo), o impacto prático hoje é zero — registrado para quando
  houver ambiente com dados reais para migrar.

**Achado desta task (corrigido parcialmente no fix round)**
- `docker compose up -d` **não aplica migrations automaticamente** em volumes novos — é desenho deliberado
  (`StartupTasks`, flag `--migrate`). O README agora documenta o passo manual (`docker compose run --rm api
  --migrate`, logo depois de `docker compose up -d` em "Rodando local", com a explicação do porquê), então o
  primeiro `curl` de um clone novo volta a funcionar seguindo o README à letra. **Ainda em aberto:** automatizar
  esse passo — por exemplo, um serviço `migrate` one-shot no `docker-compose.yml`, no padrão dos outros
  one-shots do arquivo (`gateway-keys`, `keycloak-db`) — para que a promessa do M0 ("`git clone` + `docker
  compose up` + primeiro `curl` funcionam na primeira tentativa", spec §16) valha sem um passo manual extra.
  Decisão de infraestrutura fora do escopo desta fatia, deixada para quem tocar o compose de novo.

## Próximo passo

**Fatia C — o convite do admin inicial.** Começa por brainstorming, como as fatias A e B começaram, com as duas
pendências que a §9.1 da spec mantém em aberto: (1) onde o `initialAdminEmail` vive entre o `POST` e o convite —
hoje é validado e descartado, e pô-lo no evento o levaria ao Outbox contra a regra de dados pessoais só no
Keycloak; (2) a tensão entre `ReserveSeat` exigir o tenant `Active` e o fluxo de nascimento convidar o admin
**antes** de ativar.

## Como retomar

1. Conferir a CI do [PR #3](https://github.com/Joseleno/identity-gateway/pull/3) (`gh pr checks 3`) — os jobs
   `Build`, `Testes`, `Imagem Docker` e `Compose`. Se algo falhar, corrigir na mesma branch.
2. Revisar e mesclar o PR #3. Depois do merge: `git checkout main && git pull --ff-only`, apagar a branch local
   `feat/consumidor-provisionamento` e a remota.
3. Decidir o que fazer com o achado da migration ausente no compose (pendências menores, acima) — serviço
   `migrate` one-shot ou manter o passo manual documentado no README.
4. Abrir o brainstorming da fatia C.

Docker Desktop costuma estar desligado ao abrir a sessão: sem ele, os testes de integração e funcionais falham
com `DockerUnavailableException` (ambiente, não regressão).
