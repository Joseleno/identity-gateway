# Vertical de registro de tenant — Application, Infrastructure e Api

> **Data:** 2026-09-21 · **Marco:** M0 · **Classificação:** arquitetural
>
> Sucede o [agregado `Tenant`](2026-09-20-tenant-registro-design.md), que entregou o domínio, e consome o
> [handoff](2026-09-21-registro-de-tenant-handoff.md), que fixou o escopo e três decisões.
>
> Referência normativa: [`especificacao-arquitetural-v2.3.md`](../../especificacao-arquitetural-v2.3.md)
> §6.1, §8, §9.1, §10.1, §11.4, §11.7 e §12.1.

---

## Por que esta fatia

O agregado `Tenant` existe e não tem como ser exercitado: nada o persiste, nada o expõe. A fatia fecha o
caminho inteiro de `POST /api/v1/tenants` até a linha no banco com a mensagem no Outbox, que é o primeiro
fluxo ponta a ponta do projeto.

Destrava os **4 skips** restantes da suíte: `Handlers_SaoSealed` e `CommandsEQueries_SaoRecord` em
`RegrasDeMensageriaTests` (que aguardam um handler existir) e os dois testes de `401` em `SegurancaTests`
(que aguardam um endpoint protegido).

## Escopo

**Entra:** command, handler, duas portas e validator; mapeamento EF, índice único, `xmin`, repositório,
catálogo de planos e migration; módulo Carter, policy `PlatformAdmin` e a resposta `202`.

**Fica de fora, por decisão registrada:**

| Item | Por quê |
|---|---|
| `GET /tenants` e `GET /tenants/{id}/provisioning` | Listagem, paginação e o override auditado da §10.1 são fatia própria do M0. |
| Chamada ao Keycloak | O provisionamento é do consumidor do Outbox. O handler não conhece o Keycloak. |
| Convite do `initialAdminEmail` | Passo 2 dos três da §9.1, no consumidor. Aqui o campo é validado e carregado. |
| `ConcurrencyRetryBehavior` (§11.10) | Existe para `ReserveSeat`, que é convite de membro (M1). `INSERT` não tem conflito de versão. |
| `Asp.Versioning.Http` | Ver ADR abaixo. |
| `EmailDomain` | Domínios e federação são o M4, como a fatia anterior já registrou. |

---

## Decisões de design

### 1 · Application

`RegisterTenantCommand(string Name, string Slug, string PlanCode, string InitialAdminEmail)` →
`ICommand<TenantId>`.

O `InitialAdminEmail` é acréscimo deliberado ao que a §11.4 mostra: a §9.1 e a §8 o exigem no `POST`, e sem
ele o tenant nasce trancado — convidar membros exige `tenant-admin` daquele tenant, que ainda não existiria.
A §11.4 é código de referência anterior a essa decisão, e o texto normativo prevalece.

`RegisterTenantHandler` segue a §11.4: valida o slug, checa unicidade, resolve o plano, registra, persiste.
Erro de negócio não lança — devolve `Result<TenantId>`.

**Duas portas**, em `Application/Common/Abstractions/`:

| Porta | Assinatura |
|---|---|
| `ITenantRepository` | `void Add(Tenant)`, `Task<bool> SlugExistsAsync(TenantSlug, CancellationToken)` |
| `IPlanCatalog` | `Plan? Find(string planCode)` |

> **Divergência de caminho, deliberada.** A §7 desenha `Application/Abstractions/`, mas o repositório já tem
> `Application/Common/Abstractions/` vindo do template, com as 8 abstrações da fundação. Seguir o repo evita
> duas pastas de abstração com o mesmo papel.

`RegisterTenantValidator` cobre **presença**: nome obrigatório e ≤ 200, slug obrigatório, `planCode`
obrigatório, e-mail obrigatório e com forma de e-mail. A validação de **forma** do slug não é duplicada
aqui — ela vive em `TenantSlug.Create`, que devolve `Result`.

### 2 · Infrastructure

