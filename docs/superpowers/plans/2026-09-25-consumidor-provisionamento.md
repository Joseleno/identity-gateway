# Consumidor do provisionamento — Plano de Implementação

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fazer o tenant registrado sair de `Pending` sozinho: o Outbox despacha `tenant-registered` para um `ProvisionTenantHandler` que garante a Organization no Keycloak e marca o tenant `Active` — ou `ProvisioningFailed`, por erro permanente ou janela esgotada —, e o status fica consultável em `GET /api/v1/tenants/{id}/provisioning`.

**Architecture:** Sem broker. O `LoggingOutboxPublisher` dá lugar ao `DispatchingOutboxPublisher`, que abre um escopo de DI por mensagem e envia um command do Mediator. A decisão "repetir ou desistir" mora no handler (Application), contada desde `Tenant.RegisteredAt` contra uma janela configurável; o Outbox só precisa trazer a mensagem de volta por tempo suficiente, e uma validação cruzada na subida garante isso. O lado de leitura é uma porta própria (`ITenantQueries`) com projeção sem rastreamento.

**Tech Stack:** .NET 10, Mediator 3.0.2 (source generator), EF Core 10 + Npgsql, xUnit v3 + AwesomeAssertions + NSubstitute, Testcontainers (PostgreSQL 17 e Keycloak 26.7.4), Carter.

**Spec:** [`docs/superpowers/specs/2026-09-25-consumidor-provisionamento-design.md`](../specs/2026-09-25-consumidor-provisionamento-design.md) · normativa: [`docs/especificacao-arquitetural-v2.4.md`](../../especificacao-arquitetural-v2.4.md) (§6.2, §9.1, §11.5, §14.1, §16), que esta fatia sucede com a v2.5.

## Global Constraints

- **Sem broker e sem MassTransit** nesta fatia. Nenhum pacote novo.
- Janela de provisionamento: `Provisioning:MaxPendingHours`, padrão **24**, faixa **1–720**.
- Novos padrões do Outbox: `MaxRetryDelaySeconds` **60** (era 300), `MaxAttempts` **1500** (era 5), limite de validação de `MaxAttempts` **10 000** (era 50). `BaseRetryDelaySeconds` continua **10**.
- A rede mínima do Outbox (soma dos atrasos **sem** jitter nas `MaxAttempts - 1` esperas) precisa ser **estritamente maior** que a janela, senão a subida falha.
- O handler só age em `TenantStatus.Pending`. Todo desfecho terminal devolve `Result.Success()`; falha transitória dentro da janela **propaga a exceção original**.
- Filtro da janela: `!cancellationToken.IsCancellationRequested && relogio.UtcNow >= tenant.RegisteredAt + janela` — **nunca** `ex is not OperationCanceledException`.
- `DispatchingOutboxPublisher` abre **um `AsyncServiceScope` novo por mensagem**; nunca recebe `ISender` pelo construtor.
- Evento sem entrada no mapa do publisher **lança**; `Result.IsFailure` vindo de um command **lança**.
- `GET /api/v1/tenants/{tenantId:guid}/provisioning`: policy `PlatformAdmin`; corpo `{ tenantId, status, registeredAt }`; 404 com Problem Details quando não existe. Nunca expõe `ExternalOrganizationId` nem motivo de falha.
- Logs: `LoggerMessage`; EventIds **1100–1199** na Application (provisionamento) e **2100–2199** no publisher. Só `tenantId`, status e tipo de exceção — nunca nome do tenant nem dado pessoal.
- Convenções do repo: membros privados, variáveis e testes em português (`Metodo_Cenario_Resultado` ou `Cenario_Resultado`, como os vizinhos); comentários explicam o **porquê**; `TreatWarningsAsErrors` está ligado.
- Mensagens de commit em Conventional Commits, **sem nenhuma referência a IA, Claude ou Anthropic** (sem `Co-Authored-By`).
- **Docker precisa estar rodando** para as Tasks 2, 5, 6, 7 e 8 (Testcontainers e compose).
- 🧪 = passo "Prova por mutação" obrigatório: quebrar de propósito, ver o teste ficar vermelho, **reverter**, ver verde. Registrar a mutação na mensagem de commit.

## Quatro desvios deliberados da letra da spec

- **`MaxPendingHours` (int) em vez de `MaxPendingDuration` (TimeSpan).** Todas as opções do repositório são inteiros
  com a unidade no nome (`MaxRetryDelaySeconds`, `ProcessedRetentionHours`); um `TimeSpan` em JSON ainda tem a
  armadilha de `"24:00:00"` não ser 24 horas para o `TimeSpan.Parse`. A porta `IProvisioningPolicy` continua
  expondo `TimeSpan`.
- **Testes da Application com NSubstitute**, não fakes à mão: é o padrão de `RegisterTenantHandlerTests`.
- **O teste de isolamento de escopo é o teste "commit perdido depois de criar a Organization"** (Task 6), contra o
  Keycloak real, e não um teste separado com provedor falso: o mesmo cenário prova as duas coisas — com escopo
  compartilhado o tenant sai `Active` do primeiro ciclo, e sem ele a Organization é reencontrada no segundo.
- **Porta de leitura `ITenantQueries` com `TenantProvisioningView`.** A spec pede "leitura sem rastreamento" sem
  nomear onde; repositório devolve agregado (regra `NenhumRepositorioDevolveIQueryable` e o espírito dela), então a
  projeção ganha porta própria.

## Review Focus

1. **`tenant-activated` gerado pelo próprio provisionamento** não pode virar mensagem que falha para sempre no Outbox: ela precisa sair como entregue ("sem consumidor"). Coberto na Task 6.
2. **`appsettings` com os números antigos do Outbox** (5 tentativas, teto 300s — é o que qualquer ambiente que copiou o arquivo antigo terá): a subida precisa falhar com mensagem que nomeia as tentativas e a janela, não subir e deixar tenants presos em `Pending`. Coberto na Task 3.
3. **Keycloak respondendo 403 para sempre** (service account sem `manage-organizations`): é tratado como transitório até a janela e então vira `ProvisioningFailed` — não fica em `Pending` eterno nem vira `Failed` na primeira tentativa. Coberto na Task 4.
4. **Consulta de um tenant em `ProvisioningFailed`**: responde `200` com `"ProvisioningFailed"`, não 500 nem 404. Coberto na Task 7.
5. **Id na rota malformado ou em maiúsculas**: malformado responde 404 sem chegar ao handler; GUID em maiúsculas é o mesmo tenant e responde 200. Coberto na Task 7.

Nota para o revisor: **o `OutboxProcessor` nunca foi exercitado por teste** até esta fatia — nenhum teste o
chama (verificado com `grep -rn ProcessarLoteAsync tests`). A Task 6 é a primeira a rodá-lo de verdade; um defeito
nele aparece lá, e não é regressão desta fatia.

---

## Estrutura de arquivos

**Domain**
- Modify `src/IdentityGateway.Domain/Tenants/Tenant.cs` — `RegisteredAt`; `Register(..., registeredAt)`.
- Modify `src/IdentityGateway.Domain/Tenants/TenantErrors.cs` — `NotFound`.
- Modify `src/IdentityGateway.Domain/Tenants/Events/TenantRegistered.cs`, `TenantActivated.cs` — `OccurredOn` com `init`.

**Application**
- Create `src/IdentityGateway.Application/Common/Abstractions/IProvisioningPolicy.cs` — a janela.
- Create `src/IdentityGateway.Application/Common/Abstractions/ITenantQueries.cs` — porta de leitura + `TenantProvisioningView`.
- Modify `src/IdentityGateway.Application/Common/Abstractions/ITenantRepository.cs` — `GetAsync`.
- Create `src/IdentityGateway.Application/Tenants/ProvisionTenant/ProvisionTenantCommand.cs`, `ProvisionTenantHandler.cs`, `ProvisioningLogs.cs`.
- Create `src/IdentityGateway.Application/Tenants/GetTenantProvisioning/GetTenantProvisioningQuery.cs`, `GetTenantProvisioningHandler.cs`, `TenantProvisioningResponse.cs`.
- Modify `src/IdentityGateway.Application/Tenants/RegisterTenant/RegisterTenantHandler.cs` — relógio.

**Infrastructure**
- Create `src/IdentityGateway.Infrastructure/Configuration/ProvisioningOptions.cs`, `ProvisioningPolicy.cs`, `OutboxCobreAJanelaDeProvisionamento.cs`.
- Create `src/IdentityGateway.Infrastructure/Persistence/Outbox/OutboxBackoff.cs`, `DispatchingOutboxPublisher.cs`.
- Delete `src/IdentityGateway.Infrastructure/Persistence/Outbox/LoggingOutboxPublisher.cs`.
- Create `src/IdentityGateway.Infrastructure/Persistence/Queries/TenantQueries.cs`.
- Modify `Configuration/OutboxOptions.cs`, `Persistence/Outbox/OutboxProcessor.cs`, `Persistence/Configurations/TenantConfiguration.cs`, `Persistence/Repositories/TenantRepository.cs`, `DependencyInjection.cs`.
- Create migration `Persistence/Migrations/*_InstanteDeRegistroDoTenant.cs` (gerada).

**Api**
- Modify `src/IdentityGateway.Api/Modules/TenantsModule.cs`, `appsettings.json`.

**Testes**
- Domain: `Tenants/TenantTests.cs`, `Tenants/SerializacaoDeEventosTests.cs`.
- Application: `Tenants/RegisterTenant/RegisterTenantHandlerTests.cs`; create `Tenants/ProvisionTenant/ProvisionTenantHandlerTests.cs`, `Tenants/GetTenantProvisioning/GetTenantProvisioningHandlerTests.cs`.
- Architecture: `RegrasDoOutboxTests.cs`.
- Integration: `PostgresFixture.cs`, `DependencyInjectionTests.cs`, `Persistence/*` (chamadas a `Register`), create `Persistence/Outbox/OutboxBackoffTests.cs`, `Persistence/Outbox/DispatchingOutboxPublisherTests.cs`, `Provisioning/ComposicaoDoProvisionamento.cs`, `Provisioning/ProvisionamentoContraKeycloakTests.cs`.
- Functional: create `ProvisionamentoDeTenantTests.cs`.

**Documentos**
- Create `docs/especificacao-arquitetural-v2.5.md`; modify `README.md`; create `docs/superpowers/specs/2026-09-2X-consumidor-provisionamento-handoff.md`.

---

### Task 1: `OccurredOn` sobrevive à desserialização

**Files:**
- Modify: `src/IdentityGateway.Domain/Tenants/Events/TenantRegistered.cs`
- Modify: `src/IdentityGateway.Domain/Tenants/Events/TenantActivated.cs`
- Test: `tests/IdentityGateway.Domain.UnitTests/Tenants/SerializacaoDeEventosTests.cs`
- Test: `tests/IdentityGateway.ArchitectureTests/RegrasDoOutboxTests.cs`

**Interfaces:**
- Consumes: nada.
- Produces: `OccurredOn { get; init; }` em todo domain event — nenhuma assinatura muda para quem só lê.

- [ ] **Step 1: Escrever o teste de ida e volta com valor controlado**

Em `SerializacaoDeEventosTests.cs`, acrescentar `using System.Text.Json.Nodes;` e `using IdentityGateway.Domain.Common;`
e, dentro da classe:

```csharp
    // InlineData com o nome do tipo, e não MemberData com a instância: IDomainEvent não é serializável pelo xUnit, e o
    // aviso do analisador sobre isso vira erro com TreatWarningsAsErrors.
    [Theory]
    [InlineData(nameof(TenantRegistered))]
    [InlineData(nameof(TenantActivated))]
    public void OccurredOn_VoltaComOValorGravado(string tipo)
    {
        IDomainEvent evento = tipo == nameof(TenantRegistered)
            ? new TenantRegistered(TenantId.New(), "acme")
            : new TenantActivated(TenantId.New());

        // O valor é trocado no JSON antes de desserializar: comparar com o original deixaria o teste à mercê da
        // resolução do relógio — o instante da desserialização poderia coincidir com o da criação e passar com o
        // defeito presente. Com um valor fixo no passado, só um setter faz o teste passar.
        DateTimeOffset gravado = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        JsonObject json = JsonSerializer.SerializeToNode(evento, evento.GetType(), Opcoes)!.AsObject();
        json["occurredOn"] = gravado.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        object? volta = Desserializar(json.ToJsonString(), evento.GetType());

        volta.Should().BeAssignableTo<IDomainEvent>()
            .Which.OccurredOn.Should().Be(gravado);
    }
```

- [ ] **Step 2: Escrever a regra de arquitetura**

Em `RegrasDoOutboxTests.cs`, depois de `TodoEventoRegistrado_SobreviveAoRoundTripDoOutbox`:

```csharp
    [Fact]
    public void TodoEventoRegistrado_TemOccurredOnRestauravel()
    {
        // O OutboxProcessor relê o evento com JsonSerializer.Deserialize, e o System.Text.Json não atribui
        // propriedade só com get: o OccurredOn voltava com o instante da desserialização, não o da ocorrência. Nada
        // o lia quando o defeito foi achado (fatia B), e por isso mesmo nenhum teste de comportamento o pegaria — o
        // primeiro consumidor a ler receberia um dado falso sem erro nenhum. A regra olha a forma do contrato.
        List<string> semSetter = EventosRegistrados()
            .Where(evento => evento.GetProperty(nameof(IDomainEvent.OccurredOn))?.SetMethod is null)
            .Select(evento => evento.Name)
            .ToList();

        semSetter.Should().BeEmpty(
            "o OccurredOn precisa de setter (init basta) para voltar do JSON com o valor gravado. Sem setter: "
            + string.Join(" | ", semSetter));
    }
```

- [ ] **Step 3: Rodar e ver falhar**

Run: `dotnet test tests/IdentityGateway.Domain.UnitTests --filter OccurredOn_VoltaComOValorGravado`
Expected: FAIL nos 2 casos — `Expected ... OccurredOn to be <2026-01-02 03:04:05 +0:00>, but found <agora>`.

Run: `dotnet test tests/IdentityGateway.ArchitectureTests --filter TodoEventoRegistrado_TemOccurredOnRestauravel`
Expected: FAIL listando `TenantRegistered | TenantActivated`.

- [ ] **Step 4: Corrigir os dois eventos**

Em `TenantRegistered.cs` e `TenantActivated.cs`, trocar a linha da propriedade e acrescentar ao `<remarks>` (ou
criar um no `OccurredOn`) o porquê:

```csharp
    /// <inheritdoc />
    /// <remarks>
    /// <c>init</c> e não só <c>get</c>: o Outbox relê o evento do JSON, e sem setter o System.Text.Json deixava o
    /// inicializador valer — o evento voltava com o instante da desserialização. O inicializador continua dando o
    /// valor na criação.
    /// </remarks>
    public DateTimeOffset OccurredOn { get; init; } = DateTimeOffset.UtcNow;
```

