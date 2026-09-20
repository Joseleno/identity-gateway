# Handoff — onde paramos e como continuar

> **Data:** 2026-09-20 · **Fase:** specs fechadas, fundação do CleanStart no lugar, **M0 não iniciado**.
> A fundação existe, compila e tem testes; nenhuma regra de negócio do IdentityGateway foi escrita.
>
> **Repositório:** <https://github.com/Joseleno/identity-gateway> (público, branch `main`)

---

## Situação

O brainstorm fechou, a especificação passou por revisão crítica em três frentes, e **todas as pendências
de decisão foram resolvidas**. O repositório foi refeito a partir do template CleanStart — que é a origem
correta e não tinha sido usada: o histórico anterior nascia de um `git init` vazio, e o esqueleto criado
com `dotnet new` tinha a topologia certa e nenhum conteúdo.

**Documento vigente para implementar: `especificacao-arquitetural-v2.3.md`.**

### O que já está no repositório

| Commit | Conteúdo |
|---|---|
| `chore:` | Repositório a partir do template CleanStart — a fundação inteira |
| `docs:` | Os oito documentos — a linha evolutiva completa — e o README |

**A fundação**, vinda do template e renomeada `CleanStart` → `IdentityGateway`:

| Camada | O que já existe |
|---|---|
| `Domain/Common` | `AggregateRoot`, `Entity`, `Result`, `ValueObject`, `IAuditable`, `ISoftDeletable`, `IDomainEvent`, `IHasDomainEvents` |
| `Domain/Errors` | `Error`, `ErrorType`, `DomainErrors` |
| `Domain/ValueObjects` | `Email` |
| `Application/Common` | `ICommand`/`IQuery` e handlers, 8 abstrações, behaviors de Logging, Validation, Transaction e CacheInvalidation |
| `Infrastructure` | Interceptors de auditoria, domain event e soft delete; Outbox completo; HybridCache; options validadas |
| `Api` | Middlewares de correlação, log, exceção e cabeçalhos de segurança; `ResultExtensions`; `JwtTokenService` |
| `tests/` | 4 suítes de regras de arquitetura + testes de domínio, behaviors, DI e segurança |

Também entraram `Dockerfile`, `docker-compose.yml` (postgres, redis, seq, jaeger), `.editorconfig`,
`global.json` e o workflow de CI — nada disso existia no esqueleto anterior.

Verificado em 2026-09-20: **build limpo** (0 avisos, 0 erros com warnings-as-errors) e **111 testes —
105 passam, 6 em skip documentado, 0 falham**.

**Os 6 skips são deliberados e se reativam sozinhos no M0:** quatro regras de arquitetura cuja guarda
`NotBeEmpty` dispara enquanto não há agregado nem handler para inspecionar (a guarda existe para a regra
não passar em vacuidade), e dois testes de 401 que dependem de um endpoint protegido existir — sem rota
registrada, o roteamento responde 404 antes de a autorização ser consultada.

**O que saiu do template:** a feature de exemplo (`Orders`, `Customers`, migrations, seeder,
`ExchangeRateClient`), os value objects `Money` e `Document`, e o `DevTokenModule` — que emite JWT sem
senha, e num gateway de identidade seria um emissor paralelo ao Keycloak.

**Ainda não existe:** nenhuma entidade, handler ou endpoint do IdentityGateway, nem o realm Keycloak. O
`docker-compose.yml` existe mas ainda não tem keycloak, rabbitmq, mailpit nem a segunda API.

### Os documentos

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

**Continuar o M0**, na ordem do roadmap da v2.3 §16. A fundação já está no lugar; falta o domínio. O M0
deixou de ser trabalho rotineiro e virou a peça mais crítica do projeto: com demonstração por README, é o
primeiro contato do avaliador, e uma falha ali encerra a leitura antes dos ADRs.

### 1. ~~Trazer a fundação do CleanStart~~ — feito

Feito refazendo o repositório a partir do template, como deveria ter sido desde o início. `AggregateRoot<T>`,
`Entity`, `ValueObject`, `Result<T>`, `Error`, `ICommand`/`IQuery` e os interceptors de `SaveChanges` que
implementam o Outbox do ADR-006 já existem e têm teste. Ver "O que já está no repositório", acima.

