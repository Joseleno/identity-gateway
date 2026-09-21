# Vertical de registro de tenant — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Entregar `POST /api/v1/tenants` ponta a ponta — do JSON até a linha em `tenants` com a mensagem `tenant-registered` no Outbox, na mesma unidade de trabalho.

**Architecture:** Três camadas sobre o agregado `Tenant` já existente. A Application define command, handler e duas portas; a Infrastructure implementa as portas com EF Core e mapeia o agregado em colunas planas; a Api expõe um módulo Carter protegido pela policy `PlatformAdmin`, respondendo `202`. Nenhuma chamada ao Keycloak: o evento no Outbox é o que dispara o provisionamento depois.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, Mediator (source generator), FluentValidation, Carter, xUnit v3 + NSubstitute + AwesomeAssertions, Testcontainers.

**Spec:** [`docs/superpowers/specs/2026-09-21-tenant-registro-vertical-design.md`](../specs/2026-09-21-tenant-registro-vertical-design.md)

## Global Constraints

- **`TreatWarningsAsErrors` ligado** — todo aviso quebra o build, inclusive os de estilo do `.editorconfig`.
- **`var` só quando o tipo está aparente à direita.** `WebApplicationBuilder builder = WebApplication.CreateBuilder(args);` — `var` ali não compila. `var id = Guid.CreateVersion7();` compila.
- **Comentário em português, identificadores em inglês.** Comentário explica **por quê**, nunca o quê.
- **Erro de negócio devolve `Result`; exception fica para falha de infraestrutura.**
- **`CancellationToken` propagado em toda chamada assíncrona.**
- **`IDateTimeProvider` em vez de `DateTime.UtcNow`.**
- **Provider InMemory do EF Core é proibido** em teste de integração — Testcontainers com PostgreSQL real.
- **Nenhuma dependência nova** nesta fatia. `Asp.Versioning.Http` fica de fora por decisão do ADR na spec.
- **Um caso de uso é uma pasta** com tudo dentro.
- **Handler é `sealed`; command é `record`** — regras de arquitetura que reprovam o build.
- **Nada na Application menciona o namespace `Mediator`** fora de `Common/Messaging` e `Common/Behaviors`. Use `ICommand<T>` e `ICommandHandler<,>` próprios.
- Testes usam `TestContext.Current.CancellationToken`, nunca `CancellationToken.None`.

---

## Estrutura de arquivos

| Arquivo | Responsabilidade |
|---|---|
| `Application/Common/Abstractions/ITenantRepository.cs` | Porta de persistência do tenant |
| `Application/Common/Abstractions/IPlanCatalog.cs` | Porta do catálogo de planos |
| `Application/Tenants/RegisterTenant/RegisterTenantCommand.cs` | A mensagem |
| `Application/Tenants/RegisterTenant/RegisterTenantValidator.cs` | Presença dos campos |
| `Application/Tenants/RegisterTenant/RegisterTenantHandler.cs` | Orquestração |
| `Infrastructure/Configuration/PlanOptions.cs` | Forma da seção `Plans` |
| `Infrastructure/Configuration/PlanCatalog.cs` | Catálogo em memória |
| `Infrastructure/Persistence/Configurations/TenantConfiguration.cs` | Mapeamento EF |
| `Infrastructure/Persistence/Repositories/TenantRepository.cs` | Implementação da porta |
| `Api/Modules/RegisterTenantRequest.cs` | Contrato de entrada |
| `Api/Modules/TenantAcceptedResponse.cs` | Contrato de saída |
| `Api/Modules/TenantsModule.cs` | Rota, policy, tradução |

**Ordem das tarefas:** Application (1–3) → Infrastructure (4–7) → Api (8–11) → fechamento (12).

---

### Task 1: Portas `ITenantRepository` e `IPlanCatalog`

Sem teste próprio: são interfaces, e o que as prova são as tarefas que as consomem (3, 6, 7).

**Files:**
- Create: `src/IdentityGateway.Application/Common/Abstractions/ITenantRepository.cs`
- Create: `src/IdentityGateway.Application/Common/Abstractions/IPlanCatalog.cs`

**Interfaces:**
- Consumes: `Tenant`, `TenantSlug`, `Plan` do `IdentityGateway.Domain.Tenants`.
- Produces: `ITenantRepository.Add(Tenant)`, `ITenantRepository.SlugExistsAsync(TenantSlug, CancellationToken) → Task<bool>`, `IPlanCatalog.Find(string) → Plan?`.

- [ ] **Step 1: Criar `ITenantRepository`**

```csharp
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Application.Common.Abstractions;

/// <summary>
/// Persistência do agregado <see cref="Tenant"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Não expõe <c>SaveChanges</c>.</b> Quem fecha a unidade de trabalho é o <c>TransactionBehavior</c>, e é
/// isso que põe o <c>INSERT</c> do tenant e a mensagem do Outbox no mesmo <c>SaveChanges</c> — logo, na mesma
/// transação implícita do EF Core. Um repositório que salvasse sozinho quebraria essa garantia sem erro
/// nenhum aparecer.
/// </para>
/// </remarks>
public interface ITenantRepository
{
    /// <summary>Marca o tenant para inserção. Não grava.</summary>
    void Add(Tenant tenant);

    /// <summary>Se já existe tenant com o slug informado.</summary>
    /// <remarks>
    /// Dá a mensagem de negócio boa (<c>409</c> nomeando o slug). Não substitui o índice único do banco: entre
    /// esta consulta e o <c>INSERT</c> há uma janela em que outra requisição grava o mesmo slug.
    /// </remarks>
    Task<bool> SlugExistsAsync(TenantSlug slug, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Criar `IPlanCatalog`**

```csharp
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Application.Common.Abstractions;

/// <summary>
/// Catálogo dos planos comerciais disponíveis.
/// </summary>
/// <remarks>
/// Plano é dado de catálogo, não entidade: a §6.1 o trata como value object dentro do <c>Tenant</c>, sem ciclo
/// de vida nem histórico próprio. A implementação lê da configuração, de modo que mudar um limite comercial
/// seja editar o appsettings — não uma migration.
/// </remarks>
public interface IPlanCatalog
{
    /// <summary>Resolve o plano pelo código, ou <c>null</c> se não existir.</summary>
    Plan? Find(string planCode);
}
```

- [ ] **Step 3: Compilar**

Run: `dotnet build`
Expected: 0 erros, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add src/IdentityGateway.Application/Common/Abstractions/
git commit -m "feat: portas de persistencia e catalogo do tenant"
```

---

### Task 2: `RegisterTenantCommand` e `RegisterTenantValidator`

**Files:**
- Create: `src/IdentityGateway.Application/Tenants/RegisterTenant/RegisterTenantCommand.cs`
- Create: `src/IdentityGateway.Application/Tenants/RegisterTenant/RegisterTenantValidator.cs`
- Modify: `src/IdentityGateway.Application/DependencyInjection.cs` (método `AddValidators`)
- Test: `tests/IdentityGateway.Application.UnitTests/Tenants/RegisterTenant/RegisterTenantValidatorTests.cs`

**Interfaces:**
- Consumes: `ICommand<TResponse>` de `Common/Messaging`, `TenantId` do domínio.
- Produces: `RegisterTenantCommand(string Name, string Slug, string PlanCode, string InitialAdminEmail) : ICommand<TenantId>`; `RegisterTenantValidator : AbstractValidator<RegisterTenantCommand>`.

- [ ] **Step 1: Escrever o teste que falha**

```csharp
using FluentValidation.Results;
using IdentityGateway.Application.Tenants.RegisterTenant;

namespace IdentityGateway.Application.UnitTests.Tenants.RegisterTenant;

/// <summary>
/// Cobre a validação de <b>presença</b> dos campos do comando.
/// </summary>
/// <remarks>
/// A validação de <b>forma</b> do slug não está aqui de propósito: ela vive em <c>TenantSlug.Create</c>, que
/// devolve <c>Result</c>, e duplicá-la faria a regra existir em dois lugares que envelheceriam separados.
/// </remarks>
public sealed class RegisterTenantValidatorTests
{
    private readonly RegisterTenantValidator _validator = new();

    private static RegisterTenantCommand Valido() =>
        new("Acme", "acme", "free", "admin@acme.com");

    [Fact]
    public void ComandoCompleto_Passa()
    {
        ValidationResult resultado = _validator.Validate(Valido());

        resultado.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NomeEmBranco_Falha(string nome)
    {
        ValidationResult resultado = _validator.Validate(Valido() with { Name = nome });

        resultado.IsValid.Should().BeFalse();
    }

    [Fact]
    public void NomeAcimaDe200_Falha()
    {
        ValidationResult resultado = _validator.Validate(Valido() with { Name = new string('a', 201) });

        resultado.IsValid.Should().BeFalse();
    }

    [Fact]
    public void SlugEmBranco_Falha()
    {
        ValidationResult resultado = _validator.Validate(Valido() with { Slug = "" });

        resultado.IsValid.Should().BeFalse();
    }

    [Fact]
    public void PlanCodeEmBranco_Falha()
    {
        ValidationResult resultado = _validator.Validate(Valido() with { PlanCode = "" });

        resultado.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("sem-arroba")]
    public void EmailInvalido_Falha(string email)
    {
        ValidationResult resultado = _validator.Validate(Valido() with { InitialAdminEmail = email });

        resultado.IsValid.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test tests/IdentityGateway.Application.UnitTests --filter RegisterTenantValidatorTests`