- [ ] **Step 5: Rodar e ver passar**

Run: `dotnet test tests/IdentityGateway.Domain.UnitTests --filter SerializacaoDeEventosTests`
Expected: PASS, 4 testes.

Run: `dotnet test tests/IdentityGateway.ArchitectureTests --filter RegrasDoOutboxTests`
Expected: PASS.

- [ ] **Step 6: 🧪 Prova por mutação**

Voltar `TenantActivated.OccurredOn` para `{ get; }`. Rodar os dois filtros do Step 3: o caso `TenantActivated` da
Theory e a regra de arquitetura ficam vermelhos. Reverter; verde de novo.

- [ ] **Step 7: Commit**

```bash
git add src/IdentityGateway.Domain/Tenants/Events tests/IdentityGateway.Domain.UnitTests/Tenants/SerializacaoDeEventosTests.cs tests/IdentityGateway.ArchitectureTests/RegrasDoOutboxTests.cs
git commit -m "fix: OccurredOn dos eventos sobrevive a desserializacao do Outbox

Com { get; } o System.Text.Json ignorava o valor gravado e o evento
relido carregava o instante da desserializacao.

Mutacao: TenantActivated.OccurredOn de volta a { get; } deixou vermelhos
o caso da Theory e a regra TodoEventoRegistrado_TemOccurredOnRestauravel."
```

---

### Task 2: Instante de registro e leitura do tenant pelo id

**Files:**
- Modify: `src/IdentityGateway.Domain/Tenants/Tenant.cs`
- Modify: `src/IdentityGateway.Application/Tenants/RegisterTenant/RegisterTenantHandler.cs`
- Modify: `src/IdentityGateway.Application/Common/Abstractions/ITenantRepository.cs`
- Modify: `src/IdentityGateway.Infrastructure/Persistence/Repositories/TenantRepository.cs`
- Modify: `src/IdentityGateway.Infrastructure/Persistence/Configurations/TenantConfiguration.cs`
- Create: migration `src/IdentityGateway.Infrastructure/Persistence/Migrations/*_InstanteDeRegistroDoTenant.cs` (gerada)
- Modify (chamadas a `Tenant.Register`): `tests/IdentityGateway.Domain.UnitTests/Tenants/TenantTests.cs`, `tests/IdentityGateway.Infrastructure.IntegrationTests/Persistence/AtomicidadeDoRegistroTests.cs`, `MapeamentoDeTenantTests.cs`, `SchemaDeTenantsTests.cs`, `TenantRepositoryTests.cs`
- Test: `tests/IdentityGateway.Application.UnitTests/Tenants/RegisterTenant/RegisterTenantHandlerTests.cs`

**Interfaces:**
- Consumes: nada.
- Produces:
  - `Tenant.Register(string name, TenantSlug slug, Plan plan, DateTimeOffset registeredAt)`
  - `DateTimeOffset Tenant.RegisteredAt { get; }` — sempre em UTC
  - `Task<Tenant?> ITenantRepository.GetAsync(TenantId tenantId, CancellationToken cancellationToken = default)` — devolve o agregado **rastreado**
  - `RegisterTenantHandler(ITenantRepository repositorio, IPlanCatalog catalogo, IDateTimeProvider relogio)`

- [ ] **Step 1: Escrever os testes de domínio**

Em `TenantTests.cs`, acrescentar o campo e os testes:

```csharp
    private static readonly DateTimeOffset Instante = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Register_GuardaOInstanteDoRegistro()
    {
        var tenant = Tenant.Register("Acme", SlugValido(), PlanoPadrao(), Instante);

        tenant.RegisteredAt.Should().Be(Instante);
    }

    [Fact]
    public void Register_NormalizaOInstanteParaUtc()
    {
        // O Npgsql recusa gravar DateTimeOffset com offset diferente de zero numa coluna timestamptz. Normalizar
        // aqui tira do chamador a obrigação de lembrar disso.
        DateTimeOffset emBrasilia = new(2026, 9, 25, 9, 0, 0, TimeSpan.FromHours(-3));

        var tenant = Tenant.Register("Acme", SlugValido(), PlanoPadrao(), emBrasilia);

        tenant.RegisteredAt.Offset.Should().Be(TimeSpan.Zero);
        tenant.RegisteredAt.Should().Be(emBrasilia);
    }
```

E acrescentar `, Instante` como quarto argumento nas **5** chamadas existentes a `Tenant.Register` do arquivo
(`TenantRegistrado`, `Register_ComNomeVazio_Lanca`, `Register_ComSlugNulo_Lanca`, `Register_ComPlanoNulo_Lanca`
e o teste do nome aparado).

- [ ] **Step 2: Escrever o teste do handler de registro**

Em `RegisterTenantHandlerTests.cs`: campo `private readonly IDateTimeProvider _relogio = Substitute.For<IDateTimeProvider>();`,
no construtor `_relogio.UtcNow.Returns(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero));` antes de criar o
handler, e o handler passa a ser `new RegisterTenantHandler(_repositorio, _catalogo, _relogio)`. Novo teste:

```csharp
    [Fact]
    public async Task ComandoValido_RegistraComOInstanteDoRelogio()
    {
        // É deste instante que o provisionamento conta a janela de retry: vindo de outro relógio, a janela
        // deixaria de ser testável.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant? capturado = null;
        _repositorio.Add(Arg.Do<Tenant>(tenant => capturado = tenant));

        await _handler.Handle(Comando(), ct);

        capturado!.RegisteredAt.Should().Be(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero));
    }
```

- [ ] **Step 3: Rodar e ver falhar**

Run: `dotnet build`
Expected: FAIL de compilação — `No overload for method 'Register' takes 4 arguments` e o construtor do handler com 3
argumentos.

- [ ] **Step 4: Implementar no domínio**

Em `Tenant.cs`:

1. O construtor de domínio ganha o parâmetro e o atribui:

```csharp
    private Tenant(TenantId id, string name, TenantSlug slug, Plan plan, DateTimeOffset registeredAt)
        : base(id)
    {
        Name = name;
        Slug = slug;
        Plan = plan;
        Status = TenantStatus.Pending;
        RegisteredAt = registeredAt;
    }
```

O construtor do EF (`Tenant(TenantId id, string name, TenantSlug slug)`) **não muda**: o EF preenche
`RegisteredAt` pelo setter privado, como já faz com `Status`.

2. Propriedade, depois de `Status`:

```csharp
    /// <summary>Quando o tenant foi registrado, em UTC.</summary>
    /// <remarks>
    /// É daqui que o provisionamento conta a janela de retry: a regra é "pendente há tempo demais", e isso é fato do
    /// tenant, não da mensagem que o transporta.
    /// </remarks>
    public DateTimeOffset RegisteredAt { get; private set; }
```

3. `Register`:

```csharp
    /// <param name="registeredAt">Instante do registro; normalizado para UTC.</param>
    public static Tenant Register(string name, TenantSlug slug, Plan plan, DateTimeOffset registeredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(plan);

        // (manter o comentário existente sobre aparar o nome)
        Tenant tenant = new(TenantId.New(), name.Trim(), slug, plan, registeredAt.ToUniversalTime());

        tenant.RaiseDomainEvent(new TenantRegistered(tenant.Id, slug.Value));

        return tenant;
    }
```

- [ ] **Step 5: Implementar no handler de registro**

Em `RegisterTenantHandler.cs`: acrescentar `IDateTimeProvider relogio` ao construtor primário e trocar a criação:

```csharp
        var tenant = Tenant.Register(command.Name, slug.Value, plano, relogio.UtcNow);
```

- [ ] **Step 6: Atualizar as chamadas dos testes de integração**

Acrescentar `, PostgresFixture.Agora` como quarto argumento em cada `Tenant.Register(...)` de:
`AtomicidadeDoRegistroTests.cs` (3), `MapeamentoDeTenantTests.cs` (2), `SchemaDeTenantsTests.cs` (2) e
`TenantRepositoryTests.cs` (2). Conferir que não sobrou nenhuma:

Run: `grep -rn "Tenant.Register(" tests src --include=*.cs | grep -v "/obj/" | grep -v "Instante\|Agora\|registeredAt\|relogio"`
Expected: nenhuma linha (as chamadas com lambda de `Should().Throw` também carregam `Instante`).

- [ ] **Step 7: Rodar os testes sem container**

Run: `dotnet test tests/IdentityGateway.Domain.UnitTests tests/IdentityGateway.Application.UnitTests`
Expected: PASS.

- [ ] **Step 8: Escrever os testes de persistência**

Em `MapeamentoDeTenantTests.Tenant_SobreviveAoRoundTrip`, depois de `lido.OverSubscribed...`:

```csharp
        lido.RegisteredAt.Should().Be(PostgresFixture.Agora);
```

Em `SchemaDeTenantsTests.cs`:

```csharp
    [Fact]
    public async Task RegisteredAt_EObrigatorioESemDefault()
    {
        // Sem default de propósito: o domínio sempre informa o instante, e um default no banco esconderia um
        // caminho de criação que esquecesse de informar.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using AppDbContext contexto = postgres.CriarContexto();

        List<string> coluna = await contexto.Database
            .SqlQuery<string>($"""
                SELECT is_nullable || '|' || coalesce(column_default, '') AS "Value"
                  FROM information_schema.columns
                 WHERE table_name = 'tenants' AND column_name = 'registered_at'
                """)
            .ToListAsync(ct);

        coluna.Should().ContainSingle().Which.Should().Be("NO|");
    }
```

Em `TenantRepositoryTests.cs`:

```csharp
    [Fact]
    public async Task GetAsync_DevolveOTenantRastreado()
    {
        // Rastreado é contrato: o ProvisionTenantHandler muda o estado e quem grava é o TransactionBehavior, sem
        // chamar Update. Um GetAsync com AsNoTracking faria o provisionamento "funcionar" sem nunca persistir.
        CancellationToken ct = TestContext.Current.CancellationToken;
        TenantSlug slug = TenantSlug.Create($"get-{Guid.NewGuid():N}"[..18]).Value;
        var tenant = Tenant.Register("Get", slug, new Plan(PlanTier.Free, 5, 1), PostgresFixture.Agora);
        await using (AppDbContext escrita = postgres.CriarContexto())
        {
            escrita.Tenants.Add(tenant);
            await escrita.SaveChangesAsync(ct);
        }

        await using (AppDbContext contexto = postgres.CriarContexto())
        {
            ITenantRepository repositorio = new TenantRepository(contexto);
            Tenant? lido = await repositorio.GetAsync(tenant.Id, ct);
            lido.Should().NotBeNull();
            lido!.MarkProvisioned("org-get");
            await contexto.SaveChangesAsync(ct);
        }

        await using AppDbContext conferencia = postgres.CriarContexto();
        Tenant gravado = await conferencia.Tenants.SingleAsync(item => item.Id == tenant.Id, ct);
        gravado.Status.Should().Be(TenantStatus.Active);
    }

    [Fact]
    public async Task GetAsync_NuloQuandoNaoExiste()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using AppDbContext contexto = postgres.CriarContexto();
        ITenantRepository repositorio = new TenantRepository(contexto);

        Tenant? lido = await repositorio.GetAsync(TenantId.New(), ct);

        lido.Should().BeNull();
    }
```

(`TenantRepositoryTests.cs` precisa de `using Microsoft.EntityFrameworkCore;` para o `SingleAsync`.)

- [ ] **Step 9: Implementar `GetAsync`**

Em `ITenantRepository.cs`:

```csharp
    /// <summary>Carrega o tenant para alteração, ou nulo se não existir.</summary>
    /// <remarks>
    /// Devolve o agregado <b>rastreado</b>: quem altera o estado não chama <c>Update</c> — o commit do
    /// <c>TransactionBehavior</c> grava o que mudou.
    /// </remarks>
    Task<Tenant?> GetAsync(TenantId tenantId, CancellationToken cancellationToken = default);
```

Em `TenantRepository.cs` (acrescentar o `using Microsoft.EntityFrameworkCore;` se faltar — já existe):

```csharp
    /// <inheritdoc />
    public Task<Tenant?> GetAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        context.Tenants.SingleOrDefaultAsync(tenant => tenant.Id == tenantId, cancellationToken);
```

- [ ] **Step 10: Mapear a coluna**

Em `TenantConfiguration.cs`, depois do mapeamento de `Status`:

```csharp
        // Sem default no banco: o domínio sempre informa o instante (Tenant.Register exige), e um default esconderia
        // um caminho de criação que esquecesse. timestamptz, porque é um instante, não uma hora de parede.
        builder.Property(tenant => tenant.RegisteredAt)
            .HasColumnName("registered_at")
            .IsRequired();
```

- [ ] **Step 11: Gerar a migration**

Run:
```bash
dotnet ef migrations add InstanteDeRegistroDoTenant \
  --project src/IdentityGateway.Infrastructure \
  --startup-project src/IdentityGateway.Api \
  --output-dir Persistence/Migrations
```
Expected: cria `*_InstanteDeRegistroDoTenant.cs` com **um** `AddColumn<DateTimeOffset>("registered_at", "tenants", ...)`
com `defaultValue: new DateTimeOffset(...)` (o mínimo do tipo). Se aparecer qualquer outra operação — em especial
algo sobre `xmin` — pare: o snapshot divergiu do modelo, e isso é problema a entender antes de seguir.

- [ ] **Step 12: Editar a migration à mão**

Trocar o `Up` gerado por:

```csharp
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // As linhas existentes (só há dados de desenvolvimento) recebem o instante da migration; o default sai logo
            // em seguida, porque dali em diante o domínio sempre informa o valor. Deixar o mínimo de DateTimeOffset que
            // o EF gera faria todo tenant antigo parecer registrado no ano 1 — e o provisionamento o daria como
            // expirado na primeira tentativa.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "registered_at",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.Sql("ALTER TABLE tenants ALTER COLUMN registered_at DROP DEFAULT;");
        }
```

O `Down` gerado (`DropColumn`) fica como está.

- [ ] **Step 13: Conferir que o modelo e o snapshot batem**

Run:
```bash
dotnet ef migrations add Conferencia --project src/IdentityGateway.Infrastructure --startup-project src/IdentityGateway.Api --output-dir Persistence/Migrations
```
Expected: `Up` e `Down` **vazios**. Então remover:

Run: `dotnet ef migrations remove --project src/IdentityGateway.Infrastructure --startup-project src/IdentityGateway.Api`

- [ ] **Step 14: Rodar os testes de persistência**

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter "FullyQualifiedName~Persistence"`
Expected: PASS (inclui os 3 testes novos).

- [ ] **Step 15: Commit**

```bash
git add src tests
git commit -m "feat: instante de registro do tenant e leitura pelo id

