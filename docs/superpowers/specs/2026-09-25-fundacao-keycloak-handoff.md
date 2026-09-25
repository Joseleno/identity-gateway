# Handoff — fundação Keycloak entregue

> **Data:** 2026-09-25 · **Marco:** M0 (fatia A de 3) · **Status:** implementada, build e suíte completa verdes,
> **sem push** — fica para autorização do usuário.
> **Onde parou:** as 14 tarefas do plano estão commitadas em `feat/fundacao-keycloak`; falta só decidir push e PR.
>
> Sucede o [handoff da fundação planejada](2026-09-24-fundacao-keycloak-planejada-handoff.md). Referência normativa:
> [`especificacao-arquitetural-v2.4.md`](../../especificacao-arquitetural-v2.4.md).

---

## Estado do repositório

| O quê | Estado |
|---|---|
| Branch | `feat/fundacao-keycloak`, 3 commits de planejamento + **20 commits** das Tasks 1–13 sobre `main` (`64af7fc`), mais os commits de documentação desta própria Task 14 |
| `main` | Não tocada — só recebe o merge quando autorizado |
| Working tree | Limpa após cada commit desta task |
| Docker | Rodando; usado nas Tasks 7 (funcional), 9–11 (Keycloak real) e 12 (compose verificado ao vivo, localmente). O job de compose da Task 13 foi validado com `actionlint`, mas nunca rodou no GitHub — roda pela primeira vez nesta PR |
| Push / PR | **Não feitos.** Decisão do usuário (Step 6 do plano) |

Três frentes de commits compõem a branch: **planejamento** (`docs`, 3 — design/spec v2.4, plano, handoff planejado);
**Tasks 1–13** (20 — 9 `feat`, 1 `fix`, 5 `test`, 2 `ci` e 3 `docs`, estes últimos os comentários corrigidos nos fix
rounds das Tasks 2, 4 e 11); e a **Task 14**, que acrescenta só `docs`: o handoff e as correções de revisão sobre
ele.

## O que a fatia entregou

`IIdentityProvider.EnsureOrganizationAsync`, ponta a ponta contra um Keycloak 26.7.4 real — autenticação via
`private_key_jwt`, cache single-flight do token, resiliência e idempotência da criação de Organization.

| Camada | Entregue |
|---|---|
| Application | Porta `IIdentityProvider.EnsureOrganizationAsync`; `IdentityProviderInconsistencyException` |
| Infrastructure | `KeycloakAdminOptions` + `GatewaySigningKey` (options validadas na subida); `ClientAssertionFactory` (assertion `private_key_jwt`, sem `kid`, vida de 60s); `KeycloakTokenClient` (sem retry, assertion novo por tentativa); `ServiceAccountTokenCache` (single-flight, singleton); `ServiceAccountTokenHandler` (401 → uma repetição); `KeycloakAdminClient` + `KeycloakIdentityProvider` (busca por `q`, criação idempotente, adaptador transient); `KeycloakHealthCheck` (tag `ready`, obtém token pelo cache) |
| Api | `appsettings`/`appsettings.Development.json` com a configuração do Keycloak; `AllowInsecureHttp` só em Development |
| Realm e ambiente | `keycloak/bootstrap/realm-identity-gateway.json` (realm `identity-gateway`, client `identity-gateway`, `organizationsEnabled: true`); Keycloak no `docker-compose.yml` com chave e senha do admin geradas no serviço `gateway-keys`; job `compose` na CI exigindo `/health/ready` |
| Testes | `RegrasDoKeycloakTests`/`RegrasDoRealmTests` (arquitetura); suíte completa contra Keycloak real via Testcontainers (token, menor privilégio, `EnsureOrganizationAsync`, resiliência) |
| Documentos | Comentário falso de `RegisterTenantCommand.cs` corrigido; README com a seção Keycloak e "Rodar a API pela IDE"; este handoff |

## Suíte completa

`dotnet build IdentityGateway.slnx`: **0 avisos, 0 erros.**

`dotnet test` (solução inteira, Docker rodando): **280 total, 0 falhas, 0 skips.**