Expected: FALHA de compilação — `RegisterTenantCommand` e `RegisterTenantValidator` não existem.

- [ ] **Step 3: Criar o command**

```csharp
using IdentityGateway.Application.Common.Messaging;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Application.Tenants.RegisterTenant;

/// <summary>
/// Registra um tenant novo, ainda por provisionar.
/// </summary>
/// <remarks>
/// <para>
/// <b>O <c>InitialAdminEmail</c> é acréscimo deliberado ao que a §11.4 mostra.</b> A §9.1 e a §8 o exigem no
/// <c>POST /tenants</c>, e a §9.1 explica por quê: sem ele o tenant nasce trancado, já que convidar membros
/// exige <c>tenant-admin</c> daquele tenant, que ainda não existiria.
/// </para>
/// <para>
/// Nesta fatia o campo é validado e carregado, mas nada cria o convite — isso é trabalho do consumidor do
/// provisionamento. Sem carregá-lo, o endpoint aceitaria um campo obrigatório e o descartaria em silêncio.
/// </para>
/// </remarks>
public sealed record RegisterTenantCommand(
    string Name,
    string Slug,
    string PlanCode,
    string InitialAdminEmail) : ICommand<TenantId>;
```

- [ ] **Step 4: Criar o validator**

```csharp
using FluentValidation;

namespace IdentityGateway.Application.Tenants.RegisterTenant;

/// <summary>
/// Exige que os campos obrigatórios estejam presentes e bem formados.
/// </summary>
/// <remarks>
/// Cobre <b>presença</b>; a <b>forma</b> do slug é de <c>TenantSlug.Create</c>. A divisão evita a mesma regra
/// em dois lugares — e o validator recusa antes de o handler abrir transação, porque o
/// <c>ValidationBehavior</c> roda mais cedo no pipeline.
/// </remarks>
public sealed class RegisterTenantValidator : AbstractValidator<RegisterTenantCommand>
{
    public RegisterTenantValidator()
    {
        RuleFor(comando => comando.Name)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(comando => comando.Slug)
            .NotEmpty();

        RuleFor(comando => comando.PlanCode)
            .NotEmpty();

        RuleFor(comando => comando.InitialAdminEmail)
            .NotEmpty()
            .EmailAddress();
    }
}
```

- [ ] **Step 5: Registrar o validator**

Em `src/IdentityGateway.Application/DependencyInjection.cs`, no corpo de `AddValidators`, substituir o comentário `// Um registro por validator, conforme os agregados do IdentityGateway forem entrando.` por:

```csharp
        // Um registro por validator, conforme os agregados do IdentityGateway forem entrando.
        services.AddScoped<IValidator<RegisterTenantCommand>, RegisterTenantValidator>();
```

E acrescentar ao topo do arquivo: `using IdentityGateway.Application.Tenants.RegisterTenant;`

- [ ] **Step 6: Rodar e ver passar**

Run: `dotnet test tests/IdentityGateway.Application.UnitTests --filter RegisterTenantValidatorTests`
Expected: PASS, 8 execucoes de teste (6 metodos; as duas [Theory] contam 2 cada).

- [ ] **Step 7: Commit**

```bash
git add src/IdentityGateway.Application/ tests/IdentityGateway.Application.UnitTests/
git commit -m "feat: comando e validator de registro de tenant"
```

---

### Task 3: `RegisterTenantHandler`

**Files:**
- Create: `src/IdentityGateway.Application/Tenants/RegisterTenant/RegisterTenantHandler.cs`
- Test: `tests/IdentityGateway.Application.UnitTests/Tenants/RegisterTenant/RegisterTenantHandlerTests.cs`

**Interfaces:**
- Consumes: `ITenantRepository`, `IPlanCatalog` (Task 1); `RegisterTenantCommand` (Task 2); `Tenant.Register`, `TenantSlug.Create`, `TenantErrors.SlugInUse`, `TenantErrors.UnknownPlan`.
- Produces: `RegisterTenantHandler : ICommandHandler<RegisterTenantCommand, TenantId>`, com `Handle(RegisterTenantCommand, CancellationToken) → ValueTask<Result<TenantId>>`.

- [ ] **Step 1: Escrever o teste que falha**

```csharp
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Tenants.RegisterTenant;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;
using NSubstitute;

namespace IdentityGateway.Application.UnitTests.Tenants.RegisterTenant;

/// <summary>
/// Cobre a orquestração do registro: validar o slug, checar unicidade, resolver o plano, registrar.
/// </summary>
/// <remarks>
/// As portas são dublês porque o que está sob teste é a <b>ordem das decisões</b> e a tradução em
/// <c>Result</c> — não a persistência, que é assunto do teste de integração.
/// </remarks>
public sealed class RegisterTenantHandlerTests
{
    private readonly ITenantRepository _repositorio = Substitute.For<ITenantRepository>();
    private readonly IPlanCatalog _catalogo = Substitute.For<IPlanCatalog>();
    private readonly RegisterTenantHandler _handler;

    public RegisterTenantHandlerTests()
    {
        _catalogo.Find("free").Returns(new Plan(PlanTier.Free, 5, 1));
        _repositorio.SlugExistsAsync(Arg.Any<TenantSlug>(), Arg.Any<CancellationToken>()).Returns(false);

        _handler = new RegisterTenantHandler(_repositorio, _catalogo);
    }

    private static RegisterTenantCommand Comando(string slug = "acme", string plano = "free") =>
        new("Acme", slug, plano, "admin@acme.com");

    [Fact]
    public async Task ComandoValido_RegistraEDevolveOId()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        Result<TenantId> resultado = await _handler.Handle(Comando(), ct);

        resultado.IsSuccess.Should().BeTrue();
        resultado.Value.Value.Should().NotBe(Guid.Empty);
        _repositorio.Received(1).Add(Arg.Is<Tenant>(tenant => tenant.Slug.Value == "acme"));
    }

    [Fact]
    public async Task ComandoValido_LevantaOEventoDeRegistro()
    {
        // O evento é o que o Outbox grava na mesma transação — sem ele, nenhum tenant seria provisionado.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant? capturado = null;
        _repositorio.Add(Arg.Do<Tenant>(tenant => capturado = tenant));

        await _handler.Handle(Comando(), ct);

        capturado.Should().NotBeNull();
        capturado!.DomainEvents.Should().ContainSingle(evento => evento is Events.TenantRegistered);
    }

    [Fact]
    public async Task SlugMalFormado_DevolveValidation()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        Result<TenantId> resultado = await _handler.Handle(Comando(slug: "-invalido-"), ct);

        resultado.IsFailure.Should().BeTrue();
        resultado.Error.Code.Should().Be("TenantSlug.Invalido");
        _repositorio.DidNotReceive().Add(Arg.Any<Tenant>());
    }

    [Fact]
    public async Task SlugJaEmUso_DevolveConflict()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        _repositorio.SlugExistsAsync(Arg.Any<TenantSlug>(), Arg.Any<CancellationToken>()).Returns(true);

        Result<TenantId> resultado = await _handler.Handle(Comando(), ct);

        resultado.IsFailure.Should().BeTrue();
        resultado.Error.Code.Should().Be("Tenant.SlugEmUso");
        _repositorio.DidNotReceive().Add(Arg.Any<Tenant>());
    }

    [Fact]
    public async Task PlanoDesconhecido_DevolveValidation()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        _catalogo.Find("inexistente").Returns((Plan?)null);

        Result<TenantId> resultado = await _handler.Handle(Comando(plano: "inexistente"), ct);

        resultado.IsFailure.Should().BeTrue();
        resultado.Error.Code.Should().Be("Tenant.PlanoDesconhecido");
        _repositorio.DidNotReceive().Add(Arg.Any<Tenant>());
    }
}
```

