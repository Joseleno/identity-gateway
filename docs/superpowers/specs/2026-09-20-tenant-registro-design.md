# Agregado `Tenant` — fatia de registro

> **Data:** 2026-09-20 · **Marco:** M0 · **Status:** aprovado, pronto para implementar
>
> Primeira fatia do primeiro agregado do IdentityGateway. Referência normativa:
> [`especificacao-arquitetural-v2.3.md`](../../especificacao-arquitetural-v2.3.md) §6.1, §6.2 e §11.1.

---

## Por que esta fatia primeiro

O repositório tem a fundação do CleanStart funcionando e **nenhum código de domínio próprio**. Quatro regras
de arquitetura estão em `Skip` porque não há agregado nem handler para inspecionar — a guarda `NotBeEmpty`
dispara para a regra não passar em vacuidade.

O `Tenant` é o agregado raiz de todo o resto: `Member`, `PermissionSet` e `ClientApplication` referenciam
`TenantId`. Escrevê-lo primeiro estabelece os padrões que os outros vão copiar e **reativa as quatro
regras**, que é o sinal de que a fundação está sendo usada e não apenas presente.

## Escopo

**Nesta fatia:** o caminho de registro — `Register`, `MarkProvisioned`, `MarkProvisioningFailed`,
`ReserveSeat`, `ReleaseSeat`, os value objects, o enum de estados, os erros e dois domain events.

**Fora dela, e por quê:**

| Adiado | Motivo |
|---|---|
| `Suspend`, `Reactivate`, `Terminate` | Dependem do Outbox publicando de verdade (o `PorTipo` está vazio e o broker não existe). Escrever a transição sem consumidor produziria código sem como provar que funciona. |
| Handler, repositório, EF, endpoint | Outra camada, outra revisão. A §11.4 já fixa a forma do handler; ele entra quando houver onde persistir. |
| `IPlanCatalog` | A §11.4 obtém `Plan` dele. Nesta fatia só existe o value object; o catálogo vem com o handler que o consome. |
| `initialAdminEmail` | **Não é estado do `Tenant`.** A §9.1 o exige no `POST /tenants` e explica por quê — sem ele o tenant nasce trancado, já que convidar membros exige `tenant-admin` daquele tenant, que ainda não existiria. Mas quem o consome é o provisionamento, que cria o convite (passo 2 dos três da §9.1). O campo pertence ao `RegisterTenantCommand` e ao consumidor, não ao agregado. Entra na próxima fatia, junto do handler. |
| `EmailDomain` | A §6.1 o lista no estado do `Tenant`, mas domínios e federação são o **M4** (§8: `POST/DELETE /tenants/{tenantId}/domains`). Acrescentar a coleção agora criaria estado sem operação que o alimente. |

## Decisões de design

### Value objects

| Tipo | Forma | Por quê |
|---|---|---|
| `TenantId` | `readonly record struct` sobre `Guid`, criado com `Guid.CreateVersion7()` | Igualdade por valor sem alocação — id circula muito (chave de dicionário, listas, comparação em loop). O tipo próprio impede passar um `MemberId` no lugar. v7 é ordenável por tempo, o que serve ao índice no Postgres. |
| `TenantSlug` | `sealed class : ValueObject`, com `Create` devolvendo `Result<TenantSlug>` | É a assinatura que a §11.4 já usa. Validação na fronteira: se a instância existe, passou pela checagem. |
| `Plan` | `sealed class : ValueObject` com `Tier`, `MaxUsers`, `MaxClients` | A §6.1 o descreve assim. Construído pelo catálogo, não livremente pelo chamador. |

**Regra do slug** — minúsculas, dígitos e hífen; 3 a 63 caracteres; sem hífen nas pontas nem consecutivo.
A entrada é aparada e normalizada para minúsculas antes de validar.

O limite de 63 é o maior rótulo DNS válido. A regra é conservadora de propósito: o slug vira o *alias* da
Organization no Keycloak e pode vir a ser subdomínio ou segmento de URL. Apertar a regra depois quebraria
tenants já cadastrados; afrouxá-la não quebra ninguém.