RegisteredAt e de onde o provisionamento conta a janela de retry.
Normalizado para UTC no dominio, porque o Npgsql recusa offset diferente
de zero em timestamptz. A migration preenche as linhas existentes com
now() e remove o default: o dominio sempre informa o valor.

GetAsync devolve o agregado rastreado; o teste prova que a alteracao
persiste sem Update."
```

---

### Task 3: Janela de provisionamento e a rede do Outbox

**Files:**
- Create: `src/IdentityGateway.Application/Common/Abstractions/IProvisioningPolicy.cs`
- Create: `src/IdentityGateway.Infrastructure/Configuration/ProvisioningOptions.cs`
- Create: `src/IdentityGateway.Infrastructure/Configuration/ProvisioningPolicy.cs`
- Create: `src/IdentityGateway.Infrastructure/Configuration/OutboxCobreAJanelaDeProvisionamento.cs`
- Create: `src/IdentityGateway.Infrastructure/Persistence/Outbox/OutboxBackoff.cs`
- Modify: `src/IdentityGateway.Infrastructure/Configuration/OutboxOptions.cs`
- Modify: `src/IdentityGateway.Infrastructure/Persistence/Outbox/OutboxProcessor.cs`
- Modify: `src/IdentityGateway.Infrastructure/DependencyInjection.cs`
- Modify: `src/IdentityGateway.Api/appsettings.json`
- Test: `tests/IdentityGateway.Infrastructure.IntegrationTests/Persistence/Outbox/OutboxBackoffTests.cs`
- Test: `tests/IdentityGateway.Infrastructure.IntegrationTests/DependencyInjectionTests.cs`

**Interfaces:**
- Consumes: nada.
- Produces:
  - `public interface IProvisioningPolicy { TimeSpan MaxPendingDuration { get; } }` (Application)
  - `ProvisioningOptions.MaxPendingHours` (int, padrão 24, seção `Provisioning`)
  - `internal static TimeSpan OutboxBackoff.AtrasoSemVariacao(int tentativa, OutboxOptions opcoes)`
  - `internal static TimeSpan OutboxBackoff.CoberturaMinima(OutboxOptions opcoes)`

- [ ] **Step 1: Escrever os testes do backoff**

```csharp
using IdentityGateway.Infrastructure.Configuration;
using IdentityGateway.Infrastructure.Persistence.Outbox;

namespace IdentityGateway.Infrastructure.IntegrationTests.Persistence.Outbox;

/// <summary>
/// A fórmula do atraso entre tentativas, compartilhada pelo processador e pela validação da subida.
/// </summary>
public sealed class OutboxBackoffTests
{
    private static readonly OutboxOptions Padroes = new();

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(4, 60)]
    [InlineData(1500, 60)]
    public void AtrasoSemVariacao_DobraAteOTeto(int tentativa, int segundos)
    {
        // A tentativa 1500 é o caso que importa: 2^1499 estoura o double para infinito, e o teto precisa continuar
        // valendo em vez de o atraso virar infinito ou NaN.
        OutboxBackoff.AtrasoSemVariacao(tentativa, Padroes).Should().Be(TimeSpan.FromSeconds(segundos));
    }

    [Fact]
    public void CoberturaMinima_ComOsPadroes_CobreAJanelaDe24hComFolga()
    {
        TimeSpan cobertura = OutboxBackoff.CoberturaMinima(Padroes);

        cobertura.Should().BeGreaterThan(TimeSpan.FromHours(24));
        cobertura.Should().BeLessThan(TimeSpan.FromHours(26));
    }

    [Fact]
    public void CoberturaMinima_ContaAsEsperasEntreTentativas()
    {
        // Cinco tentativas têm quatro esperas: 10 + 20 + 40 + 80. A quinta tentativa é a última — não há espera
        // depois dela.
        OutboxOptions antigos = new() { MaxAttempts = 5, MaxRetryDelaySeconds = 300 };

        OutboxBackoff.CoberturaMinima(antigos).Should().Be(TimeSpan.FromSeconds(150));
    }
}
```

- [ ] **Step 2: Escrever os testes de DI e validação**

Em `DependencyInjectionTests.cs`:

1. Acrescentar `[InlineData(typeof(IProvisioningPolicy))]` à Theory `TodasAsAbstracoesDaApplication_SaoResolviveis`.
2. Em `OutboxSemConfiguracao_UsaOsPadroes`, trocar `outbox.MaxAttempts.Should().Be(5);` por:

```csharp
        outbox.MaxAttempts.Should().Be(1500);
        outbox.MaxRetryDelaySeconds.Should().Be(60);
```

3. Helper e testes novos:

```csharp
    private static IConfiguration ConfiguracaoValidaCom(params (string Chave, string? Valor)[] extras)
    {
        Dictionary<string, string?> valores = ConfiguracaoValida()
            .AsEnumerable()
            .Where(par => par.Value is not null)
            .ToDictionary(par => par.Key, par => par.Value);

        foreach ((string chave, string? valor) in extras)
        {
            valores[chave] = valor;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
    }

    [Fact]
    public void ProvisioningSemConfiguracao_UsaAJanelaDe24Horas()
    {
        using ServiceProvider provider = Construir(ConfiguracaoValida());

        provider.GetRequiredService<IProvisioningPolicy>().MaxPendingDuration.Should().Be(TimeSpan.FromHours(24));
    }

    [Fact]
    public void JanelaForaDaFaixa_FalhaAoValidar()
    {
        using ServiceProvider provider = Construir(ConfiguracaoValidaCom(("Provisioning:MaxPendingHours", "0")));

        Action validar = () => _ = provider.GetRequiredService<IOptions<ProvisioningOptions>>().Value;

        validar.Should().Throw<OptionsValidationException>().WithMessage("*janela*");
    }

    [Fact]
    public void OutboxComOsNumerosAntigos_FalhaAoValidarNomeandoTentativasEJanela()
    {
        // É a configuração que qualquer ambiente com o appsettings anterior à fatia B tem: cinco tentativas somam
        // 150s de retry, e o tenant ficaria em Pending para sempre depois que o Outbox desistisse — sem ninguém para
        // marcá-lo ProvisioningFailed. Falhar na subida, dizendo por quê, é o que torna isso visível.
        using ServiceProvider provider = Construir(ConfiguracaoValidaCom(
            ("Outbox:MaxAttempts", "5"),
            ("Outbox:MaxRetryDelaySeconds", "300")));

        Action validar = () => _ = provider.GetRequiredService<IOptions<OutboxOptions>>().Value;

        validar.Should().Throw<OptionsValidationException>().WithMessage("*5 tentativas*24h*");
    }
```

(Acrescentar `using IdentityGateway.Infrastructure.Configuration;` se ainda não houver — já há.)

- [ ] **Step 3: Rodar e ver falhar**

Run: `dotnet build`
Expected: FAIL de compilação — `OutboxBackoff`, `IProvisioningPolicy` e `ProvisioningOptions` não existem.

- [ ] **Step 4: Criar a porta**

`src/IdentityGateway.Application/Common/Abstractions/IProvisioningPolicy.cs`:

```csharp
namespace IdentityGateway.Application.Common.Abstractions;

/// <summary>
/// Por quanto tempo o provisionamento de um tenant insiste diante de falha transitória.
/// </summary>
/// <remarks>
/// <para>
/// Porta, e não <c>IOptions</c> na Application: a camada não referencia <c>Microsoft.Extensions.Options</c>, e o
/// precedente é o <see cref="IPlanCatalog"/> — contrato aqui, implementação na Infrastructure a partir de options
/// validadas na subida.
/// </para>
/// <para>
/// A janela é contada desde <c>Tenant.RegisteredAt</c>. Esgotada, o tenant vai a <c>ProvisioningFailed</c>. O
/// Outbox precisa continuar trazendo a mensagem de volta por mais tempo que isto — a Infrastructure valida.
/// </para>
/// </remarks>
public interface IProvisioningPolicy
{
    /// <summary>Tempo máximo em <c>Pending</c> antes de desistir de falhas transitórias.</summary>
    TimeSpan MaxPendingDuration { get; }
}
```

- [ ] **Step 5: Criar as options e a implementação**

`src/IdentityGateway.Infrastructure/Configuration/ProvisioningOptions.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace IdentityGateway.Infrastructure.Configuration;

/// <summary>
/// Política do provisionamento de tenant.
/// </summary>
/// <remarks>
/// Horas em inteiro, como as demais opções do repositório têm a unidade no nome. Um <c>TimeSpan</c> em JSON teria a
/// armadilha de <c>"24:00:00"</c> não ser 24 horas para o <c>TimeSpan.Parse</c>.
/// </remarks>
public sealed class ProvisioningOptions
{
    /// <summary>Seção correspondente no arquivo de configuração.</summary>
    public const string SectionName = "Provisioning";

    /// <summary>Por quantas horas o provisionamento insiste diante de falha transitória.</summary>
    /// <remarks>
    /// Longa de propósito: cobre queda real do Keycloak, deploy dele e a demonstração do M1 sem que o tenant caia em
    /// <c>ProvisioningFailed</c> — de onde, nesta versão, só sai por intervenção manual. Um Keycloak mal configurado
    /// leva a janela inteira para virar falha; até lá aparece como erro repetido no log e no <c>/health/ready</c>.
    /// </remarks>
    [Range(1, 720, ErrorMessage = "A janela de provisionamento deve estar entre 1 e 720 horas.")]
    public int MaxPendingHours { get; init; } = 24;
}
```

`src/IdentityGateway.Infrastructure/Configuration/ProvisioningPolicy.cs`:

```csharp
using IdentityGateway.Application.Common.Abstractions;
using Microsoft.Extensions.Options;

namespace IdentityGateway.Infrastructure.Configuration;

/// <summary>
/// Implementa <see cref="IProvisioningPolicy"/> a partir de <see cref="ProvisioningOptions"/>.
/// </summary>
internal sealed class ProvisioningPolicy(IOptions<ProvisioningOptions> opcoes) : IProvisioningPolicy
{
    /// <inheritdoc />
    public TimeSpan MaxPendingDuration { get; } = TimeSpan.FromHours(opcoes.Value.MaxPendingHours);
}
```

- [ ] **Step 6: Extrair a fórmula do backoff**

`src/IdentityGateway.Infrastructure/Persistence/Outbox/OutboxBackoff.cs`:

```csharp
using IdentityGateway.Infrastructure.Configuration;

namespace IdentityGateway.Infrastructure.Persistence.Outbox;

/// <summary>
/// A fórmula do atraso entre tentativas do Outbox.
/// </summary>
/// <remarks>
/// Uma cópia só: o <see cref="OutboxProcessor"/> a usa para agendar a próxima tentativa, e a validação da subida a
/// usa para saber quanto tempo o Outbox insiste. Duas cópias da fórmula derivariam, e a validação passaria a
/// garantir uma cobertura que o processador não entrega.
/// </remarks>
internal static class OutboxBackoff
{
    /// <summary>
    /// Atraso depois da tentativa informada, sem a variação aleatória: <c>base * 2^(tentativa-1)</c>, limitado ao teto.
    /// </summary>
    /// <remarks>
    /// Com muitas tentativas a potência estoura o <c>double</c> para infinito; o <c>Math.Min</c> com o teto continua
    /// correto nesse caso, e o teste cobre a tentativa 1500.
    /// </remarks>
    internal static TimeSpan AtrasoSemVariacao(int tentativa, OutboxOptions opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);

        double dobrado = opcoes.BaseRetryDelaySeconds * Math.Pow(2, Math.Max(0, tentativa - 1));

        return TimeSpan.FromSeconds(Math.Min(dobrado, opcoes.MaxRetryDelaySeconds));
    }

    /// <summary>
    /// Tempo mínimo entre a primeira e a última tentativa de uma mensagem.
    /// </summary>
    /// <remarks>
    /// Soma as <c>MaxAttempts - 1</c> esperas sem a variação — que só alonga —, então é um piso: na prática o Outbox
    /// insiste um pouco mais que isto, nunca menos.
    /// </remarks>
    internal static TimeSpan CoberturaMinima(OutboxOptions opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);

        TimeSpan total = TimeSpan.Zero;

        for (int tentativa = 1; tentativa < opcoes.MaxAttempts; tentativa++)
        {
            total += AtrasoSemVariacao(tentativa, opcoes);
        }

        return total;
    }
}
```

Em `OutboxProcessor.cs`, trocar o corpo de `AtrasoDe` para usar a fórmula compartilhada:

```csharp
    private TimeSpan AtrasoDe(int tentativa)
    {
        double limitado = OutboxBackoff.AtrasoSemVariacao(tentativa, _options).TotalSeconds;
        double variacao = Random.Shared.NextDouble() * limitado * 0.2;

        return TimeSpan.FromSeconds(limitado + variacao);
    }
```

(Manter o XML doc que já existe acima de `AtrasoDe`, se houver, ajustando-o para dizer que a base vem de
`OutboxBackoff`.)

- [ ] **Step 7: Criar a validação cruzada**

`src/IdentityGateway.Infrastructure/Configuration/OutboxCobreAJanelaDeProvisionamento.cs`:

```csharp
using System.Globalization;
using IdentityGateway.Infrastructure.Persistence.Outbox;
using Microsoft.Extensions.Options;

namespace IdentityGateway.Infrastructure.Configuration;

/// <summary>
/// Recusa subir com um Outbox que desiste antes da janela de provisionamento.
/// </summary>
/// <remarks>
/// A janela é do handler, mas quem traz a mensagem de volta é o Outbox. Se ele esgotasse as tentativas antes, a
/// mensagem pararia e o tenant ficaria em <c>Pending</c> para sempre — ninguém o marcaria <c>ProvisioningFailed</c>,
/// porque quem marca é o handler, e o handler só roda quando a mensagem volta.
/// </remarks>
internal sealed class OutboxCobreAJanelaDeProvisionamento(IOptions<ProvisioningOptions> provisionamento)
    : IValidateOptions<OutboxOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, OutboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        TimeSpan janela = TimeSpan.FromHours(provisionamento.Value.MaxPendingHours);
        TimeSpan cobertura = OutboxBackoff.CoberturaMinima(options);

        if (cobertura > janela)
        {
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail(string.Create(
            CultureInfo.InvariantCulture,
            $"Outbox: {options.MaxAttempts} tentativas cobrem no mínimo {cobertura.TotalHours:F1}h de retry, "
            + $"menos que a janela de provisionamento de {janela.TotalHours:F0}h (Provisioning:MaxPendingHours). "
            + "Aumente Outbox:MaxAttempts ou Outbox:MaxRetryDelaySeconds, ou reduza a janela."));
    }
}
```

- [ ] **Step 8: Novos padrões do Outbox**

Em `OutboxOptions.cs`:

```csharp
    /// <summary>Quantas tentativas uma mensagem recebe antes de parar de ser lida.</summary>
    /// <remarks>
    /// <para>
    /// Alcançado o limite, a mensagem deixa de satisfazer o filtro da consulta e permanece na tabela com o erro
    /// da última tentativa — é o dead-letter deste projeto, e ele é a <i>ausência</i> de uma condição, não uma
    /// estrutura nova.
    /// </para>
    /// <para>
    /// <b>1500 porque o provisionamento de tenant roda dentro do despacho</b> (fatia B, sem broker): com teto de 60s,
    /// são ~25h de insistência, acima da janela de 24h do provisionamento — e a subida recusa qualquer combinação que
    /// fique abaixo dela (<see cref="OutboxCobreAJanelaDeProvisionamento"/>). Quando o broker chegar, o Outbox volta a
    /// só entregar a ele, e este número deve ser revisto.
    /// </para>
    /// </remarks>
    [Range(1, 10_000, ErrorMessage = "O máximo de tentativas deve estar entre 1 e 10000.")]
    public int MaxAttempts { get; init; } = 1500;