#### O `Tenant` não precisa de construtor sem parâmetro

A §11.1 anota `private Tenant() { }` como "exigido pelo EF Core". **Não é.** O EF Core mapeia construtor
parametrizado casando parâmetros com propriedades por nome: `private Tenant(TenantId id, string name,
TenantSlug slug, Plan plan)` casa com `Id`, `Name`, `Slug` e `Plan`. O que não passa pelo construtor
(`Status`, `ExternalOrganizationId`, `OccupiedSeats`, `OverSubscribed`) o EF escreve no campo de apoio, que
é o que já faria por causa do `private set`.

Mantê-lo como está evita a instância com três `null!` — um agregado momentaneamente inválido para agradar o
ORM. Encerra a pendência que a fatia anterior adiou por não ter a configuração EF na frente.

**O risco que isso abre:** se o construtor deixar de casar — renomear um parâmetro, por exemplo — o erro é de
materialização em runtime, não de compilação. O teste de round-trip da seção 4 existe para isso.

#### Mapeamento: colunas planas na tabela `tenants`

| Propriedade | Coluna | Forma |
|---|---|---|
| `Id` (`TenantId`) | `id` `uuid` | Conversor `TenantId ↔ Guid` |
| `Name` | `name` `varchar(200)` | `IsRequired` |
| `Slug` (`TenantSlug`) | `slug` `varchar(63)` | Conversor `TenantSlug ↔ string` |
| `Plan.Tier` | `plan_tier` `varchar(20)` | Owned, enum como texto |
| `Plan.MaxUsers` | `plan_max_users` `integer` | Owned |
| `Plan.MaxClients` | `plan_max_clients` `integer` | Owned |
| `Status` | `status` `varchar(20)` | Enum como texto |
| `ExternalOrganizationId` | `external_organization_id` `text` nullable | — |
| `OccupiedSeats` | `occupied_seats` `integer` | — |
| `OverSubscribed` | `over_subscribed` `boolean` | — |
| *(sombra)* | `xmin` | `IsRowVersion()` |

**`Plan` como owned type achatado** (`OwnsOne` + `HasColumnName`), e não três propriedades soltas no
`Tenant`: a §6.1 o define como value object, e achatá-lo à mão faria o agregado precisar de propriedades
espelho só para o ORM. `OwnsOne` sem tabela separada dá as colunas planas e mantém o value object intacto.

**Enums como texto, não `int`.** Um `SELECT` em produção dizendo `Pending` responde a pergunta; dizendo `0`,
exige o enum aberto ao lado. E a ordem dos membros deixa de ser dado de schema — inserir um estado no meio de
`TenantStatus`, que declara os 7 de antemão, reescreveria o significado das linhas gravadas.

**`TenantSlug` por conversor, não owned:** value object de campo único; owned type criaria um tipo aninhado
no modelo sem ganho. A volta usa `TenantSlug.Create(valor).Value` — o dado gravado já foi validado na
escrita, então falhar ali é corrupção, e deve estourar.

Os nomes de coluna são explícitos, seguindo `OutboxMessageConfiguration`: o repo não tem convenção global de
snake_case.

#### Índice único do slug: global, sem filtro

`ix_tenants_slug`, único sobre `slug`. Sem filtro porque `Tenant` não implementa `ISoftDeletable` — não há
linha excluída a excluir. Global porque o slug vira o *alias* da Organization no Keycloak e potencialmente
subdomínio: o espaço de nomes é do sistema inteiro.

A unicidade fica no banco **além** do `SlugExistsAsync`. Os papéis são distintos e nenhum substitui o outro:
a checagem no handler dá a mensagem de negócio (`409` nomeando o slug); o índice é o que impede duas
requisições concorrentes de gravarem o mesmo slug, já que entre o `SELECT` e o `INSERT` há uma janela.

#### `xmin` como propriedade de sombra

`builder.Property<uint>("xmin").IsRowVersion().HasColumnName("xmin").ValueGeneratedOnAddOrUpdate()`.