Acrescentar ao topo: `using Events = IdentityGateway.Domain.Tenants.Events;`

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test tests/IdentityGateway.Application.UnitTests --filter RegisterTenantHandlerTests`
Expected: FALHA de compilação — `RegisterTenantHandler` não existe.

- [ ] **Step 3: Escrever o handler**

```csharp
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Common.Messaging;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Application.Tenants.RegisterTenant;

/// <summary>
/// Orquestra o registro de um tenant.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nenhuma chamada ao Keycloak acontece aqui.</b> O <c>INSERT</c> e o evento no Outbox saem no mesmo
/// <c>SaveChanges</c> — que é o que garante que ou os dois acontecem, ou nenhum. O provisionamento é trabalho
/// do consumidor da mensagem, mais tarde.
/// </para>
/// <para>
/// A checagem de unicidade não dispensa o índice único do banco: ela existe para dar a mensagem de negócio
/// nomeando o slug, e entre o <c>SELECT</c> e o <c>INSERT</c> há uma janela que só a constraint fecha.
/// </para>
/// </remarks>
public sealed class RegisterTenantHandler(
    ITenantRepository repositorio,
    IPlanCatalog catalogo)
    : ICommandHandler<RegisterTenantCommand, TenantId>
{
    public async ValueTask<Result<TenantId>> Handle(
        RegisterTenantCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<TenantSlug> slug = TenantSlug.Create(command.Slug);

        if (slug.IsFailure)
        {
            return Result.Failure<TenantId>(slug.Error);
        }

        if (await repositorio.SlugExistsAsync(slug.Value, cancellationToken))
        {
            return Result.Failure<TenantId>(TenantErrors.SlugInUse(slug.Value));
        }

        Plan? plano = catalogo.Find(command.PlanCode);

        if (plano is null)
        {
            return Result.Failure<TenantId>(TenantErrors.UnknownPlan(command.PlanCode));
        }

        Tenant tenant = Tenant.Register(command.Name, slug.Value, plano);

        repositorio.Add(tenant);

        return tenant.Id;
    }
}
```

- [ ] **Step 4: Rodar e ver passar**

Run: `dotnet test tests/IdentityGateway.Application.UnitTests --filter RegisterTenantHandlerTests`
Expected: PASS, 5 testes.

- [ ] **Step 5: Reativar os dois skips de arquitetura**

Em `tests/IdentityGateway.ArchitectureTests/RegrasDeMensageriaTests.cs`, remover o argumento `Skip` dos atributos das linhas 56 e 80, deixando `[Fact]`. Nos dois XML docs, substituir a frase que explica a espera — `Esperou da T0.3 até aqui porque não havia handler para inspecionar — ver decisão 17.` — por `Reativado com o handler de RegisterTenant.` quando ela existir.

- [ ] **Step 6: Rodar a suíte de arquitetura**

Run: `dotnet test tests/IdentityGateway.ArchitectureTests`
Expected: PASS, sem skips nas regras de mensageria.

- [ ] **Step 7: Commit**

```bash
git add src/IdentityGateway.Application/ tests/
git commit -m "feat: handler de registro de tenant

Reativa Handlers_SaoSealed e CommandsEQueries_SaoRecord, que aguardavam
exatamente este handler para nao passarem em vacuidade."
```

---

### Task 4: Mapeamento EF do `Tenant`

**Files:**
- Create: `src/IdentityGateway.Infrastructure/Persistence/Configurations/TenantConfiguration.cs`
- Modify: `src/IdentityGateway.Infrastructure/Persistence/AppDbContext.cs`
- Test: `tests/IdentityGateway.Infrastructure.IntegrationTests/Persistence/MapeamentoDeTenantTests.cs`

**Interfaces:**
- Consumes: `Tenant`, `TenantId`, `TenantSlug`, `Plan`, `PlanTier`, `TenantStatus`.
- Produces: tabela `tenants`; `AppDbContext.Tenants` (`internal DbSet<Tenant>`).

- [ ] **Step 1: Escrever o teste de round-trip que falha**

```csharp
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IdentityGateway.Infrastructure.IntegrationTests.Persistence;

/// <summary>
/// Prova que o agregado sobrevive à ida e à volta do banco.
/// </summary>
/// <remarks>
/// <para>
/// <b>É o teste que cobre a decisão de não dar ao <c>Tenant</c> um construtor sem parâmetro.</b> O EF Core
/// materializa pelo construtor parametrizado, casando parâmetros com propriedades por nome — e se esse casamento
/// se quebrar (renomear um parâmetro, por exemplo), a falha é de materialização em <b>runtime</b>, não de
/// compilação. Sem este teste, o sintoma apareceria na primeira leitura real.
/// </para>
/// <para>
/// Relê num contexto novo, não no mesmo: o change tracker devolveria a instância que já está em memória, e o
/// teste passaria sem nunca provar que a gravação funcionou.
/// </para>
/// </remarks>
public sealed class MapeamentoDeTenantTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Tenant_SobreviveAoRoundTrip()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        TenantSlug slug = TenantSlug.Create($"acme-{Guid.NewGuid():N}"[..20]).Value;
        Tenant original = Tenant.Register("Acme Corp", slug, new Plan(PlanTier.Standard, 50, 5));

        await using (AppDbContext escrita = postgres.CriarContexto())
        {
            escrita.Tenants.Add(original);
            await escrita.SaveChangesAsync(ct);
        }

        await using AppDbContext leitura = postgres.CriarContexto();
        Tenant? lido = await leitura.Tenants.SingleOrDefaultAsync(tenant => tenant.Id == original.Id, ct);

        lido.Should().NotBeNull();
        lido!.Name.Should().Be("Acme Corp");
        lido.Slug.Value.Should().Be(slug.Value);
        lido.Plan.Should().Be(new Plan(PlanTier.Standard, 50, 5));
        lido.Status.Should().Be(TenantStatus.Pending);
        lido.ExternalOrganizationId.Should().BeNull();
        lido.OccupiedSeats.Should().Be(0);
        lido.OverSubscribed.Should().BeFalse();
    }

    [Fact]
    public async Task OsEnums_SaoGravadosComoTexto()
    {
        // Texto e não int: um SELECT em producao dizendo 'Pending' responde a pergunta; dizendo '0', exige o
        // enum aberto ao lado. E a ordem dos membros deixa de ser dado de schema.
        CancellationToken ct = TestContext.Current.CancellationToken;
        TenantSlug slug = TenantSlug.Create($"enum-{Guid.NewGuid():N}"[..20]).Value;
        Tenant tenant = Tenant.Register("Enum", slug, new Plan(PlanTier.Enterprise, 500, 50));

        await using AppDbContext contexto = postgres.CriarContexto();
        contexto.Tenants.Add(tenant);
        await contexto.SaveChangesAsync(ct);

        List<string> status = await contexto.Database
            .SqlQuery<string>($"SELECT status AS \"Value\" FROM tenants WHERE id = {tenant.Id.Value}")
            .ToListAsync(ct);

        status.Should().ContainSingle().Which.Should().Be("Pending");
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter MapeamentoDeTenantTests`
Expected: FALHA de compilação — `AppDbContext.Tenants` não existe.

- [ ] **Step 3: Criar a configuração**

```csharp
using IdentityGateway.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IdentityGateway.Infrastructure.Persistence.Configurations;

/// <summary>
/// Mapeia o agregado <see cref="Tenant"/> na tabela <c>tenants</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Não há construtor sem parâmetro no agregado, e não é preciso.</b> O EF Core materializa pelo construtor
/// parametrizado, casando os parâmetros <c>id</c>, <c>name</c>, <c>slug</c> e <c>plan</c> com as propriedades
/// de mesmo nome. O que fica de fora dele — <c>Status</c>, <c>ExternalOrganizationId</c>, <c>OccupiedSeats</c>
/// e <c>OverSubscribed</c> — é escrito no campo de apoio, como já aconteceria por causa do <c>private set</c>.
/// A alternativa seria um <c>private Tenant()</c> com três <c>null!</c>: um agregado momentaneamente inválido
/// para agradar o ORM.
/// </para>
/// </remarks>
internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("tenants");

        builder.HasKey(tenant => tenant.Id);

        builder.Property(tenant => tenant.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, valor => new TenantId(valor))
            .ValueGeneratedNever();

        builder.Property(tenant => tenant.Name)
            .HasColumnName("name")
            .HasMaxLength(200)
            .IsRequired();

        // Conversor e não owned type: value object de campo único, e o owned criaria um tipo aninhado no
        // modelo sem ganho nenhum. A volta usa Create(...).Value porque o dado gravado já foi validado na
        // escrita — falhar aqui seria corrupção, e deve estourar.
        builder.Property(tenant => tenant.Slug)
            .HasColumnName("slug")
            .HasMaxLength(63)
            .HasConversion(slug => slug.Value, valor => TenantSlug.Create(valor).Value)
            .IsRequired();

        // Índice único e global, sem filtro: Tenant não é ISoftDeletable, então não há linha excluída a
        // excluir. Global porque o slug vira o alias da Organization no Keycloak e potencialmente subdomínio —
        // o espaço de nomes é do sistema inteiro. É ele que fecha a janela entre o SELECT de unicidade do
        // handler e o INSERT.
        builder.HasIndex(tenant => tenant.Slug)
            .IsUnique()
            .HasDatabaseName("ix_tenants_slug");

        // Owned achatado na mesma tabela: a §6.1 define Plan como value object, e achatá-lo à mão exigiria
        // propriedades espelho no agregado só para o ORM.
        builder.OwnsOne(tenant => tenant.Plan, plano =>
        {
            plano.Property(valor => valor.Tier)
                .HasColumnName("plan_tier")
                .HasMaxLength(20)
                .HasConversion<string>()
                .IsRequired();

            plano.Property(valor => valor.MaxUsers)
                .HasColumnName("plan_max_users")
                .IsRequired();

            plano.Property(valor => valor.MaxClients)
                .HasColumnName("plan_max_clients")
                .IsRequired();
        });

        builder.Navigation(tenant => tenant.Plan).IsRequired();

        builder.Property(tenant => tenant.Status)
            .HasColumnName("status")
            .HasMaxLength(20)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(tenant => tenant.ExternalOrganizationId)
            .HasColumnName("external_organization_id");

        builder.Property(tenant => tenant.OccupiedSeats)
            .HasColumnName("occupied_seats")
            .IsRequired();

        builder.Property(tenant => tenant.OverSubscribed)
            .HasColumnName("over_subscribed")
            .IsRequired();

        // Concorrência otimista por xmin, que a §6.1 exige para OccupiedSeats. Propriedade de sombra porque
        // xmin é coluna de sistema do PostgreSQL: o domínio não deve carregar um campo de versão que só o ORM
        // entende. Não gera coluna na migration — gera o WHERE xmin = @original no UPDATE.
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .IsRowVersion()
            .ValueGeneratedOnAddOrUpdate();

        // Os domain events são levantados pelo agregado e coletados pelo DomainEventInterceptor; não são
        // estado persistido.
        builder.Ignore(tenant => tenant.DomainEvents);
    }
}
```

- [ ] **Step 4: Expor o `DbSet` no contexto**

Em `src/IdentityGateway.Infrastructure/Persistence/AppDbContext.cs`, acrescentar antes do `DbSet` de outbox:

```csharp
    /// <summary>
    /// Tenants registrados.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> como os demais: quem consulta são os repositórios, que vivem neste assembly. Público,
    /// convidaria a Api a consultar direto e a fronteira deixaria de existir.
    /// </remarks>
    internal DbSet<Domain.Tenants.Tenant> Tenants => Set<Domain.Tenants.Tenant>();