```

e

```csharp
    /// <summary>Teto do atraso entre tentativas, em segundos.</summary>
    /// <remarks>
    /// <para>
    /// É o que impede o dobro sucessivo de virar dias com muitas tentativas.
    /// </para>
    /// <para>
    /// <b>É também a latência de recuperação:</b> quando o destino volta, a mensagem espera no máximo isto para a
    /// próxima tentativa. Com 60s, o tenant criado com o Keycloak fora vira <c>Active</c> cerca de um minuto depois de
    /// o Keycloak voltar — com 300s, até cinco.
    /// </para>
    /// </remarks>
    [Range(1, 86_400, ErrorMessage = "O teto do atraso deve estar entre 1 segundo e 24 horas.")]
    public int MaxRetryDelaySeconds { get; init; } = 60;
```

No `<remarks>` da classe, o exemplo "mudar de cinco para três tentativas" continua válido como ilustração — não
precisa mudar.

Em `src/IdentityGateway.Api/appsettings.json`, na seção `Outbox`: `"MaxAttempts": 1500` e
`"MaxRetryDelaySeconds": 60`; e acrescentar, depois da seção `Outbox`:

```json
  "Provisioning": {
    "MaxPendingHours": 24
  },
```

Conferir que nenhum outro arquivo de configuração sobrescreve esses valores:

Run: `grep -rn "MaxAttempts\|MaxRetryDelaySeconds\|Outbox__" src docker-compose.yml .github`
Expected: só `appsettings.json` e `OutboxOptions.cs`.

- [ ] **Step 9: Registrar**

Em `DependencyInjection.cs`, `AddOptionsValidadas`, depois do bloco de `OutboxOptions`:

```csharp
        services.AddOptions<ProvisioningOptions>()
            .Bind(configuration.GetSection(ProvisioningOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // A rede do Outbox precisa ser maior que a janela do provisionamento. É um IValidateOptions, e não um
        // Validate(...) em linha, para a mensagem poder nomear os dois valores — o que torna o erro de subida acionável.
        services.AddSingleton<IValidateOptions<OutboxOptions>, OutboxCobreAJanelaDeProvisionamento>();
```

Em `AddServicos`, depois do `IPlanCatalog`:

```csharp
        services.AddSingleton<IProvisioningPolicy, ProvisioningPolicy>();
```

- [ ] **Step 10: Rodar e ver passar**

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter "FullyQualifiedName~OutboxBackoffTests|FullyQualifiedName~DependencyInjectionTests"`
Expected: PASS.

- [ ] **Step 11: 🧪 Prova por mutação**

Comentar a linha `services.AddSingleton<IValidateOptions<OutboxOptions>, OutboxCobreAJanelaDeProvisionamento>();`.
Rodar o filtro do Step 10: `OutboxComOsNumerosAntigos_FalhaAoValidarNomeandoTentativasEJanela` fica vermelho
(`Expected ... OptionsValidationException ... but no exception was thrown`). Reverter; verde.

- [ ] **Step 12: Commit**

```bash
git add src tests
git commit -m "feat: janela de provisionamento e rede do Outbox validada contra ela

A janela (Provisioning:MaxPendingHours, 24h) chega a Application pela
porta IProvisioningPolicy. O Outbox passa a teto de 60s e 1500
tentativas (~25h), e a subida recusa combinacao que desista antes da
janela: sem isso o tenant ficaria em Pending para sempre. A formula do
backoff foi extraida para OutboxBackoff, usada pelo processador e pela
validacao.

Mutacao: sem o registro da validacao cruzada, o teste com os numeros
antigos (5 tentativas, 300s) ficou vermelho."
```

---

### Task 4: `ProvisionTenantHandler`

**Files:**
- Create: `src/IdentityGateway.Application/Tenants/ProvisionTenant/ProvisionTenantCommand.cs`
- Create: `src/IdentityGateway.Application/Tenants/ProvisionTenant/ProvisionTenantHandler.cs`
- Create: `src/IdentityGateway.Application/Tenants/ProvisionTenant/ProvisioningLogs.cs`
- Test: `tests/IdentityGateway.Application.UnitTests/Tenants/ProvisionTenant/ProvisionTenantHandlerTests.cs`

**Interfaces:**
- Consumes: `ITenantRepository.GetAsync`, `Tenant.RegisteredAt` (Task 2); `IProvisioningPolicy` (Task 3); `IIdentityProvider.EnsureOrganizationAsync`, `IdentityProviderInconsistencyException` (fatia A); `IDateTimeProvider`.
- Produces:
  - `public sealed record ProvisionTenantCommand(TenantId TenantId) : ICommand;`
  - `public sealed class ProvisionTenantHandler : ICommandHandler<ProvisionTenantCommand>` — resolvível pelo `ISender`.

- [ ] **Step 1: Escrever os testes**

```csharp
using System.Net;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Tenants.ProvisionTenant;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace IdentityGateway.Application.UnitTests.Tenants.ProvisionTenant;

public sealed class ProvisionTenantHandlerTests
{
    private static readonly DateTimeOffset Registro = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Janela = TimeSpan.FromHours(24);

    private readonly ITenantRepository _tenants = Substitute.For<ITenantRepository>();
    private readonly IIdentityProvider _identidade = Substitute.For<IIdentityProvider>();
    private readonly IProvisioningPolicy _politica = Substitute.For<IProvisioningPolicy>();
    private readonly IDateTimeProvider _relogio = Substitute.For<IDateTimeProvider>();
    private readonly ProvisionTenantHandler _handler;

    public ProvisionTenantHandlerTests()
    {
        _politica.MaxPendingDuration.Returns(Janela);
        _relogio.UtcNow.Returns(Registro.AddMinutes(5));
        _handler = new ProvisionTenantHandler(
            _tenants, _identidade, _politica, _relogio, NullLogger<ProvisionTenantHandler>.Instance);
    }

    private Tenant TenantPendente()
    {
        var tenant = Tenant.Register("Acme", TenantSlug.Create("acme").Value, new Plan(PlanTier.Free, 5, 1), Registro);
        _tenants.GetAsync(tenant.Id, Arg.Any<CancellationToken>()).Returns(tenant);
        return tenant;
    }

    private void ProvedorDevolve(string organizacao) =>
        _identidade
            .EnsureOrganizationAsync(Arg.Any<TenantId>(), Arg.Any<TenantSlug>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(organizacao);

    private void ProvedorLanca(Exception excecao) =>
        _identidade
            .EnsureOrganizationAsync(Arg.Any<TenantId>(), Arg.Any<TenantSlug>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(excecao);

    private void RelogioEm(DateTimeOffset instante) => _relogio.UtcNow.Returns(instante);

    [Fact]
    public async Task TenantInexistente_SucessoSemChamarOProvedor()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(TenantId.New()), ct);

        resultado.IsSuccess.Should().BeTrue();
        await _identidade.DidNotReceiveWithAnyArgs().EnsureOrganizationAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task TenantJaAtivo_SucessoSemChamarOProvedor()
    {
        // Mensagem repetida: o Outbox entrega at-least-once, e a segunda entrega precisa ser inofensiva.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        tenant.MarkProvisioned("org-1");

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        await _identidade.DidNotReceiveWithAnyArgs().EnsureOrganizationAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task TenantEmProvisioningFailed_NaoEReprovisionado()
    {
        // Failed é decisão registrada; uma mensagem repetida não pode desfazê-la em silêncio. A saída de Failed é o
        // retry manual, que devolve o tenant a Pending antes de reenfileirar.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        tenant.MarkProvisioningFailed();
        ProvedorDevolve("org-1");

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
        await _identidade.DidNotReceiveWithAnyArgs().EnsureOrganizationAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task TenantPendente_GaranteAOrganizacaoEAtiva()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        ProvedorDevolve("org-42");

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.Active);
        tenant.ExternalOrganizationId.Should().Be("org-42");
        await _identidade.Received(1).EnsureOrganizationAsync(tenant.Id, tenant.Slug, tenant.Name, ct);
    }

    [Fact]
    public async Task Inconsistencia_MarcaFailedSemEsperarAJanela()
    {
        // Erro permanente: repetir só adiaria o Failed por configuração, não por decisão.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        ProvedorLanca(new IdentityProviderInconsistencyException("slug em uso por outra Organization"));

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
    }

    [Fact]
    public async Task FalhaTransitoriaDentroDaJanela_PropagaEMantemPending()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        HttpRequestException falha = new("Keycloak fora do ar");
        ProvedorLanca(falha);

        Func<Task> provisionar = async () => await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        (await provisionar.Should().ThrowAsync<HttpRequestException>()).Which.Should().BeSameAs(falha);
        tenant.Status.Should().Be(TenantStatus.Pending);
    }

    [Fact]
    public async Task FalhaTransitoriaDepoisDaJanela_MarcaFailed()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        RelogioEm(Registro + Janela + TimeSpan.FromMinutes(1));
        ProvedorLanca(new HttpRequestException("Keycloak fora do ar"));

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
    }

    [Fact]
    public async Task FalhaNaFronteiraExataDaJanela_MarcaFailed()
    {
        // A janela é fechada no fim: RegisteredAt + janela já é "tempo demais".
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        RelogioEm(Registro + Janela);
        ProvedorLanca(new HttpRequestException("Keycloak fora do ar"));

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
    }

    [Fact]
    public async Task TimeoutDaResilienciaDepoisDaJanela_MarcaFailed()
    {
        // O timeout da resiliência chega como TaskCanceledException, que É um OperationCanceledException. Um filtro
        // por tipo tiraria da janela justamente o sintoma mais comum de Keycloak lento.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        RelogioEm(Registro + Janela + TimeSpan.FromMinutes(1));
        ProvedorLanca(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
    }

    [Fact]
    public async Task Keycloak403DentroEForaDaJanela_SoViraFailedDepoisDela()
    {
        // Service account sem manage-organizations: 403 para sempre. Não é inconsistência (repetir pode resolver
        // depois que alguém corrigir o realm), então segue a janela como qualquer falha transitória.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        ProvedorLanca(new HttpRequestException("Forbidden", inner: null, HttpStatusCode.Forbidden));

        Func<Task> dentro = async () => await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);
        await dentro.Should().ThrowAsync<HttpRequestException>();
        tenant.Status.Should().Be(TenantStatus.Pending);

        RelogioEm(Registro + Janela + TimeSpan.FromSeconds(1));
        Result depois = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        depois.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
    }

    [Fact]
    public async Task DesligamentoDoHostDepoisDaJanela_PropagaSemMarcarFailed()
    {
        // O host desligando não é falha do Keycloak: a mensagem volta no próximo ciclo, de outra instância ou desta.
        Tenant tenant = TenantPendente();
        RelogioEm(Registro + Janela + TimeSpan.FromMinutes(1));
        using CancellationTokenSource desligando = new();
        await desligando.CancelAsync();
        ProvedorLanca(new OperationCanceledException(desligando.Token));

        Func<Task> provisionar = async () =>
            await _handler.Handle(new ProvisionTenantCommand(tenant.Id), desligando.Token);

        await provisionar.Should().ThrowAsync<OperationCanceledException>();
        tenant.Status.Should().Be(TenantStatus.Pending);
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet build tests/IdentityGateway.Application.UnitTests`
Expected: FAIL de compilação — `ProvisionTenantCommand` e `ProvisionTenantHandler` não existem.

- [ ] **Step 3: Criar o command**

`ProvisionTenantCommand.cs`:

```csharp
using IdentityGateway.Application.Common.Messaging;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Application.Tenants.ProvisionTenant;

/// <summary>
/// Garante que o tenant registrado existe no provedor de identidade e o coloca em operação.
/// </summary>
/// <remarks>
/// Command interno: nenhum endpoint o expõe. Quem o envia é o despacho do Outbox, ao ler <c>tenant-registered</c> —
/// e, quando o broker chegar, o consumidor dele. Carrega só o id: o estado vem do banco, que é a fonte da verdade,
/// e não de um payload que pode estar velho.
/// </remarks>
/// <param name="TenantId">Tenant a provisionar.</param>
public sealed record ProvisionTenantCommand(TenantId TenantId) : ICommand;
```

- [ ] **Step 4: Criar os logs**

`ProvisioningLogs.cs`:

```csharp
using IdentityGateway.Domain.Tenants;
using Microsoft.Extensions.Logging;

namespace IdentityGateway.Application.Tenants.ProvisionTenant;

/// <summary>
/// Mensagens de log do provisionamento. EventIds 1100–1199.
/// </summary>
/// <remarks>
/// Só o id do tenant, o status e a exceção — nunca o nome do tenant: log é indexado e lido por muita gente (§14).
/// Os dois <c>Error</c> são o único registro do motivo de um <c>ProvisioningFailed</c>; o banco guarda só o estado.
/// </remarks>
internal static partial class ProvisioningLogs
{
    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Warning,
        Message = "Provisionamento: tenant {TenantId} não existe; mensagem descartada")]
    public static partial void TenantInexistente(ILogger logger, Guid tenantId);

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Information,
        Message = "Provisionamento: tenant {TenantId} está em {Status}, não em Pending; nada a fazer")]
    public static partial void ForaDePending(ILogger logger, Guid tenantId, TenantStatus status);

    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Information,
        Message = "Provisionamento: tenant {TenantId} ativo")]
    public static partial void Provisionado(ILogger logger, Guid tenantId);

    [LoggerMessage(
        EventId = 1103,
        Level = LogLevel.Error,
        Message = "Provisionamento: tenant {TenantId} em ProvisioningFailed por inconsistência no provedor de identidade")]
    public static partial void FalhaPermanente(ILogger logger, Guid tenantId, Exception excecao);

    [LoggerMessage(
        EventId = 1104,
        Level = LogLevel.Error,
        Message = "Provisionamento: tenant {TenantId} em ProvisioningFailed; janela de {Horas}h esgotada")]
    public static partial void JanelaEsgotada(ILogger logger, Guid tenantId, double horas, Exception excecao);
}
```

- [ ] **Step 5: Criar o handler**

`ProvisionTenantHandler.cs`:

```csharp
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Common.Messaging;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;
using Microsoft.Extensions.Logging;

namespace IdentityGateway.Application.Tenants.ProvisionTenant;

/// <summary>
/// Provisiona o tenant: garante a Organization e marca <c>Active</c> — ou <c>ProvisioningFailed</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Contrato com quem despacha.</b> Falha transitória dentro da janela sai como a exceção original, e quem
/// despacha repete. Todo desfecho terminal — ativo, falha permanente, janela esgotada, mensagem repetida, tenant
/// inexistente — é <see cref="Result.Success()"/>, e a mensagem sai da fila. O commit é do <c>TransactionBehavior</c>.
/// </para>
/// <para>
/// <b>A decisão de desistir mora aqui, e não no transporte:</b> sobrevive à troca do despacho em processo pelo
/// broker sem mudar, e o relógio falso a torna testável.
/// </para>
/// </remarks>
public sealed class ProvisionTenantHandler(
    ITenantRepository tenants,
    IIdentityProvider identidade,
    IProvisioningPolicy politica,
    IDateTimeProvider relogio,
    ILogger<ProvisionTenantHandler> logger)
    : ICommandHandler<ProvisionTenantCommand>
{
    public async ValueTask<Result> Handle(ProvisionTenantCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Tenant? tenant = await tenants.GetAsync(command.TenantId, cancellationToken);

        if (tenant is null)
        {
            ProvisioningLogs.TenantInexistente(logger, command.TenantId.Value);
            return Result.Success();
        }

        // Só Pending. Active é mensagem repetida; ProvisioningFailed é decisão registrada, que só o retry manual
        // desfaz — devolvendo o tenant a Pending antes de reenfileirar. Reprovisionar um Failed aqui apagaria a
        // decisão em silêncio.
        if (tenant.Status != TenantStatus.Pending)
        {
            ProvisioningLogs.ForaDePending(logger, tenant.Id.Value, tenant.Status);
            return Result.Success();
        }

        string organizacao;

        try
        {
            organizacao = await identidade.EnsureOrganizationAsync(
                tenant.Id, tenant.Slug, tenant.Name, cancellationToken);
        }
        catch (IdentityProviderInconsistencyException excecao)
        {
            ProvisioningLogs.FalhaPermanente(logger, tenant.Id.Value, excecao);
            tenant.MarkProvisioningFailed();
            return Result.Success();
        }
        catch (Exception excecao) when (!cancellationToken.IsCancellationRequested && JanelaEsgotada(tenant))
        {
            // O filtro olha o token, e não o tipo da exceção: o timeout da resiliência chega como
            // TaskCanceledException — um OperationCanceledException — e um filtro por tipo o tiraria da janela. Só o
            // desligamento do host fica de fora: a mensagem volta no próximo ciclo.
            ProvisioningLogs.JanelaEsgotada(logger, tenant.Id.Value, politica.MaxPendingDuration.TotalHours, excecao);
            tenant.MarkProvisioningFailed();
            return Result.Success();
        }

        tenant.MarkProvisioned(organizacao);
        ProvisioningLogs.Provisionado(logger, tenant.Id.Value);

        return Result.Success();
    }

    // Fechada no fim: RegisteredAt + janela já conta como esgotada.
    private bool JanelaEsgotada(Tenant tenant) =>
        relogio.UtcNow >= tenant.RegisteredAt + politica.MaxPendingDuration;
}
```

Se o analisador acusar `CA1031` no `catch (Exception)`, seguir o precedente do `OutboxProcessor`, que captura
`Exception` com filtro `when` sem supressão — conferir com `grep -n "catch (Exception" src -r` e usar a mesma forma.

- [ ] **Step 6: Rodar e ver passar**

Run: `dotnet test tests/IdentityGateway.Application.UnitTests --filter ProvisionTenantHandlerTests`
Expected: PASS, 11 testes.

Run: `dotnet test tests/IdentityGateway.ArchitectureTests`
Expected: PASS — `Handlers_SaoSealed`, `CommandsEQueries_SaoRecord` e a regra do Mediator cobrem os tipos novos.

- [ ] **Step 7: 🧪 Prova por mutação (três)**

1. Trocar o filtro por `when (excecao is not OperationCanceledException && JanelaEsgotada(tenant))`. Rodar
   `--filter ProvisionTenantHandlerTests`: `TimeoutDaResilienciaDepoisDaJanela_MarcaFailed` fica vermelho. Reverter.
2. Trocar `tenant.Status != TenantStatus.Pending` por
   `tenant.Status is not (TenantStatus.Pending or TenantStatus.ProvisioningFailed)`: `TenantEmProvisioningFailed_NaoEReprovisionado`
   fica vermelho. Reverter.
3. Trocar `>=` por `>` em `JanelaEsgotada`: `FalhaNaFronteiraExataDaJanela_MarcaFailed` fica vermelho. Reverter.

Verde de novo depois das três.

- [ ] **Step 8: Commit**

```bash
git add src/IdentityGateway.Application tests/IdentityGateway.Application.UnitTests
git commit -m "feat: handler do provisionamento de tenant

So age em Pending. Inconsistencia vira ProvisioningFailed na hora; falha
transitoria propaga dentro da janela e vira Failed depois dela. O filtro
da janela olha o CancellationToken, nao o tipo da excecao, porque o
timeout da resiliencia e um OperationCanceledException.

Mutacoes: filtro por tipo (timeout apos a janela ficou vermelho); Failed
tratado como Pending (reprovisionamento ficou vermelho); > no lugar de >=
(fronteira exata ficou vermelho)."
```

---

### Task 5: `DispatchingOutboxPublisher`

**Files:**
- Create: `src/IdentityGateway.Infrastructure/Persistence/Outbox/DispatchingOutboxPublisher.cs`
- Delete: `src/IdentityGateway.Infrastructure/Persistence/Outbox/LoggingOutboxPublisher.cs`
- Modify: `src/IdentityGateway.Infrastructure/DependencyInjection.cs` (`AddOutbox`)
- Test: `tests/IdentityGateway.Infrastructure.IntegrationTests/Persistence/Outbox/DispatchingOutboxPublisherTests.cs`

**Interfaces:**
- Consumes: `ProvisionTenantCommand` (Task 4); `OutboxEventTypes.Registrados`; `TenantRegistered`, `TenantActivated`.
- Produces:
  - `internal sealed partial class DispatchingOutboxPublisher(IServiceScopeFactory scopeFactory, ILogger<DispatchingOutboxPublisher> logger) : IOutboxPublisher`
  - `internal static IReadOnlyCollection<Type> DispatchingOutboxPublisher.TiposComDestino`

- [ ] **Step 1: Escrever os testes**

```csharp
using IdentityGateway.Application.Tenants.ProvisionTenant;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Errors;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Domain.Tenants.Events;
using IdentityGateway.Infrastructure.Persistence.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace IdentityGateway.Infrastructure.IntegrationTests.Persistence.Outbox;

public sealed class DispatchingOutboxPublisherTests
{
    private sealed record EventoSemDestino : IDomainEvent
    {
        public DateTimeOffset OccurredOn { get; init; } = DateTimeOffset.UtcNow;
    }

    // O provider não é descartado: vive o tempo de um teste e não segura recurso externo. Se o analisador acusar
    // CA2000 aqui, transformar a classe em IDisposable guardando os providers numa lista, em vez de suprimir.
    private static DispatchingOutboxPublisher Publisher(Action<IServiceCollection>? registrar = null)
    {
        ServiceCollection services = new();
        registrar?.Invoke(services);
        IServiceScopeFactory fabrica = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new DispatchingOutboxPublisher(fabrica, NullLogger<DispatchingOutboxPublisher>.Instance);
    }

    [Fact]
    public void TodoEventoDoOutbox_TemDestinoNoPublisher()
    {
        // Um evento novo no mapa do Outbox, sem destino aqui, seria gravado e lançaria a cada tentativa. Este teste
        // obriga a decidir o destino — mesmo que seja "sem consumidor" — no mesmo PR que cria o evento.
        OutboxEventTypes.Registrados.Should().BeSubsetOf(DispatchingOutboxPublisher.TiposComDestino);
    }

    [Fact]
    public async Task EventoForaDoMapa_Lanca()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        Func<Task> publicar = () => Publisher().PublishAsync(new EventoSemDestino(), ct);

        await publicar.Should().ThrowAsync<InvalidOperationException>().WithMessage("*EventoSemDestino*");
    }

    [Fact]
    public async Task TenantActivated_EEntregueSemResolverNada()
    {
        // O provider está vazio: se o publisher tentasse resolver o ISender, lançaria. "Sem consumidor" é entregue.
        CancellationToken ct = TestContext.Current.CancellationToken;

        Func<Task> publicar = () => Publisher().PublishAsync(new TenantActivated(TenantId.New()), ct);

        await publicar.Should().NotThrowAsync();
    }

    [Fact]
    public async Task TenantRegistered_EnviaOProvisionamentoDoMesmoTenant()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Mediator.ISender sender = Substitute.For<Mediator.ISender>();
        sender.Send(Arg.Any<Mediator.ICommand<Result>>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(Result.Success()));
        var tenant = TenantId.New();

        await Publisher(services => services.AddScoped(_ => sender))
            .PublishAsync(new TenantRegistered(tenant, "acme"), ct);

        await sender.Received(1).Send(
            Arg.Is<Mediator.ICommand<Result>>(comando => comando is ProvisionTenantCommand
                && ((ProvisionTenantCommand)comando).TenantId == tenant),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CommandQueDevolveFalha_Lanca()
    {
        // Todo desfecho terminal do handler é Success; uma falha aqui é erro de programação, e engoli-la marcaria a
        // mensagem como entregue sem nada ter acontecido.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Mediator.ISender sender = Substitute.For<Mediator.ISender>();
        sender.Send(Arg.Any<Mediator.ICommand<Result>>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(Result.Failure(Error.Failure("Teste.Falha", "falha de teste"))));

        Func<Task> publicar = () => Publisher(services => services.AddScoped(_ => sender))
            .PublishAsync(new TenantRegistered(TenantId.New(), "acme"), ct);

        await publicar.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Teste.Falha*");
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet build tests/IdentityGateway.Infrastructure.IntegrationTests`
Expected: FAIL de compilação — `DispatchingOutboxPublisher` não existe.

- [ ] **Step 3: Implementar o publisher**

`DispatchingOutboxPublisher.cs`. Atenção: **não** usar `using Mediator;` — o namespace traz `Mediator.ICommand`, que
colide com o `ICommand` da Application. Referenciar `Mediator.ISender` qualificado.

```csharp
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Common.Messaging;
using IdentityGateway.Application.Tenants.ProvisionTenant;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IdentityGateway.Infrastructure.Persistence.Outbox;

/// <summary>
/// Entrega cada evento do Outbox ao seu destino dentro do próprio processo, como command do Mediator.
/// </summary>
/// <remarks>
/// <para>
/// <b>Transporte provisório.</b> Sem broker nesta versão (fatia B), o Outbox é o transporte e este publisher é o
/// consumidor. Quando o broker chegar, este é o único ponto que muda: ele passa a publicar lá, e um consumidor do
/// broker envia o mesmo command.
/// </para>
/// <para>
/// <b>Um escopo de DI por mensagem, e é regra.</b> O <see cref="OutboxProcessor"/> rastreia as mensagens do lote no
/// <c>AppDbContext</c> do seu escopo e grava o resultado com um <c>SaveChanges</c> depois do despacho. Se o handler
/// usasse o mesmo contexto, esse <c>SaveChanges</c> gravaria também o que um handler que falhou deixou modificado —
/// um tenant <c>Active</c> persistido por um provisionamento que lançou. Por isso o <c>ISender</c> é resolvido num
/// escopo novo, nunca recebido pelo construtor.
/// </para>
/// <para>
/// <b>Contrato com os handlers:</b> falha transitória chega como exceção, e o Outbox repete; desfecho terminal é
/// <see cref="Result.Success()"/>. Um <c>Result</c> de falha é erro de programação e lança — engoli-lo marcaria a
/// mensagem como entregue sem nada ter acontecido.
/// </para>
/// </remarks>
internal sealed partial class DispatchingOutboxPublisher(
    IServiceScopeFactory scopeFactory,
    ILogger<DispatchingOutboxPublisher> logger) : IOutboxPublisher
{
    // Nulo = "sem consumidor nesta versão": entregue de propósito, e dito no log. Um evento fora do mapa lança — o
    // teste de cobertura obriga a decidir o destino no mesmo PR que cria o evento.
    private static readonly Dictionary<Type, Func<IDomainEvent, ICommand?>> Destinos = new()
    {
        [typeof(TenantRegistered)] = evento => new ProvisionTenantCommand(((TenantRegistered)evento).TenantId),
        [typeof(TenantActivated)] = _ => null,
    };

    /// <summary>Tipos de evento com destino decidido — o teste de cobertura compara com o mapa do Outbox.</summary>
    internal static IReadOnlyCollection<Type> TiposComDestino => Destinos.Keys;

    /// <inheritdoc />
    public async Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (!Destinos.TryGetValue(domainEvent.GetType(), out Func<IDomainEvent, ICommand?>? destino))
        {
            throw new InvalidOperationException(
                $"O evento '{domainEvent.GetType().Name}' não tem destino em {nameof(DispatchingOutboxPublisher)}. "
                + "Acrescente uma entrada no mapa — um command, ou nulo para 'sem consumidor'.");
        }

        ICommand? comando = destino(domainEvent);

        if (comando is null)
        {
            SemConsumidor(logger, domainEvent.GetType().Name);
            return;
        }

        await using AsyncServiceScope escopo = scopeFactory.CreateAsyncScope();
        Mediator.ISender sender = escopo.ServiceProvider.GetRequiredService<Mediator.ISender>();

        Result resultado = await sender.Send(comando, cancellationToken);

        if (resultado.IsFailure)
        {
            CommandFalhou(logger, comando.GetType().Name, resultado.Error.Code);
            throw new InvalidOperationException(
                $"{comando.GetType().Name} devolveu falha ({resultado.Error.Code}): {resultado.Error.Message}. "
                + "Handler despachado pelo Outbox só devolve Success; falha transitória é exceção.");
        }
    }

    [LoggerMessage(
        EventId = 2100,
        Level = LogLevel.Debug,
        Message = "Outbox: {Evento} sem consumidor nesta versão; dado como entregue")]
    private static partial void SemConsumidor(ILogger logger, string evento);

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Error,
        Message = "Outbox: {Comando} devolveu falha {Codigo}; erro de programação")]
    private static partial void CommandFalhou(ILogger logger, string comando, string codigo);
}
```

- [ ] **Step 4: Trocar o registro e apagar o publisher antigo**

Em `DependencyInjection.cs`, `AddOutbox`, substituir o início do método até o `AddScoped<OutboxProcessor>()` por:

```csharp
        // O despacho é em processo (fatia B): cada evento vira command do Mediator. Scoped porque é resolvido no
        // escopo do processador — e ele abre o próprio escopo por mensagem, ver DispatchingOutboxPublisher. Quando o
        // broker chegar, a troca é nesta linha.
        services.AddScoped<IOutboxPublisher, DispatchingOutboxPublisher>();
        services.AddScoped<OutboxProcessor>();
```

Apagar também o comentário órfão que começa em "Singleton porque a memória do que já foi notificado…" — ele descreve
um registro que não existe mais no template. O comentário "Lido direto da configuração, e não por IOptions…" fica,
porque explica o `habilitado` logo abaixo.

Run: `git rm src/IdentityGateway.Infrastructure/Persistence/Outbox/LoggingOutboxPublisher.cs`

Conferir que nada mais o cita no código:

Run: `grep -rn "LoggingOutboxPublisher" src tests --include=*.cs`
Expected: nenhuma linha.

- [ ] **Step 5: Rodar e ver passar**

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter "FullyQualifiedName~DispatchingOutboxPublisherTests|FullyQualifiedName~DependencyInjectionTests"`
Expected: PASS.

- [ ] **Step 6: 🧪 Prova por mutação**

Remover a linha `[typeof(TenantActivated)] = _ => null,`. Rodar o filtro do Step 5:
`TodoEventoDoOutbox_TemDestinoNoPublisher` fica vermelho (e `TenantActivated_EEntregueSemResolverNada` também,
com a `InvalidOperationException`). Reverter; verde.

- [ ] **Step 7: Commit**

```bash
git add -A src/IdentityGateway.Infrastructure tests/IdentityGateway.Infrastructure.IntegrationTests
git commit -m "feat: despacho em processo dos eventos do Outbox

DispatchingOutboxPublisher substitui o LoggingOutboxPublisher: cada
evento vira command do Mediator, resolvido num escopo de DI proprio por
mensagem. TenantRegistered envia ProvisionTenantCommand; TenantActivated
fica sem consumidor, entregue de proposito. Evento fora do mapa e Result
de falha lancam.

Mutacao: sem TenantActivated no mapa, o teste de cobertura contra
OutboxEventTypes ficou vermelho."
```

---

### Task 6: Provisionamento ponta a ponta contra PostgreSQL e Keycloak reais

**Files:**
- Modify: `tests/IdentityGateway.Infrastructure.IntegrationTests/PostgresFixture.cs`
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Provisioning/ComposicaoDoProvisionamento.cs`
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Provisioning/ProvisionamentoContraKeycloakTests.cs`

**Interfaces:**
- Consumes: tudo das Tasks 2–5; `KeycloakFixture`, `Interceptacao`/`Interceptar` (fatia A); `AddApplication`, `AddInfrastructure`; `OutboxProcessor.ProcessarLoteAsync`.
- Produces: `public string PostgresFixture.ConnectionString`; nada consumido por tasks seguintes.

- [ ] **Step 1: Expor a connection string da fixture**

Em `PostgresFixture.cs`:

```csharp
    /// <summary>Connection string do contêiner, para testes que montam a composição inteira.</summary>
    public string ConnectionString => _container.GetConnectionString();
```

- [ ] **Step 2: Criar a composição e os helpers**

`Provisioning/ComposicaoDoProvisionamento.cs`:

```csharp
using IdentityGateway.Application;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Tenants.RegisterTenant;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;
using IdentityGateway.Infrastructure.Persistence;
using IdentityGateway.Infrastructure.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Infrastructure.IntegrationTests.Provisioning;

/// <summary>
/// A composição da aplicação (Application + Infrastructure) sobre PostgreSQL e Keycloak reais, e os passos do
/// provisionamento que os testes repetem.
/// </summary>
/// <remarks>
/// O despachante roda à mão (<see cref="ProcessarCicloAsync"/>), um ciclo por chamada: o <c>OutboxWorker</c> fica
/// desligado, porque um laço com temporizador tornaria o teste dependente de tempo.
/// </remarks>
internal static class ComposicaoDoProvisionamento
{
    internal static ServiceProvider Criar(
        PostgresFixture postgres, KeycloakFixture keycloak, Action<IServiceCollection>? ajustar = null)
    {
        IConfiguration configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = postgres.ConnectionString,
                ["Jwt:Issuer"] = "identitygateway",
                ["Jwt:Audience"] = "identitygateway-api",
                ["Jwt:SigningKey"] = new string('k', 32),
                ["Keycloak:Admin:BaseUrl"] = keycloak.BaseUrl,
                ["Keycloak:Admin:Realm"] = KeycloakFixture.Realm,
                ["Keycloak:Admin:ClientId"] = "identity-gateway",
                ["Keycloak:Admin:PrivateKeyPem"] = keycloak.Chaves.PemPrivado,
                ["Keycloak:Admin:AllowInsecureHttp"] = "true",
                ["HttpResilience:MaxRetryAttempts"] = "1",
                ["Outbox:Enabled"] = "false",
                ["Plans:free:tier"] = "Free",
                ["Plans:free:maxUsers"] = "5",
                ["Plans:free:maxClients"] = "1",
            })
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddApplication();
        services.AddInfrastructure(configuracao);
        ajustar?.Invoke(services);

        return services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>Registra um tenant pelo caminho de produção: command, pipeline e commit com a mensagem no Outbox.</summary>
    internal static async Task<TenantId> RegistrarAsync(ServiceProvider provider, CancellationToken ct)
    {
        string slug = KeycloakFixture.SlugUnico().Value;
        await using AsyncServiceScope escopo = provider.CreateAsyncScope();
        Mediator.ISender sender = escopo.ServiceProvider.GetRequiredService<Mediator.ISender>();

        Result<TenantId> resultado = await sender.Send(
            new RegisterTenantCommand("Acme Provisionamento", slug, "free", "admin@acme.com"), ct);

        resultado.IsSuccess.Should().BeTrue(resultado.IsFailure ? resultado.Error.Message : string.Empty);
        return resultado.Value;
    }

    /// <summary>Um ciclo do despachante, num escopo novo — como o <c>OutboxWorker</c> faz.</summary>
    internal static async Task ProcessarCicloAsync(ServiceProvider provider, CancellationToken ct)
    {
        await using AsyncServiceScope escopo = provider.CreateAsyncScope();
        await escopo.ServiceProvider.GetRequiredService<OutboxProcessor>().ProcessarLoteAsync(ct);
    }

    /// <summary>A mensagem do tipo informado que carrega o id do tenant.</summary>
    /// <remarks>Filtro de conteúdo em memória: <c>content</c> é <c>jsonb</c> e não aceita <c>LIKE</c>.</remarks>
    internal static async Task<OutboxMessage> MensagemAsync(
        ServiceProvider provider, string tipo, TenantId tenant, CancellationToken ct)
    {
        await using AsyncServiceScope escopo = provider.CreateAsyncScope();
        AppDbContext contexto = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

        List<OutboxMessage> doTipo = await contexto.OutboxMessages.AsNoTracking()
            .Where(mensagem => mensagem.Type == tipo)
            .ToListAsync(ct);

        return doTipo.Single(mensagem => mensagem.Content.Contains(tenant.Value.ToString(), StringComparison.Ordinal));
    }

    /// <summary>Torna a mensagem elegível agora, sem esperar o backoff.</summary>
    /// <remarks>
    /// Usa o <c>now()</c> do banco, que é o relógio que a reserva compara. Chamado também antes do primeiro ciclo: o
    /// <c>next_attempt_on</c> inicial vem do relógio da aplicação, e um contêiner com o relógio alguns segundos atrás
    /// deixaria a mensagem fora do lote — o teste ficaria intermitente.
    /// </remarks>
    internal static async Task LiberarAsync(ServiceProvider provider, Guid mensagem, CancellationToken ct)
    {
        await using AsyncServiceScope escopo = provider.CreateAsyncScope();
        AppDbContext contexto = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

        await contexto.Database.ExecuteSqlAsync(
            $"UPDATE outbox_messages SET next_attempt_on = now() - interval '1 second' WHERE id = {mensagem}", ct);
    }

    internal static async Task<Tenant> TenantAsync(ServiceProvider provider, TenantId tenant, CancellationToken ct)
    {
        await using AsyncServiceScope escopo = provider.CreateAsyncScope();
        AppDbContext contexto = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

        return await contexto.Tenants.AsNoTracking().SingleAsync(item => item.Id == tenant, ct);
    }
}

/// <summary>
/// <see cref="IUnitOfWork"/> que falha as próximas N vezes e depois grava normalmente.
/// </summary>
/// <remarks>
/// Simula o commit perdido depois de a Organization já existir no Keycloak. Lança <b>sem</b> chamar o
/// <c>SaveChanges</c>, então o que o handler alterou fica só rastreado no contexto do escopo dele.
/// </remarks>
internal sealed class UnitOfWorkQueFalha(AppDbContext contexto, int[] falhasRestantes) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Decrement(ref falhasRestantes[0]) >= 0)
        {
            throw new DbUpdateException("Commit perdido (injetado pelo teste).");
        }

        return contexto.SaveChangesAsync(cancellationToken);
    }
}
```

- [ ] **Step 3: Escrever os testes**

`Provisioning/ProvisionamentoContraKeycloakTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;
using IdentityGateway.Infrastructure.Persistence;
using IdentityGateway.Infrastructure.Persistence.Outbox;
using Microsoft.Extensions.DependencyInjection;
using static IdentityGateway.Infrastructure.IntegrationTests.Provisioning.ComposicaoDoProvisionamento;

