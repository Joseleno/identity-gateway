# Agregado `Tenant` — fatia de registro — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Escrever o primeiro agregado de domínio do IdentityGateway — o caminho de registro do `Tenant` — e com ele reativar duas regras de arquitetura hoje em `Skip`.

**Architecture:** Domínio puro, sem persistência nem camada de aplicação. O agregado herda `AggregateRoot<TenantId>` da fundação vinda do CleanStart e distingue erro de negócio (devolve `Result`) de erro de programação (lança `DomainInvariantViolation`). Dois domain events são levantados e registrados no mapa do Outbox.

**Tech Stack:** .NET 10 · C# · xUnit v3 · AwesomeAssertions · `TreatWarningsAsErrors` ligado

**Spec:** [`docs/superpowers/specs/2026-09-20-tenant-registro-design.md`](../specs/2026-09-20-tenant-registro-design.md)

## Global Constraints

- **`TreatWarningsAsErrors` está ligado** (`Directory.Build.props`). Aviso do analisador quebra o build — inclusive `IDE0161` (namespace com escopo de arquivo) e `CA1861`. Escreva `namespace X;` com ponto e vírgula, nunca com chaves.
- **`var` e tipo explícito convivem; o `dotnet build` é o árbitro.** O `.editorconfig` tem as três faces
  da regra (`var_when_type_is_apparent = true`, `var_for_built_in_types = false`, `var_elsewhere = false`),
  todas como `warning`. Na prática o IDE0007 só considera o tipo "aparente" quando o construtor aparece à
  direita — `new T(...)`, cast, `as`. **Retorno de factory method não conta**, por isso o código do
  template escreve `Result<Email> resultado = Email.Of(entrada);` com tipo explícito e compila limpo. Siga
  os blocos deste plano como estão e confirme com o build; se ele apontar IDE0007 ou IDE0008 em alguma
  linha, é essa linha que muda, não o padrão inteiro.
- **Comentário e documentação em português**, seguindo o tom do repositório: explique o *porquê*, não o *o quê*.
- **XML doc em todo membro público** — o repositório trata doc ausente como aviso.
- **Nomes de teste:** `Metodo_Cenario_Resultado` (ex.: `ReleaseSeat_SemVagaOcupada_Lanca`).
- **Asserções com AwesomeAssertions** (`.Should()...`), nunca `Assert.*`.
- **`Domain` não referencia EF Core, Application, Infrastructure nem Api** — há teste de arquitetura que reprova.
- Rodar teste: `dotnet test tests/IdentityGateway.Domain.UnitTests` (o `--filter` aceita `NomeDaClasse` ou `NomeDoMetodo`).

---

## File Structure

| Arquivo | Responsabilidade |
|---|---|
| `Domain/Common/DomainInvariantViolation.cs` | Exceção de invariante violada, para todos os agregados |
| `Domain/Tenants/TenantId.cs` | Identidade tipada (`readonly record struct` sobre `Guid`) |
| `Domain/Tenants/TenantSlug.cs` | Slug validado e normalizado |
| `Domain/Tenants/PlanTier.cs` | Enum do nível do plano |
| `Domain/Tenants/Plan.cs` | Value object com limites do plano |
| `Domain/Tenants/TenantStatus.cs` | Os 7 estados da §6.2 |
| `Domain/Tenants/TenantErrors.cs` | Catálogo de erros de negócio do tenant |
| `Domain/Tenants/Events/TenantRegistered.cs` | Evento de registro |
| `Domain/Tenants/Events/TenantActivated.cs` | Evento de ativação |
| `Domain/Tenants/Tenant.cs` | O agregado |
| `Infrastructure/Persistence/Outbox/OutboxEventTypes.cs` | Registrar os dois eventos no mapa |
| `ArchitectureTests/RegrasDeDominioTests.cs` | Remover `Skip` de duas regras |

---

### Task 1: `DomainInvariantViolation`

Exceção que a §11.1 usa e a fundação não tem. Sem ela nenhum agregado compila.

**Files:**
- Create: `src/IdentityGateway.Domain/Common/DomainInvariantViolation.cs`
- Test: `tests/IdentityGateway.Domain.UnitTests/Common/DomainInvariantViolationTests.cs`

**Interfaces:**
- Consumes: nada
- Produces: `public sealed class DomainInvariantViolation : Exception` com `DomainInvariantViolation(string message)`

- [ ] **Step 1: Write the failing test**

```csharp
using IdentityGateway.Domain.Common;

namespace IdentityGateway.Domain.UnitTests.Common;

public sealed class DomainInvariantViolationTests
{
    [Fact]
    public void ComMensagem_PreservaAMensagem()
    {
        DomainInvariantViolation excecao = new("Tenant X: liberação de vaga sem vaga ocupada.");

        excecao.Message.Should().Be("Tenant X: liberação de vaga sem vaga ocupada.");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/IdentityGateway.Domain.UnitTests --filter DomainInvariantViolationTests`
Expected: FAIL na compilação — `O nome de tipo ou namespace "DomainInvariantViolation" não foi encontrado`

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace IdentityGateway.Domain.Common;