| Projeto | Total | Falhas | Skips |
|---|---|---|---|
| `IdentityGateway.Domain.UnitTests` | 109 | 0 | 0 |
| `IdentityGateway.Application.UnitTests` | 35 | 0 | 0 |
| `IdentityGateway.ArchitectureTests` | 28 | 0 | 0 |
| `IdentityGateway.Infrastructure.IntegrationTests` | 89 | 0 | 0 |
| `IdentityGateway.Api.FunctionalTests` | 19 | 0 | 0 |
| **Total** | **280** | **0** | **0** |

## Prova por mutação

Toda mutação foi aplicada, confirmada vermelha (ou registrada verde, quando esse era o resultado observado),
revertida byte-a-byte e reconfirmada verde antes do commit.

| Task | Mutação | Resultado |
|---|---|---|
| 1 | `Issuer`: `BaseUrl.TrimEnd('/')` → `BaseUrl` | Vermelho |
| 1 | `BaseUrlAceitavel`: `&& AllowInsecureHttp` → `\|\| true` | Vermelho |
| 2 | `RsaSecurityKey` com `KeyId` setado | Vermelho (`kid` presente) |
| 2 | Remover `Expires` do assertion | Vermelho (`exp` cai para o padrão de 60 min da lib) |
| 2 | `Claims["aud"]` junto de `Audience` (mutação do brief) | **Verde — mutante equivalente**: na 8.19.2 do `Microsoft.IdentityModel.JsonWebTokens`, `Audience` sempre vence sobre `Claims["aud"]`; comentário de produção corrigido para não afirmar "viram um array". Mutação alternativa (`aud` como array, sem `Audience`) provou a mesma invariante e pegou |
| 3 | Pendurar resiliência no cliente do token endpoint | Vermelho |
| 3 | Reaproveitar o mesmo assertion em duas tentativas | Vermelho |
| 4 | Remover a trava do single-flight | Vermelho (20 chamadas simultâneas viram 20 requisições) |
| 4 | Margem de 30s → `TimeSpan.Zero` | Vermelho (e um teste extra caiu de brinde, achado honesto reportado) |
| 4 | `Invalidar` descarta sempre, sem comparar o token | Vermelho |
| 5 | Remover `cache.Invalidar(token)` no 401 | Vermelho |
| 5 | Trocar o `if` de repetição única por `while` | Vermelho |
| 6 | `?q=` → `?searchQuery=` na busca por atributo | Vermelho |
| 6 | `Name: slug` → `Name: name` na criação da Organization | Vermelho |
| 6 | Registro `AddTransient` → `AddScoped` do adaptador | Vermelho |
| 7 | `failureStatus: Unhealthy` → `Degraded` | Vermelho (nos dois testes, confirmando que os dois caminhos passam pelo mesmo `FailureStatus`) |
| 7 | Remover o timeout de 5s do registro do health check | Vermelho (checagem estourou para ~100s) |
| 8 | `"secret": "x"` literal no client do realm | Vermelho |
| 8 | Placeholder impuro (`${VAR:default}`) | Vermelho (com colateral esperado no teste de organização) |
| 8 | Bloco PEM literal sob chave arbitrária (fix round) | Vermelho |
| 9 | `ClientAssertionFactory` assina com `KeyId` setado | Vermelho (`invalid_client`) |
| 9 | Realm: `organizationsEnabled: true` → `false` | Vermelho (404) |
| 9 | Realm: acrescenta `view-users` ao service account | Vermelho (dois testes: privilégio mínimo e a regra da Task 8) |
| 9 | Remover `CryptoProviderFactory` próprio das `SigningCredentials` (fix round, defeito real) | Vermelho (`ObjectDisposedException`) |
| 10 | `?q=` → `?searchQuery=` (repetida contra o Keycloak real) | Vermelho — mas na 1ª versão do teste dependia da ordem de execução (3 outros testes caíam, não o nomeado); corrigido para vermelho isolado e determinístico no fix round |
| 10 | `catch (KeycloakConflictException)` sem reconsultar | Vermelho |
| 10 | Remover a consulta inicial (ir direto ao `POST`) | **Verde — observação prevista pelo brief**: o 409 da segunda chamada mais a reconsulta pós-conflito já resolvem a idempotência sozinhos |
| 10 | `Description: name` → `Description: null` | Vermelho |
| 11 | Remover `DisableForUnsafeHttpMethods()` | Vermelho |
| 11 | Inverter a ordem dos handlers (token antes da resiliência) | **Verde — registrado, não corrigido**: só a revisão de código protege essa ordem hoje |
| 11 | `JsonContent.Create` em vez de `StringContent` no corpo do `POST` | **Verde — registrado**: nesta versão do runtime `JsonContent` também reenvia corretamente pós-401; comentário de produção reescrito para não prometer o contrário |