namespace IdentityGateway.Infrastructure.IntegrationTests.Provisioning;

/// <summary>
/// O provisionamento inteiro — registro, Outbox, despacho, handler, Keycloak — contra PostgreSQL e Keycloak reais.
/// </summary>
public sealed class ProvisionamentoContraKeycloakTests(PostgresFixture postgres, KeycloakFixture keycloak)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task KeycloakForaNoPrimeiroCiclo_TenantViraActiveQuandoEleVolta()
    {
        // A demonstração do M1 (§16) em forma de teste: o tenant é aceito com o Keycloak fora, e fica Active sozinho
        // quando ele volta.
        CancellationToken ct = TestContext.Current.CancellationToken;
        bool[] keycloakFora = [true];
        Interceptacao interceptacao = new()
        {
            ResponderSemEnviar = (_, _) => keycloakFora[0] ? HttpStatusCode.ServiceUnavailable : null,
        };
        await using ServiceProvider provider = Criar(postgres, keycloak, services => services.Interceptar(interceptacao));

        TenantId tenant = await RegistrarAsync(provider, ct);
        OutboxMessage registrado = await MensagemAsync(provider, "tenant-registered", tenant, ct);

        await LiberarAsync(provider, registrado.Id, ct);
        await ProcessarCicloAsync(provider, ct);

        (await TenantAsync(provider, tenant, ct)).Status.Should().Be(TenantStatus.Pending);
        OutboxMessage aposFalha = await MensagemAsync(provider, "tenant-registered", tenant, ct);
        aposFalha.Attempts.Should().Be(1);
        aposFalha.ProcessedOn.Should().BeNull();
        aposFalha.Error.Should().NotBeNullOrEmpty();

        keycloakFora[0] = false;
        await LiberarAsync(provider, registrado.Id, ct);
        await ProcessarCicloAsync(provider, ct);

        Tenant ativo = await TenantAsync(provider, tenant, ct);
        ativo.Status.Should().Be(TenantStatus.Active);
        (await MensagemAsync(provider, "tenant-registered", tenant, ct)).ProcessedOn.Should().NotBeNull();

        JsonElement organizacao = await keycloak.LerOrganizacaoCruaAsync(ativo.ExternalOrganizationId!, ct);
        organizacao.GetProperty("alias").GetString().Should().Be(ativo.Slug.Value);
        organizacao.GetProperty("attributes").GetProperty("gateway_tenant_id")[0].GetString()
            .Should().Be(tenant.Value.ToString());
    }

    [Fact]
    public async Task TenantActivatedDoProvisionamento_SaiComoEntregue()
    {
        // O próprio provisionamento grava tenant-activated no Outbox. Sem destino no publisher, essa mensagem falharia
        // a cada ciclo por ~25h; "sem consumidor" precisa sair como entregue no primeiro.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = Criar(postgres, keycloak);

        TenantId tenant = await RegistrarAsync(provider, ct);
        await LiberarAsync(provider, (await MensagemAsync(provider, "tenant-registered", tenant, ct)).Id, ct);
        await ProcessarCicloAsync(provider, ct);

        OutboxMessage ativado = await MensagemAsync(provider, "tenant-activated", tenant, ct);
        await LiberarAsync(provider, ativado.Id, ct);
        await ProcessarCicloAsync(provider, ct);

        OutboxMessage entregue = await MensagemAsync(provider, "tenant-activated", tenant, ct);
        entregue.ProcessedOn.Should().NotBeNull();
        entregue.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task CommitPerdidoDepoisDeCriarAOrganizacao_ProximoCicloReencontraSemDuplicar()
    {
        // Dois fatos num cenário só:
        // (1) isolamento de escopo — o commit do handler falha com o tenant já Active no contexto dele; se o publisher
        //     usasse o escopo do processador, o SaveChanges que registra o resultado do lote gravaria esse Active;
        // (2) idempotência entre Keycloak e banco — a Organization criada no primeiro ciclo é reencontrada pelo
        //     atributo no segundo, sem uma segunda.
        CancellationToken ct = TestContext.Current.CancellationToken;
        int[] falhasDoCommit = [0];
        await using ServiceProvider provider = Criar(postgres, keycloak, services =>
            services.AddScoped<IUnitOfWork>(sp => new UnitOfWorkQueFalha(
                sp.GetRequiredService<AppDbContext>(), falhasDoCommit)));

        TenantId tenant = await RegistrarAsync(provider, ct);
        Guid registrado = (await MensagemAsync(provider, "tenant-registered", tenant, ct)).Id;
        falhasDoCommit[0] = 1;

        await LiberarAsync(provider, registrado, ct);
        await ProcessarCicloAsync(provider, ct);

        Tenant aposCommitPerdido = await TenantAsync(provider, tenant, ct);
        aposCommitPerdido.Status.Should().Be(TenantStatus.Pending, "o commit do handler falhou; nada dele pode ter sido gravado");
        (await keycloak.ContarPorAliasAsync(aposCommitPerdido.Slug.Value, ct)).Should().Be(1);

        await LiberarAsync(provider, registrado, ct);
        await ProcessarCicloAsync(provider, ct);

        Tenant ativo = await TenantAsync(provider, tenant, ct);
        ativo.Status.Should().Be(TenantStatus.Active);
        (await keycloak.ContarPorAliasAsync(ativo.Slug.Value, ct)).Should().Be(1);
    }
}
```

- [ ] **Step 4: Rodar e ver passar**

Docker precisa estar rodando.

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter ProvisionamentoContraKeycloakTests`
Expected: PASS, 3 testes.

