# Handoff — fundação Keycloak desenhada e planejada, implementação por começar

> **Data:** 2026-09-24 · **Marco:** misto M0/M1 · **Status:** spec aprovada, plano escrito, **nenhum código ainda**
> **Onde parou:** o plano está pronto; falta você revisá-lo e escolher o modo de execução.
>
> Sucede o [handoff da vertical entregue](2026-09-22-vertical-entregue-handoff.md). Referência normativa:
> [`especificacao-arquitetural-v2.4.md`](../../especificacao-arquitetural-v2.4.md) — **a v2.4 passou a ser a vigente
> nesta sessão**.

---

## Estado do repositório

| O quê | Estado |
|---|---|
| PR #1 (vertical de registro) | **Mesclado** em `main` em 2026-09-24 (`64af7fc`); branch removida no remoto e no local |
| `main` local | Igual a `origin/main` |
| Branch de trabalho | `feat/fundacao-keycloak`, 3 commits sobre `main`, **sem push** |
| Working tree | Limpa, exceto `docs/codeprocess-tem-a-ideia-v2.png` — arquivo solto, fora de qualquer commit, citado por nenhum documento. Decidir se entra, e em qual commit |
| Docker Desktop | **Estava desligado.** As Tasks 7 e 9–13 do plano precisam dele |

Commits da branch:

```
<este>   docs: handoff da fundacao Keycloak planejada
d10e4bd  docs: plano de implementacao da fundacao Keycloak
7292521  docs: design da fundacao Keycloak e spec v2.4
```

## O que esta sessão produziu

**1. O fatiamento do provisionamento (§9.1) em três**, cada fatia com spec, plano e PR próprios:

| Fatia | Entrega | Estado |
|---|---|---|
| **A · Fundação Keycloak** | Keycloak no compose, nos testes e na CI; service account com `private_key_jwt`; porta `IIdentityProvider.EnsureOrganizationAsync` | **Planejada** |
| B · Consumidor | Transporte, `ProvisionTenantHandler`, `MarkProvisioned`, `ProvisioningFailed`; a demonstração do M1 | Próxima |
| C · Convite do admin inicial | Onde o e-mail vive, `EnsureInvitedUserAsync`, a tensão vaga × `Active` | Depois da B |

**2. A spec da fatia A** — [`2026-09-24-fundacao-keycloak-design.md`](2026-09-24-fundacao-keycloak-design.md), com 11
decisões registradas (D1–D11), os fatos verificados no código-fonte do Keycloak 26.7.4, e a lista de testes.

**3. A v2.4 da especificação arquitetural.** A leitura do código do Keycloak desmentiu premissas que a v2.3 afirmava
como "aprovadas para implementação":

| A v2.3 dizia | O que é verdade |
|---|---|
| `KC_FEATURES=organization` é obrigatório | Organizations é padrão desde a 26.0; o obrigatório é `organizationsEnabled: true` no realm |
| Busca via `searchQuery=...&exact=true` | O parâmetro é `q`; atributos só voltam com `briefRepresentation=false` |
| `aud` do assertion = token endpoint; o issuer daria `invalid_client` | O issuer é aceito e recomendado desde a 26.2; o erro é `aud` com mais de um valor |
| Vida do assertion "preenchida pelo `JsonWebTokenHandler`; ≤ 60s" | O padrão da biblioteca é **60 minutos** |
| — | O `kid` que o .NET emite não bate com o do Keycloak: assinar **sem `kid`** |
| — | `jti` de uso único: o token endpoint **não pode ter retry** |

Mais 12 entradas de inconsistência interna e decisões novas (assinatura da porta com `TenantId`, `name` = slug,
ordem dos handlers, health check que obtém token, compose na CI, etc.) — a lista completa está na seção 0 da v2.4.
**Nenhum ADR foi revogado.**

**4. O `documentacao-negocio.md` passou à v1.2**, alinhado à v2.4. Ele ainda apontava a **v2.2** como fonte da verdade.

**5. O plano** — [`plans/2026-09-24-fundacao-keycloak.md`](../plans/2026-09-24-fundacao-keycloak.md): 14 tarefas em
TDD, com código completo, e **prova por mutação** obrigatória nos testes marcados com 🧪.

## Como o design foi validado

Antes de virar spec, o design passou por **seis revisores especialistas em paralelo** (arquitetura .NET, segurança,
Keycloak, testes, DevOps e coerência documental). Eles acharam **9 bloqueantes**, todos incorporados:

- três testes seriam **verde vacuoso** — não duplicação, busca por atributo e health check ficariam verdes com o
  código errado (a mesma lição da vertical anterior);
- `kid` que não bate e assertion de 60 minutos;
- `initdb` que quebraria quem já tem o volume do Postgres, e script montado chegando com CRLF;
- health `Degraded` respondendo 200;
- assertion reenviado sendo recusado por reuso de `jti`;
- o documento de negócio repetindo as premissas erradas.

Quatro decisões saíram dessa revisão, todas pela recomendação: compose **na CI já nesta fatia**; senha do admin master
**gerada** no init; chave e realm dessincronizados **falham alto** no `ready`; teste funcional confere **só `live`**.

## Errata do handoff de 2026-09-22

O handoff da vertical afirmava que o `initialAdminEmail` era "validado e carregado até o evento". **Não era**: o
handler chama `Tenant.Register(name, slug, plan)` sem ele, e `TenantRegistered` só tem `TenantId` e `Slug`. O campo é
validado e descartado. O comentário de `RegisterTenantCommand.cs` repete a afirmação falsa — a Task 14 do plano o
corrige. **Onde o e-mail deve viver é decisão pendente da fatia C** (v2.4, §9.1): pô-lo no evento o levaria ao
Outbox e ao RabbitMQ, contra a regra de dados pessoais só no Keycloak.

## Dívidas e pendências nomeadas

- **Fatia C:** onde o `initialAdminEmail` vive; e a tensão entre `ReserveSeat()` exigir `Active` e a §9.1 convidar
  antes de ativar. As duas estão na §9.1 da v2.4.
- **Do M0, depois da fatia A:** client scopes `gateway-roles`/`gateway-tenant`, Audience Mapper, catálogo de papéis,
  eventos do realm, remoção do `offline_access`, platform-admin com senha gerada, a API validando tokens do Keycloak e
  o RabbitMQ (v2.4, §16).
- **Da vertical anterior, ainda abertas:** o `500` em vez de `409` na corrida de slug, e os três itens adiáveis do
  handoff de 2026-09-22.
- **Na execução, Task 11:** duas mutações (ordem dos handlers e `JsonContent` no lugar de `StringContent`) podem não
  ter teste que as pegue. O plano manda registrar o resultado observado no commit, em vez de afirmar cobertura.

## Como retomar

1. **Revisar o plano** e escolher o modo de execução:
   - **Por subagentes** (recomendado): um subagente por tarefa e um revisor antes da seguinte, mais uma revisão final
     da branch. As tarefas encadeiam interfaces (cache → handler → cliente → adaptador → fixture), e um erro de
     contrato no começo se propaga.
   - **Nativo:** implementação direta na sessão, com um revisor independente só no fim.
2. **Ligar o Docker Desktop** antes da Task 7.
3. Ao fim da Task 14 sai um handoff novo (`<data>-fundacao-keycloak-handoff.md`); o push e o PR só com autorização.