Sombra porque `xmin` é coluna de sistema do PostgreSQL e o domínio não deve carregar um campo de versão que
só o ORM entende. Não gera coluna na migration — gera a cláusula `WHERE xmin = @original` no `UPDATE`, que é
o que a §6.1 exige para `OccupiedSeats`.

Mapeado agora porque é schema e muda barato; o behavior de retry que o acompanha fica para o M1, quando
houver comando que o exercite.

#### Repositório e catálogo

`TenantRepository` (`Persistence/Repositories/`): `Add` não salva — o commit é do `TransactionBehavior`, e é
isso que põe o `INSERT` e o Outbox na mesma unidade de trabalho. `SlugExistsAsync` usa `AnyAsync`, que
funciona com o conversor porque a comparação é por igualdade simples.

`PlanCatalog` (`Configuration/`) lê `PlanOptions` bindado da seção `Plans` com `ValidateOnStart` — catálogo
inválido derruba na subida, não na primeira requisição. `Find` é case-insensitive e devolve `Plan?`.

Registro: repositório `Scoped`, catálogo `Singleton` (config imutável).

### 3 · Api

#### Os testes em skip apontam para o verbo errado

`SegurancaTests` usa `GET /api/v1/tenants`; a fatia entrega `POST`. Reativar sem mais nada os manteria
vermelhos — o roteamento devolve `404` antes da autorização, que é o motivo original do skip.

**Decisão:** apontar os dois para `POST`, sem corpo. A autorização roda antes do model binding, então o `401`
acontece independentemente do corpo. Não amplia escopo e destrava os dois skips de verdade.

#### Módulo Carter com rota literal

`Api/Modules/TenantsModule.cs`, `internal sealed`, implementando `ICarterModule`. O `app.MapCarter()` já
existe no `Program.cs` e descobre por varredura — nada muda lá.

```
POST /api/v1/tenants  →  RequireAuthorization("PlatformAdmin")  →  202 + Location
```

#### ADR — versionamento por path literal, sem `Asp.Versioning.Http`

**Contexto.** A §8 determina que "a versão vive no path e o roteamento usa `Asp.Versioning.Http`". O pacote
não está no `Directory.Packages.props`, e o `CONTRIBUTING.md` exige que dependência nova sobreviva à pergunta
"o que quebra se não tiver isso?".

**Decisão.** A rota é escrita literalmente como `/api/v1/tenants`. O pacote entra quando existir uma `v2`.

**Consequências.** A URL produzida é idêntica, e a política da §8 — versão no path, convivência, `Deprecation`
e `Sunset` da RFC 8594 — continua válida como política; o que se adia é o mecanismo. O custo é que a
migração para `v2` exigirá introduzir o `ApiVersionSet` e reescrever as rotas registradas até lá, e que a
letra da §8 fica temporariamente divergente do código — divergência que este ADR registra em vez de deixar
implícita. O ganho é não carregar uma dependência cuja única função é a convivência entre versões que ainda
não existem.

#### Policy `PlatformAdmin` e a sobrecarga que ela exige

```csharp
services.AddAuthorization(options =>
    options.AddPolicy("PlatformAdmin", policy =>
        policy.RequireClaim("roles", "platform-admin")));
```

Claim plano, não `RequireRole`: a §12.1 documenta que `RequireRole` falha com o Keycloak porque o papel chega
aninhado em `realm_access.roles`, e chama isso de "o ponto que mais gera erro nessa integração".
`RequireRole` passaria hoje e quebraria quando o Keycloak entrasse.

**Consequência não prevista no handoff:** `JwtTokenService.Emitir(Guid, string)` não emite claim `roles`, e
os testes do caminho feliz precisam de um token com `platform-admin`. Entra uma sobrecarga
`Emitir(Guid, string, params string[] roles)`, emitindo um claim `roles` por papel. A assinatura atual
continua válida, então nada existente quebra.

#### Request, response e o `202`