```
"Acme-Corp"  -> acme-corp     normaliza
"acme"       -> acme
"ac"         -> recusa        curto demais
"-acme"      -> recusa        hífen na ponta
"acme--corp" -> recusa        hífen consecutivo
"acme_corp"  -> recusa        underscore não é válido em DNS
"acmé"       -> recusa        fora de [a-z0-9-]
```

### Estados

`TenantStatus` declara os **sete** estados da §6.2 — `Pending`, `Active`, `Suspending`, `Suspended`,
`Terminating`, `Terminated`, `ProvisioningFailed` — embora esta fatia implemente transições de apenas
alguns. O enum é a máquina de estados documentada; valores faltando convidariam a inventar outros depois.

### Erro de negócio × erro de programação

A distinção atravessa o agregado e é o ponto mais importante do desenho:

| Operação | Devolve | Por quê |
|---|---|---|
| `ReserveSeat` | `Result` | "Não há vaga" e "tenant não está ativo" são respostas de negócio legítimas. Quem chama decide o que fazer. |
| `ReleaseSeat` | `void`, **lança** em zero | Liberar vaga inexistente só acontece se o chamador estiver errado. |
| `MarkProvisioned` | `void`, **lança** em estado inválido | Transição inválida é erro de programação. Mas é **idempotente na entrada**: mesma mensagem, mesmo id externo, já `Active` → retorna sem efeito. |
| `MarkProvisioningFailed` | `void`, **lança** em estado inválido | Mesma natureza: só faz sentido a partir de `Pending`. Idempotente em `ProvisioningFailed` — o consumidor de Fault pode reentregar. |

### Por que `MarkProvisioningFailed` entra nesta fatia

Sem ela, `ProvisioningFailed` seria **estado inalcançável**: o enum o declara, `MarkProvisioned` aceita sair
dele, e nada nunca entraria. A §9.1 diz que "se os retries se esgotarem, o tenant vai para
`ProvisioningFailed` e o evento fica disponível para retry manual", e a §11.5 confirma que um consumidor de
Fault é quem marca.

Quem chama é da próxima fatia, mas a transição é do caminho de registro e custa uma linha. Um estado
declarado que nenhuma operação alcança é pior que um estado ausente: ele aparece na máquina de estados
documentada e ninguém descobre que é decorativo até precisar dele.

**`ReleaseSeat` não é idempotente por desenho.** A v2.0 usava `Math.Max(0, OccupiedSeats - 1)`, e a §11.1
explica por que isso era pior que nada: o clamp escondia a dupla liberação em vez de denunciá-la. Um POST
de desativação repetido (timeout + retry do cliente) decrementava duas vezes, o `xmin` não pega — ele
detecta escrita simultânea, não repetida — e, repetido N vezes, o contador chegava a zero com o tenant
cheio. O limite do plano deixava de existir em silêncio.

`DomainInvariantViolation` não existe na fundação e é criada em `Domain/Common`, para os agregados
seguintes.

### Divergência deliberada do código da spec

A §11.1 escreve `Id = TenantId.New()` num inicializador de objeto. O `Entity<TId>` da fundação exige o id
pelo construtor e o expõe `get`-only. **Segue-se a fundação:** o código da spec é ilustrativo e foi escrito
antes de ela existir aqui. Pelo mesmo motivo, o método é `RaiseDomainEvent`, não `Raise`.

> **O código de referência da §11 não é a fonte de verdade — o texto normativo é.** A primeira versão deste
> design saiu da §11.1 e por isso omitia `MarkProvisioningFailed` (exigido pela §9.1), `initialAdminEmail`
> (exigido pela §9.1 e pela §8) e `EmailDomain` (listado na §6.1). O código de referência ilustra a forma;
> as regras estão em §6, §8 e §9. Ler o código primeiro produz um agregado que compila e não atende à
> especificação.

## Arquivos