```

Nada a fazer quanto à visibilidade: `IdentityGateway.Infrastructure.csproj` já declara `InternalsVisibleTo` para `IdentityGateway.Infrastructure.IntegrationTests` (linha 43) e para `IdentityGateway.Api.FunctionalTests` (linha 50).

- [ ] **Step 5: Gerar a migration**

Run:
```bash
dotnet ef migrations add CriacaoDeTenants \
  --project src/IdentityGateway.Infrastructure \
  --startup-project src/IdentityGateway.Api \
  --output-dir Persistence/Migrations
```
Expected: cria `*_CriacaoDeTenants.cs`. **Abra o arquivo e confira**: deve conter `CreateTable("tenants")` com as 10 colunas e `CreateIndex("ix_tenants_slug", unique: true)`. **Não deve** haver coluna `xmin` — se houver, o `IsRowVersion` foi mal configurado.

- [ ] **Step 6: Rodar e ver passar**

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter MapeamentoDeTenantTests`
Expected: PASS, 2 testes.

- [ ] **Step 7: Commit**

```bash
git add src/IdentityGateway.Infrastructure/ tests/
git commit -m "feat: mapeamento EF do tenant e migration

O agregado NAO ganha construtor sem parametro: o EF materializa pelo
parametrizado, casando por nome. O teste de round-trip cobre o risco de
esse casamento se quebrar em runtime."
```

---

### Task 5: Schema e atomicidade

**Files:**
- Test: `tests/IdentityGateway.Infrastructure.IntegrationTests/Persistence/SchemaDeTenantsTests.cs`
- Test: `tests/IdentityGateway.Infrastructure.IntegrationTests/Persistence/AtomicidadeDoRegistroTests.cs`

**Interfaces:**
- Consumes: `AppDbContext.Tenants`, `AppDbContext.OutboxMessages` (Task 4).
- Produces: nada — é tarefa só de teste.

- [ ] **Step 1: Escrever o teste de schema**

```csharp
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IdentityGateway.Infrastructure.IntegrationTests.Persistence;

/// <summary>
/// Prova que a migration cria a tabela e o índice único do slug.
/// </summary>
/// <remarks>
/// Espelha <c>SchemaDoOutboxTests</c> e existe pelo mesmo motivo: sem migration aplicada, <c>MigrateAsync</c> é
/// um no-op silencioso e a suíte passaria sobre um banco sem as tabelas que o código usa.
/// </remarks>
public sealed class SchemaDeTenantsTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task AMigration_CriaATabelaDeTenants()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using AppDbContext contexto = postgres.CriarContexto();

        int total = await contexto.Tenants.CountAsync(ct);

        total.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task OIndiceUnicoDoSlug_RecusaDuplicata()
    {
        // É esta constraint que fecha a janela entre o SELECT de unicidade do handler e o INSERT: duas
        // requisicoes concorrentes com o mesmo slug passariam as duas pela checagem em memoria.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string valor = $"dup-{Guid.NewGuid():N}"[..18];
        TenantSlug slug = TenantSlug.Create(valor).Value;

        await using (AppDbContext primeiro = postgres.CriarContexto())
        {
            primeiro.Tenants.Add(Tenant.Register("Primeiro", slug, new Plan(PlanTier.Free, 5, 1)));
            await primeiro.SaveChangesAsync(ct);
        }

        await using AppDbContext segundo = postgres.CriarContexto();
        segundo.Tenants.Add(Tenant.Register("Segundo", slug, new Plan(PlanTier.Free, 5, 1)));

        Func<Task> gravar = async () => await segundo.SaveChangesAsync(ct);

        await gravar.Should().ThrowAsync<DbUpdateException>();
    }
}
```

- [ ] **Step 2: Escrever o teste de atomicidade**

