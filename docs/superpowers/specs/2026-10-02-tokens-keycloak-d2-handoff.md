# Handoff — tokens do Keycloak, parte D2 entregue

> **Data:** 2026-10-02 (atualizado às 22:35, horário local) · **Marco:** M0 + M1 (fatia D, segunda parte) ·
> **Status:** implementada, build e suíte completa verdes, revisão final da branch feita ("pronto para o merge", sem
> achado crítico nem importante), **enviada e aberta como [PR #7](https://github.com/Joseleno/identity-gateway/pull/7)**.
> **A jornada nova no compose nunca rodou, e quatro provas por mutação não foram executadas**: o sistema de permissões
> do ambiente negou a edição temporária da mutação 2 da Tarefa 16 e o `docker compose -p igverif up`, e as outras três
> (a mutação 3 da Tarefa 16 e as mutações 1 e 2 da Tarefa 11, pendentes desde a D1) dependem do compose. O autor vai
> rodá-las (ver "Pendências"). A primeira execução da jornada nova é o job `Compose` da CI do PR #7.
> **Onde parou:** as Tarefas 13 a 17 e esta atualização estão na branch `feat/leitura-do-tenant`, enviada; falta rodar
> as provas pendentes, ler a CI do PR #7 e mesclar.
>
> Sucede o [handoff da D1](2026-10-02-tokens-keycloak-d1-handoff.md). Design:
> [`2026-09-30-tokens-keycloak-design.md`](2026-09-30-tokens-keycloak-design.md). Plano:
> [`2026-09-30-tokens-keycloak.md`](../plans/2026-09-30-tokens-keycloak.md). Referência normativa:
> [`especificacao-arquitetural-v2.7.md`](../../especificacao-arquitetural-v2.7.md).

---

## Estado do repositório

| O quê | Estado |
|---|---|
| Branch | `feat/leitura-do-tenant`, sobre `main` (`cb8b568`, o merge do PR #6): `cd46313`, `30167f9`, `28fac2a` e `4ac79c1` (Tarefas 13 a 16), `bea8893` (Tarefa 17) e o `docs` desta atualização |
| `main` | Não tocada — recebe o merge pelo PR |
| Working tree | Limpa depois do commit desta tarefa |
| Realm, compose e one-shot | **Não tocados.** `git diff --stat main -- keycloak docker-compose.yml` vazio: quem já subiu o compose depois da D1 não precisa de `down -v` |
| Docker | Na D2, só os Testcontainers da suíte, além da derrubada do `igverif` que a D1 deixara de pé (`down -v`, com autorização do autor, antes da Tarefa 16). Conferido só por leitura em 2026-10-02, 22:07 (horário local, UTC−3): nenhum contêiner nem volume `igverif`; os três volumes do autor (`identitygateway_gateway-keys`, `identitygateway_postgres-data`, `identitygateway_seq-data`) intactos; nenhum arquivo de estado `ig-jornada-estado.json` no diretório temporário |
| Push / PR | Autorizados pelo autor em 2026-10-03 (UTC). Branch enviada e [PR #7](https://github.com/Joseleno/identity-gateway/pull/7) aberto contra `main`; o corpo do PR diz o que não rodou. A CI do PR não foi lida nesta sessão |

Três `feat` (Tarefas 13 a 15), um `test` (Tarefa 16) e dois `docs` (a Tarefa 17 e esta atualização). Nenhum toca o
realm, o compose nem o one-shot. A mensagem do commit da Tarefa 14 foi corrigida por `--amend` antes de qualquer publicação
(`ecbf222` → `30167f9`, só a mensagem; o diff entre os dois é vazio): item 1 de "O que mudou em relação ao plano".

## O que a D2 entregou

A primeira rota de tenant. O admin convidado lê o próprio tenant com o token do Keycloak, e a autorização confere o
token **e** a pertença no banco (ADR-011). Todo outro caso é `403`, com o mesmo Problem Details — inclusive tenant
inexistente e o platform-admin, até existir a auditoria.

| Camada | Entregue |
|---|---|
| Api | `Authorization/RoleRequirement`, `NotPlatformAdminRequirement`, `SameTenantRequirement`, `MemberRequirement` e `MemberRequirementHandler`; `AutorizacaoDaGateway.AddAutorizacaoDaGateway` (policies, fallback, `InvokeHandlersAfterFailure` falso, handler da pertença registrado depois do `AddAuthorization`); `Policies.TenantAdmin` e `Policies.DeTenant`; `GET /api/v1/tenants/{tenantId}` no `TenantsModule`, com a falha do handler traduzida no mesmo `403` |
| Application | `IMemberQueries`; `GetTenantQuery`, `GetTenantHandler`, `TenantDetailsResponse`, `TenantPlanResponse`; `ITenantQueries.GetDetailsAsync` e `TenantDetailsView` |
| Infrastructure | `MemberQueries`; `TenantQueries.GetDetailsAsync` |
| Testes | Unitários da policy com um handler que aprova tudo; a pertença por estado, numa tabela escrita à mão; a ordem dos handlers, no contêiner de teste e na composição real; a suíte negativa de autorização por HTTP; o conjunto exato de chaves do `200`; o teste de subida das policies de tenant; as consultas contra o PostgreSQL; a volta inteira com Keycloak real e o ataque do grupo; as regras de arquitetura novas |
| CI e demonstração | A fase `jornada` do app com o admin convidado lendo o próprio tenant — **compilada, nunca executada**; o passo 6 da demonstração do README. No `ci.yml`, só o comentário do passo da jornada mudou |
| Documentos | As marcas "(D2, planejado)" fechadas na v2.7 e no documento de negócio; README; este handoff |

## O que mudou em relação ao plano

O plano foi escrito antes de o código existir. O que a execução encontrou:

1. **Ligar `InvokeHandlersAfterFailure` sozinho não é equivalente** (Tarefa 14, mutação 7). O plano previa que essa
   mutação e a do handler sem a guarda `HasFailed` ficariam verdes cada uma sozinha, e vermelhas juntas. Observado:
   a opção ligada, sozinha, deixa **21** testes vermelhos, todos de `TenantAdminPolicyTests`. Com ela ligada, o
   `MemberRequirementHandler` roda depois da falha, cai na guarda `HasFailed` e chama `Fail` com o próprio motivo;
   os casos de negação, que afirmam um motivo só (o do requirement do caso), ganham um segundo. A opção não estava
   fixada pela Tarefa 13 — no estado dela a mutação ficaria verde —: é o handler novo que a torna observável, e de
   forma incidental (uma guarda que saísse sem motivo devolveria a mutação ao verde). A prova que não depende disso é
   a combinada (mutação 9): vermelha pela contagem de consultas. O handler sem a guarda, sozinho, ficou verde. A
   mensagem do commit da Tarefa 14 repetia a frase do plano e foi corrigida por `--amend` (`ecbf222` → `30167f9`).
2. **Contagens de mutação diferentes do plano, com o comportamento certo.** Tarefa 14: mutação 1 com 8 vermelhos, e
   não 4 (os 4 a mais são de `TenantAdminPolicyTests`: com o handler da pertença antes, ele veta primeiro, e o motivo
   passa a ser o dele). Tarefa 15: mutação 4 com 18, e não 15; mutação 6 com 12, e não 8; mutação 7 com 10, e não 7
   (os 3 a mais são os casos acrescentados no item 3). A mutação 3 da Tarefa 15 deu 4 na primeira rodada e foi
   **refeita sobre o arquivo commitado**, depois da revisão: 5, porque o acréscimo do platform-admin no item 3 nunca
   tinha sido visto vermelho.
3. **Tarefa 15, três desvios nos testes, que fecharam buracos do plano** (todos em `LeituraDeTenantTests`):
   - o teste do `403` único comparava só os **nomes** dos cabeçalhos — um cabeçalho com o motivo da negação passava
     verde (mutação E1 da tabela abaixo). Passou a comparar nome e valor, fora o valor do `X-Correlation-Id`;
   - três formatos a mais do GUID da rota: entre chaves, entre parênteses e com sinal de mais num componente — este
     afirma também que o corpo traz o `tenantId` canônico, isto é, que a leitura é a do `Guid` vinculado, e não a do
     texto do segmento;
   - o platform-admin entre os motivos comparados no `403`.

   A classe ficou com 29 casos, e não 26; os funcionais da tarefa, com +37, e não +34.
4. **A base da integração era 241, e não 237** (Tarefa 15). A Tarefa 14 acrescentou os 4 de `MemberQueriesTests` e,
   por falta de memória livre na máquina, não rodou a suíte de integração inteira; a Tarefa 15 a rodou pela primeira
   vez desde a D1 (244, sem falha).
5. **Tarefa 14, uma mutação a mais:** o tenant trocado na chamada da porta da pertença deixa vermelhos os três casos
   de `APertenca_EConsultadaUmaVezComOTenantDaRotaEOSubDoToken`. A asserção que liga a consulta ao tenant da rota e
   ao `sub` do token nunca tinha sido vista vermelha.
6. **O caminho real passou de primeira.** Tarefa 15: `LeituraDeTenantTests` verde na primeira execução, com o
   JwtBearer da D1, a policy, o `MemberRequirementHandler`, o `MemberQueries` e o PostgreSQL, sem ajuste no código das
   Tarefas 13 e 14 nem da D1. Tarefa 16: a coleção com Keycloak real verde na primeira execução (2/2) — o `sub` e o
   `tenant_id` do token batem com `members.external_user_id` e com o id do tenant.
7. **Tarefa 16: o README e o app corrigidos onde o plano os deixava falsos.** O parágrafo "A mesma jornada, sem
   navegador" dizia que o app só "confere o convite do admin do tenant no mailpit"; passou a descrever o passo 6 feito
   pelo app. A etapa do app anterior às novas trocou de nome ("… com o link no endereço público"), porque a checagem
   de que o link abre saiu, como o plano manda — a prova passou à etapa seguinte, que conclui o convite.
8. **Tarefa 16: a jornada no compose e duas mutações não rodaram.** O sistema de permissões do ambiente negou ao
   implementador, com o motivo "Security Weaken", a edição temporária da mutação 2 (tirar o `MemberRequirement` da
   policy), um `git diff --stat` só de leitura e o `docker compose -p igverif up`. Ninguém contornou. A mensagem do
   commit `4ac79c1` diz o que rodou e o que não rodou, em vez do parágrafo do plano, que afirmava a jornada local e as
   três mutações. Detalhe em "Verificação ao vivo" e em "Pendências".
9. **O vermelho de compilação de parte dos testes não foi observado isolado** (Tarefas 13 e 14): os arquivos de teste
   e de produção foram escritos juntos. A prova por asserção desses testes está nas mutações. Na Tarefa 15 os dois
   vermelhos de compilação foram vistos.
10. **Mutações rodadas por filtro de classe, e não na suíte funcional inteira** (Tarefa 15), pela memória livre da
    máquina: um conjunto de 120 testes de sete classes, verdes antes da primeira mutação. Classes fora dele não foram
    rodadas sob mutação.
11. **A v2.7 não ganhou errata nova** (Tarefa 17). O código das Tarefas 13 a 15 foi comparado com as §6.4, §8, §10.1,
    §11.7, §11.8, §11.9 e §13, e não há divergência de decisão. Há diferenças de redação nos blocos de referência da
    §11.7, que são referência e não cópia: a visibilidade dos tipos (`internal` no código), o texto dos motivos de
    falha, a guarda do `sub` (`string.IsNullOrWhiteSpace` no código, `Length > 0` na referência — a autenticação já
    garante o formato `D`). O comentário "o `sub` vai como o Keycloak o emite, sem normalizar" está nos dois, embora o
    `ExternalUserId.From` apare as pontas (revisão da Tarefa 14; em "Dívidas menores"). A §8 ganhou, como o plano mandava,
    a frase do `404` de um `tenantId` que não é GUID.

## Suíte completa

`dotnet build IdentityGateway.slnx`: **0 avisos, 0 erros.** `dotnet build -c Release tools/jornada-compose.cs`
(com `--no-incremental`): **código de saída `0` e nenhuma linha de aviso ou de erro** — o build de um app de arquivo
único não imprime o resumo.

Os cinco projetos rodaram um a um, com `--no-build`, Docker ligado, com as edições de documentação desta tarefa na
árvore e antes do commit dela, em 2026-10-02, das 22:03 às 22:07 (horário local) — um de cada vez, pela memória
livre da máquina, e não a solução inteira:

| Projeto | D1 | D2 | Diferença | Falhas | Skips |
|---|---|---|---|---|---|
| `IdentityGateway.Domain.UnitTests` | 161 | 161 | 0 | 0 | 0 |
| `IdentityGateway.Application.UnitTests` | 70 | 72 | +2 | 0 | 0 |
| `IdentityGateway.ArchitectureTests` | 67 | 70 | +3 | 0 | 0 |
| `IdentityGateway.Infrastructure.IntegrationTests` | 237 | 244 | +7 | 0 | 0 |
| `IdentityGateway.Api.FunctionalTests` | 145 | 229 | +84 | 0 | 0 |
| **Total** | **680** | **776** | **+96** | **0** | **0** |

De onde vêm os 96: application, os 2 de `GetTenantHandlerTests` (Tarefa 15); arquitetura, 2 da Tarefa 14 e 1 da 15;
integração, os 4 de `MemberQueriesTests` (Tarefa 14) e os 3 de `TenantDetailsTests` (Tarefa 15); funcionais, 25 da
Tarefa 13, 20 da 14, 37 da 15 e 2 da 16. O plano previa +81 nos funcionais: os 3 a mais são os formatos de GUID
acrescentados na Tarefa 15 (item 3 de "O que mudou em relação ao plano"). Depois de trocar os números no README,
`IdentityGateway.ArchitectureTests` rodou de novo: 70/70.

## Prova por mutação

Toda mutação executada foi aplicada, confirmada vermelha (erro de compilação não conta), revertida (`git diff --stat`
vazio) e reconfirmada verde. A coluna "Resultado" traz o observado nos relatórios das tarefas — o teste que ficou
vermelho e a mensagem —, ou "não executada", com o motivo. Onde o relatório traz os casos e não a mensagem, a linha
diz isso. As mensagens vêm entre « », transcritas dos relatórios; "…" marca um trecho omitido. O vermelho foi por
asserção, salvo onde a linha diz "exceção".

| Tarefa | Mutação | Resultado |
|---|---|---|
| 13 | Sem o `else { Fail }` do `RoleRequirement` | 3, os casos de `SemOPapelTenantAdmin_Veta`: «Expected resultado.Succeeded to be False because papel de outro nível: a policy não pode passar, nem com um handler que aprova tudo, but found True.» |
| 13 | O `Fail` do `NotPlatformAdminRequirement` trocado por `return` | 1, `PlatformAdminQueTambemETenantAdminDoProprioTenant_Veta`: a mesma asserção, com o motivo "platform-admin + tenant-admin" |
| 13 | Sem o `else { Fail }` do `SameTenantRequirement` | 17, a mesma asserção (motivos "rota sem tenantId", "sem recurso", "dois tenant_id …", entre outros) |
| 13 | O primeiro claim `tenant_id`; o último; algum | O primeiro: 2, os casos "dois tenant_id: o próprio e outro" e "iguais ao próprio". O último: 2, "outro e o próprio" e "iguais ao próprio". Algum: 3, os três. A mensagem não consta do relatório, só os casos |
| 13 | O `tenant_id` comparado como texto | 3, os controles positivos "rota em maiúsculas", "rota no formato N" e "claim em maiúsculas". A mensagem não consta do relatório |
| 13 | Sem a ida e volta do formato `D` no claim | 4: espaço antes, espaço depois, sinal de mais e prefixo `0x`. A mensagem não consta do relatório |
| 13 | `Guid.TryParse` simples no claim | 6: os dois espaços, o sinal de mais, o `0x`, entre chaves e o formato `N`. A mensagem não consta do relatório |
| 13 | A policy sem o `NotPlatformAdminRequirement` | 1, o caso "platform-admin + tenant-admin". A mensagem não consta do relatório |
| 14 | O tenant fora do filtro de `MemberQueries` | `MembroDeOutroTenant_NaoEAchado`: «Did not expect (ConsultarAsync(tenantB, sub, ct)) to have a value, but found MemberStatus.Invited {value: 0}.» |
| 14 | O `sub` fora do filtro | `OutroSubNoMesmoTenant_NaoEAchado` e `MembroDeOutroTenant_NaoEAchado`: «Did not expect (ConsultarAsync(tenant, SubUnico(), ct)) to have a value, but found MemberStatus.Invited {value: 0}.» (a do primeiro) |
| 14 | O handler da pertença registrado antes do `AddAuthorization` | 8, e não 4: os 4 de `QuemNaoPassaNasCamadasDoToken_NaoProvocaConsultaAoBanco` («Expected pertenca.Consultas to be 0 because sem o papel tenant-admin, but found 1 (difference of 1).») e 4 de `TenantAdminPolicyTests` (`RotaSemUmTenantIdUtilizavel_Veta` ×3 e `RecursoQueNaoEHttpContext_Veta`), em que o motivo passa a ser o do handler da pertença |
| 14 | Pertença com qualquer status não nulo | 4, `CadaEstadoDoMembro_…` em `Deactivated`, `Expired`, `Revoked` e `Erased`: «Expected sozinho.Succeeded to be False, but found True.» |
| 14 | Pertença só com `Active` | 1, `CadaEstadoDoMembro_…(Invited)`: «Expected sozinho.Succeeded to be True, but found False.» |
| 14 | A policy sem o `MemberRequirement` | 14: «Expected sozinho.Succeeded to be False, but found True.»; «Expected resultado.Succeeded to be False because sem sub, but found True.»; «Expected pertenca.Consultas to be 1, but found 0 (difference of -1).» |
| 14 | Sem o `Fail` da guarda do handler | 6, os três de `TokenSemSubUtilizavel_…` e os três de `TokenComDoisClaimsDeSub_…`: «Expected resultado.Succeeded to be False because sub vazio, but found True.» |
| 14 | `FindFirst("sub")` no lugar de exatamente um | 3, `TokenComDoisClaimsDeSub_…`: «Expected resultado.Succeeded to be False, but found True.» |
| 14 | Sem o `else { Fail }` do handler | 5, os quatro estados vetados e `QuemNaoEMembroDoTenant_EVetado`: «Expected comVizinho.Succeeded to be False because um handler que aprova tudo não muda a decisão da pertença, but found True.» |
| 14 | `InvokeHandlersAfterFailure = true`, sozinha | **Vermelha, 21, e não verde como o plano previa** — todos de `TenantAdminPolicyTests`: «Expected resultado.Failure.FailureReasons.Select(motivo => motivo.Handler.GetType()) to be equal to {…RoleRequirement} because papel de outro nível: quem veta é o requirement do caso, but …». Causa no item 1 de "O que mudou em relação ao plano" |
| 14 | Sem `context.HasFailed` na guarda, sozinha (aplicada com `false` no lugar do termo) | **Verde, 45/45 — equivalente sozinha**, como o plano previa |
| 14 | As duas anteriores juntas | 8, os 4 de `QuemNaoPassaNasCamadasDoToken_…` e 4 de `TenantAdminPolicyTests`: «Expected pertenca.Consultas to be 0 because sem o papel tenant-admin, but found 1 (difference of 1).» |
| 14 | O handler dependendo de um tipo da Infrastructure | Arquitetura, `AutorizacaoDaApi_NaoDependeDaInfrastructure`: «Violação de arquitetura — policy que alcança a Infrastructure lê o banco sem o tenant na assinatura; a pertença vem da porta IMemberQueries, da Application. Tipos violadores: - IdentityGateway.Api.Authorization.MemberRequirementHandler» |
| 14 | `Paused` acrescentado ao `MemberStatus` | Arquitetura, `NomesDosEstadosDeTenantEDeMembro_SaoContrato` («Expected Enum.GetNames…() to be a collection with 6 item(s), but {…"Erased", "Paused"} contains 1 item(s) more than …»); funcional, `CadaEstadoDoMembro_…(Paused)` («Expected PassaNaPertenca {…} to contain key MemberStatus.Paused {value: 6} because estado nov…») |
| 14 | A mais: o tenant trocado na chamada da porta (`new TenantId(Guid.NewGuid())`) | 3, os casos de `APertenca_EConsultadaUmaVezComOTenantDaRotaEOSubDoToken`: «Expected pertenca.Ultima to be (…)» |
| 15 | `POST /api/v1/tenants` com `Policies.TenantAdmin` | 11 de 128: `TodaRotaComPolicyDeTenant_TemTenantIdNoTemplate` («Expected comPolicyDeTenant.Where(…).Select(endpoint => endpoint.RoutePattern.RawText) to be empty because rota com policy de tenant precisa do parâmetro tenantId no template, but found at least one item {"/api/v1/tenants"}.»), `AsRotasDeTenant_TemAPolicyEsperada`, 6 de `RegistroDeTenantTests` e 3 de `TokenAceito_PassaDaAutenticacaoNasRotasProtegidas` («Expected resposta.StatusCode to be HttpStatusCode.Accepted {value: 202}, but found HttpStatusCode.Forbidden {value: 403}.») |
| 15 | A falha do handler como `404` (`onFailure: _ => Results.NotFound()`) | 2: `TenantNaoEncontrado_ViraOMesmo403DaAutorizacao` («Expected type to be …ProblemHttpResult, but found …NotFound.») e `QuemPassaNasCamadasDoToken_ProvocaUmaConsulta` («Expected status to be HttpStatusCode.Forbidden {value: 403}, but found HttpStatusCode.NotFound {value: 404}.») |
| 15 | A policy sem o `NotPlatformAdminRequirement` | 5 (refeita sobre o arquivo commitado): o caso do platform-admin de `MembroDoTenantComUmDefeitoNoToken_Responde403` («Expected resposta.StatusCode to be HttpStatusCode.Forbidden {value: 403} because token com defeito, but found HttpStatusCode.OK {value: 200}.»), `Todo403DaRota_TemOMesmoCorpoEOsMesmosCabecalhos` («Expected corpos.Distinct() to contain a single item because o 403 não pode dizer por que negou, but found {…}» — o pedido do platform-admin respondeu `200` com o tenant), e mais três, da policy e da ordem dos handlers: `PlatformAdminQueTambemETenantAdminDoProprioTenant_Veta` e o caso do platform-admin em `QuemNaoPassaNasCamadasDoToken_NaoProvocaConsultaAoBanco` e em `QuemNaoPassaNasCamadasDoToken_Recebe403SemConsultaAPertenca` («Expected consultas to be 0 because platform-admin que também é tenant-admin, but found 1 (difference of 1).») |
| 15 | A policy sem o `MemberRequirement` | 18 de 120, e não 15: os 14 da mesma mutação na Tarefa 14, `TokenCertoDeQuemNaoEMembro_Responde403` («… because sub sem Member, but found HttpStatusCode.OK {value: 200}.»), `MembroDesativado_PerdeOAcessoNoPedidoSeguinte` («… because membro desativado, but found HttpStatusCode.OK {value: 200}.»), `Todo403DaRota_…` e `QuemPassaNasCamadasDoToken_ProvocaUmaConsulta` («Expected consultas to be 1, but found 0 (difference of -1).») |
| 15 | As duas anteriores juntas (falha como `404` e sem a pertença) | 20 de 120, entre eles `TenantQueNaoExiste_Responde403ENao404`, que nenhuma das duas sozinha deixou vermelho («Expected resposta.StatusCode to be HttpStatusCode.Forbidden {value: 403} because tenant inexistente, token com o tenant_id dele, but found HttpStatusCode.NotFound {value: 404}.»). `Todo403DaRota_…` caiu por **exceção** (`JsonException`, o corpo vazio do `404`) |
| 15 | O e-mail do admin inicial em `TenantDetailsResponse` | Funcional, `AdminDoTenant_LeOProprioTenantComExatamenteAsChavesDoContrato` («… to be a collection with 7 item(s), but {"tenantId", …, "registeredAt", "initialAdminEmail"} contains 1 item(s) more than …»); arquitetura, `LeituraDeTenant_NaoCarregaEmail` («Expected comEmail to be empty because o e-mail do admin inicial não é atributo do tenant para quem o lê, but found at least one item {"TenantDetailsResponse.InitialAdminEmail"}.») |
| 15 | Um `Email?` em `TenantDetailsView` | Arquitetura, `LeituraDeTenant_NaoCarregaEmail`: «… but found at least one item {"TenantDetailsView.AdminInicial"}.» |
| 15 | O handler da pertença registrado antes do `AddAuthorization` | 12 de 120, e não 8: os 4 de `QuemNaoPassaNasCamadasDoToken_Recebe403SemConsultaAPertenca`, na composição real («Expected consultas to be 0 because tenant-admin de outro tenant, but found 1 (difference of 1).»), os 4 da mesma mutação na Tarefa 14 e os 4 de `TenantAdminPolicyTests` |
| 15 | O tenant comparado como texto | 10 de 120, e não 7: 3 de `TenantAdminPolicyTests`, 2 de `PertencaNaPolicyTenantAdminTests`, os 4 formatos de `ProprioTenantComOGuidDaRotaEscritoDeOutroJeito_Responde200` (`D`, `N`, `B`, `P`) e `ProprioTenantComSinalNoGuidDaRota_…`: «Expected resposta.StatusCode to be HttpStatusCode.OK {value: 200} because /api/v1/tenants/{01A0FEC1-…}, but found HttpStatusCode.Forbidden {value: 403}.» |
| 15 | O primeiro claim `tenant_id`; o último | 4 de 54 cada: por HTTP, "o próprio e outro" (ou "outro e o próprio") e "o próprio, duas vezes", mais os 2 unitários: «Expected resposta.StatusCode to be HttpStatusCode.Forbidden {value: 403} because token com defeito, but found HttpStatusCode.OK {value: 200}.» |
| 15 | `MaxClients` e `MaxUsers` trocados no handler | `TenantExistente_DevolveOsCamposComStatusETierEmTexto`: «… but found … { MaxClients = 500, MaxUsers = 20, Tier = "Enterprise" } …» |
| 15 | `0` no lugar das vagas ocupadas na projeção | `TenantAtivado_VoltaActiveComAVagaDoAdmin`: «Expected lido.OccupiedSeats to be 1, but found 0 (difference of -1).» |
| 15 | A mais: um cabeçalho `X-Negado-Por` com o nome do handler que vetou, no `403` | **Com o teste do plano: verde, 29/29** — a comparação era só pelo nome dos cabeçalhos. Com o teste corrigido: `Todo403DaRota_…` («Expected cabecalhos.Distinct() to contain a single item because nem pelos cabeçalhos, but found {"… \| X-Negado-Por: RoleRequirement", …}») |
| 16 | `Outbox:Enabled` = `"false"` na `ApiComKeycloakFactory` | `AdminConvidado_LeOProprioTenant_ENaoLeOutro_EOPlatformAdminNaoLeNenhum` (o outro teste da classe passou), em 2 min 24 s: «Expected status to be the same string because o tenant precisa ser provisionado em 90 s, but they differ at index 0:» — o filtro da saída cortou a linha com os dois valores |
| 16 | A policy sem o `MemberRequirement`, contra o Keycloak real | **Não executada:** o sistema de permissões negou a edição temporária. O `403` de `TenantIdHerdadoDeUmGrupo_NaoBasta_SemSerMembroNoBanco` nunca foi visto virar `200`. O que sustenta o teste são as premissas que ele afirma antes do `403` — o token traz só `tenant-admin` e o `tenant_id` da vítima, e `azp`, `typ` ou `sub` malformados dariam `401` —; a revisão concluiu, por leitura, que só a pertença pode dar esse `403`. É raciocínio, não observação |
| 16 | Uma chave a mais na resposta, pega pela jornada | **Não executada:** depende do compose, cuja subida foi negada |
| 11 | O one-shot sem o marcador, e o marcador lido por `--fields attributes`, com o app como testemunha | **Não executadas**, pendentes desde a D1. Vistas vermelhas na Tarefa 10, pela contagem no mailpit, sem o app (handoff da D1) |

## Verificação ao vivo

**Nada da D2 rodou contra o compose.** A jornada no projeto isolado `igverif` (Tarefa 16, Passo 5) não rodou: o
`docker compose -p igverif up` foi negado pelo sistema de permissões antes de executar. As etapas novas do app — o
admin convidado conclui o convite, entra pelo device flow e lê o próprio tenant; outro tenant e o platform-admin
levam `403` — só foram compiladas (build em Release, sem aviso, nesta tarefa e na Tarefa 16). **Não há tempos de
etapa.** Também não rodaram a conferência da saída do app (`eyJ`, `action-token?key=`, `user_code`) nem a checagem
de token nos logs do compose antes da derrubada.

O que chegou mais perto, fora do compose: a coleção com Keycloak real (Testcontainers, Keycloak 26.7.4 e mailpit),
`LeituraDeTenantComKeycloakTests`, 2/2 em 1 min 01 s, em 2026-10-02, às 21:36 (horário local) — o platform-admin
registra o tenant, o provisionamento convida o admin, ele conclui o convite, entra pelo device flow e lê o próprio
tenant com exatamente as chaves do contrato; outro tenant e o platform-admin levam `403`; e o ataque do `tenant_id`
herdado de um grupo leva `403`. Os mesmos testes rodaram de novo na suíte completa desta tarefa.

**A primeira execução da jornada nova será o job `Compose` da CI do PR da D2.** O tempo do job e o do passo
`A jornada com token do Keycloak` (`timeout-minutes: 5`, estimativa do plano, sem medição) entram aqui depois do PR.
A CI do PR #6, da D1, não foi lida nesta sessão (a leitura dos checks foi negada pelo sistema de permissões); o autor
mesclou o PR sem commits de correção.

## Pendências

**Do autor — as provas que o sistema de permissões negou aos agentes.** O roteiro único está pronto, fora do
repositório, em `.superpowers/sdd/2026-09-30-tokens-keycloak/provas-d2.sh` (pasta ignorada pelo git), e **até esta
atualização não foi executado** (conferido em 2026-10-02, 22:34, horário local: nenhum contêiner nem volume
`igverif`). Rode no Git Bash, com nenhuma suíte de testes rodando ao mesmo tempo (memória), a partir da raiz do
repositório: `bash .superpowers/sdd/2026-09-30-tokens-keycloak/provas-d2.sh`. Leva de 25 a 35 minutos, recusa
começar com a árvore suja, reverte toda edição ao sair (inclusive com Ctrl+C) e não faz commit. As quatro edições de
mutação foram conferidas a seco: cada uma troca exatamente o trecho previsto. Ele faz, nesta ordem, sempre com
`-p igverif`:

1. **A jornada no compose isolado**, com a checagem de segredos na saída do app e, antes de qualquer `down`, nos logs
   de `api`, `keycloak` e `platform-admin-invite`, com controle positivo. Os comandos estão no relatório da Tarefa 16.
2. **Mutação 2 da Tarefa 16:** tirar `new MemberRequirement()` da policy em `AutorizacaoDaGateway.cs` e rodar
   `LeituraDeTenantComKeycloakTests` com o compose **derrubado** (pouca memória). Vermelho esperado:
   `TenantIdHerdadoDeUmGrupo_NaoBasta_SemSerMembroNoBanco`, `200` no lugar de `403`, com as premissas passando.
3. **Mutação 3 da Tarefa 16:** `TenantDetailsResponse` com o e-mail, pega pela jornada no compose.
4. **Mutações 1 e 2 da Tarefa 11**, pendentes desde a D1 (roteiro no handoff da D1, "Pendências").
5. **A derrubada**, com `docker compose -p igverif down -v`, a conferência dos volumes (sobram só os três
   `identitygateway_*`) e a remoção do arquivo de estado da jornada.

Se a jornada falhar quando rodar, o conserto vira um commit novo na branch. Quando as provas rodarem, um commit `docs`
atualiza a "Prova por mutação" e a "Verificação ao vivo" deste handoff, e corrige no mesmo commit as três frases
imprecisas que a revisão final apontou (abaixo).

**A revisão final da branch** (`cb8b568..bea8893`) concluiu "pronto para o merge": nenhum achado crítico nem
importante, e nenhuma dívida das revisões por tarefa precisa ser corrigida antes do merge. Confirmou que as quatro
camadas da policy vetam em todo ramo que não é sucesso, que o banco só é lido para quem passou nas três camadas do
token, que a rota opera sobre o mesmo `Guid` que a policy autorizou, e que "não existe" e "não é seu" saem idênticos
no corpo, nos cabeçalhos e no caminho percorrido. Deixou cinco achados menores:
- **Três frases imprecisas, para o commit `docs` das provas:** o comentário "sem normalizar" em
  `MemberRequirementHandler.cs:48` e na §11.7 da v2.7 (o `ExternalUserId.From` apara as pontas; na v2.7, editar por
  script, porque o arquivo tem a sequência de escape do "e comercial" em duas linhas); o comentário de
  `ProblemDetailsDeAutorizacao.cs:16-17`, da D1, que diz "desligado, na rota de tenant" quando a D2 tornou a opção
  global; e a mensagem do commit `bea8893`, que generaliza "negadas pelo sistema de permissões" para as quatro provas
  (o cabeçalho deste handoff já foi corrigido; a mensagem fica no histórico).
- **Dívida para uma fatia transversal:** falta `Cache-Control: no-store` no `200` da leitura do tenant
  (`TenantsModule.cs:57`). Não abre acesso entre tenants.
- **Inócuo:** os `HttpResponseMessage` dos `403` da jornada não são descartados (`jornada-compose.cs:228-233`).

Para a fatia E, a revisão recomenda levar: o teste do SQL da projeção, o método único da leitura do tenant da rota,
o tipo do claim por comparação ordinal ao conceder, e a `TenantReadAccess` em `Policies.DeTenant`.

**Para o autor decidir:**
- A leitura do `tenantId` da rota: o plano implementou "qualquer formato de GUID na rota, só o formato `D` no claim"
  (a §5.2 do design); a §4.3 do design dizia formato `D` nos dois. A Tarefa 15 acrescentou controles positivos para
  chaves, parênteses e sinal de mais; o prefixo `0x` não tem teste (só seria o mesmo GUID se o id começasse por `00`,
  e o id é um GUID v7 gerado pelo domínio).
- **Endurecer a policy `TenantAdmin`** (revisão da Tarefa 13): (a) exigir o tipo do claim por comparação ordinal ao
  conceder — hoje um principal com `Roles` ou `TENANT_ID` passaria, porque o `ClaimsPrincipal` acha claims sem olhar
  a caixa do nome; inalcançável com o realm, porque o nome do claim vem do mapper e o token é assinado; a contagem do
  `tenant_id` deve continuar insensível, porque é o lado que veta —; e (b) exigir identidade autenticada na própria
  policy, que hoje é garantida pelo JwtBearer com a policy de fallback.
- **Risco de vermelho sem defeito na CI** (revisão da Tarefa 16): o primeiro teste da coleção com Keycloak real reusa
  o token do operador (vida de 300 s) depois de até 90 s de espera pelo `Active`, do convite e do device flow — num
  runner lento, um `401` apareceria no lugar do `403`. E o `timeout-minutes: 5` do passo da jornada é estimativa.
- Do handoff da D1, sem registro de decisão do autor até aqui: o `sub` em maiúsculas aceito e a v2.7 sem o nome da
  "fatia E".

**Limites que continuam abertos** (v2.7, §19): o platform-admin recebe `403` na leitura de tenant até existir a
auditoria; `Invited` passa na pertença até o aceite do convite ser sincronizado; o Data Plane continua exposto ao
`tenant_id` por grupo; a pertença não contém quem tem a chave da Gateway; e-mail digitado errado dá a leitura do
tenant ao destinatário errado.

**Um limite novo, da D2, para o autor decidir:** o limitador de requisições roda depois da autorização
(`Program.cs`: `UseAuthentication`, `UseAuthorization`, depois `UseRateLimiter`). O `403` de um token que passa nas
três camadas do token e não é de um membro em `Invited` ou `Active` custa uma consulta ao banco e não consome cota.
Mover o limitador para antes da autorização faria os `401` consumirem cota por IP.

**Dívidas menores das revisões por tarefa** (texto inteiro nas revisões):
- Nenhum teste afirma que o SQL da projeção de `TenantQueries` não lê a coluna do e-mail: a prova é pelo tipo, e uma
  projeção reescrita para carregar a entidade passaria na suíte.
- A leitura do tenant da rota está escrita duas vezes, igual (`SameTenantRequirement` e `MemberRequirementHandler`).
- O `onFailure` do módulo descarta o erro sem log: se o ramo "inalcançável" for alcançado, a quebra de invariante não
  deixa rastro próprio.
- Sem teste por HTTP de tenant fora de `Active` (o handler está coberto); os nomes de `PlanTier`, que viraram texto
  público com a rota, não estão travados.
- O app da jornada guarda o refresh token renovado no arquivo de estado só no fim da fase (a regra D-n manda
  guardá-lo antes de qualquer outro passo); a jornada aborta nesse caso, e o dano é nulo.
- `LeituraDeTenantTests` lê o JSON antes de afirmar o status no teste do `403` único (cai por exceção, sem dizer qual
  pedido), e o caso do sinal de mais exige `200`, que é tolerância do parse do runtime, e não contrato.
- O comentário de `MemberRequirementHandler` diz "sem normalizar", e o `ExternalUserId.From` apara as pontas; a
  `MemberQueriesTests` não lê `Active` da coluna; a `PertencaFalsa` descarta o `CancellationToken` e nunca lança.
- O comentário de `RegrasDeLeituraTests` promete os tipos aninhados, e a lista é escrita à mão; `Policies.DeTenant`
  também é lista manual.
- Na coleção com Keycloak real, o segundo tenant do primeiro teste é provisionado pelo Outbox sem espera (ruído no
  descarte), e todo tenant registrado por `TokensDoKeycloakNaApiTests` passou a ser provisionado no realm
  compartilhado.

**Registro histórico:** o handoff da D1 (linha 253) cita `AsRotasDeTenant_ExigemPlatformAdmin`, que a Tarefa 15
renomeou para `AsRotasDeTenant_TemAPolicyEsperada`. O documento não foi editado.

**Segue pendente do M0:** a tabela de auditoria, o armazenamento de eventos do realm e o RabbitMQ.

## Próximo passo

1. Ler a CI do [PR #7](https://github.com/Joseleno/identity-gateway/pull/7) (`gh pr checks 7`). O job `Compose` roda
   pela primeira vez a jornada nova: olhar as quatro etapas novas e o tempo do passo `A jornada com token do Keycloak`
   (teto de 5 minutos). Se algo falhar, o conserto vira um commit novo na branch.
2. O autor roda o roteiro de provas ("Pendências", itens 1 a 5), com nenhuma suíte rodando ao mesmo tempo.
3. Um commit `docs` com o resultado das provas, o tempo do job `Compose` e as três frases imprecisas da revisão final.
4. Mesclar o PR #7. Não há aviso de `down -v`. Depois, acrescentar o número do PR na linha da fatia D da §16 da v2.7.
5. Decidir a próxima fatia (o design propõe a auditoria, que destrava o `TenantReadAccess`).

## Como retomar

Na raiz do repositório: `git switch feat/leitura-do-tenant && git pull`, e leia este handoff. O livro de bordo da
execução, com cada decisão e cada achado adiado, está em `.superpowers/sdd/2026-09-30-tokens-keycloak/progress.md`
(seção "D2"); os relatórios e as revisões de cada tarefa estão na mesma pasta. A pasta é ignorada pelo git e existe
só nesta máquina.
