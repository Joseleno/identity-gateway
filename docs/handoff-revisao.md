# Handoff — onde paramos e como continuar

> **Data:** 2026-09-20 · **Fase:** specs fechadas, implementação não iniciada.
> Nenhuma linha de código escrita. A pasta contém apenas `docs/`, e ainda **não é repositório git**.
>
> **Atualização:** a documentação de negócio foi produzida e, no processo, encontrou lacunas na v2.1.
> Todas foram decididas e gravadas na **v2.2**, que passa a ser o documento vigente.

---

## Situação

O brainstorm fechou. A especificação foi revisada por três frentes independentes, as decisões em aberto
foram tomadas, e a spec corrigida está pronta para virar código.

**Documento vigente para implementar: `especificacao-arquitetural-v2.3.md`.**

### Os quatro documentos

| Arquivo | Papel |
|---|---|
| `documenta_o_arquitetural.md` | Documento de **ideia/origem**. Contexto histórico, continua válido |
| `especificacao-arquitetural.md` | **v2.0** — a spec revisada. Preservada como estava; não editar |
| `especificacao-arquitetural-v2.1.md` | **v2.1** — preservada; incorporou a revisão crítica |
| `especificacao-arquitetural-v2.2.md` | **v2.2** — preservada; fechou as lacunas da documentação de negócio |
| `especificacao-arquitetural-v2.3.md` | **v2.3 — a referência de implementação vigente** |
| `documentacao-negocio.md` | Documentação de negócio: capacidades, funcionalidades, fluxos e ADRs em linguagem de negócio |
| `revisao-critica.md` | Os 33 achados + 8 contradições + calibração. É o *porquê* de cada mudança da v2.1 |

Os cinco primeiros são uma **linha evolutiva**, não fontes concorrentes: ideia → v2.0 → v2.1 → v2.2 → v2.3. Cada um
fecha pontos que o anterior deixou em aberto. Essa rastreabilidade é um ativo do portfólio e vale ser
apontada no README.

---

## O que foi decidido

Sete decisões fechadas no brainstorm, incorporadas à v2.1 (§0.1 tem o changelog completo):

1. **`initialAdminEmail` obrigatório** no `POST /tenants` — o tenant nasce com um `tenant-admin`, em vez
   de nascer trancado. Escolhido para **eliminar** uma exceção no isolamento, não criar outra.
2. **`Invited` ocupa vaga** — e por isso a expiração de convite passou de "extra" a obrigatória.
3. **Suspensão desabilita os usuários no Keycloak** e revoga sessões. Sem isso seria uma flag sem efeito.
4. **Estado terminal `Terminated`**, alcançável só de `Suspended`, **sem remoção física** dos dados.
5. **Federação (M4) permanece no escopo**, depois do M3.
6. **Escopo completo M0–M7** — projeto sem prazo.
7. **Demonstração por README com `curl` reproduzível**, o que promove o M0 a peça crítica.

### Mais nove, fechadas ao produzir a documentação de negócio (v2.2)

A produção da `documentacao-negocio.md` percorreu a v2.1 inteira e encontrou lacunas. Todas decididas e
gravadas na v2.2 (§0 tem o changelog):

| # | Decisão |
|---|---|
| L-1 | Suspensão e encerramento desabilitam **também os clients OIDC** do tenant (`WasEnabledBeforeSuspension`) |
| L-2 | Ciclo de vida do convite (§9.9 nova): expiração por **job da Gateway**, prazo por plano com padrão 7 dias, reenvio reinicia `InvitedAt`, cancelamento → `Revoked` |
| L-3 | Suspensão em massa **idempotente e retomável**: marca gravada por membro, junto da desabilitação. Novo estado `Suspending`; reativação nele responde `409` |
| I-1 | `Terminating` vira estado real no diagrama da §6.2 |
| I-2 | Cancelamento de convite → novo estado **`Revoked`**, distinto de `Expired` (prazo) e `Erased` (LGPD) |
| I-3 | Override do `platform-admin` **restrito à leitura de tenant**, auditado — preserva o argumento de C9 |
| N8 | Downgrade de plano abaixo das vagas ocupadas rejeitado com `409`, nomeando quantos precisam sair |
| — | **`MaxClients` passa a ser aplicado** no registro de client (`409`) — antes era campo sem efeito |
| A9 | Client assertion `private_key_jwt` especificado (§10.2): `JsonWebTokenHandler`, claims `iss`/`sub`/`aud`/`jti`, e o alerta de que **`aud` é o token endpoint**, não o issuer |

### Mais nove, fechadas ao resolver as pendências de arquitetura (v2.3)

| # | Decisão |
|---|---|
| T1 | Endpoints em **módulos Carter**, como no CleanStart — não divergir do template no primeiro uso |
| T2 | Compose **unificado**: keycloak, postgres, rabbitmq, mailpit, redis, seq, jaeger e as duas APIs |
| T3 | **RabbitMQ** como transporte do Outbox do CleanStart (interceptor + tabela + worker) |
| T4 | **Redis + HybridCache** (L1+L2) para o cache de permissões do §9.6 |
| C14 | **ADR-010 novo**: `pg_try_advisory_lock` por job de fundo + unique constraint como rede final |
| — | Eventos de integração em **CloudEvents 1.0**, versão no `type`, `.v2` em paralelo ao `.v1` — §14.1 |
| — | Versionamento: path mantido, depreciação por `Deprecation`/`Sunset` (RFC 8594) — §8 |
| — | Secrets: User Secrets local, variáveis de ambiente nos demais, leitura abstraída — §10.2 |
| — | Warm-up: health `ready` do `Client.AspNetCore` só após resolver permissões uma vez — §9.6 |

