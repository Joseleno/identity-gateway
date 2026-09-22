# Handoff — vertical de registro entregue, PR #1 aguardando merge

> **Data:** 2026-09-22 · **Marco:** M0 · **Status:** implementação concluída, PR aberto e verde
> **Onde parou:** [PR #1](https://github.com/Joseleno/identity-gateway/pull/1) aberto, CI verde nos três
> checks, aguardando **revisão humana e merge** — que não é decisão minha.
>
> Sucede o [handoff da vertical](2026-09-21-registro-de-tenant-handoff.md), que parou no meio do design.
> Referência normativa: [`especificacao-arquitetural-v2.3.md`](../../especificacao-arquitetural-v2.3.md).

---

## Estado do repositório

**Branch `feat/tenant-registro-vertical`**, HEAD em `3ea7759`, 25 commits sobre `main` (`7f9ee25`),
working tree limpa, `MERGEABLE` sem conflito.

**A `main` não foi tocada.** O merge é o próximo passo humano.

### PR #1 — CI verde

| Check | Resultado |
|---|---|
| Build | ✅ 38s |
| Testes | ✅ 58s — **213 total, 0 falhas** |
| Imagem Docker | ✅ 2m22s |

A CI rodou em Release, com Testcontainers reais, e chegou ao mesmo número obtido localmente:
109 domínio · 35 application · 18 arquitetura · 32 integração · 19 funcional.

**Nenhum skip em lugar nenhum.** Os 4 que a fatia herdou foram fechados: os 2 de mensageria na
Task 3 (passou a haver handler e command) e os 2 de `401` na Task 11 (passou a haver endpoint
protegido, e eles apontavam para o verbo errado — faziam `GET` numa rota que a fatia entrega como
`POST`).

## O que a fatia entregou

`POST /api/v1/tenants` ponta a ponta, do JSON até a linha em `tenants` com a mensagem
`tenant-registered` no Outbox, na mesma unidade de trabalho. Nenhuma chamada ao Keycloak — por isso
a resposta é `202`, e o provisionamento é do consumidor da mensagem.

| Camada | Entregue |
|---|---|
| Application | `RegisterTenantCommand`, `Validator`, `Handler`, portas `ITenantRepository` e `IPlanCatalog` |
| Infrastructure | `TenantConfiguration`, migration `CriacaoDeTenants`, `TenantRepository`, `PlanCatalog` + `PlanOptions` |
| Api | `TenantsModule` (Carter), policy `PlatformAdmin`, `ParaAccepted`, request/response |

O design está em
[`2026-09-21-tenant-registro-vertical-design.md`](2026-09-21-tenant-registro-vertical-design.md), já
**alinhado ao que foi de fato construído** — sete pontos onde o desenho previa uma coisa e a
implementação provou outra estão marcados com "Corrigido durante a implementação".

## Próximo passo

**O consumidor do provisionamento** — os três passos da §9.1: lê a mensagem do Outbox, cria a
Organization no Keycloak, chama `MarkProvisioned` e envia o convite do `initialAdminEmail` (que esta
fatia já valida e carrega até o evento, mas não usa).

Depois dele, `GET /tenants` e `GET /tenants/{id}/provisioning` — o segundo fecha o `Location` que o
`202` já devolve apontando para rota inexistente.

## Dívida nomeada, a resolver em fatia própria

**Quem perde a corrida de slug recebe `500`, não `409`.** O handler devolve sucesso, o
`TransactionBehavior` chama `SaveChangesAsync` fora de `try/catch`, e a `DbUpdateException` sobe até
o catch-all do `ExceptionHandlingMiddleware`.

A janela é estreita e o dado permanece correto — o índice único cumpre seu papel. Mas o cliente
recebe a resposta errada, com entrada no log de erro para um caso que é resposta de negócio
legítima. A correção pertence ao **tratamento de erro global**: traduzir violação de unicidade em
`Conflict` vale para todo `INSERT` com constraint única que venha depois, não só para este caso de
uso.

### Três itens deferidos, classificados como adiáveis pela revisão final

- `ThrowAsync<DbUpdateException>` não fixa qual constraint disparou (padrão preexistente).
- O token de concorrência `xmin` está configurado e **nenhum teste o exercita** — o comando que o
  exerceria é `ReserveSeat`, do M1 (convite de membro).
- O binding do enum `PlanTier` a partir de string (`"tier": "Free"`) não tem cobertura.

## A lição que esta fatia ensinou

**Verde sem teste que exercite o caminho não é evidência de nada.** Quatro defeitos chegaram à
revisão com build 0/0 e revisão aprovada, e só apareceram quando algo de fato percorreu o caminho:

1. **A policy `PlatformAdmin` nunca funcionou.** O `JwtSecurityTokenHandler` remapeia o claim curto
   `roles` para a URI longa antes de a policy comparar — `403` sempre, com token correto. Dada por
   completa na Task 8, descoberta na Task 10, três tarefas depois.
2. **O `ValidateOnStart` do catálogo era no-op**, e o comentário prometia que catálogo inválido
   derrubaria a aplicação. A correção aparente (`[Range]` + `ValidateDataAnnotations`) também não
   funciona: as anotações não alcançam os **valores** de um dicionário.
3. **`MapInboundClaims = false` quebrou a identificação do usuário** — invisível porque nenhuma
   entidade é auditável ainda; o primeiro agregado com `IAuditable` nasceria com `CreatedBy` nulo.
4. **O mesmo flag quebrou a partição do rate limiter**, a três arquivos de distância — achado só na
   varredura final por leitores de claim.

**O que funcionou contra isso:** provar por **mutação** — quebrar o código de propósito e confirmar
que o teste falha. Cinco provas desta fatia foram assim: atomicidade do Outbox, resolução das portas
no contêiner, os dois testes de `401`, identificação do usuário e partição do rate limiter.

**Corolário:** quando uma flag global muda comportamento, varrer o código **inteiro** por quem
depende dela, não só o arquivo óbvio.

## Como retomar

O merge do PR #1 é decisão sua. Depois dele, a próxima fatia é o consumidor do provisionamento — e
ela começa por brainstorming, como esta começou, porque envolve integração externa (Keycloak) sem
fluxo existente no repo para seguir.