```csharp
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IdentityGateway.Infrastructure.IntegrationTests.Persistence;

/// <summary>
/// Prova que o tenant e a sua mensagem de Outbox gravam juntos, ou nenhum dos dois.
/// </summary>
/// <remarks>
/// <para>
/// <b>Não existe <c>BeginTransaction</c> no código, e é de propósito.</b> O <c>TransactionBehavior</c> chama
/// <c>SaveChangesAsync</c> uma vez, e o EF Core envolve um <c>SaveChanges</c> numa transação implícita; o
/// <c>DomainEventInterceptor</c> roda em <c>SavingChanges</c>, acrescentando os <c>OutboxMessage</c> ao change
/// tracker antes de o comando ir ao banco. Os dois <c>INSERT</c> saem na mesma unidade.
/// </para>
/// <para>
/// A propriedade observável, então, não é "existe uma transação" — é que os dois gravam juntos. O teste força a
/// falha por violação do índice único e confere que <b>nenhuma</b> mensagem sobrou do tenant recusado. Sem
/// atomicidade, a mensagem teria sido gravada e o contador daria 2 — é o par de estados que dá valor ao teste.
/// </para>
/// </remarks>
public sealed class AtomicidadeDoRegistroTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task OTenantEAMensagem_GravamJuntosOuNenhum()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string valor = $"atom-{Guid.NewGuid():N}"[..18];
        TenantSlug slug = TenantSlug.Create(valor).Value;

        await using (AppDbContext primeiro = postgres.CriarContexto())
        {
            primeiro.Tenants.Add(Tenant.Register("Primeiro", slug, new Plan(PlanTier.Free, 5, 1)));
            await primeiro.SaveChangesAsync(ct);
        }

        await using (AppDbContext segundo = postgres.CriarContexto())
        {
            segundo.Tenants.Add(Tenant.Register("Segundo", slug, new Plan(PlanTier.Free, 5, 1)));

            Func<Task> gravar = async () => await segundo.SaveChangesAsync(ct);

            await gravar.Should().ThrowAsync<DbUpdateException>();
        }

        // Uma mensagem, do tenant que de fato entrou — não duas.
        await using AppDbContext conferencia = postgres.CriarContexto();
        List<string> conteudos = await conferencia.OutboxMessages
            .Where(mensagem => mensagem.Type == "tenant-registered" && mensagem.Content.Contains(valor))
            .Select(mensagem => mensagem.Content)
            .ToListAsync(ct);

        conteudos.Should().ContainSingle();
    }

    [Fact]
    public async Task RegistroBemSucedido_GravaAMensagemComOSlugEmTexto()
    {
        // O payload carrega string, nunca value object: TenantRegistered com TenantSlug lancava
        // NotSupportedException ao voltar do Outbox, e toda mensagem iria a dead-letter.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string valor = $"ok-{Guid.NewGuid():N}"[..16];
        TenantSlug slug = TenantSlug.Create(valor).Value;

        await using AppDbContext contexto = postgres.CriarContexto();
        contexto.Tenants.Add(Tenant.Register("Ok", slug, new Plan(PlanTier.Free, 5, 1)));
        await contexto.SaveChangesAsync(ct);

        List<string> conteudos = await contexto.OutboxMessages
            .Where(mensagem => mensagem.Type == "tenant-registered" && mensagem.Content.Contains(valor))
            .Select(mensagem => mensagem.Content)
            .ToListAsync(ct);

        conteudos.Should().ContainSingle().Which.Should().Contain($"\"{valor}\"");
    }
}
```

- [ ] **Step 3: Rodar e ver passar**

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter "SchemaDeTenantsTests|AtomicidadeDoRegistroTests"`
Expected: PASS, 4 testes. Se `OTenantEAMensagem_GravamJuntosOuNenhum` achar 2 mensagens, a atomicidade está quebrada — investigue antes de seguir.

- [ ] **Step 4: Commit**

```bash
git add tests/
git commit -m "test: schema de tenants e atomicidade do registro

A atomicidade e provada nos dois estados: o INSERT recusado pelo indice unico
nao pode deixar a mensagem do Outbox para tras."
```

---

### Task 6: `TenantRepository`

**Files:**
- Create: `src/IdentityGateway.Infrastructure/Persistence/Repositories/TenantRepository.cs`
- Modify: `src/IdentityGateway.Infrastructure/DependencyInjection.cs`
- Test: `tests/IdentityGateway.Infrastructure.IntegrationTests/Persistence/TenantRepositoryTests.cs`

**Interfaces:**
- Consumes: `ITenantRepository` (Task 1), `AppDbContext.Tenants` (Task 4).
- Produces: `TenantRepository : ITenantRepository`, registrado como `Scoped`.

- [ ] **Step 1: Escrever o teste que falha**

```csharp
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.Persistence;
using IdentityGateway.Infrastructure.Persistence.Repositories;

namespace IdentityGateway.Infrastructure.IntegrationTests.Persistence;