**Escopo confirmado — modelo plano, sem sub-tenancy.** O `tenant-admin` administra, dentro do próprio
tenant: membros, clients OIDC (aplicações — SPA, mobile, M2M), permission sets, domínios e IdPs. Um tenant
**não** possui "clientes de negócio" abaixo dele; isso contraria o ADR-009 e desmontaria o
`SameTenantRequirement`, cuja comparação é de igualdade, não de subárvore.

---

## Próximo passo

**M0**, na ordem do roadmap da v2.3 §16. Ele deixou de ser fundação rotineira e virou a peça mais crítica
do projeto: com demonstração por README, é o primeiro contato do avaliador, e uma falha ali encerra a
leitura antes dos ADRs.

Quatro itens do M0 vêm diretamente de achados e não podem ser esquecidos:

- `KC_FEATURES=organization` no compose — **singular**. Sem isso o Keycloak sobe, o import passa e
  `POST .../organizations` dá 404. Falha silenciosa (A3).
- Remover `offline_access` do `default-roles` do realm — hardening de uma linha, sem o qual a desativação
  de membro não revoga de fato (C8).
- Os dois client scopes em `defaultDefaultClientScopes`: `gateway-roles` (§12.1) e `gateway-tenant`
  (§12.2).
- Bootstrap do `platform-admin` com senha **gerada**, nunca literal no JSON versionado (C13).

**Antes de codar:** vale `git init`. O projeto vai crescer, e versionar desde as specs preserva a linha
evolutiva que hoje depende de nomes de arquivo.

---

## Pendências

**Nenhuma pendência de decisão.** A9 fechou na v2.2; as lacunas de arquitetura e a reconciliação com o
CleanStart fecharam na v2.3 (§0 tem o changelog). Não é mais necessário rodar o `solution-architect`.

### O que o CleanStart já entrega (verificado em 2026-09-20)

.NET 10, EF Core 10, Mediator e Mapperly source-generated, FluentValidation, Serilog, OpenTelemetry,
`HybridCache` com Redis, xUnit v3, `ArchitectureTests` e **Outbox por interceptor de `SaveChanges`**.
Não é um `dotnet new` template: clona-se, renomeia-se `CleanStart` → `IdentityGateway` e **apaga-se a
feature `Orders`**, preservando `Domain/Common`, `Application/Common`, os interceptors e os
`ArchitectureTests`.

### Verificações técnicas — decidir no M1, por teste de integração

1. **`searchQuery` filtra atributos de Organization?** É a base da correção de C3 (correlação idempotente
   por `gateway_tenant_id`). A §11.6 já documenta o plano B caso não filtre: manter o mapeamento
   só no banco da Gateway e tratar o 409 como sinal de reconciliação pendente.
2. **O toggle de Organizations por realm é necessário além da feature flag de build?** (A3)

Ambas se resolvem com o Keycloak real rodando — não vale especular antes.

---

## Como ler a revisão

`revisao-critica.md` tem 33 achados. Se for consultar só uma parte:

- **Os dois de maior alcance:** CI-8 (o critério de pronto era mais estreito que o princípio que deveria
  provar) e a dupla **C1 + C2**, que precisam ser corrigidas **juntas** — corrigir o claim `tenant_id`
  isoladamente reabriria o sistema com o IDOR de sub-recurso ativo.
- **A seção "O que a revisão NÃO conseguiu atacar"** calibra o resto: sete dos oito alvos examinados
  resistiram inteiros.
- **Leitura obrigatória antes de mexer no Outbox:** o item 4 dessa seção registra que **o ADR-006 está
  certo**. C3 e C4 atacam a *busca* de que ele depende e o *retry do HTTP*, não o padrão. Quem ler C3/C4
  isoladamente conclui que o Outbox foi um erro de desenho — e não foi.

---

## Lições de processo (para as próximas rodadas de agentes)

1. **Mensagens entre agentes truncam por volta de 6–7 mil caracteres.** Todos os revisores tiveram
   relatórios cortados. Instrua desde o início: *"envie em mensagens de no máximo ~3000 caracteres, cada
   uma completa em si mesma; não anuncie a próxima e pare"*.
2. **Subagentes podem não ter WebSearch/WebFetch.** O revisor de arquitetura não tinha, e avisou — o que
   foi correto da parte dele. As verificações externas tiveram de ser feitas pelo coordenador e
   repassadas. Ou dê a ferramenta ao agente, ou reserve o papel de verificador para quem a tem.
3. **Um agente "idle" não falhou** — encerrou o turno e continua retomável por `SendMessage`, com contexto
   intacto. Só encerre de fato quando não precisar mais dele.
4. **Verificar vale a pena, e nos dois sentidos.** Das cinco afirmações checadas contra documentação
   oficial e código-fonte, três confirmaram suspeitas (e uma virou o achado crítico C1) e **duas
   inocentaram a spec** — a API `DisableForUnsafeHttpMethods()` existe, e `roles` está no
   `DefaultInboundClaimTypeMap`. Sem verificar, dois achados falsos teriam entrado no relatório.