**Um defeito de produção real apareceu fora da lista de mutações do brief** (Task 9): o cache estático de
`SignatureProvider` do `Microsoft.IdentityModel` (`CryptoProviderFactory.Default`) retinha o `RSA` de uma chave já
descartada pelo DI entre `ServiceProvider`s de teste, e uma nova assinatura explodia com `ObjectDisposedException`.
O comentário do código atribuía o problema à ausência de `kid` — não era. Corrigido em produção:
`ClientAssertionFactory` passou a usar um `CryptoProviderFactory` próprio, com `CacheSignatureProviders = false`,
nas `SigningCredentials` — troca de host ou rotação de chave também expõe o mesmo defeito, então a correção é de
produção, não só de teste.

## O que ficou fora (spec §8)

Consumidor e transporte (fatia B); convite e `EnsureInvitedUserAsync` (fatia C); lado de tokens do realm e a API
validando tokens do Keycloak (fatia própria); RabbitMQ; reconciliação; as demais operações da `IIdentityProvider`.

## Errata do handoff de 2026-09-22

O handoff da vertical afirmava que o `initialAdminEmail` era "validado e carregado até o evento". Não era: o
handler chamava `Tenant.Register(name, slug, plan)` sem ele, e `TenantRegistered` só tem `TenantId` e `Slug`. O campo
é validado e descartado. O comentário de `RegisterTenantCommand.cs` que repetia a afirmação foi corrigido nesta
fatia; onde ele deve viver é decisão pendente da fatia C (spec v2.4, §9.1).

## Decisões tomadas durante a execução

| Ruling | Custo se errado |
|---|---|
| Pré-flight P1: se `ContarPorAliasAsync` com `q=alias:` não achar uma Organization existente, trocar por `search={alias}&exact=true` | Helper de teste diferente do plano; nenhum código de produção afetado. **Não precisou disparar** — `q=alias:` funcionou no 26.7.4 |
| Pré-flight P2: Docker desligado no início não para a execução; só liga ao chegar nas tasks que precisam dele | Nenhum, efeito só na máquina local |
| Task 2: aceitar a mutação alternativa (`aud` como array) no lugar da mutação do brief (equivalente na 8.19.2) | Nenhum no código de produção — só a prova ficou diferente do texto do plano |
| Task 2: corrigir o comentário sobre `Claims["aud"]` para refletir que `Audience` sempre vence | Só texto |
| Task 4: manter o desenho da spec (falha não cria cache, cancelamento não propaga entre esperantes) em vez da opção com `Task` compartilhada | Sob queda longa do Keycloak, chamadas enfileiradas esperam o próprio timeout total (30s) antes de falhar |
| Task 8: acrescentar teste que recusa qualquer bloco com armadura PEM (`-----BEGIN`) | Um teste a mais; falso positivo improvável |
| Task 9: corrigir em produção com `CryptoProviderFactory` próprio no lugar de só ajustar a fixture de teste | Uma assinatura sem cache de `SignatureProvider` por renovação de token — custo baixo |
| Task 11: reescrever os comentários sobre `StringContent`/reenvio pós-401 para não prometer o que a mutação 3 desmentiu | Só texto |
| Task 13: `timeout-minutes: 20` no job de compose e `--max-time 10` nos `curl`, só nesse job novo | Job cai aos 20 min num runner muito lento |