Se o primeiro teste falhar com a mensagem ainda com `Attempts == 0` depois do primeiro ciclo, a reserva não a
pegou: conferir se o `LiberarAsync` rodou **antes** do ciclo e se o `id` é o da mensagem certa. Não "consertar"
acrescentando espera.

- [ ] **Step 5: 🧪 Prova por mutação**

Em `DispatchingOutboxPublisher`, trocar a resolução por escopo novo pelo `ISender` do escopo atual: acrescentar
`Mediator.ISender senderDoEscopo` ao construtor primário e usar `senderDoEscopo.Send(...)` no lugar do bloco
`await using AsyncServiceScope escopo = ...`. Rodar o filtro do Step 4:
`CommitPerdidoDepoisDeCriarAOrganizacao_ProximoCicloReencontraSemDuplicar` fica vermelho em
`aposCommitPerdido.Status` (`Active` em vez de `Pending`). Reverter; verde.

(Os testes unitários da Task 5 também quebram com essa mutação, por mudar o construtor; o que importa registrar é
o teste de integração vermelho pelo **motivo** certo.)

- [ ] **Step 6: Commit**

```bash
git add tests/IdentityGateway.Infrastructure.IntegrationTests
git commit -m "test: provisionamento ponta a ponta contra PostgreSQL e Keycloak reais

Keycloak fora no primeiro ciclo e de volta no segundo: o tenant vira
Active sozinho, com a Organization correlacionada pelo atributo. O
tenant-activated gerado pelo proprio provisionamento sai como entregue.
Commit perdido depois de criar a Organization: o tenant segue Pending e o
ciclo seguinte a reencontra sem duplicar.

E o primeiro teste que roda o OutboxProcessor de verdade.

Mutacao: publisher usando o ISender do escopo do processador deixou o
tenant Active apos o commit perdido."
```