```
src/IdentityGateway.Domain/
  Common/DomainInvariantViolation.cs
  Tenants/
    Tenant.cs                      Register, MarkProvisioned,
                                   MarkProvisioningFailed,
                                   ReserveSeat, ReleaseSeat
    TenantId.cs                    readonly record struct
    TenantSlug.cs                  ValueObject + Create
    Plan.cs                        ValueObject
    PlanTier.cs                    enum
    TenantStatus.cs                enum (7 estados)
    TenantErrors.cs                NotActive, SeatLimitReached, SlugInUse, UnknownPlan
    Events/TenantRegistered.cs
    Events/TenantActivated.cs

src/IdentityGateway.Infrastructure/
  Persistence/Outbox/OutboxEventTypes.cs    registrar os dois eventos

tests/IdentityGateway.Domain.UnitTests/Tenants/
  TenantSlugTests.cs
  TenantTests.cs

tests/IdentityGateway.ArchitectureTests/
  RegrasDeDominioTests.cs          remover Skip de 2 regras
  RegrasDeMensageriaTests.cs       (as outras 2 seguem em Skip: dependem de handler)
```

## Testes

TDD: teste antes da implementação, um comportamento por vez.

**`TenantSlug`** — normalização (`Acme-Corp` → `acme-corp`, espaços aparados) e cada recusa: vazio, curto,
longo, underscore, acentuado, hífen na ponta, hífen consecutivo.

**`Tenant`**

| Comportamento | Asserção |
|---|---|
| `Register` | nasce `Pending`, levanta `TenantRegistered` com o slug, ids distintos entre chamadas |
| `MarkProvisioned` | vai a `Active`, guarda o id externo, levanta `TenantActivated` |
| `MarkProvisioned` repetido, mesmo id | não levanta segundo evento |
| `MarkProvisioned` em estado inválido | lança `DomainInvariantViolation` |
| `MarkProvisioned` a partir de `ProvisioningFailed` | ativa — é o retry manual da §9.1 |
| `MarkProvisioningFailed` | vai a `ProvisioningFailed` a partir de `Pending` |
| `MarkProvisioningFailed` repetido | não lança — o consumidor de Fault pode reentregar |
| `MarkProvisioningFailed` a partir de `Active` | lança `DomainInvariantViolation` |
| `ReserveSeat` | incrementa e devolve sucesso |
| `ReserveSeat` com tenant não `Active` | `TenantErrors.NotActive`, contador intacto |
| `ReserveSeat` no limite do plano | `TenantErrors.SeatLimitReached`, contador não passa de `MaxUsers` |
| `ReleaseSeat` | decrementa |
| **`ReleaseSeat` em zero** | **lança `DomainInvariantViolation`** |

Não se testa `ValueObject`, `Result` nem `AggregateRoot`: já têm cobertura na fundação.

## Critério de pronto

- [ ] Build limpo com `TreatWarningsAsErrors`
- [ ] Testes novos passam, e cada um foi visto **falhando** antes da implementação
- [ ] `Entidades_NaoExpoemSetterPublico` e `RaizesDeAgregado_ExpoemColecoesSomenteLeitura` saem do `Skip` e
      passam
- [ ] As duas regras reativadas foram vistas **reprovando** com uma violação introduzida de propósito —
      sem isso não há prova de que não voltaram vazias
- [ ] `TenantRegistered` e `TenantActivated` registrados em `OutboxEventTypes.PorTipo`
- [ ] As outras duas regras (`Handlers_SaoSealed`, `CommandsEQueries_SaoRecord`) seguem em `Skip`, com o
      motivo atualizado: dependem do handler, não do agregado
- [ ] **Nenhum estado do enum é inalcançável** dentro do que a fatia cobre: `Pending`, `Active` e
      `ProvisioningFailed` têm transição que os atinge. `Suspending`, `Suspended`, `Terminating` e
      `Terminated` seguem sem entrada — declarados, e adiados por escrito acima

## Próxima fatia

O handler `RegisterTenantCommand` (§11.4) com `IPlanCatalog`, `ITenantRepository`, a configuração EF e a
migration — que reativa as duas regras restantes e destrava os dois testes de `401`.