## Pendências menores

**Cobertura de teste ausente**
- Cliente do token: ramo de sucesso sem `access_token` no corpo, e corpo de erro `null` produzindo `": "` em vez de sentinela (Task 3).
- Cache do token: cancelamento enquanto espera na fila; fronteira exata dos 30s (`<` vs `<=`); janela entre comparar e trocar em `Invalidar`; `Dispose` do semáforo com busca em voo lança `ObjectDisposedException` (Task 4).
- Admin API: sem teste de nulos/`enabled` omitidos no corpo, de `201` sem `Location`, nem de `POST 5xx`; contrato de exceção da porta só lista `HttpRequestException`, mas `TimeoutRejectedException`/`BrokenCircuitException` também chegam (Task 6).
- Realm: `ChavesProibidas` só casa nome de propriedade — valor em array sob chave não listada escapa; `"password"` genérico pode dar falso positivo (Task 8).
- Fixture do Keycloak real: `CriarClienteMasterAsync` vaza `HttpClient` se o token falhar; `LerOrganizacaoCruaAsync` não descarta o `JsonDocument`; `TemExatamenteManageOrganizations` não olha `/composite`; helpers do master recriam `HttpClient` a cada chamada; `chaveA` do teste de regressão sem `try/finally` (Task 9).
- `EnsureOrganizationAsync` real: corrida afirma só `Contain(Conflict)`, não exatamente um `Created` e um `Conflict`; `AntesDeEnviar` não recebe o `CancellationToken` da requisição; `ResponderSemEnviar` cria resposta sem `RequestMessage`; testes de inconsistência afirmam só o tipo da exceção, não um fragmento da mensagem (Task 10).
- Ordem dos handlers (resiliência por fora, token por dentro) sem teste automático — a mutação 2 da Task 11 ficou verde; só a revisão de código protege.

**Comentários e nomes desatualizados**
- `Directory.Packages.props`: comentário do `Http.Resilience` ainda diz "sem `PackageReference` hoje" (Task 1).
- Comentário do teste "5s de vida" quer dizer 5s restantes; o skew de relógio também justifica a margem, e o
  comentário não diz isso (Task 4).
- Nomes de teste `Cenario_Resultado`, sem prefixo de método, conforme o próprio plano pediu (Task 5).
- Comentários que citam testes que só nasceriam nas Tasks 10/11; justificativa do `HttpClient.Timeout` "indistinguível" é mais forte do que o motivo real (retries sobrepondo o `TotalRequestTimeout`); `BuscaUsaQComBriefRepresentationFalse` fora do padrão `Metodo_Cenario_Resultado` (Task 6).
- `<remarks>` do `KeycloakHealthCheck` não diz que o caso "pendura" depende do timeout do `HealthCheckService`, não de um catch próprio (Task 7).

**Recursos não descartados em teste**
- `ClientAssertionFactoryTests` não descarta o `GatewaySigningKey` criado em `CriarFabrica()` (Task 2).

**CI e infraestrutura**
- DI test compara `GetType().Name == "OutboxWorker"` — um rename passaria vazio; teste de tempo com folga de 3s (8s vs. timeout de 5s) pode oscilar em CI (Task 7).
- Imagens do Seq/Jaeger fixadas por tag/digest não foram puxadas no teste ao vivo do compose (fora da cadeia do `api`), só conferidas no Docker Hub; a guarda `test -s` do entrypoint do Keycloak falha sem mensagem diagnóstica (Task 12).

## Próximo passo

**Fatia B — o consumidor do provisionamento.** Começa por brainstorming, como a fatia A começou: define o
transporte da mensagem do Outbox, o `ProvisionTenantHandler` que chama `EnsureOrganizationAsync`, `MarkProvisioned`
e o tratamento de falha permanente vs. transitória.

## Como retomar

1. Decidir push e PR de `feat/fundacao-keycloak` para `main` — não feito nesta sessão, por instrução explícita.
2. Depois do merge, abrir o brainstorming da fatia B.