/// <summary>
/// Invariante de domínio violada — erro de programação, não de negócio.
/// </summary>
/// <remarks>
/// <para>
/// A distinção importa: recusa de negócio ("não há vaga no plano") é resultado esperado e volta como
/// <see cref="Result"/>, para quem chamou decidir o que fazer. Esta exceção é o outro caso — o chamador
/// pediu algo que a regra não admite em nenhuma circunstância, como liberar uma vaga que não está ocupada.
/// Devolver <c>Result</c> aqui convidaria a tratar como fluxo alternativo o que é defeito.
/// </para>
/// <para>
/// Não tem construtor sem parâmetro nem sobrecarga com <c>innerException</c> de propósito: toda ocorrência
/// precisa dizer qual invariante caiu e em qual agregado, ou a mensagem não ajuda quem for depurar.
/// </para>
/// </remarks>
public sealed class DomainInvariantViolation : Exception
{
    /// <summary>
    /// Cria a exceção descrevendo a invariante violada.
    /// </summary>
    /// <param name="message">Qual invariante caiu, e em qual agregado.</param>
    public DomainInvariantViolation(string message)
        : base(message)
    {
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/IdentityGateway.Domain.UnitTests --filter DomainInvariantViolationTests`
Expected: PASS (1 teste)

- [ ] **Step 5: Commit**

```bash
git add src/IdentityGateway.Domain/Common/DomainInvariantViolation.cs tests/IdentityGateway.Domain.UnitTests/Common/DomainInvariantViolationTests.cs
git commit -m "feat: acrescenta DomainInvariantViolation ao dominio comum"
```

---

### Task 2: `TenantId`

**Files:**
- Create: `src/IdentityGateway.Domain/Tenants/TenantId.cs`
- Test: `tests/IdentityGateway.Domain.UnitTests/Tenants/TenantIdTests.cs`

**Interfaces:**
- Consumes: nada
- Produces: `public readonly record struct TenantId(Guid Value)` com `static TenantId New()`

- [ ] **Step 1: Write the failing test**

```csharp
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Domain.UnitTests.Tenants;

public sealed class TenantIdTests
{
    [Fact]
    public void New_GeraIdentidadesDistintas()
    {
        TenantId um = TenantId.New();
        TenantId outro = TenantId.New();

        um.Should().NotBe(outro);
    }

    [Fact]
    public void DoisIdsComOMesmoGuid_SaoIguais()
    {
        Guid guid = Guid.CreateVersion7();

        new TenantId(guid).Should().Be(new TenantId(guid));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/IdentityGateway.Domain.UnitTests --filter TenantIdTests`
Expected: FAIL na compilação — `TenantId` não encontrado

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace IdentityGateway.Domain.Tenants;

/// <summary>
/// Identidade de um tenant.
/// </summary>
/// <remarks>
/// <para>
/// <c>readonly record struct</c> e não classe: um id circula muito — chave de dicionário, item de lista,
/// comparação em laço — e alocar no heap a cada uso não se paga. O <c>record</c> dá igualdade por valor
/// sem escrevê-la à mão.
/// </para>
/// <para>
/// O tipo próprio existe para que <c>GetAsync(MemberId)</c> não aceite um <c>TenantId</c> por engano. Com
/// <c>Guid</c> cru os dois são o mesmo tipo, e a troca só aparece em produção, como "não encontrado".
/// </para>
/// </remarks>
/// <param name="Value">O identificador.</param>
public readonly record struct TenantId(Guid Value)
{
    /// <summary>
    /// Gera uma identidade nova.
    /// </summary>
    /// <remarks>
    /// Versão 7 e não 4: o v7 embute o instante de criação, então ids gerados em sequência são adjacentes
    /// no índice. Com v4 cada inserção cai num ponto aleatório da árvore B, o que fragmenta as páginas do
    /// PostgreSQL e piora conforme a tabela cresce.
    /// </remarks>
    public static TenantId New() => new(Guid.CreateVersion7());
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/IdentityGateway.Domain.UnitTests --filter TenantIdTests`
Expected: PASS (2 testes)

- [ ] **Step 5: Commit**

```bash
git add src/IdentityGateway.Domain/Tenants/TenantId.cs tests/IdentityGateway.Domain.UnitTests/Tenants/TenantIdTests.cs
git commit -m "feat: acrescenta a identidade tipada TenantId"
```

---

### Task 3: `TenantSlug`

**Files:**
- Create: `src/IdentityGateway.Domain/Tenants/TenantSlug.cs`
- Modify: `src/IdentityGateway.Domain/Errors/DomainErrors.cs` (acrescentar grupo `Tenant`)
- Test: `tests/IdentityGateway.Domain.UnitTests/Tenants/TenantSlugTests.cs`

**Interfaces:**
- Consumes: `Result<T>`, `ValueObject`, `Error` (fundação)
- Produces: `public sealed class TenantSlug : ValueObject` com `static Result<TenantSlug> Create(string value)` e `string Value { get; }`

- [ ] **Step 1: Write the failing test**

```csharp
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Domain.UnitTests.Tenants;

public sealed class TenantSlugTests
{
    [Theory]
    [InlineData("acme", "acme")]
    [InlineData("acme-corp", "acme-corp")]
    [InlineData("Acme-Corp", "acme-corp")]     // normaliza a caixa
    [InlineData("  acme  ", "acme")]           // apara o entorno
    [InlineData("a1b2c3", "a1b2c3")]
    public void Create_ComValorValido_NormalizaEAceita(string entrada, string esperado)
    {
        Result<TenantSlug> resultado = TenantSlug.Create(entrada);

        resultado.IsSuccess.Should().BeTrue();
        resultado.Value.Value.Should().Be(esperado);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ac")]                  // curto demais
    [InlineData("-acme")]               // hífen na ponta
    [InlineData("acme-")]               // hífen na ponta
    [InlineData("acme--corp")]          // hífen consecutivo
    [InlineData("acme_corp")]           // underscore não é válido em DNS
    [InlineData("acmé")]                // fora de [a-z0-9-]
    [InlineData("acme corp")]           // espaço interno
    public void Create_ComValorInvalido_Recusa(string entrada)
    {
        Result<TenantSlug> resultado = TenantSlug.Create(entrada);

        resultado.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_ComMaisDe63Caracteres_Recusa()
    {
        string longo = new('a', 64);

        TenantSlug.Create(longo).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_ComExatamente63Caracteres_Aceita()
    {
        string limite = new('a', 63);

        TenantSlug.Create(limite).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void DoisSlugsComOMesmoValor_SaoIguais()
    {
        TenantSlug um = TenantSlug.Create("acme").Value;
        TenantSlug outro = TenantSlug.Create("ACME").Value;

        um.Should().Be(outro);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/IdentityGateway.Domain.UnitTests --filter TenantSlugTests`
Expected: FAIL na compilação — `TenantSlug` não encontrado

- [ ] **Step 3: Acrescentar o grupo de erros**

Em `src/IdentityGateway.Domain/Errors/DomainErrors.cs`, acrescentar dentro da classe `DomainErrors`, depois do grupo `Email`:

```csharp
    /// <summary>Erros do value object <c>TenantSlug</c>.</summary>
    public static class TenantSlug
    {
        /// <summary>Slug fora do formato aceito.</summary>
        public static Error Invalido(string valor) => Error.Validation(
            "TenantSlug.Invalido",
            $"'{valor}' não é um slug válido: use de 3 a 63 caracteres entre letras minúsculas, dígitos e "
            + "hífen, sem hífen no início, no fim ou repetido.");
    }
```

- [ ] **Step 4: Write minimal implementation**

```csharp
using System.Text.RegularExpressions;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Errors;

namespace IdentityGateway.Domain.Tenants;

/// <summary>
/// Identificador legível do tenant, único e imutável.
/// </summary>
/// <remarks>
/// <para>
/// Vira o <i>alias</i> da Organization no Keycloak, e pode vir a ser subdomínio ou segmento de URL. Por
/// isso a regra é a interseção conservadora do que os três aceitam: minúsculas, dígitos e hífen, de 3 a 63
/// caracteres, sem hífen no início, no fim ou repetido.
/// </para>
/// <para>
/// <b>Conservadora de propósito.</b> Afrouxar a regra depois não quebra ninguém; apertá-la invalidaria
/// slugs já cadastrados. O limite de 63 é o maior rótulo DNS válido, e o underscore fica de fora porque
/// DNS não o aceita — ainda que o Keycloak aceitasse.
/// </para>
/// </remarks>
public sealed partial class TenantSlug : ValueObject
{
    private const int TamanhoMinimo = 3;
    private const int TamanhoMaximo = 63;

    private TenantSlug(string value) => Value = value;

    /// <summary>O slug, normalizado em minúsculas.</summary>
    public string Value { get; }

    /// <summary>
    /// Cria um slug a partir do texto informado.
    /// </summary>
    /// <remarks>
    /// Normaliza antes de validar — apara o entorno e baixa a caixa —, de modo que <c>"  Acme-Corp  "</c>
    /// e <c>"acme-corp"</c> produzam o mesmo slug. Sem isso, dois tenants poderiam coexistir diferindo
    /// apenas na caixa, e o alias no Keycloak colidiria.
    /// </remarks>
    public static Result<TenantSlug> Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result.Failure<TenantSlug>(DomainErrors.General.TextoObrigatorio(nameof(TenantSlug)));
        }

        string normalizado = value.Trim().ToLowerInvariant();

        if (normalizado.Length is < TamanhoMinimo or > TamanhoMaximo || !FormaValida().IsMatch(normalizado))
        {
            return Result.Failure<TenantSlug>(DomainErrors.TenantSlug.Invalido(value));
        }

        return new TenantSlug(normalizado);
    }

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <summary>O slug em texto.</summary>
    public override string ToString() => Value;

    // Começa e termina em letra ou dígito; no meio, hífen isolado é permitido e hífen duplo não.
    // Regex compilada em tempo de build pelo gerador: sem custo de interpretação a cada chamada.
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex FormaValida();
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/IdentityGateway.Domain.UnitTests --filter TenantSlugTests`
Expected: PASS (todos os casos das `Theory` mais os 4 `Fact`)

- [ ] **Step 6: Commit**

```bash
git add src/IdentityGateway.Domain/Tenants/TenantSlug.cs src/IdentityGateway.Domain/Errors/DomainErrors.cs tests/IdentityGateway.Domain.UnitTests/Tenants/TenantSlugTests.cs
git commit -m "feat: acrescenta o value object TenantSlug"
```

---

### Task 4: `PlanTier`, `Plan` e `TenantStatus`

Três tipos pequenos, um único ciclo: `Plan` não tem comportamento além de carregar limites, e o enum não tem o que testar isoladamente.

**Files:**
- Create: `src/IdentityGateway.Domain/Tenants/PlanTier.cs`
- Create: `src/IdentityGateway.Domain/Tenants/Plan.cs`
- Create: `src/IdentityGateway.Domain/Tenants/TenantStatus.cs`
- Test: `tests/IdentityGateway.Domain.UnitTests/Tenants/PlanTests.cs`

**Interfaces:**
- Consumes: `ValueObject`
- Produces:
  - `public enum PlanTier { Free, Standard, Enterprise }`
  - `public sealed class Plan : ValueObject` com `Plan(PlanTier tier, int maxUsers, int maxClients)`, `PlanTier Tier { get; }`, `int MaxUsers { get; }`, `int MaxClients { get; }`
  - `public enum TenantStatus { Pending, Active, Suspending, Suspended, Terminating, Terminated, ProvisioningFailed }`

- [ ] **Step 1: Write the failing test**

```csharp
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Domain.UnitTests.Tenants;

public sealed class PlanTests
{
    [Fact]
    public void GuardaOsLimitesInformados()
    {
        Plan plano = new(PlanTier.Standard, maxUsers: 50, maxClients: 5);

        plano.Tier.Should().Be(PlanTier.Standard);
        plano.MaxUsers.Should().Be(50);
        plano.MaxClients.Should().Be(5);
    }

    [Fact]
    public void DoisPlanosComOsMesmosLimites_SaoIguais()
    {
        Plan um = new(PlanTier.Free, maxUsers: 5, maxClients: 1);
        Plan outro = new(PlanTier.Free, maxUsers: 5, maxClients: 1);

        um.Should().Be(outro);
    }

    [Fact]
    public void PlanosDeTiersDiferentes_NaoSaoIguais()
    {
        Plan gratuito = new(PlanTier.Free, maxUsers: 5, maxClients: 1);
        Plan pago = new(PlanTier.Standard, maxUsers: 5, maxClients: 1);

        gratuito.Should().NotBe(pago);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/IdentityGateway.Domain.UnitTests --filter PlanTests`
Expected: FAIL na compilação — `Plan` e `PlanTier` não encontrados

- [ ] **Step 3: Write minimal implementation**

`PlanTier.cs`:

```csharp
namespace IdentityGateway.Domain.Tenants;

/// <summary>
/// Nível comercial do plano.
/// </summary>
/// <remarks>
/// O nível não determina os limites: quem os informa é o catálogo de planos, para que uma mudança
/// comercial não exija recompilar o domínio. O <c>Tier</c> serve para distinguir planos que compartilham
/// limites e para a leitura humana.
/// </remarks>
public enum PlanTier
{
    /// <summary>Plano gratuito.</summary>
    Free,

    /// <summary>Plano pago padrão.</summary>
    Standard,

    /// <summary>Plano negociado.</summary>
    Enterprise,
}
```

`Plan.cs`:

```csharp
using IdentityGateway.Domain.Common;

namespace IdentityGateway.Domain.Tenants;

/// <summary>
/// Limites contratados pelo tenant.
/// </summary>
/// <remarks>
/// <para>
/// Value object e não entidade: dois tenants no mesmo plano têm planos <i>iguais</i>, não o <i>mesmo</i>
/// plano. Trocar de plano é substituir o objeto inteiro, o que evita a pergunta "alterar este plano afeta
/// quem mais?".
/// </para>
/// <para>
/// Quem constrói é o catálogo de planos, a partir de um código (<c>PlanCode</c> na §11.4). Não há
/// validação dos limites aqui de propósito: um plano com limites absurdos é erro de catálogo, e validar
/// no value object esconderia isso de quem o mantém.
/// </para>
/// </remarks>
/// <param name="tier">Nível comercial.</param>
/// <param name="maxUsers">Teto de vagas de membro.</param>
/// <param name="maxClients">Teto de clients OIDC ativos.</param>
public sealed class Plan(PlanTier tier, int maxUsers, int maxClients) : ValueObject
{
    /// <summary>Nível comercial do plano.</summary>
    public PlanTier Tier { get; } = tier;

    /// <summary>Teto de vagas de membro.</summary>
    public int MaxUsers { get; } = maxUsers;

    /// <summary>Teto de clients OIDC ativos.</summary>
    public int MaxClients { get; } = maxClients;

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Tier;
        yield return MaxUsers;
        yield return MaxClients;
    }
}
```

`TenantStatus.cs`:

```csharp
namespace IdentityGateway.Domain.Tenants;

/// <summary>
/// Estados do ciclo de vida de um tenant.
/// </summary>
/// <remarks>
/// <para>
/// O enum declara os sete estados da máquina documentada na especificação, embora o agregado ainda só
/// implemente as transições do caminho de registro. Declarar todos evita que uma fatia posterior invente
/// um nome diferente para um estado que a especificação já nomeou.
/// </para>
/// <para>
/// <see cref="Suspending"/> e <see cref="Terminating"/> são estados de passagem: a API responde <c>202</c>
/// e o efeito no Keycloak acontece depois. Sem eles não haveria como responder "aceito, ainda
/// processando" numa consulta feita nessa janela.
/// </para>
/// </remarks>
public enum TenantStatus
{
    /// <summary>Registrado, aguardando provisionamento no Keycloak.</summary>
    Pending,

    /// <summary>Provisionado e em operação.</summary>
    Active,

    /// <summary>Suspensão aceita, ainda sendo aplicada.</summary>
    Suspending,

    /// <summary>Suspenso: não aceita operação de gestão.</summary>
    Suspended,

    /// <summary>Encerramento aceito, ainda sendo aplicado.</summary>
    Terminating,

    /// <summary>Encerrado. Estado terminal.</summary>
    Terminated,

    /// <summary>Provisionamento falhou depois de esgotados os retries; aguarda retry manual.</summary>
    ProvisioningFailed,
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/IdentityGateway.Domain.UnitTests --filter PlanTests`
Expected: PASS (3 testes)

- [ ] **Step 5: Commit**

```bash
git add src/IdentityGateway.Domain/Tenants/PlanTier.cs src/IdentityGateway.Domain/Tenants/Plan.cs src/IdentityGateway.Domain/Tenants/TenantStatus.cs tests/IdentityGateway.Domain.UnitTests/Tenants/PlanTests.cs
git commit -m "feat: acrescenta Plan, PlanTier e TenantStatus"
```

---

### Task 5: eventos e `TenantErrors`

Sem comportamento próprio; são insumo do agregado da Task 6. Um ciclo só, verificado pelo build.

**Files:**
- Create: `src/IdentityGateway.Domain/Tenants/Events/TenantRegistered.cs`
- Create: `src/IdentityGateway.Domain/Tenants/Events/TenantActivated.cs`
- Create: `src/IdentityGateway.Domain/Tenants/TenantErrors.cs`

**Interfaces:**
- Consumes: `IDomainEvent` (tem um só membro: `DateTimeOffset OccurredOn { get; }`), `Error`, `TenantId`, `TenantSlug`
- Produces:
  - `public sealed record TenantRegistered(TenantId TenantId, TenantSlug Slug) : IDomainEvent`
  - `public sealed record TenantActivated(TenantId TenantId) : IDomainEvent`
  - `public static class TenantErrors` com `NotActive(TenantId)`, `SeatLimitReached(int)`, `SlugInUse(TenantSlug)`, `UnknownPlan(string)`

- [ ] **Step 1: Escrever os dois eventos**

`Events/TenantRegistered.cs`:

```csharp
using IdentityGateway.Domain.Common;

namespace IdentityGateway.Domain.Tenants.Events;

/// <summary>
/// Um tenant foi registrado e aguarda provisionamento.
/// </summary>
/// <remarks>
/// É este evento que dispara o provisionamento: gravado no Outbox na mesma transação do <c>INSERT</c>, ele
/// garante que ou os dois acontecem ou nenhum. Registrar o tenant chamando o Keycloak direto deixaria um
/// órfão de cada lado sempre que o outro falhasse.
/// </remarks>
/// <param name="TenantId">Identidade do tenant registrado.</param>
/// <param name="Slug">Slug que virará o alias da Organization.</param>
public sealed record TenantRegistered(TenantId TenantId, TenantSlug Slug) : IDomainEvent
{
    /// <inheritdoc />
    public DateTimeOffset OccurredOn { get; } = DateTimeOffset.UtcNow;
}
```

`Events/TenantActivated.cs`:

```csharp
using IdentityGateway.Domain.Common;

namespace IdentityGateway.Domain.Tenants.Events;

/// <summary>
/// O provisionamento concluiu e o tenant entrou em operação.
/// </summary>
/// <param name="TenantId">Identidade do tenant ativado.</param>
public sealed record TenantActivated(TenantId TenantId) : IDomainEvent
{
    /// <inheritdoc />
    public DateTimeOffset OccurredOn { get; } = DateTimeOffset.UtcNow;
}
```

- [ ] **Step 2: Escrever `TenantErrors`**

```csharp
using IdentityGateway.Domain.Errors;

namespace IdentityGateway.Domain.Tenants;

/// <summary>
/// Erros de negócio do tenant.
/// </summary>
/// <remarks>
/// Fora do <c>DomainErrors</c> geral porque pertencem a um agregado só. O catálogo central guarda o que
/// atravessa agregados; misturar os dois faria o arquivo crescer com o domínio inteiro.
/// </remarks>
public static class TenantErrors
{
    /// <summary>Operação que exige tenant em operação, com o tenant em outro estado.</summary>
    public static Error NotActive(TenantId tenantId) => Error.Conflict(
        "Tenant.NaoAtivo",
        $"O tenant '{tenantId.Value}' não está ativo.");

    /// <summary>Todas as vagas do plano já estão ocupadas.</summary>
    public static Error SeatLimitReached(int maxUsers) => Error.Conflict(
        "Tenant.LimiteDeVagasAtingido",
        $"O plano não admite mais de {maxUsers} membros.");

    /// <summary>Já existe tenant com o slug informado.</summary>
    /// <remarks>
    /// <see cref="ErrorType.Conflict"/> e não <c>Validation</c>: o slug é bem formado, o que impede é o
    /// estado do sistema. A Api traduz em 409.
    /// </remarks>
    public static Error SlugInUse(TenantSlug slug) => Error.Conflict(
        "Tenant.SlugEmUso",
        $"O slug '{slug.Value}' já pertence a outro tenant.");

    /// <summary>O código de plano informado não existe no catálogo.</summary>
    public static Error UnknownPlan(string planCode) => Error.Validation(
        "Tenant.PlanoDesconhecido",
        $"Não existe plano com o código '{planCode}'.");
}
```

- [ ] **Step 3: Verificar que compila**

Run: `dotnet build IdentityGateway.slnx`
Expected: `Compilação com êxito`, 0 avisos, 0 erros

- [ ] **Step 4: Commit**

```bash
git add src/IdentityGateway.Domain/Tenants/Events src/IdentityGateway.Domain/Tenants/TenantErrors.cs
git commit -m "feat: acrescenta os eventos e o catalogo de erros do tenant"
```

---

### Task 6: o agregado `Tenant`

**Files:**
- Create: `src/IdentityGateway.Domain/Tenants/Tenant.cs`
- Test: `tests/IdentityGateway.Domain.UnitTests/Tenants/TenantTests.cs`

**Interfaces:**
- Consumes: `AggregateRoot<TenantId>` (construtor `protected AggregateRoot(TId id)`, método `protected void RaiseDomainEvent(IDomainEvent)`, propriedade `public IReadOnlyCollection<IDomainEvent> DomainEvents`), `TenantId`, `TenantSlug`, `Plan`, `TenantStatus`, `TenantErrors`, `DomainInvariantViolation`, `Result`
- Produces: `public sealed class Tenant : AggregateRoot<TenantId>` com `static Tenant Register(string, TenantSlug, Plan)`, `void MarkProvisioned(string)`, `void MarkProvisioningFailed()`, `Result ReserveSeat()`, `void ReleaseSeat()`, e as propriedades `Name`, `Slug`, `Plan`, `Status`, `ExternalOrganizationId`, `OccupiedSeats`, `OverSubscribed`

- [ ] **Step 1: Write the failing test**

```csharp
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Domain.Tenants.Events;

namespace IdentityGateway.Domain.UnitTests.Tenants;

public sealed class TenantTests
{
    private static Tenant TenantRegistrado(int maxUsers = 10) =>
        Tenant.Register("Acme", TenantSlug.Create("acme").Value, new Plan(PlanTier.Standard, maxUsers, 5));

    private static Tenant TenantAtivo(int maxUsers = 10)
    {
        Tenant tenant = TenantRegistrado(maxUsers);
        tenant.MarkProvisioned("org-externa-1");
        return tenant;
    }

    [Fact]
    public void Register_NasceEmPending()
    {
        Tenant tenant = TenantRegistrado();

        tenant.Status.Should().Be(TenantStatus.Pending);
        tenant.Name.Should().Be("Acme");
        tenant.Slug.Value.Should().Be("acme");
        tenant.OccupiedSeats.Should().Be(0);
        tenant.ExternalOrganizationId.Should().BeNull();
        tenant.OverSubscribed.Should().BeFalse();
    }

    [Fact]
    public void Register_LevantaTenantRegistered()
    {
        Tenant tenant = TenantRegistrado();

        tenant.DomainEvents.Should().ContainSingle()
            .Which.Should().BeOfType<TenantRegistered>()
            .Which.Slug.Value.Should().Be("acme");
    }

    [Fact]
    public void Register_GeraIdentidadesDistintas()
    {
        TenantRegistrado().Id.Should().NotBe(TenantRegistrado().Id);
    }

    [Fact]
    public void MarkProvisioned_AtivaEGuardaOIdExterno()
    {
        Tenant tenant = TenantRegistrado();

        tenant.MarkProvisioned("org-externa-1");

        tenant.Status.Should().Be(TenantStatus.Active);
        tenant.ExternalOrganizationId.Should().Be("org-externa-1");
        tenant.DomainEvents.OfType<TenantActivated>().Should().ContainSingle();
    }

    [Fact]
    public void MarkProvisioned_RepetidoComOMesmoId_NaoLevantaSegundoEvento()
    {
        // A mesma mensagem do Outbox pode ser entregue mais de uma vez: a segunda não pode duplicar efeito.
        Tenant tenant = TenantRegistrado();

        tenant.MarkProvisioned("org-externa-1");
        tenant.MarkProvisioned("org-externa-1");

        tenant.DomainEvents.OfType<TenantActivated>().Should().ContainSingle();
    }

    [Fact]
    public void MarkProvisioned_APartirDeProvisioningFailed_Ativa()
    {
        // É o retry manual: o provisionamento falhou, alguém reprocessou o evento, e agora deu certo.
        Tenant tenant = TenantRegistrado();
        tenant.MarkProvisioningFailed();

        tenant.MarkProvisioned("org-externa-1");

        tenant.Status.Should().Be(TenantStatus.Active);
    }

    [Fact]
    public void MarkProvisioned_ComOutroIdExternoEstandoAtivo_Lanca()
    {
        Tenant tenant = TenantAtivo();

        Action ativar = () => tenant.MarkProvisioned("org-externa-2");

        ativar.Should().Throw<DomainInvariantViolation>();
    }

    [Fact]
    public void MarkProvisioningFailed_APartirDePending_MarcaFalha()
    {
        Tenant tenant = TenantRegistrado();

        tenant.MarkProvisioningFailed();

        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
    }

    [Fact]
    public void MarkProvisioningFailed_Repetido_NaoLanca()
    {
        // O consumidor de Fault pode reentregar a mesma mensagem.
        Tenant tenant = TenantRegistrado();
        tenant.MarkProvisioningFailed();

        Action denovo = tenant.MarkProvisioningFailed;

        denovo.Should().NotThrow();
    }

    [Fact]
    public void MarkProvisioningFailed_ComTenantAtivo_Lanca()
    {
        Tenant tenant = TenantAtivo();

        Action falhar = tenant.MarkProvisioningFailed;

        falhar.Should().Throw<DomainInvariantViolation>();
    }

    [Fact]
    public void ReserveSeat_ComTenantAtivo_OcupaVaga()
    {
        Tenant tenant = TenantAtivo();

        Result resultado = tenant.ReserveSeat();

        resultado.IsSuccess.Should().BeTrue();
        tenant.OccupiedSeats.Should().Be(1);
    }

    [Fact]
    public void ReserveSeat_ComTenantNaoAtivo_Recusa()
    {
        Tenant tenant = TenantRegistrado();

        Result resultado = tenant.ReserveSeat();

        resultado.IsFailure.Should().BeTrue();
        resultado.Error.Code.Should().Be("Tenant.NaoAtivo");
        tenant.OccupiedSeats.Should().Be(0);
    }

    [Fact]
    public void ReserveSeat_NoLimiteDoPlano_RecusaSemUltrapassar()
    {
        Tenant tenant = TenantAtivo(maxUsers: 2);
        tenant.ReserveSeat();
        tenant.ReserveSeat();

        Result resultado = tenant.ReserveSeat();

        resultado.IsFailure.Should().BeTrue();
        resultado.Error.Code.Should().Be("Tenant.LimiteDeVagasAtingido");
        tenant.OccupiedSeats.Should().Be(2);
    }

    [Fact]
    public void ReleaseSeat_ComVagaOcupada_Libera()
    {
        Tenant tenant = TenantAtivo();
        tenant.ReserveSeat();

        tenant.ReleaseSeat();

        tenant.OccupiedSeats.Should().Be(0);
    }

    [Fact]
    public void ReleaseSeat_SemVagaOcupada_Lanca()
    {
        // NÃO é idempotente por desenho: um clamp em zero esconderia dupla liberação, e o contador chegaria
        // a zero com o tenant cheio. Ver o XML doc do método.
        Tenant tenant = TenantAtivo();

        Action liberar = tenant.ReleaseSeat;

        liberar.Should().Throw<DomainInvariantViolation>();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/IdentityGateway.Domain.UnitTests --filter TenantTests`
Expected: FAIL na compilação — `Tenant` não encontrado

- [ ] **Step 3: Write minimal implementation**

```csharp
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants.Events;

namespace IdentityGateway.Domain.Tenants;

/// <summary>
/// Aggregate root do tenant. Guarda somente dados de governança; identidade e credenciais vivem no
/// Keycloak.
/// </summary>
public sealed class Tenant : AggregateRoot<TenantId>
{
    private Tenant(TenantId id, string name, TenantSlug slug, Plan plan)
        : base(id)
    {
        Name = name;
        Slug = slug;
        Plan = plan;
        Status = TenantStatus.Pending;
    }

    /// <summary>Nome de exibição.</summary>
    public string Name { get; private set; }

    /// <summary>Slug único e imutável; vira o alias da Organization no Keycloak.</summary>
    public TenantSlug Slug { get; private set; }

    /// <summary>Limites contratados.</summary>
    public Plan Plan { get; private set; }

    /// <summary>Estado no ciclo de vida.</summary>
    public TenantStatus Status { get; private set; }

    /// <summary>Id da Organization no Keycloak; nulo até o provisionamento concluir.</summary>
    public string? ExternalOrganizationId { get; private set; }

    /// <summary>Vagas de membro ocupadas.</summary>
    /// <remarks>
    /// Protegido por concorrência otimista na persistência (<c>xmin</c>): duas reservas simultâneas geram
    /// conflito de versão, e o retry reprocessa com o valor atual.
    /// </remarks>
    public int OccupiedSeats { get; private set; }

    /// <summary>Se o tenant excedeu o teto do plano por absorção externa.</summary>
    /// <remarks>
    /// Nenhuma operação da API liga isto. A única via é a absorção de usuários criados fora da Gateway,
    /// e ela é auditada — daí a invariante de vagas valer para a API, não para o contador em absoluto.
    /// </remarks>
    public bool OverSubscribed { get; private set; }

    /// <summary>
    /// Registra um tenant novo, ainda por provisionar.
    /// </summary>
    /// <remarks>
    /// Nenhuma chamada ao Keycloak acontece aqui. O evento levantado vira mensagem no Outbox, gravada na
    /// mesma transação do <c>INSERT</c>: com o Keycloak fora do ar nada fica órfão, e o provisionamento
    /// apenas acontece mais tarde.
    /// </remarks>
    /// <param name="name">Nome de exibição.</param>
    /// <param name="slug">Slug já validado.</param>
    /// <param name="plan">Plano vindo do catálogo.</param>
    /// <returns>O tenant em <see cref="TenantStatus.Pending"/>.</returns>
    public static Tenant Register(string name, TenantSlug slug, Plan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(plan);

        Tenant tenant = new(TenantId.New(), name, slug, plan);

        tenant.RaiseDomainEvent(new TenantRegistered(tenant.Id, slug));

        return tenant;
    }

    /// <summary>
    /// Conclui o provisionamento e coloca o tenant em operação.
    /// </summary>
    /// <remarks>
    /// <b>Idempotente na entrada:</b> a mesma mensagem do Outbox pode ser entregue mais de uma vez, e a
    /// segunda entrega precisa ser inofensiva. Já estando ativo com o mesmo id externo, retorna sem efeito
    /// — inclusive sem levantar o evento de novo.
    /// </remarks>
    /// <param name="externalOrganizationId">Id da Organization criada no Keycloak.</param>
    /// <exception cref="DomainInvariantViolation">
    /// Se o tenant não estiver em <see cref="TenantStatus.Pending"/> nem em
    /// <see cref="TenantStatus.ProvisioningFailed"/>. Transição inválida é erro de programação.
    /// </exception>
    public void MarkProvisioned(string externalOrganizationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalOrganizationId);

        if (Status == TenantStatus.Active && ExternalOrganizationId == externalOrganizationId)
        {
            return;
        }

        EnsureStatusIn(TenantStatus.Pending, TenantStatus.ProvisioningFailed);

        ExternalOrganizationId = externalOrganizationId;
        Status = TenantStatus.Active;

        RaiseDomainEvent(new TenantActivated(Id));
    }

    /// <summary>
    /// Marca que o provisionamento falhou depois de esgotados os retries.
    /// </summary>
    /// <remarks>
    /// O tenant fica aguardando retry manual, e o evento segue disponível para reprocessamento. Idempotente
    /// porque o consumidor de Fault também pode reentregar.
    /// </remarks>
    /// <exception cref="DomainInvariantViolation">Se o tenant não estiver em Pending nem já falhado.</exception>
    public void MarkProvisioningFailed()
    {
        if (Status == TenantStatus.ProvisioningFailed)
        {
            return;
        }

        EnsureStatusIn(TenantStatus.Pending);

        Status = TenantStatus.ProvisioningFailed;
    }

    /// <summary>
    /// Reserva uma vaga do plano.
    /// </summary>
    /// <remarks>
    /// Devolve <see cref="Result"/> e não lança: tanto "o tenant não está ativo" quanto "as vagas
    /// acabaram" são respostas de negócio legítimas, e quem chama decide o que fazer com elas.
    /// <para>
    /// Duas reservas concorrentes geram conflito de versão no <c>SaveChanges</c> (<c>xmin</c>); o retry que
    /// reprocessa com o valor atual vive no pipeline de comandos — o agregado não sabe de persistência.
    /// </para>
    /// </remarks>
    /// <returns>Sucesso, ou o erro que impediu a reserva.</returns>
    public Result ReserveSeat()
    {
        if (Status != TenantStatus.Active)
        {
            return Result.Failure(TenantErrors.NotActive(Id));
        }

        if (OccupiedSeats >= Plan.MaxUsers)
        {
            return Result.Failure(TenantErrors.SeatLimitReached(Plan.MaxUsers));
        }

        OccupiedSeats++;

        return Result.Success();
    }

    /// <summary>
    /// Libera uma vaga ocupada.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Não é idempotente, de propósito.</b> Deve ser chamado apenas como consequência de uma transição
    /// de estado que de fato ocorreu — quem chama é o handler, e só quando a desativação do membro reporta
    /// que a transição aconteceu.
    /// </para>
    /// <para>
    /// A versão anterior da especificação usava <c>Math.Max(0, OccupiedSeats - 1)</c>. O clamp não protegia
    /// nada: escondia a divergência, impedindo o valor negativo que denunciaria a dupla liberação. Um POST
    /// de desativação repetido (timeout mais retry do cliente) decrementava duas vezes, e o <c>xmin</c> não
    /// pega — concorrência otimista detecta escrita simultânea, não repetida. Repetido N vezes, o contador
    /// chegava a zero com o tenant cheio, e o limite do plano deixava de existir em silêncio.
    /// </para>
    /// </remarks>
    /// <exception cref="DomainInvariantViolation">Se não houver vaga ocupada para liberar.</exception>
    public void ReleaseSeat()
    {
        if (OccupiedSeats == 0)
        {
            throw new DomainInvariantViolation(
                $"Tenant {Id.Value}: liberação de vaga sem vaga ocupada.");
        }

        OccupiedSeats--;
    }

    private void EnsureStatusIn(params TenantStatus[] permitidos)
    {
        if (Array.IndexOf(permitidos, Status) >= 0)
        {
            return;
        }

        throw new DomainInvariantViolation(
            $"Tenant {Id.Value}: operação exige o estado {string.Join(" ou ", permitidos)}, "
            + $"mas o tenant está em {Status}.");
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/IdentityGateway.Domain.UnitTests --filter TenantTests`
Expected: PASS (15 testes)

- [ ] **Step 5: Verificar o build inteiro**

Run: `dotnet build IdentityGateway.slnx`
Expected: `Compilação com êxito`, 0 avisos, 0 erros

Se aparecer aviso sobre `Name`, `Slug` ou `Plan` terem `private set` sem uso: trocar por `{ get; }` e atribuir só no construtor. A propriedade privada existe para o EF Core materializar, mas enquanto não há mapeamento o analisador reclama.

- [ ] **Step 6: Commit**

```bash
git add src/IdentityGateway.Domain/Tenants/Tenant.cs tests/IdentityGateway.Domain.UnitTests/Tenants/TenantTests.cs
git commit -m "feat: acrescenta o agregado Tenant com o caminho de registro"
```

---

### Task 7: registrar os eventos no mapa do Outbox

O mapa está vazio, e `OutboxEventTypes.NomeDe` lança para evento não registrado. Registrar agora evita que o primeiro despacho real descubra isso.

**Files:**
- Modify: `src/IdentityGateway.Infrastructure/Persistence/Outbox/OutboxEventTypes.cs`

**Interfaces:**
- Consumes: `TenantRegistered`, `TenantActivated` (Task 5)
- Produces: nada novo

- [ ] **Step 1: Registrar os dois eventos**

Acrescentar o `using` no topo do arquivo:

```csharp
using IdentityGateway.Domain.Tenants.Events;
```

E substituir o corpo do dicionário `PorTipo` (hoje só comentário) por:

```csharp
    private static readonly Dictionary<Type, string> PorTipo = new()
    {
        [typeof(TenantRegistered)] = "tenant-registered",
        [typeof(TenantActivated)] = "tenant-activated",
    };
```

O nome curto é estável e desacoplado do nome do tipo de propósito: renomear a classe não pode invalidar mensagens já gravadas na fila.

- [ ] **Step 2: Verificar que compila e que nada quebrou**

Run: `dotnet build IdentityGateway.slnx && dotnet test IdentityGateway.slnx --no-build`
Expected: build com 0 avisos; testes sem falha

- [ ] **Step 3: Commit**

```bash
git add src/IdentityGateway.Infrastructure/Persistence/Outbox/OutboxEventTypes.cs
git commit -m "feat: registra os eventos do tenant no mapa do outbox"
```

---

### Task 8: reativar as duas regras de arquitetura

O agregado dá às regras de domínio o que inspecionar. Sair do `Skip` **e ver a regra reprovar** é o que prova que ela não voltou vazia.

**Files:**
- Modify: `tests/IdentityGateway.ArchitectureTests/RegrasDeDominioTests.cs`
- Modify: `tests/IdentityGateway.ArchitectureTests/README.md`
- Modify: `tests/IdentityGateway.ArchitectureTests/RegrasDeMensageriaTests.cs` (só o texto do `Skip`)

**Interfaces:**
- Consumes: `Tenant` no assembly de domínio
- Produces: nada

- [ ] **Step 1: Remover o `Skip` das duas regras de domínio**

Em `RegrasDeDominioTests.cs`, trocar em **`Entidades_NaoExpoemSetterPublico`** e em
**`RaizesDeAgregado_ExpoemColecoesSomenteLeitura`**:

```csharp
    [Fact(Skip = "Sem agregado/handler no dominio ainda: a guarda NotBeEmpty dispara de proposito para a regra nao passar em vacuidade. Reativar com o primeiro agregado do M0.")]
```

por:

```csharp
    [Fact]
```

- [ ] **Step 2: Rodar e confirmar que passam**

Run: `dotnet test tests/IdentityGateway.ArchitectureTests`
Expected: PASS. As duas de mensageria seguem em `Skip`.

Se `Entidades_NaoExpoemSetterPublico` reprovar apontando `Tenant.Name`, `Tenant.Slug`, `Tenant.Plan` ou
`Tenant.Status`: o `private set` está sendo visto como setter público — troque por `{ get; }` e atribua
apenas no construtor.

- [ ] **Step 3: Provar que a regra reprova**

Sem isto, não há prova de que a regra saiu do `Skip` para um verde legítimo em vez de voltar vazia.

Em `Tenant.cs`, trocar temporariamente `public string Name { get; private set; }` por
`public string Name { get; set; }`.

Run: `dotnet test tests/IdentityGateway.ArchitectureTests --filter Entidades_NaoExpoemSetterPublico`
Expected: **FAIL**, nomeando `Tenant.Name`

**Reverter a alteração** e rodar de novo:

Run: `dotnet test tests/IdentityGateway.ArchitectureTests --filter Entidades_NaoExpoemSetterPublico`
Expected: PASS

- [ ] **Step 4: Atualizar o motivo do `Skip` das duas de mensageria**

Em `RegrasDeMensageriaTests.cs`, em `Handlers_SaoSealed` e `CommandsEQueries_SaoRecord`, trocar o texto do
`Skip` por:

```
"Sem handler nem command na Application ainda: a guarda NotBeEmpty dispara de proposito para a regra nao passar em vacuidade. Reativar com o handler de RegisterTenant."
```

O motivo antigo dizia "com o primeiro agregado do M0", e o agregado já existe — o que falta agora é o handler.

- [ ] **Step 5: Atualizar o README dos testes de arquitetura**

Em `tests/IdentityGateway.ArchitectureTests/README.md`, mover as duas regras de domínio da seção
"Em `Skip` até o primeiro agregado" para a tabela de regras ativas, e ajustar o título e o texto da seção
restante para dizer que as duas que sobraram aguardam o **handler**, não o agregado.

- [ ] **Step 6: Rodar a suíte inteira**

Run: `dotnet build IdentityGateway.slnx && dotnet test IdentityGateway.slnx --no-build`
Expected: 0 avisos; 0 falhas; **4 ignorados** (eram 6)

- [ ] **Step 7: Commit**

```bash
git add tests/IdentityGateway.ArchitectureTests
git commit -m "test: reativa as regras de dominio agora que existe agregado"
```

---

## Verificação final

- [ ] `dotnet build IdentityGateway.slnx` → 0 avisos, 0 erros
- [ ] `dotnet test IdentityGateway.slnx` → 0 falhas, 4 ignorados
- [ ] `git log --oneline` mostra 8 commits desta fatia
- [ ] Nenhum estado coberto pela fatia ficou inalcançável: `Pending` (Register), `Active`
      (MarkProvisioned) e `ProvisioningFailed` (MarkProvisioningFailed) têm entrada