`RegisterTenantRequest(string Name, string Slug, string PlanCode, string InitialAdminEmail)` e
`TenantAcceptedResponse(Guid TenantId, string Status)`, ambos `record`, na pasta do caso de uso.

O request espelha o command e a duplicação é deliberada: contrato de fio e contrato interno mudam por razões
diferentes, e colapsá-los faria renomear um campo do command quebrar clientes HTTP.

**`202`, não `201`**, conforme a §8: o tenant não está pronto — o Keycloak só será chamado pelo consumidor do
Outbox. Responder `201` afirmaria um recurso completo. O corpo carrega o id porque um `202` vazio obrigaria o
cliente a um `GET` só para descobri-lo.

`ResultExtensions` ganha `ParaAccepted<TValue>(resultado, localizacao, corpo, correlationId)`, mantendo o
ponto único de tradução que é a razão declarada da classe existir.

**O `Location` aponta para `/api/v1/tenants/{id}/provisioning`, que não existe nesta fatia.** É limitação
conhecida, a fechar no M0: num `202`, o `Location` aponta para onde acompanhar o processamento, e um `404`
temporário ali é menos errado do que omitir o cabeçalho ou apontar para um recurso que minta sobre estar
pronto.

#### Erros

Tudo flui pelo `ResultExtensions` já existente: slug inválido → `Validation` → `400`; slug duplicado →
`Conflict` → `409`; plano inexistente → `Validation` → `400`. Falha do validator é interceptada pelo
`ValidationBehavior` antes do handler.

### 4 · Testes

#### A atomicidade, e por que a pergunta do handoff muda de forma

O handoff pergunta como provar que o evento vai ao Outbox "na mesma transação" do `INSERT`. **Não existe
`BeginTransaction` no código.** O `TransactionBehavior` chama `SaveChangesAsync` uma vez, e o EF Core envolve
um `SaveChanges` numa transação implícita; o `DomainEventInterceptor` roda em `SavingChanges`, acrescentando
os `OutboxMessage` ao change tracker antes do comando ir ao banco. Os dois `INSERT` saem juntos.

A propriedade observável, então, não é "existe uma transação" — é **"o tenant e a mensagem gravam juntos ou
nenhum dos dois"**, e é isso que o teste ataca:

`RegistroDeTenantTests.OTenantEAMensagem_GravamJuntosOuNenhum` — grava um tenant; num contexto novo, tenta
gravar outro com o mesmo slug; afirma que o segundo `SaveChanges` lançou `DbUpdateException` **e** que
`outbox_messages` contém **uma** mensagem, não duas.

O par de estados é o que dá valor ao teste: não sendo atômico, a mensagem do tenant recusado teria ficado
gravada e o contador daria 2.

#### Os níveis, pelo mapa do `CONTRIBUTING.md`

| Nível | Projeto | O que cobre |
|---|---|---|
| Handler | `Application.UnitTests` | Portas em NSubstitute: slug inválido → `Validation`; duplicado → `Conflict`; plano inexistente → `Validation`; sucesso → `Add` chamado, `TenantId` devolvido |
| Validator | `Application.UnitTests` | Campos ausentes, nome > 200, e-mail malformado |
| Mapeamento | `Infrastructure.IntegrationTests` | **Round-trip**: grava, relê em contexto novo, confere os 4 value objects — pega o construtor parametrizado deixando de casar |
| Schema | `Infrastructure.IntegrationTests` | A migration cria `tenants`; o índice único existe e recusa duplicata |
| Repositório | `Infrastructure.IntegrationTests` | `SlugExistsAsync` true/false, com o conversor no meio |
| Catálogo | `Infrastructure.IntegrationTests` | `Find` resolve `free`/`FREE`; `null` para inexistente |
| Endpoint | `Api.FunctionalTests` | `202` + `Location` + corpo; `401` sem token; **`403` sem o papel**; `400` slug inválido; `409` duplicado |

#### O `403` é o teste que hoje não existe