---

### Task 7: `GET /api/v1/tenants/{tenantId}/provisioning`

**Files:**
- Modify: `src/IdentityGateway.Domain/Tenants/TenantErrors.cs`
- Create: `src/IdentityGateway.Application/Common/Abstractions/ITenantQueries.cs`
- Create: `src/IdentityGateway.Application/Tenants/GetTenantProvisioning/GetTenantProvisioningQuery.cs`, `TenantProvisioningResponse.cs`, `GetTenantProvisioningHandler.cs`
- Create: `src/IdentityGateway.Infrastructure/Persistence/Queries/TenantQueries.cs`
- Modify: `src/IdentityGateway.Infrastructure/DependencyInjection.cs`
- Modify: `src/IdentityGateway.Api/Modules/TenantsModule.cs`
- Test: `tests/IdentityGateway.Application.UnitTests/Tenants/GetTenantProvisioning/GetTenantProvisioningHandlerTests.cs`
- Test: `tests/IdentityGateway.Infrastructure.IntegrationTests/DependencyInjectionTests.cs`
- Test: `tests/IdentityGateway.Api.FunctionalTests/ProvisionamentoDeTenantTests.cs`

**Interfaces:**
- Consumes: `Tenant.RegisteredAt` (Task 2).
- Produces:
  - `TenantErrors.NotFound(TenantId)` — código `Tenant.NaoEncontrado`
  - `public sealed record TenantProvisioningView(TenantId TenantId, TenantStatus Status, DateTimeOffset RegisteredAt)`
  - `ITenantQueries.GetProvisioningAsync(TenantId, CancellationToken) : Task<TenantProvisioningView?>`
  - `public sealed record GetTenantProvisioningQuery(TenantId TenantId) : IQuery<TenantProvisioningResponse>`
  - `public sealed record TenantProvisioningResponse(Guid TenantId, string Status, DateTimeOffset RegisteredAt)`

- [ ] **Step 1: Escrever os testes do handler**

```csharp
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Tenants.GetTenantProvisioning;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;
using NSubstitute;

namespace IdentityGateway.Application.UnitTests.Tenants.GetTenantProvisioning;

public sealed class GetTenantProvisioningHandlerTests
{
    private readonly ITenantQueries _consultas = Substitute.For<ITenantQueries>();

    [Fact]
    public async Task TenantExistente_DevolveStatusComoTexto()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var tenant = TenantId.New();
        DateTimeOffset registro = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        _consultas.GetProvisioningAsync(tenant, Arg.Any<CancellationToken>())
            .Returns(new TenantProvisioningView(tenant, TenantStatus.ProvisioningFailed, registro));

        Result<TenantProvisioningResponse> resultado =
            await new GetTenantProvisioningHandler(_consultas).Handle(new GetTenantProvisioningQuery(tenant), ct);

        resultado.Value.Should().Be(new TenantProvisioningResponse(tenant.Value, "ProvisioningFailed", registro));
    }

    [Fact]
    public async Task TenantInexistente_DevolveNotFound()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        Result<TenantProvisioningResponse> resultado = await new GetTenantProvisioningHandler(_consultas)
            .Handle(new GetTenantProvisioningQuery(TenantId.New()), ct);

        resultado.IsFailure.Should().BeTrue();
        resultado.Error.Code.Should().Be("Tenant.NaoEncontrado");
    }
}
```

- [ ] **Step 2: Escrever os testes funcionais**

`tests/IdentityGateway.Api.FunctionalTests/ProvisionamentoDeTenantTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IdentityGateway.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace IdentityGateway.Api.FunctionalTests;

public sealed class ProvisionamentoDeTenantTests(IdentityGatewayApiFactory factory)
    : IClassFixture<IdentityGatewayApiFactory>
{
    private static string Rota(Guid id) => $"/api/v1/tenants/{id}/provisioning";

    private static async Task<(Guid Id, Uri Location)> RegistrarAsync(HttpClient client, CancellationToken ct)
    {
        string slug = $"prov-{Guid.NewGuid():N}"[..20];
        HttpResponseMessage resposta = await client.PostAsJsonAsync("/api/v1/tenants", new
        {
            name = "Acme Corp",
            slug,
            planCode = "free",
            initialAdminEmail = "admin@acme.com",
        }, ct);
        resposta.StatusCode.Should().Be(HttpStatusCode.Accepted);
        JsonElement json = await resposta.Content.ReadFromJsonAsync<JsonElement>(ct);
        return (Guid.Parse(json.GetProperty("tenantId").GetString()!), resposta.Headers.Location!);
    }

    [Fact]
    public async Task LocationDoRegistro_RespondeOStatusPending()
    {
        // Fecha o custo que o PR #1 declarou: o Location do 202 apontava para uma rota que não existia.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");
        (Guid id, Uri location) = await RegistrarAsync(client, ct);

        HttpResponseMessage resposta = await client.GetAsync(location, ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement json = await resposta.Content.ReadFromJsonAsync<JsonElement>(ct);
        json.GetProperty("tenantId").GetGuid().Should().Be(id);
        json.GetProperty("status").GetString().Should().Be("Pending");
        json.GetProperty("registeredAt").GetDateTimeOffset().Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        json.TryGetProperty("externalOrganizationId", out _).Should().BeFalse();
    }

    [Fact]
    public async Task TenantEmProvisioningFailed_Responde200ComOStatus()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");
        (Guid id, _) = await RegistrarAsync(client, ct);
        TenantId alvo = new(id);
        await factory.ComEscopoAsync(async contexto =>
        {
            Tenant tenant = await contexto.Tenants.SingleAsync(item => item.Id == alvo, ct);
            tenant.MarkProvisioningFailed();
            await contexto.SaveChangesAsync(ct);
        });

        JsonElement json = await client.GetFromJsonAsync<JsonElement>(Rota(id), ct);

        json.GetProperty("status").GetString().Should().Be("ProvisioningFailed");
    }

    [Fact]
    public async Task IdEmMaiusculas_EOMesmoTenant()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");
        (Guid id, _) = await RegistrarAsync(client, ct);

        HttpResponseMessage resposta = await client.GetAsync(
            $"/api/v1/tenants/{id.ToString().ToUpperInvariant()}/provisioning", ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TenantInexistente_Responde404ComProblemDetails()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");

        HttpResponseMessage resposta = await client.GetAsync(Rota(Guid.NewGuid()), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        resposta.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task IdMalFormado_Responde404()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");

        HttpResponseMessage resposta = await client.GetAsync("/api/v1/tenants/nao-e-guid/provisioning", ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SemToken_Responde401()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage resposta = await client.GetAsync(Rota(Guid.NewGuid()), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TokenSemOPapel_Responde403()
    {
        // Prova que a policy está ligada nesta rota: sem ele, esquecer o RequireAuthorization não quebraria teste.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado();

        HttpResponseMessage resposta = await client.GetAsync(Rota(Guid.NewGuid()), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
```

- [ ] **Step 3: Rodar e ver falhar**

Run: `dotnet build`
Expected: FAIL de compilação — `ITenantQueries`, `GetTenantProvisioningHandler` etc. não existem.

- [ ] **Step 4: Erro de domínio**

Em `TenantErrors.cs`:

```csharp
    /// <summary>Não existe tenant com o id informado.</summary>
    public static Error NotFound(TenantId tenantId) => Error.NotFound(
        "Tenant.NaoEncontrado",
        $"O tenant '{tenantId.Value}' não existe.");
```

- [ ] **Step 5: Porta de leitura**

`src/IdentityGateway.Application/Common/Abstractions/ITenantQueries.cs`:

```csharp
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Application.Common.Abstractions;

/// <summary>
/// Leituras de tenant que não precisam do agregado.
/// </summary>
/// <remarks>
/// Separado do <see cref="ITenantRepository"/>: o repositório devolve o agregado rastreado, para ser alterado; aqui
/// são projeções sem rastreamento, para responder consulta. Três campos não pedem o <c>Tenant</c> montado.
/// </remarks>
public interface ITenantQueries
{
    /// <summary>Estado do provisionamento, ou nulo se o tenant não existir.</summary>
    Task<TenantProvisioningView?> GetProvisioningAsync(TenantId tenantId, CancellationToken cancellationToken = default);
}

/// <summary>Projeção do estado de provisionamento de um tenant.</summary>
/// <param name="TenantId">Identidade do tenant.</param>
/// <param name="Status">Estado no ciclo de vida.</param>
/// <param name="RegisteredAt">Quando foi registrado, em UTC.</param>
public sealed record TenantProvisioningView(TenantId TenantId, TenantStatus Status, DateTimeOffset RegisteredAt);
```

- [ ] **Step 6: Query, resposta e handler**

`GetTenantProvisioningQuery.cs`:

```csharp
using IdentityGateway.Application.Common.Messaging;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Application.Tenants.GetTenantProvisioning;

/// <summary>Estado do provisionamento de um tenant — o destino do <c>Location</c> do <c>202</c> do registro.</summary>
/// <param name="TenantId">Tenant consultado.</param>
public sealed record GetTenantProvisioningQuery(TenantId TenantId) : IQuery<TenantProvisioningResponse>;
```

`TenantProvisioningResponse.cs`:

```csharp
namespace IdentityGateway.Application.Tenants.GetTenantProvisioning;

/// <summary>
/// Resposta da consulta de provisionamento.
/// </summary>
/// <remarks>
/// Sem o id da Organization nem o motivo de falha, de propósito: o primeiro é detalhe interno do Keycloak que
/// ninguém consome; o segundo, guardado como texto de exceção, arriscaria expor detalhe interno pela API — ele fica no
/// log de erro, com o <c>tenantId</c>.
/// </remarks>
/// <param name="TenantId">Identidade do tenant.</param>
/// <param name="Status">Nome do <c>TenantStatus</c>.</param>
/// <param name="RegisteredAt">Quando foi registrado, em UTC.</param>
public sealed record TenantProvisioningResponse(Guid TenantId, string Status, DateTimeOffset RegisteredAt);
```

`GetTenantProvisioningHandler.cs`:

```csharp
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Common.Messaging;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Application.Tenants.GetTenantProvisioning;

/// <summary>Responde <see cref="GetTenantProvisioningQuery"/>.</summary>
public sealed class GetTenantProvisioningHandler(ITenantQueries consultas)
    : IQueryHandler<GetTenantProvisioningQuery, TenantProvisioningResponse>
{
    public async ValueTask<Result<TenantProvisioningResponse>> Handle(
        GetTenantProvisioningQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        TenantProvisioningView? estado = await consultas.GetProvisioningAsync(query.TenantId, cancellationToken);

        if (estado is null)
        {
            return Result.Failure<TenantProvisioningResponse>(TenantErrors.NotFound(query.TenantId));
        }

        return new TenantProvisioningResponse(estado.TenantId.Value, estado.Status.ToString(), estado.RegisteredAt);
    }
}
```

- [ ] **Step 7: Implementar a leitura na Infrastructure**

`src/IdentityGateway.Infrastructure/Persistence/Queries/TenantQueries.cs`:

```csharp
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace IdentityGateway.Infrastructure.Persistence.Queries;

/// <summary>
/// Implementa <see cref="ITenantQueries"/> com projeções sem rastreamento.
/// </summary>
internal sealed class TenantQueries(AppDbContext context) : ITenantQueries
{
    /// <inheritdoc />
    public Task<TenantProvisioningView?> GetProvisioningAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        context.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => new TenantProvisioningView(tenant.Id, tenant.Status, tenant.RegisteredAt))
            .SingleOrDefaultAsync(cancellationToken);
}
```

Em `DependencyInjection.cs`, `AddPersistencia`, depois do `ITenantRepository`:

```csharp
        services.AddScoped<ITenantQueries, TenantQueries>();
```

(com `using IdentityGateway.Infrastructure.Persistence.Queries;`). Em `DependencyInjectionTests`, acrescentar
`[InlineData(typeof(ITenantQueries))]` à Theory de abstrações.

- [ ] **Step 8: Endpoint**

Em `TenantsModule.cs`, em `AddRoutes`, depois do `MapPost`:

```csharp
        app.MapGet("/api/v1/tenants/{tenantId:guid}/provisioning", ConsultarProvisionamentoAsync)
            .RequireAuthorization("PlatformAdmin")
            .WithName("ConsultarProvisionamento")
            .WithSummary("Estado do provisionamento de um tenant.")
            .Produces<TenantProvisioningResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);
```

E o método:

