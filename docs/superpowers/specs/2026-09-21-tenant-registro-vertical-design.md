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

#### O `Tenant` ganha um segundo construtor, só para o EF

> **Corrigido durante a implementação.** O desenho original afirmava que o construtor de quatro parâmetros
> bastaria. Não basta, e o erro só apareceu ao gerar a migration.

A §11.1 anota `private Tenant() { }` como "exigido pelo EF Core", e isso continua não sendo verdade — mas a
premissa oposta, de que o construtor existente serviria inteiro, também era falsa.

O EF Core casa parâmetros de construtor com propriedades mapeadas **escalares**. `TenantId` e `TenantSlug`
são conversões de valor único e casam normalmente; `Plan` **não casa**, por ser value object de múltiplos
campos. Tanto `OwnsOne` (navegação) quanto `ComplexProperty` falham com:

```
No suitable constructor was found for the type 'Tenant'.
    Cannot bind 'plan' in 'Tenant(TenantId id, string name, TenantSlug slug, Plan plan)'
```

É limitação em aberto do EF Core 10.0.12 ([dotnet/efcore#31621](https://github.com/dotnet/efcore/issues/31621)),
não erro de configuração — as duas estratégias de mapeamento foram testadas.

**A solução são dois construtores privados:** um de três parâmetros, que só o ORM usa e que carrega o
`Plan = null!`; outro de quatro, com o plano obrigatório, que é o único que o domínio chama, via `Register`.
Assim o `null!` fica confinado ao construtor do ORM, e o compilador continua exigindo o plano em todo caminho
de criação legítimo — um caminho novo que o esquecesse não compila.

Descartada a alternativa de um construtor único sem o plano: ela funciona, mas troca a garantia do compilador
por uma convenção, e o erro reapareceria só em uso.

**O risco que permanece:** se o casamento de nomes quebrar — renomear um parâmetro, por exemplo — o erro é de
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

```csharp
builder.Property<uint>("xmin")
    .HasColumnName("xmin")
    .HasColumnType("xid")
    .ValueGeneratedOnAddOrUpdate()
    .IsConcurrencyToken();
```

Sombra porque `xmin` é coluna de sistema do PostgreSQL e o domínio não deve carregar um campo de versão que
só o ORM entende. O efeito é a cláusula `WHERE xmin = @original` no `UPDATE`, que é o que a §6.1 exige para
`OccupiedSeats`.

> **Corrigido durante a implementação, em dois pontos.** O desenho original usava `IsRowVersion()` e afirmava
> que a coluna não apareceria na migration. Ambos errados: sem `HasColumnType("xid")` o EF trata `xmin` como
> coluna comum, e mesmo com ele a emite no `CreateTable`. A linha foi **removida à mão** da migration —
> criá-la falharia, porque o PostgreSQL já tem `xmin` em toda tabela.
>
> A correção manual é única, não recorrente: depois desta migration, `xmin` está nos dois snapshots que o
> *differ* compara, então nunca reaparece. Verificado gerando uma migration de conferência — veio vazia, com
> o snapshot intocado. Só volta a morder quem apagar e refizer **esta** migration do zero, e o comentário no
> código avisa disso.

Mapeado agora porque é schema e muda barato; o behavior de retry que o acompanha fica para o M1, quando
houver comando que o exercite.

#### Repositório e catálogo

`TenantRepository` (`Persistence/Repositories/`): `Add` não salva — o commit é do `TransactionBehavior`, e é
isso que põe o `INSERT` e o Outbox na mesma unidade de trabalho. `SlugExistsAsync` usa `AnyAsync`, que
funciona com o conversor porque a comparação é por igualdade simples.

`PlanCatalog` (`Configuration/`) lê `PlanOptions` bindado da seção `Plans` com `Validate(...)` mais
`ValidateOnStart` — catálogo inválido derruba na subida, não na primeira requisição. `Find` é
case-insensitive e devolve `Plan?`.

> **Corrigido durante a implementação.** `ValidateOnStart` **sozinho é no-op**: sem nenhuma regra registrada,
> não há o que validar, e a promessa de derrubar na subida era falsa.
>
> A correção aparente — `[Range]` em `PlanDefinition` mais `ValidateDataAnnotations()` — **também não
> funciona**, e foi verificada: `PlanOptions` herda de `Dictionary`, e as anotações só valem para as
> propriedades do objeto raiz, nunca para os **valores** do dicionário, que é onde os limites moram. Um
> `maxUsers: -5` passava limpo.
>
> O que funciona é um `Validate(...)` escrito à mão percorrendo `planos.Values`. Verificado subindo a Api com
> `maxUsers: -5`: `OptionsValidationException: Plans: nenhum plano pode ter maxUsers ou maxClients negativo`.
> Sem isso, o limite negativo só apareceria como `ArgumentOutOfRangeException` no construtor do `Plan`, no
> meio do primeiro registro de tenant — catálogo mal configurado disfarçado de falha de requisição.
>
> A `case-insensitivity` **sobrevive ao binding**, o que não era óbvio e foi confirmado por dois testes
> independentes: o binder popula a instância já criada pelo construtor sem parâmetro, em vez de substituí-la.
> É justamente por isso que `PlanOptions` **herda** de `Dictionary` em vez de expor um como propriedade — na
> segunda forma, o binder criaria um dicionário novo, com comparador padrão, e a busca por `"FREE"` quebraria
> em silêncio.

Registro: repositório `Scoped`, catálogo `Singleton` (config imutável).

### 3 · Api

#### Os testes em skip apontam para o verbo errado

`SegurancaTests` usa `GET /api/v1/tenants`; a fatia entrega `POST`. Reativar sem mais nada os manteria
vermelhos — o roteamento devolve `404` antes da autorização, que é o motivo original do skip.

**Decisão:** apontar os dois para `POST`, sem corpo. A autorização roda antes do model binding, então o `401`
acontece independentemente do corpo. Não amplia escopo e destrava os dois skips de verdade.

#### Módulo Carter com rota literal

`Api/Modules/TenantsModule.cs`, **`public sealed`**, implementando `ICarterModule`.

> **Corrigido durante a implementação.** O desenho pedia `internal`, e isso **não funciona**: o `AddCarter`
> enumera `GetExportedTypes()`, e `InternalsVisibleTo` cobre chamada direta, não reflexão. Com o módulo
> `internal`, `MapCarter()` não mapeia rota nenhuma — toda requisição responde `404`, **sem erro de startup**.
> Falha silenciosa completa.
>
> Junto com isso, o `AddCarter` passou a receber o assembly da Api explicitamente
> (`new DependencyContextAssemblyCatalog(typeof(DependencyInjection).Assembly)`): o padrão resolve o *entry
> assembly*, que sob `WebApplicationFactory<Program>` é o host de teste, não a Api.
>
> Verificado por mutação: revertendo o módulo para `internal`, 7 dos 15 testes funcionais falham.

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

> **Descoberto durante a implementação: a policy não funcionava, e o motivo é o mesmo que a §12.1 alerta.**
> O `JwtSecurityTokenHandler` remapeia o claim curto `roles` para a URI longa
> (`http://schemas.microsoft.com/ws/2008/06/identity/claims/role`) **antes** de a policy comparar. Resultado:
> `403` mesmo com token correto carregando o claim correto.
>
> A correção é `options.MapInboundClaims = false` no `AddJwtBearer` — os claims passam a chegar como o
> emissor os escreveu, que é também o que um IdP externo entrega.
>
> **E essa correção trouxe uma regressão**, que a revisão pegou: com o remapeamento desligado, o `sub`
> também deixa de virar `ClaimTypes.NameIdentifier`, e o `HttpCurrentUser` — que lia só a forma longa —
> passou a devolver nulo para **todo** usuário autenticado. Invisível à suíte, porque nenhuma entidade
> implementa `IAuditable` ainda: o primeiro agregado auditável é que nasceria com `CreatedBy` nulo.
> O `HttpCurrentUser` passa a ler `sub` com a URI longa como alternativa, e `HttpCurrentUserTests` cobre as
> duas formas.
>
> **A lição de processo:** a policy foi dada por pronta com build verde e revisão aprovada, e estava quebrada.
> Só apareceu quando um teste funcional exercitou o caminho autorizado, três tarefas depois. Verde sem teste
> que exercite o caminho não é evidência de nada.

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

- [x] `dotnet build` sem avisos, com `TreatWarningsAsErrors` — 0 erros, 0 avisos nos 10 projetos
- [x] Suíte verde, **com os 4 skips reativados** e nenhum skip novo — **209 testes, 0 falhas, 0 skips**
      (109 domínio · 35 application · 18 arquitetura · 32 integração · 15 funcional)
- [x] `POST /api/v1/tenants` responde `202` com `Location` e corpo; `401`, `403`, `400` e `409` cobertos
- [x] A atomicidade provada nos dois estados, não só no caminho feliz
- [x] Documentação afetada atualizada no mesmo PR

**Nota sobre o método.** Quatro provas desta fatia foram feitas por **mutação**, não por leitura: quebrar
deliberadamente o código e confirmar que o teste falha. Ela pegou o que o verde não pegaria — a atomicidade
do Outbox, a resolução das portas no contêiner, os dois testes de `401` e a regressão da identificação do
usuário. Três defeitos desta fatia chegaram à revisão com build verde e só apareceram quando algo exercitou
o caminho: a policy `PlatformAdmin`, o `ValidateOnStart` do catálogo e o `HttpCurrentUser`.

## Próxima fatia

O consumidor do provisionamento: lê a mensagem do Outbox, cria a Organization no Keycloak, chama
`MarkProvisioned` e envia o convite do `initialAdminEmail` — os três passos da §9.1. Depois, `GET /tenants` e
`GET /tenants/{id}/provisioning`, que fecham o `Location` desta fatia.