/// <summary>
/// Cobre a consulta de unicidade do slug contra o PostgreSQL.
/// </summary>
/// <remarks>
/// Contra o banco real e não com dublê: o <c>SlugExistsAsync</c> compara um value object convertido, e uma
/// comparação mal mapeada viraria avaliação client-side — que num dublê passaria despercebida e em produção
/// traria a tabela inteira para a memória.
/// </remarks>
public sealed class TenantRepositoryTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task SlugExistsAsync_VerdadeiroQuandoJaGravado()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        TenantSlug slug = TenantSlug.Create($"rep-{Guid.NewGuid():N}"[..18]).Value;

        await using AppDbContext contexto = postgres.CriarContexto();
        ITenantRepository repositorio = new TenantRepository(contexto);

        repositorio.Add(Tenant.Register("Repo", slug, new Plan(PlanTier.Free, 5, 1)));
        await contexto.SaveChangesAsync(ct);

        bool existe = await repositorio.SlugExistsAsync(slug, ct);

        existe.Should().BeTrue();
    }

    [Fact]
    public async Task SlugExistsAsync_FalsoQuandoNaoExiste()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        TenantSlug inexistente = TenantSlug.Create($"nao-{Guid.NewGuid():N}"[..18]).Value;

        await using AppDbContext contexto = postgres.CriarContexto();
        ITenantRepository repositorio = new TenantRepository(contexto);

        bool existe = await repositorio.SlugExistsAsync(inexistente, ct);

        existe.Should().BeFalse();
    }

    [Fact]
    public async Task Add_NaoGravaSozinho()
    {
        // O commit e do TransactionBehavior. Um repositorio que salvasse sozinho tiraria o INSERT e a mensagem
        // do Outbox do mesmo SaveChanges.
        CancellationToken ct = TestContext.Current.CancellationToken;
        TenantSlug slug = TenantSlug.Create($"sem-{Guid.NewGuid():N}"[..18]).Value;

        await using AppDbContext contexto = postgres.CriarContexto();
        ITenantRepository repositorio = new TenantRepository(contexto);

        repositorio.Add(Tenant.Register("Sem commit", slug, new Plan(PlanTier.Free, 5, 1)));

        await using AppDbContext outro = postgres.CriarContexto();
        ITenantRepository leitura = new TenantRepository(outro);

        bool existe = await leitura.SlugExistsAsync(slug, ct);

        existe.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter TenantRepositoryTests`
Expected: FALHA de compilação — `TenantRepository` não existe.

- [ ] **Step 3: Escrever o repositório**

```csharp
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace IdentityGateway.Infrastructure.Persistence.Repositories;

/// <summary>
/// Implementa <see cref="ITenantRepository"/> sobre o <see cref="AppDbContext"/>.
/// </summary>
internal sealed class TenantRepository(AppDbContext context) : ITenantRepository
{
    /// <inheritdoc />
    public void Add(Tenant tenant) => context.Tenants.Add(tenant);

    /// <inheritdoc />
    public Task<bool> SlugExistsAsync(TenantSlug slug, CancellationToken cancellationToken) =>
        context.Tenants.AnyAsync(tenant => tenant.Slug == slug, cancellationToken);
}
```

- [ ] **Step 4: Registrar no contêiner**

Em `src/IdentityGateway.Infrastructure/DependencyInjection.cs`, logo após a linha que registra `IUnitOfWork`:

```csharp
        services.AddScoped<ITenantRepository, TenantRepository>();
```

Acrescentar os `using` de `IdentityGateway.Application.Common.Abstractions` e `IdentityGateway.Infrastructure.Persistence.Repositories` se ainda não existirem.

- [ ] **Step 5: Rodar e ver passar**

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter TenantRepositoryTests`
Expected: PASS, 3 testes.

- [ ] **Step 6: Commit**

```bash
git add src/IdentityGateway.Infrastructure/ tests/
git commit -m "feat: repositorio de tenant"
```

---

### Task 7: `PlanCatalog` e a seção `Plans`

**Files:**
- Create: `src/IdentityGateway.Infrastructure/Configuration/PlanOptions.cs`
- Create: `src/IdentityGateway.Infrastructure/Configuration/PlanCatalog.cs`
- Modify: `src/IdentityGateway.Infrastructure/DependencyInjection.cs`
- Modify: `src/IdentityGateway.Api/appsettings.json`
- Test: `tests/IdentityGateway.Infrastructure.IntegrationTests/Configuration/PlanCatalogTests.cs`

**Interfaces:**
- Consumes: `IPlanCatalog` (Task 1), `Plan`, `PlanTier`.
- Produces: `PlanCatalog : IPlanCatalog` (Singleton); `PlanOptions` com `SectionName = "Plans"`.

- [ ] **Step 1: Escrever o teste que falha**

```csharp
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace IdentityGateway.Infrastructure.IntegrationTests.Configuration;

/// <summary>
/// Cobre a resolução de plano por código.
/// </summary>
/// <remarks>
/// O catálogo vem da configuração para que mudar um limite comercial seja editar o appsettings e reiniciar —
/// não uma migration nem um release de engenharia.
/// </remarks>
public sealed class PlanCatalogTests
{
    private static PlanCatalog Catalogo()
    {
        PlanOptions opcoes = new()
        {
            ["free"] = new PlanDefinition { Tier = PlanTier.Free, MaxUsers = 5, MaxClients = 1 },
            ["standard"] = new PlanDefinition { Tier = PlanTier.Standard, MaxUsers = 50, MaxClients = 5 },
        };

        return new PlanCatalog(Options.Create(opcoes));
    }

    [Fact]
    public void Find_ResolveOPlanoConhecido()
    {
        IPlanCatalog catalogo = Catalogo();

        Plan? plano = catalogo.Find("free");

        plano.Should().Be(new Plan(PlanTier.Free, 5, 1));
    }

    [Theory]
    [InlineData("FREE")]
    [InlineData("Free")]
    public void Find_IgnoraACaixa(string codigo)
    {
        // O codigo vem do corpo da requisicao: exigir a caixa exata transformaria 'Free' em 400 sem razao.
        IPlanCatalog catalogo = Catalogo();

        Plan? plano = catalogo.Find(codigo);

        plano.Should().Be(new Plan(PlanTier.Free, 5, 1));
    }

    [Fact]
    public void Find_DevolveNuloParaCodigoDesconhecido()
    {
        IPlanCatalog catalogo = Catalogo();

        Plan? plano = catalogo.Find("inexistente");

        plano.Should().BeNull();
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter PlanCatalogTests`
Expected: FALHA de compilação — `PlanOptions`, `PlanDefinition` e `PlanCatalog` não existem.

- [ ] **Step 3: Criar `PlanOptions`**

```csharp
using System.Diagnostics.CodeAnalysis;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Infrastructure.Configuration;

/// <summary>
/// Um plano do catálogo, como vem da configuração.
/// </summary>
public sealed class PlanDefinition
{
    /// <summary>Nível comercial.</summary>
    public PlanTier Tier { get; set; }

    /// <summary>Teto de vagas de membro.</summary>
    public int MaxUsers { get; set; }

    /// <summary>Teto de clients OIDC ativos.</summary>
    public int MaxClients { get; set; }
}

/// <summary>
/// O catálogo de planos, indexado pelo código.
/// </summary>
/// <remarks>
/// Herda de <c>Dictionary</c> porque a seção <c>Plans</c> do appsettings é um mapa de código para limites, e o
/// binder da configuração o preenche diretamente. Acrescentar um plano é editar o JSON.
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1010:Generic interface should also be implemented",
    Justification = "É um alvo de binding da configuração, não uma coleção de domínio.")]
public sealed class PlanOptions : Dictionary<string, PlanDefinition>
{
    /// <summary>Nome da seção no appsettings.</summary>
    public const string SectionName = "Plans";

    /// <summary>
    /// Compara códigos sem diferenciar caixa.
    /// </summary>
    /// <remarks>
    /// O código chega no corpo da requisição: exigir a caixa exata transformaria <c>"Free"</c> num <c>400</c>
    /// sem razão de negócio.
    /// </remarks>
    public PlanOptions()
        : base(StringComparer.OrdinalIgnoreCase)
    {
    }
}
```

> Se o analisador apontar outra regra no lugar de `CA1010`, ajuste o `SuppressMessage` para o id reportado — a justificativa permanece.

- [ ] **Step 4: Criar `PlanCatalog`**

```csharp
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Tenants;
using Microsoft.Extensions.Options;

namespace IdentityGateway.Infrastructure.Configuration;

/// <summary>
/// Catálogo de planos em memória, lido da configuração.
/// </summary>
/// <remarks>
/// <para>
/// Em memória e não em tabela: plano é dado de catálogo, não entidade — a §6.1 o trata como value object dentro
/// do <c>Tenant</c>, sem ciclo de vida nem histórico. Uma tabela acrescentaria entidade que nada no M0 pede.
/// </para>
/// <para>
/// <c>IOptions</c> e não <c>IOptionsMonitor</c>: o catálogo é lido na subida e validado ali. Recarga a quente
/// mudaria os limites no meio de uma requisição, e o ganho não paga a pergunta "qual plano estava valendo
/// quando este tenant foi registrado?".
/// </para>
/// </remarks>
internal sealed class PlanCatalog(IOptions<PlanOptions> opcoes) : IPlanCatalog
{
    private readonly PlanOptions _planos = opcoes.Value;

    /// <inheritdoc />
    public Plan? Find(string planCode)
    {
        if (string.IsNullOrWhiteSpace(planCode))
        {
            return null;
        }

        return _planos.TryGetValue(planCode, out PlanDefinition? definicao)
            ? new Plan(definicao.Tier, definicao.MaxUsers, definicao.MaxClients)
            : null;
    }
}
```

- [ ] **Step 5: Registrar e configurar**

Em `src/IdentityGateway.Infrastructure/DependencyInjection.cs`, junto aos demais `AddOptions`:

```csharp
        // Catálogo inválido derruba a aplicação na subida, não na primeira requisição.
        services.AddOptions<PlanOptions>()
            .Bind(configuration.GetSection(PlanOptions.SectionName))
            .ValidateOnStart();

        // Singleton: é configuração imutável, e uma instância por requisição só produziria lixo.
        services.AddSingleton<IPlanCatalog, PlanCatalog>();
```

Em `src/IdentityGateway.Api/appsettings.json`, acrescentar no objeto raiz:

```json
  "Plans": {
    "free":       { "tier": "Free",       "maxUsers": 5,   "maxClients": 1 },
    "standard":   { "tier": "Standard",   "maxUsers": 50,  "maxClients": 5 },
    "enterprise": { "tier": "Enterprise", "maxUsers": 500, "maxClients": 50 }
  }
```

- [ ] **Step 6: Rodar e ver passar**

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter PlanCatalogTests`
Expected: PASS, 4 testes.

- [ ] **Step 7: Rodar `DependencyInjectionTests`**

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter DependencyInjectionTests`
Expected: PASS — prova que o contêiner resolve tudo o que foi registrado.

- [ ] **Step 8: Commit**

```bash
git add src/IdentityGateway.Infrastructure/ src/IdentityGateway.Api/appsettings.json tests/
git commit -m "feat: catalogo de planos lido da configuracao

Mudar limite comercial passa a ser editar o appsettings e reiniciar — sem
deploy nem migration."
```

---

### Task 8: Policy `PlatformAdmin` e a sobrecarga de papéis no `JwtTokenService`

**Files:**
- Modify: `src/IdentityGateway.Api/DependencyInjection.cs` (método `AddAutenticacao`)
- Modify: `src/IdentityGateway.Api/Security/JwtTokenService.cs`
- Modify: `tests/IdentityGateway.Api.FunctionalTests/IdentityGatewayApiFactory.cs`

**Interfaces:**
- Produces: policy nomeada `"PlatformAdmin"`; `JwtTokenService.Emitir(Guid, string, params string[] roles) → string`; `IdentityGatewayApiFactory.CreateClientAutenticado(Guid?, params string[] roles) → HttpClient`.

- [ ] **Step 1: Acrescentar os papéis ao `JwtTokenService`**

Em `src/IdentityGateway.Api/Security/JwtTokenService.cs`, substituir o método `Emitir` inteiro (linhas 44-70) por este. A assinatura antiga `Emitir(Guid, string)` não desaparece — `params` a cobre, e as chamadas existentes continuam compilando sem alteração:

```csharp
    public string Emitir(Guid usuarioId, string nome, params string[] roles)
    {
        DateTime agora = clock.UtcNow.UtcDateTime;

        List<Claim> claims =
        [
            new(JwtRegisteredClaimNames.Sub, usuarioId.ToString()),
            new(JwtRegisteredClaimNames.Name, nome),

            // Identificador único do token. Não é usado hoje, e é o que uma lista de revogação precisaria para
            // invalidar um token específico antes de ele expirar.
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
        ];

        // Um claim por papel, e não um único claim com lista separada por vírgula: é assim que o
        // RequireClaim da policy compara.
        foreach (string role in roles)
        {
            claims.Add(new Claim("roles", role));
        }

        SymmetricSecurityKey chave = new(Encoding.UTF8.GetBytes(_options.SigningKey));

        JwtSecurityToken token = new(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: agora,
            expires: agora.AddMinutes(_options.ExpirationMinutes),
            signingCredentials: new SigningCredentials(chave, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
```

E acrescentar ao XML doc existente do método, antes do `</remarks>`:

```csharp
    /// <para>
    /// <b>O claim de papel é <c>roles</c> plano, não aninhado.</b> A §12.1 documenta que <c>RequireRole</c>
    /// falha com o Keycloak porque o papel chega dentro de <c>realm_access.roles</c> — e chama isso de "o ponto
    /// que mais gera erro nessa integração". A policy exige o claim plano, e é ele que este método emite: o
    /// token de teste tem a mesma forma que o do Keycloak terá, com o client scope configurado.
    /// </para>
```

- [ ] **Step 2: Declarar a policy**

Em `src/IdentityGateway.Api/DependencyInjection.cs`, substituir `services.AddAuthorization();` por:

```csharp
        // Claim plano, não RequireRole: a §12.1 documenta que RequireRole falha com o Keycloak, porque o papel
        // chega aninhado em realm_access.roles. RequireRole passaria hoje, com o JwtTokenService dos testes, e
        // quebraria quando o Keycloak entrasse — o pior momento para descobrir.
        services.AddAuthorization(options =>
            options.AddPolicy("PlatformAdmin", policy =>
                policy.RequireClaim("roles", "platform-admin")));
```

- [ ] **Step 3: Estender a factory de teste**

Em `tests/IdentityGateway.Api.FunctionalTests/IdentityGatewayApiFactory.cs`, alterar a assinatura de `CreateClientAutenticado` para:

```csharp
    public HttpClient CreateClientAutenticado(Guid? usuarioId = null, params string[] roles)
```

e a linha de emissão para:

```csharp
        string token = emissor.Emitir(usuarioId ?? Guid.CreateVersion7(), "teste", roles);
```

- [ ] **Step 4: Compilar**

Run: `dotnet build`
Expected: 0 erros, 0 warnings.

- [ ] **Step 5: Rodar a suíte funcional**

Run: `dotnet test tests/IdentityGateway.Api.FunctionalTests`
Expected: PASS — os dois skips continuam pulados; os demais continuam verdes.

- [ ] **Step 6: Commit**

```bash
git add src/IdentityGateway.Api/ tests/IdentityGateway.Api.FunctionalTests/
git commit -m "feat: policy PlatformAdmin com claim plano

O JwtTokenService nao emitia claim de papel — sem a sobrecarga nao haveria como
produzir um token platform-admin para o teste do caminho feliz."
```

---

### Task 9: `ParaAccepted` no tradutor de resultado

**Files:**
- Modify: `src/IdentityGateway.Api/Extensions/ResultExtensions.cs`

**Interfaces:**
- Produces: `ResultExtensions.ParaAccepted<TValue>(Result<TValue>, Func<TValue, string>, Func<TValue, object>, string) → IResult`.

- [ ] **Step 1: Acrescentar o método**

Logo após `ParaCreated`, em `src/IdentityGateway.Api/Extensions/ResultExtensions.cs`:

```csharp
    /// <summary>
    /// Converte um resultado com valor em <c>202 Accepted</c> ou na resposta de erro correspondente.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>202</c> e não <c>201</c>:</b> o recurso foi aceito, não concluído. Num registro de tenant, o
    /// Keycloak só será chamado pelo consumidor do Outbox — responder <c>201</c> afirmaria um recurso pronto
    /// que ainda não existe do outro lado.
    /// </para>
    /// <para>
    /// O <c>Location</c> aponta para onde acompanhar o processamento, não para o recurso criado.
    /// </para>
    /// </remarks>
    /// <param name="resultado">O resultado do caso de uso.</param>
    /// <param name="localizacao">Onde acompanhar o processamento.</param>
    /// <param name="corpo">O que devolver no corpo da resposta.</param>
    /// <param name="correlationId">Identificador da requisição, incluído em toda resposta de erro.</param>
    public static IResult ParaAccepted<TValue>(
        this Result<TValue> resultado,
        Func<TValue, string> localizacao,
        Func<TValue, object> corpo,
        string correlationId)
    {
        ArgumentNullException.ThrowIfNull(resultado);
        ArgumentNullException.ThrowIfNull(localizacao);
        ArgumentNullException.ThrowIfNull(corpo);

        return resultado.Match(
            onSuccess: valor => Results.Accepted(localizacao(valor), corpo(valor)),
            onFailure: erro => ParaProblem(erro, correlationId));
    }
```

- [ ] **Step 2: Compilar**

Run: `dotnet build`
Expected: 0 erros, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/IdentityGateway.Api/Extensions/ResultExtensions.cs
git commit -m "feat: traducao de Result em 202 Accepted"
```

---

### Task 10: Módulo Carter `POST /api/v1/tenants`

**Files:**
- Create: `src/IdentityGateway.Api/Modules/RegisterTenantRequest.cs`
- Create: `src/IdentityGateway.Api/Modules/TenantAcceptedResponse.cs`
- Create: `src/IdentityGateway.Api/Modules/TenantsModule.cs`
- Test: `tests/IdentityGateway.Api.FunctionalTests/RegistroDeTenantTests.cs`

**Interfaces:**
- Consumes: `RegisterTenantCommand` (Task 2), `ParaAccepted` (Task 9), policy `"PlatformAdmin"` (Task 8), `ICorrelationIdProvider`.
- Produces: rota `POST /api/v1/tenants`.

- [ ] **Step 1: Escrever o teste funcional que falha**

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IdentityGateway.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// Exercita <c>POST /api/v1/tenants</c> por HTTP, do JSON até a linha no banco.
/// </summary>
/// <remarks>
/// Entra pela porta da frente e atravessa tudo — binding, validação, pipeline, persistência e tradução de erro
/// —, que é o que distingue o teste funcional dos das camadas de baixo.
/// </remarks>
public sealed class RegistroDeTenantTests(IdentityGatewayApiFactory factory)
    : IClassFixture<IdentityGatewayApiFactory>
{
    private const string Rota = "/api/v1/tenants";

    private static object Corpo(string slug, string plano = "free") => new
    {
        name = "Acme Corp",
        slug,
        planCode = plano,
        initialAdminEmail = "admin@acme.com",
    };

    private static string SlugUnico() => $"acme-{Guid.NewGuid():N}"[..20];

    [Fact]
    public async Task ComandoValido_Responde202ComLocationECorpo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");
        string slug = SlugUnico();

        HttpResponseMessage resposta = await client.PostAsJsonAsync(Rota, Corpo(slug), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Accepted);

        JsonElement json = await resposta.Content.ReadFromJsonAsync<JsonElement>(ct);
        string id = json.GetProperty("tenantId").GetString()!;
        json.GetProperty("status").GetString().Should().Be("Pending");

        resposta.Headers.Location!.ToString()
            .Should().Be($"/api/v1/tenants/{id}/provisioning");
    }

    [Fact]
    public async Task ComandoValido_GravaOTenantEAMensagemDoOutbox()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");
        string slug = SlugUnico();

        await client.PostAsJsonAsync(Rota, Corpo(slug), ct);

        await factory.ComEscopoAsync(async contexto =>
        {
            bool gravado = await contexto.Tenants.AnyAsync(tenant => tenant.Slug.Value == slug, ct);
            gravado.Should().BeTrue();

            bool mensagem = await contexto.OutboxMessages
                .AnyAsync(m => m.Type == "tenant-registered" && m.Content.Contains(slug), ct);
            mensagem.Should().BeTrue();
        });
    }

    [Fact]
    public async Task SemToken_Responde401()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage resposta = await client.PostAsJsonAsync(Rota, Corpo(SlugUnico()), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TokenSemOPapel_Responde403()
    {
        // O teste que prova que a policy esta ligada. Sem ele, remover o RequireAuthorization do endpoint nao
        // quebraria teste nenhum: o cliente autenticado passaria igual.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado();

        HttpResponseMessage resposta = await client.PostAsJsonAsync(Rota, Corpo(SlugUnico()), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SlugMalFormado_Responde400()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");

        HttpResponseMessage resposta = await client.PostAsJsonAsync(Rota, Corpo("-invalido-"), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SlugDuplicado_Responde409()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");
        string slug = SlugUnico();

        await client.PostAsJsonAsync(Rota, Corpo(slug), ct);
        HttpResponseMessage segunda = await client.PostAsJsonAsync(Rota, Corpo(slug), ct);

        segunda.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PlanoDesconhecido_Responde400()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");

        HttpResponseMessage resposta = await client.PostAsJsonAsync(
            Rota, Corpo(SlugUnico(), plano: "inexistente"), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test tests/IdentityGateway.Api.FunctionalTests --filter RegistroDeTenantTests`
Expected: FALHA — as requisições respondem `404`, porque nenhuma rota está registrada.

- [ ] **Step 3: Criar os contratos**

`RegisterTenantRequest.cs`:

```csharp
namespace IdentityGateway.Api.Modules;

/// <summary>
/// Corpo do <c>POST /api/v1/tenants</c>.
/// </summary>
/// <remarks>
/// Espelha <c>RegisterTenantCommand</c>, e a duplicação é deliberada: contrato de fio e contrato interno mudam
/// por razões diferentes. Colapsar os dois faria renomear um campo do command quebrar clientes HTTP.
/// </remarks>
/// <param name="Name">Nome de exibição.</param>
/// <param name="Slug">Identificador legível, único e imutável.</param>
/// <param name="PlanCode">Código do plano no catálogo.</param>
/// <param name="InitialAdminEmail">
/// E-mail do primeiro administrador. Obrigatório: sem ele o tenant nasce trancado, porque convidar membros
/// exige um <c>tenant-admin</c> que ainda não existiria (§9.1).
/// </param>
public sealed record RegisterTenantRequest(
    string Name,
    string Slug,
    string PlanCode,
    string InitialAdminEmail);
```

`TenantAcceptedResponse.cs`:

```csharp
namespace IdentityGateway.Api.Modules;

/// <summary>
/// Corpo do <c>202</c> de registro aceito.
/// </summary>
/// <remarks>
/// Carrega o id porque um <c>202</c> de corpo vazio obrigaria o cliente a uma consulta só para descobri-lo. O
/// <c>Status</c> é sempre <c>Pending</c> nesta fatia — o tenant ainda não foi provisionado no Keycloak.
/// </remarks>
/// <param name="TenantId">Identidade do tenant registrado.</param>
/// <param name="Status">Estado no ciclo de vida.</param>
public sealed record TenantAcceptedResponse(Guid TenantId, string Status);
```

- [ ] **Step 4: Criar o módulo**

```csharp
using Carter;
using IdentityGateway.Api.Extensions;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Tenants.RegisterTenant;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;
using Mediator;

namespace IdentityGateway.Api.Modules;

/// <summary>
/// Endpoints de tenant.
/// </summary>
/// <remarks>
/// <para>
/// <b>A rota traz <c>/api/v1</c> literal.</b> A §8 determina que o roteamento use <c>Asp.Versioning.Http</c>,
/// e o pacote fica de fora enquanto houver uma versão só: a URL produzida é idêntica, e o que ele entrega — a
/// convivência entre <c>v1</c> e <c>v2</c>, com <c>Deprecation</c> e <c>Sunset</c> da RFC 8594 — ainda não tem
/// consumidor. A política da §8 permanece; adia-se o mecanismo.
/// </para>
/// </remarks>
internal sealed class TenantsModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/v1/tenants", RegistrarAsync)
            .RequireAuthorization("PlatformAdmin")
            .WithName("RegistrarTenant")
            .WithSummary("Registra um tenant novo, ainda por provisionar.")
            .Produces<TenantAcceptedResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    /// <remarks>
    /// Responde <c>202</c>, não <c>201</c>: o registro foi aceito e o provisionamento no Keycloak acontece
    /// depois, a partir da mensagem do Outbox. O <c>Location</c> aponta para o acompanhamento do processamento
    /// — rota que ainda não existe nesta fatia, e é limitação conhecida: num <c>202</c>, omitir o cabeçalho ou
    /// apontar para um recurso que minta sobre estar pronto seria pior.
    /// </remarks>
    private static async Task<IResult> RegistrarAsync(
        RegisterTenantRequest request,
        ISender sender,
        ICorrelationIdProvider correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        RegisterTenantCommand comando = new(
            request.Name,
            request.Slug,
            request.PlanCode,
            request.InitialAdminEmail);

        Result<TenantId> resultado = await sender.Send(comando, cancellationToken);

        return resultado.ParaAccepted(
            localizacao: id => $"/api/v1/tenants/{id.Value}/provisioning",
            corpo: id => new TenantAcceptedResponse(id.Value, nameof(TenantStatus.Pending)),
            correlationId: correlationId.CorrelationId);
    }
}
```

- [ ] **Step 5: Rodar e ver passar**

Run: `dotnet test tests/IdentityGateway.Api.FunctionalTests --filter RegistroDeTenantTests`
Expected: PASS, 7 testes.

- [ ] **Step 6: Commit**

```bash
git add src/IdentityGateway.Api/Modules/ tests/
git commit -m "feat: endpoint POST /api/v1/tenants

Responde 202: o registro e aceito e o provisionamento no Keycloak acontece
depois, a partir da mensagem do Outbox."
```

---

### Task 11: Reativar os skips de `SegurancaTests`

**Files:**
- Modify: `tests/IdentityGateway.Api.FunctionalTests/SegurancaTests.cs`

**Interfaces:**
- Consumes: a rota da Task 10.

- [ ] **Step 1: Trocar o verbo e reativar**

Em `tests/IdentityGateway.Api.FunctionalTests/SegurancaTests.cs`:

1. Remover o argumento `Skip` dos dois `[Fact(Skip = "...")]` (linhas 30 e 41), deixando `[Fact]`.
2. Em `SemToken_Retorna401` e `ComTokenInvalido_Retorna401`, trocar `await client.GetAsync(RotaProtegida, ct)` por:

```csharp
        HttpResponseMessage resposta = await client.PostAsync(RotaProtegida, content: null, ct);
```

3. Em `TodaResposta_TrazOsCabecalhosDeSeguranca`, trocar pela mesma chamada `PostAsync`.
4. Substituir o terceiro parágrafo do XML doc da classe — o que começa com `<b>Dois testes estão em Skip</b>` — por:

```csharp
/// <para>
/// <b>Os testes usam <c>POST</c>, e não <c>GET</c>.</b> A rota protegida que existe é o registro de tenant; a
/// autorização roda antes do model binding, então o <c>401</c> acontece sem o corpo importar. Com <c>GET</c>, o
/// roteamento responderia <c>404</c> antes de a autorização ser consultada, e o teste passaria a não provar
/// nada.
/// </para>
```

- [ ] **Step 2: Rodar a suíte funcional inteira**

Run: `dotnet test tests/IdentityGateway.Api.FunctionalTests`
Expected: PASS, **sem nenhum skip**.

- [ ] **Step 3: Commit**

```bash
git add tests/IdentityGateway.Api.FunctionalTests/SegurancaTests.cs
git commit -m "test: reativa os testes de 401 apontando para o endpoint que existe

Eles faziam GET numa rota que a fatia nao entrega; com POST, o 401 acontece
antes do binding e a protecao fica de fato provada."
```

---

### Task 12: Fechamento — suíte completa, documentação e PR

**Files:**
- Modify: `docs/superpowers/specs/2026-09-21-tenant-registro-vertical-design.md` (se algo divergiu)

- [ ] **Step 1: Rodar tudo**

Run: `dotnet build && dotnet test`
Expected: 0 erros, 0 warnings; **nenhum skip** na suíte inteira; todos os testes verdes.

Confirme a contagem de skips:

```bash
grep -rn "Skip = " tests/ --include="*.cs" | grep -v "/obj/"
```
Expected: nenhuma linha.

- [ ] **Step 2: Conferir a spec contra o que foi construído**

Releia `docs/superpowers/specs/2026-09-21-tenant-registro-vertical-design.md` e corrija qualquer ponto em que a implementação divergiu — nome de arquivo, assinatura, decisão revista. Documentação que envelhece é pior que documentação ausente.

- [ ] **Step 3: Commit da documentação, se houve mudança**

```bash
git add docs/
git commit -m "docs: alinha a spec ao que foi implementado"
```

- [ ] **Step 4: Abrir o PR**

```bash
git push -u origin feat/tenant-registro-vertical
```

Depois, com o `gh`, abrindo o PR com o template do repositório preenchido: **o que muda e por quê**, **como foi verificado** (a saída de `dotnet build` e `dotnet test`, e que os 4 skips foram reativados), **o custo desta mudança** (o `Location` apontando para rota que ainda não existe; o ADR do versionamento adiando `Asp.Versioning.Http`; a sobrecarga no `JwtTokenService`).

Sem nenhuma referência a IA, Claude ou Anthropic no título, no corpo ou nos commits.

O ADR do versionamento entra no corpo do PR e, se aprovado, vai para a seção de ADRs da especificação.

---

## Checklist final

- [ ] `dotnet build` — 0 erros, 0 warnings com `TreatWarningsAsErrors`
- [ ] `dotnet test` — verde, sem nenhum skip
- [ ] Os 4 skips reativados: 2 de mensageria, 2 de `401`
- [ ] `POST /api/v1/tenants` cobre `202`, `401`, `403`, `400` (slug e plano) e `409`
- [ ] A atomicidade provada nos dois estados
- [ ] Nenhuma dependência nova
- [ ] Documentação afetada atualizada no mesmo PR