```csharp
    // Sempre 200 com o status, também quando Active: um 303 para o recurso do tenant pressupõe GET /tenants/{id}, que
    // ainda não existe. A restrição :guid na rota faz um id malformado responder 404 sem chegar ao handler.
    private static async Task<IResult> ConsultarProvisionamentoAsync(
        Guid tenantId,
        ISender sender,
        ICorrelationIdProvider correlationId,
        CancellationToken cancellationToken)
    {
        Result<TenantProvisioningResponse> resultado = await sender.Send(
            new GetTenantProvisioningQuery(new TenantId(tenantId)), cancellationToken);

        return resultado.ParaOk(correlationId.CorrelationId);
    }
```

(com `using IdentityGateway.Application.Tenants.GetTenantProvisioning;`).

- [ ] **Step 9: Rodar e ver passar**

Run: `dotnet test tests/IdentityGateway.Application.UnitTests --filter GetTenantProvisioningHandlerTests`
Expected: PASS, 2 testes.

Run: `dotnet test tests/IdentityGateway.Api.FunctionalTests --filter ProvisionamentoDeTenantTests`
Expected: PASS, 7 testes.

Run: `dotnet test tests/IdentityGateway.ArchitectureTests`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add src tests
git commit -m "feat: consulta do estado de provisionamento do tenant

GET /api/v1/tenants/{id}/provisioning, platform-admin, devolve tenantId,
status e registeredAt a partir de uma projecao sem rastreamento
(ITenantQueries). Fecha o Location do 202 do registro, que apontava para
rota inexistente. Nao expoe o id da Organization nem o motivo de falha."
```

---

### Task 8: Especificação v2.5, README, verificação ao vivo e handoff

**Files:**
- Create: `docs/especificacao-arquitetural-v2.5.md`
- Modify: `README.md`
- Create: `docs/superpowers/specs/2026-09-2X-consumidor-provisionamento-handoff.md` (data do dia da entrega)

**Interfaces:**
- Consumes: tudo.
- Produces: documentação.

- [ ] **Step 1: Criar a v2.5 a partir da v2.4**

Run: `cp docs/especificacao-arquitetural-v2.4.md docs/especificacao-arquitetural-v2.5.md`

No cabeçalho, `**Versão 2.4**` → `**Versão 2.5**`. Inserir, antes de `## 0. O que mudou da v2.3 para a v2.4`, a seção
nova, e renumerar as anteriores (`## 0.1` passa a ser a da v2.3→v2.4, e assim por diante):

```markdown
## 0. O que mudou da v2.4 para a v2.5

Esta versão registra o que a **fatia B (consumidor do provisionamento)** decidiu e verificou. Design da fatia:
[`2026-09-25-consumidor-provisionamento-design.md`](superpowers/specs/2026-09-25-consumidor-provisionamento-design.md).
**Nenhum ADR foi revogado.**

| # | Mudança | Onde |
|---|---|---|
| B1 | **Transporte em processo até a fatia do RabbitMQ.** O Outbox é o transporte e o `DispatchingOutboxPublisher` despacha cada evento como command do Mediator, num escopo de DI próprio por mensagem | ADR-006, §11.5 |
| B2 | **A §11.5 deixa de assumir MassTransit.** Verificado em 2026-09-25: o MassTransit v9 é comercial, e o v8 (Apache 2.0) tem suporte até o fim de 2026. A escolha da biblioteca fica para a fatia do broker | §11.5 |
| B3 | **A decisão de desistir é do handler**, contada desde `Tenant.RegisteredAt` contra `Provisioning:MaxPendingHours` (24h). O filtro de cancelamento olha o `CancellationToken`, não o tipo da exceção | §9.1, §11.5 |
| B4 | **O handler só age em `Pending`.** Mensagem repetida não reprovisiona um `ProvisioningFailed`; a saída de `Failed` é o retry manual, via `Pending` | §6.2, §11.5 |
| B5 | **A rede do Outbox é validada contra a janela na subida.** Padrões novos: teto de 60s e 1500 tentativas (~25h) | §9.1, §19 |
| B6 | **`OccurredOn` dos eventos precisa voltar do JSON** (`init`). Com só `get`, o evento relido carregava o instante da desserialização | §14.1 |
| B7 | `GET /tenants/{tenantId}/provisioning` entregue: `tenantId`, `status`, `registeredAt` | §8, §16 |
```

- [ ] **Step 2: Aplicar as mudanças nas seções**

Na v2.5:

1. **ADR-006, item "Mecanismo"**: acrescentar ao fim do parágrafo: "**Até a fatia do broker (v2.5), o transporte é em
   processo:** o `OutboxWorker` despacha cada evento como command do Mediator, e o retry é o do próprio Outbox."
2. **§6.2**: depois do parágrafo sobre `Terminated`, acrescentar: "**A saída de `ProvisioningFailed` é só o retry
   manual**, que devolve o tenant a `Pending` antes de reenfileirar — ou a reconciliação. Uma mensagem repetida que
   encontre o tenant em `ProvisioningFailed` não o reprovisiona (v2.5)."
3. **§9.1**, parágrafo "Se os retries se esgotarem…": substituir por "Se o tenant continuar em `Pending` além da
   **janela de provisionamento** (`Provisioning:MaxPendingHours`, 24h por padrão, contada desde `RegisteredAt`), o
   handler o marca `ProvisioningFailed` na próxima falha, e ele fica disponível para retry manual. Um erro
   **permanente** do adaptador (`IdentityProviderInconsistencyException`, §11.6) não espera a janela: vai direto a
   `ProvisioningFailed`. O Outbox precisa insistir por mais tempo que a janela, e a subida recusa configuração que não
   insista (v2.5)."
4. **§11.5**: substituir o bloco de código inteiro por:

```csharp
// Application: a regra do provisionamento — inclusive a de desistir.
public sealed class ProvisionTenantHandler(
    ITenantRepository tenants,
    IIdentityProvider identidade,
    IProvisioningPolicy politica,
    IDateTimeProvider relogio,
    ILogger<ProvisionTenantHandler> logger) : ICommandHandler<ProvisionTenantCommand>
{
    public async ValueTask<Result> Handle(ProvisionTenantCommand command, CancellationToken ct)
    {
        Tenant? tenant = await tenants.GetAsync(command.TenantId, ct);

        // Mensagem repetida, tenant removido, ou Failed (que só o retry manual desfaz): nada a fazer.
        if (tenant is null || tenant.Status != TenantStatus.Pending)
            return Result.Success();

        string organizationId;
        try
        {
            organizationId = await identidade.EnsureOrganizationAsync(tenant.Id, tenant.Slug, tenant.Name, ct);
        }
        catch (IdentityProviderInconsistencyException)
        {
            tenant.MarkProvisioningFailed();       // permanente: não espera a janela
            return Result.Success();
        }
        catch (Exception) when (!ct.IsCancellationRequested
                                && relogio.UtcNow >= tenant.RegisteredAt + politica.MaxPendingDuration)
        {
            tenant.MarkProvisioningFailed();       // janela esgotada
            return Result.Success();
        }
        // Dentro da janela, a exceção sobe intacta e o transporte repete.

        tenant.MarkProvisioned(organizationId);
        return Result.Success();                   // o commit é do TransactionBehavior
    }
}

// Infrastructure: até a fatia do broker, o transporte é o próprio Outbox. Um escopo de DI por mensagem —
// sem ele, o SaveChanges que registra o resultado do lote gravaria o que um handler que falhou deixou rastreado.
internal sealed class DispatchingOutboxPublisher(IServiceScopeFactory scopeFactory) : IOutboxPublisher
{
    public async Task PublishAsync(IDomainEvent domainEvent, CancellationToken ct)
    {
        if (domainEvent is not TenantRegistered registrado) return;   // simplificado; ver o código real
        await using AsyncServiceScope escopo = scopeFactory.CreateAsyncScope();
        Result resultado = await escopo.ServiceProvider.GetRequiredService<ISender>()
            .Send(new ProvisionTenantCommand(registrado.TenantId), ct);
        if (resultado.IsFailure) throw new InvalidOperationException(resultado.Error.Code);
    }
}
```

   E, logo abaixo do bloco: "**Por que não MassTransit (v2.5).** A v2.4 desenhava retry e redelivery no MassTransit e
   um consumidor de `Fault` marcando `ProvisioningFailed`. Verificado em 2026-09-25: o v9 exige licença comercial para
   uso em produção, e o v8 tem suporte até o fim de 2026. A decisão de desistir foi para o handler — sobrevive a
   qualquer transporte — e a escolha da biblioteca fica para a fatia do broker."
5. **§14.1**: acrescentar ao fim: "**O `OccurredOn` é parte do contrato e precisa voltar do JSON** (`init`, não só
   `get`); uma regra de arquitetura verifica todo evento do mapa do Outbox (v2.5)."
6. **§16, tabela de andamento**: linha **A** → "Entregue (PR #2)"; linha **B** → "Entregue"; linha **C** →
   "Próxima". O número do PR da fatia B entra quando o PR for aberto — não inventar. No parágrafo "Pendente do M0…",
   nada muda.
7. **§19**: acrescentar dois itens: "**`ProvisioningFailed` não tem saída automática** até existirem o retry manual e a
   reconciliação (v2.5); até lá, sair dele exige intervenção no banco." e "**Os números do Outbox estão dimensionados
   para o provisionamento em processo** (teto de 60s, 1500 tentativas): uma mensagem envenenada de outro tipo repetiria
   por ~25h. A revisar quando o broker chegar (v2.5)."

Conferir que a v2.5 não ficou com referência quebrada:

Run: `grep -n "v2.4" docs/especificacao-arquitetural-v2.5.md | head -30`
Expected: só menções históricas (a seção "O que mudou da v2.3 para a v2.4" e marcas "(v2.4)" em texto).

- [ ] **Step 3: README**

1. Trocar os links para `docs/especificacao-arquitetural-v2.4.md` por `v2.5` (linhas do "Estado do projeto", da
   tabela "Por onde começar a ler" e do link dos ADRs), e o texto "(referência normativa atual — a v2.3 é uma versão
   anterior…" por "(referência normativa atual — as anteriores ficam como registro histórico)".
2. Na linha do histórico de versões (`ideia → v2.0 → … → v2.3`), acrescentar `→ v2.4 → v2.5` se ainda não estiver, e um
   item `- **v2.5** registrou a fatia B: transporte em processo até o broker e a decisão de desistir no handler.`
3. Atualizar a contagem de testes em "Rodando local" com os números reais do Step 5.
4. Nova seção depois de "### Rodar a API pela IDE":

````markdown
### Demonstração: o tenant é provisionado quando o Keycloak volta

A API aceita o tenant com o Keycloak fora do ar e o provisiona sozinha quando ele volta (spec §16). O token é de
platform-admin, assinado com a chave de desenvolvimento do compose — o mesmo formato que a API valida hoje:

```bash
b64url() { openssl base64 -A | tr '+/' '-_' | tr -d '='; }
agora=$(date +%s)
cabecalho=$(printf '{"alg":"HS256","typ":"JWT"}' | b64url)
corpo=$(printf '{"sub":"0199a000-0000-7000-8000-000000000001","roles":"platform-admin","iss":"identitygateway","aud":"identitygateway-api","nbf":%d,"exp":%d}' "$agora" "$((agora + 3600))" | b64url)
assinatura=$(printf '%s.%s' "$cabecalho" "$corpo" | openssl dgst -sha256 -hmac "chave-de-desenvolvimento-nao-use-em-producao" -binary | b64url)
TOKEN="$cabecalho.$corpo.$assinatura"

docker compose stop keycloak

curl -si -X POST http://localhost:8080/api/v1/tenants \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"Acme","slug":"acme-demo","planCode":"free","initialAdminEmail":"admin@acme.com"}'
# 202 Accepted, com Location: /api/v1/tenants/{id}/provisioning

curl -s http://localhost:8080/api/v1/tenants/{id}/provisioning -H "Authorization: Bearer $TOKEN"
# {"tenantId":"…","status":"Pending",…}

docker compose start keycloak
# em cerca de um minuto — o teto do backoff do Outbox:
curl -s http://localhost:8080/api/v1/tenants/{id}/provisioning -H "Authorization: Bearer $TOKEN"
# {"tenantId":"…","status":"Active",…}
```
````

- [ ] **Step 4: Verificação ao vivo**

Docker rodando. Subir tudo a partir do código desta branch:

Run: `docker compose up -d --build`
Expected: `api` healthy; `curl -s http://localhost:8080/health/ready` responde `Healthy`.

Executar **exatamente** o roteiro do README do Step 3, colando os comandos. Registrar para o handoff: o status
code do `POST`, o corpo do primeiro `GET` (`Pending`), o horário do `start keycloak` e o horário do primeiro `GET`
que mostrou `Active`.

Se o token for recusado (401/403), o defeito está no snippet do README — corrigir o snippet (claims, chave, `aud`) e
repetir; não mexer na API. Se o tenant não ficar `Active` em ~2 minutos, olhar `docker compose logs api` pelos
EventIds 1100–1104 e 2000–2006 antes de qualquer hipótese.

Encerrar: `docker compose down`.

- [ ] **Step 5: Suíte completa**

Run: `dotnet build IdentityGateway.slnx`
Expected: 0 avisos, 0 erros.

Run: `dotnet test`
Expected: 0 falhas, 0 skips. Anotar o total por projeto para o README e o handoff.

- [ ] **Step 6: Handoff**

Criar `docs/superpowers/specs/2026-09-2X-consumidor-provisionamento-handoff.md` (data do dia) no formato do
[handoff da fatia A](../specs/2026-09-25-fundacao-keycloak-handoff.md): estado do repositório (branch, commits,
push/PR pendente de autorização), o que a fatia entregou por camada, a tabela da suíte por projeto, **a tabela de
mutações** (Task 1: `OccurredOn` `get`; Task 3: sem validação cruzada; Task 4: filtro por tipo, Failed como Pending,
`>`; Task 5: sem `TenantActivated` no mapa; Task 6: `ISender` do escopo do processador — com o resultado observado de
cada uma), o resultado da verificação ao vivo do Step 4, decisões tomadas durante a execução, pendências menores e o
próximo passo (fatia C — convite do admin inicial — começando por brainstorming, com as duas pendências da §9.1).

- [ ] **Step 7: Commit**

```bash
git add docs README.md
git commit -m "docs: especificacao v2.5, demonstracao no README e handoff da fatia B

A v2.5 registra o transporte em processo ate a fatia do broker, a errata
da secao 11.5 (sem MassTransit: v9 comercial, v8 com suporte ate o fim de
2026), a janela de provisionamento no handler e o OccurredOn no contrato
dos eventos. O README ganha a demonstracao do M1 por curl, verificada ao
vivo."
```

Push e PR ficam para autorização do usuário.
