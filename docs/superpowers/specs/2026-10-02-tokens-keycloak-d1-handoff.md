# Handoff — tokens do Keycloak, parte D1 entregue

> **Data:** 2026-10-02 · **Marco:** M0 + M1 (fatia D, primeira parte) · **Status:** implementada, build e suíte
> completa verdes; a jornada do compose foi confirmada ao vivo, localmente, num projeto isolado. **O job `Compose`
> da CI nunca foi executado** — a CI só roda em push na `main` ou em PR —, **e duas provas por mutação da Tarefa 11
> ficaram por fazer**, com a derrubada do ambiente de verificação, bloqueadas pelo sistema de permissões do ambiente
> (ver "Pendências"). Push e PR aguardam autorização do autor.
> **Onde parou:** as 12 tarefas da D1 estão commitadas na branch `feat/tokens-keycloak`; falta a revisão final da
> branch, enviar a branch, abrir o PR contra `main`, acompanhar a CI e mesclar. A D2 (Tarefas 13–17) começa depois
> do merge, a partir da `main`.
>
> Sucede o [handoff do design da fatia D](2026-10-01-tokens-keycloak-design-handoff.md) e o
> [handoff do convite do admin inicial](2026-09-30-convite-admin-inicial-handoff.md) (fatia C, PR #5). Design da
> fatia: [`2026-09-30-tokens-keycloak-design.md`](2026-09-30-tokens-keycloak-design.md). Plano:
> [`2026-09-30-tokens-keycloak.md`](../plans/2026-09-30-tokens-keycloak.md). Referência normativa:
> [`especificacao-arquitetural-v2.7.md`](../../especificacao-arquitetural-v2.7.md).

---

## Estado do repositório

| O quê | Estado |
|---|---|
| Branch | `feat/tokens-keycloak`, 4 commits de planejamento sobre `main` (`8537d76`, `833a9cc`, `0294b90`, `677adba`), os 8 commits do plano (`f530d74`, `4a00e6d`, `bad8f78`, `14901b1`, `aa2eff5`, `4ac4636`, `b46f1aa`, `4a896e6`, de `git log --oneline -- docs/superpowers/plans/2026-09-30-tokens-keycloak.md`) e **13 commits** das Tarefas 1–12 sobre `main` (`5b0113c`, o merge do PR #5) |
| `main` | Não tocada — recebe o merge pelo PR |
| Working tree | Limpa depois do commit desta tarefa |
| Docker | Rodando; usado nas tarefas com Testcontainers (PostgreSQL, Redis, Keycloak 26.7.4 e mailpit reais), na verificação ao vivo das Tarefas 10 e 11 (projeto isolado `igverif`) e na suíte completa da Tarefa 12. **O `igverif` da Tarefa 10 foi derrubado com `down -v`; o da Tarefa 11 não foi** — a derrubada foi negada pelo sistema de permissões. Conferido só por leitura em 2026-10-02, 10:42 (horário local): nove contêineres `igverif-*` parados e os volumes `igverif_gateway-keys` e `igverif_postgres-data` (ver "Pendências"). Os volumes `identitygateway_*` do autor não foram tocados: os três existem |
| Push / PR | **Pendentes de autorização.** Nada foi enviado |

Três frentes de commits compõem a branch: **planejamento** (12 `docs` — design, design revisado, spikes, handoff do
design e os 8 do plano, dois deles, `b46f1aa` e `4a896e6`, feitos durante a execução para corrigir o plano),
**Tarefas 1–11** (12 — **5** `feat`, **5** `test`, 1 `fix`, da rodada de correção da Tarefa 8, e 1 `ci`; nenhum
`refactor` nem `chore`) e a **Tarefa 12** (1 `docs`: especificação v2.7, documento de negócio, README, CONTRIBUTING e
este handoff).

**Aviso para quem já tem volumes do compose: rode `docker compose down -v` uma vez, depois
`docker compose up -d --build`. É a quarta vez que o projeto pede isso.** O realm só é importado na primeira subida
(`IGNORE_EXISTING`), e num volume antigo faltam os client scopes `gateway-roles`, `gateway-tenant` e `gateway-api`, o
client de demonstração, o catálogo de papéis e o usuário do primeiro platform-admin. O one-shot
`platform-admin-invite` detecta o volume antigo e sai com `1`, mandando rodar o `down -v`; como a `api` depende dele,
o `docker compose up` falha com a causa no log, em vez de a API subir e todo token dar `401`. O README e o corpo do
PR repetem o aviso. **O mesmo vale se a senha do admin do `master` do compose foi trocada, ou se ele foi apagado:** o
one-shot faz login com ele a cada subida.

## O que a D1 entregou

A API passou a aceitar só access tokens do Keycloak, e o JWT simétrico do template deixou de existir. O realm emite
o token que a §10.1 da especificação descreve, o primeiro platform-admin nasce sem senha e é convidado por e-mail
uma única vez, e a demonstração, os testes e a CI obtêm o token pelo device flow. Fecha o critério do M0 "primeiro
`curl` com token do Keycloak". A rota de tenant e a policy `TenantAdmin` são da D2.

| Camada | Entregue |
|---|---|
| Projeto de suporte de testes | `tests/IdentityGateway.Testing.Keycloak`, biblioteca (`IsTestProject=false`, sem referência a `src/`): `KeycloakFixture`, `ChavesDeTeste` e `RaizDoRepositorio` (movidos do projeto de integração), `FamiliaDeFalha`, `FalhaDoHarnessException`, `ClienteDoMailpit`, `HarnessDeLogin`, `TokensDeUsuario`, `UsuarioDeTeste`, `SenhasDeTeste`, `PayloadDoJwt`; `xunit.v3.extensibility.core` no `Directory.Packages.props`; o ROPC do fixture removido |
| Realm | Catálogo `platform-admin`, `tenant-admin`, `financial-manager` e `reader`, nunca compostos, mais `offline_access` e `uma_authorization` fora do papel padrão; scopes `gateway-roles`, `gateway-tenant` e `gateway-api`, nenhum default do realm; `CreateDefaultClientScopes`; `identity-gateway` com `basic` e `roles`; `identity-gateway-demo` público, só device flow; `accessTokenLifespan` 300; `registrationAllowed` falso; `bruteForceProtected`; rotação do refresh token; o usuário do platform-admin com `${PLATFORM_ADMIN_EMAIL}`, sem credencial; `RegrasDoRealmTests` com as regras novas |
| Infrastructure | `AccessTokenValidationOptions` (seção `Keycloak:Auth`: `Audience`, `AllowedClients`; `Issuer`, `MetadataAddress` e `RequireHttpsMetadata` derivados pelo adaptador); `KeycloakAdminOptions.Issuer` e `MetadataAddress`, com `AssertionAudience => Issuer`; a recusa do client de demonstração fora de Development; `JwtOptions` removido |
| Api | `Authentication/ValidacaoDoAccessToken` e `AvisoDeChavesIndisponiveis` (JwtBearer por metadados internos, `IssuerValidator` estrito, RS256, `ClockSkew` 30 s, `BackchannelTimeout` 5 s, `RefreshInterval` 30 s, `IncludeErrorDetails` falso), `FormaDoAccessToken` (`azp`, `typ`, `sub`), `AutenticacaoLogs` (EventIds 2100–2103), `AvisoDeClientsPermitidos`; `Authorization/Policies`, `RespostasDeAutorizacao` e `ProblemDetailsDeAutorizacao` (Problem Details em `401` e `403`); `FallbackPolicy` autenticada e `AllowAnonymous` explícito nas rotas anônimas; `HttpCurrentUser` com `"sub"`; `Security/JwtTokenService` e a seção `Jwt` dos appsettings removidos; `AllowedClients` só no `appsettings.Development.json` |
| Compose | One-shot `platform-admin-invite` (`kcadm`, marcador `platformAdminInviteSentAt` no realm antes do envio, link de 4 h, saída `0` ou `1`, reenvio por `REENVIAR=1`); a `api` depende dele; `PLATFORM_ADMIN_EMAIL` com padrão e recusa de vazio ou maiúsculas; `api` e Jaeger só em `127.0.0.1`; sem `Jwt__SigningKey` |
| CI e ferramentas | `tools/jornada-compose.cs` (app de arquivo único, `#:project`, sem AOT) com as fases `convites`, `jornada`, `antes-de-parar`, `com-keycloak-parado` e `depois-de-voltar`; job `Compose` com `pipefail`, prazo por passo, o convite contado exato, a receita HS256 antiga com `401`, o convite do admin do tenant e o Keycloak parado com `stop`/`start` |
| Testes | OIDC falso com emissor divergente na `IdentityGatewayApiFactory`; suíte negativa de autenticação; opções do JwtBearer conferidas em execução; `ApiEmProducaoFactory` com pedidos; coleção com Keycloak real atravessando a API e a ponte de contrato; vazamento do e-mail no token, com log e trace; regras de arquitetura novas (`RegrasDaApiTests`, `RegrasDoAmbienteLocalTests`, `RegrasDeFerramentasTests`) |
| Documentos | Especificação v2.7 (§0 nova, com T1–T15 e as erratas E1–E8, o ADR-011 e as seções que a fatia tocou; o que é da D2 marcado "(D2, planejado)"); documento de negócio 1.4; README (andamento, v2.7, contagem, onze ADRs, linha evolutiva; a demonstração por device flow e as notas fixas, da Tarefa 11); CONTRIBUTING apontando a v2.7 e o projeto de suporte; este handoff |

## O que mudou em relação ao plano

O plano foi escrito antes de o código existir. O que a execução encontrou:

1. **O `sub` é conferido pela ida e volta do GUID, e não pelo tamanho** (`c608aa8`, achado da revisão da Tarefa 8).
   O plano mandava `{ Length: 36 }` mais `Guid.TryParseExact(sub, "D")`. O `TryParseExact` aceita, em cada
   componente, o prefixo `0x` e o sinal de mais, além de espaço nas pontas: `0x99a000-0000-7000-8000-00000000000a`,
   com 36 caracteres, passava — reproduzido antes da correção, na forma e por HTTP (`400` no lugar do `401`). O
   código agora compara `id.ToString("D")` com o texto recebido, sem diferenciar caixa, e a checagem de tamanho saiu.
   Mutações da correção: sem a comparação, 6 vermelhos; com `Ordinal`, o controle do `sub` em maiúsculas fica
   vermelho. A mutação "`Guid.TryParse` no lugar de `TryParseExact`" foi vista vermelha no código anterior e **não
   foi refeita sobre o código novo** — equivalente por raciocínio, não por execução —, e a mutação "sem o tamanho"
   deixou de existir. O plano da D2 já traz a mesma conferência no claim `tenant_id` (`4a896e6`), e a v2.7 a
   descreve assim nos dois lugares (§11.7 e §11.8).
2. **`(x?.Y).Should()` precisa dos parênteses** (Tarefa 7, `1be059e`; plano corrigido em `b46f1aa`). Como o plano
   escreveu, `resposta.Content.Headers.ContentType?.MediaType.Should().Be(…)` não afirma nada quando o cabeçalho é
   nulo: o `?.` curto-circuita a cadeia inteira. Achado pela mutação que tira o result handler do Problem Details:
   os 16 casos da suíte negativa ficaram verdes. Com os parênteses, a mesma mutação dá 18 vermelhos. Eram três
   asserções; não havia outra ocorrência do padrão em `tests/`.
3. **As factories de teste ganharam uma máscara de configuração do Serilog** (Tarefas 7 e 8). O
   `WebApplicationFactory` entrega cada `UseSetting` como argumento de linha de comando, com `""` nas seções
   intermediárias, e o Serilog lia `Serilog:WriteTo:9 = ""` como nome de sink e descartava o coletor em memória, em
   silêncio. As duas factories acrescentam `["Serilog:WriteTo:9"] = null` num provedor em memória. Sem a máscara, o
   coletor fica vazio — provado por mutação na `ApiEmProducaoFactory`.
4. **O `.gitignore` ganhou uma exceção** para `tests/IdentityGateway.Api.FunctionalTests/Logs/` (Tarefa 7,
   `1be059e`): a regra `[Ll]ogs/` engolia a pasta do coletor, e o commit não compilaria num clone limpo. A mensagem
   desse commit não cita o `.gitignore`, os parênteses do item 2 nem parte das mutações; elas estão na tabela abaixo.
5. **A base não era de 491 testes, e sim de 493** (Tarefa 1): 161 no domínio e 190 na integração, onde o plano
   supunha 160 e 189 — a integração, conferida com `--list-tests` num worktree do commit de partida. Todo total de
   integração do plano está uma unidade abaixo do observado. Sem mudança de comportamento.
6. **Uma previsão do plano estava errada na mutação `fullScopeAllowed: true` do client de demonstração** (Tarefa 5).
   O plano previa vermelho o teste do usuário sem papel do catálogo; ele continua verde. Esse usuário só tem
   `default-roles-identity-gateway`, e o Keycloak 26.7.4 emite o token **sem** o claim `roles` nesse caso, com ou sem
   `fullScopeAllowed`. A mutação é pega por outros quatro testes.
7. **O teste de vazamento do e-mail afirma a ausência por booleano, e ganhou um controle positivo** (Tarefa 9,
   `b85b1ec`). O `NotContain` do plano imprimiria, ao falhar, o token procurado e a coleção de logs inteira. E o
   controle positivo do plano passava com a linha "Request starting", que é `Information`: o teste ficaria verde
   mesmo se o nível `Debug` não chegasse ao Serilog. O controle novo exige um evento em `Debug`, de origem
   `Microsoft.AspNetCore`, com a rota do pedido. Custo: a mensagem de falha não mostra mais a linha que vazou.
8. **Três mutações do plano não compilam como escritas**, por causa do `TreatWarningsAsErrors`, e foram aplicadas
   de forma equivalente: a do login reenviado no harness (`CS0219`), a da guarda `HttpSoEmDesenvolvimento`
   (`CS0162` com o `return true;`) e a do `Authorization` no log do pedido (`CA1873`). Uma quarta, a do
   `EhFalhaDeChaveOuDeMetadados` sempre verdadeiro, foi aplicada como `excecao is not null`, para não deixar o
   parâmetro sem uso. Nas quatro, o vermelho observado foi por asserção.
9. **O Keycloak leva de 2,3 a 2,8 s para recusar `PLATFORM_ADMIN_EMAIL` vazio ou com maiúsculas** (Tarefa 10), e
   não "menos de um segundo", como o plano dizia: a medida inclui a criação do contêiner pelo `docker compose run`.
10. **Tarefa 11: a jornada passou na primeira execução, sem ajuste no app — e duas mutações não foram executadas.**
    O `docker compose -p igverif down -v`, necessário para recriar o ambiente com o convite pendente, foi negado
    pelo sistema de permissões do ambiente de execução. Ninguém o contornou. A mensagem do commit `751f8c9` foi
    ajustada para dizer isso. Detalhe em "Pendências".
11. **O build de um app de arquivo único não imprime o resumo "0 Aviso(s), 0 Erro(s)"** que o plano esperava
    (Tarefas 11 e 12): a evidência é o código de saída `0` e a ausência de linhas de aviso.

## Suíte completa

`dotnet build IdentityGateway.slnx`: **0 avisos, 0 erros.** `dotnet build -c Release tools/jornada-compose.cs`:
**compila, com código de saída `0` e nenhuma linha de aviso ou de erro** — o build de um app de arquivo único não
imprime o resumo de avisos e erros.

`dotnet test` (solução inteira, Docker rodando, HEAD `751f8c9`, com as edições de documentação desta tarefa já na
árvore e antes do commit dela, em 2026-10-02, das 10:28 às 10:30): **676 total, 0 falhas, 0 skips**, em 1 min 48 s.
A saída da solução inteira só traz o total geral; os totais por projeto vêm de uma segunda execução, projeto a
projeto, com `--no-build`, das 10:31 às 10:34, todas com 0 falhas e 0 skips.

| Projeto | Total | Falhas | Skips |
|---|---|---|---|
| `IdentityGateway.Domain.UnitTests` | 161 | 0 | 0 |
| `IdentityGateway.Application.UnitTests` | 70 | 0 | 0 |
| `IdentityGateway.ArchitectureTests` | 67 | 0 | 0 |
| `IdentityGateway.Infrastructure.IntegrationTests` | 233 | 0 | 0 |
| `IdentityGateway.Api.FunctionalTests` | 145 | 0 | 0 |
| **Total** | **676** | **0** | **0** |

`IdentityGateway.Testing.Keycloak` é biblioteca de suporte e não tem testes: não aparece no resumo. O handoff da
fatia C registrava 491 testes; a Tarefa 1 contou **493** no commit de partida (161 no domínio e 190 na integração,
e não 160 e 189), e a origem da diferença não foi investigada. O design estimava de 50 a 55 testes novos na D1;
**entraram 183** (26 na arquitetura, 43 na integração e 114 nos funcionais). Depois de trocar os números no README,
`IdentityGateway.ArchitectureTests` rodou de novo: 67/67.

## Prova por mutação

Toda mutação executada foi aplicada, confirmada vermelha (erro de compilação não conta), revertida byte a byte
(`git diff --stat` vazio) e reconfirmada verde antes do commit. As linhas são as da §5.3 do design marcadas D1; a
coluna "Resultado" traz o observado nos relatórios das tarefas e nas mensagens de commit — o teste que ficou
vermelho e a mensagem —, ou "não executada", com o motivo. Onde o relatório de uma tarefa não traz a evidência, a
linha diz "não consta do relatório". `F` = funcional com o OIDC falso; `K` = Keycloak real; `P` = host em
`Production`.

Como ler as mensagens: elas vêm entre « », transcritas dos relatórios, sem os sinais de menor e maior que a
biblioteca de asserções põe em volta de `null` e dos nomes de tipo. A mensagem que mais se repete aparece abreviada:
"esperado `401`, veio `400`" é «Expected resposta.StatusCode to be HttpStatusCode.Unauthorized {value: 401} …, but
found HttpStatusCode.BadRequest {value: 400}.» — o `400` é o que a rota responde quando o token passa da
autenticação. O vermelho foi por asserção, salvo onde a linha diz "exceção".

| Mutação | Deve ser pega por | Resultado |
|---|---|---|
| Remover o `IssuerValidator` (manter `ValidIssuer`) | F: `iss` do discovery ≠ configurado (entre os F, só ele); K: `PublicBaseUrl` errado | Vermelha. F (Tarefa 7): 2 testes — `AValidacao_EstaLigadaPorInteiroESoAceitaRs256` («Expected parametros.IssuerValidator not to be null because o ValidIssuer sozinho não restringe.») e, na suíte negativa, **só** o caso `iss igual ao do discovery, diferente do configurado` («Expected resposta.StatusCode to be Unauthorized {401} because POST /api/v1/tenants, but found BadRequest {400}.»); os outros quatro casos de `iss` continuaram `401`. K (Tarefa 9): `ApiComOEnderecoPublicoErrado_RecusaTokenRealDoKeycloak` («Expected resposta.StatusCode to be HttpStatusCode.Unauthorized {value: 401}, but found HttpStatusCode.Accepted {value: 202}.») |
| Validador frouxo: comparar com o `BaseUrl`, `StartsWith` ou `OrdinalIgnoreCase` | F: todos os sucessos; barra final, sufixo e maiúsculas | Vermelhas, as três (Tarefa 7). **Pelo `BaseUrl`** (`KeycloakAdminOptions.Issuer` sempre pelo endereço interno): 23 testes — os 3 casos aceitos e todos os que usam `CreateClientAutenticado` («…to be Accepted {202}/BadRequest {400}/Forbidden {403}/NotFound {404}, but found Unauthorized {401}.»); um dos 23, `RegistroAceitoEConsulta_NaoDevolvemOEmail`, cai por `KeyNotFoundException`, e não por asserção. A mesma mutação, na Tarefa 6: `Issuer_ComPublicBaseUrl_EOMesmoTextoDoAudDoAssertion`, `MetadataAddress_SaiDoBaseUrlInternoENuncaDoPublico` e `IssuerEMetadadosNaConfiguracao_SaoIgnorados` («Expected validacao.Issuer to be the same string, but they differ at index 7/8»). **`StartsWith`:** 4 — `EmissorEstritoTests` (barra final; sufixo `-outro`) e os casos `iss com barra final` e `iss com sufixo`. **`OrdinalIgnoreCase`:** 2 — `EmissorEstritoTests` com `HTTPS://SSO…` («Expected a SecurityTokenInvalidIssuerException to be thrown, but no exception was thrown.») e o caso `iss em maiúsculas` (esperado `401`, veio `400`) |
| `ValidateAudience=false` | F: `aud` errada; K: token do client de device flow do fixture, com `azp` aceito e sem `identity-gateway-api`; opções resolvidas | Vermelha. F (Tarefa 7): 4 — `AValidacao_EstaLigadaPorInteiroESoAceitaRs256` («Expected parametros.ValidateAudience to be True, but found False.»), os casos `aud = account` e `sem aud` (esperado `401`, veio `400`) e, a mais, `AvisoDeChaveNoLogTests`. K (Tarefa 9): `TokenDoClientDeDeviceFlowDoFixture_AzpAceitoSemAAudiencia_401` — esperado `401`, veio `403`, e não `202`: o token passa a autenticar, mas o client do fixture não tem `gateway-roles`, e o token não traz `roles` |
| `ClockSkew` padrão, ou `ValidateLifetime=false` | F: vencido há 2 min | Vermelhas, as duas (Tarefa 7). **Sem o `ClockSkew`:** 3 — `AValidacao_…` («Expected 30s, but found 5m.») e os casos `vencido há 2 min` e `nbf 2 min no futuro` (esperado `401`, veio `400`). **`ValidateLifetime = false`:** 4 — `AValidacao_…` («Expected parametros.ValidateLifetime to be True, but found False.»), `vencido há 2 min`, `nbf 2 min no futuro` e `sem exp`. Os casos do `nbf` e do `exp` ausente não estavam na previsão |
| Tirar a checagem do `azp`, ou aceitar qualquer `azp` com a lista vazia | F: `azp` fora da lista; P: lista vazia com token válido | **Tirar só a checagem do `azp`: não consta do relatório da Tarefa 8.** O que consta: sem a linha do `OnTokenValidated` inteira, 11 vermelhos — os nove casos de forma, entre eles `azp fora da lista`, `azp ausente` e `azp vazio`, mais `TokenDoClientDeDemonstracao_EmProducaoLeva401` e `ListaDeClientsVazia_SobeAvisaERecusaTokenValido` (esperado `401`, veio `400`). **Lista vazia aceitando qualquer `azp`:** `ListaVazia_RecusaTodoToken` («Expected Recusar(permitidos: []) null to contain "azp"») e `ListaDeClientsVazia_SobeAvisaERecusaTokenValido` (esperado `401`, veio `400`) |
| Ler a forma pelos claims, e não pelo JSON (aceita o array de um elemento) | F: `typ` em array. Para o `azp`, a mutação é equivalente e **não foi executada**: a biblioteca recusa, ao ler o token, o `azp` que não é texto, e o caso `azp` em array leva `401` com ou sem a checagem | Vermelha para o `typ` (Tarefa 8, com `Texto` devolvendo o elemento de um array de um): o caso `typ em array` por HTTP (esperado `401`, veio `400`) e `TypEmArrayDeUmElemento_ERecusado` («Expected Recusar(payload => payload["typ"] = TypEmArray) null to contain "typ"»). Sob a mesma mutação, o caso `azp em array de um` continuou `401` — confirmação indireta da equivalência |
| Aceitar o demo fora de Development (tirar a recusa do `ValidateOnStart`) | P | Vermelha. P (Tarefa 8): `ClientDeDemonstracaoNaLista_ASubidaFalha` («Expected falha not to be null because a subida precisava falhar com esta configuração»). Na Tarefa 6, sobre a option: `ClientDeDemonstracaoForaDeDevelopment_…`, em Production e em Staging («Expected a OptionsValidationException to be thrown, but no exception was thrown.»); e, com `!IsProduction()` no lugar de `IsDevelopment()`, o caso de Staging |
| Tirar a checagem do `typ`, ou a do `sub`; `TryParse` no lugar de `TryParseExact("D")` | F: `typ` = `ID`; sem `sub`; `sub` no formato `N` | Vermelhas (Tarefa 8). **Sem o `typ`:** 7 — os casos `typ = ID com a audiência certa`, `typ em array` e `sem typ` por HTTP, `TypDeIdToken_ERecusado`, `TypEmArrayDeUmElemento_ERecusado`, `TypAusente_ERecusado` e, a mais, `OMotivo_NuncaCarregaOValorRecusado` («Expected … null to contain "typ"»). **Sem o `sub`:** 9 — `sem sub`, `sub que não é GUID` e `sub no formato N` por HTTP, os cinco casos de `SubForaDoFormatoD_ERecusado` e `OMotivo_NuncaCarregaOValorRecusado`. **`Guid.TryParse`:** vista vermelha no código **anterior** à correção do `sub` (`sub no formato N` por HTTP, e `SubForaDoFormatoD_ERecusado` nos casos `N`, `B` e do espaço); **não refeita sobre o código commitado** — equivalente por raciocínio, não por execução. Sobre o código commitado (`c608aa8`): sem a ida e volta, 6 vermelhos (os três textos com `0x` e `+` e o do espaço, na forma; `sub com prefixo 0x` e `sub com sinal` por HTTP); com `Ordinal` na ida e volta, `SubEmMaiusculasNoFormatoD_EAceito` («…to be null, but found "sub ausente ou fora do formato de GUID"») |
| `SymmetricSecurityKey` de volta em produção | Arquitetura, e só ela | Vermelha (Tarefa 7, com `IssuerSigningKey = new SymmetricSecurityKey(…)`): `NenhumaCamadaDeProducaoUsaChaveSimetrica` («Violação de arquitetura — IdentityGateway.Api: … Tipos violadores: - IdentityGateway.Api.Authentication.ValidacaoDoAccessToken»). **Não foi só a arquitetura:** o funcional `AsChaves_VemSoDosMetadados` também ficou vermelho. A receita HS256 antiga continuou `401` |
| `IssuerSigningKey`, `IssuerSigningKeys`, `SignatureValidator` ou `IssuerSigningKeyResolver` fixos | Configuração: opções resolvidas em execução | Vermelhas, as quatro (Tarefa 7), todas em `AsChaves_VemSoDosMetadados`: «Expected parametros.IssuerSigningKey to be null, but found Microsoft.IdentityModel.Tokens.SymmetricSecurityKey…»; «Expected (parametros.IssuerSigningKeys ?? []) to be empty, but found at least one item…»; «Expected parametros.SignatureValidator to be null, but found …SignatureValidator» — e, com ele, 23 vermelhos, porque todo token válido vira `401` —; «Expected parametros.IssuerSigningKeyResolver to be null, but found …IssuerSigningKeyResolver» |
| `RequireHttpsMetadata=false` fixo | P: opções resolvidas em `Production` | Vermelha. P (Tarefa 8): `OpcoesResolvidas_ExigemHttpsNosMetadadosComPrazoDe5s` («Expected jwt.RequireHttpsMetadata to be True, but found False»). Na Tarefa 6, sobre a option: `RequireHttpsMetadata_SegueOAllowInsecureHttp` e `IssuerEMetadadosNaConfiguracao_SaoIgnorados` |
| A guarda `HttpSoEmDesenvolvimento` sempre verdadeira | P: a subida com `BaseUrl` `http` deixa de falhar | **Executada e vista vermelha** (Tarefa 8) — na fatia C, ela tinha ficado sem prova. `TransporteEmHttpComAllowInsecureHttp_ASubidaFalha` e `EnderecoPublicoEmHttp_ASubidaFalha` («Expected falha not to be null because a subida precisava falhar com esta configuração», nos dois); o controle `ConfiguracaoDeProducaoValida_SobeEOLiveResponde` continuou verde. Aplicada com uma alternativa sempre verdadeira na condição (`ambiente.EnvironmentName.Length >= 0`), porque o `return true;` do plano não compila (`CS0162`). O ambiente de execução não recusou rodá-la. Os testes de `KeycloakAdminOptionsTests`, na integração, sob esta mutação, não constam do relatório da Tarefa 8 |
| `IncludeErrorDetails=true` | Configuração: opções resolvidas; P: `error_description` no `WWW-Authenticate` | Vermelha. Tarefa 7: 16 — `AResposta_NaoDetalhaOErro…` («Expected jwt.IncludeErrorDetails to be False because o WWW-Authenticate ecoaria o iss e o aud recusados, but found True.») e 15 dos 16 casos recusados («Expected resposta.Headers.WwwAuthenticate.ToString() to be the same string because o motivo da recusa fica no log, não na resposta, but they differ at index 6»); o caso `Bearer vazio` não chega à validação. Tarefa 8, com o host em Production: 32, entre eles `AudienciaErrada_401SemDetalheDoErroTambemEmProducao` e `OpcoesResolvidas_…` |
| `BackchannelTimeout` padrão | P: metadados frios passam de ~7 s | Vermelha (Tarefa 8): `MetadadosFrios_401EmPoucosSegundosComOAvisoESemOTokenNoLog` («Expected relogio.Elapsed to be less than 7s, but found 1m, 49ms…»), `OpcoesResolvidas_ExigemHttpsNosMetadadosComPrazoDe5s` e `OsMetadados_VemPeloEnderecoDeTransporteComPrazoCurto` («Expected 5s, but found 1m»). Com o código certo, o teste dos metadados frios leva 5,3 s, contra o teto de 7 s |
| Tirar o log do `OnAuthenticationFailed`, ou logar o token | P: metadados frios sem o `Warning`; vazamento: o token no log | Vermelhas. **Sem o aviso** (Tarefa 8, sem a chamada `ChavesIndisponiveis`): `MetadadosFrios_…` («Expected api.Logs.Eventos … to have an item matching …"indisponíveis"…») e `AvisoDeChaveNoLogTests` («Expected logs.Eventos.Count(EhOAvisoDeChave) to be 1 …, but found 0»). **Token no log da falha** (o cabeçalho `Authorization` junto do tipo): na Tarefa 8, `MetadadosFrios_…` — a mensagem inteira não consta do relatório, porque a saída trazia o token de teste; na Tarefa 9, os casos `vencido`, `audiência errada` e `assinatura inválida` do teste de vazamento («Expected logs.Any(…) to be False because o e-mail em base64url (alinhamento 0) não pode aparecer no log, but found True.»). **`Authorization` no log do pedido** (Tarefa 9): os seis casos, com a mesma mensagem |
| `MapInboundClaims=true` | F: todos os `202` | Vermelha (Tarefa 7): 17 — `AResposta_…` («Expected jwt.MapInboundClaims to be False because …, but found True.»), os 3 casos aceitos e todo teste autenticado que esperava `202`, `200`, `400`, `404` ou `409` («…but found Forbidden {403}.») |
| Tirar a `FallbackPolicy` | F: caminho não mapeado sem token deixa de levar `401` | Vermelha (Tarefa 7): só `CaminhoNaoMapeado_SemToken401EComToken404` («Expected semToken.StatusCode to be Unauthorized {401}, but found NotFound {404}.»). `TodoEndpoint_TemPolicyNomeadaOuAnonimatoDeclarado` continuou verde, como previsto |
| `aud` num scope default do realm, ou `gateway-*` nos defaults do service account | Regras do realm; K: o token do client de device flow do fixture sem `identity-gateway-api`, e a forma do token do service account | Vermelhas. **`gateway-api` em `defaultDefaultClientScopes`:** a regra `ScopesDaGatewayExistemEForaDosDefaultsDoRealm` («Expected noRealm {"gateway-api"} to not contain {…}, but found {"gateway-api"}», Tarefas 2 e 9); K (Tarefa 9): `TokenDoClientDeDeviceFlowDoFixture_AzpAceitoSemAAudiencia_401`, na asserção da forma («Expected PayloadDoJwt.Audiencias(payload) {"identity-gateway-api", "account"} to not contain "identity-gateway-api".»). **`gateway-api` nos defaults do service account:** a regra `TodoClientDeclaraEscoposEFluxos` («defaultClientScopes to be a collection with 2 item(s), but {"basic","roles","gateway-api"}»); K: `TokenDoServiceAccount_…` e `Clients_FicaramComOsScopesDoJson` (Tarefa 3: «Expected PayloadDoJwt.Audiencias(payload) {"identity-gateway-api","realm-management"} to not contain "identity-gateway-api"») e `TokenDoServiceAccountDaGateway_401` (Tarefa 9, a mesma mensagem) |
| Tirar `CreateDefaultClientScopes` | Regra do realm; K: scopes embutidos presentes e `sub` no token | Vermelha. Regra (Tarefa 2): `CreateDefaultClientScopesLigado`, por `KeyNotFoundException` — **exceção, e não asserção**: com `"attributes": {}`, a regra falha ao ler a chave. K (Tarefa 3), por asserção: o import **não** recusou o JSON, o fixture subiu, e ficaram vermelhos `ScopesEmbutidos_ExistemNoRealm` («Expected scopes {"gateway-tenant","offline_access","gateway-roles","gateway-api"} to contain {"basic","roles","acr",...}»), `ScopesDaGateway_NaoSaoDefaultNemOpcionalDoRealm`, `Clients_FicaramComOsScopesDoJson` («Expected defaults {empty} to contain "basic"») e `TokenDoServiceAccount_…`. O token sem `sub` foi visto na mutação do client de demonstração sem `basic` (Tarefa 5), por `KeyNotFoundException` |
| `fullScopeAllowed=true` no demo | Regra do realm; K: `roles` ⊆ catálogo, com o usuário que tem `default-roles-*` | Vermelha. Regra (Tarefa 2): `ClientDeDemonstracaoSoComDeviceFlow` («fullScopeAllowed to be False because com true, default-roles-*...»). K (Tarefa 5): `TokenDoTenantAdmin_…`, `TokenDoPlatformAdmin_…`, `Roles_SoTrazOCatalogo_…` e `GrupoComTenantId_…` («Expected Roles(payload) to be equal to {"tenant-admin"}, but {"tenant-admin", "default-roles-identity-gateway"} contains 1 item(s) too many»). **`UsuarioSemPapelDoCatalogo_TokenSaiSemOClaimRoles` continuou verde**, ao contrário do que o plano previa: item 6 de "O que mudou em relação ao plano" |
| `revokeRefreshToken` falso, ou `refreshTokenMaxReuse: 1` | Regra do realm; K: refresh token reusado recusado | Vermelhas. **`revokeRefreshToken` falso:** a regra `TemposEProtecoesDoRealm` («revokeRefreshToken to be True, but found False», Tarefa 2); K: `Realm_GravouOsTemposEARotacaoDoRefresh` (Tarefa 3) e `RefreshToken_RotacionaEOReusoDerrubaASessaoDoClient` («Expected a FalhaDoHarnessException to be thrown, but no exception was thrown.», Tarefa 5: o reuso é aceito). **`refreshTokenMaxReuse: 1`:** K, o mesmo teste e a mesma mensagem (Tarefa 5: o primeiro reuso é aceito); **a regra estática sob essa mutação não consta do relatório da Tarefa 2** |
| Tirar o scope `offline_access` do JSON | Regra do realm; K: papel padrão `[manage-account, view-profile]` | Vermelha. Regra (Tarefa 2): `OfflineAccessDeclaradoEForaDeTodoClient`, por `InvalidOperationException: Sequence contains no matching element` — **exceção, e não asserção**. K (Tarefa 3), por asserção: `PapelPadrao_SoComOsPapeisDaConta` («Expected compostos to be a collection with 2 item(s), but {"manage-account","offline_access","view-profile"}») e `ScopesDaGateway_NaoSaoDefaultNemOpcionalDoRealm`, porque o scope embutido volta como default ou opcional |
| One-shot sem o marcador, ou lendo o marcador com `--fields attributes` | CI: `convites --esperado 1` depois do `run`; ao vivo, no `igverif` | **Na CI, com o app como testemunha: não executada.** O job nunca rodou, e as mutações 1 e 2 da Tarefa 11 pediam recriar o `igverif`, o que o sistema de permissões negou (ver "Pendências"). **Ao vivo, no `igverif`, na Tarefa 10: vermelhas, pela contagem no mailpit.** Sem o marcador (`if false; then`): "convite enviado (o link vale 4 h)", e os e-mails foram de 1 para 2. Com `--fields attributes`: os e-mails foram a 3, e a regra `ConviteDoPlatformAdminNaoRecebeSenhaPorVariavelNemAtribuiPapel` ficou vermelha («Expected CamposComAttributes().IsMatch(compose) to be False because com --fields ... as travas ficam vacuosas, but found True.») |

**Demais mutações executadas nas tarefas**, além das da §5.3:

| Tarefa | Mutação | Resultado |
|---|---|---|
| 1 | Tag `26.7.3` na constante `ImagemDoKeycloak` do fixture | `ComposeEFixtureUsamAMesmaTagDoKeycloak`: «Expected Fixture() ... to contain "ImagemDoKeycloak = "quay.io/keycloak/keycloak:26.7.4"" because o teste de integracao precisa provar o mesmo Keycloak que o compose sobe.» |
| 2 | `"composite": true` no papel `reader` | `CatalogoDePapeisExatoENuncaComposto`: «Expected papeis {{ ...» (nenhum papel composto) |
| 2 | `offline_access` nos scope mappings do `gateway-roles` | `GatewayRolesEmiteSoOCatalogo`: «Expected Textos(mapeamentos[0], "roles") to be a collection with 4 item(s), but {..., "offline_access"}» |
| 2 | `"aggregate.attrs": "true"` no mapper do `gateway-tenant` | `GatewayTenantEmiteUmValorSoSemAgregar`: «aggregate.attrs ... to be the same string because agregando...» |
| 2 | `"id.token.claim": "true"` no mapper do `gateway-api` | `AudienciaDaGatewaySoNoScopeGatewayApi`: «config.GetProperty("id.token.claim") ... differ at index 0» |
| 2 | Client de demonstração sem a linha `"optionalClientScopes": []` | `TodoClientDeclaraEscoposEFluxos`: «ChavesObrigatoriasDoClient to contain only items matching Tem(...) because o client identity-gateway-demo declara as cinco chaves» |
| 2 | `"directAccessGrantsEnabled": true` no client de demonstração | `TodoClientDeclaraEscoposEFluxos`: «directAccessGrantsEnabled to be False because identity-gateway-demo: sem ROPC» |
| 2 | `email` nos `defaultClientScopes` do client de demonstração | `ClientDeDemonstracaoSoComDeviceFlow`: «defaultClientScopes ... 5 item(s), but {..., "email", ...}» |
| 2 | Client de demonstração sem `basic` | `ClientDeDemonstracaoSoComDeviceFlow`: «defaultClientScopes ... 5 item(s), but {"acr", ...}» |
| 2 | Device flow ligado no client `identity-gateway` | `SoOClientDeDemonstracaoTemDeviceFlow`: «comDeviceFlow to be equal to {"identity-gateway-demo"}, but ... 1 item(s) too many» |
| 2 | Um grupo com `tenant_id` na raiz do realm | `RealmSemGrupos`: «realm.TryGetProperty("groups") to be False, but found True» |
| 2 | `"accessTokenLifespan": 3600` | `TemposEProtecoesDoRealm`: «accessTokenLifespan to be 300 ... but found 3600» |
| 2 | `tenant-admin` nos `realmRoles` do usuário do bootstrap | `PlatformAdminDoBootstrapNasceSemSenhaESoComOPapel`: «realmRoles to be equal to {"platform-admin"}, but ... 1 item(s) too many». Com reimport, na Tarefa 3: `PlatformAdminDoBootstrap_…` («Expected Nomes(papeis.GetProperty("realmMappings")) to be equal to {"platform-admin"}, but {"tenant-admin","platform-admin"} contains 1 item(s) too many») |
| 2 | `credentials` no usuário do bootstrap | `NenhumaChaveDeCredencial` («violacoes ... {"$.users[1].credentials"}») e `PlatformAdminDoBootstrapNasceSemSenhaESoComOPapel` («ChavesQueOBootstrapNaoTem ... not have any items matching») |
| 2 | Uma chave `secret` dentro do JSON embutido do User Profile | `NenhumaChaveDeCredencial`: «violacoes ... {"$.kc.user.profile.config.attributes[4].secret"}» |
| 2 | A descrição do papel `offline_access` como chave de i18n | `TodoPlaceholderEPuro`: «violacoes ... {"$.roles.realm[4].description"}» |
| 3 | `fullScopeAllowed` falso no client `identity-gateway`, com reimport | `TokenDoServiceAccount_…` e, com `KeycloakRealTests` junto, `HealthCheck_ComKeycloakDePe_Healthy` e `EndpointDeOrganizations_NaoResponde404` — por `KeyNotFoundException`, porque o `resource_access` some: **exceção, e não asserção** |
| 4 | Harness: sem a soma de 5 s do `slow_down` | `SlowDown_SomaCincoSegundosAoIntervaloESegue`: «Expected esperas to be equal to {5s, 10s, 10s}, but {5s, 5s, 5s} differs at index 1.» |
| 4 | Harness: `access_denied` e `expired_token` tratados como pendentes | 4 — `ErroDefinitivoNoToken_FalhaNaHoraSemRepetir` (2 casos), `ConsentimentoNegado_FalhaNaHora`, `DeviceCodeExpirado_FalhaNaHora`: «Expected a FalhaDoHarnessException to be thrown, but no exception was thrown.» |
| 4 | Harness: os valores dos campos na mensagem da exceção | `PaginaDesconhecida_LancaComTituloFormularioENomesDosCamposSemValores`: «Expected falha.Message "...formulário "kc-otp-login-form" com os campos [VALOR-SECRETO, ]." to contain "credentialId".» |
| 4 | Harness: o cookie vencido mantido | `Cookies_SecureVoltaPorHttpEVencidoSome`: «Expected cookiesRecebidos to be equal to {null, "AUTH_SESSION_ID=abc", null}, but {null, "AUTH_SESSION_ID=abc", "AUTH_SESSION_ID="} differs at index 2.» |
| 4 | Harness: sem o `Host` público no pedido | `EnderecoPublico_EDiscadoNoTransporteComOHostPublico`: «Expected host to be "keycloak.test:8081", but found null.» |
| 4 | Harness: sem o ramo da página de erro | `PaginaDeErroDoKeycloak_LancaNaHora`: «...to contain "página de erro".» |
| 4 | Harness: a renovação repetida | `Renovacao_Recusada_LancaSemRepetir`: «Expected transporte.Chamadas to be 1, but found 2.» |
| 4 | Harness: o login reenviado depois de recusado | `LoginRecusado_FalhaNaPrimeiraVoltaDoFormularioSemInsistir`: «Expected falha.Message "device flow: mais de 12 páginas sem chegar ao fim." to contain "recusados".» |
| 5 | Client de demonstração sem `basic`, com reimport | `TokenDoTenantAdmin_…`, `UsuarioSemPapelDoCatalogo_…` e `RefreshToken_…`, por `KeyNotFoundException` (o token sai sem `sub`): exceção, e não asserção |
| 5 | Client de demonstração com `email`, com reimport | `TokenDoTenantAdmin_…`: «ClaimsQueNaoPodemSair.Where(...) to be empty, but found at least one item {"email"}» |
| 5 | Client de demonstração sem `gateway-api`, com reimport | `TokenDoPlatformAdmin_TemOPapelENaoTemTenant` («Expected PayloadDoJwt.Audiencias(payload) to be equal to {"identity-gateway-api"}, but found empty collection.») e `TokenDoTenantAdmin_TemAFormaQueAGatewayValida` (`KeyNotFoundException`, sem `aud`). Na primeira execução, o `sed` apagou `gateway-tenant` por engano; a mutação foi refeita, e a errada mostrou que tirar o `gateway-tenant` também derruba os testes que leem o `tenant_id` |
| 5 | `multivalued: "false"` no mapper do `gateway-roles`, com reimport | `TokenDoTenantAdmin_…`: «Expected payload.GetProperty("roles").ValueKind to be Array, but found String»; em `TokenDoPlatformAdmin_…`, `Roles_…` e `GrupoComTenantId_…`, `InvalidOperationException` |
| 6 | `MetadataAddress` montado sobre o emissor público | `MetadataAddress_SaiDoBaseUrlInternoENuncaDoPublico`, `IssuerEMetadadosNaConfiguracao_SaoIgnorados` e `MetadataAddress_PreservaOPrefixoDeCaminhoDoBaseUrl`: «Expected validacao.MetadataAddress to be the same string / start with ...» |
| 6 | A seção `Auth`, com a lista de `azp`, no `appsettings.json` base | `ListaDeClientsPermitidosSoNoAppsettingsDeDevelopment`: «Expected ...TryGetProperty("Auth", out _) to be False because nem a lista, nem a secao..., but found True.» |
| 7 | Sem o result handler do Problem Details | 18, depois da correção dos parênteses: `SemToken_OCorpoEProblemDetails…`, `TokenValidoSemOPapel_…` e os 16 casos recusados («Expected (resposta.Content.Headers.ContentType?.MediaType) to be "application/problem+json", but found null.»). Antes da correção: só 2, e por exceção — item 2 de "O que mudou em relação ao plano" |
| 7 | `/health/live` sem `AllowAnonymous()` | `OsHealthChecks_ContinuamAbertos` («Expected live.StatusCode to be OK {200}, but found Unauthorized {401}.») e `TodoEndpoint_TemPolicyNomeadaOuAnonimatoDeclarado` («...found at least one item {"Health checks"}.») |
| 7 | `POST /tenants` sem a policy `PlatformAdmin` | 4 — `AsRotasDeTenant_ExigemPlatformAdmin` («Expected policies to contain 2 item(s), but found 1: {"PlatformAdmin"}.»), `TodoEndpoint_…`, `SegurancaTests.TokenValidoSemOPapel_…` e `RegistroDeTenantTests.TokenSemOPapel_Responde403` |
| 7 | A chave da partição do limitador lendo `"nameid"` | `ChaveDaParticaoTests.ComOClaimCurto_ParticionaPeloUsuario`: «Expected chave to be the same string, but they differ at index 0» |
| 7 | Sem `preserveStaticLogger: true` | `LogsPorHostTests.CadaHost_SoVeOsPropriosLogs` («Expected logsDaFactory {empty} to have an item matching ...») e `AvisoDeChaveNoLogTests` |
| 7 | `IdentityModelEventSource.ShowPII = true` | Arquitetura, `NenhumaCamadaLigaPiiDaBibliotecaDeIdentidade`: «Violação de arquitetura — IdentityGateway.Api: ShowPII levaria tokens e claims para o log.» |
| 7 | Um `IClaimsTransformation` registrado | Arquitetura, `Api_NaoTransformaClaims` («Expected transformadores to be empty because os claims vêm só do token (ADR-004), but found at least one item {"Mutacao"}.»); funcional, `NinguemTransformaClaimsDepoisDaValidacao` |
| 7 | Um segundo esquema de autenticação | `HaUmEsquemaDeAutenticacaoSo`: «Expected todos.Select(esquema => esquema.Name) to be equal to {"Bearer"}, but {"Bearer", "Outro"} contains 1 item(s) too many.» |
| 7 | `AoFalhar` sem o limite `PodeAvisar()` | `TokenDeKidDesconhecido_…`: «Expected logs.Eventos.Count(EhOAvisoDeChave) to be 1 because o aviso é limitado a um por intervalo, but found 3.» |
| 7 | `EhFalhaDeChaveOuDeMetadados` sempre verdadeiro | 9 — 8 dos 9 casos de `SoAFaltaDeChave_EFalhaDeChave` («Expected ValidacaoDoAccessToken.EhFalhaDeChaveOuDeMetadados(excecao) to be False, but found True.») e `TokenDeKidDesconhecido_…` |
| 7 | `RoleClaimType`, `NameClaimType` e `ValidAlgorithms` | **Não executadas**, por indicação do plano; o motivo não consta do relatório da Tarefa 7 |
| 8 | O `azp` comparado sem diferenciar caixa | `AzpComOutraCaixa_ERecusado`: «Expected Recusar(payload => payload["azp"] = "Identity-Gateway-Demo") null to contain "azp"» |
| 8 | Sem o `AddHostedService` do `AvisoDeClientsPermitidos` | `ListaDeClientsVazia_SobeAvisaERecusaTokenValido`: «Expected cenario.Api.Logs.Eventos {…} to have an item matching …"AllowedClients"…» — com o coletor vivo e o `401` mantido |
| 8 | A mais: a `ApiEmProducaoFactory` sem a máscara do Serilog | `ListaDeClientsVazia_…` e `MetadadosFrios_…`: «Expected cenario.Api.Logs.Eventos {empty} to have an item matching ...» |
| 9 | Um mapper a mais no scope `gateway-tenant`, com reimport | `PonteDeContrato_OsClaimsDoTokenRealTemOsTiposDosDoEmissorDeTeste`: «Expected tiposDoForjado to be a dictionary with 15 item(s), but it misses key(s) {"extra"}»; a regra `GatewayTenantEmiteUmValorSoSemAgregar` também caiu |
| 9 | A mais, três controles de presença: o coletor do canal vazio; o exportador de spans trocado; sem o nível `Debug` | Os seis casos de vazamento, em cada um: «Expected DoPedido(logs, rota) to be True because o log do pedido precisa ter sido capturado, but found False.», e as equivalentes do span e do nível `Debug` |
| 9 | A mais: o detector de base64 sem o deslocamento; o `aud` como array no emissor de teste; o `Authorization` numa tag do span | `FormasEmBase64_SaoAchadasEmQualquerPosicaoDoPayload` (os três casos); `PonteDeContrato_…` («Expected tiposDoForjado[aud] to equal JsonValueKind.String {value: 3} by value, but found JsonValueKind.Array {value: 2}.»); os seis casos de vazamento («...não pode aparecer nos spans, but found True.») |
| 10 | Ao vivo: o envio antes da gravação do marcador | Com o SMTP parado, o envio falha com saída `1`, e o marcador **não** é regravado: as duas leituras dão `2026-10-02T12:44:24Z`. Com o código certo, a segunda leitura (`12:44:24Z`) é posterior à primeira (`12:43:50Z`) |
| 10 | Regras do compose: a porta da `api` sem `127.0.0.1`; o Jaeger com `4317:4317`; uma porta a mais; só `"8080"`; `'8025:8025'` no mailpit | `DependenciasComDadoPessoalPublicamSoEmLocalhost`, nas cinco: «Expected PortasPublicadas(compose)[0] to be the same string, but they differ at index 0» (e nos índices 7 e 2); na porta a mais, «to be a collection with 8 item(s), but {... "9999:9999" ...}» |
| 10 | Regras do compose: a `api` sem a dependência do one-shot; a dependência movida para o `migrate` | `ApiSoSobeDepoisDoConviteDoPlatformAdmin`: «a api depende de platform-admin-invite com service_completed_successfully, but found False.» A segunda foi refeita com a expressão ancorada: a primeira aplicação tinha caído dentro do `depends_on` da própria `api`, e o teste passou, com razão |
| 10 | Regras do compose: o padrão do e-mail com maiúscula; a tag `26.7.3` no one-shot; `--fields id,attributes` | `PadraoDoEmailDoPlatformAdminEMinusculo` («Expected email to be the same string, but they differ at index 0»); `ComposeEFixtureUsamAMesmaTagDoKeycloak` («Expected imagens to contain a single item ... found {26.7.4, 26.7.3}»); `ConviteDoPlatformAdminNaoRecebeSenhaPorVariavelNemAtribuiPapel` («Expected CamposComAttributes().IsMatch(compose) to be False ... but found True.») |
| 10 | Regras do compose: sem o `test -n` do e-mail no entrypoint; `Jwt__SigningKey` de volta; `lifespan=86400`; `KC_CLI_PASSWORD` declarada no one-shot | `EntrypointDoKeycloakRecusaEmailVazioOuComMaiusculas`, `ComposeNaoTemMaisAChaveJwt` e, nas duas últimas, `ConviteDoPlatformAdminNaoRecebeSenhaPorVariavelNemAtribuiPapel`. **A mensagem de asserção dessas quatro não consta do relatório da Tarefa 10**, só o nome do teste |
| 11 | O padrão do e-mail trocado no app | `PadraoDoEmailDoPlatformAdminEOMesmoNoComposeNoAppENoReadme`: «Expected noApp.Groups["email"].Value to be the same string, but they differ at index 0: ... "admin@identity-gateway.local"» |
| 11 | Um `#:package` declarado no app | `FerramentasNaoDeclaramPacotes`: «Did not expect File.ReadAllText(ferramenta) "#:package Humanizer@2.14.1 ..." to contain #:package» |
| 11 | O app sem `#:property PublishAot=false` | `AppDaJornadaUsaABibliotecaDoHarnessESemAot`: «Expected app "#:project ../tests/IdentityGateway.Testing.Keycloak ..." to contain #:property PublishAot=false»; e o build do app falha com `IL2026` e `IL3050` |
| 11 | O one-shot sem o marcador, e o marcador lido com `--fields attributes`, com o app como testemunha | **Não executadas** — ver "Pendências" |

## Verificação ao vivo

**Antes da execução, ao escrever o plano (2026-10-01), contra um Keycloak 26.7.4 com mailpit v1.31.3, num ambiente
descartável:**

- O realm final importa, e a leitura pelo master confere: scopes embutidos presentes, `gateway-*` fora dos defaults
  do realm, papel padrão `[manage-account, view-profile]`, o client de demonstração com os cinco scopes e o service
  account com `basic` e `roles`.
- O script do one-shot roda como está: a primeira execução envia um e-mail, com link de `exp − iat = 14400`; a
  segunda sai `0` com "convite já enviado em …" e continua um e-mail; `REENVIAR=1` envia outro; e o e-mail
  configurado em maiúsculas é comparado em minúsculas.
- O harness de login roda como está: o link de ações em 4 páginas; o device flow em 5,2 s; o platform-admin, sem
  Organization no realm, entra em **1** passo de login, e um usuário com Organization, em **2**; um segundo login na
  mesma instância, 0 passos (cookie de SSO); a renovação devolve um refresh token novo; e **o refresh token reusado
  leva `invalid_grant` e, depois dele, o novo também** — o reuso derruba a sessão do client.
- **O spike do marcador** (fim da §3 do design): `platformAdminInviteSentAt` como atributo do realm funciona — o
  `kcadm update -s` só acrescenta o atributo, a segunda e a terceira execução saem `0` sem e-mail, o marcador
  sobrevive a `stop`/`start` e não aparece em nenhum token, no userinfo nem no discovery.

**Na execução — o one-shot e o compose (Tarefa 10), no projeto isolado `igverif`** (`docker compose -p igverif …`,
volumes próprios, sem tocar os do projeto padrão):

| Passo | Horário | Resultado |
|---|---|---|
| `docker compose -p igverif up -d --build --wait api` | 09:38–09:40 (horário local, UTC−3, em toda a tabela) | `api` `Healthy`; `/health/ready` → `Healthy`; o one-shot `exited` com código `0`, e o log dele só com "convite enviado (o link vale 4 h)" |
| E-mails para o `PLATFORM_ADMIN_EMAIL` no mailpit | 09:40 | 1 |
| `docker compose -p igverif run --rm --no-deps platform-admin-invite`, com o convite pendente | 09:40 | Saída `0`, "convite já enviado em 2026-10-02T12:39:50Z; nada a fazer"; continua 1 e-mail |
| Travas do usuário (papel de realm a mais, `tenant_id`, grupo, papel de client) | 09:40–09:41 | Saída `1` nas quatro, com "a conta do platform-admin não tem a forma do bootstrap"; continua 1 e-mail |
| Convite já concluído | 09:41 | Saída `1`, "o convite já foi concluído; não há o que reenviar" |
| Volume antigo (realm sem o scope `gateway-api`) | 09:41 | Simulado com o scope renomeado: saída `1`, "realm anterior aos tokens do Keycloak (o import é IGNORE_EXISTING). Rode: docker compose down -v" |
| Senha do `master` errada | 09:41 | Saída `1`, "credencial do master recusada: o admin do compose foi alterado? Rode: docker compose down -v" |
| Reenvio com o convite pendente (`-e REENVIAR=1`) | 09:42 | Saída `0`, "os links anteriores ainda não usados continuam válidos até expirar" e "convite enviado (o link vale 4 h)"; 2 e-mails |
| `PLATFORM_ADMIN_EMAIL` vazio e com maiúsculas | 09:43 | O `keycloak` sai com `1` nos dois casos, em 2,8 s e 2,3 s, contando a criação do contêiner — o plano dizia menos de um segundo. A mensagem não consta do relatório: a saída foi suprimida |
| Reenvio depois de `down` e `up` (`-e REENVIAR=1`) | 09:42–09:43 | Na segunda subida, o one-shot diz "convite já enviado em 2026-10-02T12:41:57Z; nada a fazer", e o mailpit, que não tem volume, volta com 0 e-mails; o reenvio sai com `0` e leva a 1 e-mail |
| Mutações ao vivo do marcador (sem o marcador; `--fields attributes`; envio antes da gravação) | o horário não consta do relatório (os marcadores da terceira são de 12:43:50Z e 12:44:24Z) | Vermelhas, as três — nas tabelas de "Prova por mutação" |
| `docker compose -p igverif down -v` | o horário não consta do relatório | Contêineres e volumes `igverif_gateway-keys` e `igverif_postgres-data` removidos; os três `identitygateway_*` intactos. **Isto é o fim da Tarefa 10; a Tarefa 11 subiu o `igverif` de novo e não pôde derrubá-lo** |

**Na execução — a jornada do job `Compose` rodada localmente (Tarefa 11)**, no mesmo projeto isolado, com os mesmos
comandos e na mesma ordem do job. Horários em UTC, como no relatório da tarefa; a máquina está em UTC−3:

| Fase | Horário | Resultado |
|---|---|---|
| `docker compose -p igverif up -d --build --wait --wait-timeout 300 api` | 12:53:47Z–12:55:00Z | Saída `0`; às 12:55:10Z, `/health/ready` → `Healthy` |
| `convites --esperado 1`, o `run` do one-shot e `convites --esperado 1` | entre 12:55:10Z e 12:55:35Z | "convites do platform-admin no mailpit: exatamente 1" (3,1 s, saída `0`); o `run` diz "convite já enviado em 2026-10-02T12:54:51Z; nada a fazer" (saída `0`); de novo "exatamente 1" (saída `0`) |
| `jornada` | 12:55:35Z–12:55:49Z | Saída `0`, na primeira execução e sem ajuste no app. O convite concluído pelo link do e-mail (0,5 s); o token pelo device flow (5,3 s) — **o número de passos do login não consta do relatório**; a receita HS256 antiga → `401`; `POST /tenants` → `202`; `Active` em 6,1 s; o convite do admin do tenant no mailpit, com um link que abre |
| `antes-de-parar`, `stop`, `com-keycloak-parado`, `start`, `depois-de-voltar` | 12:55:57Z–12:56:53Z | Saída `0` nas três fases. A renovação do token e um `GET` autenticado; com o Keycloak parado (pré-condição "o Keycloak não responde", 2,1 s), `202`, e o tenant `Pending` por 15 s; o Keycloak voltou a `healthy` em menos de 20 s; a renovação, e `Active` em 15,1 s |
| Segunda subida (`down`, `up --wait api`) | 12:57:05Z–12:58:01Z | `up` com saída `0`; o one-shot diz "convite já enviado em 2026-10-02T12:54:51Z; nada a fazer"; `convites --esperado 0` → "exatamente 0" |
| Segredos na saída do app | — | `eyJ`, `action-token?key=`, `user_code` e `device_code`: 0 ocorrências na saída capturada das quatro partes |
| As receitas `curl` do README, sem imprimir valor nenhum | 13:00:00Z | O `POST .../auth/device` devolve o código, o endereço público, `interval` 5 e `expires_in` 300; a renovação devolve tokens novos, com `expires_in` 300 e `refresh_expires_in` 1800; o `GET` de um tenant inexistente responde `404` com o token e `401` sem ele; o refresh token reusado leva `invalid_grant`. **O login e o consentimento no navegador não foram feitos à mão** |
| Tempo total do job na CI | — | **Não observado: o job nunca rodou.** A estimativa do design é de cerca de 5 min 30 s |

**Não verificado**, e registrado na §19 da v2.7: o cache de metadados além de ~9 minutos com o Keycloak fora; se o
mailpit valida o `Host` contra *DNS rebinding*; e, herdado da fatia C, se a troca de e-mail pela account console
exige verificação na 26.7.4.

**Não verificado na execução:**

- **O job `Compose` da CI, o passo novo do job `build` e o app em Linux nunca rodaram** — inclusive o ramo que
  aplica a permissão 600 ao arquivo de estado (`File.SetUnixFileMode`). O `ci.yml` passou no `actionlint` 1.7.12, e
  a mesma sequência de comandos rodou localmente, em Windows. A primeira execução real é a do PR.
- **O `0` do `grep` de token no log da `api` não é prova.** Ele veio de um contêiner recriado pelo `down` e `up` da
  segunda subida, e não cobre nenhum pedido da jornada: nem a recusa da receita HS256, nem os `POST /tenants`, nem o
  login e o device flow no log do Keycloak. O que os contêineres parados mostram — `eyJ`, `action-token?key=` e
  `user_code=` com 0 ocorrências em `api`, `keycloak` e `platform-admin-invite` — vale só a partir da recriação, com
  um controle positivo parcial: um `GET` com token real (`404`) e um sem token (`401`). O `ci.yml` não tem essa
  checagem.
- **As mutações 1 e 2 da Tarefa 11**, com o app como testemunha (ver "Pendências").
- **O login e o consentimento da demonstração do README, à mão, no navegador.** As receitas `curl` foram conferidas;
  as páginas do Keycloak foram percorridas só pelo harness.
- **O `500` que o Kestrel daria a um `iss` com caractere de controle**, se o detalhe do erro estivesse ligado: o
  `TestServer` dos testes não valida cabeçalho como o Kestrel, e o caso reprova pelo cabeçalho, com `401`.
- **`PLATFORM_ADMIN_EMAIL` com `+`** na busca do one-shot por e-mail.

## Decisões tomadas durante a execução

| Decisão | Custo se errado |
|---|---|
| Executar na branch `feat/tokens-keycloak` do checkout principal, sem worktree, com um implementador e um revisor por tarefa | Nenhum além de trocar de diretório |
| **Corrigir o `sub` contra o texto do plano** (Tarefa 8): a decisão do design é "`sub` GUID no formato `D`", e o código do plano não a cumpria. Ida e volta com `OrdinalIgnoreCase` | Trocar uma comparação e um caso de teste |
| **O `sub` em maiúsculas continua aceito**: maiúsculas são formato `D`, e recusá-las seria decisão nova, fora do design. **Pendente de confirmação do autor** | Trocar `OrdinalIgnoreCase` por `Ordinal` e inverter o controle `SubEmMaiusculasNoFormatoD_EAceito` |
| **O `tenantId` da rota aceita qualquer formato de GUID; o claim, só o formato `D`** (a leitura da §5.2 do design; D2). **Pendente de confirmação do autor** — a divergência está em "Pendências" | Trocar um `Guid.TryParse` por `TryParseExact` e um controle positivo por caso de `403` |
| **A v2.7 não nomeia a "fatia E"**: a §9 do design cita a fatia, a decisão D-l proíbe, e a v2.7 segue a D-l. **Pendente de confirmação do autor** | Trocar "chega com a auditoria" por "na fatia E" em dois trechos da v2.7 |
| **O limitador de requisições continua depois da autorização** (`Program.cs`): mover é decisão de produto, porque os `401` passariam a consumir cota por IP. O limite fica para o handoff da D2. **Pendente de confirmação do autor** | Uma linha no `Program.cs` e ajuste de testes |
| A máscara do Serilog nas duas factories de teste, em vez de trocar o registro do logger (Tarefas 7 e 8) | Só o índice 9 do `WriteTo` é mascarado: outro `UseSetting` aninhado sob um item de lista do Serilog cai na mesma armadilha |
| A exceção no `.gitignore` para a pasta `Logs/` dos testes funcionais, em vez de `git add -f` (Tarefa 7) | Nenhum conhecido; o `git add -f` deixaria a armadilha para o próximo arquivo da pasta |
| O teste de vazamento afirma a ausência por booleano (Tarefa 9), para a falha não imprimir o token | A mensagem de falha diz a forma do segredo e o lugar, mas não mostra a linha que vazou |
| Mutações que não compilam como o plano as escreveu foram aplicadas de forma equivalente, sem afrouxar o `TreatWarningsAsErrors` (Tarefas 4, 7, 8 e 9) | Nenhum: o vermelho foi por asserção em todas |
| **A Tarefa 11 foi commitada sem as mutações 1 e 2**, quando a derrubada do `igverif` foi negada pelo sistema de permissões. Ninguém rodou o comando negado nem o contornou com outro projeto do compose | As duas mutações rodam depois, e o resultado entra neste handoff |

## Pendências

**Do autor, bloqueadas por permissão — a derrubada do `igverif` e duas provas por mutação:**

O estado do Docker, conferido só por leitura em 2026-10-02, 10:42 (horário local), com
`docker ps -a --format '{{.Names}}' | grep igverif` e `docker volume ls --format '{{.Name}}' | grep igverif`:

- **Nove contêineres `igverif-*`, todos parados:** `api` (saiu com 137), `keycloak` (143), `platform-admin-invite`,
  `keycloak-db`, `migrate`, `redis`, `postgres`, `gateway-keys` e `mailpit` (os sete com 0).
- **Dois volumes do projeto isolado:** `igverif_gateway-keys` e `igverif_postgres-data`.
- **Os três volumes do autor intactos:** `identitygateway_gateway-keys`, `identitygateway_postgres-data` e
  `identitygateway_seq-data`.

Nesta tarefa, nada foi derrubado, subido nem removido no Docker, fora os contêineres que os Testcontainers da suíte
criam e apagam sozinhos. O que falta, tudo na raiz do repositório e **sempre com `-p igverif`**:

1. **Mutação 1 — o one-shot sem o marcador, com o app como testemunha.** `docker compose -p igverif down -v`; trocar
   por `if false; then` a linha do `docker-compose.yml` que testa o marcador (a que começa com
   `if [ -n "$$marcador" ]`); `docker compose -p igverif up -d --build --wait --wait-timeout 300 api`;
   `dotnet run -c Release tools/jornada-compose.cs -- convites --esperado 1` (saída `0`);
   `docker compose -p igverif run --rm -T --no-deps platform-admin-invite`; e de novo `convites --esperado 1`.
   Vermelho esperado: o `run` diz "convite enviado (o link vale 4 h)", e o app, "esperados 1 convites, achados 2",
   com saída `10`. Reverter com `git restore docker-compose.yml` e conferir `git diff --stat` vazio.
2. **Mutação 2 — o marcador lido com `--fields attributes`.** O mesmo roteiro, com `--fields attributes` acrescentado
   ao `kc get "realms/$$realm"` que lê o marcador. O mesmo vermelho esperado.
3. **A checagem de token nos logs, no momento certo** — a que o `0` de hoje não prova. Com o `docker-compose.yml`
   restaurado e o ambiente recriado: rodar a fase `jornada` e, **antes de qualquer `down`**, procurar `eyJ`,
   `action-token?key=`, `user_code` e `device_code` no log de `api`, `keycloak` e `platform-admin-invite`, com
   controle positivo — o `POST /api/v1/tenants` e um `401` presentes no mesmo log da `api`. Se o autor quiser a prova
   permanente, é um passo no job `Compose`, antes do `down` da segunda subida, com `grep -q`, para não imprimir a
   linha achada.
4. **A derrubada, com a conferência dos volumes:** `docker compose -p igverif down -v` e
   `docker volume ls | grep -E "igverif|identitygateway"` — o esperado é sobrarem só os três `identitygateway_*`. A
   fase `jornada` deixa um arquivo de estado no diretório temporário (`ig-jornada-estado.json`), com o refresh token
   do platform-admin local: apagar.

As duas mutações do marcador **foram vistas vermelhas na Tarefa 10**, pela contagem no mailpit, sem o app. O que
falta é a testemunha que a CI usa. Elas **não** são critério de aceite do PR; a derrubada é limpeza.

**Para o autor decidir:**
- **Confirmar três decisões da execução** (tabela acima): o `sub` em maiúsculas aceito, a v2.7 sem o nome da
  "fatia E" e o limitador de requisições depois da autorização.
- **A dívida das factories de teste.** A derrubada de um host derivado por `WithWebHostBuilder` leva cerca de 10 s
  em parte dos casos, pelo flush do exportador OTLP (`src/IdentityGateway.Api/DependencyInjection.cs`,
  `AddOtlpExporter()` sem endpoint, padrão `localhost:4317`) e do sink Seq (`appsettings.Development.json`,
  `localhost:5341`) sem destino; sem os dois, menos de 0,2 s — medido na revisão da Tarefa 9. Efeito colateral que
  já existia: com o compose de desenvolvimento de pé, os hosts de teste mandam spans e logs para o Jaeger e o Seq
  locais. A escolha é entre neutralizar os destinos na factory e condicionar o OTLP a um endpoint configurado.
- **A `api` sai com 137 no `stop`** — morta depois dos 10 s de tolerância, em vez de encerrar no `SIGTERM`. É
  comportamento de produto que já existia, fora desta fatia, e a causa não foi investigada. Custa cerca de 10 s em
  cada `down`, inclusive na CI.
- **Risco de vermelho sem defeito na CI:** se o Keycloak levar perto dos 180 s do laço para voltar num runner lento
  e o teto de tentativas do Outbox for baixo, o tenant registrado com o Keycloak parado vira `ProvisioningFailed`.
  Localmente, o Keycloak voltou em menos de 20 s. E o teto do job `Compose` (20 min) é menor que a soma dos tetos dos
  passos (34 min): num caminho já patológico, o job é cancelado, e o diagnóstico não roda.
- **O `kc()` do one-shot cala o stderr do `kcadm`**, de propósito, para nunca ecoar uma resposta: a causa real de
  uma falha (SMTP, `403`, conexão) não aparece, só a mensagem genérica com a instrução.
- **`PLATFORM_ADMIN_EMAIL` com `+`** não foi testado na busca do one-shot (`-q email=`). A falha é fechada — o
  one-shot não envia para a conta errada —, mas vale conferir ao vivo antes de usar um e-mail assim.
- **Uma divergência interna do design, a fechar antes da D2:** a §4.3 diz que o `tenantId` da rota e o claim
  `tenant_id` são, os dois, GUID no formato `D`; a §5.2 tem como controle positivo o próprio tenant com o GUID da
  rota **no formato `N`** respondendo `200`. A v2.7 (§10.1 e §11.7) ficou com a leitura da §5.2 — o claim só no
  formato `D`, a rota em qualquer formato de GUID —, e a Tarefa 13 do plano implementa essa leitura. Se o autor
  preferir a outra, mudam a Tarefa 13 (o `Guid.TryParse` da rota vira `TryParseExact`, e o controle positivo do
  formato `N` vira caso de `403`) e, na Tarefa 17, a v2.7.

**Limites registrados na v2.7 (§19), que continuam abertos:**
- **O platform-admin recebe `403` na leitura de tenant** até o override virar a policy `TenantReadAccess`, com a
  auditoria.
- **O Data Plane continua exposto ao `tenant_id` por grupo** (ADR-011): a regra "nenhum grupo" é conferida só no
  JSON do bootstrap.
- **A pertença não contém quem tem a chave da Gateway**, que toma a conta de um `Member` real.
- **Links de ações antigos trocam a senha de uma conta ativa.** O do platform-admin vale 4 horas; **o link de 7 dias
  do convite do admin do tenant é risco aceito**, com dono na operação que trocar ou reenviar esse convite.
- **E-mail digitado errado:** com a D2, o destinatário passa a ler o tenant pela API.
- **Device flow:** consentimento forçado, e o client público é o vetor clássico de phishing de código de
  dispositivo; por isso fica só no ambiente local e só na lista de `azp` de Development.
- **`CreateDefaultClientScopes` não é documentado:** reverificar a cada troca de tag do Keycloak.
- **Rotação de chave do realm:** cada réplica recusa o primeiro pedido com o `kid` novo; o runbook está na §19.
- **Keycloak fora com metadados frios responde `401`**, e não `503`; o `/health/ready` não cobre um `jwks_uri`
  inalcançável.
- **A `api` depende de um one-shot que depende do Keycloak saudável**, e o one-shot faz login no `master` a cada
  subida: não trocar nem apagar o admin do `master` do compose.
- **`PLATFORM_ADMIN_EMAIL` fica fixado no primeiro import**, e a recuperação do platform-admin só existe pelo
  console do `master`. O bootstrap de produção não está decidido.
- **O marcador do convite vive no realm:** usuário recriado à mão, ou envio que falhou, só saem pelo reenvio manual.
- **A lista de `azp` é estática**, e fica vazia fora de Development até existir um client administrativo.
- **Reusar um refresh token derruba a sessão do client:** nenhuma renovação pode ser repetida automaticamente.
- **Mailpit sem autenticação** em `127.0.0.1:8025`.

**Dívidas herdadas, que seguem:** e-mail duplicado entre o envio e o commit; admin órfão; tenants anteriores à v2.6;
a mesma pessoa em dois tenants; retenção real do e-mail apagado; notification-hub; vínculo federado no M4; corrida
residual do `POST` de papel; slug perdedor com `500`; a troca de e-mail sem verificação, não verificada na 26.7.4.

**Não entrou, e segue pendente do M0:** a tabela de auditoria, o armazenamento de eventos do realm (custa outro
`docker compose down -v` quando entrar, e a retenção dos eventos de login, que guardam o e-mail, é decidida junto) e
o RabbitMQ.

**Propostas do design (§7), sem decisão:** a sequência das próximas fatias; a suspensão cobrindo também o admin
`Invited`; a revisão da §12 da especificação, do Data Plane; o bootstrap de produção do primeiro platform-admin; e o
RabbitMQ fora da lista do M0. Nenhuma está na v2.7.

**Documento de negócio:** o RabbitMQ continua descrito como destino da arquitetura fora do fluxo 9.1, o que está
certo como destino; a frase "sem código escrito até esta versão", da nota "Natureza do artefato", está desatualizada
desde a vertical de registro; e o documento explica nove ADRs, sem ficha para o ADR-010 nem para o ADR-011.

**Achados menores das revisões por tarefa, adiados para a revisão final da branch:**
- **App da jornada** (`tools/jornada-compose.cs`): o título do `::error` sai cortado na vírgula, nas quatro etapas
  que têm vírgula, e sem escape de `%` nem de quebra de linha; uma exceção fora das quatro capturas sai com stack
  trace e código 134, sem família de falha; `convites` sem `--esperado` sai com `10`, e não com `2`; o arquivo de
  estado guarda o refresh token, recebe a permissão 600 só depois de gravado e tem nome previsível no diretório
  temporário; e a função que mascara segredos no log da CI é o único caminho pelo qual um segredo chega à saída, e
  depende de a linha começar por `::add-mask::` — a saída do app nunca pode ser encanada.
- **Workflow:** o diagnóstico em falha não imprime `migrate`, `keycloak-db`, `postgres` nem `mailpit`; e o `rm -f`
  do arquivo de estado vem depois do `down -v`, e não roda se ele falhar.
- **README:** a demonstração diz que o comando `-- jornada` percorre os passos dela, mas ele não para o Keycloak — os
  passos com o Keycloak parado são as outras três fases, intercaladas com `stop` e `start`; e não avisa que a fase
  deixa o arquivo de estado, com o refresh token.
- **Testes:** em `Logs/CapturaDeSpans.cs`, uma tag do tipo `string[]` vira o texto `System.String[]`, e o detector
  não veria um token num cabeçalho capturado assim; em `VazamentoDoEmailNoTokenTests`, a busca nos cabeçalhos não
  inclui os do conteúdo, e nenhuma mutação exercita a ausência no corpo nem nos cabeçalhos; em `HostEmProducaoTests`,
  a asserção negativa do token imprimiria os textos do log, com o token de teste, se falhasse; o teto de 7 s de
  `MetadadosFrios_…` tem 1,7 s de folga sobre os 5,3 s observados; e as regras textuais do compose e das ferramentas
  não pegam variantes de forma (`- KC_CLI_PASSWORD=x` em lista, `-F attributes`, uma linha comentada).
- **Regras do realm:** `AudienciaDaGatewaySoNoScopeGatewayApi` não tem mutação para "Audience Mapper em outro
  lugar"; e duas regras caem por exceção, e não por asserção, quando a chave some (tabela de mutações).
- **Log do framework:** em Development, o `kid` do token — texto controlado por quem manda o token — aparece no log
  do `JwtBearerHandler` (`IDX10503`, em `Information`). Em produção, a categoria `Microsoft.AspNetCore` fica em
  `Warning`. Os logs da Gateway não o registram.
- **v2.7, §11.8:** os blocos de código são referência, e não cópia. O do `AddKeycloakIdentity` não traz a validação
  "`AllowedClients` não aceita item vazio", que o código tem, e a mensagem da recusa do client de demonstração difere
  em palavras; o `FormaDoAccessToken.Recusar` de referência não traz o ramo do payload ilegível.
- **Pacotes:** `OpenTelemetry.Exporter.InMemory` continua no `Directory.Packages.props` com um comentário que não
  vale mais — quem o referencia é o projeto de integração.

## Próximo passo

1. Revisão final da branch inteira, com a lista de pendências acima.
2. O autor derruba o `igverif` e, se quiser, fecha as duas mutações do marcador e a checagem de token nos logs
   ("Pendências").
3. Autorizar o push e abrir o PR contra `main`, com o aviso de `docker compose down -v` no corpo.
4. Acompanhar a CI (`gh pr checks <n>`) — `Build`, `Testes`, `Imagem Docker` e `Compose`, que agora roda a jornada
   pelo app C#, com o Keycloak parado. **É a primeira execução do job `Compose`, do passo novo do job `build` e do
   app em Linux.** O que ela precisa mostrar: a compilação do app nos dois jobs; as linhas `::add-mask::` saindo como
   `***`; a tabela do resumo; o laço que espera o Keycloak voltar a `healthy`; e o tempo total do job, para pôr na
   tabela da verificação ao vivo. Se algo falhar, corrigir na mesma branch.
5. Mesclar. Depois: `git checkout main && git pull --ff-only`, apagar a branch local e a remota, e acrescentar o
   número do PR na linha da fatia D da §16 da v2.7.
6. Começar a **D2** (Tarefas 13–17 do plano) numa branch nova, a partir da `main`: os requirements e a policy
   `TenantAdmin`, a porta `IMemberQueries`, a rota `GET /api/v1/tenants/{tenantId}`, a coleção real estendida e o
   fechamento dos itens "(D2, planejado)" — `grep -n "(D2, planejado)" docs/especificacao-arquitetural-v2.7.md docs/documentacao-negocio.md`
   lista todos. A D2 não mexe no realm, no compose nem no one-shot.

## Como retomar

Docker Desktop costuma estar desligado ao abrir a sessão: sem ele, os testes de integração e funcionais falham com
`DockerUnavailableException` (ambiente, não regressão). Quem já tinha o compose de pé antes desta fatia precisa de
`docker compose down -v` uma vez. O token da demonstração vale 5 minutos, e o refresh token, 30 minutos de
inatividade: cada renovação devolve um refresh token novo, e o usado não serve mais.