**O primeiro agregado do IdentityGateway é o passo seguinte** — e ele reativa os 4 testes de arquitetura
hoje em skip, que é o sinal de que a fundação está de fato sendo usada.

### 2. Os quatro itens críticos do realm e do compose

Vêm diretamente de achados da revisão e não podem ser esquecidos:

- `KC_FEATURES=organization` no compose — **singular**. Sem isso o Keycloak sobe, o import passa e
  `POST .../organizations` dá 404. Falha silenciosa (A3). O M0 inclui um *smoke test* que falha
  explicitamente nesse 404, para o erro aparecer na subida e não na primeira demonstração.
- Remover `offline_access` do `default-roles` do realm — hardening de uma linha, sem o qual a
  desativação de membro não revoga de fato (C8).
- Os dois client scopes em `defaultDefaultClientScopes`: `gateway-roles` (§12.1) e `gateway-tenant`
  (§12.2).
- Bootstrap do `platform-admin` com senha **gerada**, nunca literal no JSON versionado (C13). Um
  teste de CI falha se houver credencial literal no bootstrap.

### 3. O resto do M0

`docker-compose.yml` unificado (§15: keycloak, postgres com dois bancos, rabbitmq, mailpit, redis,
seq, jaeger e as duas APIs), health checks, CI com os testes de arquitetura, e auditoria e
observabilidade desde já.

Parte disso veio do template e é ponto de partida, não folha em branco: o compose já sobe postgres,
redis, seq e jaeger, o CI já tem build, testes e imagem Docker, e os health checks `/health/live` e
`/health/ready` já respondem (com teste). Falta acrescentar keycloak, rabbitmq, mailpit e a segunda API,
e ligar os testes de arquitetura no CI.

### 4. Ao final, atualizar o README

O README atual é **de estágio atual** e deliberadamente **não tem seção de `curl`** — prometer
`docker compose up` sem código funcionando falharia exatamente onde a §16 diz que não se pode falhar.
Quando o M0 fechar, acrescentar as duas demonstrações que a §16 exige:

1. `docker compose stop keycloak` → `POST /tenants` responde `202` → `docker compose start keycloak`
   → o tenant vira `Active` sozinho.
2. Com token do tenant A: rota do tenant B dá **403**; `memberId` do tenant B dentro da rota do
   tenant A dá **404**.

E trocar o estado do M0 de 🔨 para ✅ na tabela de marcos.

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

## Convenções fixadas no esqueleto

Decisões tomadas ao montar a solution que a spec não registra, e que valem para os próximos projetos:

| Convenção | Por quê |
|---|---|
| Solution em **`.slnx`** | Formato novo do .NET, e é a convenção canônica do time |
| **`Directory.Packages.props`** com versões centralizadas | Um lugar só para versionar pacote; evita divergência entre dez projetos |
| **`Directory.Build.props`** com `TreatWarningsAsErrors` | Ligado desde o primeiro commit — depois de acumular avisos, ninguém liga |
| **xUnit v3** | Herdado do CleanStart, que é a base do repositório |
| **`.gitattributes` com `eol=lf`** e `core.autocrlf false` | Repositório nasce com terminadores consistentes, em vez de normalizar depois |
| **Partir do template, não de `dotnet new`** | O CleanStart é um *template repository*: usá-lo dá a fundação inteira com testes. Criar a topologia à mão dá pastas vazias que parecem prontas — foi o que aconteceu na primeira tentativa e custou um refazimento |

> **Cuidado ao editar estes documentos por script em Windows.** Script reescreve o arquivo inteiro em
> CRLF e polui o diff; pior, `perl -e` inline lê o próprio script como Latin-1 e transforma os acentos
> em mojibake (`fundação` → `fundaÃ§Ã£o`) sem avisar. Editar com ferramenta que preserve o encoding, ou
> normalizar para LF e conferir os acentos antes de commitar. Já aconteceu duas vezes.

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
