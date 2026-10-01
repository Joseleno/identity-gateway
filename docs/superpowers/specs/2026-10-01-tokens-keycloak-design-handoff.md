# Handoff — Fatia D (tokens do Keycloak): design concluído, plano em redação

> **Data:** 2026-10-01 · **Marco:** M0 + M1 · **Branch:** `feat/tokens-keycloak` (não publicada) · **Base:** `main` em
> `5b0113c` (fatia C mesclada, PR #5).
> **Estado:** spec de design aprovada pelo autor e commitada; todos os spikes fechados; o plano de implementação estava
> sendo escrito quando a sessão parou.

---

## Onde estamos

| Etapa | Estado |
|---|---|
| Brainstorming (decisões D-a a D-o) | Concluído |
| Duas rodadas de análise do rascunho (5 + 4 especialistas) e verificação ao vivo no Keycloak 26.7.4 | Concluídas |
| Spec escrita, revisada por 5 especialistas e corrigida | Concluída e aprovada: `docs/superpowers/specs/2026-09-30-tokens-keycloak-design.md` |
| Spikes antes do plano | Todos fechados (registrados no fim da §3 da spec) |
| Plano de implementação | **Em redação** quando a sessão parou (ver "Como retomar") |
| Implementação | Não começou |

Commits na branch (só documentação):
- `8537d76` design da fatia D;
- `833a9cc` design revisado por cinco especialistas;
- `0294b90` spikes fechados (marcador no realm e rotação do refresh);
- o commit deste handoff.

## O que a fatia D é, em uma frase

A API passa a aceitar só tokens do Keycloak (fim do JWT simétrico do template) e ganha a primeira rota de tenant,
`GET /api/v1/tenants/{tenantId}`, que o admin convidado da fatia C consegue chamar. A fatia foi **dividida em dois
PRs**: a D1 leva o Keycloak de ponta a ponta (realm, bootstrap do platform-admin, harness de device flow, validação dos
tokens, app de CI, README); a D2 leva a rota com as quatro camadas da policy, sem mexer no realm.

## Decisões do autor (todas na §2.1 da spec)

D-a escopo até a primeira rota de tenant · D-b platform-admin `403` na rota até a auditoria · D-c device flow na
demonstração · D-d/D-h platform-admin no JSON do realm, e-mail de ações uma vez (marcador no realm) · D-e `aud` em
scope próprio + lista de `azp` · D-f `Member` exigido no banco · D-g separação de funções · D-i dividir em D1 e D2 ·
D-j `Member` só `Invited`/`Active` · D-k tenant inexistente `403` · D-l a v2.7 só com o que a D implementa (roadmap,
suspensão de `Invited`, §12 do Data Plane, TOTP de produção e RabbitMQ fora do M0 viraram propostas) · D-m link do
platform-admin de 4 h · D-n rotação do refresh token nesta fatia · D-o o link de 7 dias da fatia C é risco aceito,
com dono na fatia F.

## Achados que importam para quem implementa

- **A ferramenta de escrita dos agentes (Write/Edit) decodifica a sequência barra-u-0026.** Foi o que deixou a frase
  errada na v2.6. Na v2.7, grave a errata E1 por shell e confira com `grep -c 'u0026'`.
- **Reusar um refresh token derruba a sessão inteira do client** no Keycloak (até o token novo passa a ser recusado).
  Harness, app de CI e README nunca repetem uma renovação; se ela falhar, device flow novo.
- **Links de ações antigos trocam a senha de uma conta já ativa** (provado ao vivo). Por isso o link do
  platform-admin é curto e enviado uma vez; o convite de 7 dias da fatia C fica como risco aceito, com a fatia F.
- **Declarar `clientScopes` no JSON do realm apaga os scopes embutidos** sem erro; a correção é o atributo
  `CreateDefaultClientScopes` (não persiste; a presença dos embutidos é provada no fixture).
- **O `ValidIssuer` do JwtBearer não restringe** (a biblioteca aceita o emissor do discovery); por isso o
  `IssuerValidator` estrito, provado por um OIDC falso que anuncia emissor diferente.
- **O `tenant_id` pode ser forjado por grupo** (o mapper cai no atributo do grupo); por isso o `MemberRequirement`.
- **Uma prova por mutação da fatia C continua pendente:** a guarda `HttpSoEmDesenvolvimento` sempre verdadeira
  (roteiro no handoff da fatia C). A proteção automática do ambiente bloqueou agentes de rodá-la; o plano a inclui
  como passo, e se o bloqueio se repetir ela é do autor.

## Como retomar

1. `git checkout feat/tokens-keycloak && git status`.
2. **Verificar se o plano chegou a ser gravado:** `docs/superpowers/plans/2026-09-30-tokens-keycloak.md`.
   - Se existir **e estiver completo** (17 tarefas, D1 = 1-12, D2 = 13-17, sem placeholders): revisar contra a spec
     (assinaturas entre tarefas, nenhuma referência a IA nos commits, nenhum `404` na rota, `IMemberQueries`, link de
     4 h) e commitar.
   - Se não existir ou estiver incompleto: apagar o arquivo parcial e reescrever o plano com a decomposição abaixo
     (superpowers:writing-plans; o plano da fatia C é o modelo de formato).
3. Escolher o modo de execução (na fatia C foi por subagentes, com revisão por tarefa e revisão final da branch).
4. Executar a **D1**, abrir o PR, mesclar; depois a **D2** num PR próprio.

O material verificado ao vivo nesta sessão (JSON do realm que funcionou, harness C#, spikes) estava no scratchpad da
sessão, que é efêmero. Tudo o que importa dele está na spec: o JSON final na §4.4, os fatos com evidência na §3, os
comandos do one-shot na §4.5 e o desenho do harness e do app na §4.6.

## Decomposição do plano (fixada pelo controlador)

**PARTE D1** (um PR; a CI fica verde com o `POST /tenants` feito pelo platform-admin via device flow):
1. Projeto de suporte `tests/IdentityGateway.Testing.Keycloak` como biblioteca (`IsTestProject=false`,
   `OutputType=Library`, sem os pacotes de xunit herdados, `xunit.v3.extensibility.core` no CPM): mover o
   `KeycloakFixture` e o mailpit, sem mudança de comportamento.
2. Realm, parte estática: o JSON final da §4.4 e as regras novas de `RegrasDoRealmTests`, com mutação.
3. Realm, parte viva: scopes embutidos presentes; o service account com `manage-users` (health check e Admin API
   verdes); papéis efetivos; token do service account sem o `aud` da Gateway.
4. Harness de login por device flow (cookies manuais, reescrita de host, identity-first, ações obrigatórias,
   consentimento, `slow_down`, refresh gravado antes de tudo e nunca repetido), um platform-admin por teste, cliente
   do mailpit extraído, exceções sem segredo; fim do ROPC no fixture.
5. Conclusão do convite pelo link do mailpit + testes da forma do token contra o Keycloak real (inclusive a rotação do
   refresh: o reuso derruba a sessão).
6. `AccessTokenValidationOptions` pública e neutra (`Keycloak:Auth`), preenchida pelo adaptador; validação na subida;
   testes com o host em Production.
7. Troca da autenticação (JwtBearer por metadados internos, `IssuerValidator` estrito, RS256, timeouts,
   `IncludeErrorDetails=false`, `FallbackPolicy`, Problem Details para 401/403), OIDC falso na factory, remoção de
   `JwtOptions`/`JwtTokenService`/`Jwt__SigningKey`, `HttpCurrentUser` ajustado.
8. Checagens além do JwtBearer (`azp`, `typ`, `sub`) + suíte negativa de autenticação em `[Theory]`; testes em
   Production.
9. Coleção com Keycloak real em `Api.FunctionalTests`; teste de vazamento estendido (log, trace, base64url).
10. One-shot `platform-admin-invite` (kcadm, marcador no realm antes do envio, link de 4 h, exit 0/1), compose
    (`depends_on`, api e Jaeger em `127.0.0.1`), verificação local em projeto isolado (`-p igverif`).
11. App `tools/jornada-compose.cs` (`#:project`, `PublishAot=false`), job `Compose` reescrito (`pipefail`, os passos
    da §4.7, "um e-mail só", HS256 → 401, `stop`/`start`), README; a mutação pendente da guarda `https` da fatia C.
12. Documentação da D1: v2.7 (itens da D2 marcados "(D2, planejado)"; errata E1 por shell), documento de negócio 1.4,
    README, CONTRIBUTING, handoff da D1.

**PARTE D2** (outro PR, sem mexer no realm):
13. `SameTenantRequirement` + negação por `platform-admin` + handler do papel com `Fail()` + policy `TenantAdmin`
    (ordem de registro explícita, `InvokeHandlersAfterFailure=false`), com testes unitários.
14. `MemberRequirement` com a porta `IMemberQueries` (só `Invited`/`Active`), teste no PostgreSQL; nomes de
    `TenantStatus`/`MemberStatus` travados.
15. `GET /api/v1/tenants/{tenantId}` (`TenantDetails` com chaves exatas, `403` uniforme, OpenAPI), teste de subida do
    `{tenantId}`, suíte negativa de autorização.
16. Coleção real estendida (Outbox ligado): o admin convidado lê o próprio tenant; os `403`; o ataque do grupo negado;
    o passo do admin convidado na CI e no README.
17. Documentação da D2: fechar os itens "(D2, planejado)"; handoff da D2.

## Pendências fora da fatia D (do handoff da fatia C, ainda abertas)

A frase `&` da v2.6 (corrigida pela errata E1 da v2.7), a corrida residual do role-mapping, o admin `Invited` até
a detecção de aceite, o e-mail errado sem recuperação pela API e o link de 7 dias (fatia F). O roadmap proposto depois
da D está na §7 da spec, como proposta.