O repo tem testes de autenticação (`401`) e **nenhum** de autorização. Com a policy e a sobrecarga de
`roles`, passa a ser possível provar que a policy está ligada: token válido, sem o papel, recebe `403`. Sem
ele, remover a policy do endpoint não quebraria teste nenhum — é o mesmo raciocínio que `SegurancaTests` já
documenta para o `401`.

#### O que não se testa

`ConcurrencyRetryBehavior` (fora da fatia); o `OutboxWorker` despachando a mensagem (cobertura própria;
repeti-la testaria o worker, não esta fatia); e o `Location` resolvendo — afirma-se o **valor** do cabeçalho,
não que ele responda.

---

## Arquivos

```
src/IdentityGateway.Application/
  Common/Abstractions/ITenantRepository.cs                      novo
  Common/Abstractions/IPlanCatalog.cs                           novo
  Tenants/RegisterTenant/RegisterTenantCommand.cs               novo
  Tenants/RegisterTenant/RegisterTenantHandler.cs               novo
  Tenants/RegisterTenant/RegisterTenantValidator.cs             novo
  DependencyInjection.cs                                        registra o validator

src/IdentityGateway.Infrastructure/
  Persistence/Configurations/TenantConfiguration.cs             novo
  Persistence/Repositories/TenantRepository.cs                  novo
  Configuration/PlanOptions.cs                                  novo
  Configuration/PlanCatalog.cs                                  novo
  Persistence/AppDbContext.cs                                   DbSet<Tenant> internal
  Persistence/Migrations/*_CriacaoDeTenants.cs                  gerada
  DependencyInjection.cs                                        repositório, catálogo, PlanOptions

src/IdentityGateway.Api/
  Modules/TenantsModule.cs                                      novo
  Modules/RegisterTenantRequest.cs                              novo
  Modules/TenantAcceptedResponse.cs                             novo
  Extensions/ResultExtensions.cs                                + ParaAccepted
  Security/JwtTokenService.cs                                   + sobrecarga com roles
  DependencyInjection.cs                                        + policy PlatformAdmin

appsettings.json                                                seção Plans

tests/
  Application.UnitTests/Tenants/RegisterTenant/                 handler + validator
  Infrastructure.IntegrationTests/Persistence/                  mapeamento, schema, repositório, atomicidade
  Infrastructure.IntegrationTests/Configuration/                catálogo
  Api.FunctionalTests/RegistroDeTenantTests.cs                  endpoint
  Api.FunctionalTests/SegurancaTests.cs                         2 skips → POST
  ArchitectureTests/RegrasDeMensageriaTests.cs                  2 skips reativados
```

## Fluxo de entrega

Branch `feat/tenant-registro-vertical`, commits pequenos, **PR no GitHub** ao final — não merge local. O
`CONTRIBUTING.md` descreve o fluxo de PR como o do repositório, e a CI só dispara em `push` na `main` ou em
`pull_request` para `main`: sem PR, ela verifica depois do fato consumado em vez de servir de portão.

O `CONTRIBUTING.md` também determina que um PR resolve uma coisa. Infrastructure e Api são assuntos
separáveis, e a divisão em dois PRs se decide no plano de implementação, quando as tarefas estiverem
fatiadas.

O ADR do versionamento (seção 3) vai no corpo do PR e, aprovado, para a seção de ADRs da especificação.

## Critério de pronto

- `dotnet build` sem avisos, com `TreatWarningsAsErrors`
- `dotnet test` verde, **com os 4 skips reativados** — nenhum skip novo
- `POST /api/v1/tenants` responde `202` com `Location` e corpo; `401`, `403`, `400` e `409` cobertos
- A atomicidade provada nos dois estados, não só no caminho feliz
- Documentação afetada atualizada no mesmo PR

## Próxima fatia

O consumidor do provisionamento: lê a mensagem do Outbox, cria a Organization no Keycloak, chama
`MarkProvisioned` e envia o convite do `initialAdminEmail` — os três passos da §9.1. Depois, `GET /tenants` e
`GET /tenants/{id}/provisioning`, que fecham o `Location` desta fatia.
