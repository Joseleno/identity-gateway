# Fatia C — Convite do admin inicial: plano de implementação

> **Para agentes:** SUB-SKILL OBRIGATÓRIA: use superpowers:subagent-driven-development (recomendado) ou superpowers:executing-plans para executar este plano tarefa por tarefa. Os passos usam checkbox (`- [ ]`).

**Objetivo:** Fechar o provisionamento da §9.1: o `ProvisionTenantHandler` garante a Organization, garante o convite do `initialAdminEmail` com o papel `tenant-admin` (usuário habilitado, vínculo, papel e `execute-actions-email` com o prazo da política) e só então ativa o tenant numa operação de domínio só — `Active`, vaga do admin ocupada, `Member` em `Invited` e e-mail apagado —, num commit só. Com o compose de pé, o e-mail de convite aparece no mailpit com um link público que abre.

**Arquitetura:** O e-mail fica temporariamente numa coluna anulável do tenant (fora do evento) e é apagado na ativação ou na falha. A Application ganha a porta `EnsureInvitedUserAsync(organizationId, tenantId, InviteData)` e a `IInvitationPolicy`; o adaptador do Keycloak executa cinco passos idempotentes correlacionados pelo atributo de usuário `tenant_id`, declarado no User Profile só para `admin`. O endereço público do Keycloak (`KC_HOSTNAME`) passa a alimentar só o `aud` do client assertion (`PublicBaseUrl`), e o transporte continua no `BaseUrl` interno.

**Stack:** .NET 10, Mediator 3.0.2, EF Core 10 + Npgsql, FluentValidation 12, xUnit v3 + AwesomeAssertions + NSubstitute, Testcontainers 4.15 (PostgreSQL 17, Keycloak 26.7.4, mailpit v1.31.3), OpenTelemetry 1.18 (InMemory exporter só nos testes), Docker Compose, GitHub Actions.

**Spec:** docs/superpowers/specs/2026-09-29-convite-admin-inicial-design.md

## Restrições globais

- E-mail: `Email.Of` é a única regra de forma, usada também pelo validador do `POST`: até 254 caracteres; parte local de 1 a 64 caracteres em `[a-z0-9._+-]`, sem ponto no início, no fim ou repetido; domínio com rótulos `[a-z0-9-]` (hífen só no meio, até 63 cada) e pelo menos um ponto; normalizado com `Trim()` e `ToLowerInvariant()`.
- `Email.ToString()` devolve `Email(***)`; quem precisa do endereço usa `Email.Value`. `DomainErrors.Email.Invalido()` não recebe nem ecoa o valor.
- Coluna `tenants.initial_admin_email`: `varchar(254)`, anulável. Tabela `members`: `id`, `tenant_id` (FK para `tenants.id`, `Restrict`), `external_user_id` (`varchar(255)`), `status` (`varchar(20)`, texto), `invited_at`, `created_at`, `updated_at`, `created_by`, `updated_by`; índice único `ix_members_tenant_id_external_user_id` em `(tenant_id, external_user_id)`. Uma migration só: `ConviteDoAdminInicial`.
- `MemberStatus`: `Invited`, `Active`, `Deactivated`, `Expired`, `Revoked`, `Erased` (§6.1 da v2.5), persistido como texto.
- `Invitations:LinkLifetime`: padrão `7.00:00:00`; recusado na subida se não for positivo, se tiver fração de segundo ou se passar de 30 dias.
- Catálogo de planos: `maxUsers >= 1` e `maxClients >= 0` na subida. O `Plan` continua aceitando `maxUsers = 0` (dado antigo).
- Keycloak: `AssertionAudience = {PublicBaseUrl ?? BaseUrl}/realms/{Realm}`; token endpoint e Admin API derivados **só** do `BaseUrl`. `PublicBaseUrl` opcional, absoluta, `http`/`https`, sem query nem fragmento; `http` e `AllowInsecureHttp` recusados fora de `Development`.
- Atributo de usuário `tenant_id` (valor único, igual ao `TenantId`), declarado no User Profile com `view` e `edit` só `["admin"]`; nenhuma `unmanagedAttributePolicy`; nenhum `${...}` dentro de `kc.user.profile.config`.
- Busca de usuário: `GET /users?email=<escapado>&exact=true&briefRepresentation=false`. Depois do `409` do `POST /users`, **uma** nova busca por e-mail e por `username=<escapado>&exact=true`, nunca em laço.
- `execute-actions-email` só com `UPDATE_PASSWORD` pendente, corpo `["UPDATE_PASSWORD","VERIFY_EMAIL"]`, `lifespan` = `(int)LinkLifetime.TotalSeconds`. `400` nesse `PUT` e papel ausente no realm → `IdentityProviderInconsistencyException`; `500`, timeout e o resto → transitório.
- Nenhuma exceção, log ou mensagem de erro carrega o e-mail; levam o `tenantId`. EventIds novos: `1105`–`1106` (`ProvisioningLogs`), `2205`–`2213` (`KeycloakLogs`).
- `smtpServer` do realm: `connectionTimeout` 2000, `timeout` 3000, `writeTimeout` 3000 ms (chaves de `DefaultEmailSenderProvider.java` L149-151 da 26.7.4, padrão 10000), abaixo do `AttemptTimeout` de 10 s.
- mailpit fixado em `axllent/mailpit:v1.31.3` (release de 2026-09-27) no compose **e** no `KeycloakFixture`; um teste de arquitetura exige a mesma tag nos dois.
- Compose: `KC_HOSTNAME=http://localhost:8081` igual a `Keycloak__Admin__PublicBaseUrl` (teste de arquitetura); Postgres, Redis, Seq e mailpit (8025) só em `127.0.0.1`; SMTP do mailpit sem porta publicada.
- Testes contra o Keycloak: e-mails únicos com `+` (`admin+{guid}@acme.test`); mailpit sempre filtrado por destinatário exato; `KC_HOSTNAME=http://keycloak.test:8081` no fixture e `PublicBaseUrl` igual em toda composição.
- Convenções do repo: identificadores públicos em inglês, variáveis, membros privados e testes em português (`Metodo_Cenario_Resultado`), `sealed`, `partial` para `LoggerMessage`, comentários XML em português que explicam o porquê, `TreatWarningsAsErrors` ligado, `TestContext.Current.CancellationToken` nos testes.
- Commits em Conventional Commits, em português sem acentos, como o histórico. **Nenhum trailer nem menção a IA, Claude ou Anthropic** (sem `Co-Authored-By`, sem "Generated with").
- Docker Desktop ligado nas Tarefas 4, 6 a 12 (Testcontainers e compose). Sem ele, `DockerUnavailableException` é ambiente, não regressão.
- 🧪 = passo "Prova por mutação" obrigatório: aplicar a mutação, rodar o teste indicado, ver vermelho, **reverter** (conferir com `git diff` que o arquivo voltou byte a byte), ver verde. Registrar a mutação na mensagem de commit e, depois, na tabela do handoff.

## Foco de revisão

Cinco condições que a spec não cobre explicitamente; cada uma tem teste na tarefa dona:

1. **E-mail com maiúsculas e espaços ao redor** (`"  Admin@Acme.COM "`): gravado normalizado, e o usuário do Keycloak — que grava em minúsculas — é encontrado e reaproveitado no retry. Tarefa 1 (`Of_ComCaixaEEspacos_Normaliza`), Tarefa 3 (`Register_GuardaOEmailNormalizado`), Tarefa 8 (`UsuarioCriadoPeloMasterComCaixaMista_EReaproveitado`), Tarefa 11 (funcional `ComandoValido_GravaOEmailNormalizadoForaDoEvento`).
2. **Entrega concorrente duplicada da mesma mensagem**: um só `Member`, uma só vaga ocupada, o segundo commit falha por `xmin` ou pelo índice e a reentrega não duplica efeito no domínio. Tarefa 11 (`DuasEntregasConcorrentes_UmMemberUmaVagaEUmCommitFalha`).
3. **Keycloak cai entre passos** (usuário criado, vínculo ou papel falham): o retry completa sem duplicar. Tarefa 8 (`QuedaNoVinculo_RetryCompletaSemDuplicar`, `QuedaNoPapel_RetryCompletaSemDuplicar`).
4. **SMTP fora do ar e depois de volta**: o tenant fica `Pending` com o e-mail e completa na volta, com o usuário reaproveitado. Tarefa 8 (`SmtpFora_RetryReaproveitaOUsuarioEEnviaNaVolta`), Tarefa 9 (`SmtpForaEDeVolta_FicaPendingComEmailEDepoisAtiva`).
5. **E-mail de 254 caracteres** (limite da coluna): aceito ponta a ponta. Tarefa 1 (`Of_Com254Caracteres_Aceita`, validator), Tarefa 4 (`EmailDe254Caracteres_SobreviveAoRoundTrip`), Tarefa 8 (`EmailDe254Caracteres_ViraUsernameNoKeycloak`).

Nota para o revisor: **nenhum teste exercitou o SMTP do Keycloak até esta fatia.** O `500` com o SMTP fora do ar vem de leitura de código (`UserResource.java` L1073-1075) e é injetado por interceptação (Tarefa 8), porque derrubar o mailpit compartilhado quebraria os testes paralelos.

---

## Desvios deliberados da decomposição combinada

- **`MarkProvisioned` sai na Tarefa 9, e não na 3.** O `ProvisionTenantHandler` o chama até a Tarefa 9, e cada commit precisa compilar. A Tarefa 3 acrescenta `CompleteProvisioning` e os testes de recusa; a 9 remove o método e os testes que o exercitavam.
- **Os dois testes de permissão do service account contra o Keycloak real mudam na Tarefa 6, e não na 7.** O `manage-users` entra no realm na Tarefa 6, e `ServiceAccount_RecebeProibidoEmUsuarios` e `ServiceAccount_TemExatamenteManageOrganizations` ficariam vermelhos entre as duas. A troca do `403` e o teste de papéis efetivos vão juntos com o realm.
- **O e-mail único em `ComposicaoDoProvisionamento.RegistrarAsync` entra na Tarefa 9, e não na 11.** Com o convite no handler, dois testes com `admin@acme.com` fixo caem no D5 (o segundo vira `ProvisioningFailed`). O resto da Tarefa 11 (E2E, dois e-mails, atomicidade, concorrência, funcional) fica lá.
- **`Email.Of` fica mais estrito do que "a mesma regra do validador".** O username do convidado é o e-mail, e o Keycloak recusa parte local acima de 64 caracteres (`EmailValidationUtil.java` L18, L74) e username com `!#$%&'*=?^{}|~/` (`UsernameProhibitedCharactersValidator.java` L44). Um e-mail que a Gateway aceitasse e o Keycloak recusasse viraria `400` no `POST /users`, repetido pela janela inteira. A regra comum passa a ser o recorte seguro dos dois.
- **Teste de vazamento com o log de dados sensíveis do EF no valor do `appsettings.Development.json`, e não ligado à força.** Ligado, o EF registra por construção o parâmetro do `INSERT` do registro e, no `DetectChanges` em `Debug`, o valor antigo da coluna ao apagá-la — o teste seria vermelho sem defeito nenhum. Lendo o valor do arquivo, o teste prova o que a spec quer (Development não vaza) e a mutação "voltar `EnableSensitiveDataLogging` a `true`" o deixa vermelho.
- **`ToString` sobrescrito também em `RegisterTenantCommand` e `RegisterTenantRequest`** (Tarefa 1). São records com o e-mail, e o `ToString` gerado o imprimiria em qualquer log que os formatasse — o mesmo motivo do `InviteData` (§4.3).
- **`displayName` literal no User Profile** (`"Username"`, `"Email"`, `"First name"`, `"Last name"`), e não `"${username}"` como no perfil padrão: o import substituiria o `${...}` e o `TodoPlaceholderEPuro` reprovaria o realm.

## Mapa de arquivos

**Domain**
- Modify `src/IdentityGateway.Domain/IdentityGateway.Domain.csproj` — `InternalsVisibleTo` para `IdentityGateway.Domain.UnitTests` (a fábrica do `Member` é `internal`).
- Modify `src/IdentityGateway.Domain/ValueObjects/Email.cs` — regra única, `IsValid`, `ToString` redigido.
- Modify `src/IdentityGateway.Domain/Errors/DomainErrors.cs` — `Email.Invalido()` sem valor.
- Create `src/IdentityGateway.Domain/Members/MemberId.cs`, `MemberStatus.cs`, `ExternalUserId.cs`, `RoleName.cs`, `Member.cs`.
- Modify `src/IdentityGateway.Domain/Tenants/Tenant.cs` — `InitialAdminEmail`, `HasSeatAvailable`, `Register` com e-mail, `CompleteProvisioning`, `MarkProvisioningFailed` apaga o e-mail; `MarkProvisioned` removido (Tarefa 9).
- Modify `src/IdentityGateway.Domain/Tenants/Plan.cs` — comentário do "zero".

**Application**
- Modify `src/IdentityGateway.Application/Tenants/RegisterTenant/RegisterTenantValidator.cs`, `RegisterTenantHandler.cs`, `RegisterTenantCommand.cs`.
- Create `src/IdentityGateway.Application/Common/Abstractions/IMemberRepository.cs`, `IInvitationPolicy.cs`, `InviteData.cs`.
- Modify `src/IdentityGateway.Application/Common/Abstractions/IIdentityProvider.cs` — `EnsureInvitedUserAsync`.
- Modify `src/IdentityGateway.Application/Tenants/ProvisionTenant/ProvisionTenantHandler.cs`, `ProvisioningLogs.cs`.

**Infrastructure**
- Modify `Persistence/AppDbContext.cs` (`Members`), `Persistence/Configurations/TenantConfiguration.cs`; create `Persistence/Configurations/MemberConfiguration.cs`, `Persistence/Repositories/MemberRepository.cs`.
- Create migration `Persistence/Migrations/*_ConviteDoAdminInicial.cs` (gerada; `Designer` e snapshot também).
- Create `Configuration/InvitationOptions.cs`, `Configuration/InvitationPolicy.cs`; modify `Configuration/DatabaseOptions.cs` (comentário), `DependencyInjection.cs`.
- Modify `Identity/Keycloak/KeycloakAdminOptions.cs`, `ClientAssertionFactory.cs`, `KeycloakTokenClient.cs`, `KeycloakServiceCollectionExtensions.cs`, `KeycloakHealthCheck.cs`, `KeycloakAdminClient.cs`, `KeycloakIdentityProvider.cs`, `KeycloakLogs.cs`; create `Identity/Keycloak/UserRepresentation.cs`.

**Api**
- Modify `src/IdentityGateway.Api/Modules/RegisterTenantRequest.cs` (`ToString`), `appsettings.json` (`Invitations`), `appsettings.Development.json` (EF sem dados sensíveis).

**Realm, compose e CI**
- Modify `keycloak/bootstrap/realm-identity-gateway.json`, `docker-compose.yml`, `.github/workflows/ci.yml`.
- Modify `Directory.Packages.props` — `OpenTelemetry.Exporter.InMemory` 1.18.0 (só testes).

**Testes**
- Domain: `ValueObjects/EmailTests.cs`, `Tenants/TenantTests.cs` (reescrito), `Tenants/PlanTests.cs`; create `Members/MemberTests.cs`, `Members/ExternalUserIdTests.cs`, `Members/RoleNameTests.cs`, `Members/MemberIdTests.cs`.
- Application: `Tenants/RegisterTenant/RegisterTenantValidatorTests.cs`, `RegisterTenantHandlerTests.cs`, create `RegisterTenantCommandTests.cs`; `Tenants/ProvisionTenant/ProvisionTenantHandlerTests.cs` (reescrito); create `ColetorDeLogs.cs`.
- Architecture: `RegrasDeDominioTests.cs`, `RegrasDoRealmTests.cs`; create `RegrasDoAmbienteLocalTests.cs`.
- Integration: create `AmbienteDeTeste.cs`, `ColetorDeLogs.cs`; modify `PostgresFixture.cs`, `DependencyInjectionTests.cs`, `Persistence/*`, `Persistence/Outbox/OutboxProcessorTests.cs`; create `Persistence/MapeamentoDeMemberTests.cs`, `Persistence/SchemaDeMembersTests.cs`; `Identity/Keycloak/KeycloakFixture.cs` (mailpit, rede, `KC_HOSTNAME`), `KeycloakHealthCheckTests.cs`, `KeycloakRealTests.cs`, `ClientAssertionFactoryTests.cs`, `KeycloakAdminOptionsTests.cs`, `KeycloakTokenClientTests.cs`, `KeycloakIdentityProviderTests.cs`, `ResilienciaDoAdminClientTests.cs`, `HandlerDeInterceptacao.cs`; create `Identity/Keycloak/EnsureInvitedUserContraKeycloakTests.cs`; `Provisioning/ComposicaoDoProvisionamento.cs`, `ProvisionamentoContraKeycloakTests.cs`; create `Provisioning/AtomicidadeDoProvisionamentoTests.cs`, `Provisioning/VazamentoDoEmailTests.cs`.
- Functional: `RegistroDeTenantTests.cs`; create `VazamentoDoEmailNaApiTests.cs`.

**Documentos**
- Create `docs/especificacao-arquitetural-v2.6.md`; modify `docs/documentacao-negocio.md`, `README.md`, `CONTRIBUTING.md`; create `docs/superpowers/specs/<data>-convite-admin-inicial-handoff.md`.

---

### Tarefa 1: E-mail redigido e validação única

**Arquivos:**
- Modify: `src/IdentityGateway.Domain/ValueObjects/Email.cs` (arquivo inteiro)
- Modify: `src/IdentityGateway.Domain/Errors/DomainErrors.cs:35-42` (grupo `Email`)
- Modify: `src/IdentityGateway.Application/Tenants/RegisterTenant/RegisterTenantValidator.cs` (arquivo inteiro)
- Modify: `src/IdentityGateway.Application/Tenants/RegisterTenant/RegisterTenantCommand.cs` (`ToString` e o `<remarks>`)
- Modify: `src/IdentityGateway.Api/Modules/RegisterTenantRequest.cs` (`ToString`)
- Test: `tests/IdentityGateway.Domain.UnitTests/ValueObjects/EmailTests.cs` (arquivo inteiro)
- Test: `tests/IdentityGateway.Application.UnitTests/Tenants/RegisterTenant/RegisterTenantValidatorTests.cs`
- Create: `tests/IdentityGateway.Application.UnitTests/Tenants/RegisterTenant/RegisterTenantCommandTests.cs`
- Create: `tests/IdentityGateway.Api.FunctionalTests/RegisterTenantRequestTests.cs`

**Interfaces:**
- Consome: nada.
- Produz:
  - `public static Result<Email> Email.Of(string value)` — regra única (ver Restrições globais).
  - `public static bool Email.IsValid(string? value)`
  - `public override string Email.ToString()` → `"Email(***)"`; o endereço só por `Email.Value`.
  - `public static Error DomainErrors.Email.Invalido()` — sem parâmetro.

Não há, hoje, nenhum uso de `Email.ToString()` nem conversão do EF sobre `Email` (conferido com
`grep -rn "Email" src --include=*.cs`): o único consumidor futuro é a configuração da Tarefa 4, que já nasce com
`.Value`.

- [ ] **Passo 1: Reescrever os testes do `Email`**

`tests/IdentityGateway.Domain.UnitTests/ValueObjects/EmailTests.cs`, inteiro:

```csharp
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.ValueObjects;

namespace IdentityGateway.Domain.UnitTests.ValueObjects;

/// <summary>
/// Criação de <see cref="Email"/>: o que a regra aceita, o que ela recusa, e o que ela nunca revela.
/// </summary>
public sealed class EmailTests
{
    // Parte local de 64 (o máximo) + "@" + domínio de 189 em rótulos de até 63: o maior endereço que a regra aceita.
    private static string Endereco254() =>
        $"{new string('a', 64)}@{new string('b', 63)}.{new string('c', 63)}.{new string('d', 56)}.test";

    private static string Endereco255() =>
        $"{new string('a', 64)}@{new string('b', 63)}.{new string('c', 63)}.{new string('d', 57)}.test";

    [Theory]
    [InlineData("joao@example.com")]
    [InlineData("joao.silva@example.com")]
    [InlineData("joao+tag@example.com")]
    [InlineData("joao_silva-2@example.com")]
    [InlineData("joao@sub.example.com.br")]
    [InlineData("a@b.co")]
    public void Of_ComEnderecoValido_RetornaSucesso(string entrada)
    {
        Email.Of(entrada).IsSuccess.Should().BeTrue($"'{entrada}' tem forma de endereço");
    }

    [Theory]
    [InlineData("sem-arroba.com")]
    [InlineData("@example.com")]          // sem parte local
    [InlineData("joao@")]                 // sem domínio
    [InlineData("joao@com")]              // domínio sem ponto
    [InlineData("a@b")]                   // o que o EmailAddress() do FluentValidation aceitava
    [InlineData("joao@example.")]         // termina em ponto
    [InlineData("joao@@example.com")]     // dois arrobas
    [InlineData("joao@exa..mple.com")]    // pontos consecutivos no domínio
    [InlineData("jo..ao@example.com")]    // pontos consecutivos na parte local
    [InlineData(".joao@example.com")]     // parte local começa em ponto
    [InlineData("joao.@example.com")]     // parte local termina em ponto
    [InlineData("joao!@example.com")]     // o Keycloak recusa ! no username
    [InlineData("joão@example.com")]      // fora do recorte ASCII
    [InlineData("joao@-example.com")]     // rótulo começa em hífen
    [InlineData("joao@example-.com")]     // rótulo termina em hífen
    [InlineData("joao@exam_ple.com")]     // sublinhado não é DNS
    [InlineData("joao silva@example.com")] // com espaço
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Of_ComEnderecoInvalido_RetornaFalha(string? entrada)
    {
        Email.Of(entrada!).IsFailure.Should().BeTrue($"'{entrada}' não tem forma de endereço aceita");
    }

    [Theory]
    [InlineData("JOAO@EXAMPLE.COM", "joao@example.com")]
    [InlineData("  Joao@Example.com  ", "joao@example.com")]
    public void Of_NormalizaParaMinusculas(string entrada, string esperado)
    {
        Result<Email> resultado = Email.Of(entrada);

        // Sem normalizar, Joao@x.com e joao@x.com seriam endereços distintos — e o mesmo usuário
        // conseguiria se cadastrar duas vezes.
        resultado.Value.Value.Should().Be(esperado);
    }

    [Fact]
    public void Of_ComCaixaEEspacos_Normaliza()
    {
        // Foco de revisão 1: é o valor que vai para a coluna e para a busca exata do Keycloak, que compara em
        // minúsculas. Guardado como veio, o retry não reencontraria o usuário criado na primeira tentativa.
        Email.Of("  Admin@Acme.COM ").Value.Value.Should().Be("admin@acme.com");
    }

    [Fact]
    public void Of_Com254Caracteres_Aceita()
    {
        // Foco de revisão 5: 254 é o tamanho da coluna initial_admin_email e o limite da RFC 5321.
        string endereco = Endereco254();
        endereco.Length.Should().Be(254);

        Email.Of(endereco).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Of_Com255Caracteres_Recusa()
    {
        string endereco = Endereco255();
        endereco.Length.Should().Be(255);

        Email.Of(endereco).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Of_ParteLocalDe64_AceitaEDe65_Recusa()
    {
        // 64 é o limite da RFC 5321 e o do Keycloak (EmailValidationUtil, MAX_LOCAL_PART_LENGTH). Aceitar 65 faria o
        // POST /users responder 400 a cada tentativa do provisionamento, pela janela inteira.
        Email.Of($"{new string('a', 64)}@example.com").IsSuccess.Should().BeTrue();
        Email.Of($"{new string('a', 65)}@example.com").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void ToString_NaoContemOEndereco()
    {
        // D15: um log com {email}, uma interpolação numa mensagem de exceção — o ToString é o caminho acidental.
        Email email = Email.Of("segredo+tag@acme.test").Value;

        email.ToString().Should().NotContain("segredo").And.NotContain("acme");
    }

    [Fact]
    public void ErroDeEnderecoInvalido_NaoEcoaOValor()
    {
        // A mensagem do erro vai para log e para o corpo da resposta 400.
        Result<Email> resultado = Email.Of("segredo@@acme.test");

        resultado.Error.Message.Should().NotContain("segredo");
        resultado.Error.Code.Should().Be("Email.Invalido");
    }

    [Theory]
    [InlineData("joao@example.com", true)]
    [InlineData("a@b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid_ConcordaComOf(string? entrada, bool esperado)
    {
        Email.IsValid(entrada).Should().Be(esperado);
    }

    [Fact]
    public void Equals_IgnorandoCaixa_SaoIguais()
    {
        Email um = Email.Of("joao@example.com").Value;
        Email outro = Email.Of("JOAO@example.com").Value;

        um.Should().Be(outro);
    }
}
```

- [ ] **Passo 2: Escrever os testes do validador e do `ToString` dos dois records**

Em `RegisterTenantValidatorTests.cs`, trocar a Theory `EmailInvalido_Falha` e acrescentar os testes abaixo:

```csharp
    [Theory]
    [InlineData("")]
    [InlineData("sem-arroba")]
    [InlineData("a@b")]
    [InlineData("joao!@acme.com")]
    public void EmailInvalido_Falha(string email)
    {
        // "a@b" passava no EmailAddress() do FluentValidation e era recusado pelo Email.Of: o POST respondia 202 e o
        // handler falhava depois. Uma regra só, a do Email.Of.
        ValidationResult resultado = _validator.Validate(Valido() with { InitialAdminEmail = email });

        resultado.IsValid.Should().BeFalse();
    }

    [Fact]
    public void EmailDe254Caracteres_Passa()
    {
        string endereco =
            $"{new string('a', 64)}@{new string('b', 63)}.{new string('c', 63)}.{new string('d', 56)}.test";

        ValidationResult resultado = _validator.Validate(Valido() with { InitialAdminEmail = endereco });

        resultado.IsValid.Should().BeTrue();
    }

    [Fact]
    public void EmailInvalido_MensagemNaoEcoaOValor()
    {
        ValidationResult resultado = _validator.Validate(Valido() with { InitialAdminEmail = "segredo@@acme.com" });

        resultado.Errors.Should().NotBeEmpty();
        resultado.Errors.Select(falha => falha.ErrorMessage).Should().NotContain(mensagem =>
            mensagem.Contains("segredo", StringComparison.Ordinal));
    }
```

`tests/IdentityGateway.Application.UnitTests/Tenants/RegisterTenant/RegisterTenantCommandTests.cs`:

```csharp
using IdentityGateway.Application.Tenants.RegisterTenant;

namespace IdentityGateway.Application.UnitTests.Tenants.RegisterTenant;

public sealed class RegisterTenantCommandTests
{
    [Fact]
    public void ToString_NaoContemOEmailNemONome()
    {
        // O ToString gerado do record imprimiria todas as propriedades num log que formatasse o command (D15). O nome
        // do tenant fica de fora pela regra que já vale para os logs (ProvisioningLogs).
        RegisterTenantCommand comando = new("Acme Segredo", "acme", "free", "segredo@acme.test");

        string texto = comando.ToString();

        texto.Should().NotContain("segredo@acme.test")
            .And.NotContain("Acme Segredo")
            .And.Contain("acme");
    }
}
```

`tests/IdentityGateway.Api.FunctionalTests/RegisterTenantRequestTests.cs` (não usa a factory, não precisa de Docker):

```csharp
using IdentityGateway.Api.Modules;

namespace IdentityGateway.Api.FunctionalTests;

public sealed class RegisterTenantRequestTests
{
    [Fact]
    public void ToString_NaoContemOEmail()
    {
        RegisterTenantRequest corpo = new("Acme", "acme", "free", "segredo@acme.test");

        corpo.ToString().Should().NotContain("segredo@acme.test");
    }
}
```

- [ ] **Passo 3: Rodar e ver falhar**

Run: `dotnet build IdentityGateway.slnx`
Expected: FAIL de compilação — `'Email' does not contain a definition for 'IsValid'`.

(Só o build: os demais testes de `Email` não chegam a rodar antes de o método existir.)

- [ ] **Passo 4: Reescrever o `Email`**

`src/IdentityGateway.Domain/ValueObjects/Email.cs`, inteiro:

```csharp
using System.Text.RegularExpressions;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Errors;

namespace IdentityGateway.Domain.ValueObjects;

/// <summary>
/// Endereço de e-mail válido em forma.
/// </summary>
/// <remarks>
/// <para>
/// O tipo serve para que uma função que recebe <c>Email</c> não precise revalidar: se a instância existe,
/// passou pela checagem. É o ganho de substituir <c>string</c> por value object — a validação acontece uma
/// vez, na fronteira, em vez de espalhada em cada uso.
/// </para>
/// <para>
/// <b>A regra é conservadora de propósito, e é a única do sistema.</b> O validador do <c>POST /tenants</c> chama
/// <see cref="IsValid"/>, e o convite do admin usa o endereço como username no Keycloak. Por isso ela é o recorte que
/// os dois aceitam: parte local de até 64 caracteres (RFC 5321, e o limite do Keycloak) em letras minúsculas, dígitos,
/// ponto, sublinhado, <c>+</c> e hífen, sem ponto nas pontas nem repetido; domínio em rótulos de letras, dígitos e
/// hífen. Um endereço que a Gateway aceitasse e o Keycloak recusasse viraria um convite repetido pela janela inteira
/// do provisionamento. Afrouxar depois não quebra ninguém; apertar invalidaria o que já foi aceito.
/// </para>
/// <para>
/// Nenhuma validação sintática prova que o endereço existe — só o envio prova, e a confirmação de verdade fica
/// para o fluxo de convite.
/// </para>
/// <para>
/// <b>O endereço é dado pessoal (D15).</b> <see cref="ToString"/> não o devolve, para que nenhum log, mensagem de
/// exceção ou interpolação o carregue por acidente. Quem precisa do valor usa <see cref="Value"/>, e isso fica
/// visível em revisão.
/// </para>
/// </remarks>
public sealed partial class Email : ValueObject
{
    /// <summary>Limite prático de tamanho, conforme RFC 5321 — e o tamanho da coluna que o guarda.</summary>
    private const int TamanhoMaximo = 254;

    /// <summary>Limite da parte local, conforme RFC 5321 — e o do Keycloak (<c>EmailValidationUtil</c>).</summary>
    private const int TamanhoMaximoDaParteLocal = 64;

    private Email(string value) => Value = value;

    /// <summary>O endereço, normalizado em minúsculas.</summary>
    public string Value { get; }

    /// <summary>
    /// Cria um e-mail a partir do texto informado.
    /// </summary>
    /// <remarks>
    /// Normaliza para minúsculas porque a parte do domínio é insensível a caixa e, na prática, a parte
    /// local também é nos provedores reais — guardar <c>Joao@x.com</c> e <c>joao@x.com</c> como endereços
    /// distintos produziria cadastro duplicado do mesmo usuário. O Keycloak também grava em minúsculas, e a busca
    /// exata do convite depende de os dois lados concordarem.
    /// </remarks>
    public static Result<Email> Of(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result.Failure<Email>(DomainErrors.General.TextoObrigatorio(nameof(Email)));
        }

        string normalizado = value.Trim().ToLowerInvariant();

        if (normalizado.Length > TamanhoMaximo || !TemFormaDeEndereco(normalizado))
        {
            return Result.Failure<Email>(DomainErrors.Email.Invalido());
        }

        return new Email(normalizado);
    }

    /// <summary>Se o texto seria aceito por <see cref="Of"/>.</summary>
    /// <remarks>Existe para o validador do <c>POST</c> usar a mesma regra, em vez de reescrevê-la.</remarks>
    public static bool IsValid(string? value) => value is not null && Of(value).IsSuccess;

    /// <summary>
    /// Um <c>@</c> só, parte local e domínio dentro do recorte descrito no XML doc da classe.
    /// </summary>
    private static bool TemFormaDeEndereco(string valor)
    {
        int arroba = valor.IndexOf('@');

        if (arroba <= 0 || arroba != valor.LastIndexOf('@'))
        {
            return false;
        }

        string local = valor[..arroba];
        string dominio = valor[(arroba + 1)..];

        return local.Length <= TamanhoMaximoDaParteLocal
            && ParteLocal().IsMatch(local)
            && Dominio().IsMatch(dominio);
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <summary>Não devolve o endereço (D15): use <see cref="Value"/>.</summary>
    public override string ToString() => "Email(***)";

    // Átomos separados por ponto: sem ponto no início, no fim ou repetido. Os caracteres são os que o validador de
    // username do Keycloak aceita — o username do convidado é o próprio e-mail.
    [GeneratedRegex(@"^[a-z0-9_+-]+(\.[a-z0-9_+-]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ParteLocal();

    // Rótulos DNS: letras e dígitos nas pontas, hífen só no meio, até 63 cada, e pelo menos um ponto.
    [GeneratedRegex(
        @"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Dominio();
}
```

- [ ] **Passo 5: `DomainErrors.Email.Invalido()` sem o valor**

Em `DomainErrors.cs`, trocar o grupo `Email` por:

```csharp
    /// <summary>Erros do value object <c>Email</c>.</summary>
    public static class Email
    {
        /// <summary>Endereço fora do formato aceito.</summary>
        /// <remarks>
        /// Sem o valor na mensagem, ao contrário do slug: e-mail é dado pessoal, e a mensagem do erro vai para log e
        /// para o corpo do 400 (D15).
        /// </remarks>
        public static Error Invalido() => Error.Validation(
            "Email.Invalido",
            "O endereço de e-mail informado não é válido.");
    }
```

- [ ] **Passo 6: O validador usa a regra do `Email`**

`RegisterTenantValidator.cs`, inteiro:

```csharp
using FluentValidation;
using IdentityGateway.Domain.ValueObjects;

namespace IdentityGateway.Application.Tenants.RegisterTenant;

/// <summary>
/// Exige que os campos obrigatórios estejam presentes e bem formados.
/// </summary>
/// <remarks>
/// <para>
/// Cobre <b>presença</b>; a <b>forma</b> do slug é de <c>TenantSlug.Create</c>. A divisão evita a mesma regra
/// em dois lugares — e o validator recusa antes de o handler abrir transação, porque o
/// <c>ValidationBehavior</c> roda mais cedo no pipeline.
/// </para>
/// <para>
/// <b>A forma do e-mail é a do <see cref="Email.Of"/>, chamada por <see cref="Email.IsValid"/>.</b> O
/// <c>EmailAddress()</c> do FluentValidation aceitava <c>a@b</c>, que o <c>Email.Of</c> recusa: o <c>POST</c>
/// respondia 202 e o tenant falharia depois. A mensagem não ecoa o valor (D15).
/// </para>
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
            .Must(Email.IsValid)
            .WithMessage("O e-mail do administrador inicial não é válido.");
    }
}
```

- [ ] **Passo 7: `ToString` dos dois records sem o e-mail**

Em `RegisterTenantCommand.cs`, trocar a declaração (linhas 22-26) por:

```csharp
public sealed record RegisterTenantCommand(
    string Name,
    string Slug,
    string PlanCode,
    string InitialAdminEmail) : ICommand<TenantId>
{
    /// <summary>Sem o e-mail nem o nome: o <c>ToString</c> gerado do record os imprimiria em qualquer log (D15).</summary>
    public override string ToString() => $"RegisterTenantCommand {{ Slug = {Slug}, PlanCode = {PlanCode} }}";
}
```

(O `<remarks>` "validado e descartado" é reescrito na Tarefa 3, quando o e-mail passa a ser guardado.)

Em `RegisterTenantRequest.cs`, trocar a declaração (linhas 17-21) por:

```csharp
public sealed record RegisterTenantRequest(
    string Name,
    string Slug,
    string PlanCode,
    string InitialAdminEmail)
{
    /// <summary>Sem o e-mail: o <c>ToString</c> gerado do record o imprimiria em qualquer log (D15).</summary>
    public override string ToString() => $"RegisterTenantRequest {{ Slug = {Slug}, PlanCode = {PlanCode} }}";
}
```

- [ ] **Passo 8: Rodar e ver passar**

Run: `dotnet test tests/IdentityGateway.Domain.UnitTests --filter "FullyQualifiedName~EmailTests"`
Expected: PASS.

Run: `dotnet test tests/IdentityGateway.Application.UnitTests --filter "FullyQualifiedName~RegisterTenant"`
Expected: PASS.

Run: `dotnet test tests/IdentityGateway.Api.FunctionalTests --filter "FullyQualifiedName~RegisterTenantRequestTests"`
Expected: PASS, 1 teste.

- [ ] **Passo 9: 🧪 Prova por mutação**

1. `Email.ToString() => Value;` → `ToString_NaoContemOEndereco` vermelho. Reverter.
2. No validador, `.Must(Email.IsValid)` → `.EmailAddress()` (tirando o `WithMessage`) → `EmailInvalido_Falha("a@b")` vermelho. Reverter.
3. `Invalido()` voltando a receber e ecoar o valor (`$"'{valor}' não é..."`, com `Email.Of` passando `value`) → `ErroDeEnderecoInvalido_NaoEcoaOValor` vermelho. Reverter.
4. `TamanhoMaximoDaParteLocal = 65` → `Of_ParteLocalDe64_AceitaEDe65_Recusa` vermelho. Reverter.

Run: `git diff --stat` — só os arquivos desta tarefa.

- [ ] **Passo 10: Commit**

```bash
git add src/IdentityGateway.Domain src/IdentityGateway.Application src/IdentityGateway.Api/Modules/RegisterTenantRequest.cs tests/IdentityGateway.Domain.UnitTests tests/IdentityGateway.Application.UnitTests tests/IdentityGateway.Api.FunctionalTests/RegisterTenantRequestTests.cs
git commit -m "feat: regra unica de e-mail e endereco fora do ToString

Email.Of passa a ser a unica regra, usada tambem pelo validador do POST
(o EmailAddress aceitava a@b): parte local de ate 64 caracteres no
recorte que o username do Keycloak aceita, dominio em rotulos DNS.
ToString, o erro de validacao e os records do registro deixam de
carregar o endereco.

Mutacoes: ToString devolvendo Value, EmailAddress no validador, erro
ecoando o valor e limite local de 65 deixaram vermelhos os testes
correspondentes."
```

---

### Tarefa 2: Value objects e aggregate `Member`

**Arquivos:**
- Modify: `src/IdentityGateway.Domain/IdentityGateway.Domain.csproj`
- Create: `src/IdentityGateway.Domain/Members/MemberId.cs`
- Create: `src/IdentityGateway.Domain/Members/MemberStatus.cs`
- Create: `src/IdentityGateway.Domain/Members/ExternalUserId.cs`
- Create: `src/IdentityGateway.Domain/Members/RoleName.cs`
- Create: `src/IdentityGateway.Domain/Members/Member.cs`
- Create: `tests/IdentityGateway.Domain.UnitTests/Members/MemberIdTests.cs`
- Create: `tests/IdentityGateway.Domain.UnitTests/Members/ExternalUserIdTests.cs`
- Create: `tests/IdentityGateway.Domain.UnitTests/Members/RoleNameTests.cs`
- Create: `tests/IdentityGateway.Domain.UnitTests/Members/MemberTests.cs`
- Modify: `tests/IdentityGateway.ArchitectureTests/RegrasDeDominioTests.cs`

**Interfaces:**
- Consome: `TenantId`, `AggregateRoot<TId>`, `ValueObject`, `IAuditable` (existentes).
- Produz (namespace `IdentityGateway.Domain.Members`):
  - `public readonly record struct MemberId(Guid Value)` com `public static MemberId New()`.
  - `public enum MemberStatus { Invited, Active, Deactivated, Expired, Revoked, Erased }`.
  - `public sealed class ExternalUserId : ValueObject` — `public static ExternalUserId From(string value)` (lança `ArgumentException` se vazio), `string Value`, `ToString() => Value`.
  - `public sealed partial class RoleName : ValueObject` — `public static readonly RoleName TenantAdmin` (`"tenant-admin"`), `public static RoleName From(string value)`, `string Value`, `ToString() => Value`.
  - `public sealed class Member : AggregateRoot<MemberId>, IAuditable` — `TenantId TenantId`, `ExternalUserId ExternalUserId`, `MemberStatus Status`, `DateTimeOffset InvitedAt`, auditoria; `internal static Member Invite(TenantId tenantId, ExternalUserId externalUserId, DateTimeOffset invitedAt)`.

O projeto não expõe internals a ninguém hoje (conferido: `IdentityGateway.Domain.csproj` não tem `ItemGroup`). A
fábrica do `Member` é `internal` por decisão da spec (§4.2), então o teste de domínio precisa de
`InternalsVisibleTo` — o mesmo recurso que a Infrastructure já usa para os testes dela.

- [ ] **Passo 1: Escrever os testes dos value objects e do `Member`**

`tests/IdentityGateway.Domain.UnitTests/Members/MemberIdTests.cs`:

```csharp
using IdentityGateway.Domain.Members;

namespace IdentityGateway.Domain.UnitTests.Members;

public sealed class MemberIdTests
{
    [Fact]
    public void New_GeraIdentidadesDistintasEmVersao7()
    {
        MemberId um = MemberId.New();
        MemberId outro = MemberId.New();

        um.Should().NotBe(outro);
        um.Value.Version.Should().Be(7);
    }
}
```

`tests/IdentityGateway.Domain.UnitTests/Members/ExternalUserIdTests.cs`:

```csharp
using IdentityGateway.Domain.Members;

namespace IdentityGateway.Domain.UnitTests.Members;

public sealed class ExternalUserIdTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void From_Vazio_Lanca(string? valor)
    {
        // O sub vem do Keycloak, não de quem chama a API: vazio é defeito do adaptador, e defeito lança.
        Action criar = () => ExternalUserId.From(valor!);

        criar.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void From_GuardaOValorAparado()
    {
        ExternalUserId.From(" 9f1c-sub ").Value.Should().Be("9f1c-sub");
    }

    [Fact]
    public void DoisComOMesmoValor_SaoIguais()
    {
        ExternalUserId.From("abc").Should().Be(ExternalUserId.From("abc"));
    }
}
```

`tests/IdentityGateway.Domain.UnitTests/Members/RoleNameTests.cs`:

```csharp
using IdentityGateway.Domain.Members;

namespace IdentityGateway.Domain.UnitTests.Members;

public sealed class RoleNameTests
{
    [Fact]
    public void TenantAdmin_EONomeDoPapelDeRealm()
    {
        // O nome tem de bater com o papel declarado no realm (keycloak/bootstrap): é por ele que o adaptador o acha.
        RoleName.TenantAdmin.Value.Should().Be("tenant-admin");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Tenant-Admin")]
    [InlineData("-admin")]
    [InlineData("admin--x")]
    [InlineData("admin x")]
    public void From_ForaDoFormato_Lanca(string valor)
    {
        Action criar = () => RoleName.From(valor);

        criar.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void From_ComOMesmoNome_IgualAoCatalogado()
    {
        RoleName.From("tenant-admin").Should().Be(RoleName.TenantAdmin);
    }
}
```

`tests/IdentityGateway.Domain.UnitTests/Members/MemberTests.cs`:

```csharp
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Domain.UnitTests.Members;

/// <summary>
/// A fábrica do <see cref="Member"/> é interna: quem convida é o <c>Tenant</c>, que reserva a vaga antes.
/// </summary>
public sealed class MemberTests
{
    private static readonly DateTimeOffset Instante = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Invite_NasceInvitedComTenantESub()
    {
        TenantId tenant = TenantId.New();

        Member membro = Member.Invite(tenant, ExternalUserId.From("sub-1"), Instante);

        membro.TenantId.Should().Be(tenant);
        membro.ExternalUserId.Should().Be(ExternalUserId.From("sub-1"));
        membro.Status.Should().Be(MemberStatus.Invited);
        membro.InvitedAt.Should().Be(Instante);
        membro.DomainEvents.Should().BeEmpty("MemberInvited não tem consumidor nesta fatia (spec §4.2)");
    }

    [Fact]
    public void Invite_NormalizaInvitedAtParaUtc()
    {
        // Mesmo motivo do Tenant.RegisteredAt: o Npgsql recusa offset diferente de zero numa coluna timestamptz.
        DateTimeOffset emBrasilia = new(2026, 9, 29, 9, 0, 0, TimeSpan.FromHours(-3));

        Member membro = Member.Invite(TenantId.New(), ExternalUserId.From("sub-1"), emBrasilia);

        membro.InvitedAt.Offset.Should().Be(TimeSpan.Zero);
        membro.InvitedAt.Should().Be(emBrasilia);
    }

    [Fact]
    public void Invite_ComTenantIdVazio_Lanca()
    {
        Action convidar = () => Member.Invite(new TenantId(Guid.Empty), ExternalUserId.From("sub-1"), Instante);

        convidar.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Invite_SemSub_Lanca()
    {
        Action convidar = () => Member.Invite(TenantId.New(), null!, Instante);

        convidar.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Invite_GeraIdentidadesDistintas()
    {
        TenantId tenant = TenantId.New();

        Member um = Member.Invite(tenant, ExternalUserId.From("sub-1"), Instante);
        Member outro = Member.Invite(tenant, ExternalUserId.From("sub-2"), Instante);

        um.Id.Should().NotBe(outro.Id);
    }
}
```

Em `RegrasDeDominioTests.cs`, acrescentar o teste (e `using IdentityGateway.Domain.Members;` no topo):

```csharp
    /// <summary>
    /// Nenhum membro nasce sem a vaga reservada: só o <c>Tenant</c> cria um <see cref="Member"/>.
    /// </summary>
    /// <remarks>
    /// A fábrica é <c>internal</c> e o construtor é privado (spec §4.2). Um construtor ou uma fábrica pública
    /// permitiriam a um handler criar o membro direto, sem passar pela reserva de vaga do tenant — e a invariante de
    /// vagas deixaria de ter guardião sem nenhum teste de comportamento perceber.
    /// </remarks>
    [Fact]
    public void Member_NaoTemConstrutorNemFabricaPublicos()
    {
        Type member = typeof(Member);

        member.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Should().BeEmpty("o Member não pode ser construído fora do domínio");

        member.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(metodo => metodo.ReturnType == member)
            .Select(metodo => metodo.Name)
            .Should().BeEmpty("quem cria o Member é Tenant.CompleteProvisioning, que reserva a vaga antes");
    }
```

- [ ] **Passo 2: Rodar e ver falhar**

Run: `dotnet build IdentityGateway.slnx`
Expected: FAIL de compilação — `The type or namespace name 'Members' does not exist in the namespace 'IdentityGateway.Domain'`.

- [ ] **Passo 3: `InternalsVisibleTo` no Domain**

`IdentityGateway.Domain.csproj`, inteiro:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    Sem PropertyGroup: TargetFramework, Nullable e ImplicitUsings vêm do Directory.Build.props.
    Sem ProjectReference: o Domain não depende de nada — é o centro da arquitetura, e isso é
    travado por teste em IdentityGateway.ArchitectureTests (T0.3).
  -->

  <!--
    A fábrica do Member é internal: só o Tenant a chama, depois de reservar a vaga (spec da fatia C, §4.2). O teste
    de domínio precisa exercitá-la sozinha — as validações dela não são alcançáveis pelo Tenant, que nunca passa um
    TenantId vazio. Tornar a fábrica pública para o teste abriria a porta que ela existe para fechar.
  -->
  <ItemGroup>
    <InternalsVisibleTo Include="IdentityGateway.Domain.UnitTests" />
  </ItemGroup>

</Project>
```

- [ ] **Passo 4: Criar os value objects e o enum**

`src/IdentityGateway.Domain/Members/MemberId.cs`:

```csharp
namespace IdentityGateway.Domain.Members;

/// <summary>
/// Identidade de um membro.
/// </summary>
/// <remarks>
/// Tipada pelo mesmo motivo do <c>TenantId</c>: <c>GetAsync(TenantId, MemberId)</c> (M2) não pode aceitar os dois
/// trocados. Versão 7 pelo mesmo motivo também — ids em sequência ficam adjacentes no índice.
/// </remarks>
/// <param name="Value">O identificador.</param>
public readonly record struct MemberId(Guid Value)
{
    /// <summary>Gera uma identidade nova.</summary>
    public static MemberId New() => new(Guid.CreateVersion7());
}
```

`src/IdentityGateway.Domain/Members/MemberStatus.cs`:

```csharp
namespace IdentityGateway.Domain.Members;

/// <summary>
/// Estados do ciclo de vida de um membro (§6.1).
/// </summary>
/// <remarks>
/// Declara os seis estados da especificação, embora esta fatia só crie <see cref="Invited"/> — pelo mesmo motivo do
/// <c>TenantStatus</c>: uma fatia posterior não inventa nome diferente para um estado já nomeado. Persistido como
/// texto: a ordem dos membros não é dado de schema.
/// </remarks>
public enum MemberStatus
{
    /// <summary>Convidado; já ocupa vaga.</summary>
    Invited,

    /// <summary>Aceitou o convite (chega pela sincronização do ADR-007).</summary>
    Active,

    /// <summary>Desativado; a vaga foi liberada.</summary>
    Deactivated,

    /// <summary>O convite expirou sem aceite; a vaga foi liberada.</summary>
    Expired,

    /// <summary>O convite foi cancelado; a vaga foi liberada.</summary>
    Revoked,

    /// <summary>Apagado a pedido do titular. Terminal, sem dado que identifique a pessoa.</summary>
    Erased,
}
```

`src/IdentityGateway.Domain/Members/ExternalUserId.cs`:

```csharp
using IdentityGateway.Domain.Common;

namespace IdentityGateway.Domain.Members;

/// <summary>
/// Identificador do usuário no provedor de identidade — o <c>sub</c> (§11.3).
/// </summary>
/// <remarks>
/// <para>
/// É o que a Gateway guarda da pessoa: o nome e o e-mail ficam no Keycloak (§6). Value object, e não
/// <c>string</c>, para que um id de Organization ou um slug não entrem por engano onde se espera um usuário.
/// </para>
/// <para>
/// <b>Lança, e não devolve <c>Result</c>.</b> O valor vem do adaptador, nunca de quem chama a API: vazio é defeito
/// de integração, não resposta de negócio.
/// </para>
/// </remarks>
public sealed class ExternalUserId : ValueObject
{
    private ExternalUserId(string value) => Value = value;

    /// <summary>O id, como o provedor o devolveu (aparado).</summary>
    public string Value { get; }

    /// <summary>Cria o identificador a partir do id devolvido pelo provedor.</summary>
    /// <exception cref="ArgumentException">Se o valor for vazio ou só espaços.</exception>
    public static ExternalUserId From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return new ExternalUserId(value.Trim());
    }

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <summary>O id em texto. Não é dado pessoal: é um identificador opaco do provedor.</summary>
    public override string ToString() => Value;
}
```

`src/IdentityGateway.Domain/Members/RoleName.cs`:

```csharp
using System.Text.RegularExpressions;
using IdentityGateway.Domain.Common;

namespace IdentityGateway.Domain.Members;

/// <summary>
/// Nome de um papel do catálogo global (ADR-005).
/// </summary>
/// <remarks>
/// <para>
/// O papel vive no Keycloak, como papel de realm. Esta fatia só usa <see cref="TenantAdmin"/>, e o escolhe no
/// handler, não no adaptador (D11): escolher o papel é regra de negócio, e o M2 convida com outros.
/// </para>
/// <para>
/// Mesmo formato conservador do slug — minúsculas, dígitos e hífen isolado —, porque o nome aparece no token de
/// todos os usuários e nas policies das APIs consumidoras.
/// </para>
/// </remarks>
public sealed partial class RoleName : ValueObject
{
    /// <summary>Administrador de um tenant: o papel do admin inicial.</summary>
    public static readonly RoleName TenantAdmin = new("tenant-admin");

    private RoleName(string value) => Value = value;

    /// <summary>O nome, como está no realm.</summary>
    public string Value { get; }

    /// <summary>Cria o nome de um papel do catálogo.</summary>
    /// <exception cref="ArgumentException">Se o nome estiver fora do formato.</exception>
    public static RoleName From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (!FormaValida().IsMatch(value))
        {
            throw new ArgumentException(
                "Nome de papel fora do formato: minúsculas, dígitos e hífen isolado.", nameof(value));
        }

        return new RoleName(value);
    }

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <summary>O nome em texto.</summary>
    public override string ToString() => Value;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex FormaValida();
}
```

- [ ] **Passo 5: Criar o `Member`**

`src/IdentityGateway.Domain/Members/Member.cs`:

```csharp
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Domain.Members;

/// <summary>
/// Aggregate root do membro: o vínculo de um usuário do Keycloak com um tenant.
/// </summary>
/// <remarks>
/// <para>
/// <b>O mínimo que o convite do admin inicial exige</b> (spec da fatia C, §4.2): tenant, <c>sub</c>, status e
/// <c>InvitedAt</c>. O papel vive no Keycloak (ADR-005) e não é duplicado aqui nesta fatia; os papéis do catálogo e os
/// Permission Sets da §6.1 chegam no M2.
/// </para>
/// <para>
/// <b>Só o <see cref="Tenant"/> cria um membro</b>, por <see cref="Invite"/>, que é <c>internal</c>: é assim que
/// nenhum membro nasce sem a vaga reservada. Um teste de arquitetura garante que não há construtor nem fábrica
/// públicos.
/// </para>
/// <para>
/// Nenhum evento de domínio: <c>MemberInvited</c> ainda não teria consumidor.
/// </para>
/// </remarks>
public sealed class Member : AggregateRoot<MemberId>, IAuditable
{
    // Um construtor só, usado pela fábrica e pelo EF: todos os parâmetros são conversões de valor único, que o EF
    // vincula por nome — diferente do Tenant, cujo Plan é complex type.
    private Member(
        MemberId id,
        TenantId tenantId,
        ExternalUserId externalUserId,
        MemberStatus status,
        DateTimeOffset invitedAt)
        : base(id)
    {
        TenantId = tenantId;
        ExternalUserId = externalUserId;
        Status = status;
        InvitedAt = invitedAt;
    }

    /// <summary>Tenant ao qual o membro pertence.</summary>
    public TenantId TenantId { get; private set; }

    /// <summary>O <c>sub</c> do usuário no Keycloak.</summary>
    public ExternalUserId ExternalUserId { get; private set; }

    /// <summary>Estado no ciclo de vida.</summary>
    public MemberStatus Status { get; private set; }

    /// <summary>Quando o convite foi enviado, em UTC.</summary>
    public DateTimeOffset InvitedAt { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <inheritdoc />
    public Guid? CreatedBy { get; private set; }

    /// <inheritdoc />
    public Guid? UpdatedBy { get; private set; }

    /// <summary>
    /// Cria o membro convidado. Chamado só pelo <see cref="Tenant"/>, depois de reservar a vaga.
    /// </summary>
    /// <param name="tenantId">Tenant do membro; não pode ser vazio.</param>
    /// <param name="externalUserId">O <c>sub</c> devolvido pelo provedor.</param>
    /// <param name="invitedAt">Instante do convite; normalizado para UTC.</param>
    /// <exception cref="ArgumentException">Se o tenant for vazio.</exception>
    /// <exception cref="ArgumentNullException">Se o <c>sub</c> for nulo.</exception>
    internal static Member Invite(TenantId tenantId, ExternalUserId externalUserId, DateTimeOffset invitedAt)
    {
        if (tenantId.Value == Guid.Empty)
        {
            throw new ArgumentException("O membro precisa de um tenant.", nameof(tenantId));
        }

        ArgumentNullException.ThrowIfNull(externalUserId);

        return new Member(
            MemberId.New(), tenantId, externalUserId, MemberStatus.Invited, invitedAt.ToUniversalTime());
    }
}
```

As quatro propriedades de auditoria são escritas pelo `AuditableInterceptor` via `CurrentValue` do change tracker
(é assim que ele funciona com `private set`). Se o analisador reclamar de setter privado nunca atribuído
(`TreatWarningsAsErrors`), trocar por `{ get; private init; }` só nessas quatro — o interceptor não usa o setter.

- [ ] **Passo 6: Rodar e ver passar**

Run: `dotnet test tests/IdentityGateway.Domain.UnitTests --filter "FullyQualifiedName~Members"`
Expected: PASS.

Run: `dotnet test tests/IdentityGateway.ArchitectureTests`
Expected: PASS (inclui `Member_NaoTemConstrutorNemFabricaPublicos` e as regras existentes de identidade tipada e
de setter público, que passam a inspecionar o `Member`).

- [ ] **Passo 7: 🧪 Prova por mutação**

1. `internal static Member Invite` → `public static Member Invite` → `Member_NaoTemConstrutorNemFabricaPublicos` vermelho. Reverter.
2. Remover `.ToUniversalTime()` em `Invite` → `Invite_NormalizaInvitedAtParaUtc` vermelho. Reverter.
3. `AggregateRoot<MemberId>` → `AggregateRoot<Guid>` (e `MemberId.New()` → `Guid.CreateVersion7()` no `Invite`; o construtor recebendo `Guid id`) → `TodaRaizDeAgregadoTemIdentidadeTipada` vermelho. Reverter.

- [ ] **Passo 8: Commit**

```bash
git add src/IdentityGateway.Domain tests/IdentityGateway.Domain.UnitTests/Members tests/IdentityGateway.ArchitectureTests/RegrasDeDominioTests.cs
git commit -m "feat: aggregate Member e os value objects do convite

Member minimo (tenant, sub, status, InvitedAt e auditoria), com fabrica
internal: so o Tenant o cria, depois de reservar a vaga. ExternalUserId,
RoleName com tenant-admin, MemberId tipado em versao 7 e os seis estados
da secao 6.1.

Mutacoes: fabrica publica, InvitedAt sem UTC e identidade Guid crua
deixaram vermelhos a regra nova, o teste de UTC e a regra de identidade
tipada."
```

---

### Tarefa 3: `Tenant` — e-mail, vaga e `CompleteProvisioning`

**Arquivos:**
- Modify: `src/IdentityGateway.Domain/Tenants/Tenant.cs`
- Modify: `src/IdentityGateway.Application/Tenants/RegisterTenant/RegisterTenantHandler.cs`
- Modify: `src/IdentityGateway.Application/Tenants/RegisterTenant/RegisterTenantCommand.cs` (`<remarks>`)
- Modify: `src/IdentityGateway.Infrastructure/Persistence/Configurations/TenantConfiguration.cs` (um `Ignore` de transição, trocado pelo mapeamento na Tarefa 4)
- Test: `tests/IdentityGateway.Domain.UnitTests/Tenants/TenantTests.cs` (arquivo inteiro)
- Test: `tests/IdentityGateway.Application.UnitTests/Tenants/RegisterTenant/RegisterTenantHandlerTests.cs`
- Modify (chamadas a `Tenant.Register` e `MarkProvisioned`): `tests/IdentityGateway.Application.UnitTests/Tenants/ProvisionTenant/ProvisionTenantHandlerTests.cs`, `tests/IdentityGateway.Infrastructure.IntegrationTests/PostgresFixture.cs`, `Persistence/AtomicidadeDoRegistroTests.cs`, `Persistence/MapeamentoDeTenantTests.cs`, `Persistence/SchemaDeTenantsTests.cs`, `Persistence/TenantRepositoryTests.cs`, `Persistence/Outbox/OutboxProcessorTests.cs`

**Interfaces:**
- Consome: `Email` (Tarefa 1); `Member`, `ExternalUserId` (Tarefa 2).
- Produz:
  - `public static Tenant Register(string name, TenantSlug slug, Plan plan, Email initialAdminEmail, DateTimeOffset registeredAt)`
  - `public Email? Tenant.InitialAdminEmail { get; private set; }`
  - `public bool Tenant.HasSeatAvailable { get; }` — `OccupiedSeats < Plan.MaxUsers`
  - `public Member Tenant.CompleteProvisioning(string externalOrganizationId, ExternalUserId adminUserId, DateTimeOffset invitedAt)`
  - `MarkProvisioningFailed()` passa a apagar `InitialAdminEmail`.
  - `public static Email PostgresFixture.EmailDoAdmin()` (teste) — e-mail único com `+`.
  - `MarkProvisioned` **continua** até a Tarefa 9 (ver "Desvios deliberados").

Os usos de `Register` e `MarkProvisioned` na suíte, conferidos com
`grep -rn "Tenant.Register(\|MarkProvisioned" src tests --include=*.cs`: `Tenant.cs`, `RegisterTenantHandler.cs`,
`ProvisionTenantHandler.cs` (fica para a Tarefa 9), `TenantTests.cs` (19), `ProvisionTenantHandlerTests.cs` (2),
`AtomicidadeDoRegistroTests.cs` (3), `MapeamentoDeTenantTests.cs` (2), `OutboxProcessorTests.cs` (2),
`SchemaDeTenantsTests.cs` (2), `TenantRepositoryTests.cs` (4). A spec (§5.4) não lista o `OutboxProcessorTests`
entre os que usam `MarkProvisioned` — ele só chama `Register`.

- [ ] **Passo 1: Reescrever os testes do `Tenant`**

`tests/IdentityGateway.Domain.UnitTests/Tenants/TenantTests.cs`, inteiro:

```csharp
using System.Text.Json;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Domain.Tenants.Events;
using IdentityGateway.Domain.ValueObjects;

namespace IdentityGateway.Domain.UnitTests.Tenants;

public sealed class TenantTests
{
    private static readonly DateTimeOffset Instante = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Convite = new(2026, 9, 25, 12, 5, 0, TimeSpan.Zero);
    private static readonly ExternalUserId Sub = ExternalUserId.From("sub-admin");

    private static Email EmailDoAdmin() => Email.Of("admin@acme.test").Value;

    private static Tenant TenantRegistrado(int maxUsers = 10) =>
        Tenant.Register(
            "Acme", TenantSlug.Create("acme").Value, new Plan(PlanTier.Standard, maxUsers, 5), EmailDoAdmin(), Instante);

    // Ativo pelo caminho de produção: a ativação ocupa a vaga do admin inicial.
    private static Tenant TenantAtivo(int maxUsers = 10)
    {
        Tenant tenant = TenantRegistrado(maxUsers);
        tenant.CompleteProvisioning("org-externa-1", Sub, Convite);
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
            .Which.Slug.Should().Be("acme");
    }

    [Fact]
    public void Register_GeraIdentidadesDistintas()
    {
        TenantRegistrado().Id.Should().NotBe(TenantRegistrado().Id);
    }

    [Fact]
    public void Register_GuardaOEmailNormalizado()
    {
        // Foco de revisão 1: o que vai para a coluna é o endereço normalizado, o mesmo que a busca exata do Keycloak
        // compara.
        var tenant = Tenant.Register(
            "Acme", SlugValido(), PlanoPadrao(), Email.Of("  Admin@Acme.COM ").Value, Instante);

        tenant.InitialAdminEmail!.Value.Should().Be("admin@acme.com");
    }

    [Fact]
    public void Register_ComEmailNulo_Lanca()
    {
        // Sem o e-mail o tenant nasceria trancado: ninguém seria convidado como tenant-admin (§9.1).
        Action registrar = () => Tenant.Register("Acme", SlugValido(), PlanoPadrao(), null!, Instante);

        registrar.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Register_TenantRegisteredNaoCarregaOEmail()
    {
        // D1: o evento vira mensagem no Outbox e, com o broker, no RabbitMQ. O e-mail fica só na coluna.
        Tenant tenant = TenantRegistrado();
        IDomainEvent evento = tenant.DomainEvents.Single();

        string json = JsonSerializer.Serialize(evento, evento.GetType());

        json.Should().NotContain("admin@acme.test");
    }

    [Fact]
    public void HasSeatAvailable_ComVaga_Verdadeiro()
    {
        TenantRegistrado(maxUsers: 1).HasSeatAvailable.Should().BeTrue();
    }

    [Fact]
    public void HasSeatAvailable_ComPlanoSemVagas_Falso()
    {
        // O catálogo recusa maxUsers < 1 na subida, mas o Plan gravado no tenant pode ser antigo (D14).
        TenantRegistrado(maxUsers: 0).HasSeatAvailable.Should().BeFalse();
    }

    [Fact]
    public void CompleteProvisioning_AtivaOcupaUmaVagaCriaOMemberEApagaOEmail()
    {
        Tenant tenant = TenantRegistrado();

        Member admin = tenant.CompleteProvisioning("org-externa-1", Sub, Convite);

        tenant.Status.Should().Be(TenantStatus.Active);
        tenant.ExternalOrganizationId.Should().Be("org-externa-1");
        tenant.OccupiedSeats.Should().Be(1, "a vaga do admin é reservada na ativação, exatamente uma (D2)");
        tenant.InitialAdminEmail.Should().BeNull("o e-mail só existe em tenant Pending (D1)");

        admin.TenantId.Should().Be(tenant.Id);
        admin.ExternalUserId.Should().Be(Sub);
        admin.Status.Should().Be(MemberStatus.Invited);
        admin.InvitedAt.Should().Be(Convite);
    }

    [Fact]
    public void CompleteProvisioning_LevantaTenantActivatedUmaVez()
    {
        Tenant tenant = TenantRegistrado();

        tenant.CompleteProvisioning("org-externa-1", Sub, Convite);

        tenant.DomainEvents.OfType<TenantActivated>().Should().ContainSingle();
    }

    [Fact]
    public void CompleteProvisioning_NormalizaInvitedAtParaUtc()
    {
        Tenant tenant = TenantRegistrado();
        DateTimeOffset emBrasilia = new(2026, 9, 25, 9, 5, 0, TimeSpan.FromHours(-3));

        Member admin = tenant.CompleteProvisioning("org-externa-1", Sub, emBrasilia);

        admin.InvitedAt.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void CompleteProvisioning_ComTenantAtivo_Lanca()
    {
        // Inversão deliberada em relação ao MarkProvisioned, que aceitava Active: uma segunda ativação criaria um
        // segundo Member e ocuparia uma segunda vaga. Quem entrega a mensagem repetida é o handler, que só age em
        // Pending.
        Tenant tenant = TenantAtivo();

        Action ativar = () => tenant.CompleteProvisioning("org-externa-1", Sub, Convite);

        ativar.Should().Throw<DomainInvariantViolation>();
        tenant.OccupiedSeats.Should().Be(1);
    }

    [Fact]
    public void CompleteProvisioning_ComTenantEmProvisioningFailed_Lanca()
    {
        // O MarkProvisioned aceitava sair de ProvisioningFailed; o retry manual, quando existir, devolve o tenant a
        // Pending antes (§6.2).
        Tenant tenant = TenantRegistrado();
        tenant.MarkProvisioningFailed();

        Action ativar = () => tenant.CompleteProvisioning("org-externa-1", Sub, Convite);

        ativar.Should().Throw<DomainInvariantViolation>();
    }

    [Fact]
    public void CompleteProvisioning_SemVaga_LancaSemTerMudadoNada()
    {
        // O handler verifica a vaga antes (D14); chegar aqui sem vaga é defeito. E o defeito não pode deixar o
        // agregado pela metade: nada muda antes de tudo ser validado.
        Tenant tenant = TenantRegistrado(maxUsers: 0);

        Action ativar = () => tenant.CompleteProvisioning("org-externa-1", Sub, Convite);

        ativar.Should().Throw<DomainInvariantViolation>();
        tenant.Status.Should().Be(TenantStatus.Pending);
        tenant.ExternalOrganizationId.Should().BeNull();
        tenant.OccupiedSeats.Should().Be(0);
        tenant.InitialAdminEmail.Should().NotBeNull();
        tenant.DomainEvents.OfType<TenantActivated>().Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CompleteProvisioning_ComIdExternoVazio_LancaSemMudarNada(string idExterno)
    {
        // Sem esta guarda o tenant iria a Active com id externo em branco — indistinguível de "não provisionado" para
        // o job de reconciliação.
        Tenant tenant = TenantRegistrado();

        Action ativar = () => tenant.CompleteProvisioning(idExterno, Sub, Convite);

        ativar.Should().Throw<ArgumentException>();
        tenant.Status.Should().Be(TenantStatus.Pending);
        tenant.OccupiedSeats.Should().Be(0);
    }

    [Fact]
    public void CompleteProvisioning_SemSub_LancaSemMudarNada()
    {
        Tenant tenant = TenantRegistrado();

        Action ativar = () => tenant.CompleteProvisioning("org-externa-1", null!, Convite);

        ativar.Should().Throw<ArgumentNullException>();
        tenant.Status.Should().Be(TenantStatus.Pending);
        tenant.OccupiedSeats.Should().Be(0);
    }

    // ── MarkProvisioned: removido na Tarefa 9, com estes cinco testes ──

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
        Tenant tenant = TenantRegistrado();

        tenant.MarkProvisioned("org-externa-1");
        tenant.MarkProvisioned("org-externa-1");

        tenant.DomainEvents.OfType<TenantActivated>().Should().ContainSingle();
    }

    [Fact]
    public void MarkProvisioned_APartirDeProvisioningFailed_Ativa()
    {
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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void MarkProvisioned_ComIdExternoVazio_Lanca(string idExterno)
    {
        Tenant tenant = TenantRegistrado();

        Action provisionar = () => tenant.MarkProvisioned(idExterno);

        provisionar.Should().Throw<ArgumentException>();
        tenant.Status.Should().Be(TenantStatus.Pending);
    }

    // ── fim dos testes do MarkProvisioned ──

    [Fact]
    public void MarkProvisioningFailed_APartirDePending_MarcaFalha()
    {
        Tenant tenant = TenantRegistrado();

        tenant.MarkProvisioningFailed();

        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
    }

    [Fact]
    public void MarkProvisioningFailed_ApagaOEmail()
    {
        // D13: ProvisioningFailed não tem saída automática, e guardar o e-mail ali seria retenção sem prazo. O retry
        // manual recebe o e-mail de novo.
        Tenant tenant = TenantRegistrado();

        tenant.MarkProvisioningFailed();

        tenant.InitialAdminEmail.Should().BeNull();
    }

    [Fact]
    public void MarkProvisioningFailed_Repetido_NaoLanca()
    {
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
        tenant.OccupiedSeats.Should().Be(2, "a primeira vaga é do admin inicial");
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

        tenant.OccupiedSeats.Should().Be(1);
    }

    [Fact]
    public void ReleaseSeat_SemVagaOcupada_Lanca()
    {
        // NÃO é idempotente por desenho: um clamp em zero esconderia dupla liberação. Ver o XML doc do método.
        Tenant tenant = TenantAtivo();
        tenant.ReleaseSeat();

        Action liberar = tenant.ReleaseSeat;

        liberar.Should().Throw<DomainInvariantViolation>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_ComNomeVazio_Lanca(string nome)
    {
        Action registrar = () => Tenant.Register(nome, SlugValido(), PlanoPadrao(), EmailDoAdmin(), Instante);

        registrar.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Register_ComSlugNulo_Lanca()
    {
        Action registrar = () => Tenant.Register("Acme", null!, PlanoPadrao(), EmailDoAdmin(), Instante);

        registrar.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Register_ComPlanoNulo_Lanca()
    {
        Action registrar = () => Tenant.Register("Acme", SlugValido(), null!, EmailDoAdmin(), Instante);

        registrar.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Register_ComNomeCercadoDeEspacos_Apara()
    {
        var tenant = Tenant.Register("  Acme Corp  ", SlugValido(), PlanoPadrao(), EmailDoAdmin(), Instante);

        tenant.Name.Should().Be("Acme Corp");
    }

    [Fact]
    public void Register_GuardaOInstanteDoRegistro()
    {
        var tenant = Tenant.Register("Acme", SlugValido(), PlanoPadrao(), EmailDoAdmin(), Instante);

        tenant.RegisteredAt.Should().Be(Instante);
    }

    [Fact]
    public void Register_NormalizaOInstanteParaUtc()
    {
        DateTimeOffset emBrasilia = new(2026, 9, 25, 9, 0, 0, TimeSpan.FromHours(-3));

        var tenant = Tenant.Register("Acme", SlugValido(), PlanoPadrao(), EmailDoAdmin(), emBrasilia);

        tenant.RegisteredAt.Offset.Should().Be(TimeSpan.Zero);
        tenant.RegisteredAt.Should().Be(emBrasilia);
    }

    private static TenantSlug SlugValido() => TenantSlug.Create("acme").Value;

    private static Plan PlanoPadrao() => new(PlanTier.Standard, maxUsers: 10, maxClients: 5);
}
```

- [ ] **Passo 2: Escrever os testes do handler de registro**

Em `RegisterTenantHandlerTests.cs`, acrescentar (e `using IdentityGateway.Domain.ValueObjects;` não é necessário —
o teste lê `InitialAdminEmail!.Value`):

```csharp
    [Fact]
    public async Task ComandoValido_GuardaOEmailNormalizadoNoTenant()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant? capturado = null;
        _repositorio.Add(Arg.Do<Tenant>(tenant => capturado = tenant));

        await _handler.Handle(new RegisterTenantCommand("Acme", "acme", "free", "  Admin@Acme.COM "), ct);

        capturado!.InitialAdminEmail!.Value.Should().Be("admin@acme.com");
    }

    [Fact]
    public async Task EmailInvalido_DevolveValidationSemRegistrar()
    {
        // O validador recusa antes, no pipeline; o handler não confia nisso sozinho, porque o command pode chegar por
        // outro caminho (um teste, um job) que não passe pelo ValidationBehavior.
        CancellationToken ct = TestContext.Current.CancellationToken;

        Result<TenantId> resultado = await _handler.Handle(
            new RegisterTenantCommand("Acme", "acme", "free", "a@b"), ct);

        resultado.IsFailure.Should().BeTrue();
        resultado.Error.Code.Should().Be("Email.Invalido");
        _repositorio.DidNotReceive().Add(Arg.Any<Tenant>());
    }
```

- [ ] **Passo 3: Rodar e ver falhar**

Run: `dotnet build IdentityGateway.slnx`
Expected: FAIL de compilação — `No overload for method 'Register' takes 5 arguments` e
`'Tenant' does not contain a definition for 'CompleteProvisioning'` (e as chamadas antigas de 4 argumentos
quebram depois do Passo 4).

- [ ] **Passo 4: Implementar no `Tenant`**

Em `Tenant.cs`:

1. `using IdentityGateway.Domain.Members;` e `using IdentityGateway.Domain.ValueObjects;` no topo.
2. O resumo da classe passa a ser:

```csharp
/// <summary>
/// Aggregate root do tenant. Guarda somente dados de governança; identidade e credenciais vivem no
/// Keycloak — com uma exceção declarada e temporária, o e-mail do admin inicial (<see cref="InitialAdminEmail"/>).
/// </summary>
```

3. O construtor de domínio (hoje nas linhas 57-65) passa a ser:

```csharp
    private Tenant(
        TenantId id, string name, TenantSlug slug, Plan plan, Email initialAdminEmail, DateTimeOffset registeredAt)
        : base(id)
    {
        Name = name;
        Slug = slug;
        Plan = plan;
        Status = TenantStatus.Pending;
        RegisteredAt = registeredAt;
        InitialAdminEmail = initialAdminEmail;
    }
```

O construtor do EF (`Tenant(TenantId id, string name, TenantSlug slug)`) **não muda**: o EF preenche o e-mail pelo
setter privado, como já faz com `Status`.

4. Depois de `OverSubscribed`, as duas propriedades novas:

```csharp
    /// <summary>E-mail do primeiro administrador, só enquanto o tenant está em <see cref="TenantStatus.Pending"/>.</summary>
    /// <remarks>
    /// <para>
    /// <b>Exceção declarada à regra "dados pessoais só no Keycloak" (D1).</b> O convite acontece depois do
    /// <c>POST</c>, no consumidor do Outbox, e o endereço precisa esperar em algum lugar. Fora do evento
    /// <c>TenantRegistered</c>, que o levaria ao Outbox e, com o broker, ao RabbitMQ.
    /// </para>
    /// <para>
    /// Apagado na mesma operação que ativa (<see cref="CompleteProvisioning"/>) <b>ou</b> que marca a falha
    /// (<see cref="MarkProvisioningFailed"/>, D13). Nulo também em tenant registrado antes da fatia C — o handler trata
    /// esse caso como falha permanente.
    /// </para>
    /// </remarks>
    public Email? InitialAdminEmail { get; private set; }

    /// <summary>Se ainda cabe um membro no plano.</summary>
    /// <remarks>
    /// Leitura, para o provisionamento recusar antes de tocar o Keycloak um tenant cujo plano não comporta o admin
    /// (D14) — o <c>Plan</c> gravado pode ser anterior à regra do catálogo que exige <c>maxUsers</c> de pelo menos 1.
    /// </remarks>
    public bool HasSeatAvailable => OccupiedSeats < Plan.MaxUsers;
```

5. `Register`:

```csharp
    /// <param name="name">Nome de exibição.</param>
    /// <param name="slug">Slug já validado.</param>
    /// <param name="plan">Plano vindo do catálogo.</param>
    /// <param name="initialAdminEmail">E-mail de quem será convidado como <c>tenant-admin</c>.</param>
    /// <param name="registeredAt">Instante do registro; normalizado para UTC.</param>
    /// <returns>O tenant em <see cref="TenantStatus.Pending"/>.</returns>
    public static Tenant Register(
        string name, TenantSlug slug, Plan plan, Email initialAdminEmail, DateTimeOffset registeredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(initialAdminEmail);

        // O nome é aparado, mas não tem a caixa alterada, e a diferença em relação ao slug é deliberada:
        // o slug é identificador — `Acme` e `acme` seriam o mesmo tenant e precisam colidir —, enquanto o
        // nome é texto de exibição, e "IBM" não pode virar "ibm". Aparar o entorno resolve o espaço colado
        // sem tocar no que o cliente escolheu se chamar.
        Tenant tenant = new(
            TenantId.New(), name.Trim(), slug, plan, initialAdminEmail, registeredAt.ToUniversalTime());

        // O evento carrega só o id e o slug: o e-mail fica na coluna (D1).
        tenant.RaiseDomainEvent(new TenantRegistered(tenant.Id, slug.Value));

        return tenant;
    }
```

6. Logo depois de `Register`, o método novo:

```csharp
    /// <summary>
    /// Conclui o provisionamento: ativa o tenant, ocupa a vaga do admin inicial e devolve o <see cref="Member"/> dele.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Uma operação de domínio, num commit só (D2).</b> No Keycloak o convite já aconteceu; aqui, <c>Active</c>, a
    /// vaga, o membro em <c>Invited</c> e o e-mail apagado acontecem juntos ou não acontecem.
    /// </para>
    /// <para>
    /// <b>Valida tudo antes de mudar qualquer coisa.</b> Exige <see cref="TenantStatus.Pending"/> e vaga livre, e cria o
    /// membro — que valida os próprios argumentos — antes da primeira atribuição. Uma exceção nunca deixa o agregado
    /// pela metade.
    /// </para>
    /// <para>
    /// <b>Recusa <c>Active</c> e <c>ProvisioningFailed</c></b>, ao contrário do <c>MarkProvisioned</c> que substitui:
    /// uma segunda ativação criaria um segundo membro. A mensagem repetida é tratada pelo handler, que só age em
    /// <c>Pending</c>.
    /// </para>
    /// </remarks>
    /// <param name="externalOrganizationId">Id da Organization no Keycloak.</param>
    /// <param name="adminUserId">O <c>sub</c> do admin convidado.</param>
    /// <param name="invitedAt">Instante do convite; normalizado para UTC.</param>
    /// <returns>O membro do admin, para o handler entregar ao repositório.</returns>
    /// <exception cref="DomainInvariantViolation">
    /// Fora de <c>Pending</c>, ou sem vaga — o handler verifica a vaga antes (D14), então chegar aqui sem ela é defeito.
    /// </exception>
    public Member CompleteProvisioning(
        string externalOrganizationId, ExternalUserId adminUserId, DateTimeOffset invitedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalOrganizationId);
        ArgumentNullException.ThrowIfNull(adminUserId);

        EnsureStatusIn(TenantStatus.Pending);

        if (!HasSeatAvailable)
        {
            throw new DomainInvariantViolation(
                $"Tenant {Id.Value}: ativação sem vaga para o admin inicial; o provisionamento verifica antes.");
        }

        Member admin = Member.Invite(Id, adminUserId, invitedAt);

        ExternalOrganizationId = externalOrganizationId;
        Status = TenantStatus.Active;
        OcuparVaga();
        InitialAdminEmail = null;

        RaiseDomainEvent(new TenantActivated(Id));

        return admin;
    }
```

7. `MarkProvisioningFailed` passa a ser:

```csharp
    /// <summary>
    /// Marca que o provisionamento falhou: erro permanente, ou janela de retry esgotada.
    /// </summary>
    /// <remarks>
    /// O tenant fica aguardando o retry manual, que o devolverá a <c>Pending</c> antes de reenfileirar (§6.2). <b>Apaga
    /// o e-mail do admin (D13):</b> este estado não tem saída automática, e guardá-lo seria retenção sem prazo; o retry
    /// manual recebe o e-mail de novo, o que também permite corrigir um endereço digitado errado. Não levanta evento —
    /// nada reage à falha hoje. Idempotente, porque a mesma mensagem pode ser reentregue.
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
        InitialAdminEmail = null;
    }
```

8. Depois de `ReleaseSeat`, o método privado:

```csharp
    /// <summary>
    /// Ocupa uma vaga sem exigir <c>Active</c>.
    /// </summary>
    /// <remarks>
    /// Existe para <see cref="CompleteProvisioning"/>, que ocupa a vaga do admin no mesmo passo em que ativa.
    /// <see cref="ReserveSeat"/> continua exigindo <c>Active</c> para todo outro chamador (D2): afrouxá-lo enfraqueceria
    /// a invariante para quem vier depois.
    /// </remarks>
    private void OcuparVaga() => OccupiedSeats++;
```

O `MarkProvisioned` e o XML doc dele ficam como estão até a Tarefa 9.

- [ ] **Passo 5: Implementar no handler de registro**

Em `RegisterTenantHandler.cs`, `using IdentityGateway.Domain.ValueObjects;` no topo e, logo depois do bloco que
valida o slug (antes do `SlugExistsAsync`):

```csharp
        // Validação barata antes da consulta ao banco. O validador do pipeline já recusou o e-mail malformado; aqui é
        // a defesa de quem envia o command por outro caminho.
        Result<Email> email = Email.Of(command.InitialAdminEmail);

        if (email.IsFailure)
        {
            return Result.Failure<TenantId>(email.Error);
        }
```

e a criação passa a ser:

```csharp
        var tenant = Tenant.Register(command.Name, slug.Value, plano, email.Value, relogio.UtcNow);
```

Em `RegisterTenantCommand.cs`, o segundo parágrafo do `<remarks>` (hoje "Nesta versão o campo é validado e
descartado…") passa a ser:

```csharp
/// <para>
/// <b>O e-mail fica no tenant até a ativação (fatia C, D1)</b>, fora do evento <c>TenantRegistered</c>: o evento vai
/// para o Outbox e, com o broker, para o RabbitMQ, contra a regra de dados pessoais só no Keycloak. A coluna é
/// apagada na transação que ativa o tenant ou que o marca <c>ProvisioningFailed</c>.
/// </para>
```

- [ ] **Passo 6: `Ignore` de transição no mapeamento**

Sem ele, o EF tentaria mapear `Email` como entidade (sem chave) e todo teste de integração quebraria até a Tarefa
4. Em `TenantConfiguration.cs`, antes do `builder.Ignore(tenant => tenant.DomainEvents);`:

```csharp
        // Transição: a coluna initial_admin_email e a migration chegam na Tarefa 4 do plano da fatia C, que troca
        // este Ignore pelo mapeamento.
        builder.Ignore(tenant => tenant.InitialAdminEmail);
```

- [ ] **Passo 7: Atualizar as chamadas dos testes**

1. `PostgresFixture.cs`: `using IdentityGateway.Domain.ValueObjects;` e, depois de `Usuario`:

```csharp
    /// <summary>E-mail de admin único, com <c>+</c>: o mesmo formato dos testes contra o Keycloak.</summary>
    public static Email EmailDoAdmin() => Email.Of($"admin+{Guid.NewGuid():N}@acme.test").Value;
```

2. Em cada `Tenant.Register(...)` de `AtomicidadeDoRegistroTests.cs` (3), `MapeamentoDeTenantTests.cs` (2),
`SchemaDeTenantsTests.cs` (2), `TenantRepositoryTests.cs` (2) e `OutboxProcessorTests.cs` (2), acrescentar
`PostgresFixture.EmailDoAdmin(), ` antes de `PostgresFixture.Agora`.

3. Em `TenantRepositoryTests.GetAsync_DevolveOTenantRastreado`, trocar `lido!.MarkProvisioned("org-get");` por:

```csharp
            lido!.CompleteProvisioning("org-get", ExternalUserId.From("sub-get"), PostgresFixture.Agora);
```

(com `using IdentityGateway.Domain.Members;`). O `Member` devolvido não é adicionado ao contexto: o teste prova só
que o tenant rastreado persiste.

4. Em `ProvisionTenantHandlerTests.TenantPendente`, trocar a criação por:

```csharp
        var tenant = Tenant.Register(
            "Acme", TenantSlug.Create("acme").Value, new Plan(PlanTier.Free, 5, 1),
            Email.Of("admin@acme.test").Value, Registro);
```

(com `using IdentityGateway.Domain.ValueObjects;`). O `tenant.MarkProvisioned("org-1")` de `TenantJaAtivo_…` fica
até a Tarefa 9.

Conferir que não sobrou chamada de quatro argumentos:

Run: `grep -rn "Tenant.Register(" tests src --include=*.cs | grep -v "/obj/" | grep -v "Email\|email"`
Expected: nenhuma linha.

- [ ] **Passo 8: Rodar e ver passar**

Run: `dotnet build IdentityGateway.slnx`
Expected: 0 avisos, 0 erros.

Run: `dotnet test tests/IdentityGateway.Domain.UnitTests tests/IdentityGateway.Application.UnitTests`
Expected: PASS.

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter "FullyQualifiedName~Persistence"`
Expected: PASS (Docker ligado).

- [ ] **Passo 9: 🧪 Prova por mutação**

1. Comentar `OcuparVaga();` em `CompleteProvisioning` → `CompleteProvisioning_AtivaOcupaUmaVagaCriaOMemberEApagaOEmail` vermelho (`OccupiedSeats` 0). Reverter.
2. Mover o bloco de atribuições (`ExternalOrganizationId = …` até `InitialAdminEmail = null;`) para **antes** do `if (!HasSeatAvailable)` → `CompleteProvisioning_SemVaga_LancaSemTerMudadoNada` vermelho. Reverter.
3. Remover `InitialAdminEmail = null;` de `CompleteProvisioning` → teste de ativação vermelho; remover de `MarkProvisioningFailed` → `MarkProvisioningFailed_ApagaOEmail` vermelho. Reverter os dois.
4. `EnsureStatusIn(TenantStatus.Pending)` → `EnsureStatusIn(TenantStatus.Pending, TenantStatus.ProvisioningFailed)` → `CompleteProvisioning_ComTenantEmProvisioningFailed_Lanca` vermelho. Reverter.

- [ ] **Passo 10: Commit**

```bash
git add src tests
git commit -m "feat: e-mail do admin no tenant e ativacao com vaga e Member

Register exige o e-mail, que fica no tenant so enquanto Pending (fora
do evento). CompleteProvisioning valida estado e vaga antes de mudar
qualquer coisa e, numa operacao, ativa, ocupa a vaga do admin, cria o
Member em Invited e apaga o e-mail; recusa Active e ProvisioningFailed.
MarkProvisioningFailed tambem apaga o e-mail. MarkProvisioned sai quando
o handler deixar de usa-lo.

Mutacoes: sem ocupar a vaga, mudanca antes da validacao, e-mail nao
apagado (ativacao e falha) e ProvisioningFailed aceito deixaram
vermelhos os testes de dominio correspondentes."
```

---

### Tarefa 4: Persistência

**Arquivos:**
- Modify: `src/IdentityGateway.Infrastructure/Persistence/Configurations/TenantConfiguration.cs`
- Create: `src/IdentityGateway.Infrastructure/Persistence/Configurations/MemberConfiguration.cs`
- Modify: `src/IdentityGateway.Infrastructure/Persistence/AppDbContext.cs`
- Create: `src/IdentityGateway.Application/Common/Abstractions/IMemberRepository.cs`
- Create: `src/IdentityGateway.Infrastructure/Persistence/Repositories/MemberRepository.cs`
- Modify: `src/IdentityGateway.Infrastructure/DependencyInjection.cs` (`AddPersistencia`)
- Create (gerados): `src/IdentityGateway.Infrastructure/Persistence/Migrations/<timestamp>_ConviteDoAdminInicial.cs`, `.Designer.cs`; modify `AppDbContextModelSnapshot.cs`
- Test: `tests/IdentityGateway.Infrastructure.IntegrationTests/Persistence/MapeamentoDeTenantTests.cs`
- Test: `tests/IdentityGateway.Infrastructure.IntegrationTests/Persistence/SchemaDeTenantsTests.cs`
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Persistence/MapeamentoDeMemberTests.cs`
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Persistence/SchemaDeMembersTests.cs`
- Test: `tests/IdentityGateway.Infrastructure.IntegrationTests/DependencyInjectionTests.cs`

**Interfaces:**
- Consome: `Tenant.InitialAdminEmail`, `Tenant.CompleteProvisioning` (Tarefa 3); `Member` (Tarefa 2).
- Produz:
  - `public interface IMemberRepository { void Add(Member member); }` (Application)
  - `internal sealed class MemberRepository(AppDbContext context) : IMemberRepository`
  - `internal DbSet<Member> AppDbContext.Members`
  - Coluna `tenants.initial_admin_email`, tabela `members` e índice `ix_members_tenant_id_external_user_id`.

A migration é gerada com a própria Infrastructure como projeto de startup: o `AppDbContextFactory` a torna
autossuficiente, e o comando foi conferido neste repositório com
`dotnet ef migrations has-pending-model-changes --project src/IdentityGateway.Infrastructure --startup-project src/IdentityGateway.Infrastructure`
("No changes have been made to the model since the last migration"). Não é preciso a referência temporária ao
`Microsoft.EntityFrameworkCore.Design` na Api que a fatia B usou.

- [ ] **Passo 1: Escrever os testes de mapeamento e de schema**

Em `MapeamentoDeTenantTests.cs` (com `using IdentityGateway.Domain.Members;` e
`using IdentityGateway.Domain.ValueObjects;`):

1. Em `Tenant_SobreviveAoRoundTrip`, depois de `lido.RegisteredAt…`:

```csharp
        lido.InitialAdminEmail.Should().Be(original.InitialAdminEmail);
```

2. Testes novos:

```csharp
    [Fact]
    public async Task EmailDe254Caracteres_SobreviveAoRoundTrip()
    {
        // Foco de revisão 5: 254 é o maior endereço que o Email.Of aceita e o tamanho exato da coluna.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string local = $"admin+{Guid.NewGuid():N}{new string('a', 26)}";
        string endereco = $"{local}@{new string('b', 63)}.{new string('c', 63)}.{new string('d', 56)}.test";
        endereco.Length.Should().Be(254);

        TenantSlug slug = TenantSlug.Create($"longo-{Guid.NewGuid():N}"[..20]).Value;
        var tenant = Tenant.Register(
            "Longo", slug, new Plan(PlanTier.Free, 5, 1), Email.Of(endereco).Value, PostgresFixture.Agora);

        await using (AppDbContext escrita = postgres.CriarContexto())
        {
            escrita.Tenants.Add(tenant);
            await escrita.SaveChangesAsync(ct);
        }

        await using AppDbContext leitura = postgres.CriarContexto();
        Tenant lido = await leitura.Tenants.SingleAsync(item => item.Id == tenant.Id, ct);

        lido.InitialAdminEmail!.Value.Should().Be(endereco);
    }

    [Fact]
    public async Task Ativacao_ApagaAColunaDoEmail()
    {
        // D1 em forma de dado: depois do commit da ativação, a linha não guarda mais o endereço.
        CancellationToken ct = TestContext.Current.CancellationToken;
        TenantSlug slug = TenantSlug.Create($"apaga-{Guid.NewGuid():N}"[..20]).Value;
        var tenant = Tenant.Register(
            "Apaga", slug, new Plan(PlanTier.Free, 5, 1), PostgresFixture.EmailDoAdmin(), PostgresFixture.Agora);

        await using (AppDbContext escrita = postgres.CriarContexto())
        {
            escrita.Tenants.Add(tenant);
            await escrita.SaveChangesAsync(ct);
        }

        await using (AppDbContext contexto = postgres.CriarContexto())
        {
            Tenant rastreado = await contexto.Tenants.SingleAsync(item => item.Id == tenant.Id, ct);
            rastreado.CompleteProvisioning("org-apaga", ExternalUserId.From("sub-apaga"), PostgresFixture.Agora);
            await contexto.SaveChangesAsync(ct);
        }

        await using AppDbContext conferencia = postgres.CriarContexto();
        List<string> coluna = await conferencia.Database
            .SqlQuery<string>(
                $"SELECT coalesce(initial_admin_email, '<nulo>') AS \"Value\" FROM tenants WHERE id = {tenant.Id.Value}")
            .ToListAsync(ct);

        coluna.Should().ContainSingle().Which.Should().Be("<nulo>");
    }
```

Em `SchemaDeTenantsTests.cs`:

```csharp
    [Fact]
    public async Task InitialAdminEmail_AnulavelDe254()
    {
        // Anulável: nulo em tenant ativo, falhado ou registrado antes da fatia C. 254: o limite do Email.Of.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using AppDbContext contexto = postgres.CriarContexto();

        List<string> coluna = await contexto.Database
            .SqlQuery<string>($"""
                SELECT is_nullable || '|' || data_type || '|' || character_maximum_length AS "Value"
                  FROM information_schema.columns
                 WHERE table_name = 'tenants' AND column_name = 'initial_admin_email'
                """)
            .ToListAsync(ct);

        coluna.Should().ContainSingle().Which.Should().Be("YES|character varying|254");
    }
```

`tests/IdentityGateway.Infrastructure.IntegrationTests/Persistence/MapeamentoDeMemberTests.cs`:

```csharp
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.Persistence;
using IdentityGateway.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IdentityGateway.Infrastructure.IntegrationTests.Persistence;

/// <summary>
/// O <see cref="Member"/> sobrevive à ida e à volta do banco, com auditoria, e o índice único vale.
/// </summary>
public sealed class MapeamentoDeMemberTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static TenantSlug SlugUnico() => TenantSlug.Create($"mem-{Guid.NewGuid():N}"[..18]).Value;

    /// <summary>Registra, ativa pelo caminho de domínio e grava o admin pelo repositório — como o handler fará.</summary>
    private async Task<Member> AtivarComAdminAsync(string sub, CancellationToken ct)
    {
        var tenant = Tenant.Register(
            "Membros", SlugUnico(), new Plan(PlanTier.Free, 5, 1), PostgresFixture.EmailDoAdmin(), PostgresFixture.Agora);

        await using (AppDbContext escrita = postgres.CriarContexto())
        {
            escrita.Tenants.Add(tenant);
            await escrita.SaveChangesAsync(ct);
        }

        await using AppDbContext contexto = postgres.CriarContexto();
        Tenant rastreado = await contexto.Tenants.SingleAsync(item => item.Id == tenant.Id, ct);
        Member admin = rastreado.CompleteProvisioning("org-mem", ExternalUserId.From(sub), PostgresFixture.Agora);

        IMemberRepository membros = new MemberRepository(contexto);
        membros.Add(admin);
        await contexto.SaveChangesAsync(ct);

        return admin;
    }

    [Fact]
    public async Task Member_SobreviveAoRoundTripComAuditoria()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Member admin = await AtivarComAdminAsync($"sub-{Guid.NewGuid():N}", ct);

        await using AppDbContext leitura = postgres.CriarContexto();
        Member lido = await leitura.Members.SingleAsync(item => item.Id == admin.Id, ct);

        lido.TenantId.Should().Be(admin.TenantId);
        lido.ExternalUserId.Should().Be(admin.ExternalUserId);
        lido.Status.Should().Be(MemberStatus.Invited);
        lido.InvitedAt.Should().Be(PostgresFixture.Agora);
        lido.CreatedAt.Should().Be(PostgresFixture.Agora, "o AuditableInterceptor preenche no insert");
        lido.CreatedBy.Should().Be(PostgresFixture.Usuario);
        lido.UpdatedAt.Should().BeNull();
    }

    [Fact]
    public async Task Status_EGravadoComoTexto()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Member admin = await AtivarComAdminAsync($"sub-{Guid.NewGuid():N}", ct);

        await using AppDbContext contexto = postgres.CriarContexto();
        List<string> status = await contexto.Database
            .SqlQuery<string>($"SELECT status AS \"Value\" FROM members WHERE id = {admin.Id.Value}")
            .ToListAsync(ct);

        status.Should().ContainSingle().Which.Should().Be("Invited");
    }

    [Fact]
    public async Task IndiceUnico_RecusaOMesmoSubNoMesmoTenant()
    {
        // É o índice que pega a entrega concorrente duplicada que escapasse do xmin do tenant (Tarefa 11).
        CancellationToken ct = TestContext.Current.CancellationToken;
        string sub = $"sub-{Guid.NewGuid():N}";
        Member admin = await AtivarComAdminAsync(sub, ct);

        await using AppDbContext contexto = postgres.CriarContexto();
        Func<Task> duplicar = () => contexto.Database.ExecuteSqlAsync($"""
            INSERT INTO members (id, tenant_id, external_user_id, status, invited_at, created_at)
            VALUES ({Guid.CreateVersion7()}, {admin.TenantId.Value}, {sub}, 'Invited', now(), now())
            """, ct);

        await duplicar.Should().ThrowAsync<PostgresException>()
            .Where(excecao => excecao.SqlState == PostgresErrorCodes.UniqueViolation);
    }
}
```

`tests/IdentityGateway.Infrastructure.IntegrationTests/Persistence/SchemaDeMembersTests.cs`:

```csharp
using IdentityGateway.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IdentityGateway.Infrastructure.IntegrationTests.Persistence;

/// <summary>
/// A migration cria a tabela <c>members</c> com as colunas, a FK e o índice único da spec (§4.6).
/// </summary>
public sealed class SchemaDeMembersTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ColunasENulidade()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using AppDbContext contexto = postgres.CriarContexto();

        List<string> colunas = await contexto.Database
            .SqlQuery<string>($"""
                SELECT column_name || '|' || is_nullable || '|' || data_type AS "Value"
                  FROM information_schema.columns
                 WHERE table_name = 'members'
                 ORDER BY column_name
                """)
            .ToListAsync(ct);

        colunas.Should().Equal(
            "created_at|NO|timestamp with time zone",
            "created_by|YES|uuid",
            "external_user_id|NO|character varying",
            "id|NO|uuid",
            "invited_at|NO|timestamp with time zone",
            "status|NO|character varying",
            "tenant_id|NO|uuid",
            "updated_at|YES|timestamp with time zone",
            "updated_by|YES|uuid");
    }

    [Fact]
    public async Task ForeignKeyParaTenants()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using AppDbContext contexto = postgres.CriarContexto();

        List<string> referencias = await contexto.Database
            .SqlQuery<string>($"""
                SELECT ccu.table_name || '.' || ccu.column_name AS "Value"
                  FROM information_schema.table_constraints tc
                  JOIN information_schema.constraint_column_usage ccu ON ccu.constraint_name = tc.constraint_name
                 WHERE tc.table_name = 'members' AND tc.constraint_type = 'FOREIGN KEY'
                """)
            .ToListAsync(ct);

        referencias.Should().ContainSingle().Which.Should().Be("tenants.id");
    }

    [Fact]
    public async Task IndiceUnicoEmTenantESub()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using AppDbContext contexto = postgres.CriarContexto();

        List<string> definicao = await contexto.Database
            .SqlQuery<string>($"""
                SELECT indexdef AS "Value"
                  FROM pg_indexes
                 WHERE tablename = 'members' AND indexname = 'ix_members_tenant_id_external_user_id'
                """)
            .ToListAsync(ct);

        definicao.Should().ContainSingle().Which.Should()
            .Contain("UNIQUE").And.Contain("(tenant_id, external_user_id)");
    }
}
```

Em `DependencyInjectionTests.cs`, acrescentar `[InlineData(typeof(IMemberRepository))]` à Theory
`TodasAsAbstracoesDaApplication_SaoResolviveis`.

- [ ] **Passo 2: Rodar e ver falhar**

Run: `dotnet build IdentityGateway.slnx`
Expected: FAIL de compilação — `IMemberRepository`, `MemberRepository` e `AppDbContext.Members` não existem.

- [ ] **Passo 3: Porta e repositório**

`src/IdentityGateway.Application/Common/Abstractions/IMemberRepository.cs`:

```csharp
using IdentityGateway.Domain.Members;

namespace IdentityGateway.Application.Common.Abstractions;

/// <summary>
/// Persistência do agregado <see cref="Member"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Toda assinatura exige o tenant (§6.4).</b> Nesta fatia há só <see cref="Add"/>, e o <c>Member</c> já carrega o
/// <c>TenantId</c>. <c>GetAsync(TenantId, MemberId)</c>, <c>ListAsync(TenantId)</c> e o teste que proíbe a
/// sobrecarga só por id chegam no M2, com as rotas de membro.
/// </para>
/// <para>
/// Como o <see cref="ITenantRepository"/>, não expõe <c>SaveChanges</c>: o membro e a ativação do tenant saem no
/// mesmo commit do <c>TransactionBehavior</c>.
/// </para>
/// </remarks>
public interface IMemberRepository
{
    /// <summary>Marca o membro para inserção. Não grava.</summary>
    void Add(Member member);
}
```

`src/IdentityGateway.Infrastructure/Persistence/Repositories/MemberRepository.cs`:

```csharp
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Members;

namespace IdentityGateway.Infrastructure.Persistence.Repositories;

/// <summary>
/// Implementa <see cref="IMemberRepository"/> sobre o <see cref="AppDbContext"/>.
/// </summary>
internal sealed class MemberRepository(AppDbContext context) : IMemberRepository
{
    /// <inheritdoc />
    public void Add(Member member) => context.Members.Add(member);
}
```

Em `AppDbContext.cs`, depois de `Tenants`:

```csharp
    /// <summary>
    /// Membros dos tenants.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> como os demais: quem consulta é o repositório.
    /// </remarks>
    internal DbSet<Domain.Members.Member> Members => Set<Domain.Members.Member>();
```

Em `DependencyInjection.cs` (`AddPersistencia`), depois de `services.AddScoped<ITenantRepository, TenantRepository>();`:

```csharp
        services.AddScoped<IMemberRepository, MemberRepository>();
```

- [ ] **Passo 4: Mapeamentos**

Em `TenantConfiguration.cs`: `using IdentityGateway.Domain.ValueObjects;` no topo e trocar o `Ignore` de transição
da Tarefa 3 por:

```csharp
        // Só existe em tenant Pending (D1): anulável, e apagado na ativação ou na falha. 254 é o limite do Email.Of.
        // A volta usa Of(...).Value pelo mesmo motivo do slug: o valor gravado já foi validado na escrita.
        builder.Property(tenant => tenant.InitialAdminEmail)
            .HasColumnName("initial_admin_email")
            .HasMaxLength(254)
            .HasConversion(email => email!.Value, valor => Email.Of(valor).Value);

        // Leitura calculada a partir do plano e do contador; não é coluna.
        builder.Ignore(tenant => tenant.HasSeatAvailable);
```

`src/IdentityGateway.Infrastructure/Persistence/Configurations/MemberConfiguration.cs`:

```csharp
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IdentityGateway.Infrastructure.Persistence.Configurations;

/// <summary>
/// Mapeia o agregado <see cref="Member"/> na tabela <c>members</c>.
/// </summary>
/// <remarks>
/// <para>
/// O EF materializa pelo construtor privado do <see cref="Member"/>: todos os parâmetros são conversões de valor único,
/// casados por nome — diferente do <c>Tenant</c>, que precisa de um construtor à parte por causa do <c>Plan</c>.
/// </para>
/// <para>
/// <b>O índice único em <c>(tenant_id, external_user_id)</c></b> é o que a entrega concorrente duplicada encontra se
/// escapar do <c>xmin</c> do tenant: um usuário é membro de um tenant uma vez só.
/// </para>
/// </remarks>
internal sealed class MemberConfiguration : IEntityTypeConfiguration<Member>
{
    public void Configure(EntityTypeBuilder<Member> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("members");

        builder.HasKey(membro => membro.Id);

        builder.Property(membro => membro.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, valor => new MemberId(valor))
            .ValueGeneratedNever();

        builder.Property(membro => membro.TenantId)
            .HasColumnName("tenant_id")
            .HasConversion(id => id.Value, valor => new TenantId(valor))
            .IsRequired();

        // Restrict, e não Cascade: tenant é encerrado, nunca apagado (§9.8), e um DELETE em cascata apagaria o
        // histórico de membros em silêncio.
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(membro => membro.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(membro => membro.ExternalUserId)
            .HasColumnName("external_user_id")
            .HasMaxLength(255)
            .HasConversion(sub => sub.Value, valor => ExternalUserId.From(valor))
            .IsRequired();

        builder.Property(membro => membro.Status)
            .HasColumnName("status")
            .HasMaxLength(20)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(membro => membro.InvitedAt)
            .HasColumnName("invited_at")
            .IsRequired();

        // Auditoria do template: preenchida pelo AuditableInterceptor, nunca pelo domínio.
        builder.Property(membro => membro.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(membro => membro.UpdatedAt)
            .HasColumnName("updated_at");

        builder.Property(membro => membro.CreatedBy)
            .HasColumnName("created_by");

        builder.Property(membro => membro.UpdatedBy)
            .HasColumnName("updated_by");

        builder.HasIndex(membro => new { membro.TenantId, membro.ExternalUserId })
            .IsUnique()
            .HasDatabaseName("ix_members_tenant_id_external_user_id");

        builder.Ignore(membro => membro.DomainEvents);
    }
}
```

- [ ] **Passo 5: Gerar a migration**

Run:
```bash
dotnet ef migrations add ConviteDoAdminInicial \
  --project src/IdentityGateway.Infrastructure \
  --startup-project src/IdentityGateway.Infrastructure \
  --output-dir Persistence/Migrations
```
Expected: cria `Persistence/Migrations/<timestamp>_ConviteDoAdminInicial.cs` e `.Designer.cs` e atualiza o
`AppDbContextModelSnapshot.cs`. O `Up` gerado precisa ter **exatamente** estas operações (a ordem das colunas
dentro do `CreateTable` pode diferir, e é o conjunto que importa):

```csharp
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "initial_admin_email",
                table: "tenants",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_user_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    invited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_members", x => x.id);
                    table.ForeignKey(
                        name: "FK_members_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_members_tenant_id_external_user_id",
                table: "members",
                columns: new[] { "tenant_id", "external_user_id" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "members");

            migrationBuilder.DropColumn(
                name: "initial_admin_email",
                table: "tenants");
        }
```

Se aparecer qualquer outra operação — um índice separado em `tenant_id` (o composto já cobre a FK como prefixo), ou
algo sobre `xmin` — pare: o snapshot divergiu do modelo, e isso precisa ser entendido antes de seguir. A migration
é só de expansão (coluna anulável e tabela nova): a api antiga convive com o schema novo, e o serviço `migrate`
roda antes da nova.

- [ ] **Passo 6: Conferir que modelo e snapshot batem**

Run:
```bash
dotnet ef migrations has-pending-model-changes --project src/IdentityGateway.Infrastructure --startup-project src/IdentityGateway.Infrastructure
```
Expected: `No changes have been made to the model since the last migration.`

- [ ] **Passo 7: Rodar e ver passar**

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter "FullyQualifiedName~Persistence|FullyQualifiedName~DependencyInjectionTests"`
Expected: PASS (Docker ligado).

Run: `dotnet test tests/IdentityGateway.ArchitectureTests`
Expected: PASS (`IMemberRepository` termina em `Repository` e não devolve `IQueryable`; a Api não o conhece).

- [ ] **Passo 8: 🧪 Prova por mutação**

1. Remover o `HasIndex(...)` do `MemberConfiguration` **e** o `CreateIndex` da migration → `IndiceUnico_RecusaOMesmoSubNoMesmoTenant` e `IndiceUnicoEmTenantESub` vermelhos. Reverter os dois (o snapshot também, com `git checkout` do arquivo).
2. `HasMaxLength(254)` → `HasMaxLength(200)` só na configuração e na migration → `EmailDe254Caracteres_SobreviveAoRoundTrip` vermelho (`value too long for type character varying(200)`). Reverter.

Run: `git status --short` — só os arquivos desta tarefa, e a migration com os três arquivos gerados.

- [ ] **Passo 9: Commit**

```bash
git add src tests
git commit -m "feat: coluna do e-mail do admin e tabela de membros

initial_admin_email varchar(254) anulavel em tenants; members com FK
para tenants, status em texto, InvitedAt, auditoria do template e
indice unico em (tenant_id, external_user_id). IMemberRepository so com
Add nesta fatia. Uma migration so, de expansao.

Mutacoes: sem o indice unico e coluna de 200 deixaram vermelhos o teste
do indice e o round trip do e-mail de 254 caracteres."
```

---

### Tarefa 5: Configuração — política de convite e planos

**Arquivos:**
- Create: `src/IdentityGateway.Application/Common/Abstractions/IInvitationPolicy.cs`
- Create: `src/IdentityGateway.Infrastructure/Configuration/InvitationOptions.cs`
- Create: `src/IdentityGateway.Infrastructure/Configuration/InvitationPolicy.cs`
- Modify: `src/IdentityGateway.Infrastructure/DependencyInjection.cs` (`AddOptionsValidadas`, `AddServicos`)
- Modify: `src/IdentityGateway.Api/appsettings.json`
- Modify: `src/IdentityGateway.Domain/Tenants/Plan.cs` (`<remarks>` do construtor)
- Test: `tests/IdentityGateway.Infrastructure.IntegrationTests/DependencyInjectionTests.cs`
- Test: `tests/IdentityGateway.Domain.UnitTests/Tenants/PlanTests.cs` (comentário de `ComLimitesZerados_Aceita`)

**Interfaces:**
- Consome: nada.
- Produz:
  - `public interface IInvitationPolicy { TimeSpan LinkLifetime { get; } }` (Application)
  - `public sealed class InvitationOptions` — seção `Invitations`, `TimeSpan LinkLifetime` (padrão 7 dias), `internal static readonly TimeSpan PrazoMaximo` (30 dias)
  - `internal sealed class InvitationPolicy(IOptions<InvitationOptions> opcoes) : IInvitationPolicy`

Mesmo desenho de `IProvisioningPolicy`/`ProvisioningOptions`/`ProvisioningPolicy` (porta na Application, options
validadas na subida, implementação singleton). A diferença deliberada é o tipo: a spec fixa `Invitations:LinkLifetime`
como `TimeSpan` (`7.00:00:00`), e a validação de "no máximo 30 dias" pega também a armadilha de `"24:00:00"` virar 24
dias só quando passar do teto — por isso o comentário da options mostra o formato com o dia explícito.

- [ ] **Passo 1: Escrever os testes**

Em `DependencyInjectionTests.cs`:

1. Acrescentar `[InlineData(typeof(IInvitationPolicy))]` à Theory `TodasAsAbstracoesDaApplication_SaoResolviveis`.
2. Testes novos:

```csharp
    [Fact]
    public void ConvitesSemConfiguracao_UsamSeteDias()
    {
        // Alinhado à §9.9: o padrão do realm (12 h) ficaria desalinhado do ciclo do convite.
        using ServiceProvider provider = Construir(ConfiguracaoValida());

        provider.GetRequiredService<IInvitationPolicy>().LinkLifetime.Should().Be(TimeSpan.FromDays(7));
    }

    [Theory]
    [InlineData("00:00:00")]        // zero: o link nasceria expirado
    [InlineData("-1.00:00:00")]     // negativo
    [InlineData("00:00:01.500")]    // fração: o Keycloak recebe segundos inteiros, e truncar mudaria o prazo em silêncio
    [InlineData("30.00:00:01")]     // acima do teto
    public void PrazoDoLinkInvalido_FalhaAoValidar(string prazo)
    {
        using ServiceProvider provider = Construir(ConfiguracaoValidaCom(("Invitations:LinkLifetime", prazo)));

        Action validar = () => _ = provider.GetRequiredService<IOptions<InvitationOptions>>().Value;

        validar.Should().Throw<OptionsValidationException>().WithMessage("*LinkLifetime*");
    }

    [Theory]
    [InlineData("00:00:01", 1)]
    [InlineData("2.12:00:00", 216_000)]
    [InlineData("30.00:00:00", 2_592_000)]
    public void PrazoDoLinkValido_ChegaInteiroAPolitica(string prazo, int segundos)
    {
        using ServiceProvider provider = Construir(ConfiguracaoValidaCom(("Invitations:LinkLifetime", prazo)));

        provider.GetRequiredService<IInvitationPolicy>().LinkLifetime.Should().Be(TimeSpan.FromSeconds(segundos));
    }

    [Fact]
    public void PlanoSemVagas_FalhaAoValidar()
    {
        // O admin inicial ocupa uma vaga na ativação (D2): um plano com maxUsers 0 não comporta nem ele, e cada tenant
        // nesse plano cairia em ProvisioningFailed.
        using ServiceProvider provider = Construir(ConfiguracaoValidaCom(
            ("Plans:gratis:tier", "Free"),
            ("Plans:gratis:maxUsers", "0"),
            ("Plans:gratis:maxClients", "1")));

        Action validar = () => _ = provider.GetRequiredService<IOptions<PlanOptions>>().Value;

        validar.Should().Throw<OptionsValidationException>().WithMessage("*maxUsers*");
    }
```

(`DependencyInjectionTests` já tem `using IdentityGateway.Infrastructure.Configuration;`, que traz `InvitationOptions`
e `PlanOptions`.)

- [ ] **Passo 2: Rodar e ver falhar**

Run: `dotnet build IdentityGateway.slnx`
Expected: FAIL de compilação — `IInvitationPolicy` e `InvitationOptions` não existem.

- [ ] **Passo 3: Criar a porta**

`src/IdentityGateway.Application/Common/Abstractions/IInvitationPolicy.cs`:

```csharp
namespace IdentityGateway.Application.Common.Abstractions;

/// <summary>
/// Regras do convite que vêm de configuração.
/// </summary>
/// <remarks>
/// <para>
/// Porta pelo mesmo motivo do <see cref="IProvisioningPolicy"/>: a Application não referencia
/// <c>Microsoft.Extensions.Options</c>.
/// </para>
/// <para>
/// Nesta versão o prazo é global (D9). O prazo por tenant da §9.9 chega no M2, pela mesma porta — quem chama não muda.
/// </para>
/// </remarks>
public interface IInvitationPolicy
{
    /// <summary>Por quanto tempo o link do e-mail de convite vale.</summary>
    /// <remarks>Passado ao Keycloak em cada envio, em segundos inteiros; o padrão do realm (12 h) não é usado.</remarks>
    TimeSpan LinkLifetime { get; }
}
```

- [ ] **Passo 4: Criar as options e a implementação**

`src/IdentityGateway.Infrastructure/Configuration/InvitationOptions.cs`:

```csharp
namespace IdentityGateway.Infrastructure.Configuration;

/// <summary>
/// Política do convite de membros.
/// </summary>
/// <remarks>
/// <c>TimeSpan</c>, e não um inteiro com a unidade no nome como as demais options: a spec da fatia C fixa o formato
/// <c>7.00:00:00</c>. Escreva sempre o dia explícito — <c>"24:00:00"</c> não é 24 horas para o <c>TimeSpan.Parse</c>.
/// </remarks>
public sealed class InvitationOptions
{
    /// <summary>Seção correspondente no arquivo de configuração.</summary>
    public const string SectionName = "Invitations";

    /// <summary>Teto do prazo do link.</summary>
    /// <remarks>
    /// Arbitrário, e é o ponto: sem teto, um erro de digitação faria o link valer, na prática, para sempre — e um link
    /// não usado continua trocando a senha do admin até expirar (spec §3.4).
    /// </remarks>
    internal static readonly TimeSpan PrazoMaximo = TimeSpan.FromDays(30);

    /// <summary>Por quanto tempo o link do e-mail de convite vale. Positivo, em segundos inteiros, até 30 dias.</summary>
    public TimeSpan LinkLifetime { get; init; } = TimeSpan.FromDays(7);

    /// <summary>A regra que a subida confere.</summary>
    internal static bool EhValido(InvitationOptions opcoes) =>
        opcoes.LinkLifetime > TimeSpan.Zero
        && opcoes.LinkLifetime.Ticks % TimeSpan.TicksPerSecond == 0
        && opcoes.LinkLifetime <= PrazoMaximo;
}
```

`src/IdentityGateway.Infrastructure/Configuration/InvitationPolicy.cs`:

```csharp
using IdentityGateway.Application.Common.Abstractions;
using Microsoft.Extensions.Options;

namespace IdentityGateway.Infrastructure.Configuration;

/// <summary>
/// Implementa <see cref="IInvitationPolicy"/> a partir de <see cref="InvitationOptions"/>.
/// </summary>
internal sealed class InvitationPolicy(IOptions<InvitationOptions> opcoes) : IInvitationPolicy
{
    /// <inheritdoc />
    public TimeSpan LinkLifetime { get; } = opcoes.Value.LinkLifetime;
}
```

- [ ] **Passo 5: Registrar, validar e configurar**

Em `DependencyInjection.cs`, em `AddOptionsValidadas`, depois do bloco de `ProvisioningOptions`:

```csharp
        services.AddOptions<InvitationOptions>()
            .Bind(configuration.GetSection(InvitationOptions.SectionName))
            .Validate(
                InvitationOptions.EhValido,
                "Invitations:LinkLifetime precisa ser positivo, em segundos inteiros e de no máximo 30 dias "
                + "(formato d.hh:mm:ss, por exemplo 7.00:00:00).")
            .ValidateOnStart();
```

No mesmo método, trocar a validação do catálogo (hoje `plano.MaxUsers >= 0 && plano.MaxClients >= 0`) e a mensagem:

```csharp
        // maxUsers de pelo menos 1: o admin inicial ocupa uma vaga na ativação (fatia C, D2), e um plano sem vagas faria
        // todo tenant nele cair em ProvisioningFailed. O Plan continua aceitando zero, porque pode vir de dado antigo
        // gravado no tenant; o handler trata esse caso (D14).
        services.AddOptions<PlanOptions>()
            .Bind(configuration.GetSection(PlanOptions.SectionName))
            .Validate(
                planos => planos.Values.All(plano => plano.MaxUsers >= 1 && plano.MaxClients >= 0),
                "Plans: todo plano precisa de maxUsers de pelo menos 1 (o admin inicial ocupa uma vaga) e de "
                + "maxClients não negativo.")
            .ValidateOnStart();
```

(manter, acima dele, o comentário existente sobre a validação escrita à mão.)

Em `AddServicos`, depois de `services.AddSingleton<IProvisioningPolicy, ProvisioningPolicy>();`:

```csharp
        services.AddSingleton<IInvitationPolicy, InvitationPolicy>();
```

Em `src/IdentityGateway.Api/appsettings.json`, depois da seção `"Provisioning"`:

```json
  "Invitations": {
    "LinkLifetime": "7.00:00:00"
  },
```

- [ ] **Passo 6: Comentário do `Plan`**

Em `Plan.cs`, o `<remarks>` do construtor passa a ser:

```csharp
    /// <remarks>
    /// Limite negativo é recusado aqui, e não mais adiante: um <c>MaxUsers</c> negativo faria
    /// <c>ReserveSeat</c> recusar toda reserva com "o plano não admite mais de -5 membros" — uma mensagem
    /// que descreve o sintoma e esconde a causa, que é catálogo mal configurado. <b>Zero é aceito aqui, mas não no
    /// catálogo</b> (fatia C): o catálogo recusa <c>maxUsers</c> abaixo de 1 na subida, porque o admin inicial ocupa uma
    /// vaga; o value object continua aceitando zero porque o plano gravado num tenant pode ser anterior a essa regra, e
    /// o provisionamento trata o tenant sem vaga como falha permanente.
    /// </remarks>
```

Em `PlanTests.ComLimitesZerados_Aceita`, trocar o comentário por:

```csharp
        // O value object aceita zero: o plano gravado num tenant pode ser anterior à regra do catálogo, que recusa
        // maxUsers < 1 na subida. Quem trata o tenant sem vaga é o provisionamento (D14).
```

- [ ] **Passo 7: Rodar e ver passar**

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter "FullyQualifiedName~DependencyInjectionTests|FullyQualifiedName~PlanCatalogTests"`
Expected: PASS (sem Docker: estes testes não abrem conexão).

Run: `dotnet test tests/IdentityGateway.Domain.UnitTests --filter "FullyQualifiedName~PlanTests"`
Expected: PASS.

- [ ] **Passo 8: 🧪 Prova por mutação**

1. Tirar `&& opcoes.LinkLifetime.Ticks % TimeSpan.TicksPerSecond == 0` → `PrazoDoLinkInvalido_FalhaAoValidar("00:00:01.500")` vermelho. Reverter.
2. `<= PrazoMaximo` → `< PrazoMaximo` → `PrazoDoLinkValido_ChegaInteiroAPolitica("30.00:00:00", …)` vermelho. Reverter.
3. Voltar `plano.MaxUsers >= 1` para `>= 0` → `PlanoSemVagas_FalhaAoValidar` vermelho. Reverter.

- [ ] **Passo 9: Commit**

```bash
git add src tests
git commit -m "feat: politica do prazo do convite e plano com pelo menos uma vaga

Invitations:LinkLifetime (padrao 7 dias) validado na subida: positivo,
em segundos inteiros e ate 30 dias, exposto a Application por
IInvitationPolicy. O catalogo passa a recusar maxUsers abaixo de 1,
porque o admin inicial ocupa uma vaga; o Plan continua aceitando zero,
que pode vir de dado antigo.

Mutacoes: sem a regra dos segundos inteiros, teto exclusivo e maxUsers
zero aceito deixaram vermelhos os testes de configuracao."
```

---

### Tarefa 6: Realm do Keycloak e regras

**Arquivos:**
- Modify: `keycloak/bootstrap/realm-identity-gateway.json` (arquivo inteiro)
- Modify: `tests/IdentityGateway.ArchitectureTests/RegrasDoRealmTests.cs` (arquivo inteiro)
- Modify: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakRealTests.cs` (dois testes de permissão do service account — ver "Desvios deliberados")

**Interfaces:**
- Consome: nada.
- Produz, no realm: papel de realm `tenant-admin`; service account com `manage-organizations` e `manage-users`;
  componente `org.keycloak.userprofile.UserProfileProvider` (`declarative-user-profile`) com o atributo `tenant_id`
  só para `admin`; `smtpServer` por ambiente (`SMTP_HOST`, `SMTP_PORT`, `SMTP_FROM`); `resetPasswordAllowed: false`;
  `adminEventsEnabled: true`.

**Fatos conferidos em execução (2026-09-29), além da §3 da spec.** O JSON abaixo foi importado num Keycloak 26.7.4
de verdade (container descartável, rede própria, com um mailpit v1.31.3), antes de escrever este plano:
- import sem erro (`Realm 'identity-gateway' imported`), `smtpServer` com os placeholders substituídos e os três
  timeouts, `resetPasswordAllowed: false`, `adminEventsEnabled: true`, `tenant_id` no `GET .../users/profile` com
  `view`/`edit` só `admin`; atributo fora do perfil continua descartado em silêncio no `POST /users`;
- papéis efetivos do service account em `realm-management`: exatamente `manage-organizations` e `manage-users`
  (nenhum dos dois é composto: `RealmManager.java` L249-254 só compõe `view-*`); papéis de realm efetivos: nenhum
  (o import não dá os papéis padrão ao service account);
- com o token do service account: `GET .../clients` → 403, `PUT` do realm → 403, `GET .../roles/tenant-admin` → 403
  (exige `view-realm`), `GET .../users/{id}/role-mappings/realm/available` → 200, `GET .../users` → 200;
- o `components` sem provedor de chave não impede a geração das chaves do realm (`DefaultExportImportManager.java`
  L518-522 cria as padrão quando nenhuma veio no JSON).

O `displayName` dos quatro atributos padrão é texto literal, e não `"${username}"` como em
`keycloak-default-user-profile.json` (conferido idêntico ao da tag 26.7.4): o import substitui `${...}` no texto
inteiro, e o `TodoPlaceholderEPuro` reprovaria o realm.

- [ ] **Passo 1: Reescrever as regras do realm**

`tests/IdentityGateway.ArchitectureTests/RegrasDoRealmTests.cs`, inteiro:

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IdentityGateway.ArchitectureTests;

/// <summary>
/// O JSON de bootstrap do realm não contém credencial literal, e tem a forma que a Gateway exige.
/// </summary>
/// <remarks>
/// <para>
/// O repositório é público. Um segredo commitado aqui é segredo publicado, e removê-lo do histórico não o tira de
/// quem já clonou. O teste percorre a ÁRVORE do JSON, e não procura texto: um grep por "secret" passaria por um
/// <c>credentials[].value</c>, e reprovaria a palavra num comentário.
/// </para>
/// <para>
/// <b>Placeholder sem default.</b> <c>${GATEWAY_CLIENT_CERT:MIIC...}</c> parece placeholder e carrega um literal no
/// default. Só <c>${NOME}</c> puro passa.
/// </para>
/// <para>
/// <b><c>components</c> deixou de ser proibido em absoluto (fatia C).</b> O motivo da regra eram os provedores de chave,
/// que levariam material de chave para o repositório; eles continuam proibidos. O único componente aceito é o User
/// Profile, sem o qual o Keycloak descarta o atributo <c>tenant_id</c> em silêncio.
/// </para>
/// <para>
/// <b>Limite honesto.</b> Estes testes pegam chave proibida, placeholder impuro, base64 longo e bloco PEM; não
/// pegam um segredo curto colado numa chave qualquer, fora da lista de <see cref="ChavesProibidas"/> — isso fica
/// para a revisão humana.
/// </para>
/// </remarks>
public sealed partial class RegrasDoRealmTests
{
    private const string ProvedorDePerfil = "org.keycloak.userprofile.UserProfileProvider";

    private static readonly string[] ChavesProibidas =
    [
        "credentials", "secret", "secretData", "clientSecret", "bindCredential", "password", "privateKey",
    ];

    private static JsonElement Realm()
    {
        string caminho = RaizDoRepositorio.Caminho("keycloak", "bootstrap", "realm-identity-gateway.json");
        return JsonDocument.Parse(File.ReadAllText(caminho)).RootElement.Clone();
    }

    /// <summary>A configuração do User Profile, que o realm guarda como texto JSON dentro do JSON.</summary>
    private static JsonElement ConfiguracaoDoPerfil()
    {
        string texto = Realm().GetProperty("components").GetProperty(ProvedorDePerfil)[0]
            .GetProperty("config").GetProperty("kc.user.profile.config")[0].GetString()!;

        return JsonDocument.Parse(texto).RootElement.Clone();
    }

    [GeneratedRegex(@"^\$\{[A-Z0-9_]+\}$")]
    private static partial Regex PlaceholderPuro();

    // Base64 longo é o formato de certificado e de chave colados: nenhum valor legítimo do realm tem essa cara.
    [GeneratedRegex(@"^[A-Za-z0-9+/=]{200,}$")]
    private static partial Regex Base64Longo();

    private static IEnumerable<(string Caminho, JsonElement Valor)> Percorrer(JsonElement elemento, string caminho)
    {
        yield return (caminho, elemento);

        if (elemento.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty propriedade in elemento.EnumerateObject())
            {
                foreach ((string Caminho, JsonElement Valor) item in
                    Percorrer(propriedade.Value, $"{caminho}.{propriedade.Name}"))
                {
                    yield return item;
                }
            }
        }
        else if (elemento.ValueKind == JsonValueKind.Array)
        {
            int indice = 0;

            foreach (JsonElement item in elemento.EnumerateArray())
            {
                foreach ((string Caminho, JsonElement Valor) filho in Percorrer(item, $"{caminho}[{indice++}]"))
                {
                    yield return filho;
                }
            }
        }
    }

    [Fact]
    public void NenhumaChaveDeCredencial()
    {
        string[] violacoes =
        [
            .. Percorrer(Realm(), "$")
                .Where(item => ChavesProibidas.Any(chave =>
                    item.Caminho.EndsWith($".{chave}", StringComparison.OrdinalIgnoreCase)))
                .Select(item => item.Caminho),
        ];

        violacoes.Should().BeEmpty("o realm é versionado num repositório público");
    }

    [Fact]
    public void TodoPlaceholderEPuro()
    {
        string[] violacoes =
        [
            .. Percorrer(Realm(), "$")
                .Where(item => item.Valor.ValueKind == JsonValueKind.String)
                .Where(item => item.Valor.GetString()!.Contains("${", StringComparison.Ordinal))
                .Where(item => !PlaceholderPuro().IsMatch(item.Valor.GetString()!))
                .Select(item => item.Caminho),
        ];

        violacoes.Should().BeEmpty("placeholder com default ou embutido em texto carrega literal para o repositório");
    }

    [Fact]
    public void NenhumBase64LongoLiteral()
    {
        string[] violacoes =
        [
            .. Percorrer(Realm(), "$")
                .Where(item => item.Valor.ValueKind == JsonValueKind.String)
                .Where(item => Base64Longo().IsMatch(item.Valor.GetString()!))
                .Select(item => item.Caminho),
        ];

        violacoes.Should().BeEmpty("certificado ou chave colados no realm são literal versionado");
    }

    [Fact]
    public void NenhumBlocoPemLiteral()
    {
        string[] violacoes =
        [
            .. Percorrer(Realm(), "$")
                .Where(item => item.Valor.ValueKind == JsonValueKind.String)
                .Where(item => item.Valor.GetString()!.Contains("-----BEGIN", StringComparison.Ordinal))
                .Select(item => item.Caminho),
        ];

        violacoes.Should().BeEmpty(
            "certificado ou chave colados com armadura PEM são literal versionado, mesmo quando o base64 sozinho "
            + "não chega a 200 caracteres por causa das quebras de linha e do cabeçalho/rodapé");
    }

    [Fact]
    public void ComponentsSoComOUserProfile()
    {
        // Provedores de chave (org.keycloak.keys.KeyProvider) levariam material de chave para o repositório — o motivo
        // original de "components" ser proibido. O User Profile é o único componente de que a Gateway precisa.
        JsonElement componentes = Realm().GetProperty("components");

        componentes.EnumerateObject().Select(tipo => tipo.Name).Should().Equal(ProvedorDePerfil);
        componentes.GetProperty(ProvedorDePerfil).EnumerateArray()
            .Select(componente => componente.GetProperty("providerId").GetString())
            .Should().Equal("declarative-user-profile");
    }

    [Fact]
    public void AtributoTenantIdDeclaradoSoParaAdmin()
    {
        // D10: sem a declaração, o Keycloak descarta o atributo em silêncio no POST /users; com "user" em edit, o
        // próprio usuário trocaria o tenant_id pela account console e sequestraria a correlação e o isolamento.
        JsonElement atributo = ConfiguracaoDoPerfil().GetProperty("attributes").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "tenant_id");

        atributo.GetProperty("permissions").GetProperty("view").EnumerateArray()
            .Select(papel => papel.GetString()).Should().Equal("admin");
        atributo.GetProperty("permissions").GetProperty("edit").EnumerateArray()
            .Select(papel => papel.GetString()).Should().Equal("admin");
    }

    [Fact]
    public void PerfilSemUnmanagedAttributePolicy()
    {
        // ENABLED é o conserto que qualquer busca sugere para "o atributo sumiu" — e é o que entrega o tenant_id ao
        // usuário (D10). Ausente, vale o padrão: só atributos declarados.
        ConfiguracaoDoPerfil().TryGetProperty("unmanagedAttributePolicy", out _).Should().BeFalse();
    }

    [Fact]
    public void PerfilDeclaraOsAtributosPadrao()
    {
        // Um perfil declarado substitui o padrão inteiro: sem username, email, firstName e lastName, o Keycloak recusa o
        // perfil ou perde o VERIFY_PROFILE que pede nome e sobrenome ao convidado (D16).
        ConfiguracaoDoPerfil().GetProperty("attributes").EnumerateArray()
            .Select(item => item.GetProperty("name").GetString())
            .Should().BeEquivalentTo(new[] { "username", "email", "firstName", "lastName", "tenant_id" });
    }

    [Fact]
    public void ResetDeSenhaDesligadoEEventosDeAdministracaoLigados()
    {
        // "Esqueci a senha" daria acesso ao convidado habilitado fora do ciclo do convite; os eventos de administração
        // registram as atribuições de papel feitas pelo service account (§10.3).
        JsonElement realm = Realm();

        realm.GetProperty("resetPasswordAllowed").GetBoolean().Should().BeFalse();
        realm.GetProperty("adminEventsEnabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void SmtpPorAmbienteComTimeoutsAbaixoDoDaGateway()
    {
        // Sem o prefixo KC_, que o Keycloak leria como opção dele. Os timeouts (DefaultEmailSenderProvider, padrão
        // 10 s cada) somados precisam caber no AttemptTimeout da Admin API, senão a Gateway desiste antes de o Keycloak
        // responder que o SMTP caiu.
        JsonElement smtp = Realm().GetProperty("smtpServer");

        smtp.GetProperty("host").GetString().Should().Be("${SMTP_HOST}");
        smtp.GetProperty("port").GetString().Should().Be("${SMTP_PORT}");
        smtp.GetProperty("from").GetString().Should().Be("${SMTP_FROM}");
        smtp.GetProperty("auth").GetString().Should().Be("false");
        smtp.GetProperty("ssl").GetString().Should().Be("false");
        smtp.GetProperty("starttls").GetString().Should().Be("false");

        int soma = new[] { "connectionTimeout", "timeout", "writeTimeout" }
            .Sum(chave => int.Parse(smtp.GetProperty(chave).GetString()!, CultureInfo.InvariantCulture));

        using JsonDocument appsettings = JsonDocument.Parse(
            File.ReadAllText(RaizDoRepositorio.Caminho("src", "IdentityGateway.Api", "appsettings.json")));
        int tentativaMs = appsettings.RootElement.GetProperty("HttpResilience")
            .GetProperty("AttemptTimeoutSeconds").GetInt32() * 1000;

        soma.Should().BeLessThan(tentativaMs);
    }

    [Fact]
    public void PapelDeRealmTenantAdminExiste()
    {
        Realm().GetProperty("roles").GetProperty("realm").EnumerateArray()
            .Select(papel => papel.GetProperty("name").GetString())
            .Should().Contain("tenant-admin");
    }

    [Fact]
    public void RealmTemOrganizationsEOClientDaGatewayComPrivateKeyJwt()
    {
        JsonElement realm = Realm();

        realm.GetProperty("realm").GetString().Should().Be("identity-gateway");
        realm.GetProperty("organizationsEnabled").GetBoolean().Should().BeTrue(
            "sem isto o import passa e POST /organizations responde 404");

        JsonElement client = realm.GetProperty("clients").EnumerateArray()
            .Single(c => c.GetProperty("clientId").GetString() == "identity-gateway");

        client.GetProperty("clientAuthenticatorType").GetString().Should().Be("client-jwt");
        client.GetProperty("serviceAccountsEnabled").GetBoolean().Should().BeTrue();
        client.GetProperty("attributes").GetProperty("jwt.credential.certificate").GetString()
            .Should().Be("${GATEWAY_CLIENT_CERT}");
        client.GetProperty("attributes").GetProperty("token.endpoint.auth.signing.alg").GetString()
            .Should().Be("PS256");
    }

    [Fact]
    public void ServiceAccountComManageOrganizationsEManageUsers()
    {
        // D6: vincular um usuário à Organization exige as duas. Em qualquer ordem; nenhum outro papel.
        JsonElement usuario = Realm().GetProperty("users").EnumerateArray()
            .Single(u => u.GetProperty("username").GetString() == "service-account-identity-gateway");

        usuario.GetProperty("clientRoles").EnumerateObject().Select(p => p.Name)
            .Should().Equal("realm-management");
        usuario.GetProperty("clientRoles").GetProperty("realm-management").EnumerateArray()
            .Select(papel => papel.GetString())
            .Should().BeEquivalentTo(new[] { "manage-organizations", "manage-users" });
        usuario.TryGetProperty("realmRoles", out _).Should().BeFalse();
    }
}
```

- [ ] **Passo 2: Trocar os dois testes de permissão contra o Keycloak real**

Em `KeycloakRealTests.cs`, substituir `ServiceAccount_RecebeProibidoEmUsuarios` e
`ServiceAccount_TemExatamenteManageOrganizations` por:

```csharp
    [Fact]
    public async Task ServiceAccount_RecebeProibidoEmClientsENaConfiguracaoDoRealm()
    {
        // Com manage-users (fatia C), GET /users deixou de ser o negativo. O que o service account continua sem poder:
        // ler clients (view-clients) e alterar o realm (manage-realm).
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();
        string token = await provider.GetRequiredService<ServiceAccountTokenCache>().ObterAsync(ct);

        using HttpClient http = new() { BaseAddress = new Uri($"{keycloak.BaseUrl}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage clients = await http.GetAsync(
            new Uri($"admin/realms/{KeycloakFixture.Realm}/clients", UriKind.Relative), ct);
        using StringContent corpo = new("""{"realm":"identity-gateway"}""", Encoding.UTF8, "application/json");
        using HttpResponseMessage realm = await http.PutAsync(
            new Uri($"admin/realms/{KeycloakFixture.Realm}", UriKind.Relative), corpo, ct);

        clients.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        realm.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ServiceAccount_PapeisEfetivosSaoExatamenteOsDois()
    {
        // D6: os papéis EFETIVOS, com compostos expandidos (role-mappings/.../composite). Um composto como realm-admin
        // passaria num teste que olhasse só os papéis atribuídos diretamente.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient master = await keycloak.CriarClienteMasterAsync(ct);
        string realm = KeycloakFixture.Realm;

        using var usuarios = JsonDocument.Parse(await master.GetStringAsync(
            new Uri($"admin/realms/{realm}/users?username=service-account-identity-gateway&exact=true",
                UriKind.Relative), ct));
        string usuarioId = usuarios.RootElement[0].GetProperty("id").GetString()!;

        using var clientes = JsonDocument.Parse(await master.GetStringAsync(
            new Uri($"admin/realms/{realm}/clients?clientId=realm-management", UriKind.Relative), ct));
        string realmManagementId = clientes.RootElement[0].GetProperty("id").GetString()!;

        using var efetivosDoCliente = JsonDocument.Parse(await master.GetStringAsync(
            new Uri($"admin/realms/{realm}/users/{usuarioId}/role-mappings/clients/{realmManagementId}/composite",
                UriKind.Relative), ct));
        using var efetivosDoRealm = JsonDocument.Parse(await master.GetStringAsync(
            new Uri($"admin/realms/{realm}/users/{usuarioId}/role-mappings/realm/composite", UriKind.Relative), ct));

        List<string> papeisDoCliente = [.. efetivosDoCliente.RootElement.EnumerateArray()
            .Select(papel => papel.GetProperty("name").GetString()!)];
        List<string> papeisDoRealm = [.. efetivosDoRealm.RootElement.EnumerateArray()
            .Select(papel => papel.GetProperty("name").GetString()!)];

        papeisDoCliente.Should().BeEquivalentTo(new[] { "manage-organizations", "manage-users" });
        papeisDoCliente.Should().NotContain(
            new[] { "impersonation", "realm-admin", "manage-realm", "manage-clients", "manage-identity-providers" });
        papeisDoRealm.Should().BeSubsetOf(
            new[] { "default-roles-identity-gateway", "offline_access", "uma_authorization" },
            "nenhum papel de negócio (tenant-admin, platform-admin) no service account");
    }
```

(Acrescentar `using System.Text;` ao topo do arquivo.)

- [ ] **Passo 3: Rodar e ver falhar**

Run: `dotnet test tests/IdentityGateway.ArchitectureTests --filter "FullyQualifiedName~RegrasDoRealmTests"`
Expected: FAIL — `ComponentsSoComOUserProfile` (sem `components`), `AtributoTenantIdDeclaradoSoParaAdmin`,
`ResetDeSenhaDesligado…`, `SmtpPorAmbiente…`, `PapelDeRealmTenantAdminExiste` e
`ServiceAccountComManageOrganizationsEManageUsers`.

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter "FullyQualifiedName~KeycloakRealTests"`
Expected: FAIL — `ServiceAccount_PapeisEfetivosSaoExatamenteOsDois` (só `manage-organizations`). (O teste do `403`
em clients já passa hoje; ele existe para continuar valendo com o `manage-users`.)

- [ ] **Passo 4: Reescrever o realm**

`keycloak/bootstrap/realm-identity-gateway.json`, inteiro (o texto de `kc.user.profile.config` é uma linha só, com
as aspas escapadas; é o mesmo arquivo importado na verificação acima):

```json
{
  "realm": "identity-gateway",
  "enabled": true,
  "organizationsEnabled": true,
  "resetPasswordAllowed": false,
  "adminEventsEnabled": true,
  "smtpServer": {
    "host": "${SMTP_HOST}",
    "port": "${SMTP_PORT}",
    "from": "${SMTP_FROM}",
    "auth": "false",
    "ssl": "false",
    "starttls": "false",
    "connectionTimeout": "2000",
    "timeout": "3000",
    "writeTimeout": "3000"
  },
  "roles": {
    "realm": [
      {
        "name": "tenant-admin",
        "description": "Administrador de um tenant. O tenant vem do atributo tenant_id do usuário."
      }
    ]
  },
  "components": {
    "org.keycloak.userprofile.UserProfileProvider": [
      {
        "providerId": "declarative-user-profile",
        "subComponents": {},
        "config": {
          "kc.user.profile.config": [
            "{\"attributes\":[{\"name\":\"username\",\"displayName\":\"Username\",\"permissions\":{\"view\":[\"admin\",\"user\"],\"edit\":[\"admin\",\"user\"]},\"validations\":{\"length\":{\"min\":3,\"max\":255},\"username-prohibited-characters\":{},\"up-username-not-idn-homograph\":{}}},{\"name\":\"email\",\"displayName\":\"Email\",\"required\":{\"roles\":[\"user\"]},\"permissions\":{\"view\":[\"admin\",\"user\"],\"edit\":[\"admin\",\"user\"]},\"validations\":{\"email\":{},\"length\":{\"max\":255}}},{\"name\":\"firstName\",\"displayName\":\"First name\",\"required\":{\"roles\":[\"user\"]},\"permissions\":{\"view\":[\"admin\",\"user\"],\"edit\":[\"admin\",\"user\"]},\"validations\":{\"length\":{\"max\":255},\"person-name-prohibited-characters\":{}}},{\"name\":\"lastName\",\"displayName\":\"Last name\",\"required\":{\"roles\":[\"user\"]},\"permissions\":{\"view\":[\"admin\",\"user\"],\"edit\":[\"admin\",\"user\"]},\"validations\":{\"length\":{\"max\":255},\"person-name-prohibited-characters\":{}}},{\"name\":\"tenant_id\",\"displayName\":\"Tenant\",\"permissions\":{\"view\":[\"admin\"],\"edit\":[\"admin\"]},\"multivalued\":false}],\"groups\":[{\"name\":\"user-metadata\",\"displayHeader\":\"User metadata\",\"displayDescription\":\"Attributes, which refer to user metadata\"}]}"
          ]
        }
      }
    ]
  },
  "clients": [
    {
      "clientId": "identity-gateway",
      "name": "IdentityGateway (service account)",
      "description": "Client confidencial da Gateway. Só client_credentials, autenticado por private_key_jwt.",
      "enabled": true,
      "protocol": "openid-connect",
      "publicClient": false,
      "bearerOnly": false,
      "standardFlowEnabled": false,
      "implicitFlowEnabled": false,
      "directAccessGrantsEnabled": false,
      "serviceAccountsEnabled": true,
      "clientAuthenticatorType": "client-jwt",
      "attributes": {
        "jwt.credential.certificate": "${GATEWAY_CLIENT_CERT}",
        "token.endpoint.auth.signing.alg": "PS256"
      }
    }
  ],
  "users": [
    {
      "username": "service-account-identity-gateway",
      "enabled": true,
      "serviceAccountClientId": "identity-gateway",
      "clientRoles": {
        "realm-management": [
          "manage-organizations",
          "manage-users"
        ]
      }
    }
  ]
}

```

Conferir o JSON e a configuração embutida:

Run: `jq -r '.components["org.keycloak.userprofile.UserProfileProvider"][0].config["kc.user.profile.config"][0]' keycloak/bootstrap/realm-identity-gateway.json | jq '.attributes[] | .name'`
Expected: `"username"`, `"email"`, `"firstName"`, `"lastName"`, `"tenant_id"`.

- [ ] **Passo 5: Rodar e ver passar**

Run: `dotnet test tests/IdentityGateway.ArchitectureTests`
Expected: PASS.

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter "FullyQualifiedName~Keycloak|FullyQualifiedName~Provisioning"`
Expected: PASS — o fixture importa o realm novo (o `SMTP_*` ainda não existe no container e fica literal no
`smtpServer`; nenhum teste envia e-mail antes da Tarefa 7, que o configura).

- [ ] **Passo 6: 🧪 Prova por mutação**

1. No realm, `"edit": ["admin"]` do `tenant_id` → `"edit": ["admin", "user"]` → `AtributoTenantIdDeclaradoSoParaAdmin` vermelho. Reverter.
2. Acrescentar `\"unmanagedAttributePolicy\":\"ENABLED\"` à raiz da configuração do perfil → `PerfilSemUnmanagedAttributePolicy` vermelho. Reverter.
3. Acrescentar `"realm-admin"` aos papéis do service account → `ServiceAccountComManageOrganizationsEManageUsers` vermelho e, no Keycloak real, `ServiceAccount_PapeisEfetivosSaoExatamenteOsDois` vermelho. Reverter.
4. Trocar `"displayName":"Username"` por `"displayName":"${username}"` → `TodoPlaceholderEPuro` vermelho. Reverter.
5. `"timeout": "3000"` → `"timeout": "10000"` → `SmtpPorAmbienteComTimeoutsAbaixoDoDaGateway` vermelho. Reverter.

- [ ] **Passo 7: Commit**

```bash
git add keycloak/bootstrap/realm-identity-gateway.json tests/IdentityGateway.ArchitectureTests/RegrasDoRealmTests.cs tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakRealTests.cs
git commit -m "feat: realm com tenant-admin, manage-users, User Profile e SMTP

O realm ganha o papel tenant-admin, manage-users no service account (o
vinculo a Organization exige as duas permissoes), o atributo tenant_id
declarado no User Profile so para admin, sem unmanagedAttributePolicy,
o SMTP por ambiente com timeouts abaixo do AttemptTimeout, reset de
senha desligado e eventos de administracao ligados. components passa a
aceitar so o User Profile; provedores de chave continuam proibidos.

Mutacoes: tenant_id editavel pelo usuario, politica ENABLED, realm-admin
no service account, placeholder no perfil e timeout de 10 s deixaram
vermelhas as regras correspondentes e o teste de papeis efetivos."
```

---

### Tarefa 7: Keycloak — separar o `aud` do transporte, fixture com mailpit e health check

**Arquivos:**
- Modify: `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakAdminOptions.cs` (arquivo inteiro)
- Modify: `src/IdentityGateway.Infrastructure/Identity/Keycloak/ClientAssertionFactory.cs:56` (`Audience`)
- Modify: `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakTokenClient.cs:49` (URL do token)
- Modify: `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakServiceCollectionExtensions.cs` (validações)
- Modify: `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakHealthCheck.cs` (arquivo inteiro)
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/AmbienteDeTeste.cs`
- Modify: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakFixture.cs` (arquivo inteiro)
- Modify: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/OpcoesDeTeste.cs`
- Modify: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakHealthCheckTests.cs`
- Modify: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakAdminOptionsTests.cs`
- Modify: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/ClientAssertionFactoryTests.cs`
- Modify: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakTokenClientTests.cs`
- Modify: `tests/IdentityGateway.Infrastructure.IntegrationTests/Provisioning/ComposicaoDoProvisionamento.cs`
- Modify: `tests/IdentityGateway.Infrastructure.IntegrationTests/DependencyInjectionTests.cs` (`Construir`)

**Interfaces:**
- Consome: o realm da Tarefa 6 (o health check exige `manage-users` no token).
- Produz:
  - `KeycloakAdminOptions.PublicBaseUrl` (`string?`), `KeycloakAdminOptions.AssertionAudience` (`string`),
    `KeycloakAdminOptions.TokenEndpoint` (`string`); `Issuer` é removido.
  - `KeycloakHealthCheck.PapelExigido` = `"manage-users"`.
  - `internal static IServiceCollection AmbienteDeTeste.ComAmbiente(this IServiceCollection services, string nome = "Development")` (teste).
  - `KeycloakFixture`: `HostnamePublico` (`"http://keycloak.test:8081"`), `ImagemDoMailpit`
    (`"axllent/mailpit:v1.31.3"`), `MailpitUrl`, `static string EmailUnico()`,
    `static string EmailUnicoDe254Caracteres()`, `Task<IReadOnlyList<string>> MensagensParaAsync(string, CancellationToken)`,
    `Task<IReadOnlyList<string>> EsperarMensagensAsync(string, int, CancellationToken)`,
    `Task<string> TextoDaMensagemAsync(string, CancellationToken)`, `Task<Uri> LinkDoConviteAsync(string, CancellationToken)`.
  - `KeycloakHealthCheckTests.ColecaoDaComposicao(string baseUrl, string? pem = null, string? publicBaseUrl = null)`.

**Por que o ambiente entra por `IHostEnvironment`.** `AddInfrastructure` recebe só a configuração, e a recusa de
`AllowInsecureHttp` fora de `Development` precisa saber o ambiente. `Validate<IHostEnvironment>` resolve a
dependência do contêiner — que o host sempre registra — e falha fechado se ninguém a registrou. As composições de
teste montadas à mão (`ServiceCollection`) passam a registrar um ambiente `Development` por `ComAmbiente()`.

**mailpit.** Tag `v1.31.3`, release de 2026-09-27 (conferida no Docker Hub e em
`api.github.com/repos/axllent/mailpit/releases/latest`). Conferido na imagem: o healthcheck embutido é
`["CMD", "/mailpit", "readyz"]`, o endpoint `/readyz` responde 200, a busca é
`GET /api/v1/search?query=to:"<endereço>"` (o `+` precisa ir escapado; a resposta tem `messages[].ID` e
`messages[].To[].Address`) e `GET /api/v1/message/{ID}` devolve o corpo em `Text` — com o `&` do link escapado como
`&` no JSON, que o `JsonDocument` decodifica. A busca por `to:` casa por trecho; o filtro exato por
destinatário é feito no teste.

- [ ] **Passo 1: Helper de ambiente para os testes**

`tests/IdentityGateway.Infrastructure.IntegrationTests/AmbienteDeTeste.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace IdentityGateway.Infrastructure.IntegrationTests;

/// <summary>
/// O <see cref="IHostEnvironment"/> que o host registraria, para as composições montadas à mão.
/// </summary>
/// <remarks>
/// A validação das options do Keycloak recusa <c>AllowInsecureHttp</c> fora de <c>Development</c> e depende do
/// ambiente pelo contêiner. Sem host, ninguém o registra, e a validação falha fechada — é o comportamento certo em
/// produção, e o motivo deste helper existir nos testes.
/// </remarks>
internal static class AmbienteDeTeste
{
    public static IServiceCollection ComAmbiente(this IServiceCollection services, string nome = "Development")
    {
        IHostEnvironment ambiente = Substitute.For<IHostEnvironment>();
        ambiente.EnvironmentName.Returns(nome);

        return services.AddSingleton(ambiente);
    }
}
```

- [ ] **Passo 2: Escrever os testes das options, do assertion e do token endpoint**

Em `OpcoesDeTeste.cs`, a assinatura e o corpo passam a ser:

```csharp
    public static IOptions<KeycloakAdminOptions> Keycloak(
        string baseUrl = "http://keycloak.test:8080", string? pem = null, string? publicBaseUrl = null) =>
        Options.Create(new KeycloakAdminOptions
        {
            BaseUrl = baseUrl,
            PublicBaseUrl = publicBaseUrl,
            Realm = "identity-gateway",
            ClientId = "identity-gateway",
            PrivateKeyPem = pem ?? ChavesDeTeste.Gerar().PemPrivado,
            AllowInsecureHttp = true,
        });
```

Em `KeycloakAdminOptionsTests.cs`:

1. `Resolver` passa a receber o ambiente:

```csharp
    private static KeycloakAdminOptions Resolver(Dictionary<string, string?> valores, string ambiente = "Development")
    {
        IConfiguration configuracao = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();

        ServiceCollection services = new();
        services.ComAmbiente(ambiente);
        services.AddKeycloakIdentity(configuracao);

        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<KeycloakAdminOptions>>().Value;
    }
```

2. `ConfiguracaoCompleta_ExpoeIssuerEEnderecoBase` passa a ser:

```csharp
    [Fact]
    public void ConfiguracaoCompleta_SemPublicBaseUrl_AudEOTransporteSaemDoBaseUrl()
    {
        // Omitido o PublicBaseUrl, nada muda para quem roda a API pela IDE com BaseUrl=http://localhost:8081.
        KeycloakAdminOptions opcoes = Resolver(Validos());

        opcoes.AssertionAudience.Should().Be("https://sso.exemplo.test/realms/identity-gateway");
        opcoes.TokenEndpoint.Should().Be("https://sso.exemplo.test/realms/identity-gateway/protocol/openid-connect/token");
        opcoes.AdminBaseAddress.Should().Be(new Uri("https://sso.exemplo.test/"));
    }
```

3. Em `BaseUrlComPrefixoDeCaminhoEBarraFinal_PreservaOPrefixo`, trocar `opcoes.Issuer.Should()...` por
`opcoes.AssertionAudience.Should().Be("https://sso.exemplo.test/auth/realms/identity-gateway");`.
4. Em `HttpComPermissaoDeDesenvolvimento_Aceita`, trocar `opcoes.Issuer` por `opcoes.AssertionAudience`.
5. Testes novos:

```csharp
    [Fact]
    public void ComPublicBaseUrl_AudUsaOPublicoEOTransporteContinuaNoBaseUrl()
    {
        // D8: com KC_HOSTNAME, o emissor é o endereço público, e o aud é comparado por texto com ele. O token endpoint
        // e a Admin API continuam no endereço interno — de dentro do container, localhost:8081 não é o Keycloak.
        Dictionary<string, string?> valores = Validos();
        valores["Keycloak:Admin:BaseUrl"] = "http://keycloak:8080";
        valores["Keycloak:Admin:AllowInsecureHttp"] = "true";
        valores["Keycloak:Admin:PublicBaseUrl"] = "http://localhost:8081/";

        KeycloakAdminOptions opcoes = Resolver(valores);

        opcoes.AssertionAudience.Should().Be("http://localhost:8081/realms/identity-gateway");
        opcoes.TokenEndpoint.Should().Be("http://keycloak:8080/realms/identity-gateway/protocol/openid-connect/token");
        opcoes.AdminBaseAddress.Should().Be(new Uri("http://keycloak:8080/"));
    }

    [Theory]
    [InlineData("localhost:8081")]
    [InlineData("http://localhost:8081/?x=1")]
    [InlineData("http://localhost:8081/#frag")]
    [InlineData("ftp://localhost:8081")]
    public void PublicBaseUrlMalFormado_FalhaAoValidar(string publico)
    {
        Dictionary<string, string?> valores = Validos();
        valores["Keycloak:Admin:PublicBaseUrl"] = publico;

        Action resolver = () => Resolver(valores);

        resolver.Should().Throw<OptionsValidationException>().WithMessage("*PublicBaseUrl*");
    }

    [Fact]
    public void AllowInsecureHttpForaDeDevelopment_FalhaAoValidar()
    {
        // "Transporte interno" não pode virar convite a http em produção com um bearer de manage-users.
        Dictionary<string, string?> valores = Validos();
        valores["Keycloak:Admin:BaseUrl"] = "http://keycloak:8080";
        valores["Keycloak:Admin:AllowInsecureHttp"] = "true";

        Action resolver = () => Resolver(valores, ambiente: "Production");

        resolver.Should().Throw<OptionsValidationException>().WithMessage("*Development*");
    }

    [Fact]
    public void PublicBaseUrlHttpForaDeDevelopment_FalhaAoValidar()
    {
        Dictionary<string, string?> valores = Validos();
        valores["Keycloak:Admin:PublicBaseUrl"] = "http://sso.exemplo.test";

        Action resolver = () => Resolver(valores, ambiente: "Production");

        resolver.Should().Throw<OptionsValidationException>().WithMessage("*Development*");
    }

    [Fact]
    public void HttpsEmProducao_Aceita()
    {
        // Controle positivo das duas regras acima: sem ele, "Production recusa tudo" também passaria.
        Dictionary<string, string?> valores = Validos();
        valores["Keycloak:Admin:PublicBaseUrl"] = "https://sso.exemplo.test";

        KeycloakAdminOptions opcoes = Resolver(valores, ambiente: "Production");

        opcoes.AssertionAudience.Should().Be("https://sso.exemplo.test/realms/identity-gateway");
    }
```

Em `ClientAssertionFactoryTests.cs`:

```csharp
    [Fact]
    public void Criar_ComPublicBaseUrl_AudEOEmissorPublico()
    {
        // Literal, e não recalculado pela fórmula: o aud é o KC_HOSTNAME, e o BaseUrl interno não aparece nele.
        IOptions<KeycloakAdminOptions> opcoes = OpcoesDeTeste.Keycloak(
            "http://keycloak:8080", _chaves.PemPrivado, publicBaseUrl: "http://keycloak.test:8081");
        IDateTimeProvider relogio = Substitute.For<IDateTimeProvider>();
        relogio.UtcNow.Returns(Agora);

        string assertion = new ClientAssertionFactory(new GatewaySigningKey(opcoes), opcoes, relogio).Criar();

        Payload(assertion).GetProperty("aud").GetString()
            .Should().Be("http://keycloak.test:8081/realms/identity-gateway");
    }
```

Em `KeycloakTokenClientTests.cs`:

1. `Criar` passa a ser `private static KeycloakTokenClient Criar(HandlerFalso handler, string? publicBaseUrl = null)`
e cria as options com `OpcoesDeTeste.Keycloak(publicBaseUrl: publicBaseUrl)`.
2. Em `ClienteRegistrado_NaoRepeteONoTokenEndpoint`, logo depois de `services.AddLogging();`:
`services.ComAmbiente();`.
3. Teste novo:

```csharp
    [Fact]
    public async Task ObterAsync_ComPublicBaseUrl_ContinuaChamandoOBaseUrl()
    {
        // D8: o PublicBaseUrl nunca é discado. Chamado de dentro do container, localhost:8081 seria o próprio container.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Uri? destino = null;

        HandlerFalso handler = new((pedido, _) =>
        {
            destino = pedido.RequestUri;
            return Task.FromResult(TokenOk());
        });

        await Criar(handler, publicBaseUrl: "http://publico.test:8081").ObterAsync(ct);

        destino.Should().Be(new Uri("http://keycloak.test:8080/realms/identity-gateway/protocol/openid-connect/token"));
    }
```

- [ ] **Passo 3: Escrever os testes do health check**

Em `KeycloakHealthCheckTests.cs` (com `using System.Text;`, `using System.Text.Json;`,
`using IdentityGateway.Infrastructure.Identity.Keycloak;` e `using Microsoft.IdentityModel.Tokens;`):

1. `ColecaoDaComposicao` passa a ser:

```csharp
    internal static ServiceCollection ColecaoDaComposicao(
        string baseUrl, string? pem = null, string? publicBaseUrl = null)
    {
        IConfiguration configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = "Host=localhost;Database=x;Username=u;Password=p",
                ["Jwt:Issuer"] = "identitygateway",
                ["Jwt:Audience"] = "identitygateway-api",
                ["Jwt:SigningKey"] = new string('k', 32),
                ["Keycloak:Admin:BaseUrl"] = baseUrl,
                ["Keycloak:Admin:PublicBaseUrl"] = publicBaseUrl,
                ["Keycloak:Admin:Realm"] = "identity-gateway",
                ["Keycloak:Admin:ClientId"] = "identity-gateway",
                ["Keycloak:Admin:PrivateKeyPem"] = pem ?? ChavesDeTeste.Gerar().PemPrivado,
                ["Keycloak:Admin:AllowInsecureHttp"] = "true",
                ["HttpResilience:MaxRetryAttempts"] = "1",
            })
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.ComAmbiente();
        services.AddInfrastructure(configuracao);

        return services;
    }
```

2. Testes novos e o dublê:

```csharp
    [Fact]
    public async Task TokenSemManageUsers_UnhealthyMandandoRecriarOsVolumes()
    {
        // Um volume de antes da fatia C tem o realm antigo: o token sai, mas sem manage-users. Sem esta checagem, o
        // /health/ready ficaria verde e cada tenant passaria 24 h em retry até cair em ProvisioningFailed.
        CancellationToken ct = TestContext.Current.CancellationToken;
        ServiceCollection services = ColecaoDaComposicao("http://127.0.0.1:9");
        services.AddSingleton<ITokenEndpoint>(new EndpointFixo(TokenComPapeis("manage-organizations")));
        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        HealthReportEntry entrada = await Checar(provider, ct);

        entrada.Status.Should().Be(HealthStatus.Unhealthy);
        entrada.Description.Should().Contain("manage-users").And.Contain("docker compose down -v");
    }

    [Fact]
    public async Task TokenComManageUsers_Healthy()
    {
        // Controle positivo: sem ele, "o parse do token sempre falha" também passaria no teste acima.
        CancellationToken ct = TestContext.Current.CancellationToken;
        ServiceCollection services = ColecaoDaComposicao("http://127.0.0.1:9");
        services.AddSingleton<ITokenEndpoint>(
            new EndpointFixo(TokenComPapeis("manage-organizations", "manage-users")));
        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        HealthReportEntry entrada = await Checar(provider, ct);

        entrada.Status.Should().Be(HealthStatus.Healthy);
    }

    // Token não assinado com só a parte que o check lê: resource_access.realm-management.roles.
    private static string TokenComPapeis(params string[] papeis)
    {
        static string Codificar(string json) => Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(json));

        string corpo = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["resource_access"] = new Dictionary<string, object>
            {
                ["realm-management"] = new Dictionary<string, object> { ["roles"] = papeis },
            },
        });

        return $"{Codificar("""{"alg":"none","typ":"JWT"}""")}.{Codificar(corpo)}.";
    }

    private sealed class EndpointFixo(string token) : ITokenEndpoint
    {
        public Task<TokenObtido> ObterAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new TokenObtido(token, TimeSpan.FromMinutes(5)));
    }
```

Em `DependencyInjectionTests.Construir`, logo depois de `services.AddLogging();`: `services.ComAmbiente();`.

- [ ] **Passo 4: Rodar e ver falhar**

Run: `dotnet build IdentityGateway.slnx`
Expected: FAIL de compilação — `KeycloakAdminOptions` não tem `PublicBaseUrl`, `AssertionAudience` nem
`TokenEndpoint`.

- [ ] **Passo 5: Implementar as options**

`KeycloakAdminOptions.cs`, inteiro:

```csharp
using System.ComponentModel.DataAnnotations;

namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// Como a Gateway alcança a Admin API do Keycloak e se autentica nela.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>class</c>, e não <c>record</c>, de propósito.</b> O <c>ToString()</c> que o compilador gera para um record
/// imprime todas as propriedades — inclusive <see cref="PrivateKeyPem"/>. Um log de diagnóstico que fizesse
/// <c>{options}</c> vazaria a chave privada da Gateway.
/// </para>
/// <para>
/// <b>A chave vem por arquivo ou por PEM, nunca pelos dois.</b> <see cref="PrivateKeyPath"/> é o preferido: segredo
/// montado como arquivo não aparece em <c>docker inspect</c> nem em <c>/proc/*/environ</c>, e é assim que cofres e
/// orquestradores entregam segredo. <see cref="PrivateKeyPem"/> existe para user-secrets e testes.
/// </para>
/// <para>
/// <b>Endereço público e transporte são coisas diferentes (fatia C, D8).</b> O <c>aud</c> do assertion é o emissor
/// público (<see cref="AssertionAudience"/>); o token endpoint e a Admin API são chamados pelo <see cref="BaseUrl"/>,
/// que pode ser o endereço interno.
/// </para>
/// </remarks>
internal sealed class KeycloakAdminOptions
{
    /// <summary>Seção correspondente no arquivo de configuração.</summary>
    public const string SectionName = "Keycloak:Admin";

    /// <summary>Endereço do Keycloak como a Gateway o alcança (transporte), com eventual prefixo de caminho.</summary>
    /// <remarks>
    /// O token endpoint e a Admin API derivam só daqui. Pode ser o endereço interno — <c>http://keycloak:8080</c> no
    /// compose —, porque, com <c>KC_HOSTNAME_BACKCHANNEL_DYNAMIC</c>, o Keycloak responde por ele sem redirecionar.
    /// </remarks>
    [Required(ErrorMessage = "Keycloak:Admin:BaseUrl é obrigatório.")]
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Endereço público do Keycloak — o <c>KC_HOSTNAME</c>. Só alimenta o <c>aud</c>; nunca é discado.</summary>
    /// <remarks>
    /// Com <c>KC_HOSTNAME</c>, o Keycloak calcula o emissor pelo endereço público, mesmo com a requisição chegando pelo
    /// interno, e compara o <c>aud</c> por igualdade de texto: <c>127.0.0.1</c> no lugar de <c>localhost</c> é recusado
    /// com "Invalid token audience". Omitido, vale o <see cref="BaseUrl"/>, e nada muda para quem roda a API pela IDE
    /// com <c>BaseUrl=http://localhost:8081</c>. Absoluto, sem query nem fragmento; <c>http</c> só em Development.
    /// </remarks>
    public string? PublicBaseUrl { get; init; }

    /// <summary>Realm da Gateway. Nunca o <c>master</c>.</summary>
    [Required(ErrorMessage = "Keycloak:Admin:Realm é obrigatório.")]
    public string Realm { get; init; } = string.Empty;

    /// <summary>ClientId do client confidencial da Gateway.</summary>
    [Required(ErrorMessage = "Keycloak:Admin:ClientId é obrigatório.")]
    public string ClientId { get; init; } = string.Empty;

    /// <summary>Caminho do arquivo PEM com a chave privada RSA.</summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>A chave privada RSA em PEM, inline.</summary>
    public string? PrivateKeyPem { get; init; }

    /// <summary>Permite <c>http://</c>. Aceito só no ambiente Development.</summary>
    /// <remarks>
    /// Fora do desenvolvimento, o assertion e o token do service account trafegariam em claro — e o token dá
    /// <c>manage-organizations</c> e <c>manage-users</c> sobre o realm inteiro. A subida recusa a opção fora de
    /// Development (fatia C): "o transporte pode ser interno" não é licença para <c>http</c> em produção.
    /// </remarks>
    public bool AllowInsecureHttp { get; init; }

    /// <summary>O <c>aud</c> do client assertion: o emissor público do realm, sem barra final.</summary>
    public string AssertionAudience =>
        $"{(string.IsNullOrWhiteSpace(PublicBaseUrl) ? BaseUrl : PublicBaseUrl).TrimEnd('/')}/realms/{Realm}";

    /// <summary>URL do token endpoint, derivada só do <see cref="BaseUrl"/>.</summary>
    public string TokenEndpoint => $"{BaseUrl.TrimEnd('/')}/realms/{Realm}/protocol/openid-connect/token";

    /// <summary>Endereço base dos clientes HTTP, com barra final.</summary>
    /// <remarks>
    /// <para>
    /// A barra final não é estética: sem ela, <c>new Uri(base, "admin/realms/...")</c> descarta o último segmento
    /// do caminho — e um Keycloak em <c>/auth</c> seria chamado na raiz.
    /// </para>
    /// <para>
    /// <b>Nunca lança.</b> <c>ValidateDataAnnotations</c> percorre recursivamente as propriedades complexas do
    /// options para validar objetos aninhados, e chama este <c>get</c> antes de o <see cref="BaseUrl"/> vazio ou
    /// inválido ser rejeitado pelo <c>[Required]</c> — um <c>BaseUrl</c> ausente faria <c>new Uri("/")</c> lançar
    /// <see cref="UriFormatException"/> não tratada em vez da <c>OptionsValidationException</c> esperada. Com
    /// <c>BaseUrl</c> ausente ou inválido, o retorno é um placeholder sem sentido — que nunca chega a ser lido: a
    /// validação já barrou a subida antes de qualquer consumidor pedir esta propriedade.
    /// </para>
    /// </remarks>
    public Uri AdminBaseAddress => Uri.TryCreate($"{BaseUrl.TrimEnd('/')}/", UriKind.Absolute, out Uri? endereco)
        ? endereco
        : new Uri("about:blank");
}
```

- [ ] **Passo 6: Assertion, token endpoint e validações**

Em `ClientAssertionFactory.cs`, trocar `Audience = opcoes.Issuer,` por `Audience = opcoes.AssertionAudience,` e, no
`<list>` do XML doc, o primeiro item por:

```csharp
///   <item><b><c>aud</c> = emissor público, string única.</b> Aceito sempre e recomendado desde a 26.2; <c>aud</c> com
///   mais de um valor é recusado. O emissor é o <c>KC_HOSTNAME</c> quando configurado, não o endereço que a Gateway
///   disca (<see cref="KeycloakAdminOptions.AssertionAudience"/>).</item>
```

Em `KeycloakTokenClient.cs`, trocar a chamada (linhas 48-49) por:

```csharp
        using HttpResponseMessage resposta = await http.PostAsync(
            new Uri(opcoes.TokenEndpoint), corpo, cancellationToken);
```

Em `KeycloakServiceCollectionExtensions.cs`: `using Microsoft.Extensions.Hosting;` no topo; a cadeia de validação
passa a ser:

```csharp
        // As mensagens nunca incluem o valor: o que está sendo validado é, entre outras coisas, uma chave privada.
        services.AddOptions<KeycloakAdminOptions>()
            .Bind(configuration.GetSection(KeycloakAdminOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                BaseUrlAceitavel,
                "Keycloak:Admin:BaseUrl precisa ser uma URL absoluta https (http só com AllowInsecureHttp, em "
                + "desenvolvimento).")
            .Validate(
                PublicBaseUrlAceitavel,
                "Keycloak:Admin:PublicBaseUrl precisa ser uma URL absoluta http ou https, sem query nem fragmento.")
            .Validate<IHostEnvironment>(
                HttpSoEmDesenvolvimento,
                "Keycloak:Admin: AllowInsecureHttp e PublicBaseUrl em http só são aceitos no ambiente Development.")
            .Validate(
                GatewaySigningKey.TemExatamenteUmaFonte,
                "Keycloak:Admin: informe exatamente um entre PrivateKeyPath e PrivateKeyPem.")
            .Validate(
                GatewaySigningKey.EhLegivel,
                "Keycloak:Admin: a chave privada não pôde ser lida como RSA em PEM (arquivo ausente, sem permissão "
                + "ou conteúdo inválido).")
            .ValidateOnStart();
```

E, depois de `BaseUrlAceitavel`:

```csharp
    private static bool PublicBaseUrlAceitavel(KeycloakAdminOptions opcoes)
    {
        if (string.IsNullOrWhiteSpace(opcoes.PublicBaseUrl))
        {
            return true;
        }

        return Uri.TryCreate(opcoes.PublicBaseUrl, UriKind.Absolute, out Uri? endereco)
               && (endereco.Scheme == Uri.UriSchemeHttps || endereco.Scheme == Uri.UriSchemeHttp)
               && string.IsNullOrEmpty(endereco.Query)
               && string.IsNullOrEmpty(endereco.Fragment);
    }

    // O ambiente vem do contêiner (o host sempre registra IHostEnvironment); sem ele, a validação falha fechada.
    private static bool HttpSoEmDesenvolvimento(KeycloakAdminOptions opcoes, IHostEnvironment ambiente)
    {
        if (ambiente.IsDevelopment())
        {
            return true;
        }

        bool publicoEmHttp = !string.IsNullOrWhiteSpace(opcoes.PublicBaseUrl)
                             && !opcoes.PublicBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        return !opcoes.AllowInsecureHttp && !publicoEmHttp;
    }
```

- [ ] **Passo 7: Health check exige `manage-users`**

`KeycloakHealthCheck.cs`, inteiro:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// O Keycloak está pronto para a Gateway se ela consegue obter o token do service account — com os papéis que o
/// provisionamento usa.
/// </summary>
/// <remarks>
/// <para>
/// <b>Obter o token, e não só ler a metadata OIDC.</b> A metadata responderia 200 com a chave errada, o realm sem o
/// client ou o certificado dessincronizado. Obter o token prova chave, realm e <c>private_key_jwt</c> de uma vez — e
/// custa zero por sonda enquanto o token do cache vale.
/// </para>
/// <para>
/// <b>E exigir <c>manage-users</c> no token (fatia C).</b> O import do realm só roda na primeira subida: num volume
/// antigo, o token sai, mas sem o papel que o convite do admin exige, e cada tenant passaria a janela inteira em retry.
/// Aqui isso vira <c>Unhealthy</c> com a instrução do conserto.
/// </para>
/// <para>
/// Devolve o <c>FailureStatus</c> do registro (<c>Unhealthy</c>): o <c>MapHealthChecks</c> responde 200 para
/// <c>Degraded</c>, e um check degradado nunca tiraria a instância do balanceador.
/// </para>
/// </remarks>
internal sealed class KeycloakHealthCheck(ServiceAccountTokenCache cache) : IHealthCheck
{
    /// <summary>Papel de <c>realm-management</c> que o convite exige além de <c>manage-organizations</c>.</summary>
    internal const string PapelExigido = "manage-users";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        string token;

        try
        {
            token = await cache.ObterAsync(cancellationToken);
        }
        catch (HttpRequestException excecao)
        {
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                "Não foi possível obter o token do service account no Keycloak.",
                excecao);
        }

        if (!TemPapelDeRealmManagement(token, PapelExigido))
        {
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                $"O service account do Keycloak não tem {PapelExigido}: o realm foi importado antes da fatia C, e o "
                + "import só roda na primeira subida. Rode `docker compose down -v` e suba de novo com "
                + "`docker compose up -d --build`.");
        }

        return HealthCheckResult.Healthy("Token do service account obtido, com manage-users.");
    }

    // Lê resource_access.realm-management.roles sem validar a assinatura: o token acabou de vir do token endpoint por
    // um canal autenticado, e a pergunta é só "que papéis o realm deu".
    private static bool TemPapelDeRealmManagement(string token, string papel)
    {
        try
        {
            JsonWebToken jwt = new(token);
            using var corpo = JsonDocument.Parse(Base64UrlEncoder.Decode(jwt.EncodedPayload));

            return corpo.RootElement.TryGetProperty("resource_access", out JsonElement acessos)
                   && acessos.TryGetProperty("realm-management", out JsonElement cliente)
                   && cliente.TryGetProperty("roles", out JsonElement papeis)
                   && papeis.EnumerateArray().Any(item => item.GetString() == papel);
        }
        catch (Exception excecao) when (excecao is ArgumentException or JsonException or FormatException)
        {
            return false;
        }
    }
}
```

- [ ] **Passo 8: O fixture ganha rede, mailpit e `KC_HOSTNAME`**

`KeycloakFixture.cs`, inteiro:

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.Keycloak;

[assembly: AssemblyFixture(typeof(KeycloakFixture))]

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// Um Keycloak 26.7.4 de verdade para o assembly inteiro, importando o MESMO realm que o compose usa, com um mailpit
/// na mesma rede recebendo o SMTP dele.
/// </summary>
/// <remarks>
/// <para>
/// <b>Um container por assembly</b>, e não por classe: o Keycloak leva dezenas de segundos para subir, e o xUnit v3
/// rodaria as classes em paralelo, cada uma com o seu.
/// </para>
/// <para>
/// <b>O realm é o arquivo do repositório</b>, não uma cópia de teste: o teste prova o arquivo que o compose importa.
/// O certificado e o SMTP entram pelos mesmos placeholders, por variável de ambiente.
/// </para>
/// <para>
/// <b><c>KC_HOSTNAME</c> fixo e diferente do endereço discado</b> (<see cref="HostnamePublico"/>). Assim todo teste
/// de Keycloak exercita a separação do D8: o <c>aud</c> sai do <c>PublicBaseUrl</c>, e o transporte vai pela porta
/// mapeada. Um <c>aud</c> vindo do <c>BaseUrl</c> quebra todos eles com "Invalid token audience".
/// </para>
/// <para>
/// <b>Isolamento por dado único.</b> Os testes compartilham o realm e o mailpit; cada um cria as próprias
/// Organizations com slug aleatório e usa e-mails únicos com <c>+</c>, e nunca afirma sobre contagem global. O
/// mailpit é sempre filtrado pelo destinatário exato.
/// </para>
/// </remarks>
public sealed partial class KeycloakFixture : IAsyncLifetime
{
    public const string Realm = "identity-gateway";

    /// <summary>O <c>KC_HOSTNAME</c> do container. Não resolve na máquina do teste — e não precisa: nunca é discado.</summary>
    public const string HostnamePublico = "http://keycloak.test:8081";

    /// <summary>A mesma tag do <c>docker-compose.yml</c> — um teste de arquitetura confere.</summary>
    public const string ImagemDoMailpit = "axllent/mailpit:v1.31.3";

    private const int PortaDoMailpit = 8025;

    private readonly INetwork _rede;
    private readonly IContainer _mailpit;
    private readonly KeycloakContainer _container;

    public KeycloakFixture()
    {
        Chaves = ChavesDeTeste.Gerar();

        _rede = new NetworkBuilder().Build();

        _mailpit = new ContainerBuilder(ImagemDoMailpit)
            .WithNetwork(_rede)
            .WithNetworkAliases("mailpit")
            .WithPortBinding(PortaDoMailpit, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(pedido =>
                pedido.ForPort(PortaDoMailpit).ForPath("/readyz")))
            .Build();

        _container = new KeycloakBuilder("quay.io/keycloak/keycloak:26.7.4")
            .WithNetwork(_rede)
            .WithRealm(CaminhoDoRealm())
            .WithEnvironment("GATEWAY_CLIENT_CERT", Chaves.CertificadoBase64)
            .WithEnvironment("KC_HOSTNAME", HostnamePublico)
            .WithEnvironment("KC_HOSTNAME_BACKCHANNEL_DYNAMIC", "true")
            .WithEnvironment("SMTP_HOST", "mailpit")
            .WithEnvironment("SMTP_PORT", "1025")
            .WithEnvironment("SMTP_FROM", "convites@identity-gateway.test")
            .Build();
    }

    // Internal, não public: ParDeChaves é internal (ChavesDeTeste.cs), e uma propriedade não pode ser mais
    // acessível que o próprio tipo. Nenhum teste precisa da chave por fora — só CriarProvider a usa como padrão.
    internal ParDeChaves Chaves { get; }

    public string BaseUrl => _container.GetBaseAddress().TrimEnd('/');

    /// <summary>Interface e API do mailpit, pela porta mapeada.</summary>
    public string MailpitUrl => $"http://{_mailpit.Hostname}:{_mailpit.GetMappedPublicPort(PortaDoMailpit)}";

    public async ValueTask InitializeAsync()
    {
        await _rede.CreateAsync();
        await Task.WhenAll(_mailpit.StartAsync(), _container.StartAsync());
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        await _mailpit.DisposeAsync();
        await _rede.DisposeAsync();
    }

    /// <summary>Slug aleatório, válido e curto — o isolamento entre testes.</summary>
    public static TenantSlug SlugUnico() => TenantSlug.Create($"t-{Guid.NewGuid():N}"[..18]).Value;

    /// <summary>E-mail único com <c>+</c>: exercita o escape da query em todo teste e evita o conflito do D5.</summary>
    public static string EmailUnico() => $"admin+{Guid.NewGuid():N}@acme.test";

    /// <summary>E-mail único de exatamente 254 caracteres: parte local de 64, domínio em rótulos de até 63.</summary>
    public static string EmailUnicoDe254Caracteres() =>
        $"admin+{Guid.NewGuid():N}{new string('a', 26)}@{new string('b', 63)}.{new string('c', 63)}."
        + $"{new string('d', 56)}.test";

    /// <summary>
    /// A composição real (<c>AddInfrastructure</c>) apontada para este Keycloak, com handlers de teste opcionais
    /// acrescentados antes de construir.
    /// </summary>
    public ServiceProvider CriarProvider(Action<IServiceCollection>? ajustar = null, string? pem = null)
    {
        ServiceCollection services = KeycloakHealthCheckTests.ColecaoDaComposicao(
            BaseUrl, pem ?? Chaves.PemPrivado, HostnamePublico);
        ajustar?.Invoke(services);

        return services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>Cliente HTTP autenticado como admin do realm master — para preparar e conferir estado.</summary>
    public async Task<HttpClient> CriarClienteMasterAsync(CancellationToken cancellationToken)
    {
        HttpClient http = new() { BaseAddress = new Uri($"{BaseUrl}/") };

        using FormUrlEncodedContent corpo = new(
        [
            new("grant_type", "password"),
            new("client_id", "admin-cli"),
            new("username", KeycloakBuilder.DefaultUsername),
            new("password", KeycloakBuilder.DefaultPassword),
        ]);

        using HttpResponseMessage resposta = await http.PostAsync(
            new Uri("realms/master/protocol/openid-connect/token", UriKind.Relative), corpo, cancellationToken);
        resposta.EnsureSuccessStatusCode();

        using var token = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync(cancellationToken));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", token.RootElement.GetProperty("access_token").GetString());

        return http;
    }

    /// <summary>Cria uma Organization por fora da Gateway, como o master faria.</summary>
    public async Task<string> CriarOrganizacaoComoMasterAsync(
        string alias, string? tenantId, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);

        Dictionary<string, object> organizacao = new()
        {
            ["name"] = alias,
            ["alias"] = alias,
            ["enabled"] = true,
        };

        if (tenantId is not null)
        {
            organizacao["attributes"] = new Dictionary<string, string[]> { ["gateway_tenant_id"] = [tenantId] };
        }

        using HttpResponseMessage resposta = await master.PostAsJsonAsync(
            $"admin/realms/{Realm}/organizations", organizacao, cancellationToken);
        resposta.EnsureSuccessStatusCode();

        return resposta.Headers.Location!.Segments[^1];
    }

    /// <summary>A Organization em JSON cru, lida pelo master — nunca pelo DTO do adaptador sob teste.</summary>
    public async Task<JsonElement> LerOrganizacaoCruaAsync(string id, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        string json = await master.GetStringAsync(
            new Uri($"admin/realms/{Realm}/organizations/{id}", UriKind.Relative), cancellationToken);

        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>Quantas Organizations têm o alias — pela chave especial <c>alias</c> do <c>q</c>.</summary>
    public async Task<int> ContarPorAliasAsync(string alias, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        string json = await master.GetStringAsync(
            new Uri($"admin/realms/{Realm}/organizations?q={Uri.EscapeDataString($"alias:{alias}")}&max=10",
                UriKind.Relative),
            cancellationToken);

        using var lista = JsonDocument.Parse(json);
        return lista.RootElement.GetArrayLength();
    }

    /// <summary>Os ids das mensagens do mailpit cujo destinatário é exatamente o informado.</summary>
    /// <remarks>
    /// A busca <c>to:</c> do mailpit casa por trecho — <c>x@acme.test</c> acharia <c>pre.x@acme.test</c>. O filtro
    /// exato é feito aqui, sobre <c>To[].Address</c>.
    /// </remarks>
    public async Task<IReadOnlyList<string>> MensagensParaAsync(string destinatario, CancellationToken cancellationToken)
    {
        using HttpClient http = new() { BaseAddress = new Uri($"{MailpitUrl}/") };
        string consulta = Uri.EscapeDataString($"to:\"{destinatario}\"");

        using var busca = JsonDocument.Parse(await http.GetStringAsync(
            new Uri($"api/v1/search?query={consulta}", UriKind.Relative), cancellationToken));

        return
        [
            .. busca.RootElement.GetProperty("messages").EnumerateArray()
                .Where(mensagem => mensagem.GetProperty("To").EnumerateArray().Any(para =>
                    string.Equals(para.GetProperty("Address").GetString(), destinatario, StringComparison.OrdinalIgnoreCase)))
                .Select(mensagem => mensagem.GetProperty("ID").GetString()!),
        ];
    }

    /// <summary>Espera até haver ao menos <paramref name="quantidade"/> mensagens para o destinatário (até 5 s).</summary>
    /// <remarks>
    /// O Keycloak envia dentro da requisição de <c>execute-actions-email</c>, então o e-mail já deveria estar lá quando
    /// a chamada volta; a espera curta só absorve a gravação do mailpit.
    /// </remarks>
    public async Task<IReadOnlyList<string>> EsperarMensagensAsync(
        string destinatario, int quantidade, CancellationToken cancellationToken)
    {
        for (int tentativa = 0; tentativa < 20; tentativa++)
        {
            IReadOnlyList<string> ids = await MensagensParaAsync(destinatario, cancellationToken);

            if (ids.Count >= quantidade)
            {
                return ids;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        return await MensagensParaAsync(destinatario, cancellationToken);
    }

    /// <summary>O corpo em texto da mensagem (o HTML traz o <c>&amp;</c> do link escapado).</summary>
    public async Task<string> TextoDaMensagemAsync(string id, CancellationToken cancellationToken)
    {
        using HttpClient http = new() { BaseAddress = new Uri($"{MailpitUrl}/") };

        using var mensagem = JsonDocument.Parse(await http.GetStringAsync(
            new Uri($"api/v1/message/{id}", UriKind.Relative), cancellationToken));

        return mensagem.RootElement.GetProperty("Text").GetString()!;
    }

    /// <summary>O link de ações do primeiro convite para o destinatário, com o host público.</summary>
    public async Task<Uri> LinkDoConviteAsync(string destinatario, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> ids = await EsperarMensagensAsync(destinatario, 1, cancellationToken);
        ids.Should().NotBeEmpty("o convite precisa estar no mailpit");

        Match link = LinkDeAcoes().Match(await TextoDaMensagemAsync(ids[0], cancellationToken));
        link.Success.Should().BeTrue("o e-mail precisa trazer o link com o endereço público (KC_HOSTNAME)");

        return new Uri(link.Value);
    }

    [GeneratedRegex(@"http://keycloak\.test:8081/realms/identity-gateway/login-actions/action-token\?key=\S+")]
    private static partial Regex LinkDeAcoes();

    private static string CaminhoDoRealm()
    {
        DirectoryInfo? pasta = new(AppContext.BaseDirectory);

        while (pasta is not null && !File.Exists(Path.Combine(pasta.FullName, "IdentityGateway.slnx")))
        {
            pasta = pasta.Parent;
        }

        return Path.Combine(
            pasta?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada."),
            "keycloak", "bootstrap", "realm-identity-gateway.json");
    }
}
```

Se a estratégia de espera padrão do `KeycloakBuilder` (Testcontainers 4.15) consultar o endereço do realm e o
`KC_HOSTNAME` a atrapalhar (o container não fica pronto em ~2 min), o sintoma aparece no primeiro teste de Keycloak:
nesse caso, acrescentar `.WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(pedido =>
pedido.ForPort(9000).ForPath("/health/ready")))` ao builder e registrar no handoff. (A porta de gestão 9000 não é
afetada pelo `KC_HOSTNAME`.)

- [ ] **Passo 9: A composição do provisionamento passa o `PublicBaseUrl` e o ambiente**

Em `ComposicaoDoProvisionamento.Criar`, acrescentar ao dicionário, depois de `["Keycloak:Admin:BaseUrl"]`:

```csharp
                ["Keycloak:Admin:PublicBaseUrl"] = KeycloakFixture.HostnamePublico,
```

e, logo depois de `services.AddLogging();`, `services.ComAmbiente();`.

Conferir que nenhuma composição contra o Keycloak real ficou sem o `PublicBaseUrl`:

Run: `grep -rn "Keycloak:Admin:BaseUrl" tests --include=*.cs`
Expected: `KeycloakHealthCheckTests.cs` (com `PublicBaseUrl` logo abaixo), `ComposicaoDoProvisionamento.cs` (idem),
e as composições que não falam com o Keycloak real (`DependencyInjectionTests`, `KeycloakAdminOptionsTests`,
`KeycloakTokenClientTests`, `IdentityGatewayApiFactory` em `http://127.0.0.1:9`).

- [ ] **Passo 10: Rodar e ver passar**

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests`
Expected: PASS — **todos** os testes de Keycloak existentes continuam verdes com o `KC_HOSTNAME` diferente do
endereço discado, e os novos passam. Docker ligado.

- [ ] **Passo 11: 🧪 Prova por mutação**

1. `AssertionAudience` devolvendo `$"{BaseUrl.TrimEnd('/')}/realms/{Realm}"` → todos os testes contra o Keycloak real vermelhos (`invalid_client: Invalid token audience`) e `Criar_ComPublicBaseUrl_AudEOEmissorPublico` vermelho. Reverter.
2. `TokenEndpoint` derivado do `PublicBaseUrl` quando presente → `ObterAsync_ComPublicBaseUrl_ContinuaChamandoOBaseUrl` e `ComPublicBaseUrl_AudUsaOPublicoEOTransporteContinuaNoBaseUrl` vermelhos (e os testes reais, por DNS de `keycloak.test`). Reverter.
3. `HttpSoEmDesenvolvimento` devolvendo sempre `true` → `AllowInsecureHttpForaDeDevelopment_FalhaAoValidar` e `PublicBaseUrlHttpForaDeDevelopment_FalhaAoValidar` vermelhos. Reverter.
4. Tirar a checagem de `manage-users` do health check (devolver `Healthy` logo depois do token) → `TokenSemManageUsers_UnhealthyMandandoRecriarOsVolumes` vermelho. Reverter.

- [ ] **Passo 12: Commit**

```bash
git add src tests
git commit -m "feat: aud do assertion pelo endereco publico e mailpit nos testes

KeycloakAdminOptions separa o endereco publico (PublicBaseUrl, so o aud
do client assertion) do transporte (BaseUrl, token endpoint e Admin API).
AllowInsecureHttp e PublicBaseUrl em http so em Development. O health
check exige manage-users no token e manda rodar docker compose down -v.
O KeycloakFixture sobe um mailpit na mesma rede e fixa KC_HOSTNAME
diferente do endereco discado, e todo teste de Keycloak passa a provar
a separacao.

Mutacoes: aud pelo BaseUrl, token pelo PublicBaseUrl, http aceito em
producao e health check sem manage-users deixaram vermelhos os testes
correspondentes."
```

---

### Tarefa 8: Adaptador `EnsureInvitedUserAsync`

**Arquivos:**
- Create: `src/IdentityGateway.Application/Common/Abstractions/InviteData.cs`
- Modify: `src/IdentityGateway.Application/Common/Abstractions/IIdentityProvider.cs`
- Create: `src/IdentityGateway.Infrastructure/Identity/Keycloak/UserRepresentation.cs`
- Modify: `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakAdminClient.cs`
- Modify: `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakIdentityProvider.cs`
- Modify: `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakLogs.cs`
- Create: `tests/IdentityGateway.Application.UnitTests/Common/InviteDataTests.cs`
- Modify: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/HandlerDeInterceptacao.cs`
- Modify: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakFixture.cs` (métodos do master para usuários)
- Modify: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/KeycloakIdentityProviderTests.cs`
- Modify: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/ResilienciaDoAdminClientTests.cs`
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/EnsureInvitedUserContraKeycloakTests.cs`

**Interfaces:**
- Consome: `Email`, `RoleName`, `ExternalUserId` (Tarefas 1-2); o realm (Tarefa 6); o fixture com mailpit e `KC_HOSTNAME` (Tarefa 7).
- Produz:
  - `public sealed record InviteData(Email Email, RoleName Role, TimeSpan LinkLifetime)` com `ToString` sem o e-mail.
  - `Task<ExternalUserId> IIdentityProvider.EnsureInvitedUserAsync(string organizationId, TenantId tenantId, InviteData invite, CancellationToken cancellationToken)`
  - `KeycloakIdentityProvider.AtributoDoUsuario` = `"tenant_id"`.
  - `KeycloakAdminClient`: `FindUsersByEmailAsync`, `FindUsersByUsernameAsync`, `CreateUserAsync`,
    `AddOrganizationMemberAsync`, `GetUserRealmRolesAsync`, `GetAvailableUserRealmRolesAsync`,
    `AddUserRealmRolesAsync`, `ExecuteActionsEmailAsync` (assinaturas no Passo 5).
  - `Interceptacao.Responder` e `Interceptacao.Detalhes` (teste).

**Fatos conferidos em execução (2026-09-29)**, no mesmo Keycloak descartável da Tarefa 6, além da §3 da spec:
- `POST /users` com `tenant_id` → `201` com o id no `Location`; o atributo volta na leitura. O mesmo e-mail de novo
  → `409`. Username igual a um e-mail que outro usuário usa como username → `409`. E-mail com caixa mista é gravado
  em minúsculas, no username e no e-mail. Username de 254 caracteres com `+` → `201`.
- `GET /users?email=admin%2Bx%40acme.test&exact=true` acha o usuário; com o `+` sem escape (`admin+x@…`) → lista
  vazia. **`GET /users` devolve a representação completa por padrão** (`UsersResource.java` L266: "default: false"
  para `briefRepresentation`): os atributos voltam mesmo sem o parâmetro, e só `briefRepresentation=true` os tira.
  O parâmetro continua explícito na query (é o que a spec pede, e protege contra mudança de padrão), mas **a mutação
  "remover `briefRepresentation=false`" não é pega por teste de integração nesta versão** — quem a pega é o teste de
  forma da URI (`KeycloakIdentityProviderTests`).
- `role-mappings/realm/available` traz `tenant-admin` com o id; o `POST` com `[{id, name}]` → `204`, repetido →
  `204`. `POST /organizations/{id}/members` com o id entre aspas → `201`; repetido → `409`.
- `PUT .../execute-actions-email?lifespan=7200` → `204`; o e-mail chega ao mailpit com o link
  `http://keycloak.test:8081/realms/identity-gateway/login-actions/action-token?key=…`; no token do `key`,
  `exp − iat = 7200` e `sub` = id do usuário. Aberto com a autoridade trocada pela porta mapeada → `200` com
  `id="kc-info-message"` (também na segunda abertura); com `lifespan=1`, depois de 3 s → `400` com
  `id="kc-error-message"`. O `key` carrega o e-mail (`eml`): o link nunca vai para log.
- Usuário desabilitado → `400 {"errorMessage":"User is disabled"}`.
- Account REST API (`/realms/identity-gateway/account/`, com `Accept: application/json`) com token de usuário comum:
  `GET` → `200` sem o `tenant_id`; `POST` trocando o `tenant_id` → `400 error-user-attribute-read-only`, e o valor
  continua o mesmo; `POST` sem tocar o atributo → `204`.

- [ ] **Passo 1: Escrever o teste do `InviteData`**

`tests/IdentityGateway.Application.UnitTests/Common/InviteDataTests.cs`:

```csharp
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.ValueObjects;

namespace IdentityGateway.Application.UnitTests.Common;

public sealed class InviteDataTests
{
    [Fact]
    public void ToString_TemPapelEPrazoMasNaoOEmail()
    {
        // O ToString gerado do record imprimiria o e-mail (D15) — e o Email.ToString já não o devolve, mas o record
        // não pode depender disso para não vazar.
        InviteData convite = new(Email.Of("segredo@acme.test").Value, RoleName.TenantAdmin, TimeSpan.FromDays(7));

        string texto = convite.ToString();

        texto.Should().NotContain("segredo").And.Contain("tenant-admin").And.Contain("7.00:00:00");
    }
}
```

- [ ] **Passo 2: Estender a interceptação de teste**

Em `HandlerDeInterceptacao.cs`, na classe `Interceptacao`, acrescentar:

```csharp
    /// <summary>Responde com esta resposta (com corpo) sem enviar ao Keycloak.</summary>
    /// <remarks>
    /// Para simular uma busca vazia (corrida do 409) ou um 500 do SMTP, que precisam de corpo ou de caminho — o
    /// <see cref="ResponderSemEnviar"/> só devolve o status.
    /// </remarks>
    public Func<HttpRequestMessage, int, HttpResponseMessage?>? Responder { get; init; }

    /// <summary>Método, caminho e status de cada chamada, na ordem.</summary>
    public ConcurrentQueue<(HttpMethod Metodo, string Caminho, HttpStatusCode? Status)> Detalhes { get; } = new();

    public int Contar(HttpMethod metodo, string sufixoDoCaminho) =>
        Detalhes.Count(chamada => chamada.Metodo == metodo
                                  && chamada.Caminho.EndsWith(sufixoDoCaminho, StringComparison.Ordinal));
```

Em `HandlerDeInterceptacao.SendAsync`, depois do bloco de `ResponderSemEnviar`:

```csharp
        if (estado.Responder?.Invoke(request, ordinal) is { } pronta)
        {
            estado.Registro.Enqueue((request.Method, pronta.StatusCode));
            estado.Detalhes.Enqueue((request.Method, request.RequestUri!.AbsolutePath, pronta.StatusCode));
            return pronta;
        }
```

e acrescentar `estado.Detalhes.Enqueue((request.Method, request.RequestUri!.AbsolutePath, status));` junto do
`Registro.Enqueue` do `ResponderSemEnviar`, e
`estado.Detalhes.Enqueue((request.Method, request.RequestUri!.AbsolutePath, resposta.StatusCode));` junto do
`Registro.Enqueue` depois de `base.SendAsync`.

- [ ] **Passo 3: Métodos do master para usuários, no fixture**

Em `KeycloakFixture.cs`, acrescentar:

```csharp
    /// <summary>O usuário em JSON cru, lido pelo master.</summary>
    public async Task<JsonElement> LerUsuarioCruAsync(string id, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        string json = await master.GetStringAsync(
            new Uri($"admin/realms/{Realm}/users/{id}", UriKind.Relative), cancellationToken);

        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>Os usuários com o e-mail exato, lidos pelo master.</summary>
    public async Task<IReadOnlyList<JsonElement>> UsuariosPorEmailAsync(string email, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        string json = await master.GetStringAsync(
            new Uri($"admin/realms/{Realm}/users?email={Uri.EscapeDataString(email)}&exact=true", UriKind.Relative),
            cancellationToken);

        using var lista = JsonDocument.Parse(json);
        return [.. lista.RootElement.EnumerateArray().Select(usuario => usuario.Clone())];
    }

    /// <summary>Cria um usuário por fora da Gateway e devolve o id.</summary>
    public async Task<string> CriarUsuarioComoMasterAsync(object usuario, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        using HttpResponseMessage resposta = await master.PostAsJsonAsync(
            $"admin/realms/{Realm}/users", usuario, cancellationToken);
        resposta.EnsureSuccessStatusCode();

        return resposta.Headers.Location!.Segments[^1];
    }

    public async Task DesabilitarUsuarioComoMasterAsync(string id, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        using HttpResponseMessage resposta = await master.PutAsJsonAsync(
            $"admin/realms/{Realm}/users/{id}", new { enabled = false }, cancellationToken);
        resposta.EnsureSuccessStatusCode();
    }

    /// <summary>Os ids dos membros da Organization, lidos pelo master.</summary>
    public async Task<IReadOnlyList<string>> MembrosDaOrganizacaoAsync(string organizacao, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        string json = await master.GetStringAsync(
            new Uri($"admin/realms/{Realm}/organizations/{organizacao}/members", UriKind.Relative), cancellationToken);

        using var lista = JsonDocument.Parse(json);
        return [.. lista.RootElement.EnumerateArray().Select(membro => membro.GetProperty("id").GetString()!)];
    }

    /// <summary>Os papéis de realm atribuídos diretamente ao usuário, lidos pelo master.</summary>
    public async Task<IReadOnlyList<string>> PapeisDeRealmDoUsuarioAsync(string id, CancellationToken cancellationToken)
    {
        using HttpClient master = await CriarClienteMasterAsync(cancellationToken);
        string json = await master.GetStringAsync(
            new Uri($"admin/realms/{Realm}/users/{id}/role-mappings/realm", UriKind.Relative), cancellationToken);

        using var lista = JsonDocument.Parse(json);
        return [.. lista.RootElement.EnumerateArray().Select(papel => papel.GetProperty("name").GetString()!)];
    }

    /// <summary>
    /// Token de um usuário comum do realm, por senha, num client de teste criado para isso.
    /// </summary>
    /// <remarks>
    /// Um client próprio, público e com direct grant: o <c>admin-cli</c> do realm não tem <c>fullScopeAllowed</c>, e o
    /// token dele não traria os papéis do client <c>account</c> que a Account REST API exige.
    /// </remarks>
    public async Task<string> TokenDeUsuarioComumAsync(string username, string senha, CancellationToken cancellationToken)
    {
        string clientId = $"teste-conta-{Guid.NewGuid():N}"[..24];

        using (HttpClient master = await CriarClienteMasterAsync(cancellationToken))
        {
            using HttpResponseMessage criado = await master.PostAsJsonAsync(
                $"admin/realms/{Realm}/clients",
                new
                {
                    clientId,
                    publicClient = true,
                    directAccessGrantsEnabled = true,
                    standardFlowEnabled = false,
                    fullScopeAllowed = true,
                },
                cancellationToken);
            criado.EnsureSuccessStatusCode();
        }

        using HttpClient http = new() { BaseAddress = new Uri($"{BaseUrl}/") };
        using FormUrlEncodedContent corpo = new(
        [
            new("grant_type", "password"),
            new("client_id", clientId),
            new("username", username),
            new("password", senha),
        ]);

        using HttpResponseMessage resposta = await http.PostAsync(
            new Uri($"realms/{Realm}/protocol/openid-connect/token", UriKind.Relative), corpo, cancellationToken);
        resposta.EnsureSuccessStatusCode();

        using var token = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync(cancellationToken));
        return token.RootElement.GetProperty("access_token").GetString()!;
    }
```

- [ ] **Passo 4: Escrever os testes do adaptador sem Keycloak (forma da requisição e ramos)**

Em `KeycloakIdentityProviderTests.cs` (com `using IdentityGateway.Domain.Members;` e
`using IdentityGateway.Domain.ValueObjects;`), acrescentar:

```csharp
    private static readonly InviteData Convite =
        new(Email.Of("admin+tag@acme.test").Value, RoleName.TenantAdmin, TimeSpan.FromDays(7));

    private static HttpResponseMessage UsuarioCriado(string id)
    {
        HttpResponseMessage resposta = new(HttpStatusCode.Created);
        resposta.Headers.Location = new Uri($"http://keycloak.test/admin/realms/identity-gateway/users/{id}");
        return resposta;
    }

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status);

    private static string UsuarioDoTenant(string id, params string[] acoes) =>
        $$"""[{"id":"{{id}}","username":"admin+tag@acme.test","email":"admin+tag@acme.test","enabled":true,"requiredActions":[{{string.Join(",", acoes.Select(acao => $"\"{acao}\""))}}],"attributes":{"tenant_id":["{{Tenant.Value}}"]}}]""";

    [Fact]
    public async Task Convite_BuscaComEmailEscapadoExactEBriefRepresentationFalse()
    {
        // Forma da busca, comparada sem decodificar: o + sem escape vira espaço, e a busca exata não acha ninguém.
        // É também o único teste que pega a remoção do briefRepresentation=false — o Keycloak 26.7.4 devolve a
        // representação completa por padrão, e o teste de integração não vê diferença.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Uri? consultada = null;

        (KeycloakIdentityProvider adaptador, List<HttpMethod> _) = Montar(
            pedido =>
            {
                consultada = pedido.RequestUri;
                return Lista(UsuarioDoTenant("u-1"));
            },
            _ => Status(HttpStatusCode.Created),
            _ => Lista("""[{"id":"r-1","name":"tenant-admin"}]"""));

        await adaptador.EnsureInvitedUserAsync("org-1", Tenant, Convite, ct);

        consultada!.AbsolutePath.Should().EndWith("/admin/realms/identity-gateway/users");
        consultada.Query.Should().Be("?email=admin%2Btag%40acme.test&exact=true&briefRepresentation=false");
    }

    [Fact]
    public async Task UsuarioQueJaAceitou_NaoEnviaEmail()
    {
        // D12: sem UPDATE_PASSWORD pendente, o convite já foi aceito; um link agora trocaria a senha de um admin ativo.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (KeycloakIdentityProvider adaptador, List<HttpMethod> metodos) = Montar(
            _ => Lista(UsuarioDoTenant("u-1")),
            _ => Status(HttpStatusCode.Conflict),
            _ => Lista("""[{"id":"r-1","name":"tenant-admin"}]"""));

        ExternalUserId sub = await adaptador.EnsureInvitedUserAsync("org-1", Tenant, Convite, ct);

        sub.Value.Should().Be("u-1");
        metodos.Should().Equal(HttpMethod.Get, HttpMethod.Post, HttpMethod.Get);
    }

    [Fact]
    public async Task ConflitoSemNossoUsuarioNaReconsulta_LancaInconsistenciaSemLaco()
    {
        // Uma reconsulta só (por e-mail e por username), nunca em laço: o 409 também sai quando outro usuário tem
        // username igual ao nosso e-mail.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (KeycloakIdentityProvider adaptador, List<HttpMethod> metodos) = Montar(
            _ => Lista("[]"),
            _ => Status(HttpStatusCode.Conflict),
            _ => Lista("[]"),
            _ => Lista("""[{"id":"outro","username":"admin+tag@acme.test","email":"outro@acme.test","enabled":true}]"""));

        Func<Task> convidar = () => adaptador.EnsureInvitedUserAsync("org-1", Tenant, Convite, ct);

        (await convidar.Should().ThrowAsync<IdentityProviderInconsistencyException>())
            .Which.Message.Should().Contain(Tenant.Value.ToString()).And.NotContain("admin+tag");
        metodos.Should().Equal(HttpMethod.Get, HttpMethod.Post, HttpMethod.Get, HttpMethod.Get);
    }

    [Fact]
    public async Task UsuarioDeOutroTenant_LancaInconsistenciaSemOEmail()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (KeycloakIdentityProvider adaptador, List<HttpMethod> metodos) = Montar(_ => Lista(
            """[{"id":"u-9","email":"admin+tag@acme.test","enabled":true,"attributes":{"tenant_id":["0199a1b2-0000-7000-8000-000000000999"]}}]"""));

        Func<Task> convidar = () => adaptador.EnsureInvitedUserAsync("org-1", Tenant, Convite, ct);

        (await convidar.Should().ThrowAsync<IdentityProviderInconsistencyException>())
            .Which.Message.Should().NotContain("admin+tag");
        metodos.Should().Equal(HttpMethod.Get);
    }

    [Fact]
    public async Task EnvioCom400_LancaInconsistenciaSemOEmail()
    {
        // 400 no execute-actions-email: usuário desabilitado à mão ou sem e-mail. Repetir não corrige.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (KeycloakIdentityProvider adaptador, List<HttpMethod> _) = Montar(
            _ => Lista(UsuarioDoTenant("u-1", "UPDATE_PASSWORD", "VERIFY_EMAIL")),
            _ => Status(HttpStatusCode.Created),
            _ => Lista("""[{"id":"r-1","name":"tenant-admin"}]"""),
            _ => Status(HttpStatusCode.BadRequest));

        Func<Task> convidar = () => adaptador.EnsureInvitedUserAsync("org-1", Tenant, Convite, ct);

        (await convidar.Should().ThrowAsync<IdentityProviderInconsistencyException>())
            .Which.Message.Should().NotContain("admin+tag");
    }

    [Fact]
    public async Task EnvioCom500_SobeComoTransitorio()
    {
        // SMTP fora do ar vira 500 no Keycloak (UserResource.java L1073-1075): transitório, o Outbox repete.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (KeycloakIdentityProvider adaptador, List<HttpMethod> _) = Montar(
            _ => Lista(UsuarioDoTenant("u-1", "UPDATE_PASSWORD", "VERIFY_EMAIL")),
            _ => Status(HttpStatusCode.Created),
            _ => Lista("""[{"id":"r-1","name":"tenant-admin"}]"""),
            _ => Status(HttpStatusCode.InternalServerError));

        Func<Task> convidar = () => adaptador.EnsureInvitedUserAsync("org-1", Tenant, Convite, ct);

        await convidar.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task UsuarioNovo_CriaComUsernameIgualAoEmailHabilitadoComAcoesEAtributoEEnviaComOPrazo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string? corpoDaCriacao = null;
        Uri? envio = null;

        (KeycloakIdentityProvider adaptador, List<HttpMethod> metodos) = Montar(
            _ => Lista("[]"),
            pedido =>
            {
                corpoDaCriacao = pedido.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
                return UsuarioCriado("u-novo");
            },
            _ => Status(HttpStatusCode.Created),
            _ => Lista("[]"),
            _ => Lista("""[{"id":"r-1","name":"tenant-admin"},{"id":"r-2","name":"offline_access"}]"""),
            _ => Status(HttpStatusCode.NoContent),
            pedido =>
            {
                envio = pedido.RequestUri;
                return Status(HttpStatusCode.NoContent);
            });

        ExternalUserId sub = await adaptador.EnsureInvitedUserAsync("org-1", Tenant, Convite, ct);

        sub.Value.Should().Be("u-novo");
        metodos.Should().Equal(
            HttpMethod.Get, HttpMethod.Post, HttpMethod.Post, HttpMethod.Get, HttpMethod.Get, HttpMethod.Post,
            HttpMethod.Put);
        corpoDaCriacao.Should().Contain("\"username\":\"admin+tag@acme.test\"")
            .And.Contain("\"enabled\":true")
            .And.Contain("\"requiredActions\":[\"UPDATE_PASSWORD\",\"VERIFY_EMAIL\"]")
            .And.Contain($"\"tenant_id\":[\"{Tenant.Value}\"]");
        envio!.Query.Should().Be("?lifespan=604800", "7 dias em segundos inteiros (TotalSeconds, não Seconds)");
    }
```

- [ ] **Passo 5: Escrever os testes contra o Keycloak real**

`tests/IdentityGateway.Infrastructure.IntegrationTests/Identity/Keycloak/EnsureInvitedUserContraKeycloakTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;

/// <summary>
/// <c>EnsureInvitedUserAsync</c> contra o Keycloak real e o mailpit: os cinco passos, a idempotência, as
/// inconsistências, as falhas no meio e o link.
/// </summary>
/// <remarks>
/// Toda conferência de estado é leitura crua pelo master, nunca pelo DTO do adaptador sob teste: o Keycloak ignora em
/// silêncio ação obrigatória desconhecida e atributo não declarado, e um DTO que os ecoasse aprovaria o defeito.
/// </remarks>
public sealed class EnsureInvitedUserContraKeycloakTests(KeycloakFixture keycloak)
{
    private static readonly TimeSpan Prazo = TimeSpan.FromHours(2);

    private static InviteData Convite(string email, RoleName? papel = null) =>
        new(Email.Of(email).Value, papel ?? RoleName.TenantAdmin, Prazo);

    private static async Task<string> OrganizacaoAsync(IIdentityProvider identidade, TenantId tenant, CancellationToken ct) =>
        await identidade.EnsureOrganizationAsync(tenant, KeycloakFixture.SlugUnico(), "Acme", ct);

    private static HttpResponseMessage ListaVazia() =>
        new(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") };

    [Fact]
    public async Task ConviteNovo_UsuarioHabilitadoComTenantIdAcoesVinculoPapelEUmEmail()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();
        IIdentityProvider identidade = provider.GetRequiredService<IIdentityProvider>();
        var tenant = TenantId.New();
        string email = KeycloakFixture.EmailUnico();
        string organizacao = await OrganizacaoAsync(identidade, tenant, ct);

        ExternalUserId sub = await identidade.EnsureInvitedUserAsync(organizacao, tenant, Convite(email), ct);

        JsonElement cru = await keycloak.LerUsuarioCruAsync(sub.Value, ct);
        cru.GetProperty("username").GetString().Should().Be(email);
        cru.GetProperty("email").GetString().Should().Be(email);
        cru.GetProperty("enabled").GetBoolean().Should().BeTrue("D4: desabilitado, o Keycloak recusa o envio e o clique");
        cru.GetProperty("requiredActions").EnumerateArray().Select(acao => acao.GetString())
            .Should().BeEquivalentTo(new[] { "UPDATE_PASSWORD", "VERIFY_EMAIL" });
        cru.GetProperty("attributes").GetProperty("tenant_id").EnumerateArray().Select(valor => valor.GetString())
            .Should().Equal(tenant.Value.ToString());

        (await keycloak.MembrosDaOrganizacaoAsync(organizacao, ct)).Should().Contain(sub.Value);
        (await keycloak.PapeisDeRealmDoUsuarioAsync(sub.Value, ct)).Should().Contain("tenant-admin");
        (await keycloak.EsperarMensagensAsync(email, 1, ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task ChamadoDuasVezes_MesmoSubUmUsuarioEDoisEmails()
    {
        // Sem aceite, o usuário continua com UPDATE_PASSWORD, e a segunda chamada reenvia — é o duplo envio que a §4.3
        // declara. O que não pode duplicar é o usuário nem o vínculo.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();
        IIdentityProvider identidade = provider.GetRequiredService<IIdentityProvider>();
        var tenant = TenantId.New();
        string email = KeycloakFixture.EmailUnico();
        string organizacao = await OrganizacaoAsync(identidade, tenant, ct);

        ExternalUserId primeiro = await identidade.EnsureInvitedUserAsync(organizacao, tenant, Convite(email), ct);
        ExternalUserId segundo = await identidade.EnsureInvitedUserAsync(organizacao, tenant, Convite(email), ct);

        segundo.Should().Be(primeiro);
        (await keycloak.UsuariosPorEmailAsync(email, ct)).Should().ContainSingle();
        (await keycloak.MembrosDaOrganizacaoAsync(organizacao, ct)).Where(id => id == primeiro.Value)
            .Should().ContainSingle();
        (await keycloak.EsperarMensagensAsync(email, 2, ct)).Should().HaveCount(2);
    }

    [Fact]
    public async Task UsuarioPreCriadoComNossoTenantSemVinculoNemPapel_CompletaTudo()
    {
        // Retomada parcial: uma tentativa anterior criou o usuário e caiu antes do vínculo. O reaproveitamento precisa
        // seguir para os passos 3, 4 e 5 — pular algum deixaria o admin sem Organization ou sem papel, sem erro.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();
        IIdentityProvider identidade = provider.GetRequiredService<IIdentityProvider>();
        var tenant = TenantId.New();
        string email = KeycloakFixture.EmailUnico();
        string organizacao = await OrganizacaoAsync(identidade, tenant, ct);
        string preCriado = await keycloak.CriarUsuarioComoMasterAsync(new
        {
            username = email,
            email,
            enabled = true,
            requiredActions = new[] { "UPDATE_PASSWORD", "VERIFY_EMAIL" },
            attributes = new Dictionary<string, string[]> { ["tenant_id"] = [tenant.Value.ToString()] },
        }, ct);

        ExternalUserId sub = await identidade.EnsureInvitedUserAsync(organizacao, tenant, Convite(email), ct);

        sub.Value.Should().Be(preCriado);
        (await keycloak.MembrosDaOrganizacaoAsync(organizacao, ct)).Should().Contain(preCriado);
        (await keycloak.PapeisDeRealmDoUsuarioAsync(preCriado, ct)).Should().Contain("tenant-admin");
        (await keycloak.EsperarMensagensAsync(email, 1, ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task UsuarioQueJaAceitou_NenhumEmailEnviado()
    {
        // D12: sem UPDATE_PASSWORD pendente, um link trocaria a senha de um admin já ativo.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();
        IIdentityProvider identidade = provider.GetRequiredService<IIdentityProvider>();
        var tenant = TenantId.New();
        string email = KeycloakFixture.EmailUnico();
        string organizacao = await OrganizacaoAsync(identidade, tenant, ct);
        string aceito = await keycloak.CriarUsuarioComoMasterAsync(new
        {
            username = email,
            email,
            enabled = true,
            attributes = new Dictionary<string, string[]> { ["tenant_id"] = [tenant.Value.ToString()] },
        }, ct);

        ExternalUserId sub = await identidade.EnsureInvitedUserAsync(organizacao, tenant, Convite(email), ct);

        sub.Value.Should().Be(aceito);
        (await keycloak.EsperarMensagensAsync(email, 1, ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task UsuarioPreExistenteSemTenantId_LancaInconsistencia()
    {
        // D5: reaproveitar um usuário sem o nosso tenant_id poderia entregar o tenant ao platform-admin ou a uma conta
        // abandonada.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();
        IIdentityProvider identidade = provider.GetRequiredService<IIdentityProvider>();
        var tenant = TenantId.New();
        string email = KeycloakFixture.EmailUnico();
        string organizacao = await OrganizacaoAsync(identidade, tenant, ct);
        await keycloak.CriarUsuarioComoMasterAsync(new { username = email, email, enabled = true }, ct);

        Func<Task> convidar = () => identidade.EnsureInvitedUserAsync(organizacao, tenant, Convite(email), ct);

        await convidar.Should().ThrowAsync<IdentityProviderInconsistencyException>();
    }

    [Fact]
    public async Task UsuarioComTenantIdDeOutroTenant_LancaInconsistencia()
    {
        // "Igual", e não "existe": o atributo de outro tenant não é tentativa anterior deste.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();
        IIdentityProvider identidade = provider.GetRequiredService<IIdentityProvider>();
        var tenant = TenantId.New();
        string email = KeycloakFixture.EmailUnico();
        string organizacao = await OrganizacaoAsync(identidade, tenant, ct);
        await keycloak.CriarUsuarioComoMasterAsync(new
        {
            username = email,
            email,
            enabled = true,
            attributes = new Dictionary<string, string[]> { ["tenant_id"] = [TenantId.New().Value.ToString()] },
        }, ct);

        Func<Task> convidar = () => identidade.EnsureInvitedUserAsync(organizacao, tenant, Convite(email), ct);

        await convidar.Should().ThrowAsync<IdentityProviderInconsistencyException>();
    }

    [Fact]
    public async Task UsernameIgualAoEmailEmOutroUsuario_LancaInconsistenciaSemLaco()
    {
        // A busca por e-mail não acha ninguém, o POST dá 409 por causa do username, e a reconsulta acha um usuário sem o
        // nosso tenant_id. Um laço "409 → busca → POST" nunca terminaria aqui.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Interceptacao interceptacao = new();
        await using ServiceProvider provider = keycloak.CriarProvider(services => services.Interceptar(interceptacao));
        IIdentityProvider identidade = provider.GetRequiredService<IIdentityProvider>();
        var tenant = TenantId.New();
        string email = KeycloakFixture.EmailUnico();
        string organizacao = await OrganizacaoAsync(identidade, tenant, ct);
        await keycloak.CriarUsuarioComoMasterAsync(
            new { username = email, email = $"outro+{Guid.NewGuid():N}@acme.test", enabled = true }, ct);

        Func<Task> convidar = () => identidade.EnsureInvitedUserAsync(organizacao, tenant, Convite(email), ct);

        await convidar.Should().ThrowAsync<IdentityProviderInconsistencyException>();
        interceptacao.Contar(HttpMethod.Post, "/users").Should().Be(1);
    }

    [Fact]
    public async Task PreXExistente_ConviteParaXCriaOutroUsuario()
    {
        // exact=true: sem ele, a busca vira LIKE %x% e acharia pre.x — sem o nosso tenant_id, o convite de x viraria
        // inconsistência por causa de um vizinho.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();
        IIdentityProvider identidade = provider.GetRequiredService<IIdentityProvider>();
        var tenant = TenantId.New();
        string email = KeycloakFixture.EmailUnico();
        string vizinho = $"pre.{email}";
        string organizacao = await OrganizacaoAsync(identidade, tenant, ct);
        string idDoVizinho = await keycloak.CriarUsuarioComoMasterAsync(
            new { username = vizinho, email = vizinho, enabled = true }, ct);

        ExternalUserId sub = await identidade.EnsureInvitedUserAsync(organizacao, tenant, Convite(email), ct);

        sub.Value.Should().NotBe(idDoVizinho);
        (await keycloak.LerUsuarioCruAsync(sub.Value, ct)).GetProperty("email").GetString().Should().Be(email);
    }

    [Fact]
    public async Task NossoUsuarioDesabilitado_LancaInconsistencia()
    {
        // Alguém desabilitou o nosso usuário à mão: o Keycloak recusa o envio com 400, e repetir não corrige.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();
        IIdentityProvider identidade = provider.GetRequiredService<IIdentityProvider>();
        var tenant = TenantId.New();
        string email = KeycloakFixture.EmailUnico();
        string organizacao = await OrganizacaoAsync(identidade, tenant, ct);
        ExternalUserId sub = await identidade.EnsureInvitedUserAsync(organizacao, tenant, Convite(email), ct);
        await keycloak.DesabilitarUsuarioComoMasterAsync(sub.Value, ct);

        Func<Task> convidar = () => identidade.EnsureInvitedUserAsync(organizacao, tenant, Convite(email), ct);

        (await convidar.Should().ThrowAsync<IdentityProviderInconsistencyException>())
            .Which.Message.Should().NotContain(email);
    }

    [Fact]
    public async Task PapelAusenteNoRealm_LancaInconsistencia()
    {
        // O papel não aparece nem nos atribuídos nem nos disponíveis: o realm não é o que a Gateway espera.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();
        IIdentityProvider identidade = provider.GetRequiredService<IIdentityProvider>();
        var tenant = TenantId.New();
        string organizacao = await OrganizacaoAsync(identidade, tenant, ct);
        RoleName inexistente = RoleName.From($"papel-{Guid.NewGuid():N}");

        Func<Task> convidar = () => identidade.EnsureInvitedUserAsync(
            organizacao, tenant, Convite(KeycloakFixture.EmailUnico(), inexistente), ct);

        await convidar.Should().ThrowAsync<IdentityProviderInconsistencyException>();
    }

    [Fact]
    public async Task CorridaNoPost_O409ReencontraONossoUsuario()
    {
        // Duas entregas concorrentes: a primeira busca desta chamada volta vazia (interceptada), o POST real dá 409 —
        // porque a outra entrega criou o usuário —, e a reconsulta o reencontra pelo tenant_id.
        CancellationToken ct = TestContext.Current.CancellationToken;
        var tenant = TenantId.New();
        string email = KeycloakFixture.EmailUnico();
        string organizacao;
        ExternalUserId vencedor;

        await using (ServiceProvider primeiro = keycloak.CriarProvider())
        {
            IIdentityProvider identidade = primeiro.GetRequiredService<IIdentityProvider>();
            organizacao = await OrganizacaoAsync(identidade, tenant, ct);
            vencedor = await identidade.EnsureInvitedUserAsync(organizacao, tenant, Convite(email), ct);
        }

        Interceptacao interceptacao = new()
        {
            Responder = (pedido, ordinal) =>
                pedido.Method == HttpMethod.Get && ordinal == 1 && pedido.RequestUri!.AbsolutePath.EndsWith("/users", StringComparison.Ordinal)
                    ? ListaVazia()
                    : null,
        };
        await using ServiceProvider segundo = keycloak.CriarProvider(services => services.Interceptar(interceptacao));

        ExternalUserId sub = await segundo.GetRequiredService<IIdentityProvider>()
            .EnsureInvitedUserAsync(organizacao, tenant, Convite(email), ct);

        sub.Should().Be(vencedor);
        interceptacao.Detalhes.Should().Contain(chamada =>
            chamada.Metodo == HttpMethod.Post && chamada.Caminho.EndsWith("/users", StringComparison.Ordinal)
            && chamada.Status == HttpStatusCode.Conflict);
        (await keycloak.UsuariosPorEmailAsync(email, ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task SmtpFora_RetryReaproveitaOUsuarioEEnviaNaVolta()
    {
        // Foco de revisão 4. O 500 do SMTP é injetado: derrubar o mailpit compartilhado quebraria os testes paralelos.
        // Que "SMTP fora do ar = 500" vem da leitura de código (UserResource.java L1073-1075).
        CancellationToken ct = TestContext.Current.CancellationToken;
        Interceptacao interceptacao = new()
        {
            ResponderSemEnviar = (pedido, ordinal) =>
                pedido.Method == HttpMethod.Put && ordinal == 1 ? HttpStatusCode.InternalServerError : null,
        };
        await using ServiceProvider provider = keycloak.CriarProvider(services => services.Interceptar(interceptacao));
        IIdentityProvider identidade = provider.GetRequiredService<IIdentityProvider>();
        var tenant = TenantId.New();
        string email = KeycloakFixture.EmailUnico();
        string organizacao = await OrganizacaoAsync(identidade, tenant, ct);

        Func<Task> primeira = () => identidade.EnsureInvitedUserAsync(organizacao, tenant, Convite(email), ct);
        await primeira.Should().ThrowAsync<HttpRequestException>();
        (await keycloak.MensagensParaAsync(email, ct)).Should().BeEmpty();

        ExternalUserId sub = await identidade.EnsureInvitedUserAsync(organizacao, tenant, Convite(email), ct);

        interceptacao.Contar(HttpMethod.Post, "/users").Should().Be(1, "o usuário da primeira tentativa é reaproveitado");
        (await keycloak.UsuariosPorEmailAsync(email, ct)).Should().ContainSingle()
            .Which.GetProperty("id").GetString().Should().Be(sub.Value);
        (await keycloak.EsperarMensagensAsync(email, 1, ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task QuedaNoVinculo_RetryCompletaSemDuplicar()
    {
        // Foco de revisão 3: usuário criado, Keycloak cai no vínculo. POST não é repetido pela resiliência, então a
        // exceção sobe; a próxima entrega retoma do usuário existente.
        CancellationToken ct = TestContext.Current.CancellationToken;
        bool[] jaCaiu = [false];
        Interceptacao interceptacao = new()
        {
            Responder = (pedido, _) =>
            {
                if (pedido.Method == HttpMethod.Post && pedido.RequestUri!.AbsolutePath.EndsWith("/members", StringComparison.Ordinal) && !jaCaiu[0])
                {
                    jaCaiu[0] = true;
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }

                return null;
            },
        };
        await using ServiceProvider provider = keycloak.CriarProvider(services => services.Interceptar(interceptacao));
        IIdentityProvider identidade = provider.GetRequiredService<IIdentityProvider>();
        var tenant = TenantId.New();
        string email = KeycloakFixture.EmailUnico();
        string organizacao = await OrganizacaoAsync(identidade, tenant, ct);

        Func<Task> primeira = () => identidade.EnsureInvitedUserAsync(organizacao, tenant, Convite(email), ct);
        await primeira.Should().ThrowAsync<HttpRequestException>();

        ExternalUserId sub = await identidade.EnsureInvitedUserAsync(organizacao, tenant, Convite(email), ct);

        (await keycloak.UsuariosPorEmailAsync(email, ct)).Should().ContainSingle();
        (await keycloak.MembrosDaOrganizacaoAsync(organizacao, ct)).Should().Contain(sub.Value);
        (await keycloak.PapeisDeRealmDoUsuarioAsync(sub.Value, ct)).Should().Contain("tenant-admin");
        (await keycloak.EsperarMensagensAsync(email, 1, ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task QuedaNoPapel_RetryCompletaSemDuplicar()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        bool[] jaCaiu = [false];
        Interceptacao interceptacao = new()
        {
            Responder = (pedido, _) =>
            {
                if (pedido.Method == HttpMethod.Post && pedido.RequestUri!.AbsolutePath.EndsWith("/role-mappings/realm", StringComparison.Ordinal) && !jaCaiu[0])
                {
                    jaCaiu[0] = true;
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }

                return null;
            },
        };
        await using ServiceProvider provider = keycloak.CriarProvider(services => services.Interceptar(interceptacao));
        IIdentityProvider identidade = provider.GetRequiredService<IIdentityProvider>();
        var tenant = TenantId.New();
        string email = KeycloakFixture.EmailUnico();
        string organizacao = await OrganizacaoAsync(identidade, tenant, ct);

        Func<Task> primeira = () => identidade.EnsureInvitedUserAsync(organizacao, tenant, Convite(email), ct);
        await primeira.Should().ThrowAsync<HttpRequestException>();

        ExternalUserId sub = await identidade.EnsureInvitedUserAsync(organizacao, tenant, Convite(email), ct);

        (await keycloak.UsuariosPorEmailAsync(email, ct)).Should().ContainSingle();
        (await keycloak.PapeisDeRealmDoUsuarioAsync(sub.Value, ct)).Should().Contain("tenant-admin");
        (await keycloak.EsperarMensagensAsync(email, 1, ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task LinkDoEmail_PrazoDaPoliticaSubDoUsuarioEAbreAPaginaDeAcoes()
    {
        // Prazo de 2 h, diferente do padrão do realm (12 h) e do da política (7 dias): só passa se o lifespan chegou.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();
        IIdentityProvider identidade = provider.GetRequiredService<IIdentityProvider>();
        var tenant = TenantId.New();
        string email = KeycloakFixture.EmailUnico();
        string organizacao = await OrganizacaoAsync(identidade, tenant, ct);

        ExternalUserId sub = await identidade.EnsureInvitedUserAsync(organizacao, tenant, Convite(email), ct);
        Uri link = await keycloak.LinkDoConviteAsync(email, ct);

        string chave = Uri.UnescapeDataString(link.Query.TrimStart('?').Split('&')
            .Single(par => par.StartsWith("key=", StringComparison.Ordinal))[4..]);
        using var carga = JsonDocument.Parse(Base64UrlEncoder.Decode(new JsonWebToken(chave).EncodedPayload));
        long exp = carga.RootElement.GetProperty("exp").GetInt64();
        long iat = carga.RootElement.GetProperty("iat").GetInt64();
        (exp - iat).Should().BeCloseTo((long)Prazo.TotalSeconds, 2);
        carga.RootElement.GetProperty("sub").GetString().Should().Be(sub.Value);

        // Com o KC_HOSTNAME fixo, o clique é validado contra o emissor gravado no token, não contra o host da
        // requisição: trocar só a autoridade pela porta mapeada abre a mesma página que o navegador veria.
        Uri local = new($"{keycloak.BaseUrl}{link.PathAndQuery}");
        using HttpClient navegador = new();
        using HttpResponseMessage pagina = await navegador.GetAsync(local, ct);
        string html = await pagina.Content.ReadAsStringAsync(ct);

        pagina.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("id=\"kc-info-message\"").And.NotContain("id=\"kc-error-message\"");
    }

    [Fact]
    public async Task UsuarioCriadoPeloMasterComCaixaMista_EReaproveitado()
    {
        // Foco de revisão 1: o Keycloak grava em minúsculas, e o Email.Of também normaliza — a busca exata dos dois
        // lados concorda, e o usuário de uma tentativa anterior é reencontrado.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();
        IIdentityProvider identidade = provider.GetRequiredService<IIdentityProvider>();
        var tenant = TenantId.New();
        string marca = Guid.NewGuid().ToString("N").ToUpperInvariant();
        string organizacao = await OrganizacaoAsync(identidade, tenant, ct);
        string preCriado = await keycloak.CriarUsuarioComoMasterAsync(new
        {
            username = $"Admin+{marca}@Acme.Test",
            email = $"Admin+{marca}@Acme.Test",
            enabled = true,
            requiredActions = new[] { "UPDATE_PASSWORD" },
            attributes = new Dictionary<string, string[]> { ["tenant_id"] = [tenant.Value.ToString()] },
        }, ct);

        ExternalUserId sub = await identidade.EnsureInvitedUserAsync(
            organizacao, tenant, Convite($"  ADMIN+{marca}@acme.TEST "), ct);

        sub.Value.Should().Be(preCriado);
        (await keycloak.EsperarMensagensAsync($"admin+{marca.ToLowerInvariant()}@acme.test", 1, ct))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task EmailDe254Caracteres_ViraUsernameNoKeycloak()
    {
        // Foco de revisão 5: parte local de 64 (o limite do Keycloak) e username de 254 (o perfil aceita 255).
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = keycloak.CriarProvider();
        IIdentityProvider identidade = provider.GetRequiredService<IIdentityProvider>();
        var tenant = TenantId.New();
        string email = KeycloakFixture.EmailUnicoDe254Caracteres();
        email.Length.Should().Be(254);
        string organizacao = await OrganizacaoAsync(identidade, tenant, ct);

        ExternalUserId sub = await identidade.EnsureInvitedUserAsync(organizacao, tenant, Convite(email), ct);

        (await keycloak.LerUsuarioCruAsync(sub.Value, ct)).GetProperty("username").GetString().Should().Be(email);
    }

    [Fact]
    public async Task UsuarioComum_NaoAlteraOTenantIdPelaAccountApi()
    {
        // D10: o tenant_id é a correlação do convite e a fonte do claim da §12.2. Editável pelo usuário, ele se
        // mudaria de tenant.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string username = KeycloakFixture.EmailUnico();
        const string senha = "Senha-de-teste-1";
        string original = TenantId.New().Value.ToString();
        string id = await keycloak.CriarUsuarioComoMasterAsync(new
        {
            username,
            email = username,
            emailVerified = true,
            firstName = "Comum",
            lastName = "Teste",
            enabled = true,
            attributes = new Dictionary<string, string[]> { ["tenant_id"] = [original] },
            credentials = new[] { new { type = "password", value = senha, temporary = false } },
        }, ct);

        string token = await keycloak.TokenDeUsuarioComumAsync(username, senha, ct);
        using HttpClient conta = new() { BaseAddress = new Uri($"{keycloak.BaseUrl}/") };
        conta.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        conta.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        Uri rota = new($"realms/{KeycloakFixture.Realm}/account/", UriKind.Relative);

        // Controle: a mesma rota aceita uma alteração permitida — o 400 abaixo não é de autenticação.
        using HttpResponseMessage permitida = await conta.PostAsJsonAsync(
            rota, new { username, email = username, firstName = "Comum2", lastName = "Teste" }, ct);
        using HttpResponseMessage sequestro = await conta.PostAsJsonAsync(
            rota,
            new
            {
                username,
                email = username,
                firstName = "Comum2",
                lastName = "Teste",
                attributes = new Dictionary<string, string[]> { ["tenant_id"] = [TenantId.New().Value.ToString()] },
            },
            ct);

        permitida.StatusCode.Should().Be(HttpStatusCode.NoContent);
        sequestro.IsSuccessStatusCode.Should().BeFalse();
        (await keycloak.LerUsuarioCruAsync(id, ct)).GetProperty("attributes").GetProperty("tenant_id")[0]
            .GetString().Should().Be(original);
    }
}
```

Em `ResilienciaDoAdminClientTests.cs` (com `using IdentityGateway.Domain.Members;` e
`using IdentityGateway.Domain.ValueObjects;`):

```csharp
    [Fact]
    public async Task PutDoEnvioComRespostaPerdida_NaoERepetidoEOEmailJaSaiu()
    {
        // O Keycloak envia dentro da requisição: um timeout no PUT não significa "não enviado". Por isso o PUT fica fora
        // do retry automático (DisableForUnsafeHttpMethods), como o POST — e a próxima entrega reenvia, porque o usuário
        // continua com UPDATE_PASSWORD.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Interceptacao interceptacao = new()
        {
            PerderResposta = (pedido, ordinal) => pedido.Method == HttpMethod.Put && ordinal == 1,
        };
        await using ServiceProvider provider = keycloak.CriarProvider(services => services.Interceptar(interceptacao));
        IIdentityProvider identidade = provider.GetRequiredService<IIdentityProvider>();
        var tenant = TenantId.New();
        string email = KeycloakFixture.EmailUnico();
        string organizacao = await identidade.EnsureOrganizationAsync(tenant, KeycloakFixture.SlugUnico(), "Acme", ct);
        InviteData convite = new(Email.Of(email).Value, RoleName.TenantAdmin, TimeSpan.FromHours(2));

        Func<Task> primeira = () => identidade.EnsureInvitedUserAsync(organizacao, tenant, convite, ct);

        await primeira.Should().ThrowAsync<HttpRequestException>();
        interceptacao.Contar(HttpMethod.Put).Should().Be(1);
        (await keycloak.EsperarMensagensAsync(email, 1, ct)).Should().ContainSingle("o Keycloak enviou antes de a resposta se perder");

        await identidade.EnsureInvitedUserAsync(organizacao, tenant, convite, ct);

        interceptacao.Contar(HttpMethod.Put).Should().Be(2);
        (await keycloak.EsperarMensagensAsync(email, 2, ct)).Should().HaveCount(2);
    }
```

- [ ] **Passo 6: Rodar e ver falhar**

Run: `dotnet build IdentityGateway.slnx`
Expected: FAIL de compilação — `InviteData` e `EnsureInvitedUserAsync` não existem.

- [ ] **Passo 7: A porta**

`src/IdentityGateway.Application/Common/Abstractions/InviteData.cs`:

```csharp
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.ValueObjects;

namespace IdentityGateway.Application.Common.Abstractions;

/// <summary>
/// O que o provedor de identidade precisa para convidar alguém.
/// </summary>
/// <remarks>
/// O papel vem de quem convida (D11), e o prazo, da <see cref="IInvitationPolicy"/> (D9): o adaptador não decide
/// nenhum dos dois.
/// </remarks>
/// <param name="Email">Endereço do convidado; vira também o username no Keycloak.</param>
/// <param name="Role">Papel de realm atribuído no convite.</param>
/// <param name="LinkLifetime">Por quanto tempo o link do e-mail vale.</param>
public sealed record InviteData(Email Email, RoleName Role, TimeSpan LinkLifetime)
{
    /// <summary>Sem o e-mail: o <c>ToString</c> gerado do record o imprimiria (D15).</summary>
    public override string ToString() => $"InviteData {{ Role = {Role}, LinkLifetime = {LinkLifetime} }}";
}
```

Em `IIdentityProvider.cs`:

1. `using IdentityGateway.Domain.Members;` no topo.
2. No segundo parágrafo do `<remarks>` da interface, trocar "Declarar as outras cinco da §11.3 agora" por
"Declarar as outras quatro da §11.3 agora".
3. Depois de `EnsureOrganizationAsync`:

```csharp
    /// <summary>
    /// Garante que o convidado existe, pertence à Organization, tem o papel e — se ainda não aceitou — recebeu o
    /// e-mail com o link. Devolve o <c>sub</c> dele.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>O <paramref name="tenantId"/> é a chave da correlação</b> (atributo de usuário <c>tenant_id</c>, §12.2): um
    /// usuário com o mesmo e-mail e o mesmo tenant é tentativa anterior e é reaproveitado; com qualquer outro valor, ou
    /// sem valor, é conta alheia (D5). A assinatura acrescenta o tenant à da §11.3, como o I1 fez em
    /// <see cref="EnsureOrganizationAsync"/>.
    /// </para>
    /// <para>
    /// Mesmo contrato de erro de <see cref="EnsureOrganizationAsync"/>: só
    /// <see cref="IdentityProviderInconsistencyException"/> é permanente. <b>Nenhuma exceção carrega o e-mail</b>; elas
    /// levam o tenant (D15).
    /// </para>
    /// <para>
    /// <b>O e-mail pode sair mais de uma vez:</b> enquanto o convidado não aceitar, cada chamada reenvia, e qualquer
    /// falha entre esta chamada e o commit faz a mensagem voltar (§4.3 da spec da fatia C).
    /// </para>
    /// </remarks>
    /// <param name="organizationId">Organization do tenant, devolvida por <see cref="EnsureOrganizationAsync"/>.</param>
    /// <param name="tenantId">Tenant do convite; é gravado no usuário e correlaciona as tentativas.</param>
    /// <param name="invite">E-mail, papel e prazo do link.</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    /// <exception cref="IdentityProviderInconsistencyException">
    /// O e-mail ou o username pertence a um usuário que não é deste tenant, o nosso usuário foi desabilitado à mão, ou
    /// o papel não existe no realm. Repetir não resolve.
    /// </exception>
    Task<ExternalUserId> EnsureInvitedUserAsync(
        string organizationId, TenantId tenantId, InviteData invite, CancellationToken cancellationToken);
```

- [ ] **Passo 8: Representações e cliente da Admin API**

`src/IdentityGateway.Infrastructure/Identity/Keycloak/UserRepresentation.cs`:

```csharp
namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// A <c>UserRepresentation</c> da Admin API, só com os campos usados.
/// </summary>
/// <remarks>
/// Nulos ficam fora do JSON (ver <c>KeycloakAdminClient</c>): o Keycloak interpreta campo presente como intenção, e um
/// <c>"attributes": null</c> numa atualização apagaria os atributos.
/// </remarks>
internal sealed record UserRepresentation(
    string? Id,
    string? Username,
    string? Email,
    bool? Enabled,
    List<string>? RequiredActions,
    Dictionary<string, List<string>>? Attributes);

/// <summary>A <c>RoleRepresentation</c> da Admin API: atribuir papel exige o id, não só o nome.</summary>
internal sealed record RoleRepresentation(string Id, string Name);

/// <summary>O Keycloak respondeu 400: a requisição não serve para o estado atual do recurso.</summary>
/// <remarks>Interna: nunca sai do adaptador. O <c>KeycloakIdentityProvider</c> a traduz em inconsistência.</remarks>
internal sealed class KeycloakBadRequestException : Exception
{
    public KeycloakBadRequestException()
    {
    }

    public KeycloakBadRequestException(string message)
        : base(message)
    {
    }

    public KeycloakBadRequestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
```

Em `KeycloakAdminClient.cs`: `using System.Globalization;` no topo; trocar a propriedade `Organizations` por:

```csharp
    private string Realm => Uri.EscapeDataString(options.Value.Realm);

    private string Organizations => $"admin/realms/{Realm}/organizations";

    private string Users => $"admin/realms/{Realm}/users";
```

e acrescentar, depois de `FindOrganizationByAttributeAsync`:

```csharp
    /// <summary>Os usuários com o e-mail exato, com atributos e ações obrigatórias.</summary>
    /// <remarks>
    /// <c>exact=true</c>: sem ele a busca vira <c>LIKE %x%</c>. <c>briefRepresentation=false</c>: explícito, porque
    /// a forma resumida não traz atributos — na 26.7.4 o padrão de <c>/users</c> já é a completa, e o parâmetro protege
    /// contra a mudança desse padrão. O e-mail vai escapado: o <c>+</c> cru viraria espaço.
    /// </remarks>
    public Task<IReadOnlyList<UserRepresentation>> FindUsersByEmailAsync(string email, CancellationToken cancellationToken) =>
        BuscarUsuariosAsync("email", email, cancellationToken);

    /// <summary>Os usuários com o username exato, com atributos e ações obrigatórias.</summary>
    public Task<IReadOnlyList<UserRepresentation>> FindUsersByUsernameAsync(
        string username, CancellationToken cancellationToken) =>
        BuscarUsuariosAsync("username", username, cancellationToken);

    /// <summary>Cria o usuário e devolve o id que o Keycloak atribuiu.</summary>
    /// <exception cref="KeycloakConflictException">E-mail ou username já em uso (409).</exception>
    public async Task<string> CreateUserAsync(UserRepresentation usuario, CancellationToken cancellationToken)
    {
        using StringContent corpo = new(JsonSerializer.Serialize(usuario, Json), Encoding.UTF8, "application/json");

        using HttpResponseMessage resposta = await http.PostAsync(
            new Uri(Users, UriKind.Relative), corpo, cancellationToken);

        if (resposta.StatusCode == HttpStatusCode.Conflict)
        {
            throw new KeycloakConflictException("O Keycloak respondeu 409 ao criar o usuário.");
        }

        resposta.EnsureSuccessStatusCode();

        Uri local = resposta.Headers.Location
                    ?? throw new HttpRequestException("O Keycloak respondeu 201 sem o cabeçalho Location.");

        return local.Segments[^1].TrimEnd('/');
    }

    /// <summary>Vincula o usuário à Organization. Já vinculado (409) conta como sucesso.</summary>
    /// <remarks>Exige <c>manage-organizations</c> e <c>manage-users</c> (<c>OrganizationMemberResource</c>).</remarks>
    public async Task AddOrganizationMemberAsync(
        string organizationId, string userId, CancellationToken cancellationToken)
    {
        // O corpo é o id como string JSON (entre aspas); o Keycloak aceita com ou sem aspas.
        using StringContent corpo = new(JsonSerializer.Serialize(userId), Encoding.UTF8, "application/json");

        using HttpResponseMessage resposta = await http.PostAsync(
            new Uri($"{Organizations}/{Uri.EscapeDataString(organizationId)}/members", UriKind.Relative),
            corpo,
            cancellationToken);

        if (resposta.StatusCode == HttpStatusCode.Conflict)
        {
            return;
        }

        resposta.EnsureSuccessStatusCode();
    }

    /// <summary>Os papéis de realm atribuídos diretamente ao usuário.</summary>
    /// <remarks>
    /// Pelos endpoints do próprio usuário, que exigem só a visão de usuários: ler o papel por <c>GET /roles/{nome}</c>
    /// exigiria <c>view-realm</c>, que o service account não tem.
    /// </remarks>
    public Task<IReadOnlyList<RoleRepresentation>> GetUserRealmRolesAsync(
        string userId, CancellationToken cancellationToken) =>
        LerPapeisAsync($"{Users}/{Uri.EscapeDataString(userId)}/role-mappings/realm", cancellationToken);

    /// <summary>Os papéis de realm que ainda podem ser atribuídos ao usuário, com o id de cada um.</summary>
    public Task<IReadOnlyList<RoleRepresentation>> GetAvailableUserRealmRolesAsync(
        string userId, CancellationToken cancellationToken) =>
        LerPapeisAsync($"{Users}/{Uri.EscapeDataString(userId)}/role-mappings/realm/available", cancellationToken);

    /// <summary>Atribui papéis de realm ao usuário. Reatribuir é inofensivo.</summary>
    public async Task AddUserRealmRolesAsync(
        string userId, IReadOnlyList<RoleRepresentation> papeis, CancellationToken cancellationToken)
    {
        using StringContent corpo = new(JsonSerializer.Serialize(papeis, Json), Encoding.UTF8, "application/json");

        using HttpResponseMessage resposta = await http.PostAsync(
            new Uri($"{Users}/{Uri.EscapeDataString(userId)}/role-mappings/realm", UriKind.Relative),
            corpo,
            cancellationToken);

        resposta.EnsureSuccessStatusCode();
    }

    /// <summary>Envia o e-mail com o link de ações, que vale <paramref name="prazoEmSegundos"/>.</summary>
    /// <remarks>
    /// O Keycloak envia dentro da requisição: timeout aqui não significa "não enviado". <c>PUT</c> fica fora do retry
    /// automático (<c>DisableForUnsafeHttpMethods</c>), como o <c>POST</c>.
    /// </remarks>
    /// <exception cref="KeycloakBadRequestException">400: usuário desabilitado, sem e-mail ou ação inválida.</exception>
    public async Task ExecuteActionsEmailAsync(
        string userId, IReadOnlyList<string> acoes, int prazoEmSegundos, CancellationToken cancellationToken)
    {
        using StringContent corpo = new(JsonSerializer.Serialize(acoes, Json), Encoding.UTF8, "application/json");
        string prazo = prazoEmSegundos.ToString(CultureInfo.InvariantCulture);

        using HttpResponseMessage resposta = await http.PutAsync(
            new Uri($"{Users}/{Uri.EscapeDataString(userId)}/execute-actions-email?lifespan={prazo}", UriKind.Relative),
            corpo,
            cancellationToken);

        // Decide pelo status, nunca pelo texto do corpo ("User is disabled" é detalhe do Keycloak).
        if (resposta.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new KeycloakBadRequestException("O Keycloak respondeu 400 ao enviar o e-mail de ações.");
        }

        resposta.EnsureSuccessStatusCode();
    }

    private async Task<IReadOnlyList<UserRepresentation>> BuscarUsuariosAsync(
        string campo, string valor, CancellationToken cancellationToken)
    {
        List<UserRepresentation>? achados = await http.GetFromJsonAsync<List<UserRepresentation>>(
            new Uri($"{Users}?{campo}={Uri.EscapeDataString(valor)}&exact=true&briefRepresentation=false", UriKind.Relative),
            Json,
            cancellationToken);

        return achados ?? [];
    }

    private async Task<IReadOnlyList<RoleRepresentation>> LerPapeisAsync(
        string caminho, CancellationToken cancellationToken)
    {
        List<RoleRepresentation>? papeis = await http.GetFromJsonAsync<List<RoleRepresentation>>(
            new Uri(caminho, UriKind.Relative), Json, cancellationToken);

        return papeis ?? [];
    }
```

- [ ] **Passo 9: Os cinco passos no adaptador**

Em `KeycloakLogs.cs`, acrescentar (e trocar, no `<remarks>` da classe, "o token dá <c>manage-organizations</c> sobre o
realm" por "o token dá <c>manage-organizations</c> e <c>manage-users</c> sobre o realm", e acrescentar a frase
"<b>Nem o e-mail do convidado</b>: o que identifica o convite nos logs é o tenant."):

```csharp
    [LoggerMessage(
        EventId = 2205,
        Level = LogLevel.Debug,
        Message = "Keycloak: usuário do convite do tenant {TenantId} já existia; reaproveitado")]
    public static partial void UsuarioJaExistia(ILogger logger, Guid tenantId);

    [LoggerMessage(
        EventId = 2206,
        Level = LogLevel.Information,
        Message = "Keycloak: usuário do convite do tenant {TenantId} criado")]
    public static partial void UsuarioCriado(ILogger logger, Guid tenantId);

    [LoggerMessage(
        EventId = 2207,
        Level = LogLevel.Information,
        Message = "Keycloak: corrida na criação do usuário do tenant {TenantId} resolvida pela reconsulta")]
    public static partial void CorridaDoUsuarioResolvida(ILogger logger, Guid tenantId);

    [LoggerMessage(
        EventId = 2208,
        Level = LogLevel.Error,
        Message = "Keycloak: o e-mail do convite do tenant {TenantId} pertence a usuário não correlacionado")]
    public static partial void UsuarioNaoCorrelacionado(ILogger logger, Guid tenantId);

    [LoggerMessage(
        EventId = 2209,
        Level = LogLevel.Information,
        Message = "Keycloak: papel {Papel} atribuído ao convidado do tenant {TenantId}")]
    public static partial void PapelAtribuido(ILogger logger, string papel, Guid tenantId);

    [LoggerMessage(
        EventId = 2210,
        Level = LogLevel.Error,
        Message = "Keycloak: o papel {Papel} do convite do tenant {TenantId} não existe no realm")]
    public static partial void PapelAusente(ILogger logger, string papel, Guid tenantId);

    [LoggerMessage(
        EventId = 2211,
        Level = LogLevel.Information,
        Message = "Keycloak: e-mail de convite do tenant {TenantId} enviado")]
    public static partial void ConviteEnviado(ILogger logger, Guid tenantId);

    [LoggerMessage(
        EventId = 2212,
        Level = LogLevel.Information,
        Message = "Keycloak: convite do tenant {TenantId} já aceito; e-mail não reenviado")]
    public static partial void ConviteJaAceito(ILogger logger, Guid tenantId);

    [LoggerMessage(
        EventId = 2213,
        Level = LogLevel.Error,
        Message = "Keycloak: envio do convite do tenant {TenantId} recusado com 400 (usuário desabilitado ou sem e-mail)")]
    public static partial void EnvioRecusado(ILogger logger, Guid tenantId);
```

Em `KeycloakIdentityProvider.cs`: `using IdentityGateway.Domain.Members;` no topo; no `<remarks>` da classe,
acrescentar o parágrafo:

```csharp
/// <para>
/// <b>O convite segue a mesma ideia, pelo atributo de usuário <see cref="AtributoDoUsuario"/></b>: buscar, criar,
/// vincular, atribuir o papel e enviar, cada passo idempotente, para que uma entrega que caiu no meio seja retomada
/// pela próxima sem duplicar nada.
/// </para>
```

e acrescentar à classe:

```csharp
    /// <summary>
    /// Atributo de usuário que correlaciona o convidado ao tenant — o mesmo <c>tenant_id</c> que a §12.2 define como
    /// fonte do claim. Declarado no User Profile do realm só para <c>admin</c> (D10): sem a declaração, o Keycloak o
    /// descartaria em silêncio.
    /// </summary>
    internal const string AtributoDoUsuario = "tenant_id";

    private const string AtualizarSenha = "UPDATE_PASSWORD";
    private const string VerificarEmail = "VERIFY_EMAIL";

    public async Task<ExternalUserId> EnsureInvitedUserAsync(
        string organizationId, TenantId tenantId, InviteData invite, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        ArgumentNullException.ThrowIfNull(invite);

        UserRepresentation usuario = await ObterOuCriarUsuarioAsync(invite.Email.Value, tenantId, cancellationToken);
        string id = usuario.Id!;

        // Passo 3: o 409 (já é membro) é tratado como sucesso no cliente.
        await admin.AddOrganizationMemberAsync(organizationId, id, cancellationToken);

        await GarantirPapelAsync(id, invite.Role, tenantId, cancellationToken);

        // Passo 5 (D12): só com UPDATE_PASSWORD pendente. Sem ele, o convite foi aceito, e um link trocaria a senha de
        // um admin já ativo.
        if (usuario.RequiredActions?.Contains(AtualizarSenha) != true)
        {
            KeycloakLogs.ConviteJaAceito(logger, tenantId.Value);
            return ExternalUserId.From(id);
        }

        try
        {
            await admin.ExecuteActionsEmailAsync(
                id, [AtualizarSenha, VerificarEmail], (int)invite.LinkLifetime.TotalSeconds, cancellationToken);
        }
        catch (KeycloakBadRequestException excecao)
        {
            KeycloakLogs.EnvioRecusado(logger, tenantId.Value);

            throw new IdentityProviderInconsistencyException(
                $"O Keycloak recusou o envio do convite do tenant {tenantId.Value}: o usuário foi desabilitado ou "
                + "está sem e-mail.",
                excecao);
        }

        KeycloakLogs.ConviteEnviado(logger, tenantId.Value);
        return ExternalUserId.From(id);
    }

    /// <summary>Passos 1 e 2: acha o nosso usuário pelo e-mail ou o cria; conta alheia é inconsistência.</summary>
    private async Task<UserRepresentation> ObterOuCriarUsuarioAsync(
        string email, TenantId tenantId, CancellationToken cancellationToken)
    {
        string tenant = tenantId.Value.ToString();
        IReadOnlyList<UserRepresentation> achados = await admin.FindUsersByEmailAsync(email, cancellationToken);

        if (achados.Count > 0)
        {
            UserRepresentation? nosso = achados.FirstOrDefault(usuario => EhDoTenant(usuario, tenant));

            if (nosso is null)
            {
                KeycloakLogs.UsuarioNaoCorrelacionado(logger, tenantId.Value);
                throw ContaAlheia(tenantId);
            }

            KeycloakLogs.UsuarioJaExistia(logger, tenantId.Value);
            return nosso;
        }

        UserRepresentation novo = new(
            Id: null,
            Username: email,
            Email: email,
            Enabled: true,
            RequiredActions: [AtualizarSenha, VerificarEmail],
            Attributes: new() { [AtributoDoUsuario] = [tenant] });

        try
        {
            string id = await admin.CreateUserAsync(novo, cancellationToken);
            KeycloakLogs.UsuarioCriado(logger, tenantId.Value);

            return novo with { Id = id };
        }
        catch (KeycloakConflictException)
        {
            // UMA reconsulta, por e-mail e por username, e nunca em laço: o 409 também sai quando outro usuário tem
            // username igual ao nosso e-mail — e nesse caso repetir o POST daria 409 para sempre.
            List<UserRepresentation> candidatos =
            [
                .. await admin.FindUsersByEmailAsync(email, cancellationToken),
                .. await admin.FindUsersByUsernameAsync(email, cancellationToken),
            ];

            UserRepresentation? vencedor = candidatos.FirstOrDefault(usuario => EhDoTenant(usuario, tenant));

            if (vencedor is not null)
            {
                // Duas entregas da mesma mensagem concorreram, e a outra criou primeiro.
                KeycloakLogs.CorridaDoUsuarioResolvida(logger, tenantId.Value);
                return vencedor;
            }

            KeycloakLogs.UsuarioNaoCorrelacionado(logger, tenantId.Value);
            throw ContaAlheia(tenantId);
        }
    }

    /// <summary>Passo 4: o papel pelos endpoints do próprio usuário (sem <c>view-realm</c>).</summary>
    private async Task GarantirPapelAsync(
        string userId, RoleName papel, TenantId tenantId, CancellationToken cancellationToken)
    {
        IReadOnlyList<RoleRepresentation> atribuidos = await admin.GetUserRealmRolesAsync(userId, cancellationToken);

        if (atribuidos.Any(atribuido => atribuido.Name == papel.Value))
        {
            return;
        }

        IReadOnlyList<RoleRepresentation> disponiveis =
            await admin.GetAvailableUserRealmRolesAsync(userId, cancellationToken);
        RoleRepresentation? alvo = disponiveis.FirstOrDefault(disponivel => disponivel.Name == papel.Value);

        if (alvo is null)
        {
            // Nem atribuído nem disponível: o papel não existe no realm. O realm não é o que a Gateway espera, e
            // repetir não corrige.
            KeycloakLogs.PapelAusente(logger, papel.Value, tenantId.Value);

            throw new IdentityProviderInconsistencyException(
                $"O papel de realm '{papel.Value}' do convite do tenant {tenantId.Value} não existe no realm.");
        }

        await admin.AddUserRealmRolesAsync(userId, [alvo], cancellationToken);
        KeycloakLogs.PapelAtribuido(logger, papel.Value, tenantId.Value);
    }

    // Igual, e não "existe": o tenant_id de outro tenant não é tentativa anterior deste.
    private static bool EhDoTenant(UserRepresentation usuario, string tenant) =>
        usuario.Attributes is not null
        && usuario.Attributes.TryGetValue(AtributoDoUsuario, out List<string>? valores)
        && valores is [var unico]
        && unico == tenant;

    // Sem o e-mail na mensagem (D15): o OutboxProcessor grava a mensagem da exceção em outbox_messages.error.
    private static IdentityProviderInconsistencyException ContaAlheia(TenantId tenantId) =>
        new($"O e-mail ou o username do convite do tenant {tenantId.Value} pertence a um usuário não correlacionado.");
```

- [ ] **Passo 10: Rodar e ver passar**

Run: `dotnet test tests/IdentityGateway.Application.UnitTests --filter "FullyQualifiedName~InviteDataTests"`
Expected: PASS.

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter "FullyQualifiedName~Identity.Keycloak"`
Expected: PASS — inclui os 19 testes de `EnsureInvitedUserContraKeycloakTests`, os 7 novos de
`KeycloakIdentityProviderTests` e o novo de `ResilienciaDoAdminClientTests`. Docker ligado.

Run: `dotnet test tests/IdentityGateway.ArchitectureTests`
Expected: PASS (`UserRepresentation`, `RoleRepresentation` e `KeycloakBadRequestException` são internos ao namespace
do Keycloak).

- [ ] **Passo 11: 🧪 Prova por mutação**

Uma por vez, rodando o filtro `FullyQualifiedName~Identity.Keycloak`, e revertendo cada uma:

1. `EhDoTenant` aceitando qualquer valor (`&& valores.Count > 0`, sem comparar) → `UsuarioComTenantIdDeOutroTenant_LancaInconsistencia` e `UsuarioDeOutroTenant_LancaInconsistenciaSemOEmail` vermelhos. Aceitando sem atributo (`usuario.Attributes is null ||` na frente) → `UsuarioPreExistenteSemTenantId_LancaInconsistencia` vermelho.
2. Laço sem limite depois do 409 (no `catch`, chamar `ObterOuCriarUsuarioAsync` de novo em vez de reconsultar) → `ConflitoSemNossoUsuarioNaReconsulta_LancaInconsistenciaSemLaco` vermelho (índice fora da lista de respostas) e `UsernameIgualAoEmailEmOutroUsuario_…` vermelho (mais de um POST, ou nunca termina — use o timeout do teste).
3. Tirar o `Uri.EscapeDataString(valor)` da busca → `ChamadoDuasVezes_MesmoSubUmUsuarioEDoisEmails` vermelho (a segunda busca não acha, o POST dá 409, a reconsulta também não acha) e `Convite_BuscaComEmailEscapado…` vermelho.
4. Remover `&exact=true` → `PreXExistente_ConviteParaXCriaOutroUsuario` vermelho.
5. Remover `&briefRepresentation=false` → **só** `Convite_BuscaComEmailEscapadoExactEBriefRepresentationFalse` vermelho. Registrar no handoff que é mutante equivalente contra a 26.7.4 (o padrão de `/users` é a representação completa).
6. `(int)invite.LinkLifetime.TotalSeconds` → `invite.LinkLifetime.Seconds` → `LinkDoEmail_PrazoDaPolitica…` vermelho (`exp − iat` 0) e `UsuarioNovo_…EnviaComOPrazo` vermelho (`lifespan=0`). Tirar o `?lifespan=` da URL → `LinkDoEmail_…` vermelho (12 h do realm).
7. Pular o passo 3 ou o 4 quando o usuário foi achado (um `return` logo depois do `UsuarioJaExistia`, antes do vínculo) → `UsuarioPreCriadoComNossoTenantSemVinculoNemPapel_CompletaTudo` vermelho.
8. Enviar sem checar `UPDATE_PASSWORD` (tirar o `if`) → `UsuarioQueJaAceitou_NenhumEmailEnviado` e `UsuarioQueJaAceitou_NaoEnviaEmail` vermelhos.
9. Tirar `VerificarEmail` do `RequiredActions` da criação → `ConviteNovo_…` vermelho (leitura crua); `Enabled: false` → `ConviteNovo_…` vermelho (e o envio dá 400).
10. No realm, tirar o `tenant_id` da configuração do perfil → `ConviteNovo_…` vermelho (atributo descartado em silêncio). Trocar `edit` para `["admin","user"]` → `UsuarioComum_NaoAlteraOTenantIdPelaAccountApi` vermelho.
11. Em `KeycloakServiceCollectionExtensions`, comentar `resiliencia.Retry.DisableForUnsafeHttpMethods();` → `PutDoEnvioComRespostaPerdida_NaoERepetidoEOEmailJaSaiu` vermelho (PUT repetido, dois e-mails já na primeira chamada) e o `PostComRespostaPerdida_NaoERepetido` existente também.

- [ ] **Passo 12: Commit**

```bash
git add src tests
git commit -m "feat: convite do usuario no Keycloak em cinco passos idempotentes

EnsureInvitedUserAsync busca pelo e-mail exato (escapado), cria o
usuario habilitado com UPDATE_PASSWORD, VERIFY_EMAIL e o tenant_id, faz
uma reconsulta so depois do 409, vincula a Organization, atribui o papel
pelos endpoints do usuario e envia o execute-actions-email com o prazo
da politica so se o convite nao foi aceito. Conta alheia, usuario
desabilitado e papel ausente viram inconsistencia; nenhuma excecao leva
o e-mail. O PUT fica fora do retry automatico.

Mutacoes: tenant_id por existencia, laco no 409, sem escape, sem exact,
Seconds no prazo, passos pulados na retomada, envio sem checar o aceite,
sem VERIFY_EMAIL, perfil sem o atributo e PUT com retry deixaram
vermelhos os testes correspondentes; sem briefRepresentation=false so o
teste de forma da URI fica vermelho (mutante equivalente na 26.7.4)."
```

---

### Tarefa 9: `ProvisionTenantHandler`

**Arquivos:**
- Modify: `src/IdentityGateway.Application/Tenants/ProvisionTenant/ProvisionTenantHandler.cs` (arquivo inteiro)
- Modify: `src/IdentityGateway.Application/Tenants/ProvisionTenant/ProvisioningLogs.cs`
- Modify: `src/IdentityGateway.Domain/Tenants/Tenant.cs` (remove `MarkProvisioned`)
- Create: `tests/IdentityGateway.Application.UnitTests/ColetorDeLogs.cs`
- Modify: `tests/IdentityGateway.Application.UnitTests/Tenants/ProvisionTenant/ProvisionTenantHandlerTests.cs` (arquivo inteiro)
- Modify: `tests/IdentityGateway.Domain.UnitTests/Tenants/TenantTests.cs` (remove os cinco testes do `MarkProvisioned`)
- Modify: `tests/IdentityGateway.Infrastructure.IntegrationTests/Provisioning/ComposicaoDoProvisionamento.cs` (`RegistrarAsync` com e-mail único — ver "Desvios deliberados")

**Interfaces:**
- Consome: `EnsureInvitedUserAsync`, `InviteData` (Tarefa 8); `IInvitationPolicy` (Tarefa 5); `IMemberRepository` (Tarefa 4); `Tenant.CompleteProvisioning`, `HasSeatAvailable`, `InitialAdminEmail` (Tarefa 3); `RoleName.TenantAdmin` (Tarefa 2).
- Produz:
  - `ProvisionTenantHandler(ITenantRepository tenants, IMemberRepository membros, IIdentityProvider identidade, IProvisioningPolicy politica, IInvitationPolicy convites, IDateTimeProvider relogio, ILogger<ProvisionTenantHandler> logger)`
  - `ProvisioningLogs.SemEmailDoAdmin` (1105), `ProvisioningLogs.SemVagaParaOAdmin` (1106).
  - `ComposicaoDoProvisionamento.RegistrarAsync(ServiceProvider provider, string email, CancellationToken ct)` e a sobrecarga sem e-mail (usa `KeycloakFixture.EmailUnico()`).
  - `Tenant.MarkProvisioned` deixa de existir.

O handler é registrado sozinho pelo source generator do Mediator (como hoje), e as dependências novas já estão na DI
(Tarefas 4 e 5): não há registro novo a fazer — `DependencyInjectionTests` e os testes de integração do provisionamento
provam a resolução.

- [ ] **Passo 1: Logger de captura para os testes da Application**

`tests/IdentityGateway.Application.UnitTests/ColetorDeLogs.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace IdentityGateway.Application.UnitTests;

/// <summary>
/// Logger que guarda tudo o que recebe: a mensagem formatada, os valores estruturados e a exceção.
/// </summary>
/// <remarks>
/// Para provar o que um log NÃO contém (D15), o teste precisa ver o que o sink veria — o texto e os argumentos
/// separados, que o Seq indexa um a um.
/// </remarks>
internal sealed class ColetorDeLogs<T> : ILogger<T>
{
    public List<(LogLevel Nivel, EventId Evento, string Texto)> Registros { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        string valores = state is IEnumerable<KeyValuePair<string, object?>> pares
            ? string.Join(" ", pares.Select(par => $"{par.Key}={par.Value}"))
            : string.Empty;

        Registros.Add((logLevel, eventId, $"{formatter(state, exception)} {valores} {exception}"));
    }
}
```

- [ ] **Passo 2: Reescrever os testes do handler**

`ProvisionTenantHandlerTests.cs`, inteiro:

```csharp
using System.Net;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Tenants.ProvisionTenant;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Domain.ValueObjects;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace IdentityGateway.Application.UnitTests.Tenants.ProvisionTenant;

public sealed class ProvisionTenantHandlerTests
{
    private const string EnderecoDoAdmin = "segredo+admin@acme.test";
    private static readonly DateTimeOffset Registro = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Agora = Registro.AddMinutes(5);
    private static readonly TimeSpan Janela = TimeSpan.FromHours(24);
    private static readonly TimeSpan PrazoDoLink = TimeSpan.FromDays(3);
    private static readonly ExternalUserId Sub = ExternalUserId.From("sub-admin");

    private readonly ITenantRepository _tenants = Substitute.For<ITenantRepository>();
    private readonly IMemberRepository _membros = Substitute.For<IMemberRepository>();
    private readonly IIdentityProvider _identidade = Substitute.For<IIdentityProvider>();
    private readonly IProvisioningPolicy _politica = Substitute.For<IProvisioningPolicy>();
    private readonly IInvitationPolicy _convites = Substitute.For<IInvitationPolicy>();
    private readonly IDateTimeProvider _relogio = Substitute.For<IDateTimeProvider>();
    private readonly ColetorDeLogs<ProvisionTenantHandler> _logs = new();
    private readonly ProvisionTenantHandler _handler;

    public ProvisionTenantHandlerTests()
    {
        _politica.MaxPendingDuration.Returns(Janela);
        _convites.LinkLifetime.Returns(PrazoDoLink);
        _relogio.UtcNow.Returns(Agora);
        _handler = new ProvisionTenantHandler(
            _tenants, _membros, _identidade, _politica, _convites, _relogio, _logs);
    }

    private Tenant TenantPendente(int maxUsers = 5)
    {
        var tenant = Tenant.Register(
            "Acme", TenantSlug.Create("acme").Value, new Plan(PlanTier.Free, maxUsers, 1),
            Email.Of(EnderecoDoAdmin).Value, Registro);
        _tenants.GetAsync(tenant.Id, Arg.Any<CancellationToken>()).Returns(tenant);
        return tenant;
    }

    // Tenant registrado antes da fatia C: a coluna nasceu nula na migration. O domínio não produz esse estado — a
    // reflexão simula a linha antiga que o EF materializaria.
    private Tenant TenantPendenteSemEmail()
    {
        Tenant tenant = TenantPendente();
        typeof(Tenant).GetProperty(nameof(Tenant.InitialAdminEmail))!.SetValue(tenant, null);
        return tenant;
    }

    private void OrganizacaoDevolve(string organizacao) =>
        _identidade
            .EnsureOrganizationAsync(Arg.Any<TenantId>(), Arg.Any<TenantSlug>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(organizacao);

    private void ConviteDevolve(ExternalUserId sub) =>
        _identidade
            .EnsureInvitedUserAsync(Arg.Any<string>(), Arg.Any<TenantId>(), Arg.Any<InviteData>(), Arg.Any<CancellationToken>())
            .Returns(sub);

    // Faz lançar a chamada escolhida ("organizacao" ou "convite"); a outra funciona.
    private void ChamadaLanca(string chamada, Exception excecao)
    {
        if (chamada == "organizacao")
        {
            _identidade
                .EnsureOrganizationAsync(Arg.Any<TenantId>(), Arg.Any<TenantSlug>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(excecao);
            return;
        }

        OrganizacaoDevolve("org-1");
        _identidade
            .EnsureInvitedUserAsync(Arg.Any<string>(), Arg.Any<TenantId>(), Arg.Any<InviteData>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(excecao);
    }

    private void RelogioEm(DateTimeOffset instante) => _relogio.UtcNow.Returns(instante);

    private async Task NadaFoiChamadoNoKeycloakAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await _identidade.DidNotReceiveWithAnyArgs().EnsureOrganizationAsync(default, default!, default!, ct);
        await _identidade.DidNotReceiveWithAnyArgs().EnsureInvitedUserAsync(default!, default, default!, ct);
    }

    [Fact]
    public async Task TenantInexistente_SucessoSemChamarOProvedor()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(TenantId.New()), ct);

        resultado.IsSuccess.Should().BeTrue();
        await NadaFoiChamadoNoKeycloakAsync();
    }

    [Fact]
    public async Task TenantJaAtivo_SucessoSemChamarOProvedor()
    {
        // Mensagem repetida: o Outbox entrega at-least-once, e a segunda entrega precisa ser inofensiva.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        tenant.CompleteProvisioning("org-1", Sub, Agora);

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        await NadaFoiChamadoNoKeycloakAsync();
        _membros.DidNotReceive().Add(Arg.Any<Member>());
    }

    [Fact]
    public async Task TenantEmProvisioningFailed_NaoEReprovisionado()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        tenant.MarkProvisioningFailed();

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
        await NadaFoiChamadoNoKeycloakAsync();
    }

    [Fact]
    public async Task TenantPendente_GaranteOrganizacaoConvidaEAtivaComOMember()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        OrganizacaoDevolve("org-42");
        ConviteDevolve(Sub);
        Member? adicionado = null;
        _membros.Add(Arg.Do<Member>(membro => adicionado = membro));

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.Active);
        tenant.ExternalOrganizationId.Should().Be("org-42");
        tenant.OccupiedSeats.Should().Be(1);
        tenant.InitialAdminEmail.Should().BeNull();

        await _identidade.Received(1).EnsureOrganizationAsync(tenant.Id, tenant.Slug, tenant.Name, ct);
        await _identidade.Received(1).EnsureInvitedUserAsync(
            "org-42",
            tenant.Id,
            Arg.Is<InviteData>(convite =>
                convite.Email.Value == EnderecoDoAdmin
                && convite.Role == RoleName.TenantAdmin
                && convite.LinkLifetime == PrazoDoLink),
            ct);

        _membros.Received(1).Add(Arg.Any<Member>());
        adicionado!.TenantId.Should().Be(tenant.Id);
        adicionado.ExternalUserId.Should().Be(Sub);
        adicionado.InvitedAt.Should().Be(Agora, "CompleteProvisioning recebe relogio.UtcNow");
    }

    [Fact]
    public async Task SemEmailDoAdmin_MarcaFailedSemTocarOKeycloak()
    {
        // Só acontece com tenant registrado antes da fatia C. Sem o e-mail, ninguém seria convidado, e repetir não
        // traz o e-mail de volta: falha permanente, e o retry manual o recebe de novo (D13).
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendenteSemEmail();

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
        await NadaFoiChamadoNoKeycloakAsync();
    }

    [Fact]
    public async Task SemVaga_MarcaFailedSemTocarOKeycloak()
    {
        // D14: deixar a falta de vaga para o CompleteProvisioning faria cada retry reenviar o convite até a janela.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente(maxUsers: 0);

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
        tenant.InitialAdminEmail.Should().BeNull("D13: a falha também apaga o e-mail");
        await NadaFoiChamadoNoKeycloakAsync();
    }

    [Theory]
    [InlineData("organizacao")]
    [InlineData("convite")]
    public async Task Inconsistencia_MarcaFailedSemEsperarAJanela(string chamada)
    {
        // Erro permanente, venha de qualquer uma das duas chamadas: o mesmo try cobre as duas.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        ChamadaLanca(chamada, new IdentityProviderInconsistencyException("conta alheia no provedor"));

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
        _membros.DidNotReceive().Add(Arg.Any<Member>());
    }

    [Theory]
    [InlineData("organizacao")]
    [InlineData("convite")]
    public async Task FalhaTransitoriaDentroDaJanela_PropagaEMantemPendingComOEmail(string chamada)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        HttpRequestException falha = new("Keycloak fora do ar");
        ChamadaLanca(chamada, falha);

        Func<Task> provisionar = async () => await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        (await provisionar.Should().ThrowAsync<HttpRequestException>()).Which.Should().BeSameAs(falha);
        tenant.Status.Should().Be(TenantStatus.Pending);
        tenant.InitialAdminEmail!.Value.Should().Be(EnderecoDoAdmin, "a próxima entrega precisa do e-mail");
        _membros.DidNotReceive().Add(Arg.Any<Member>());
    }

    [Theory]
    [InlineData("organizacao")]
    [InlineData("convite")]
    public async Task FalhaTransitoriaDepoisDaJanela_MarcaFailed(string chamada)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        RelogioEm(Registro + Janela + TimeSpan.FromMinutes(1));
        ChamadaLanca(chamada, new HttpRequestException("Keycloak fora do ar"));

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
    }

    [Fact]
    public async Task SmtpForaEDeVolta_FicaPendingComEmailEDepoisAtiva()
    {
        // Foco de revisão 4: o 500 do SMTP é transitório; na volta, a mesma entrega completa.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        OrganizacaoDevolve("org-1");
        _identidade
            .EnsureInvitedUserAsync(Arg.Any<string>(), Arg.Any<TenantId>(), Arg.Any<InviteData>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new HttpRequestException("500", inner: null, HttpStatusCode.InternalServerError),
                _ => Task.FromResult(Sub));

        Func<Task> primeira = async () => await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);
        await primeira.Should().ThrowAsync<HttpRequestException>();
        tenant.Status.Should().Be(TenantStatus.Pending);
        tenant.InitialAdminEmail.Should().NotBeNull();

        Result segunda = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        segunda.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.Active);
        tenant.InitialAdminEmail.Should().BeNull();
        _membros.Received(1).Add(Arg.Any<Member>());
    }

    [Fact]
    public async Task FalhaNaFronteiraExataDaJanela_MarcaFailed()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        RelogioEm(Registro + Janela);
        ChamadaLanca("organizacao", new HttpRequestException("Keycloak fora do ar"));

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
    }

    [Fact]
    public async Task TimeoutDaResilienciaDepoisDaJanela_MarcaFailed()
    {
        // TaskCanceledException é o que o timeout cru do HttpClient.Timeout produz, e É um OperationCanceledException;
        // o filtro olha o CancellationToken, não o tipo.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        RelogioEm(Registro + Janela + TimeSpan.FromMinutes(1));
        ChamadaLanca("convite", new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        Result resultado = await _handler.Handle(new ProvisionTenantCommand(tenant.Id), ct);

        resultado.IsSuccess.Should().BeTrue();
        tenant.Status.Should().Be(TenantStatus.ProvisioningFailed);
    }

    [Fact]
    public async Task Keycloak403DentroEForaDaJanela_SoViraFailedDepoisDela()
    {
        // Um volume antigo sem manage-users dá 403 no vínculo: não é inconsistência (corrigir o realm resolve), então
        // segue a janela — e o health check avisa antes (Tarefa 7).
        CancellationToken ct = TestContext.Current.CancellationToken;
        Tenant tenant = TenantPendente();
        ChamadaLanca("convite", new HttpRequestException("Forbidden", inner: null, HttpStatusCode.Forbidden));

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
        Tenant tenant = TenantPendente();
        RelogioEm(Registro + Janela + TimeSpan.FromMinutes(1));
        using CancellationTokenSource desligando = new();
        await desligando.CancelAsync();
        ChamadaLanca("convite", new OperationCanceledException(desligando.Token));

        Func<Task> provisionar = async () =>
            await _handler.Handle(new ProvisionTenantCommand(tenant.Id), desligando.Token);

        await provisionar.Should().ThrowAsync<OperationCanceledException>();
        tenant.Status.Should().Be(TenantStatus.Pending);
    }

    [Theory]
    [InlineData("inexistente")]
    [InlineData("ativo")]
    [InlineData("semEmail")]
    [InlineData("semVaga")]
    [InlineData("sucesso")]
    [InlineData("inconsistenciaNoConvite")]
    [InlineData("janelaEsgotadaNoConvite")]
    public async Task CadaRamoQueRegistra_TemLogENenhumContemOEmail(string ramo)
    {
        // D15: contagem maior que zero em cada ramo prova que o coletor viu o log — um teste só com "nenhum contém"
        // passaria com o log desligado. O ramo transitório dentro da janela não registra aqui: a exceção sobe, e quem
        // registra é o OutboxProcessor (coberto no teste de vazamento da Tarefa 10).
        CancellationToken ct = TestContext.Current.CancellationToken;
        TenantId alvo = TenantId.New();

        switch (ramo)
        {
            case "ativo":
                Tenant ativo = TenantPendente();
                ativo.CompleteProvisioning("org-1", Sub, Agora);
                alvo = ativo.Id;
                break;
            case "semEmail":
                alvo = TenantPendenteSemEmail().Id;
                break;
            case "semVaga":
                alvo = TenantPendente(maxUsers: 0).Id;
                break;
            case "sucesso":
                alvo = TenantPendente().Id;
                OrganizacaoDevolve("org-1");
                ConviteDevolve(Sub);
                break;
            case "inconsistenciaNoConvite":
                alvo = TenantPendente().Id;
                ChamadaLanca("convite", new IdentityProviderInconsistencyException("conta alheia no provedor"));
                break;
            case "janelaEsgotadaNoConvite":
                alvo = TenantPendente().Id;
                RelogioEm(Registro + Janela + TimeSpan.FromMinutes(1));
                ChamadaLanca("convite", new HttpRequestException("Keycloak fora do ar"));
                break;
        }

        await _handler.Handle(new ProvisionTenantCommand(alvo), ct);

        _logs.Registros.Should().NotBeEmpty();
        _logs.Registros.Select(registro => registro.Texto).Should().NotContain(texto =>
            texto.Contains("segredo", StringComparison.OrdinalIgnoreCase));
    }
}
```

- [ ] **Passo 3: Rodar e ver falhar**

Run: `dotnet build IdentityGateway.slnx`
Expected: FAIL de compilação — o construtor do `ProvisionTenantHandler` não recebe `IMemberRepository` nem
`IInvitationPolicy`.

- [ ] **Passo 4: Logs novos**

Em `ProvisioningLogs.cs`, acrescentar ao `<remarks>` da classe "Nem o e-mail do admin inicial (D15)." e os dois logs:

```csharp
    [LoggerMessage(
        EventId = 1105,
        Level = LogLevel.Error,
        Message = "Provisionamento: tenant {TenantId} em ProvisioningFailed; sem o e-mail do admin inicial (registrado antes da fatia C)")]
    public static partial void SemEmailDoAdmin(ILogger logger, Guid tenantId);

    [LoggerMessage(
        EventId = 1106,
        Level = LogLevel.Error,
        Message = "Provisionamento: tenant {TenantId} em ProvisioningFailed; o plano ({MaxUsers} vagas) não comporta o admin inicial")]
    public static partial void SemVagaParaOAdmin(ILogger logger, Guid tenantId, int maxUsers);
```

- [ ] **Passo 5: Reescrever o handler**

`ProvisionTenantHandler.cs`, inteiro:

```csharp
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Common.Messaging;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using Microsoft.Extensions.Logging;

namespace IdentityGateway.Application.Tenants.ProvisionTenant;

/// <summary>
/// Provisiona o tenant: garante a Organization, convida o admin inicial e ativa — ou marca <c>ProvisioningFailed</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Contrato com quem despacha.</b> Falha transitória dentro da janela sai como a exceção original, e quem
/// despacha repete. Todo desfecho terminal — ativo, falha permanente, janela esgotada, mensagem repetida, tenant
/// inexistente — é <see cref="Result.Success()"/>, e a mensagem sai da fila. O commit é do <c>TransactionBehavior</c>.
/// </para>
/// <para>
/// <b>O que repetir não corrige é recusado antes de tocar o Keycloak:</b> tenant sem o e-mail do admin (registrado
/// antes da fatia C) e tenant cujo plano não comporta o admin (D14). Deixar a falta de vaga para
/// <c>CompleteProvisioning</c> faria cada retry reenviar o convite até esgotar a janela.
/// </para>
/// <para>
/// <b>O mesmo <c>try</c> cobre as duas chamadas ao provedor.</b> A classificação é uma só: inconsistência vai direto a
/// <c>ProvisioningFailed</c>; qualquer outro erro, com a janela esgotada e sem cancelamento, também; senão, a exceção
/// sobe. Um convite que falhou deixa o tenant em <c>Pending</c>, com o e-mail, para a próxima entrega.
/// </para>
/// <para>
/// <b>A decisão de desistir mora aqui, e não no transporte:</b> sobrevive à troca do despacho em processo pelo
/// broker sem mudar, e o relógio falso a torna testável.
/// </para>
/// </remarks>
public sealed class ProvisionTenantHandler(
    ITenantRepository tenants,
    IMemberRepository membros,
    IIdentityProvider identidade,
    IProvisioningPolicy politica,
    IInvitationPolicy convites,
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

        if (tenant.InitialAdminEmail is not { } email)
        {
            ProvisioningLogs.SemEmailDoAdmin(logger, tenant.Id.Value);
            tenant.MarkProvisioningFailed();
            return Result.Success();
        }

        if (!tenant.HasSeatAvailable)
        {
            ProvisioningLogs.SemVagaParaOAdmin(logger, tenant.Id.Value, tenant.Plan.MaxUsers);
            tenant.MarkProvisioningFailed();
            return Result.Success();
        }

        string organizacao;
        ExternalUserId admin;

        try
        {
            organizacao = await identidade.EnsureOrganizationAsync(
                tenant.Id, tenant.Slug, tenant.Name, cancellationToken);

            admin = await identidade.EnsureInvitedUserAsync(
                organizacao,
                tenant.Id,
                new InviteData(email, RoleName.TenantAdmin, convites.LinkLifetime),
                cancellationToken);
        }
        catch (IdentityProviderInconsistencyException excecao)
        {
            ProvisioningLogs.FalhaPermanente(logger, tenant.Id.Value, excecao);
            tenant.MarkProvisioningFailed();
            return Result.Success();
        }
        catch (Exception excecao) when (!cancellationToken.IsCancellationRequested && JanelaEsgotada(tenant))
        {
            // O filtro olha o token, e não o tipo da exceção — e as duas famílias que podem chegar aqui provam por
            // quê. O timeout do AddStandardResilienceHandler que envolve a Admin API chega como
            // TimeoutRejectedException, que não deriva de OperationCanceledException; o timeout cru de
            // HttpClient.Timeout do cliente do token endpoint (sem resiliência) chega como TaskCanceledException,
            // que É um OperationCanceledException. Um filtro por tipo teria que acompanhar as duas, e ainda erraria
            // a próxima. Só o desligamento do host fica de fora: a mensagem volta no próximo ciclo.
            ProvisioningLogs.JanelaEsgotada(logger, tenant.Id.Value, politica.MaxPendingDuration.TotalHours, excecao);
            tenant.MarkProvisioningFailed();
            return Result.Success();
        }

        // Ativação, vaga, Member e e-mail apagado numa operação de domínio; tenant e Member no mesmo commit (D2).
        Member membro = tenant.CompleteProvisioning(organizacao, admin, relogio.UtcNow);
        membros.Add(membro);

        ProvisioningLogs.Provisionado(logger, tenant.Id.Value);

        return Result.Success();
    }

    // Fechada no fim: RegisteredAt + janela já conta como esgotada.
    private bool JanelaEsgotada(Tenant tenant) =>
        relogio.UtcNow >= tenant.RegisteredAt + politica.MaxPendingDuration;
}
```


- [ ] **Passo 6: `MarkProvisioned` sai do domínio**

Em `Tenant.cs`, apagar o método `MarkProvisioned` inteiro (com o XML doc). Em `TenantTests.cs`, apagar o bloco
entre os comentários `// ── MarkProvisioned: removido na Tarefa 9, com estes cinco testes ──` e
`// ── fim dos testes do MarkProvisioned ──` (os cinco testes e os dois comentários).

Run: `grep -rn "MarkProvisioned\b" src tests --include=*.cs`
Expected: nenhuma linha.

- [ ] **Passo 7: E-mail único no registro da composição de integração**

Em `ComposicaoDoProvisionamento.cs`, trocar `RegistrarAsync` por:

```csharp
    /// <summary>Registra um tenant pelo caminho de produção, com e-mail de admin único.</summary>
    /// <remarks>
    /// Único por teste: com o convite no provisionamento, dois tenants com o mesmo e-mail caem no D5 (o segundo vira
    /// ProvisioningFailed), e os testes passariam a depender da ordem.
    /// </remarks>
    internal static Task<TenantId> RegistrarAsync(ServiceProvider provider, CancellationToken ct) =>
        RegistrarAsync(provider, KeycloakFixture.EmailUnico(), ct);

    /// <summary>Registra um tenant pelo caminho de produção: command, pipeline e commit com a mensagem no Outbox.</summary>
    internal static async Task<TenantId> RegistrarAsync(ServiceProvider provider, string email, CancellationToken ct)
    {
        string slug = KeycloakFixture.SlugUnico().Value;
        await using AsyncServiceScope escopo = provider.CreateAsyncScope();
        Mediator.ISender sender = escopo.ServiceProvider.GetRequiredService<Mediator.ISender>();

        Result<TenantId> resultado = await sender.Send(
            new RegisterTenantCommand("Acme Provisionamento", slug, "free", email), ct);

        resultado.IsSuccess.Should().BeTrue(resultado.IsFailure ? resultado.Error.Message : string.Empty);
        return resultado.Value;
    }
```

- [ ] **Passo 8: Rodar e ver passar**

Run: `dotnet test tests/IdentityGateway.Application.UnitTests tests/IdentityGateway.Domain.UnitTests`
Expected: PASS.

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter "FullyQualifiedName~Provisioning|FullyQualifiedName~DependencyInjectionTests"`
Expected: PASS — os três testes de `ProvisionamentoContraKeycloakTests` continuam verdes, agora com o convite no
caminho (o `KeycloakForaNoPrimeiroCiclo` responde 503 também às chamadas de usuário, e o tenant fica `Active`
quando o Keycloak volta).

- [ ] **Passo 9: 🧪 Prova por mutação**

1. Fechar o `try` logo depois do `EnsureOrganizationAsync` (o `EnsureInvitedUserAsync` fora dele) → `Inconsistencia_MarcaFailedSemEsperarAJanela("convite")` e `FalhaTransitoriaDepoisDaJanela_MarcaFailed("convite")` vermelhos. Reverter.
2. Mover a verificação de vaga para depois do `try` → `SemVaga_MarcaFailedSemTocarOKeycloak` vermelho. Reverter.
3. `RoleName.TenantAdmin` → `RoleName.From("platform-admin")` → `TenantPendente_GaranteOrganizacaoConvidaEAtivaComOMember` vermelho. Reverter.
4. Tirar `membros.Add(membro);` → o mesmo teste vermelho. Reverter.
5. Acrescentar o e-mail a um log (`SemVagaParaOAdmin` com um parâmetro `{Email}` recebendo `email.Value`) → `CadaRamoQueRegistra_TemLogENenhumContemOEmail("semVaga")` vermelho. Reverter.
6. `relogio.UtcNow` → `DateTimeOffset.UtcNow` no `CompleteProvisioning` → `…InvitedAt.Should().Be(Agora…)` vermelho. Reverter.

- [ ] **Passo 10: Commit**

```bash
git add src tests
git commit -m "feat: provisionamento convida o admin inicial antes de ativar

O handler recusa antes de tocar o Keycloak o tenant sem e-mail (registrado
antes desta fatia) e o sem vaga para o admin. O mesmo try cobre a
Organization e o convite, com o papel tenant-admin e o prazo da politica;
CompleteProvisioning recebe o relogio e o Member vai para o repositorio,
no mesmo commit. MarkProvisioned sai do dominio.

Mutacoes: try so na primeira chamada, vaga verificada depois, outro
papel, Member nao adicionado, e-mail num log e relogio do sistema
deixaram vermelhos os testes da Application."
```

---

### Tarefa 10: O e-mail fora de logs, spans, erros e respostas (D15, parte de infraestrutura)

**Arquivos:**
- Modify: `src/IdentityGateway.Api/appsettings.Development.json` (arquivo inteiro)
- Modify: `src/IdentityGateway.Infrastructure/Configuration/DatabaseOptions.cs` (`<remarks>` de `EnableSensitiveDataLogging`)
- Modify: `Directory.Packages.props` (grupo `Testes`)
- Modify: `tests/IdentityGateway.Infrastructure.IntegrationTests/IdentityGateway.Infrastructure.IntegrationTests.csproj`
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/ColetorDeLogs.cs`
- Modify: `tests/IdentityGateway.Infrastructure.IntegrationTests/Provisioning/ComposicaoDoProvisionamento.cs` (`Criar` aceita configuração extra)
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Provisioning/VazamentoDoEmailTests.cs`
- Create: `tests/IdentityGateway.ArchitectureTests/RegrasDoAmbienteLocalTests.cs`
- Create: `tests/IdentityGateway.Api.FunctionalTests/VazamentoDoEmailNaApiTests.cs`

**Interfaces:**
- Consome: a composição do provisionamento com o convite (Tarefa 9), as interceptações e os métodos do master (Tarefa 8).
- Produz:
  - `internal sealed class ColetorDeLogs : ILoggerProvider` com `ConcurrentQueue<RegistroDeLog> Registros` e `internal sealed record RegistroDeLog(string Categoria, LogLevel Nivel, string Texto)` (teste).
  - `ComposicaoDoProvisionamento.Criar(PostgresFixture postgres, KeycloakFixture keycloak, Action<IServiceCollection>? ajustar = null, IReadOnlyDictionary<string, string?>? extras = null)`.
  - `RegrasDoAmbienteLocalTests` (a Tarefa 12 acrescenta as regras do compose ao mesmo arquivo).

**Decisões desta tarefa.**
- **O `EnableSensitiveDataLogging` sai do `appsettings.Development.json`, e não só do compose.** O compose roda a api
  com `ASPNETCORE_ENVIRONMENT: Development` (é esse arquivo que ele carrega), e a IDE também: desligar só por
  variável no compose deixaria a IDE registrando o `INSERT` do e-mail e, no `DetectChanges` em `Debug`, o valor antigo
  da coluna ao apagá-la. A categoria `Microsoft.EntityFrameworkCore` sobe para `Warning` no mesmo arquivo, como
  defesa em profundidade.
- **A query `?email=` não precisa de código de redação.** Desde o .NET 9, os logs do `IHttpClientFactory` trocam a
  query por `*` ("URI query redaction in IHttpClientFactory logs", breaking change do .NET 9 Preview 7, com a chave
  `System.Net.Http.DisableUriRedaction` para desligar); a atividade nativa de `System.Net.Http`, que o
  `AddHttpClientInstrumentation` do OpenTelemetry 1.18 consome no .NET 9+, também redige a query de `url.full`. O
  teste decide: se ele ficar vermelho no estado normal, o Passo 7 tem o conserto; a mutação do Passo 8 prova que o
  teste enxerga o canal.
- **Pacote novo, só de teste: `OpenTelemetry.Exporter.InMemory` 1.18.0** (Apache-2.0, mesmo repositório e mesma
  versão das demais `OpenTelemetry.*` já fixadas). O teste monta as mesmas instrumentações que a Api registra em
  `AddTelemetria` (`AddHttpClientInstrumentation`, `AddNpgsql`) e exporta para uma lista.

- [ ] **Passo 1: Pacotes do teste**

Em `Directory.Packages.props`, no `ItemGroup Label="Testes"`, depois de `Bogus`:

```xml
    <!--
      Exporter OpenTelemetry em memória, só no teste de vazamento do e-mail (fatia C): o teste precisa ver os spans
      que a Api exportaria, com as mesmas instrumentações. Mesma versão das demais OpenTelemetry.
    -->
    <PackageVersion Include="OpenTelemetry.Exporter.InMemory" Version="1.18.0" />
```

Em `IdentityGateway.Infrastructure.IntegrationTests.csproj`, um `ItemGroup` novo:

```xml
  <!--
    OpenTelemetry só para o teste de vazamento do e-mail: as mesmas instrumentações que a Api liga (HttpClient e
    Npgsql), exportando para uma lista em memória em vez do OTLP.
  -->
  <ItemGroup>
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Http" />
    <PackageReference Include="OpenTelemetry.Exporter.InMemory" />
    <PackageReference Include="Npgsql.OpenTelemetry" />
  </ItemGroup>
```

Run: `dotnet restore`
Expected: sucesso, sem aviso de versão (`NU1608`/`NU1605`).

- [ ] **Passo 2: Coletor de logs e composição com configuração extra**

`tests/IdentityGateway.Infrastructure.IntegrationTests/ColetorDeLogs.cs`:

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace IdentityGateway.Infrastructure.IntegrationTests;

/// <summary>
/// Provider de log que guarda tudo, de todas as categorias: mensagem formatada, valores estruturados, escopos e
/// exceção.
/// </summary>
/// <remarks>
/// Guarda os escopos também: o <c>LogicalHandler</c> do <c>HttpClient</c> abre um escopo com a URI, e um vazamento
/// ali não apareceria na mensagem.
/// </remarks>
internal sealed class ColetorDeLogs : ILoggerProvider
{
    public ConcurrentQueue<RegistroDeLog> Registros { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Registros);

    public void Dispose()
    {
    }

    private sealed class Logger(string categoria, ConcurrentQueue<RegistroDeLog> destino) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            destino.Enqueue(new RegistroDeLog(categoria, LogLevel.None, $"[escopo] {Valores(state)} {state}"));
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            destino.Enqueue(new RegistroDeLog(
                categoria, logLevel, $"{formatter(state, exception)} {Valores(state)} {exception}"));
        }

        private static string Valores<TState>(TState state) =>
            state is IEnumerable<KeyValuePair<string, object?>> pares
                ? string.Join(" ", pares.Select(par => $"{par.Key}={par.Value}"))
                : string.Empty;
    }
}

/// <summary>Um registro capturado: categoria, nível e todo o texto que um sink poderia gravar.</summary>
internal sealed record RegistroDeLog(string Categoria, LogLevel Nivel, string Texto);
```

Em `ComposicaoDoProvisionamento.Criar`, a assinatura passa a ser
`internal static ServiceProvider Criar(PostgresFixture postgres, KeycloakFixture keycloak, Action<IServiceCollection>? ajustar = null, IReadOnlyDictionary<string, string?>? extras = null)`
e o dicionário da configuração vira uma variável, completada antes do `Build()`:

```csharp
        Dictionary<string, string?> valores = new()
        {
            ["Database:ConnectionString"] = postgres.ConnectionString,
            ["Jwt:Issuer"] = "identitygateway",
            ["Jwt:Audience"] = "identitygateway-api",
            ["Jwt:SigningKey"] = new string('k', 32),
            ["Keycloak:Admin:BaseUrl"] = keycloak.BaseUrl,
            ["Keycloak:Admin:PublicBaseUrl"] = KeycloakFixture.HostnamePublico,
            ["Keycloak:Admin:Realm"] = KeycloakFixture.Realm,
            ["Keycloak:Admin:ClientId"] = "identity-gateway",
            ["Keycloak:Admin:PrivateKeyPem"] = keycloak.Chaves.PemPrivado,
            ["Keycloak:Admin:AllowInsecureHttp"] = "true",
            ["HttpResilience:MaxRetryAttempts"] = "1",
            ["Outbox:Enabled"] = "false",
            ["Plans:free:tier"] = "Free",
            ["Plans:free:maxUsers"] = "5",
            ["Plans:free:maxClients"] = "1",
        };

        foreach ((string chave, string? valor) in extras ?? new Dictionary<string, string?>())
        {
            valores[chave] = valor;
        }

        IConfiguration configuracao = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
```

- [ ] **Passo 3: Escrever o teste de vazamento na composição real**

`tests/IdentityGateway.Infrastructure.IntegrationTests/Provisioning/VazamentoDoEmailTests.cs`:

```csharp
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;
using IdentityGateway.Infrastructure.Persistence;
using IdentityGateway.Infrastructure.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry.Trace;
using static IdentityGateway.Infrastructure.IntegrationTests.Provisioning.ComposicaoDoProvisionamento;

namespace IdentityGateway.Infrastructure.IntegrationTests.Provisioning;

/// <summary>
/// D15: o e-mail do admin não aparece em log, span, <c>outbox_messages.error</c> — no caminho feliz e nas falhas.
/// </summary>
/// <remarks>
/// <para>
/// <b>Na composição real</b> (Application + Infrastructure, PostgreSQL, Keycloak e mailpit), com todas as categorias
/// em <c>Trace</c> e as instrumentações que a Api liga: um teste só da Application não enxergaria o EF, o
/// <c>HttpClient</c>, a resiliência nem o <c>OutboxProcessor</c>.
/// </para>
/// <para>
/// <b>O log de dados sensíveis do EF vale o que o <c>appsettings.Development.json</c> diz</b>, lido do arquivo: é a
/// configuração que a IDE e o compose usam. Ligado, o EF registra o parâmetro do <c>INSERT</c> por construção — e é
/// exatamente o que este teste pega se alguém religar a opção no arquivo.
/// </para>
/// <para>
/// <b>Antes de afirmar "nada vazou", afirma que o canal foi capturado</b>: um log e um span do <c>HttpClient</c> com
/// <c>/users</c>. Sem isso, um coletor mal ligado passaria o teste sem ver nada.
/// </para>
/// </remarks>
public sealed class VazamentoDoEmailTests(PostgresFixture postgres, KeycloakFixture keycloak)
    : IClassFixture<PostgresFixture>
{
    private static string SensitiveDataLoggingDoDevelopment()
    {
        DirectoryInfo? pasta = new(AppContext.BaseDirectory);

        while (pasta is not null && !File.Exists(Path.Combine(pasta.FullName, "IdentityGateway.slnx")))
        {
            pasta = pasta.Parent;
        }

        string caminho = Path.Combine(
            pasta?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada."),
            "src", "IdentityGateway.Api", "appsettings.Development.json");

        using JsonDocument desenvolvimento = JsonDocument.Parse(File.ReadAllText(caminho));
        return desenvolvimento.RootElement.GetProperty("Database").GetProperty("EnableSensitiveDataLogging")
            .GetBoolean() ? "true" : "false";
    }

    private ServiceProvider Compor(ColetorDeLogs logs, List<Activity> spans, Action<IServiceCollection>? ajustar = null)
    {
        ServiceProvider provider = Criar(
            postgres,
            keycloak,
            services =>
            {
                services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
                services.AddOpenTelemetry().WithTracing(tracing => tracing
                    .AddHttpClientInstrumentation()
                    .AddNpgsql()
                    .AddInMemoryExporter(spans));
                ajustar?.Invoke(services);
            },
            new Dictionary<string, string?>
            {
                ["Database:EnableSensitiveDataLogging"] = SensitiveDataLoggingDoDevelopment(),
            });

        // Sem host, ninguém liga o TracerProvider: resolvê-lo é o que começa a ouvir as atividades.
        _ = provider.GetRequiredService<TracerProvider>();

        return provider;
    }

    private static async Task<TenantId> RegistrarEProcessarAsync(
        ServiceProvider provider, string email, CancellationToken ct)
    {
        TenantId tenant = await RegistrarAsync(provider, email, ct);
        await LiberarAsync(provider, (await MensagemAsync(provider, "tenant-registered", tenant, ct)).Id, ct);
        await ProcessarCicloAsync(provider, ct);
        return tenant;
    }

    private static string TextoDoSpan(Activity span) =>
        string.Join(
            " ",
            [
                span.DisplayName,
                span.StatusDescription ?? string.Empty,
                .. span.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"),
                .. span.Events.SelectMany(evento => evento.Tags.Select(tag => $"{evento.Name} {tag.Key}={tag.Value}")),
            ]);

    private static async Task AfirmarQueNadaVazouAsync(
        string email, ColetorDeLogs logs, List<Activity> spans, ServiceProvider provider, TenantId tenant,
        CancellationToken ct)
    {
        logs.Registros.Should().Contain(
            registro => registro.Categoria.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal)
                        && registro.Texto.Contains("/users", StringComparison.Ordinal),
            "sem o canal do HttpClient capturado, 'nada vazou' não prova nada");
        spans.Should().Contain(
            span => TextoDoSpan(span).Contains("/users", StringComparison.Ordinal),
            "idem para os spans do HttpClient");

        // O e-mail cru e escapado (é assim que ele iria numa URL: %2B e %40).
        string[] formas = [email, Uri.EscapeDataString(email)];

        foreach (string forma in formas)
        {
            logs.Registros.Where(registro => registro.Texto.Contains(forma, StringComparison.OrdinalIgnoreCase))
                .Select(registro => $"{registro.Categoria}: {registro.Texto}")
                .Should().BeEmpty($"nenhum log pode conter o e-mail ({forma})");

            spans.Where(span => TextoDoSpan(span).Contains(forma, StringComparison.OrdinalIgnoreCase))
                .Select(TextoDoSpan)
                .Should().BeEmpty($"nenhum span pode conter o e-mail ({forma})");
        }

        await using AsyncServiceScope escopo = provider.CreateAsyncScope();
        AppDbContext contexto = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
        List<OutboxMessage> mensagens = await contexto.OutboxMessages.AsNoTracking().ToListAsync(ct);

        mensagens.Where(mensagem => mensagem.Content.Contains(tenant.Value.ToString(), StringComparison.Ordinal))
            .Select(mensagem => mensagem.Error ?? string.Empty)
            .Should().NotContain(erro => formas.Any(forma => erro.Contains(forma, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task CaminhoFeliz_NadaVaza()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ColetorDeLogs logs = new();
        List<Activity> spans = [];
        await using ServiceProvider provider = Compor(logs, spans);
        string email = KeycloakFixture.EmailUnico();

        TenantId tenant = await RegistrarEProcessarAsync(provider, email, ct);

        (await TenantAsync(provider, tenant, ct)).Status.Should().Be(TenantStatus.Active);
        await AfirmarQueNadaVazouAsync(email, logs, spans, provider, tenant, ct);
    }

    [Fact]
    public async Task Inconsistencia_NadaVaza()
    {
        // O e-mail já pertence a uma conta sem o nosso tenant_id (D5): a exceção vai para o log do handler.
        CancellationToken ct = TestContext.Current.CancellationToken;
        ColetorDeLogs logs = new();
        List<Activity> spans = [];
        await using ServiceProvider provider = Compor(logs, spans);
        string email = KeycloakFixture.EmailUnico();
        await keycloak.CriarUsuarioComoMasterAsync(new { username = email, email, enabled = true }, ct);

        TenantId tenant = await RegistrarEProcessarAsync(provider, email, ct);

        (await TenantAsync(provider, tenant, ct)).Status.Should().Be(TenantStatus.ProvisioningFailed);
        await AfirmarQueNadaVazouAsync(email, logs, spans, provider, tenant, ct);
    }

    [Fact]
    public async Task ConflitoNoPost_NadaVaza()
    {
        // Username igual ao e-mail em outra conta: GET vazio, POST 409, reconsulta, inconsistência.
        CancellationToken ct = TestContext.Current.CancellationToken;
        ColetorDeLogs logs = new();
        List<Activity> spans = [];
        await using ServiceProvider provider = Compor(logs, spans);
        string email = KeycloakFixture.EmailUnico();
        await keycloak.CriarUsuarioComoMasterAsync(
            new { username = email, email = $"outro+{Guid.NewGuid():N}@acme.test", enabled = true }, ct);

        TenantId tenant = await RegistrarEProcessarAsync(provider, email, ct);

        (await TenantAsync(provider, tenant, ct)).Status.Should().Be(TenantStatus.ProvisioningFailed);
        await AfirmarQueNadaVazouAsync(email, logs, spans, provider, tenant, ct);
    }

    [Fact]
    public async Task UsuarioDesabilitado_NadaVaza()
    {
        // O nosso usuário (com o tenant_id deste tenant), desabilitado à mão: o envio dá 400.
        CancellationToken ct = TestContext.Current.CancellationToken;
        ColetorDeLogs logs = new();
        List<Activity> spans = [];
        await using ServiceProvider provider = Compor(logs, spans);
        string email = KeycloakFixture.EmailUnico();

        TenantId tenant = await RegistrarAsync(provider, email, ct);
        await keycloak.CriarUsuarioComoMasterAsync(new
        {
            username = email,
            email,
            enabled = false,
            requiredActions = new[] { "UPDATE_PASSWORD" },
            attributes = new Dictionary<string, string[]> { ["tenant_id"] = [tenant.Value.ToString()] },
        }, ct);
        await LiberarAsync(provider, (await MensagemAsync(provider, "tenant-registered", tenant, ct)).Id, ct);
        await ProcessarCicloAsync(provider, ct);

        (await TenantAsync(provider, tenant, ct)).Status.Should().Be(TenantStatus.ProvisioningFailed);
        await AfirmarQueNadaVazouAsync(email, logs, spans, provider, tenant, ct);
    }

    [Fact]
    public async Task SmtpFora_NadaVazaNemNoErroDoOutbox()
    {
        // O 500 sobe como transitório e a mensagem da exceção vai para outbox_messages.error.
        CancellationToken ct = TestContext.Current.CancellationToken;
        ColetorDeLogs logs = new();
        List<Activity> spans = [];
        Interceptacao interceptacao = new()
        {
            ResponderSemEnviar = (pedido, _) =>
                pedido.Method == HttpMethod.Put ? HttpStatusCode.InternalServerError : null,
        };
        await using ServiceProvider provider = Compor(logs, spans, services => services.Interceptar(interceptacao));
        string email = KeycloakFixture.EmailUnico();

        TenantId tenant = await RegistrarEProcessarAsync(provider, email, ct);

        (await TenantAsync(provider, tenant, ct)).Status.Should().Be(TenantStatus.Pending);
        (await MensagemAsync(provider, "tenant-registered", tenant, ct)).Error.Should().NotBeNullOrEmpty();
        await AfirmarQueNadaVazouAsync(email, logs, spans, provider, tenant, ct);
    }

    [Fact]
    public async Task FalhaNoCommit_NadaVaza()
    {
        // Conflito de xmin de verdade: o tenant é tocado por fora entre a leitura do handler e o commit (durante o
        // envio do convite), e o SaveChanges do TransactionBehavior falha pelo caminho real do EF.
        CancellationToken ct = TestContext.Current.CancellationToken;
        ColetorDeLogs logs = new();
        List<Activity> spans = [];
        TenantId[] alvo = [default];
        Interceptacao interceptacao = new()
        {
            AntesDeEnviar = async (pedido, _) =>
            {
                if (pedido.Method == HttpMethod.Put)
                {
                    await using AppDbContext externo = postgres.CriarContexto();
                    await externo.Database.ExecuteSqlAsync(
                        $"UPDATE tenants SET name = name WHERE id = {alvo[0].Value}", ct);
                }
            },
        };
        await using ServiceProvider provider = Compor(logs, spans, services => services.Interceptar(interceptacao));
        string email = KeycloakFixture.EmailUnico();

        TenantId tenant = await RegistrarAsync(provider, email, ct);
        alvo[0] = tenant;
        await LiberarAsync(provider, (await MensagemAsync(provider, "tenant-registered", tenant, ct)).Id, ct);
        await ProcessarCicloAsync(provider, ct);

        (await TenantAsync(provider, tenant, ct)).Status.Should().Be(TenantStatus.Pending);
        (await MensagemAsync(provider, "tenant-registered", tenant, ct)).Error.Should().NotBeNullOrEmpty();
        await AfirmarQueNadaVazouAsync(email, logs, spans, provider, tenant, ct);
    }
}
```

- [ ] **Passo 4: Escrever a regra do ambiente local e o teste da Api**

`tests/IdentityGateway.ArchitectureTests/RegrasDoAmbienteLocalTests.cs`:

```csharp
using System.Text.Json;

namespace IdentityGateway.ArchitectureTests;

/// <summary>
/// O ambiente de desenvolvimento (IDE e compose) não expõe o e-mail do admin, e o compose é coerente consigo mesmo.
/// </summary>
/// <remarks>
/// Lê os arquivos, como <c>RegrasDoRealmTests</c> lê o realm: são configuração versionada, e um valor errado neles
/// não quebra build nem teste de comportamento — só aparece num log de desenvolvimento, que é público na prática.
/// </remarks>
public sealed partial class RegrasDoAmbienteLocalTests
{
    [Fact]
    public void Development_NaoRegistraDadosSensiveisDoEf()
    {
        // D15, "inclusive em Development": com o log de dados sensíveis ligado, o EF registra o parâmetro do INSERT do
        // e-mail e, no DetectChanges, o valor antigo da coluna ao apagá-la.
        using JsonDocument desenvolvimento = JsonDocument.Parse(File.ReadAllText(
            RaizDoRepositorio.Caminho("src", "IdentityGateway.Api", "appsettings.Development.json")));
        JsonElement raiz = desenvolvimento.RootElement;

        raiz.GetProperty("Database").GetProperty("EnableSensitiveDataLogging").GetBoolean().Should().BeFalse();
        raiz.GetProperty("Serilog").GetProperty("MinimumLevel").GetProperty("Override")
            .GetProperty("Microsoft.EntityFrameworkCore").GetString().Should().Be("Warning");
    }
}
```

`tests/IdentityGateway.Api.FunctionalTests/VazamentoDoEmailNaApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// D15 na borda HTTP: nenhuma resposta — nem os ProblemDetails, que em Development trazem detalhe — devolve o e-mail.
/// </summary>
public sealed class VazamentoDoEmailNaApiTests(IdentityGatewayApiFactory factory)
    : IClassFixture<IdentityGatewayApiFactory>
{
    private const string Rota = "/api/v1/tenants";

    private static object Corpo(string slug, string email) => new
    {
        name = "Acme Corp",
        slug,
        planCode = "free",
        initialAdminEmail = email,
    };

    private static string SlugUnico() => $"vaza-{Guid.NewGuid():N}"[..20];

    [Fact]
    public async Task EmailMalFormado_400SemOValor()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");
        string marca = $"segredo{Guid.NewGuid():N}";

        using HttpResponseMessage resposta = await client.PostAsJsonAsync(
            Rota, Corpo(SlugUnico(), $"{marca}@@acme.test"), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resposta.Content.ReadAsStringAsync(ct)).Should().NotContain(marca);
    }

    [Fact]
    public async Task RegistroAceitoEConsulta_NaoDevolvemOEmail()
    {
        // Nenhum read model expõe o InitialAdminEmail, inclusive o GET .../provisioning (§4.8).
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");
        string email = $"segredo+{Guid.NewGuid():N}@acme.test";

        using HttpResponseMessage aceito = await client.PostAsJsonAsync(Rota, Corpo(SlugUnico(), email), ct);
        string corpoDoAceito = await aceito.Content.ReadAsStringAsync(ct);
        string id = JsonDocument.Parse(corpoDoAceito).RootElement.GetProperty("tenantId").GetString()!;
        using HttpResponseMessage consulta = await client.GetAsync(new Uri($"{Rota}/{id}/provisioning", UriKind.Relative), ct);

        aceito.StatusCode.Should().Be(HttpStatusCode.Accepted);
        corpoDoAceito.Should().NotContain("segredo");
        (await consulta.Content.ReadAsStringAsync(ct)).Should().NotContain("segredo");
    }

    [Fact]
    public async Task SlugDuplicado_409SemOEmail()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");
        string slug = SlugUnico();
        await client.PostAsJsonAsync(Rota, Corpo(slug, $"primeiro+{Guid.NewGuid():N}@acme.test"), ct);
        string email = $"segredo+{Guid.NewGuid():N}@acme.test";

        using HttpResponseMessage segunda = await client.PostAsJsonAsync(Rota, Corpo(slug, email), ct);

        segunda.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await segunda.Content.ReadAsStringAsync(ct)).Should().NotContain("segredo");
    }
}
```

- [ ] **Passo 5: Rodar e ver falhar**

Run: `dotnet test tests/IdentityGateway.ArchitectureTests --filter "FullyQualifiedName~RegrasDoAmbienteLocalTests"`
Expected: FAIL — `EnableSensitiveDataLogging` é `true` no `appsettings.Development.json`.

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter "FullyQualifiedName~VazamentoDoEmailTests"`
Expected: FAIL nos seis — o EF, com o log de dados sensíveis ligado pelo arquivo, registra o e-mail no parâmetro do
`INSERT` do registro (categoria `Microsoft.EntityFrameworkCore.Database.Command`). É a prova de que o teste enxerga
o EF.

Run: `dotnet test tests/IdentityGateway.Api.FunctionalTests --filter "FullyQualifiedName~VazamentoDoEmailNaApiTests"`
Expected: PASS já no estado atual (o validador e o `Email.Of` não ecoam o valor desde a Tarefa 1; o read model nunca
teve o e-mail). O teste existe para guardar isso.

- [ ] **Passo 6: Desligar os dados sensíveis em Development**

`src/IdentityGateway.Api/appsettings.Development.json`, inteiro:

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Debug",
      "Override": {
        "Microsoft.AspNetCore": "Information",
        "Microsoft.EntityFrameworkCore": "Warning"
      }
    },
    "WriteTo": [
      {
        "Name": "Console"
      },
      {
        "Name": "Seq",
        "Args": {
          "serverUrl": "http://localhost:5341"
        }
      }
    ]
  },
  "Database": {
    "EnableSensitiveDataLogging": false
  },
  "Keycloak": {
    "Admin": {
      "BaseUrl": "http://localhost:8081",
      "AllowInsecureHttp": true
    }
  }
}
```

Em `DatabaseOptions.cs`, o `<remarks>` de `EnableSensitiveDataLogging` passa a ser:

```csharp
    /// <remarks>
    /// <b>Falso por padrão, e é importante que seja.</b> Os parâmetros carregam dado de cliente — documento,
    /// e-mail, endereço — e ligá-los manda PII para o log. <b>Desligado também no <c>appsettings.Development.json</c></b>
    /// desde a fatia C: a tabela de tenants guarda o e-mail do admin inicial até a ativação, e o EF o registraria no
    /// <c>INSERT</c> e, ao apagá-lo, no valor antigo da coluna. Um teste de arquitetura trava o arquivo, e o teste de
    /// vazamento usa o valor dele.
    /// </remarks>
```

- [ ] **Passo 7: Rodar e ver passar**

Run: `dotnet test tests/IdentityGateway.ArchitectureTests --filter "FullyQualifiedName~RegrasDoAmbienteLocalTests"`
Expected: PASS.

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter "FullyQualifiedName~VazamentoDoEmailTests"`
Expected: PASS nos seis.

**Se algum ficar vermelho por causa do `HttpClient`** (a mensagem da asserção lista o registro: categoria
`System.Net.Http.HttpClient.KeycloakAdminClient.*` com `email=admin%2B…`), a redação padrão do .NET não está valendo
neste ambiente. Conserto, em `KeycloakServiceCollectionExtensions.AddKeycloakIdentity`, logo depois de
`admin.AddHttpMessageHandler<ServiceAccountTokenHandler>();`:

```csharp
        // A busca de usuário leva o e-mail na query (?email=): os logs padrão do HttpClient não podem registrá-la
        // (D15). Sem os loggers do IHttpClientFactory, os erros continuam chegando pelas exceções e pelos logs do
        // adaptador, que levam o tenant.
        admin.RemoveAllLoggers();
```

e registrar no handoff que a premissa "o .NET 9+ redige a query" não se confirmou. Se o vermelho vier de um span
(`url.full` com a query), acrescentar na Api, em `AddTelemetria`, `.AddHttpClientInstrumentation(opcoes =>
opcoes.FilterHttpRequestMessage = pedido => !pedido.RequestUri!.Query.Contains("email=", StringComparison.Ordinal))`
e replicar o mesmo filtro no `Compor` do teste — e registrar também.

- [ ] **Passo 8: 🧪 Prova por mutação**

1. `"EnableSensitiveDataLogging": true` de volta no `appsettings.Development.json` → os seis `VazamentoDoEmailTests` e `Development_NaoRegistraDadosSensiveisDoEf` vermelhos. Reverter.
2. Desligar a redação de URI do .NET só para a execução do teste (prova de que o canal do `HttpClient` é visto):
   PowerShell: `$env:DOTNET_SYSTEM_NET_HTTP_DISABLEURIREDACTION = '1'; dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter "FullyQualifiedName~VazamentoDoEmailTests.CaminhoFeliz_NadaVaza"; Remove-Item Env:DOTNET_SYSTEM_NET_HTTP_DISABLEURIREDACTION`
   → vermelho, com o registro do `LogicalHandler` contendo `email=admin%2B…`. Sem a variável, verde de novo.
3. Pôr o e-mail na mensagem de `ContaAlheia` no `KeycloakIdentityProvider` (`… pertence a um usuário não correlacionado ({email})`, passando o e-mail) → `Inconsistencia_NadaVaza` e `ConflitoNoPost_NadaVaza` vermelhos. Reverter.
4. Pôr o e-mail num log do adaptador (`UsuarioCriado` com `{Email}`) → `CaminhoFeliz_NadaVaza` vermelho. Reverter.

- [ ] **Passo 9: Commit**

```bash
git add Directory.Packages.props src/IdentityGateway.Api/appsettings.Development.json src/IdentityGateway.Infrastructure/Configuration/DatabaseOptions.cs tests
git commit -m "fix: e-mail do admin fora dos logs do EF em Development

O appsettings.Development.json, usado pela IDE e pelo compose, desliga o
log de dados sensiveis do EF e sobe a categoria do EF para Warning: o EF
registraria o e-mail no INSERT e o valor antigo ao apaga-lo. Um teste na
composicao real (Postgres, Keycloak e mailpit, tudo em Trace, spans em
memoria) prova que nenhum log, span ou erro do Outbox contem o e-mail,
no caminho feliz e em cinco falhas, e exige antes que o canal do
HttpClient tenha sido capturado. A query ?email= ja sai redigida pelo
.NET.

Mutacoes: dados sensiveis de volta, redacao de URI desligada, e-mail na
mensagem de excecao e num log deixaram o teste vermelho."
```

---

### Tarefa 11: E2E, atomicidade, entrega concorrente e funcional

**Arquivos:**
- Modify: `tests/IdentityGateway.Infrastructure.IntegrationTests/Provisioning/ComposicaoDoProvisionamento.cs` (leituras de `members` e da coluna; o provedor com encontro)
- Modify: `tests/IdentityGateway.Infrastructure.IntegrationTests/Provisioning/ProvisionamentoContraKeycloakTests.cs`
- Create: `tests/IdentityGateway.Infrastructure.IntegrationTests/Provisioning/AtomicidadeDoProvisionamentoTests.cs`
- Modify: `tests/IdentityGateway.Api.FunctionalTests/RegistroDeTenantTests.cs`

**Interfaces:**
- Consome: a composição (Tarefas 7, 9 e 10), os métodos do master e do mailpit (Tarefas 7 e 8).
- Produz (teste): `ComposicaoDoProvisionamento.MembrosAsync(ServiceProvider, TenantId, CancellationToken)`,
  `ComposicaoDoProvisionamento.EmailGravadoAsync(ServiceProvider, TenantId, CancellationToken)`,
  `ComposicaoDoProvisionamento.ProvisionarAsync(ServiceProvider, TenantId, CancellationToken)`,
  `ProvedorComEncontro`, `EncontroDeDois`.

Nenhum código de produção muda nesta tarefa: ela prova, contra PostgreSQL, Keycloak e mailpit reais, o que as
anteriores construíram — e é onde um defeito de integração apareceria.

- [ ] **Passo 1: Utilitários da composição**

Em `ComposicaoDoProvisionamento.cs` (com `using IdentityGateway.Application.Tenants.ProvisionTenant;` e
`using IdentityGateway.Domain.Members;`), acrescentar à
classe:

```csharp
    /// <summary>Os membros gravados do tenant, lidos num escopo novo.</summary>
    internal static async Task<List<Member>> MembrosAsync(ServiceProvider provider, TenantId tenant, CancellationToken ct)
    {
        await using AsyncServiceScope escopo = provider.CreateAsyncScope();
        AppDbContext contexto = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

        return await contexto.Members.AsNoTracking().Where(membro => membro.TenantId == tenant).ToListAsync(ct);
    }

    /// <summary>O valor cru da coluna initial_admin_email, ou <c>&lt;nulo&gt;</c>.</summary>
    internal static async Task<string> EmailGravadoAsync(ServiceProvider provider, TenantId tenant, CancellationToken ct)
    {
        await using AsyncServiceScope escopo = provider.CreateAsyncScope();
        AppDbContext contexto = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

        List<string> valor = await contexto.Database
            .SqlQuery<string>(
                $"SELECT coalesce(initial_admin_email, '<nulo>') AS \"Value\" FROM tenants WHERE id = {tenant.Value}")
            .ToListAsync(ct);

        return valor.Single();
    }

    /// <summary>Entrega o command do provisionamento num escopo novo, pelo pipeline — como o despacho do Outbox faz.</summary>
    internal static async Task ProvisionarAsync(ServiceProvider provider, TenantId tenant, CancellationToken ct)
    {
        await using AsyncServiceScope escopo = provider.CreateAsyncScope();
        Mediator.ISender sender = escopo.ServiceProvider.GetRequiredService<Mediator.ISender>();

        await sender.Send(new ProvisionTenantCommand(tenant), ct);
    }
```

E, no fim do arquivo:

```csharp
/// <summary>Faz duas chamadas se encontrarem: nenhuma segue antes de a outra chegar.</summary>
internal sealed class EncontroDeDois
{
    private readonly TaskCompletionSource _ambas = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _chegaram;

    public Task ChegarAsync(CancellationToken ct)
    {
        if (Interlocked.Increment(ref _chegaram) == 2)
        {
            _ambas.SetResult();
        }

        return _ambas.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
    }
}

/// <summary>
/// O provedor real, com um ponto de encontro depois do convite: as duas entregas terminam a parte do Keycloak antes de
/// qualquer uma commitar — e as duas leram o tenant em Pending antes disso.
/// </summary>
internal sealed class ProvedorComEncontro(IIdentityProvider real, EncontroDeDois encontro) : IIdentityProvider
{
    public Task<string> EnsureOrganizationAsync(
        TenantId tenantId, TenantSlug slug, string name, CancellationToken cancellationToken) =>
        real.EnsureOrganizationAsync(tenantId, slug, name, cancellationToken);

    public async Task<ExternalUserId> EnsureInvitedUserAsync(
        string organizationId, TenantId tenantId, InviteData invite, CancellationToken cancellationToken)
    {
        ExternalUserId sub = await real.EnsureInvitedUserAsync(organizationId, tenantId, invite, cancellationToken);
        await encontro.ChegarAsync(cancellationToken);
        return sub;
    }
}
```

- [ ] **Passo 2: Escrever os testes ponta a ponta**

Em `ProvisionamentoContraKeycloakTests.cs` (com `using IdentityGateway.Domain.Members;`,
`using IdentityGateway.Infrastructure.Identity.Keycloak;` e `using Microsoft.EntityFrameworkCore;`):

1. Teste novo:

```csharp
    [Fact]
    public async Task TenantPendente_ViraActiveComMemberEmailApagadoUsuarioEConvite()
    {
        // O critério de sucesso da fatia em forma de teste: Organization, usuário convidado, e-mail no mailpit, e no
        // banco o tenant Active com a vaga do admin, o Member em Invited e a coluna do e-mail vazia.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ServiceProvider provider = Criar(postgres, keycloak);
        string email = KeycloakFixture.EmailUnico();

        TenantId tenant = await RegistrarAsync(provider, email, ct);
        await LiberarAsync(provider, (await MensagemAsync(provider, "tenant-registered", tenant, ct)).Id, ct);
        await ProcessarCicloAsync(provider, ct);

        Tenant ativo = await TenantAsync(provider, tenant, ct);
        ativo.Status.Should().Be(TenantStatus.Active);
        ativo.OccupiedSeats.Should().Be(1);
        (await EmailGravadoAsync(provider, tenant, ct)).Should().Be("<nulo>");

        JsonElement usuario = (await keycloak.UsuariosPorEmailAsync(email, ct)).Should().ContainSingle().Subject;
        string sub = usuario.GetProperty("id").GetString()!;

        Member membro = (await MembrosAsync(provider, tenant, ct)).Should().ContainSingle().Subject;
        membro.ExternalUserId.Value.Should().Be(sub);
        membro.Status.Should().Be(MemberStatus.Invited);

        (await keycloak.MembrosDaOrganizacaoAsync(ativo.ExternalOrganizationId!, ct)).Should().Contain(sub);
        (await keycloak.EsperarMensagensAsync(email, 1, ct)).Should().ContainSingle();
    }
```

2. Em `CommitPerdidoDepoisDeCriarAOrganizacao_ProximoCicloReencontraSemDuplicar`, registrar com e-mail conhecido e
acrescentar as asserções do convite. O teste passa a ser:

```csharp
    [Fact]
    public async Task CommitPerdidoDepoisDeCriarAOrganizacao_ProximoCicloReencontraSemDuplicar()
    {
        // Três fatos num cenário só:
        // (1) isolamento de escopo — o commit do handler falha com o tenant já Active no contexto dele; se o publisher
        //     usasse o escopo do processador, o SaveChanges que registra o resultado do lote gravaria esse Active;
        // (2) idempotência entre Keycloak e banco — Organization e usuário do primeiro ciclo são reencontrados no
        //     segundo, sem duplicar;
        // (3) o duplo envio declarado na §4.3 — o convite saiu antes do commit perdido e sai de novo, porque o usuário
        //     continua com UPDATE_PASSWORD: exatamente dois e-mails, nem um nem três.
        CancellationToken ct = TestContext.Current.CancellationToken;
        int[] falhasDoCommit = [0];
        await using ServiceProvider provider = Criar(postgres, keycloak, services =>
            services.AddScoped<IUnitOfWork>(sp => new UnitOfWorkQueFalha(
                sp.GetRequiredService<AppDbContext>(), falhasDoCommit)));
        string email = KeycloakFixture.EmailUnico();

        TenantId tenant = await RegistrarAsync(provider, email, ct);
        Guid registrado = (await MensagemAsync(provider, "tenant-registered", tenant, ct)).Id;
        falhasDoCommit[0] = 1;

        await LiberarAsync(provider, registrado, ct);
        await ProcessarCicloAsync(provider, ct);

        Tenant aposCommitPerdido = await TenantAsync(provider, tenant, ct);
        aposCommitPerdido.Status.Should().Be(TenantStatus.Pending, "o commit do handler falhou; nada dele pode ter sido gravado");
        (await EmailGravadoAsync(provider, tenant, ct)).Should().NotBe("<nulo>", "o e-mail precisa estar lá para o segundo ciclo");
        (await keycloak.ContarPorAliasAsync(aposCommitPerdido.Slug.Value, ct)).Should().Be(1);

        await LiberarAsync(provider, registrado, ct);
        await ProcessarCicloAsync(provider, ct);

        Tenant ativo = await TenantAsync(provider, tenant, ct);
        ativo.Status.Should().Be(TenantStatus.Active);
        (await keycloak.ContarPorAliasAsync(ativo.Slug.Value, ct)).Should().Be(1);
        (await keycloak.UsuariosPorEmailAsync(email, ct)).Should().ContainSingle();
        (await MembrosAsync(provider, tenant, ct)).Should().ContainSingle();
        (await keycloak.EsperarMensagensAsync(email, 2, ct)).Should().HaveCount(2);
    }
```

3. Teste novo da entrega concorrente:

```csharp
    [Fact]
    public async Task DuasEntregasConcorrentes_UmMemberUmaVagaEUmCommitFalha()
    {
        // Foco de revisão 2. Duas réplicas (ou dois ciclos sobrepostos) processam a mesma mensagem: as duas leem o
        // tenant em Pending, as duas passam pelo Keycloak (Organization e usuário: o 409 da segunda é reencontrado), e
        // só então commitam. O xmin do tenant — ou, se escapasse, o índice único de members — faz a segunda falhar.
        CancellationToken ct = TestContext.Current.CancellationToken;
        EncontroDeDois encontro = new();
        await using ServiceProvider provider = Criar(postgres, keycloak, services =>
            services.AddTransient<IIdentityProvider>(sp => new ProvedorComEncontro(
                ActivatorUtilities.CreateInstance<KeycloakIdentityProvider>(sp), encontro)));
        string email = KeycloakFixture.EmailUnico();
        TenantId tenant = await RegistrarAsync(provider, email, ct);

        async Task<Exception?> EntregarAsync()
        {
            try
            {
                await ProvisionarAsync(provider, tenant, ct);
                return null;
            }
            catch (DbUpdateException excecao)
            {
                return excecao;
            }
        }

        Exception?[] resultados = await Task.WhenAll(EntregarAsync(), EntregarAsync());

        resultados.Count(resultado => resultado is null).Should().Be(1, "uma entrega commita");
        resultados.Count(resultado => resultado is DbUpdateException).Should().Be(1, "a outra falha no commit");

        Tenant ativo = await TenantAsync(provider, tenant, ct);
        ativo.Status.Should().Be(TenantStatus.Active);
        ativo.OccupiedSeats.Should().Be(1, "uma vaga só");
        (await MembrosAsync(provider, tenant, ct)).Should().ContainSingle();
        (await keycloak.UsuariosPorEmailAsync(email, ct)).Should().ContainSingle();

        // A mensagem da entrega que falhou volta pelo Outbox: encontra o tenant Active e não muda nada.
        await ProvisionarAsync(provider, tenant, ct);

        (await TenantAsync(provider, tenant, ct)).OccupiedSeats.Should().Be(1);
        (await MembrosAsync(provider, tenant, ct)).Should().ContainSingle();
    }
```

`tests/IdentityGateway.Infrastructure.IntegrationTests/Provisioning/AtomicidadeDoProvisionamentoTests.cs`:

```csharp
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.IntegrationTests.Identity.Keycloak;
using IdentityGateway.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static IdentityGateway.Infrastructure.IntegrationTests.Provisioning.ComposicaoDoProvisionamento;

namespace IdentityGateway.Infrastructure.IntegrationTests.Provisioning;

/// <summary>
/// A ativação do tenant, o <c>Member</c> e o <c>tenant-activated</c> gravam juntos ou nenhum.
/// </summary>
/// <remarks>
/// <para>
/// O provedor de identidade é um dublê que devolve um <c>sub</c> conhecido, e uma linha em <c>members</c> com o mesmo
/// <c>(tenant_id, sub)</c> é pré-inserida por SQL: o <c>INSERT</c> do <c>Member</c> viola o índice único, e o commit
/// falha — com o <c>UPDATE</c> do tenant e o <c>INSERT</c> do Outbox no mesmo <c>SaveChanges</c>.
/// </para>
/// <para>
/// Sem atomicidade (um handler que gravasse o tenant antes de adicionar o membro, por exemplo), o tenant sairia
/// <c>Active</c>, sem o e-mail e com o <c>tenant-activated</c> no Outbox — e sem o membro. É o par de estados que dá
/// valor ao teste.
/// </para>
/// </remarks>
public sealed class AtomicidadeDoProvisionamentoTests(PostgresFixture postgres, KeycloakFixture keycloak)
    : IClassFixture<PostgresFixture>
{
    private sealed class ProvedorFixo(string organizacao, ExternalUserId sub) : IIdentityProvider
    {
        public Task<string> EnsureOrganizationAsync(
            TenantId tenantId, TenantSlug slug, string name, CancellationToken cancellationToken) =>
            Task.FromResult(organizacao);

        public Task<ExternalUserId> EnsureInvitedUserAsync(
            string organizationId, TenantId tenantId, InviteData invite, CancellationToken cancellationToken) =>
            Task.FromResult(sub);
    }

    [Fact]
    public async Task MemberDuplicadoNoCommit_NadaDaAtivacaoEGravado()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ExternalUserId sub = ExternalUserId.From($"sub-{Guid.NewGuid():N}");
        await using ServiceProvider provider = Criar(postgres, keycloak, services =>
            services.AddTransient<IIdentityProvider>(_ => new ProvedorFixo("org-atomica", sub)));

        TenantId tenant = await RegistrarAsync(provider, KeycloakFixture.EmailUnico(), ct);

        await using (AppDbContext contexto = postgres.CriarContexto())
        {
            await contexto.Database.ExecuteSqlAsync($"""
                INSERT INTO members (id, tenant_id, external_user_id, status, invited_at, created_at)
                VALUES ({Guid.CreateVersion7()}, {tenant.Value}, {sub.Value}, 'Invited', now(), now())
                """, ct);
        }

        Func<Task> provisionar = () => ProvisionarAsync(provider, tenant, ct);

        await provisionar.Should().ThrowAsync<DbUpdateException>();

        Tenant depois = await TenantAsync(provider, tenant, ct);
        depois.Status.Should().Be(TenantStatus.Pending);
        depois.OccupiedSeats.Should().Be(0);
        (await EmailGravadoAsync(provider, tenant, ct)).Should().NotBe("<nulo>");

        await using AppDbContext conferencia = postgres.CriarContexto();
        List<string> ativacoes = await conferencia.OutboxMessages
            .Where(mensagem => mensagem.Type == "tenant-activated")
            .Select(mensagem => mensagem.Content)
            .ToListAsync(ct);
        ativacoes.Where(conteudo => conteudo.Contains(tenant.Value.ToString(), StringComparison.Ordinal))
            .Should().BeEmpty();
    }
}
```

- [ ] **Passo 3: Escrever o teste funcional**

Em `RegistroDeTenantTests.cs`, `Corpo` ganha o e-mail como parâmetro opcional:

```csharp
    private static object Corpo(string slug, string plano = "free", string email = "admin@acme.com") => new
    {
        name = "Acme Corp",
        slug,
        planCode = plano,
        initialAdminEmail = email,
    };
```

e o teste novo:

```csharp
    [Fact]
    public async Task ComandoValido_GravaOEmailNormalizadoForaDoEvento()
    {
        // Foco de revisão 1 e D1: a coluna guarda o endereço normalizado, e o tenant-registered no Outbox não o leva —
        // o evento vai para o broker, e o e-mail não pode ir junto.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = factory.CreateClientAutenticado(roles: "platform-admin");
        string slug = SlugUnico();
        string marca = Guid.NewGuid().ToString("N");

        HttpResponseMessage resposta = await client.PostAsJsonAsync(
            Rota, Corpo(slug, email: $"  Admin+{marca.ToUpperInvariant()}@Acme.TEST "), ct);

        resposta.StatusCode.Should().Be(HttpStatusCode.Accepted);

        await factory.ComEscopoAsync(async contexto =>
        {
            List<string> coluna = await contexto.Database
                .SqlQuery<string>($"SELECT initial_admin_email AS \"Value\" FROM tenants WHERE slug = {slug}")
                .ToListAsync(ct);
            coluna.Should().ContainSingle().Which.Should().Be($"admin+{marca}@acme.test");

            List<string> conteudos = await contexto.OutboxMessages
                .Where(m => m.Type == "tenant-registered")
                .Select(m => m.Content)
                .ToListAsync(ct);
            string evento = conteudos.Single(conteudo => conteudo.Contains(slug, StringComparison.Ordinal));
            evento.Should().NotContain(marca, "o e-mail não viaja no evento")
                .And.NotContainEquivalentOf("initialAdminEmail");
        });
    }
```

- [ ] **Passo 4: Rodar e ver passar**

Run: `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter "FullyQualifiedName~Provisioning"`
Expected: PASS. Docker ligado.

Run: `dotnet test tests/IdentityGateway.Api.FunctionalTests --filter "FullyQualifiedName~RegistroDeTenantTests"`
Expected: PASS.

(Estes testes nascem verdes: o código já existe. O vermelho deles é provado pelas mutações abaixo, que é o que
impede um verde vacuoso.)

- [ ] **Passo 5: 🧪 Prova por mutação**

1. No handler, gravar o tenant antes de adicionar o membro: acrescentar `IUnitOfWork unitOfWork` ao construtor do `ProvisionTenantHandler` e `await unitOfWork.SaveChangesAsync(cancellationToken);` entre o `CompleteProvisioning` e o `membros.Add` → `MemberDuplicadoNoCommit_NadaDaAtivacaoEGravado` vermelho (tenant `Active`, `tenant-activated` no Outbox). Reverter (e o construtor dos testes da Application, se tiver sido tocado).
2. Tirar `InitialAdminEmail = null;` de `CompleteProvisioning` → `TenantPendente_ViraActiveComMemberEmailApagadoUsuarioEConvite` vermelho (coluna preenchida). Reverter.
3. Pôr o e-mail no `TenantRegistered` (acrescentar `string AdminEmail` ao record e passar `initialAdminEmail.Value` em `Register`) → `ComandoValido_GravaOEmailNormalizadoForaDoEvento` vermelho. Reverter.
4. Tirar do handler a guarda `if (tenant.Status != TenantStatus.Pending)` → a reentrega final de `DuasEntregasConcorrentes_UmMemberUmaVagaEUmCommitFalha` lança `DomainInvariantViolation` (sem o e-mail, o handler tenta `MarkProvisioningFailed` num tenant `Active`) e o teste fica vermelho. Reverter.
5. Observação, não mutação a reverter com vermelho: tirar só o `.IsConcurrencyToken()` do `xmin` em `TenantConfiguration` deixa `DuasEntregasConcorrentes_…` **verde** — a segunda entrega passa pelo `UPDATE` e cai no índice único de `members` no mesmo `SaveChanges`. É a segunda linha de defesa que o foco de revisão 2 pede; registrar o resultado no handoff e reverter.

- [ ] **Passo 6: Commit**

```bash
git add tests
git commit -m "test: convite ponta a ponta, commit perdido, atomicidade e entrega concorrente

Contra PostgreSQL, Keycloak e mailpit reais: o tenant chega a Active
com o Member, a vaga e a coluna do e-mail vazia, e o convite no mailpit;
o commit perdido termina com um usuario, um membro e exatamente dois
e-mails; um membro duplicado no commit desfaz a ativacao inteira; duas
entregas concorrentes deixam um Member e uma vaga. O POST grava o e-mail
normalizado e o tenant-registered nao o leva.

Mutacoes: tenant gravado antes do Member, e-mail nao apagado e e-mail
no TenantRegistered deixaram vermelhos os testes correspondentes."
```

---

### Tarefa 12: Compose e CI

**Arquivos:**
- Modify: `docker-compose.yml`
- Modify: `.github/workflows/ci.yml` (job `compose`)
- Modify: `tests/IdentityGateway.ArchitectureTests/RegrasDoAmbienteLocalTests.cs`

**Interfaces:**
- Consome: o realm (Tarefa 6), o `PublicBaseUrl` (Tarefa 7), o convite no provisionamento (Tarefa 9), a constante `KeycloakFixture.ImagemDoMailpit` (Tarefa 7).
- Produz: serviço `mailpit` no compose; `KC_HOSTNAME`, `KC_HOSTNAME_BACKCHANNEL_DYNAMIC` e `SMTP_*` no `keycloak`; `Keycloak__Admin__PublicBaseUrl` na `api`; passo "Conferir o convite no mailpit" na CI.

- [ ] **Passo 1: Escrever as regras do compose**

Em `RegrasDoAmbienteLocalTests.cs`, acrescentar `using System.Text.RegularExpressions;` e:

```csharp
    private static string Compose() => File.ReadAllText(RaizDoRepositorio.Caminho("docker-compose.yml"));

    // Valor de uma variável de ambiente no formato do compose (CHAVE: valor ou CHAVE: "valor"), numa linha só.
    private static string? ValorNoCompose(string chave)
    {
        Match achado = Regex.Match(
            Compose(), $@"^\s*{Regex.Escape(chave)}:\s*""?(?<valor>[^""\s]+)""?\s*$", RegexOptions.Multiline);

        return achado.Success ? achado.Groups["valor"].Value : null;
    }

    [Fact]
    public void ComposeTemKcHostnameIgualAoPublicBaseUrl()
    {
        // A porta 8081 aparece em três lugares: a publicada, o KC_HOSTNAME e o PublicBaseUrl. Os dois últimos precisam
        // ser o mesmo texto — o aud é comparado por igualdade exata com o emissor que o KC_HOSTNAME define. Sem o
        // KC_HOSTNAME, o link do e-mail sai com o host interno (keycloak:8080).
        string? hostname = ValorNoCompose("KC_HOSTNAME");
        string? publico = ValorNoCompose("Keycloak__Admin__PublicBaseUrl");

        hostname.Should().NotBeNull("sem KC_HOSTNAME o link do convite sai com http://keycloak:8080");
        publico.Should().Be(hostname);
        ValorNoCompose("KC_HOSTNAME_BACKCHANNEL_DYNAMIC").Should().Be("true",
            "sem o backchannel dinâmico, a api não conseguiria falar com o Keycloak pelo nome do serviço");
    }

    [Fact]
    public void ComposeEFixtureUsamAMesmaTagDoMailpit()
    {
        Match imagem = Regex.Match(Compose(), @"^\s*image:\s*(?<imagem>axllent/mailpit:\S+)\s*$", RegexOptions.Multiline);
        string fixture = File.ReadAllText(RaizDoRepositorio.Caminho(
            "tests", "IdentityGateway.Infrastructure.IntegrationTests", "Identity", "Keycloak", "KeycloakFixture.cs"));

        imagem.Success.Should().BeTrue("o compose precisa do serviço mailpit com a versão fixada");
        fixture.Should().Contain($"ImagemDoMailpit = \"{imagem.Groups["imagem"].Value}\"",
            "o teste de integração precisa provar o mesmo mailpit que o compose sobe");
    }

    [Fact]
    public void DependenciasComDadoPessoalPublicamSoEmLocalhost()
    {
        // Agora há dado pessoal no banco (o e-mail do admin até a ativação) e, em falha, nos logs; e os e-mails do
        // mailpit trazem links que trocam senha. Nada disso fica exposto à rede local.
        string compose = Compose();

        compose.Should().Contain("\"127.0.0.1:5432:5432\"")
            .And.Contain("\"127.0.0.1:6379:6379\"")
            .And.Contain("\"127.0.0.1:5341:80\"")
            .And.Contain("\"127.0.0.1:8025:8025\"");
        compose.Should().NotContain("\"5432:5432\"").And.NotContain("\"6379:6379\"").And.NotContain("\"5341:80\"");
        compose.Should().NotContain(":1025\"", "o SMTP do mailpit só existe na rede do compose");
    }
```

- [ ] **Passo 2: Rodar e ver falhar**

Run: `dotnet test tests/IdentityGateway.ArchitectureTests --filter "FullyQualifiedName~RegrasDoAmbienteLocalTests"`
Expected: FAIL nos três novos (não há `KC_HOSTNAME`, nem mailpit, e as portas publicam em todas as interfaces).

- [ ] **Passo 3: Compose**

Em `docker-compose.yml`:

1. No cabeçalho, trocar o parágrafo que começa em "# O Keycloak também não:" (4 linhas) por:

```yaml
# O Keycloak também não: `start-dev`, http e chave gerada em volume são de desenvolvimento. Em produção, o KC_HOSTNAME
# é o endereço público do Keycloak, e a Gateway o recebe em Keycloak:Admin:PublicBaseUrl — é o aud do client assertion;
# o BaseUrl, por onde ela chama o token endpoint e a Admin API, pode ser o endereço interno. A chave vem do cofre como
# arquivo montado, e a Api recusa http e AllowInsecureHttp fora de Development.
#
# QUEM JÁ TINHA VOLUMES DE ANTES DA FATIA C PRECISA DE `docker compose down -v`, e depois `docker compose up -d --build`:
# o realm só é importado na primeira subida, e num volume antigo faltam o papel tenant-admin, o manage-users, o User
# Profile e o SMTP. O /health/ready da Api acusa isso pelo nome (manage-users) em vez de deixar o convite falhar por 24 h.
```

2. Na `api`, trocar o bloco das duas linhas do Keycloak (o comentário "A Api alcança o Keycloak pelo nome do
serviço…" e `Keycloak__Admin__BaseUrl`) por:

```yaml
      # Transporte: a api chama o token endpoint e a Admin API pelo nome do serviço, dentro da rede do compose.
      Keycloak__Admin__BaseUrl: "http://keycloak:8080"
      # Endereço público: só o aud do client assertion, nunca discado. Igual ao KC_HOSTNAME do keycloak (um teste de
      # arquitetura confere); sem ele o aud seria keycloak:8080, e o Keycloak responderia "Invalid token audience".
      Keycloak__Admin__PublicBaseUrl: "http://localhost:8081"
```

(o `Keycloak__Admin__PrivateKeyPath` continua logo abaixo.)

3. No `keycloak`, o `entrypoint` passa a ser:

```yaml
    # O ENTRYPOINT da imagem é o kc.sh, então a troca vai em `entrypoint`, não em `command`. `test -s` e `test -n`
    # falham cedo se o arquivo ou a variável faltarem: um placeholder sem valor seria gravado no realm como texto
    # literal, sem erro, e só falharia na autenticação ou no primeiro e-mail. `exec` para o kc.sh receber o SIGTERM.
    entrypoint:
      - /bin/sh
      - -c
      - >-
        test -s /keys/cert.b64 && test -s /keys/admin-password &&
        test -n "$$SMTP_HOST" && test -n "$$SMTP_PORT" && test -n "$$SMTP_FROM" &&
        export GATEWAY_CLIENT_CERT="$$(cat /keys/cert.b64)" KC_BOOTSTRAP_ADMIN_PASSWORD="$$(cat /keys/admin-password)" &&
        exec /opt/keycloak/bin/kc.sh start-dev --import-realm
```

e, em `environment`, depois de `KC_HEALTH_ENABLED: "true"`:

```yaml
      # Endereço público (D8): o link do e-mail, o iss e o discovery usam localhost:8081, mesmo com a requisição
      # chegando por keycloak:8080 — e a Admin API continua respondendo pelo nome do serviço (backchannel dinâmico).
      # 127.0.0.1 no lugar de localhost quebraria o aud do assertion, comparado por texto.
      KC_HOSTNAME: "http://localhost:8081"
      KC_HOSTNAME_BACKCHANNEL_DYNAMIC: "true"
      # Lidos pelo smtpServer do realm, sem o prefixo KC_ (que o Keycloak leria como opção dele). Plugar outro servidor
      # — ou o notification-hub, quando ele aceitar SMTP — é trocar estes três valores.
      SMTP_HOST: mailpit
      SMTP_PORT: "1025"
      SMTP_FROM: convites@identity-gateway.local
```

e, em `depends_on` do `keycloak`:

```yaml
      # O SMTP do convite precisa responder quando o primeiro tenant for provisionado.
      mailpit:
        condition: service_healthy
```

4. Serviço novo, depois do `keycloak`:

```yaml
  # E-mail de desenvolvimento, em http://localhost:8025: o Keycloak envia para cá o convite do admin inicial. O SMTP
  # (1025) só existe na rede do compose, sem porta publicada; a interface e a API ficam só em 127.0.0.1, porque os
  # e-mails trazem links que trocam a senha do convidado.
  mailpit:
    # Fixada na release v1.31.3 (2026-09-27, conferida no Docker Hub e nas releases do GitHub). A mesma tag está no
    # KeycloakFixture dos testes, e um teste de arquitetura confere as duas.
    image: axllent/mailpit:v1.31.3
    ports:
      - "127.0.0.1:8025:8025"
    healthcheck:
      # O subcomando readyz da própria imagem — é o healthcheck embutido dela —, que consulta o /readyz do servidor.
      test: ["CMD", "/mailpit", "readyz"]
      interval: 5s
      timeout: 3s
      retries: 10
```

5. Portas só em `127.0.0.1`:
   - `postgres`: o comentário e a porta passam a ser
     ```yaml
         # Só em 127.0.0.1: uma ferramenta de banco na máquina alcança o contêiner, a rede local não — a tabela de
         # tenants guarda o e-mail do admin até a ativação.
         - "127.0.0.1:5432:5432"
     ```
   - `redis`: `- "127.0.0.1:6379:6379"`.
   - `seq`: `- "127.0.0.1:5341:80"` (os logs de falha podem trazer dado pessoal).

Run: `docker compose config --quiet`
Expected: sem saída (o arquivo é válido).

- [ ] **Passo 4: Rodar e ver passar**

Run: `dotnet test tests/IdentityGateway.ArchitectureTests`
Expected: PASS.

- [ ] **Passo 5: CI**

Em `.github/workflows/ci.yml`, no job `compose`:

1. No passo "Registrar um tenant e esperar o provisionamento", logo depois de `token="$cabecalho.$corpo.$assinatura"`:

```bash
          # Únicos por execução: um e-mail já convidado cairia no D5 (e-mail em uso por outra conta), e a segunda
          # execução sobre o mesmo volume quebraria. O + exercita o escape da busca no Keycloak.
          sufixo=$(date +%s)
          email="admin+$sufixo@acme.test"
          echo "EMAIL_CONVIDADO=$email" >> "$GITHUB_ENV"
```

a linha do corpo do `POST` passa a ser:

```bash
            -d "{\"name\":\"Acme\",\"slug\":\"acme-ci-$sufixo\",\"planCode\":\"free\",\"initialAdminEmail\":\"$email\"}")
```

e, dentro do laço, depois do `if … "Active" … exit 0; fi`:

```bash
            if echo "$provisionamento" | grep -q '"status":"ProvisioningFailed"'; then
              echo "O tenant caiu em ProvisioningFailed."; exit 1
            fi
```

2. Passo novo, logo depois dele (antes de "Subir de novo sobre os mesmos volumes"):

```yaml
      # O critério de sucesso da fatia C: o convite chega ao mailpit, com o link no endereço público, e o link abre. Lê
      # o campo Text com jq (o HTML traz &amp;, e o JSON da API escapa o & como &). Nunca imprime o link: ele troca
      # a senha do convidado e carrega o e-mail no token. Exige ao menos uma mensagem, nunca exatamente uma — a entrega
      # "pelo menos uma vez" pode mandar duas.
      - name: Conferir o convite no mailpit
        run: |
          busca="$RUNNER_TEMP/busca.json"
          quantidade=0
          for _ in $(seq 1 10); do
            curl --fail --silent --show-error --max-time 10 -G \
              --data-urlencode "query=to:\"$EMAIL_CONVIDADO\"" http://127.0.0.1:8025/api/v1/search -o "$busca"
            quantidade=$(jq --arg para "$EMAIL_CONVIDADO" \
              '[.messages[] | select(any(.To[]; .Address == $para))] | length' "$busca")
            if [ "$quantidade" -ge 1 ]; then break; fi
            sleep 2
          done
          if [ "$quantidade" -lt 1 ]; then echo "Nenhum convite para $EMAIL_CONVIDADO no mailpit em 20 s."; exit 1; fi

          id=$(jq -r --arg para "$EMAIL_CONVIDADO" \
            '[.messages[] | select(any(.To[]; .Address == $para))][0].ID' "$busca")
          texto=$(curl --fail --silent --show-error --max-time 10 "http://127.0.0.1:8025/api/v1/message/$id" | jq -r '.Text')
          link=$(printf '%s\n' "$texto" | grep -o 'http[^[:space:]]*/login-actions/action-token?key=[^[:space:]]*' | head -n 1)

          prefixo='http://localhost:8081/realms/identity-gateway/login-actions/action-token?key='
          case "$link" in
            "$prefixo"*) ;;
            *) echo "O link do convite não usa o endereço público: '${link%%\?*}'"; exit 1 ;;
          esac

          pagina="$RUNNER_TEMP/pagina.html"
          status=$(curl --silent --show-error --max-time 10 --output "$pagina" --write-out '%{http_code}' "$link")
          if [ "$status" != "200" ]; then echo "O link do convite respondeu $status, esperado 200."; exit 1; fi
          if grep -q 'id="kc-error-message"' "$pagina" || ! grep -q 'id="kc-info-message"' "$pagina"; then
            echo "O link abriu a página de erro ou de link expirado, e não a de ações."; exit 1
          fi
          echo "Convite no mailpit ($quantidade mensagem(ns)); o link público abriu a página de ações."
```

3. O passo "Logs em caso de falha" passa a ser:

```yaml
      - name: Logs em caso de falha
        if: failure()
        run: |
          docker compose logs --no-color
          echo "── mailpit: mensagens recebidas (destinatário, assunto, horário) ──"
          curl --silent --max-time 10 http://127.0.0.1:8025/api/v1/messages \
            | jq '[.messages[] | {para: [.To[].Address], assunto: .Subject, criado: .Created}]' || true
```

(A listagem não traz o corpo: o link fica fora do log da CI também na falha.)

4. No comentário do job (`# ─── compose ───`), acrescentar ao fim do primeiro parágrafo: "E, desde a fatia C, o
convite do admin: o e-mail no mailpit e o link público que abre."

- [ ] **Passo 6: Verificação local, num projeto de compose isolado**

**Nunca** usar o projeto padrão (`identitygateway`) nem os volumes dele: tudo aqui roda com `-p igverif`, que tem
volumes próprios (`igverif_*`).

1. Conferir que as portas estão livres:

Run: `docker compose -p identitygateway ps --format '{{.Name}} {{.Status}}'`
Expected: nenhuma linha. **Se o compose do autor estiver de pé** (as portas 8080, 8081, 8025, 5432, 6379 e 5341
estariam ocupadas), pare e pergunte ao autor antes de continuar — não derrube nada dele.

2. Subir:

Run: `docker compose -p igverif up -d --build --wait --wait-timeout 300 api`
Expected: `api` healthy; `curl -s http://localhost:8080/health/ready` responde `Healthy`.

3. Salvar o roteiro da CI num arquivo temporário, **fora do repositório** (no scratchpad da sessão), e rodá-lo com
Git Bash. `verificar-convite.sh`:

```bash
#!/usr/bin/env bash
# Os dois passos do job Compose (registro + convite no mailpit), repetidos do ci.yml, com RUNNER_TEMP e GITHUB_ENV
# locais. Se divergirem do ci.yml, o ci.yml vale: corrija este arquivo, nunca o contrário.
set -euo pipefail
export RUNNER_TEMP="$(mktemp -d)"
export GITHUB_ENV="$RUNNER_TEMP/github_env"
: > "$GITHUB_ENV"

bash -euo pipefail <<'REGISTRO'
b64url() { openssl base64 -A | tr '+/' '-_' | tr -d '='; }
agora=$(date +%s)
cabecalho=$(printf '{"alg":"HS256","typ":"JWT"}' | b64url)
corpo=$(printf '{"sub":"0199a000-0000-7000-8000-000000000001","roles":"platform-admin","iss":"identitygateway","aud":"identitygateway-api","nbf":%d,"exp":%d}' "$agora" "$((agora + 3600))" | b64url)
assinatura=$(printf '%s.%s' "$cabecalho" "$corpo" | openssl dgst -sha256 -hmac "chave-de-desenvolvimento-nao-use-em-producao" -binary | b64url)
token="$cabecalho.$corpo.$assinatura"

sufixo=$(date +%s)
email="admin+$sufixo@acme.test"
echo "EMAIL_CONVIDADO=$email" >> "$GITHUB_ENV"

resposta="$RUNNER_TEMP/resposta.json"
cabecalhos="$RUNNER_TEMP/cabecalhos.txt"
status=$(curl --silent --show-error --max-time 10 --output "$resposta" --dump-header "$cabecalhos" \
  --write-out '%{http_code}' -X POST http://localhost:8080/api/v1/tenants \
  -H "Authorization: Bearer $token" -H "Content-Type: application/json" \
  -d "{\"name\":\"Acme\",\"slug\":\"acme-ci-$sufixo\",\"planCode\":\"free\",\"initialAdminEmail\":\"$email\"}")
if [ "$status" != "202" ]; then
  echo "POST /api/v1/tenants respondeu $status, esperado 202:"; cat "$resposta"; exit 1
fi

location=$(grep -i '^location:' "$cabecalhos" | cut -d' ' -f2 | tr -d '\r')
case "$location" in http*) ;; *) location="http://localhost:8080$location" ;; esac

for _ in $(seq 1 30); do
  provisionamento=$(curl --fail --silent --show-error --max-time 10 "$location" -H "Authorization: Bearer $token")
  echo "$provisionamento"
  if echo "$provisionamento" | grep -q '"status":"Active"'; then exit 0; fi
  if echo "$provisionamento" | grep -q '"status":"ProvisioningFailed"'; then
    echo "O tenant caiu em ProvisioningFailed."; exit 1
  fi
  sleep 3
done
echo "O tenant não chegou a Active em 90 s."; exit 1
REGISTRO

set -a; . "$GITHUB_ENV"; set +a

bash -euo pipefail <<'CONVITE'
busca="$RUNNER_TEMP/busca.json"
quantidade=0
for _ in $(seq 1 10); do
  curl --fail --silent --show-error --max-time 10 -G \
    --data-urlencode "query=to:\"$EMAIL_CONVIDADO\"" http://127.0.0.1:8025/api/v1/search -o "$busca"
  quantidade=$(jq --arg para "$EMAIL_CONVIDADO" \
    '[.messages[] | select(any(.To[]; .Address == $para))] | length' "$busca")
  if [ "$quantidade" -ge 1 ]; then break; fi
  sleep 2
done
if [ "$quantidade" -lt 1 ]; then echo "Nenhum convite para $EMAIL_CONVIDADO no mailpit em 20 s."; exit 1; fi

id=$(jq -r --arg para "$EMAIL_CONVIDADO" \
  '[.messages[] | select(any(.To[]; .Address == $para))][0].ID' "$busca")
texto=$(curl --fail --silent --show-error --max-time 10 "http://127.0.0.1:8025/api/v1/message/$id" | jq -r '.Text')
link=$(printf '%s\n' "$texto" | grep -o 'http[^[:space:]]*/login-actions/action-token?key=[^[:space:]]*' | head -n 1)

prefixo='http://localhost:8081/realms/identity-gateway/login-actions/action-token?key='
case "$link" in
  "$prefixo"*) ;;
  *) echo "O link do convite não usa o endereço público: '${link%%\?*}'"; exit 1 ;;
esac

pagina="$RUNNER_TEMP/pagina.html"
status=$(curl --silent --show-error --max-time 10 --output "$pagina" --write-out '%{http_code}' "$link")
if [ "$status" != "200" ]; then echo "O link do convite respondeu $status, esperado 200."; exit 1; fi
if grep -q 'id="kc-error-message"' "$pagina" || ! grep -q 'id="kc-info-message"' "$pagina"; then
  echo "O link abriu a página de erro ou de link expirado, e não a de ações."; exit 1
fi
echo "Convite no mailpit ($quantidade mensagem(ns)); o link público abriu a página de ações."
CONVITE
```

Run: `bash <scratchpad>/verificar-convite.sh`
Expected: `… "status":"Active" …` e, no fim, `Convite no mailpit (1 mensagem(ns)); o link público abriu a página de ações.`

Abrir http://localhost:8025 no navegador e conferir o e-mail e o link (ele abre a página do Keycloak em
`http://localhost:8081`). Registrar para o handoff: o status do `POST`, o horário do `Active`, o assunto do e-mail e o
resultado do `GET` no link.

4. Segunda subida sobre os mesmos volumes (o que a CI faz em seguida):

Run: `docker compose -p igverif down && docker compose -p igverif up -d --wait --wait-timeout 300 api && bash <scratchpad>/verificar-convite.sh`
Expected: verde de novo, com outro e-mail e outro slug (os sufixos são por execução).

- [ ] **Passo 7: 🧪 Prova vermelha do `KC_HOSTNAME`**

1. Tirar do `docker-compose.yml` as linhas `KC_HOSTNAME: "http://localhost:8081"` e
`Keycloak__Admin__PublicBaseUrl: "http://localhost:8081"` (o estado de antes da fatia: o `aud` volta a sair do
`BaseUrl`, e o provisionamento continua funcionando).

Run: `dotnet test tests/IdentityGateway.ArchitectureTests --filter "FullyQualifiedName~ComposeTemKcHostnameIgualAoPublicBaseUrl"`
Expected: FAIL — "sem KC_HOSTNAME o link do convite sai com http://keycloak:8080".

Run: `docker compose -p igverif up -d --force-recreate --wait --wait-timeout 300 keycloak api && bash <scratchpad>/verificar-convite.sh`
Expected: FAIL no passo do convite — `O link do convite não usa o endereço público: 'http://keycloak:8080/realms/identity-gateway/login-actions/action-token'`.

2. Reverter: `git checkout docker-compose.yml`, `docker compose -p igverif up -d --force-recreate --wait --wait-timeout 300 keycloak api`,
rodar o roteiro de novo e ver verde. Registrar os dois resultados no handoff.

3. Encerrar **só o projeto isolado**, com os volumes dele:

Run: `docker compose -p igverif down -v`
Expected: remove os contêineres e os volumes `igverif_*`. Os volumes do projeto `identitygateway` não aparecem na
saída.

- [ ] **Passo 8: Commit**

```bash
git add docker-compose.yml .github/workflows/ci.yml tests/IdentityGateway.ArchitectureTests/RegrasDoAmbienteLocalTests.cs
git commit -m "ci: mailpit no compose e o convite conferido no job Compose

O compose sobe o mailpit (v1.31.3, SMTP so na rede interna, interface em
127.0.0.1:8025), da ao Keycloak o KC_HOSTNAME publico com backchannel
dinamico e o SMTP por ambiente, e a api recebe o PublicBaseUrl igual ao
KC_HOSTNAME. Postgres, Redis e Seq passam a publicar so em 127.0.0.1. O
job Compose registra com e-mail e slug unicos, confere o convite no
mailpit, o link no endereco publico e o GET com 200 na pagina de acoes.

Verificado localmente num projeto isolado (igverif). Prova vermelha: sem
KC_HOSTNAME o link saiu com keycloak:8080 e o passo falhou, e a regra de
arquitetura tambem."
```

---

### Tarefa 13: Especificação v2.6, documento de negócio, README, CONTRIBUTING e handoff

**Arquivos:**
- Criar: `docs/especificacao-arquitetural-v2.6.md` (cópia da v2.5, editada abaixo)
- Modificar: `docs/documentacao-negocio.md`
- Modificar: `README.md`
- Modificar: `CONTRIBUTING.md`
- Criar: `docs/superpowers/specs/AAAA-MM-DD-convite-admin-inicial-handoff.md` (data do dia da entrega, `date +%F`)

**Interfaces:**
- Consome: tudo (Tarefas 1–12).
- Produz: documentação.

**Regra desta tarefa:** todo trecho "a localizar" abaixo existe literalmente no arquivo de origem e aparece **uma
vez só** (conferido com `grep -cF` sobre a v2.5, o documento de negócio, o README e o CONTRIBUTING da branch).
Na v2.6, que nasce como cópia da v2.5, os trechos são os mesmos. Se um `Edit` falhar por não achar o trecho, o
arquivo foi alterado fora deste roteiro: pare e confira, não improvise. Todo texto novo está completo; copie como
está. Os documentos levam acento; a mensagem de commit, não.

- [ ] **Passo 1: Criar a v2.6 a partir da v2.5**

Run: `cp docs/especificacao-arquitetural-v2.5.md docs/especificacao-arquitetural-v2.6.md`

Na v2.6, cabeçalho — localizar `**Versão 2.5** · Status: aprovada para implementação` e trocar por:

```markdown
**Versão 2.6** · Status: aprovada para implementação
```

Renumerar as seções 0.x existentes, **de baixo para cima** (os títulos levam as versões, então cada um é único):

| Título atual (localizar a linha inteira) | Título novo |
|---|---|
| `## 0.4. O que mudou da v2.0 para a v2.1` | `## 0.5. O que mudou da v2.0 para a v2.1` |
| `## 0.3. O que mudou da v2.1 para a v2.2` | `## 0.4. O que mudou da v2.1 para a v2.2` |
| `## 0.2. O que mudou da v2.2 para a v2.3` | `## 0.3. O que mudou da v2.2 para a v2.3` |
| `## 0.1. O que mudou da v2.3 para a v2.4` | `## 0.2. O que mudou da v2.3 para a v2.4` |
| `## 0. O que mudou da v2.4 para a v2.5` | `## 0.1. O que mudou da v2.4 para a v2.5` |

Depois, inserir **antes** da linha `## 0.1. O que mudou da v2.4 para a v2.5` (a que acabou de ser renumerada) a
seção nova, seguida de uma linha `---` e de uma linha em branco, como as demais:

```markdown
## 0. O que mudou da v2.5 para a v2.6

Esta versão registra o que a **fatia C (convite do admin inicial)** decidiu e verificou, lendo o código-fonte do
Keycloak na tag **26.7.4**. Design da fatia:
[`2026-09-29-convite-admin-inicial-design.md`](superpowers/specs/2026-09-29-convite-admin-inicial-design.md).
**Nenhum ADR foi revogado.** As duas erratas corrigem a §9.9 e a §10.2, não ADRs, e a exceção do e-mail do admin
inicial é à regra de dados pessoais da §6 e da §10.3.

| # | Mudança | Onde |
|---|---|---|
| C1 | **O e-mail do admin inicial fica temporariamente no tenant** (`InitialAdminEmail`, coluna anulável), fora do evento `TenantRegistered`, e é apagado na transação que ativa o tenant ou que o marca `ProvisioningFailed`. É a exceção declarada e limitada à regra de dados pessoais só no Keycloak: só existe em tenant `Pending` | §5, §6, §6.1, §10.3 |
| C2 | **Ativar e reservar a vaga numa operação de domínio só, num commit só.** `Tenant.CompleteProvisioning` substitui `MarkProvisioned`: ativa, reserva a vaga do admin, cria o `Member` em `Invited` e apaga o e-mail. No Keycloak, o convite acontece antes | §6.1, §9.1, §11.1 |
| C3 | **O convite é feito pela Admin API**, em cinco passos idempotentes: buscar, criar, vincular à Organization, atribuir o papel e enviar `execute-actions-email`. O `invite-user` de Organization não cria usuário para e-mail novo, e o `Member` nasceria sem `sub` | §9.1, §11.6 |
| C4 | **Errata: o convidado nasce habilitado**, com `UPDATE_PASSWORD` e `VERIFY_EMAIL`. A §9.9 dizia que ele nascia desabilitado, e o Keycloak recusa o envio e o clique de usuário desabilitado. A expiração passa a desabilitar o usuário, e o cancelamento desabilita **e** revoga sessões. No primeiro acesso, o convidado informa nome e sobrenome (`VERIFY_PROFILE`) | §9.9 |
| C5 | **O usuário é correlacionado pelo atributo `tenant_id`**, o mesmo que alimenta o claim, declarado no User Profile só para `admin` e sem `unmanagedAttributePolicy`. E-mail em uso por outra conta é falha permanente | §9.1, §12.2, §15 |
| C6 | **O service account recebe `manage-users`**, ao lado de `manage-organizations`: vincular usuário a uma Organization exige as duas. O alcance real fica nomeado, e um teste compara os papéis **efetivos** | §10.2, §19 |
| C7 | **Errata: o `aud` do assertion é o emissor público, e o transporte pode ser interno.** `KC_HOSTNAME` no Keycloak e `Keycloak:Admin:PublicBaseUrl` na Gateway, usado só no `aud`. A regra da v2.5 "`BaseUrl` igual ao `KC_HOSTNAME`" cai. `AllowInsecureHttp` só em Development | §10.2, §15 |
| C8 | **O prazo do link vem da política de convite** (`IInvitationPolicy.LinkLifetime`, `Invitations:LinkLifetime`, padrão 7 dias, no máximo 30), passado ao Keycloak em cada envio. Global nesta versão; por tenant no M2, pela mesma porta | §9.9, §11.3 |
| C9 | **O papel vai na porta:** `EnsureInvitedUserAsync(organizationId, tenantId, InviteData, ct)`, com o `RoleName` no `InviteData` | §11.3 |
| C10 | **O e-mail só é enviado se o convite ainda não foi aceito** (`UPDATE_PASSWORD` pendente): um reenvio depois do aceite trocaria a senha do admin ativo | §9.9, §11.6 |
| C11 | **Sem e-mail ou sem vaga, o provisionamento falha antes de tocar o Keycloak.** `MarkProvisioningFailed` também apaga o e-mail | §11.5 |
| C12 | **O e-mail não vaza** por log, exceção, resposta nem coluna de erro, inclusive em Development | §10.3, §13 |
| C13 | **`Member` mínimo entregue**, com `IMemberRepository` só com `Add` nesta versão. O papel continua no Keycloak, e o admin inicial nasce sem `MemberInvited` | §6.1, §6.3, §11.9 |
| C14 | **E-mail pelo SMTP do Keycloak**, com `mailpit` em desenvolvimento e na CI; o notification-hub fica para depois. `resetPasswordAllowed: false` e eventos de administração ligados no realm | §10.3, §15, §19 |
| C15 | **Compose:** `mailpit` (que a v2.5 listava sem ele existir), SMTP por ambiente, Postgres, Redis e Seq publicados só em `127.0.0.1`; o job da CI confere o e-mail e abre o link. Volume anterior exige `docker compose down -v` | §15 |
| C16 | Fatia C entregue no andamento; o catálogo de papéis ganha o `tenant-admin`. Nenhuma rota nova | §8, §16 |
```

- [ ] **Passo 2: §5 e §6 (abertura) — a exceção de LGPD do e-mail do admin inicial**

Na v2.6, §5, localizar a linha que começa com `| Usuários | Perfil, dados pessoais, sessões |` (linha inteira:
``| Usuários | Perfil, dados pessoais, sessões | Vínculo (`sub`), status de governança, papéis e permission sets |``)
e substituí-la por:

```markdown
| Usuários | Perfil, dados pessoais, sessões | Vínculo (`sub`), status de governança, papéis e permission sets; **temporariamente**, o e-mail do admin inicial de um tenant `Pending` (v2.6, §6) |
```

Na §6, localizar o parágrafo (uma linha só) que começa com `O bounded context é **Identity Governance**.`,
mantê-lo, e inserir logo depois dele, separado por uma linha em branco:

```markdown
**Exceção declarada e limitada (v2.6): o e-mail do admin inicial.** Entre o `POST /tenants` e o convite, o `initialAdminEmail` fica numa coluna anulável do tenant (`InitialAdminEmail`), fora do evento `TenantRegistered` — que o levaria ao Outbox e, com o broker, ao RabbitMQ. Ele só existe em tenant `Pending` e é apagado na transação que ativa o tenant **ou** que o marca `ProvisioningFailed` (§9.1). Cifrá-lo em repouso exigiria gestão de chave para um dado que vive horas; uma tabela própria seria uma segunda fonte de estado do provisionamento. A retenção real do valor apagado e as proteções contra vazamento estão na §10.3.
```

- [ ] **Passo 3: §6.1 — `InitialAdminEmail`, o `Member` mínimo e a vaga do admin na ativação**

Na v2.6, §6.1, localizar a linha
``- Estado: `Plan` (value object com `Tier`, `MaxUsers` e `MaxClients`), `Status`, `OverSubscribed` (bool) e a lista de `EmailDomain`.``
mantê-la, e inserir logo depois dela:

```markdown
- **`InitialAdminEmail`** (`Email?`, v2.6): o e-mail do admin inicial, exigido por `Register` e guardado só enquanto o tenant está `Pending`. É apagado na mesma operação que ativa o tenant (`CompleteProvisioning`) ou que o marca `ProvisioningFailed`, e nunca vai ao evento `TenantRegistered`. É a exceção declarada à regra de dados pessoais só no Keycloak (§6).
- **`HasSeatAvailable`** (leitura, v2.6): se ainda há vaga livre no plano. O provisionamento a consulta antes de tocar o Keycloak (§11.5).
```

Ainda na §6.1, localizar a linha `  - um tenant fora do status `Active` não aceita novos membros;` e substituí-la por:

```markdown
  - um tenant fora do status `Active` não aceita novos membros. **A única exceção é o admin inicial** (v2.6): a vaga dele e o `Member` nascem na mesma operação de domínio que ativa o tenant (`CompleteProvisioning`, §11.1), gravada num commit só — `ReserveSeat` continua exigindo `Active` para todo outro chamador;
```

Ainda na §6.1, localizar a linha (uma só) que começa com
``- `WasActiveBeforeSuspension` existe para que a reativação do tenant``, mantê-la, e inserir logo depois dela,
separado por uma linha em branco:

```markdown
- **O admin inicial é a exceção de ordem na ocupação de vaga** (v2.6): no Keycloak, o convite — e o e-mail — acontece antes da ativação; no domínio, a vaga é reservada na ativação, junto com a criação do `Member`, depois do envio do e-mail. Um tenant sem vaga livre falha antes de tocar o Keycloak (§11.5). Deixar o admin fora do contador criaria um membro grátis e um caso especial eterno nas vagas, no downgrade (N8) e na expiração.

> **Entregue na v2.6 (fatia C), no mínimo que o convite do admin exige.** `Id` (`MemberId`, `readonly record struct` com `Guid.CreateVersion7()`, como o `TenantId`), `TenantId`, `ExternalUserId` (o `sub`), `Status` (`MemberStatus`, com os seis valores acima, persistido como texto) e `InvitedAt` (UTC), na tabela `members`, com índice único em `(tenant_id, external_user_id)` — a rede final que o ADR-007 e o ADR-010 já previam. A fábrica `Member.Invite` é `internal` ao Domain e só `Tenant.CompleteProvisioning` a chama, para que nenhum membro nasça sem vaga reservada; um teste de arquitetura garante que não há construtor nem fábrica públicos. **O papel vive no Keycloak** (ADR-005) e não é duplicado no `Member` nesta versão: os "papéis do catálogo global" acima ficam para o M2, com `PUT .../roles`. `WasActiveBeforeSuspension` e as referências a Permission Sets entram com as fatias que as usam.
```

- [ ] **Passo 4: §6.3 — `MemberInvited` continua no catálogo, mas o admin inicial nasce sem ele**

Na v2.6, §6.3, localizar este trecho (fim do item "Domain events"):

```markdown
`MemberRolesChanged` e `PermissionSetChanged`. São convertidos em eventos de integração e publicados pelo Outbox.
```

e substituí-lo por:

```markdown
`MemberRolesChanged` e `PermissionSetChanged`. São convertidos em eventos de integração e publicados pelo Outbox. `MemberInvited` continua no catálogo, para o convite comum do M2, mas **o admin inicial nasce sem ele** (v2.6): o evento ainda não teria consumidor, e o `Member` do admin é criado dentro de `Tenant.CompleteProvisioning`, que levanta só `TenantActivated`.
```

- [ ] **Passo 5: §8 — sem rota nova; nota sobre a operação de plataforma futura**

Na v2.6, §8, localizar a última linha da tabela de endpoints,
``| **Saúde** | `GET /health/live`, `GET /health/ready` | anônimo (rede interna) |``, mantê-la, e inserir logo
depois dela, separado por uma linha em branco:

```markdown
**Sem rota nova na v2.6.** O convite do admin inicial acontece dentro do provisionamento (§9.1), e o `initialAdminEmail` do `POST /tenants` passa a ficar guardado no tenant só até a ativação (§6). Nenhuma resposta nem read model expõe esse e-mail, inclusive o `GET .../provisioning`. **Falta uma operação de plataforma para trocar ou reenviar o convite do admin inicial:** o platform-admin não opera rotas de membro (§10.1), e hoje um e-mail digitado errado entrega o tenant a quem o recebe — o runbook provisório é desabilitar o usuário no Keycloak à mão. A operação fica para o M1/M2 (§19).
```

- [ ] **Passo 6: §9.1 — diagrama com o convite e sem o RabbitMQ; as decisões no lugar das pendências**

Na v2.6, §9.1, localizar o bloco de código cuja primeira linha é
`Cliente            Gateway API                PostgreSQL           Consumidor              Keycloak` e cuja última
linha termina em `│ Tenant → Active      │`. Substituir o bloco inteiro — da cerca de abertura à de fechamento —
por:

````markdown
```
Cliente            Gateway API                PostgreSQL           Consumidor              Keycloak
   │ POST /tenants      │                          │                    │                      │
   │───────────────────►│ Tenant (Pending)         │                    │                      │
   │                    │ + e-mail no tenant       │                    │                      │
   │                    │ + evento no Outbox ─────►│ (mesma transação)  │                      │
   │ 202 + Location     │                          │                    │                      │
   │◄───────────────────│                          │ ── Outbox ────────►│                      │
   │                    │                          │                    │ 0. e-mail e vaga?    │
   │                    │                          │                    │ 1. Organization      │
   │                    │                          │                    │─────────────────────►│
   │                    │                          │                    │ 2. usuário, vínculo, │
   │                    │                          │                    │    papel e e-mail    │
   │                    │                          │                    │─────────────────────►│ ──► SMTP (mailpit)
   │                    │                          │◄───────────────────│ 3. um commit só:     │
   │                    │                          │                    │ Active + vaga +      │
   │                    │                          │                    │ Member (Invited) +   │
   │                    │                          │                    │ e-mail apagado       │
```
````

Ainda na §9.1, localizar estas cinco linhas (a do parágrafo e as três do passo a passo, com a linha em branco
entre elas):

```markdown
**O tenant nasce com um administrador.** `POST /tenants` exige `initialAdminEmail`, e o provisionamento executa três passos idempotentes antes de marcar `Active`:

1. garante a Organization (com o atributo `gateway_tenant_id`);
2. garante o convite do `initialAdminEmail` já com o papel `tenant-admin`;
3. marca o tenant `Active`.
```

e substituí-las por:

```markdown
**O tenant nasce com um administrador.** `POST /tenants` exige `initialAdminEmail`, que fica no tenant até a ativação (§6, v2.6). O consumidor, hoje o `ProvisionTenantHandler` despachado pelo próprio Outbox (§11.5), verifica antes o que repetir não corrige, e depois executa dois passos idempotentes no Keycloak e um no banco:

0. sem e-mail (tenant registrado antes da v2.6) ou sem vaga livre no plano: `ProvisioningFailed`, sem tocar o Keycloak;
1. garante a Organization (com o atributo `gateway_tenant_id`);
2. garante o convite do `initialAdminEmail` com o papel `tenant-admin`: usuário habilitado com `UPDATE_PASSWORD` e `VERIFY_EMAIL` e o atributo `tenant_id`, vínculo à Organization, papel e o e-mail de ações obrigatórias, enviado só se o convite ainda não foi aceito (§11.6);
3. numa operação de domínio e num commit só: marca o tenant `Active`, reserva a vaga do admin, cria o `Member` em `Invited` e apaga o e-mail (§11.1).
```

Ainda na §9.1, localizar o bloco de citação (uma linha só) que começa com
`> **Duas decisões pendentes do passo 2, a fechar antes da fatia C (v2.4).**` e substituí-lo inteiro por:

```markdown
**As decisões do passo 2 (v2.6).** A v2.4 deixou duas decisões pendentes para a fatia C; a fatia as fechou e acrescentou uma terceira:

1. **Onde o `initialAdminEmail` vive.** Numa coluna anulável do tenant (`InitialAdminEmail`), fora do evento `TenantRegistered`, que iria ao Outbox e, com o broker, ao RabbitMQ. É apagado na mesma transação que ativa o tenant **ou** que o marca `ProvisioningFailed`, e só existe em tenant `Pending`: é a exceção declarada à regra de dados pessoais só no Keycloak (§6, §10.3). Apagar também na falha evita retenção sem prazo, porque `ProvisioningFailed` não tem saída automática; o retry manual, quando existir, recebe o e-mail de novo no corpo, o que também permite corrigir um e-mail digitado errado. Convidar de forma síncrona no `POST` quebraria o `202` com o Keycloak fora do ar.
2. **Vaga × `Active`.** No Keycloak, o convite acontece antes da ativação; no domínio, ativar, reservar a vaga e criar o `Member` são uma operação só — `Tenant.CompleteProvisioning` (§11.1) —, gravada num commit só. `ReserveSeat` continua exigindo `Active`, e a invariante "tenant fora de `Active` não aceita membros" continua valendo para todo outro chamador (§6.1). Fazer `ReserveSeat` aceitar `Pending` enfraqueceria a invariante para todo chamador futuro.
3. **E-mail já em uso por outra conta.** O usuário é correlacionado pelo atributo de usuário `tenant_id`, o mesmo que alimenta o claim (§12.2). Um usuário com o `tenant_id` deste tenant é uma tentativa anterior e é reaproveitado; um e-mail em uso por qualquer outra conta é `IdentityProviderInconsistencyException`, e o tenant vai a `ProvisioningFailed`. Reaproveitar um usuário sem esse vínculo poderia entregar o tenant ao platform-admin ou a uma conta abandonada. Consequência: a mesma pessoa não administra dois tenants (ADR-009, §19).

A janela de provisionamento e a classificação de erros do parágrafo anterior valem para as duas chamadas ao Keycloak, a da Organization e a do convite (v2.6).
```

- [ ] **Passo 7: §9.9 — errata: o convidado nasce habilitado; prazo do link, reenvio e cancelamento**

Na v2.6, §9.9, localizar o parágrafo (uma linha só) que começa com
``**Convite.** `POST /tenants/{tenantId}/members` reserva a vaga`` e substituí-lo inteiro por:

```markdown
**Convite.** `POST /tenants/{tenantId}/members` reserva a vaga, cria o usuário no Keycloak **habilitado**, sem senha, com as *required actions* (`UPDATE_PASSWORD`, `VERIFY_EMAIL`) e o atributo `tenant_id`, dispara o e-mail e grava `InvitedAt`. O membro nasce `Invited`. O link do e-mail vale pelo prazo da **política de convite** (`IInvitationPolicy.LinkLifetime`, padrão de 7 dias), passado ao Keycloak em cada envio — o padrão do realm, 12 horas, ficaria desalinhado do ciclo do convite. Nesta versão o prazo é global; o prazo por tenant chega no M2, pela mesma porta (v2.6). O admin inicial é o primeiro convite de todo tenant e percorre este mesmo caminho dentro do provisionamento (§9.1), com uma diferença de ordem: a vaga dele é reservada na ativação, depois do envio do e-mail (§6.1).

> **Errata da v2.6: o convidado nasce habilitado.** A v2.5 dizia que o usuário nascia desabilitado. O Keycloak 26.7.4 recusa `execute-actions-email` para usuário desabilitado (`400 User is disabled`) e recusa também o clique no link, e nada no fluxo o habilita. O que protege a conta antes do aceite é não ter senha: o único caminho de entrada é o link, e o "esqueci a senha" fica desligado no realm (`resetPasswordAllowed: false`, §10.3). Por isso a expiração e o cancelamento, abaixo, passam a desabilitar o usuário.
```

Ainda na §9.9, localizar a primeira frase do parágrafo de aceite,
`**Aceite.** O usuário define a senha no Keycloak e o evento chega pela sincronização do ADR-007.`, e substituí-la
(só a frase; o resto do parágrafo fica) por:

```markdown
**Aceite.** O usuário define a senha e informa nome e sobrenome no Keycloak (a ação `VERIFY_PROFILE`, que o perfil padrão do realm já exige), e o evento chega pela sincronização do ADR-007 (v2.6).
```

Ainda na §9.9, no parágrafo de expiração, localizar a frase
`O prazo é **configurável por tenant, com padrão de 7 dias**, e deve ser alinhado ao prazo do link de ações do Keycloak — um link ainda válido para um membro já `Expired` produziria um aceite sem vaga reservada.`
e substituí-la por:

```markdown
O prazo é **configurável por tenant, com padrão de 7 dias**, e o prazo do link de ações é o da política de convite, alinhado a ele. **A expiração também desabilita o usuário no Keycloak** (v2.6): como ele nasce habilitado, um link ainda válido para um membro já `Expired` produziria um aceite sem vaga reservada. **A expiração não pode chegar antes da sincronização do ADR-007** (M4) sem outra forma de saber do aceite: até lá, quem aceitou continua `Invited` na Gateway — é o caso do admin inicial —, e o job expiraria quem já entrou (§19).
```

Ainda na §9.9, no parágrafo de reenvio, localizar
``prorrogando a vaga já ocupada. Só é aceito em `Invited`.`` e substituir por:

```markdown
prorrogando a vaga já ocupada. Só é aceito em `Invited`, e **só envia se o usuário ainda tiver `UPDATE_PASSWORD` pendente** (v2.6): um link enviado depois do aceite trocaria a senha de quem já entrou. Os links emitidos antes continuam válidos até expirar, porque o Keycloak não os revoga por usuário (§19).
```

Ainda na §9.9, localizar o parágrafo (uma linha só) que começa com
``**Cancelamento.** `DELETE .../members/{memberId}/invite` leva`` e termina em `porque o usuário está desabilitado.`,
e substituí-lo inteiro por:

```markdown
**Cancelamento.** `DELETE .../members/{memberId}/invite` leva ao estado **`Revoked`**, libera a vaga, desabilita o usuário no Keycloak **e revoga as sessões dele** (v2.6). Só é aceito em `Invited`. A v2.5 dizia que não havia sessão a revogar, porque o usuário nascia desabilitado; ele nasce habilitado, e pode ter definido a senha pelo link antes de o aceite chegar à Gateway pela sincronização do ADR-007. Desabilitado, ele também deixa de conseguir usar um link de ações ainda válido.
```

- [ ] **Passo 8: §10.2 — errata do `aud`, o raio de dano com `manage-users`, `AllowInsecureHttp`**

Na v2.6, §10.2, tabela de claims do assertion, localizar a linha
``| `aud` | o **issuer** do realm (`{BaseUrl}/realms/{realm}`), como **valor único** |`` e substituí-la por:

```markdown
| `aud` | o **emissor público** do realm (`{PublicBaseUrl ?? BaseUrl}/realms/{realm}`, `KeycloakAdminOptions.AssertionAudience`), como **valor único** (v2.6) |
```

No bloco de código logo abaixo, localizar a linha
`    Audience = issuer,                 // string única; nunca também em Claims["aud"]` e substituí-la por:

```csharp
    Audience = assertionAudience,      // emissor PÚBLICO (v2.6); string única; nunca também em Claims["aud"]
```

Localizar o parágrafo (uma linha só) que começa com
`O issuer esperado é calculado pelo Keycloak a partir da URL da requisição.` e substituí-lo inteiro por:

```markdown
**Emissor público e transporte (errata da v2.6).** O Keycloak compara o `aud` com o emissor do realm por igualdade exata de texto, e o emissor é a URL de **frontend**: com `KC_HOSTNAME` definido, o endereço público, mesmo que a requisição chegue pelo endereço interno. A v2.5 dizia que, em produção, o `BaseUrl` precisava ser igual ao `KC_HOSTNAME`; isso só valeria se a Gateway chamasse o Keycloak pelo endereço público. A regra certa: **o `aud` é o emissor público, e o transporte pode ser o endereço interno.** `KeycloakAdminOptions.PublicBaseUrl` — opcional, absoluta, sem query nem fragmento, nunca discada — alimenta só `AssertionAudience = {PublicBaseUrl ?? BaseUrl}/realms/{Realm}`; o token endpoint e a Admin API continuam derivados **só** do `BaseUrl`. Omitido, vale o `BaseUrl`, e nada muda para quem roda a API pela IDE com `BaseUrl=http://localhost:8081`. `127.0.0.1` no lugar de `localhost` quebra o `aud`, porque a comparação é por texto (`Invalid token audience`). O `ValidateOnStart` recusa `BaseUrl` sem https fora de `Development`, e **`AllowInsecureHttp` passa a ser recusado fora de `Development`** (v2.6): "transporte interno" não pode virar convite a `http` em produção com um bearer de `manage-users`.
```

Localizar o item (uma linha só) que começa com
``- O service account recebe **somente** o papel `manage-organizations` `` e substituí-lo inteiro por:

```markdown
- O service account recebe **exatamente** dois papéis do client `realm-management` (v2.6): `manage-organizations` (novo na 26.7.0; cobre criar, listar e ler Organizations — `view-realm` não lê) e `manage-users`, que o convite exige — vincular um usuário a uma Organization pede **as duas** permissões. Um teste de integração compara os papéis **efetivos**, com os compostos expandidos, sem depender de ordem, e afirma a ausência de `impersonation`, `realm-admin`, `manage-realm`, `manage-clients` e `manage-identity-providers`. **Raio de dano:** `manage-organizations` permite alterar e apagar *qualquer* Organization do realm, inclusive reescrever `gateway_tenant_id` e domínios. `manage-users` permite atribuir qualquer papel que não seja de administração — **inclusive `platform-admin`** — e os de administração que o próprio service account tem, trocar senhas e desabilitar usuários, inclusive platform-admins. Quem tiver a chave da Gateway pode criar uma conta própria com esses papéis, e esse acesso sobrevive à rotação da chave (§19). As permissões granulares (v2) não recortam "só os usuários das Organizations da Gateway", e o M2 precisa de `manage-users` de qualquer forma. Cada papel novo continua entrando com o teste que o justifica.
```

- [ ] **Passo 9: §10.3 — exceção de LGPD e retenção real; e-mail fora dos logs; `resetPasswordAllowed`; eventos de administração**

Na v2.6, §10.3, localizar o trecho
`Os eventos administrativos do Keycloak são guardados com retenção configurada.` (fim do item "Auditoria") e
substituí-lo por:

```markdown
Os eventos administrativos do Keycloak são guardados com retenção configurada, e ficam **ligados no realm** (`adminEventsEnabled: true`, v2.6), para que as atribuições de papel feitas pelo service account fiquem registradas.
```

Localizar a linha `- **LGPD:** dados pessoais somente no Keycloak; a Gateway guarda vínculos e governança.` e
substituí-la por estes três itens:

```markdown
- **LGPD:** dados pessoais somente no Keycloak; a Gateway guarda vínculos e governança. **Exceção declarada (v2.6):** o e-mail do admin inicial fica na coluna `tenants.initial_admin_email` enquanto o tenant está `Pending`, e é apagado na transação que o ativa ou que o marca `ProvisioningFailed` (§6, §9.1). Nenhuma resposta nem read model o expõe, inclusive o `GET .../provisioning`. **Retenção real:** o apagamento é lógico; WAL, *dead tuples* e backups guardam o valor pela retenção deles (§19). Postgres, Redis e Seq publicam só em `127.0.0.1` no compose (§15).
- **O e-mail não vaza por log, exceção, resposta nem coluna de erro, inclusive em Development (v2.6).** `Email.ToString()` não devolve o endereço (quem precisa do valor usa `Email.Value`); `InviteData` sobrescreve o `ToString` (§11.3); `DomainErrors.Email.Invalido` não ecoa o valor; e as exceções do adaptador levam o `tenantId`, nunca o e-mail — o `OutboxProcessor` grava a mensagem da exceção em `outbox_messages.error`. Em Development, `Database:EnableSensitiveDataLogging` fica `false` e a categoria `Microsoft.EntityFrameworkCore` sobe para `Warning`: sem isso, o EF registraria os parâmetros de um comando que falha e, no caminho feliz, o valor antigo da coluna ao apagá-la. O validador do `POST` e `Email.Of` usam a mesma regra: parte local de até 64 caracteres em `[a-z0-9._+-]` e domínio com rótulos `[a-z0-9-]`. Um teste com todas as categorias em `Trace` e um exporter OpenTelemetry em memória prova a regra (§13).
- **Sem "esqueci a senha" (v2.6).** `resetPasswordAllowed: false` explícito no realm: o convidado nasce habilitado (§9.9), e o reset de senha daria acesso a ele fora do ciclo do convite.
```

- [ ] **Passo 10: §11.1 — `Tenant` com `Register` com e-mail e `CompleteProvisioning`; o `Member`**

Na v2.6, §11.1, localizar a linha
`    public bool OverSubscribed { get; private set; }             // só a absorção externa (9.4) liga isto`,
mantê-la, e inserir logo depois dela:

```csharp
    public DateTimeOffset RegisteredAt { get; private set; }     // de onde conta a janela de provisionamento (v2.5)
    public Email? InitialAdminEmail { get; private set; }        // só em Pending; apagado na ativação ou na falha (v2.6)

    public bool HasSeatAvailable => OccupiedSeats < Plan.MaxUsers;  // o handler consulta antes do Keycloak (v2.6)
```

Ainda no bloco, localizar o trecho que vai da linha
`    public static Tenant Register(string name, TenantSlug slug, Plan plan)` até a linha `    }` que fecha o método
`MarkProvisioned` (a que vem logo depois de `        Raise(new TenantActivated(Id));`), e substituí-lo inteiro por:

```csharp
    public static Tenant Register(
        string name, TenantSlug slug, Plan plan, Email initialAdminEmail, DateTimeOffset registeredAt)
    {
        var tenant = new Tenant
        {
            Id = TenantId.New(),
            Name = name,
            Slug = slug,
            Plan = plan,
            Status = TenantStatus.Pending,
            // Exceção declarada à regra da §6 (v2.6): o e-mail vive aqui só enquanto o tenant está Pending,
            // e nunca no evento — que iria ao Outbox e, com o broker, ao RabbitMQ.
            InitialAdminEmail = initialAdminEmail,
            RegisteredAt = registeredAt.ToUniversalTime()
        };

        // O evento vira mensagem no Outbox; é ele que dispara o provisionamento. Continua sem o e-mail.
        tenant.Raise(new TenantRegistered(tenant.Id, slug));
        return tenant;
    }

    /// <summary>
    /// Conclui o provisionamento (v2.6): ativa, reserva a vaga do admin inicial, cria o Member em Invited e
    /// apaga o e-mail — numa operação só, gravada num commit só. Substitui o MarkProvisioned da v2.5.
    /// </summary>
    public Member CompleteProvisioning(
        string externalOrganizationId, ExternalUserId adminUserId, DateTimeOffset invitedAt)
    {
        // Valida TUDO antes de mudar qualquer coisa: uma exceção no meio não pode deixar o agregado pela metade.
        // Recusa Active e ProvisioningFailed — o inverso do MarkProvisioned da v2.5, que aceitava os dois.
        EnsureStatusIn(TenantStatus.Pending);

        // O handler já verificou a vaga antes de tocar o Keycloak; faltar aqui é erro de programação.
        if (!HasSeatAvailable)
            throw new DomainInvariantViolation($"Tenant {Id}: sem vaga para o admin inicial.");

        ExternalOrganizationId = externalOrganizationId;
        Status = TenantStatus.Active;
        Raise(new TenantActivated(Id));

        OccupiedSeats++;                  // não passa por ReserveSeat, que continua exigindo Active
        InitialAdminEmail = null;         // o dado pessoal sai na mesma transação que ativa

        // Fábrica internal: nenhum membro nasce sem a vaga reservada.
        return Member.Invite(Id, adminUserId, invitedAt);
    }

    /// <summary>
    /// Marca a falha do provisionamento. Também apaga o e-mail (v2.6): este estado não tem saída automática,
    /// e o retry manual recebe o e-mail de novo.
    /// </summary>
    public void MarkProvisioningFailed()
    {
        if (Status == TenantStatus.ProvisioningFailed)
            return;

        EnsureStatusIn(TenantStatus.Pending);

        Status = TenantStatus.ProvisioningFailed;
        InitialAdminEmail = null;
    }
```

Ainda na §11.1, localizar a linha `### 11.2. Domain: regra contra escalação de privilégio` e inserir **antes** dela
(depois da cerca que fecha o bloco do `Tenant`, com uma linha em branco antes e outra depois):

````markdown
O `Member` entregue na v2.6 é o mínimo que o convite do admin exige (§6.1):

```csharp
namespace IdentityGateway.Domain.Members;

/// <summary>
/// Vínculo de uma pessoa com um tenant. O papel vive no Keycloak (ADR-005) e não é duplicado aqui nesta versão.
/// </summary>
public sealed class Member : AggregateRoot<MemberId>, IAuditable
{
    // Um construtor só, usado pela fábrica e pelo EF (todos os parâmetros são conversões de valor único).
    private Member(MemberId id, TenantId tenantId, ExternalUserId externalUserId, MemberStatus status, DateTimeOffset invitedAt)
        : base(id)
    {
        TenantId = tenantId;
        ExternalUserId = externalUserId;
        Status = status;
        InvitedAt = invitedAt;
    }

    public TenantId TenantId { get; private set; }
    public ExternalUserId ExternalUserId { get; private set; }   // o sub
    public MemberStatus Status { get; private set; }             // persistido como texto
    public DateTimeOffset InvitedAt { get; private set; }        // UTC
    // CreatedAt, UpdatedAt, CreatedBy e UpdatedBy (IAuditable), preenchidos pelo interceptor de auditoria.

    // internal: só Tenant.CompleteProvisioning a chama, para que nenhum membro nasça sem a vaga reservada. Um teste de
    // arquitetura garante que não há construtor nem fábrica públicos.
    internal static Member Invite(TenantId tenantId, ExternalUserId externalUserId, DateTimeOffset invitedAt)
    {
        if (tenantId.Value == Guid.Empty) throw new ArgumentException("O membro precisa de um tenant.", nameof(tenantId));
        ArgumentNullException.ThrowIfNull(externalUserId);
        return new Member(MemberId.New(), tenantId, externalUserId, MemberStatus.Invited, invitedAt.ToUniversalTime());
    }
}
// Identidade tipada, como TenantId: os ArchitectureTests reprovam Guid cru como identidade de raiz.
public readonly record struct MemberId(Guid Value)
{
    public static MemberId New() => new(Guid.CreateVersion7());
}

public enum MemberStatus { Invited, Active, Deactivated, Expired, Revoked, Erased }
```
````

- [ ] **Passo 11: §11.3 — `EnsureInvitedUserAsync` com o `TenantId` e o papel no `InviteData`**

Na v2.6, §11.3, localizar a linha
`    Task<ExternalUserId> EnsureInvitedUserAsync(string organizationId, InviteData data, CancellationToken ct);` e
substituí-la por:

```csharp
    // v2.6: o TenantId vai no atributo tenant_id do usuário (§12.2) e correlaciona tentativas; o papel vai no InviteData.
    Task<ExternalUserId> EnsureInvitedUserAsync(
        string organizationId, TenantId tenantId, InviteData invite, CancellationToken ct);
```

Localizar a linha `### 11.4. Application: command de registro de tenant` e inserir **antes** dela (depois da cerca
que fecha o bloco do `IIdentityProvider`, com uma linha em branco antes e outra depois):

````markdown
**A assinatura de `EnsureInvitedUserAsync` diverge da v2.5 (v2.6).** Acrescenta o `TenantId`, que é a chave da correlação do usuário (atributo `tenant_id`, §12.2) — o mesmo movimento do item I1 da v2.4 para `EnsureOrganizationAsync` —, e o papel vai no `InviteData`, porque escolher o papel é regra de negócio, e o M2 convida com outros papéis. O contrato de erro é o de `EnsureOrganizationAsync`: só `IdentityProviderInconsistencyException` é permanente, o resto é infraestrutura, e nenhuma exceção carrega o e-mail.

```csharp
namespace IdentityGateway.Application.Common.Abstractions;

public sealed record InviteData(Email Email, RoleName Role, TimeSpan LinkLifetime)
{
    // O ToString gerado do record imprimiria o e-mail em qualquer log que o interpolasse (§10.3).
    public override string ToString() => $"InviteData {{ Role = {Role}, LinkLifetime = {LinkLifetime} }}";
}

/// <summary>
/// Prazo do link de convite. Global nesta versão (Invitations:LinkLifetime, padrão 7.00:00:00, positivo, em
/// segundos inteiros e no máximo 30 dias, validado na subida); por tenant no M2, pela mesma porta.
/// </summary>
public interface IInvitationPolicy
{
    TimeSpan LinkLifetime { get; }
}
```
````

- [ ] **Passo 12: §11.4 — o command com `InitialAdminEmail`**

Na v2.6, §11.4, localizar o bloco de código que começa com a linha
`namespace IdentityGateway.Application.Tenants.Commands;` e termina na cerca antes de
`### 11.5. Application e Infrastructure: provisionamento idempotente`. Substituir o **conteúdo** do bloco (a cerca
` ```csharp ` de abertura e a de fechamento ficam) por:

```csharp
namespace IdentityGateway.Application.Tenants.Commands;

// v2.6: o e-mail do admin inicial entra no command e fica no tenant até a ativação (§9.1). Não vai ao evento.
public sealed record RegisterTenantCommand(
    string Name, string Slug, string PlanCode, string InitialAdminEmail) : ICommand<TenantId>;

internal sealed class RegisterTenantHandler(
    ITenantRepository tenants,
    IPlanCatalog plans,
    IDateTimeProvider relogio) : ICommandHandler<RegisterTenantCommand, TenantId>
{
    public async ValueTask<Result<TenantId>> Handle(RegisterTenantCommand command, CancellationToken ct)
    {
        var slug = TenantSlug.Create(command.Slug);
        if (slug.IsFailure)
            return slug.Error;

        if (await tenants.SlugExistsAsync(slug.Value, ct))
            return TenantErrors.SlugInUse(slug.Value);

        var plan = plans.Find(command.PlanCode);
        if (plan is null)
            return TenantErrors.UnknownPlan(command.PlanCode);

        // A mesma regra do validador do POST (v2.6); o erro não ecoa o valor (§10.3).
        var email = Email.Of(command.InitialAdminEmail);
        if (email.IsFailure)
            return email.Error;

        var tenant = Tenant.Register(command.Name, slug.Value, plan, email.Value, relogio.UtcNow);
        tenants.Add(tenant);

        // Nenhuma chamada ao Keycloak aqui. O INSERT e o evento no Outbox são gravados na mesma transação,
        // pelo TransactionBehavior: se o Keycloak estiver fora do ar, nada fica órfão; o provisionamento
        // apenas acontece mais tarde.
        return tenant.Id;
    }
}
```

- [ ] **Passo 13: §11.5 — o handler com a verificação prévia, o convite, o `IMemberRepository` e o `CompleteProvisioning`**

Na v2.6, §11.5, localizar o trecho do bloco de código que vai da linha
`// Application: a regra do provisionamento — inclusive a de desistir.` até a linha `}` que fecha a classe
`ProvisionTenantHandler` (a segunda `}` depois de
`        return Result.Success();                   // o commit é do TransactionBehavior`). O
`DispatchingOutboxPublisher`, abaixo, não muda. Substituir esse trecho por:

```csharp
// Application: a regra do provisionamento — inclusive a de desistir.
public sealed class ProvisionTenantHandler(
    ITenantRepository tenants,
    IMemberRepository members,
    IIdentityProvider identidade,
    IProvisioningPolicy politica,
    IInvitationPolicy convites,
    IDateTimeProvider relogio,
    ILogger<ProvisionTenantHandler> logger) : ICommandHandler<ProvisionTenantCommand>
{
    public async ValueTask<Result> Handle(ProvisionTenantCommand command, CancellationToken ct)
    {
        Tenant? tenant = await tenants.GetAsync(command.TenantId, ct);

        // Mensagem repetida, tenant removido, ou Failed (que só o retry manual desfaz): nada a fazer.
        if (tenant is null || tenant.Status != TenantStatus.Pending)
            return Result.Success();

        // Verificação prévia (v2.6): o que repetir não corrige sai antes de tocar o Keycloak.
        // Sem e-mail: tenant registrado antes da v2.6. Sem vaga: Plan com MaxUsers zero, de dado antigo.
        if (tenant.InitialAdminEmail is not { } email || !tenant.HasSeatAvailable)
        {
            tenant.MarkProvisioningFailed();       // também apaga o e-mail
            return Result.Success();
        }

        string organizationId;
        ExternalUserId adminUserId;
        try
        {
            // O mesmo try cobre as duas chamadas: a classificação de erro vale para as duas.
            organizationId = await identidade.EnsureOrganizationAsync(tenant.Id, tenant.Slug, tenant.Name, ct);
            adminUserId = await identidade.EnsureInvitedUserAsync(
                organizationId, tenant.Id, new InviteData(email, RoleName.TenantAdmin, convites.LinkLifetime), ct);
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
        // Dentro da janela, a exceção sobe intacta, o tenant continua Pending e com o e-mail, e o transporte repete.

        Member admin = tenant.CompleteProvisioning(organizationId, adminUserId, relogio.UtcNow);
        members.Add(admin);
        return Result.Success();                   // o commit é do TransactionBehavior: tenant e Member juntos
    }
}
```

Ainda na §11.5, localizar o parágrafo (uma linha só) que começa com `**Por que não MassTransit (v2.5).**`, mantê-lo, e
inserir logo depois dele, separado por uma linha em branco:

```markdown
**Verificação prévia, convite e ativação (v2.6).** O handler recusa, antes de tocar o Keycloak, o que repetir não corrige: tenant sem e-mail (registrado antes da v2.6) e tenant sem vaga (um `Plan` com `MaxUsers` zero, vindo de dado antigo — o catálogo passa a recusar `maxUsers < 1` na subida). Deixar a falta de vaga para `CompleteProvisioning` faria cada retry reenviar o convite até esgotar a janela. O mesmo `try` cobre as duas chamadas ao Keycloak, e a classificação não muda. Depois de uma falha transitória no convite, o tenant continua `Pending` e com o e-mail. `CompleteProvisioning` e `IMemberRepository.Add` gravam tenant e `Member` no mesmo commit.

**O e-mail de convite pode sair mais de uma vez (v2.6).** O Keycloak envia dentro da requisição do passo 5 (§11.6). Qualquer exceção depois dele e antes do commit — falha de commit, conflito de `xmin`, violação do índice único de `members` — faz a mensagem voltar, e a nova tentativa reenvia, porque o usuário ainda tem `UPDATE_PASSWORD` pendente. É coerente com a entrega "pelo menos uma vez" do Outbox, e está nos limites (§19).
```

- [ ] **Passo 14: §11.6 — os cinco passos do adaptador e a tabela de erros**

Na v2.6, §11.6, localizar a linha `    private const string TenantIdAttribute = "gateway_tenant_id";`, mantê-la, e inserir
logo depois dela:

```csharp

    // Atributo de USUÁRIO que correlaciona a conta ao tenant (v2.6). É o mesmo que alimenta o claim tenant_id
    // (§12.2), e precisa estar declarado no User Profile do realm, senão o Keycloak o descarta em silêncio.
    private const string UserTenantAttribute = "tenant_id";

    private static readonly string[] InviteActions = ["UPDATE_PASSWORD", "VERIFY_EMAIL"];
```

Localizar a linha
`    // ... demais operações seguem o mesmo padrão: consultar, criar se necessário, traduzir erros.` e substituí-la por:

```csharp
    // v2.6: os cinco passos do convite, cada um idempotente. Nenhuma exceção leva o e-mail; todas levam o tenantId.
    public async Task<ExternalUserId> EnsureInvitedUserAsync(
        string organizationId, TenantId tenantId, InviteData invite, CancellationToken ct)
    {
        string email = invite.Email.Value;
        string tenant = tenantId.Value.ToString();

        // 1. Buscar: exact=true (sem ele, LIKE %x%) e briefRepresentation=false (explícito; a forma resumida não traz
        //    atributos). O e-mail vai escapado na query.
        IReadOnlyList<UserRepresentation> achados = await admin.FindUsersByEmailAsync(email, ct);
        UserRepresentation? user = achados.FirstOrDefault(u => PertenceAo(u, tenant));
        if (achados.Count > 0 && user is null)
            throw new IdentityProviderInconsistencyException(
                $"O e-mail do convite do tenant {tenantId.Value} pertence a um usuário não correlacionado.");

        // 2. Criar, se não achou: habilitado, com as duas ações e o tenant_id.
        if (user is null)
        {
            UserRepresentation novo = new(null, email, email, Enabled: true, [.. InviteActions],
                new() { [UserTenantAttribute] = [tenant] });
            try
            {
                user = novo with { Id = await admin.CreateUserAsync(novo, ct) };
            }
            catch (KeycloakConflictException)
            {
                // UMA nova busca, por e-mail e por username. Nunca em laço: o 409 também sai quando outra conta tem
                // username igual ao nosso e-mail e outro e-mail.
                user = (await admin.FindUsersByEmailAsync(email, ct))
                    .Concat(await admin.FindUsersByUsernameAsync(email, ct))
                    .FirstOrDefault(u => PertenceAo(u, tenant))
                    ?? throw new IdentityProviderInconsistencyException(
                        $"O e-mail do convite do tenant {tenantId.Value} pertence a um usuário não correlacionado.");
            }
        }

        // 3. Vincular à Organization. O 409 (já é membro) conta como sucesso.
        await admin.AddOrganizationMemberAsync(organizationId, user.Id!, ct);

        // 4. Papel: o id vem das listas do próprio usuário (role-mappings/realm e .../available); GET /roles/{nome}
        //    exigiria view-realm. Ausente das duas listas: o realm não é o que a Gateway espera (permanente).
        if (!(await admin.GetUserRealmRolesAsync(user.Id!, ct)).Any(p => p.Name == invite.Role.Value))
        {
            RoleRepresentation papel = (await admin.GetAvailableUserRealmRolesAsync(user.Id!, ct))
                .FirstOrDefault(p => p.Name == invite.Role.Value)
                ?? throw new IdentityProviderInconsistencyException(
                    $"O papel de realm '{invite.Role.Value}' do convite do tenant {tenantId.Value} não existe no realm.");
            await admin.AddUserRealmRolesAsync(user.Id!, [papel], ct);
        }

        // 5. E-mail só se o convite ainda não foi aceito: um envio depois do aceite mandaria ao admin ativo um link
        //    que troca a senha dele. 400 (usuário desabilitado à mão) é permanente. O PUT fica fora do retry.
        if (user.RequiredActions?.Contains("UPDATE_PASSWORD") == true)
        {
            try
            {
                await admin.ExecuteActionsEmailAsync(
                    user.Id!, InviteActions, (int)invite.LinkLifetime.TotalSeconds, ct);
            }
            catch (KeycloakBadRequestException excecao)
            {
                throw new IdentityProviderInconsistencyException(
                    $"O Keycloak recusou o envio do convite do tenant {tenantId.Value}.", excecao);
            }
        }

        return ExternalUserId.From(user.Id!);
    }

    // Igual, e não "existe": o tenant_id de outro tenant não é tentativa anterior deste.
    private static bool PertenceAo(UserRepresentation user, string tenant) =>
        user.Attributes?.GetValueOrDefault(UserTenantAttribute) is [var valor] && valor == tenant;
    // ... demais operações seguem o mesmo padrão: consultar, criar se necessário, traduzir erros.
```

Localizar a linha `    // As demais operações entram com as fatias que as usarem.` (no `KeycloakAdminClient`) e substituí-la por:

```csharp
    /// <summary>Usuários com o e-mail exato, com atributos (v2.6).</summary>
    /// <remarks>
    /// exact=true compara por igualdade — sem ele, pre.x@acme.test casaria com x@acme.test. briefRepresentation=false
    /// traz os atributos. O e-mail vai escapado, porque o + viraria espaço. Esta URL nunca vai a log nem a exceção.
    /// FindUsersByUsernameAsync é igual, com username= no lugar de email=.
    /// </remarks>
    public async Task<IReadOnlyList<UserRepresentation>> FindUsersByEmailAsync(string email, CancellationToken ct) =>
        await http.GetFromJsonAsync<List<UserRepresentation>>(
            $"admin/realms/{_realm}/users?email={Uri.EscapeDataString(email)}&exact=true&briefRepresentation=false", ct)
        ?? [];

    public async Task ExecuteActionsEmailAsync(
        string userId, IReadOnlyList<string> actions, int lifespanSeconds, CancellationToken ct)
    {
        // lifespan em segundos, por chamada; sem ele, vale o padrão do realm (12 h).
        using var response = await http.PutAsJsonAsync(
            $"admin/realms/{_realm}/users/{userId}/execute-actions-email?lifespan={lifespanSeconds}", actions, ct);

        // 400 ("User is disabled", "User email missing"): o adaptador traduz em inconsistência. Decide pelo status.
        if (response.StatusCode == HttpStatusCode.BadRequest)
            throw new KeycloakBadRequestException("O Keycloak respondeu 400 ao enviar o e-mail de ações.");

        response.EnsureSuccessStatusCode();   // 500 (SMTP fora do ar): HttpRequestException, transitória
    }

    // CreateUserAsync (409 → KeycloakConflictException), AddOrganizationMemberAsync (409 = sucesso),
    // GetUserRealmRolesAsync, GetAvailableUserRealmRolesAsync e AddUserRealmRolesAsync seguem o mesmo padrão.
    // As demais operações entram com as fatias que as usarem.
```

Localizar o parágrafo que começa com `**Classes de erro que saem do adaptador.** Nada é engolido` e inserir **antes**
dele, separado por uma linha em branco antes e outra depois:

```markdown
**Os cinco passos de `EnsureInvitedUserAsync` (v2.6)**, verificados no código do Keycloak 26.7.4:

1. **Buscar** por `GET /users?email=<escapado>&exact=true&briefRepresentation=false`. Achou com o `tenant_id` deste tenant: é o nosso, segue para o passo 3. Achou sem esse valor: `IdentityProviderInconsistencyException`.
2. **Criar** com `POST /users`: username igual ao e-mail, o e-mail, `enabled=true`, `requiredActions=[UPDATE_PASSWORD, VERIFY_EMAIL]` e `attributes.tenant_id`; o id vem do `Location` do `201`. Ações desconhecidas são ignoradas em silêncio, e atributo não declarado no User Profile é descartado em silêncio — por isso os testes leem o usuário cru pelo master. No `409`, **uma** nova busca, por e-mail e por `username=<e-mail>&exact=true`: com o nosso `tenant_id`, segue; senão, inconsistência.
3. **Vincular** com `POST /organizations/{id}/members`. O `409` (já é membro) conta como sucesso. Exige `manage-organizations` **e** `manage-users`.
4. **Atribuir o papel**, que exige o id no corpo: `GET /users/{id}/role-mappings/realm`; se o papel não estiver lá, o id vem de `.../realm/available` e o `POST` leva `{id, name}`. Papel ausente das duas listas: inconsistência. Reatribuir é inofensivo.
5. **Enviar o e-mail** com `PUT /users/{id}/execute-actions-email?lifespan=<segundos>`, só se o usuário ainda tiver `UPDATE_PASSWORD` pendente. O link sai com a URL de frontend (`KC_HOSTNAME`, §15), leva ao client `account` e abre primeiro uma página de confirmação numa sessão nova, o que neutraliza a pré-busca de scanners de e-mail.
```

Na tabela de classes de erro, localizar a linha
`| 404 "Organizations not enabled for this realm" | erro de configuração do realm; o smoke test da §15 pega |`,
mantê-la, e inserir logo depois dela:

```markdown
| E-mail em uso por conta sem o nosso `tenant_id`, ou `409` no `POST /users` sem conta nossa na nova busca (v2.6) | `IdentityProviderInconsistencyException` — **permanente** |
| `400 User is disabled` no `execute-actions-email`: o nosso usuário foi desabilitado à mão (v2.6) | `IdentityProviderInconsistencyException` — **permanente** |
| Papel ausente das duas listas do usuário: o realm não é o que a Gateway espera (v2.6) | `IdentityProviderInconsistencyException` — **permanente** |
| `500` no `execute-actions-email` (SMTP fora do ar) ou timeout do `PUT` (v2.6) | `HttpRequestException` / `TimeoutRejectedException` — **transiente**. O `PUT` não tem retry automático, e timeout **não** significa "não enviado": o Keycloak envia dentro da requisição |
| `409` no vínculo à Organization (v2.6) | sucesso: o usuário já é membro |
```

- [ ] **Passo 15: §11.9 — `IMemberRepository` só com `Add` nesta fatia**

Na v2.6, §11.9, localizar estas linhas do bloco de código:

```csharp
/// <summary>
/// Não existe GetAsync(MemberId). A ausência da sobrecarga é o que torna a regra
/// da seção 6.4 verificável: não há como escrever o acesso inseguro por engano.
/// </summary>
public interface IMemberRepository
{
    Task<Member?> GetAsync(TenantId tenantId, MemberId memberId, CancellationToken ct);
    Task<IReadOnlyList<Member>> ListAsync(TenantId tenantId, CancellationToken ct);
}
```

e substituí-las por:

```csharp
/// <summary>
/// Não existe GetAsync(MemberId). A ausência da sobrecarga é o que torna a regra
/// da seção 6.4 verificável: não há como escrever o acesso inseguro por engano.
/// </summary>
/// <remarks>
/// v2.6 (fatia C): só Add, porque o Member carrega o TenantId e nenhum caso de uso ainda lê membros.
/// GetAsync(TenantId, MemberId), ListAsync(TenantId) e o teste que proíbe a sobrecarga só por id chegam no M2.
/// </remarks>
public interface IMemberRepository
{
    void Add(Member member);

    // M2:
    // Task<Member?> GetAsync(TenantId tenantId, MemberId memberId, CancellationToken ct);
    // Task<IReadOnlyList<Member>> ListAsync(TenantId tenantId, CancellationToken ct);
}
```

- [ ] **Passo 16: §12.2 — o `tenant_id` gravado no convite, também chave de correlação, declarado no User Profile**

Na v2.6, §12.2, tabela do mapper, localizar a linha
``| User Attribute | `tenant_id` | Gravado pela Gateway no provisionamento |`` e substituí-la por:

```markdown
| User Attribute | `tenant_id` | Gravado pela Gateway no convite (§11.6) e declarado no User Profile do realm (v2.6) |
```

Localizar a linha `1. no provisionamento do membro (§9.1 e convite), junto da criação do usuário;` e substituí-la por:

```markdown
1. no convite do membro, junto da criação do usuário — inclusive no do admin inicial, dentro do provisionamento (§9.1, §11.6). **Na v2.6, o atributo também é a chave de correlação do convite:** um usuário com o `tenant_id` deste tenant é uma tentativa anterior e é reaproveitado, e um e-mail em uso por conta sem esse valor é falha permanente. Um segundo atributo só para correlação duplicaria este, que precisa ser gravado de qualquer forma;
```

Localizar a linha
`O claim `organization` continua sendo emitido e é útil em auditoria, mas **não é fonte de autorização**.`, mantê-la,
e inserir logo depois dela, separado por uma linha em branco:

```markdown
**Declarado no User Profile, e só para `admin` (v2.6).** Com a `unmanagedAttributePolicy` nula — o padrão, e o que o realm tem —, o Keycloak 26.7.4 **descarta em silêncio** atributo que o User Profile não declare. O realm declara `tenant_id` num componente `declarative-user-profile`, com `view` e `edit` só para `admin`. A `unmanagedAttributePolicy` fica desligada, e `ENABLED` — o conserto que qualquer busca sugere — é proibido: com ele, o próprio usuário editaria o `tenant_id` pela account console e sequestraria a correlação e o isolamento. Uma regra do `RegrasDoRealmTests` confere o JSON do realm, e um teste de integração prova que um usuário comum não altera o atributo pela Account REST API. O atributo da Organization continua `gateway_tenant_id` (§11.6).
```

- [ ] **Passo 17: §13 — o mailpit nos testes; papéis efetivos do service account; vazamento do e-mail**

Na v2.6, §13, localizar a última linha da tabela,
``| Contrato | Documento OpenAPI gerado comparado com a versão aprovada, o que evita *breaking changes* acidentais | Snapshot do documento |``,
mantê-la, e inserir logo depois dela:

```markdown
| Convite contra o Keycloak real (v2.6) | O `KeycloakFixture` sobe o Keycloak com um **mailpit** numa rede Testcontainers e `KC_HOSTNAME=http://keycloak.test:8081` com o backchannel dinâmico, e toda composição de teste passa o `PublicBaseUrl` correspondente: **todo** teste de Keycloak exercita a separação entre emissor público e transporte (§10.2). Leituras cruas pelo master (atributo `tenant_id`, `enabled`, as duas ações, o vínculo, o papel), idempotência, retomada parcial, as inconsistências da §11.6 (inclusive `pre.{x}` para o `exact=true`), o `409` sem laço, o link lido no mailpit (`exp − iat ≈ LinkLifetime`) e aberto. E-mails de teste únicos e com `+`, e o mailpit compartilhado sempre filtrado por destinatário | Testcontainers (Keycloak, mailpit), `HttpClient` cru |
| Papéis efetivos do service account (v2.6) | Os papéis **efetivos**, com compostos expandidos, são exatamente `manage-organizations` e `manage-users`, sem depender de ordem; ausência de `impersonation`, `realm-admin`, `manage-realm`, `manage-clients` e `manage-identity-providers`; `403` em `GET .../clients` e no `PUT` do realm. Um token sem `manage-users` deixa o health check em `503` | Testcontainers |
| Vazamento do e-mail (v2.6) | Composição real com todas as categorias em `Trace`, o log de dados sensíveis do EF ligado e um exporter OpenTelemetry em memória; caminho feliz e falhas injetadas (inconsistência, `409`, `400 disabled`, `500`, falha no commit). Exige que houve registros das categorias do `HttpClient` com `users` — o canal foi capturado — e que nenhum log, span, `outbox_messages.error` nem Problem Details contém o e-mail. Na Application, um logger de captura passa por todos os ramos | Testcontainers, OpenTelemetry InMemory exporter |
| Convite ponta a ponta e atomicidade (v2.6) | Postgres, Keycloak e mailpit: `POST` → `Active`, com o `Member` gravado, `initial_admin_email` nulo e o e-mail no mailpit. **Commit perdido:** o segundo ciclo termina com um usuário, uma linha em `members` e **exatamente dois** e-mails para o destinatário — o duplo envio declarado na §11.5. **Atomicidade:** uma linha pré-inserida em `members` com o mesmo `(tenant_id, sub)` faz falhar o último comando do commit, e o tenant continua `Pending`, com o e-mail e sem `tenant-activated` no Outbox | Testcontainers |
```

- [ ] **Passo 18: §15 — mailpit, `KC_HOSTNAME`, SMTP por ambiente, bootstrap novo, `IGNORE_EXISTING`, CI, portas**

Na v2.6, §15, substituir seis linhas da tabela de serviços. Para cada par abaixo, localizar a primeira linha (a
linha inteira da tabela) e trocá-la pela segunda:

```markdown
| `keycloak` | Versão **26.7.4** fixada; importa `keycloak/bootstrap/realm-identity-gateway.json` na primeira subida; porta publicada só em `127.0.0.1` |
```

por

```markdown
| `keycloak` | Versão **26.7.4** fixada; importa `keycloak/bootstrap/realm-identity-gateway.json` na primeira subida; porta publicada só em `127.0.0.1`; `KC_HOSTNAME=http://localhost:8081` com `KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true`, e o SMTP por ambiente (`SMTP_HOST=mailpit`, `SMTP_PORT=1025`, `SMTP_FROM=convites@identity-gateway.local`, conferidos por `test -n` no entrypoint); sobe depois do `mailpit` saudável (v2.6) |
```

---

```markdown
| `postgres` | Um servidor com dois bancos: `keycloak` e `identitygateway` |
```

por

```markdown
| `postgres` | Um servidor com dois bancos: `keycloak` e `identitygateway`; porta publicada só em `127.0.0.1` (v2.6: agora há dado pessoal no banco) |
```

---

```markdown
| `mailpit` | Captura os e-mails de convite e de ações obrigatórias do Keycloak |
```

por

```markdown
| `mailpit` | `axllent/mailpit:v1.31.3` fixado (release de 2026-09-27): captura os e-mails de convite e de ações obrigatórias do Keycloak. Interface e API só em `127.0.0.1:8025`; o SMTP (1025) só na rede interna; healthcheck `["CMD", "/mailpit", "readyz"]`, o da própria imagem. **Existe desde a v2.6** — a v2.5 o listava sem ele estar no compose |
```

---

```markdown
| `redis` | L2 do `HybridCache` — permissões efetivas do §9.6 |
```

por

```markdown
| `redis` | L2 do `HybridCache` — permissões efetivas do §9.6; porta publicada só em `127.0.0.1` (v2.6) |
```

---

```markdown
| `seq` | Logs estruturados do Serilog, com interface de consulta |
```

por

```markdown
| `seq` | Logs estruturados do Serilog, com interface de consulta; porta publicada só em `127.0.0.1` (v2.6: em falha, os logs podem carregar dado pessoal) |
```

---

```markdown
| `identity-gateway-api` | A API |
```

por

```markdown
| `identity-gateway-api` | A API, com `Keycloak__Admin__PublicBaseUrl=http://localhost:8081`, usado só no `aud` do assertion (§10.2, v2.6) |
```

Localizar a linha `**Organizations precisa estar ligado no realm.**` (início de parágrafo) e inserir **antes** dela, separados
por linhas em branco, estes dois parágrafos:

```markdown
**O que a fatia C acrescentou ao bootstrap (v2.6):** o papel de realm `tenant-admin`, o primeiro do catálogo a existir; `manage-users` no service account, ao lado de `manage-organizations` (§10.2); um componente de User Profile (`org.keycloak.userprofile.UserProfileProvider`, provider `declarative-user-profile`) que declara o atributo `tenant_id` só para `admin`, sem `unmanagedAttributePolicy` (§12.2) — o texto da configuração não usa `${...}`, que o import substituiria; o `smtpServer` com placeholders puros (`${SMTP_HOST}`, `${SMTP_PORT}`, `${SMTP_FROM}`, sem o prefixo `KC_`, que o Keycloak leria como opção dele), `auth`, `ssl` e `starttls` como `"false"` literais e os timeouts `connectionTimeout` 2000, `timeout` 3000 e `writeTimeout` 3000 (milissegundos), abaixo do `AttemptTimeout` de 10 s da Gateway — chaves conferidas em `DefaultEmailSenderProvider.java` L149-151 da 26.7.4; `resetPasswordAllowed: false`; e `adminEventsEnabled: true`. O `RegrasDoRealmTests` aceita `components` só com o provider de User Profile — provedores de chave continuam proibidos, que era o motivo da regra — e exige o atributo declarado só com `admin`, nenhuma `unmanagedAttributePolicy`, `resetPasswordAllowed` falso e o service account com exatamente os dois papéis.

**Endereço público do Keycloak (v2.6).** Sem `KC_HOSTNAME`, o link do e-mail sairia com o host interno (`http://keycloak:8080`), e trocar o host à mão não funciona, porque o clique é validado contra o emissor gravado no token. Com `KC_HOSTNAME=http://localhost:8081` e `KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true`, o link, o `iss` e o discovery usam `localhost:8081`, e a Admin API continua respondendo por `keycloak:8080`, sem redirecionar. A API recebe `Keycloak__Admin__PublicBaseUrl=http://localhost:8081`, que alimenta só o `aud` do assertion (§10.2). A porta `8081` aparece em três lugares — a porta publicada, o `KC_HOSTNAME` e o `PublicBaseUrl` —, e um teste de arquitetura lê o `docker-compose.yml` e exige que os dois últimos sejam iguais. O SMTP vem do ambiente: plugar outro servidor, ou o notification-hub quando ele aceitar SMTP, é trocar configuração, sem código na Gateway.
```

No item "Chave e realm andam juntos", localizar
``e o README manda `docker compose down -v`, que apaga os dois volumes juntos.`` e substituir por:

```markdown
e o README manda `docker compose down -v`, que apaga os dois volumes juntos. **O `IGNORE_EXISTING` vale também para o que a v2.6 acrescentou** (v2.6): o papel, o `manage-users`, o User Profile e o SMTP só entram no primeiro import, e um volume antigo fica sem eles. Para isso falhar alto, e não virar 24 h de retry, o `KeycloakHealthCheck` confere que o token do service account traz `manage-users` em `resource_access.realm-management.roles`; sem ele, o `ready` responde 503 com uma mensagem que manda rodar `docker compose down -v`.
```

No parágrafo "O compose sobe na CI", localizar
`A promessa "funciona na primeira tentativa" do M0 passa a ser verificada a cada PR, não só no dia em que alguém a testou à mão.`
e substituir por:

```markdown
A promessa "funciona na primeira tentativa" do M0 passa a ser verificada a cada PR, não só no dia em que alguém a testou à mão. **Desde a v2.6, o job também confere o convite:** depois do `Active`, consulta o mailpit (`GET /api/v1/search?query=to:"<e-mail>"`, 10 × 2 s, exigindo ao menos uma mensagem, nunca exatamente uma), lê o campo `Text` da mensagem (`GET /api/v1/message/{ID}`, com `jq`, porque o JSON escapa `&` como `&` e o HTML traz `&amp;`), exige o prefixo `http://localhost:8081/realms/identity-gateway/login-actions/action-token?key=` e faz um `GET` no link, exigindo `200` sem a página de erro nem a de expirado — o que prova que o link abre, e não só o formato dele. O e-mail e o slug são únicos por execução (`admin+<timestamp>@acme.test`), porque e-mail em uso por outra conta é falha permanente (§9.1); em falha, o job grava também a listagem do mailpit.
```

- [ ] **Passo 19: §16 — andamento: fatia C entregue; o catálogo de papéis ganha o `tenant-admin`**

Na v2.6, §16, localizar `**Andamento do M0 e do M1 (v2.5).**` e substituir por:

```markdown
**Andamento do M0 e do M1 (v2.6).**
```

Localizar a linha `| **C · Convite do admin inicial** | M1 | Próxima |` e substituí-la por:

```markdown
| **C · Convite do admin inicial**: `EnsureInvitedUserAsync`, o `Member` mínimo, a vaga do admin na ativação, o e-mail pelo SMTP do Keycloak e o `mailpit` no compose | M1 | Entregue |
```

O número do PR entra quando o PR for aberto — não inventar.

No parágrafo "Pendente do M0 depois da fatia A", localizar `Audience Mapper, catálogo de papéis, armazenamento de eventos do realm,`
e substituir por:

```markdown
Audience Mapper, catálogo de papéis (o `tenant-admin` existe desde a fatia C; faltam `platform-admin`, `financial-manager` e `reader`), armazenamento de eventos do realm,
```

No mesmo parágrafo, localizar
``o critério "primeiro `curl`" do M0 **com token do Keycloak** segue em aberto.`` e substituir por:

```markdown
o critério "primeiro `curl`" do M0 **com token do Keycloak** segue em aberto.

**Pendente do M1 depois da fatia C (v2.6):** suspensão, encerramento, reconciliação, retry manual de `ProvisioningFailed` e downgrade de plano. O ciclo completo do convite (expiração, reenvio, cancelamento, `POST /members`) é o M2, e a fatia C deixa prontos para ele o `Member`, `EnsureInvitedUserAsync` com o papel na porta e a `IInvitationPolicy`.
```

- [ ] **Passo 20: §19 — os limites da fatia C**

Na v2.6, §19, localizar o último item, que começa com
`- **Os números do Outbox estão dimensionados para o provisionamento em processo**` (uma linha só), mantê-lo, e
inserir logo depois dele:

```markdown
- **O e-mail de convite pode sair mais de uma vez** (v2.6). Qualquer falha entre o envio pelo Keycloak e o commit — commit perdido, conflito de `xmin`, violação do índice de `members` — faz a mensagem voltar, e o convite é reenviado (§11.5). Com duas réplicas processando a mesma mensagem em paralelo (item acima), também saem dois e-mails.
- **Um link de convite duplicado continua válido depois do aceite** (v2.6). Cada link é de uso único, mas os outros emitidos para o mesmo usuário valem até expirar, e o Keycloak não os revoga por usuário: um link duplicado permite trocar a senha do admin por até 7 dias. Não reenviar depois do aceite (§11.6, passo 5) não invalida um duplicado enviado antes.
- **O `Member` do admin inicial fica `Invited` até a sincronização do ADR-007** (M4). A expiração de convite do M2 **não pode chegar antes dela** sem outra forma de saber do aceite, senão expira um admin que já entrou (§9.9).
- **Admin órfão** (v2.6): se o e-mail sai e a janela de provisionamento esgota antes do commit, o tenant fica `ProvisioningFailed` com um usuário habilitado, com `tenant-admin` e com link válido. O retry manual e a reconciliação precisam tratar esse caso, desabilitando o usuário ou reaproveitando-o.
- **Tenants registrados antes da v2.6 vão para `ProvisioningFailed`**, porque não têm o e-mail guardado, e só saem pelo retry manual, informando o e-mail.
- **E-mail digitado errado entrega o tenant a um estranho**, e não há revogação pela API: o platform-admin não opera rotas de membro (§10.1). O runbook provisório é desabilitar o usuário no Keycloak; uma operação de plataforma para trocar ou reenviar o convite do admin inicial fica para o M1/M2 (§8).
- **A mesma pessoa não administra dois tenants**: o segundo `POST /tenants` com o mesmo e-mail termina em `ProvisioningFailed`. É consequência do ADR-009 e da unicidade de e-mail no realm.
- **SMTP, papel `tenant-admin`, User Profile e permissões do realm só entram no primeiro import** (`IGNORE_EXISTING`, §15). Um volume anterior à v2.6 exige `docker compose down -v`; o `ready` avisa, mas não corrige.
- **O e-mail apagado não some de imediato do disco.** O apagamento é lógico: WAL, *dead tuples* e backups guardam o valor pela retenção deles (§10.3).
- **Alcance do `manage-users`** (v2.6): o service account atribui qualquer papel que não seja de administração, inclusive `platform-admin`, e os de administração que ele mesmo tem; troca senhas e desabilita usuários, inclusive platform-admins. Quem tiver a chave da Gateway pode criar uma conta própria com esses papéis, e o acesso **sobrevive à rotação da chave** (§10.2).
- **O convite não sai pelo notification-hub** (v2.6). O Keycloak só envia e-mail por SMTP, trocar o transporte exige um provider num SPI interno, e o hub só recebe HTTP. A integração depende de o hub aceitar SMTP ou de uma extensão Java no Keycloak; como o SMTP vem do ambiente, plugar o hub depois é trocar configuração, sem código na Gateway.
- **No M4, a conta convidada sem senha é alvo de vínculo automático no primeiro login federado.** Nunca ligar vínculo de conta sem verificação.
- **Um membro existente pode tomar de antemão o e-mail do futuro admin**, se a troca de e-mail no Keycloak não exigir verificação, e a correlação por `tenant_id` derruba o provisionamento (§9.1). Se a troca de e-mail vem ligada por padrão na 26.7.4 não foi verificado.
- **Quem perde a corrida de slug no `POST /tenants` recebe `500`, e não `409`** (herdado da vertical de registro): o índice único fecha a janela entre a checagem e o `INSERT`, mas a violação ainda não é traduzida em `409`.
```

- [ ] **Passo 21: Conferir a v2.6 contra termos desatualizados**

Run: `grep -n "MarkProvisioned" docs/especificacao-arquitetural-v2.6.md`
Expected: só três ocorrências, todas intencionais — o item C2 da §0 ("substitui `MarkProvisioned`"), o comentário
do `summary` de `CompleteProvisioning` ("Substitui o MarkProvisioned da v2.5") e o comentário "o inverso do
MarkProvisioned da v2.5" no corpo do método (§11.1). Nenhuma definição de método `MarkProvisioned` e nenhuma chamada
`tenant.MarkProvisioned(`.

Run: `grep -n "nasce desabilitado\|Keycloak desabilitado\|Não há sessões a revogar\|precisa ser igual ao\|Duas decisões pendentes" docs/especificacao-arquitetural-v2.6.md`
Expected: nenhuma linha. (A v2.5 tinha uma de cada; as erratas usam "nascia desabilitado", "não havia sessão a
revogar" e "precisava ser igual".)

Run: `grep -n "somente\*\* o papel\|InviteData data\|string PlanCode) : ICommand\|slug.Value, plan);\|LGPD:\*\* dados pessoais somente no Keycloak; a Gateway guarda vínculos e governança\.$\|Andamento do M0 e do M1 (v2.5)\|| Próxima |" docs/especificacao-arquitetural-v2.6.md`
Expected: nenhuma linha. (Cobre "só `manage-organizations`" no service account, a assinatura antiga da porta, o
command antigo, o `Register` antigo, o item LGPD sem a exceção, a marca "(v2.5)" do andamento e a fatia C como
"Próxima".)

Run: `grep -n "desabilitad" docs/especificacao-arquitetural-v2.6.md | grep -i "convid\|convite\|nasc"`
Expected: só linhas que dizem que o convidado nasce **habilitado** ou citam a errata ("A v2.5 dizia que o usuário
nascia desabilitado", "porque o usuário nascia desabilitado", "A §9.9 dizia que ele nascia desabilitado") e as que
dizem que a expiração ou o cancelamento **desabilitam** o usuário.

Run: `grep -c "(v2.6)" docs/especificacao-arquitetural-v2.6.md`
Expected: 30 ou mais (cada mudança marcada).

Run: `grep -n "^## 0" docs/especificacao-arquitetural-v2.6.md`
Expected, nesta ordem: `## 0. O que mudou da v2.5 para a v2.6`, `## 0.1. ... v2.4 para a v2.5`,
`## 0.2. ... v2.3 para a v2.4`, `## 0.3. ... v2.2 para a v2.3`, `## 0.4. ... v2.1 para a v2.2`,
`## 0.5. ... v2.0 para a v2.1`.

Run: `grep -n "v2.5" docs/especificacao-arquitetural-v2.6.md | head -40`
Expected: só menções históricas — a seção "O que mudou da v2.4 para a v2.5", marcas "(v2.5)" em texto e as
erratas da v2.6 que citam o que a v2.5 dizia. Nenhum link para `especificacao-arquitetural-v2.5.md`.

Se algum grep mostrar sobra, corrigir com o texto da v2.6 deste roteiro e repetir.

- [ ] **Passo 22: Documento de negócio alinhado à v2.6**

Arquivo: `docs/documentacao-negocio.md`. Os números de linha são da versão atual da branch e servem só de
referência; o `Edit` casa pelo texto. Os blocos Mermaid seguem o estilo do documento: texto entre aspas sem
acento.

1. **Cabeçalho, versão** (linha 3). Localizar `> **Versão:** 1.2 · **Data:** 2026-09-24` e substituir por (com a data
   do dia, `date +%F`):

   ```markdown
   > **Versão:** 1.3 · **Data:** AAAA-MM-DD
   ```

2. **Cabeçalho, fonte da verdade** (linha 4). Localizar
   ``> **Fonte da verdade:** [`especificacao-arquitetural-v2.4.md`](especificacao-arquitetural-v2.4.md)`` e substituir por:

   ```markdown
   > **Fonte da verdade:** [`especificacao-arquitetural-v2.6.md`](especificacao-arquitetural-v2.6.md)
   ```

3. **Cabeçalho, estado** (linha 5). Localizar
   `> **Estado do projeto:** implementação em andamento — registro de tenant entregue; fundação Keycloak em curso.` e
   substituir por:

   ```markdown
   > **Estado do projeto:** implementação em andamento — registro de tenant, fundação Keycloak, consumidor do provisionamento e convite do admin inicial entregues.
   ```

4. **Nota da versão 1.3** (antes da linha 7). Localizar `> **Nota da versão 1.2.** Este documento foi derivado da v2.2.`
   e inserir **antes** dessa linha:

   ```markdown
   > **Nota da versão 1.3.** Alinha à v2.6 os trechos que a fatia C (convite do admin inicial) tornou falsos: o
   > convidado nasce **habilitado**, não desabilitado; o e-mail do administrador inicial fica **temporariamente** no
   > banco da Gateway, até a ativação ou a falha do provisionamento (RN-019); o terceiro passo do provisionamento
   > ativa, reserva a vaga e registra o membro num commit só; o cancelamento de convite **revoga sessões**; e o prazo
   > do link de ações é definido pela Gateway e passado ao Keycloak em cada envio. As citações `§N` continuam
   > válidas.
   >
   ```

5. **Rastreabilidade** (linha 17). Localizar `remete a uma seção da spec vigente (v2.4),` e substituir por:

   ```markdown
   remete a uma seção da spec vigente (v2.6),
   ```

6. **Conformidade** (linha 67). Localizar
   `**Conformidade:** a Gateway guarda vínculos e governança, não dados pessoais — esses ficam no Keycloak —` e
   substituir por:

   ```markdown
   **Conformidade:** a Gateway guarda vínculos e governança, não dados pessoais — esses ficam no Keycloak, com uma exceção temporária e declarada: o e-mail do administrador inicial, apagado quando o tenant é ativado ou falha (RN-019) —
   ```

7. **Registro de tenant, tabela de funcionalidades** (linha 257). Localizar
   ``O provisionamento executa três passos idempotentes — garante a Organization, garante o convite do admin como `tenant-admin`, marca `Active` — e o tenant **nasce operável por construção** (§8, §9.1)``
   e substituir por:

   ```markdown
   O provisionamento garante a Organization, convida o admin como `tenant-admin` — o Keycloak envia o e-mail — e só então, num commit só, marca `Active`, reserva a vaga do admin e registra o membro; o tenant **nasce operável por construção** (§8, §9.1)
   ```

8. **F-01, regras de negócio (o passo 3)** (linha 516). Localizar
   ``em três passos: garantir a Organization (com o atributo `gateway_tenant_id`), garantir o convite do admin inicial já com o papel `tenant-admin`, e só então marcar `Active` (§9.1).``
   e substituir por:

   ```markdown
   em três passos: garantir a Organization (com o atributo `gateway_tenant_id`), garantir o convite do admin inicial já com o papel `tenant-admin` — o Keycloak cria o usuário e envia o e-mail —, e só então, num commit só, marcar `Active`, reservar a vaga do admin e registrar o membro em `Invited` (§9.1). Entre o `POST` e a ativação, o `initialAdminEmail` fica guardado no tenant, fora do evento, e é apagado quando o tenant é ativado ou falha (RN-019). Sem vaga livre no plano, o provisionamento falha antes de tocar o Keycloak.
   ```

9. **F-01, erros de negócio** (linha 518). Localizar
   ``Se os retries do provisionamento se esgotarem, o tenant vai para `ProvisioningFailed` e o evento fica disponível para retry manual — o registro **não** é desfeito.``
   e substituir por:

   ```markdown
   Se os retries do provisionamento se esgotarem, o tenant vai para `ProvisioningFailed` e o evento fica disponível para retry manual — o registro **não** é desfeito. Um `initialAdminEmail` já em uso por outra conta do Keycloak também leva a `ProvisioningFailed`: a mesma pessoa não administra dois tenants (ADR-009). O retry manual, quando existir, recebe o e-mail de novo, o que também corrige um e-mail digitado errado (§9.1).
   ```

10. **F-07, pós-condições (usuário "desabilitado")** (linha 602). Localizar
    ``usuário garantido no Keycloak (desabilitado, com as *required actions*) e vinculado à Organization`` e substituir por:

    ```markdown
    usuário garantido no Keycloak (**habilitado e sem senha**, com as *required actions* — a senha só existe depois que a pessoa a define pelo link) e vinculado à Organization
    ```

11. **F-09, regras de negócio** (linha 627). Localizar `O usuário é desabilitado no Keycloak. A liberação da vaga` e
    substituir por:

    ```markdown
    O usuário é desabilitado no Keycloak **e suas sessões são revogadas**: ele nasce habilitado e pode já ter definido a senha pelo link antes de o aceite chegar à Gateway (§9.9). A liberação da vaga
    ```

12. **F-09, pós-condições** (linha 628). Localizar
    ``| **Pós-condições** | Membro em `Revoked`; usuário desabilitado no Keycloak; vaga liberada exatamente uma vez. |``
    e substituir por:

    ```markdown
    | **Pós-condições** | Membro em `Revoked`; usuário desabilitado no Keycloak e sem sessões; vaga liberada exatamente uma vez. |
    ```

13. **F-10, pré-condições (o prazo do link)** (linha 639). Localizar
    ``e deve ser alinhado ao tempo de vida do link de ações do Keycloak — um link ainda válido para um membro já `Expired` produziria um aceite sem vaga reservada (§9.9).``
    e substituir por:

    ```markdown
    e o tempo de vida do link de ações é definido pela própria Gateway — uma política de convite, com padrão de 7 dias — e passado ao Keycloak em cada envio, alinhado a esse prazo. Como o usuário nasce habilitado, a expiração também o **desabilita** no Keycloak: um link ainda válido para um membro já `Expired` produziria um aceite sem vaga reservada (§9.9).
    ```

14. **F-10, pós-condições** (linha 641). Localizar
    ``| **Pós-condições** | Membro em `Expired`; vaga liberada; evento `MemberInviteExpired`. |`` e substituir por:

    ```markdown
    | **Pós-condições** | Membro em `Expired`; usuário desabilitado no Keycloak; vaga liberada; evento `MemberInviteExpired`. |
    ```

15. **Estados do membro, `Expired` e `Revoked`** (linhas 920 e 921). Localizar
    `transicionou o membro ao passar do prazo. A vaga voltou ao plano automaticamente.` e substituir por:

    ```markdown
    transicionou o membro ao passar do prazo e desabilitou o usuário no Keycloak. A vaga voltou ao plano automaticamente.
    ```

    Localizar `O usuário é desabilitado no Keycloak e a vaga volta ao plano.` e substituir por:

    ```markdown
    O usuário é desabilitado no Keycloak, suas sessões são revogadas e a vaga volta ao plano.
    ```

16. **RN-027 (o prazo do link)** (linhas 1051–1052). Localizar as duas linhas

    ```markdown
    prazo deve ser **alinhado ao tempo de vida do link de ações do Keycloak**: um link ainda válido
    para um membro já `Expired` produziria um aceite sem vaga reservada.
    ```

    e substituí-las por:

    ```markdown
    prazo do link de ações é **definido pela Gateway** (política de convite, padrão de 7 dias) e passado ao
    Keycloak em cada envio, alinhado ao prazo do convite; e a expiração desabilita o usuário no Keycloak,
    que nasce habilitado — um link ainda válido para um membro já `Expired` produziria um aceite sem vaga
    reservada.
    ```

17. **RN-019 ("nenhum dado pessoal no banco")** (linhas 1129–1132). Localizar as quatro linhas

    ```markdown
    **RN-019 — Dados pessoais ficam no Keycloak; a Gateway guarda vínculo e governança.**
    Nome, e-mail e telefone não vivem no banco da Gateway, que guarda o identificador do usuário
    (`sub`) e os dados de governança (§6). Isso limita o impacto de um eventual vazamento.
    *Quando violada:* o banco da Gateway entra no escopo mais sensível de compliance.
    ```

    e substituí-las por:

    ```markdown
    **RN-019 — Dados pessoais ficam no Keycloak; a Gateway guarda vínculo e governança.**
    Nome, e-mail e telefone não vivem no banco da Gateway, que guarda o identificador do usuário
    (`sub`) e os dados de governança (§6). Isso limita o impacto de um eventual vazamento.
    **Exceção declarada e limitada** (§6, §10.3): o e-mail do administrador inicial fica guardado no
    tenant entre o `POST /tenants` e o convite, fora do evento, e é apagado na mesma transação que ativa
    o tenant ou que o marca `ProvisioningFailed` — só existe em tenant `Pending`, e nenhuma resposta o
    expõe. O apagamento é lógico: cópias de segurança e registros internos do banco guardam o valor pela
    retenção deles. O e-mail também não vai a log, mensagem de erro nem resposta.
    *Quando violada:* o banco da Gateway entra no escopo mais sensível de compliance.
    ```

18. **Fluxo 9.1, passo 2 do Passo a passo** (linha 1464). Localizar
    `   Nada é enviado ao Keycloak neste momento. Se a transação falhar, não sobra nem tenant nem evento.` e substituir por:

    ```markdown
       Nada é enviado ao Keycloak neste momento. Se a transação falhar, não sobra nem tenant nem evento. O
       `initialAdminEmail` fica numa coluna do tenant, **fora do evento**, e é apagado quando o tenant é ativado
       ou falha (RN-019).
    ```

19. **Fluxo 9.1, diagrama (o passo 3)** (linhas 1452–1454). Localizar as três linhas

    ```text
        CONS->>KC: "Passo 2: garante o convite do admin inicial com papel tenant-admin"
        KC-->>CONS: "convite registrado"
        CONS->>PG: "Passo 3: grava organizationId e marca Tenant como Active"
    ```

    e substituí-las por:

    ```text
        CONS->>KC: "Passo 2: garante o usuario do admin, o vinculo, o papel tenant-admin e o e-mail"
        Note over CONS,KC: "Usuario habilitado, sem senha, correlacionado pelo atributo tenant_id"
        KC-->>CONS: "sub do admin"
        CONS->>PG: "Passo 3, num commit so: Active, vaga do admin, membro Invited, e-mail apagado"
    ```

20. **Fluxo 9.1, passo 4 do Passo a passo (o passo 3)** (linhas 1471–1472). Localizar as duas linhas

    ```markdown
       2. garante o convite do `initialAdminEmail` já com o papel `tenant-admin`;
       3. marca o tenant `Active`.
    ```

    e substituí-las por:

    ```markdown
       2. garante o convite do `initialAdminEmail` já com o papel `tenant-admin`: o Keycloak cria o usuário
          habilitado e sem senha, vincula-o à Organization, atribui o papel e envia o e-mail de ações
          obrigatórias — só se o convite ainda não foi aceito;
       3. num commit só, marca o tenant `Active`, reserva a vaga do admin, registra o membro em `Invited` e
          apaga o e-mail guardado. Antes do passo 1, um tenant sem vaga livre no plano falha sem tocar o
          Keycloak.
    ```

21. **Fluxo 9.9, diagrama (usuário "desabilitado" e "não há sessão a revogar")** (linhas 1954 e 1973). Localizar
    `    GW->>KC: "cria usuario desabilitado com required actions"` e substituir por:

    ```text
        GW->>KC: "cria usuario habilitado e sem senha, com required actions"
    ```

    Localizar `        Note over GW,KC: "Nao ha sessao a revogar, o usuario nunca autenticou"` e substituir por:

    ```text
            GW->>KC: "revoga as sessoes"
            Note over GW,KC: "O usuario nasce habilitado e pode ter definido a senha antes de o aceite chegar"
    ```

22. **Fluxo 9.9, passo 1** (linha 1982). Localizar
    ``   Keycloak **desabilitado**, com as *required actions* `UPDATE_PASSWORD` e `VERIFY_EMAIL`, dispara o e-mail``
    e substituir por:

    ```markdown
       Keycloak **habilitado e sem senha**, com as *required actions* `UPDATE_PASSWORD` e `VERIFY_EMAIL` — nascer
       desabilitado não funciona, porque o Keycloak recusa o envio e o clique de usuário desabilitado (v2.6) —,
       dispara o e-mail
    ```

23. **Fluxo 9.9, passo 2 (nome e sobrenome)** (linha 1985). Localizar
    `2. **O aceite não cria reserva nova.** A pessoa define a senha no Keycloak,` e substituir por:

    ```markdown
    2. **O aceite não cria reserva nova.** A pessoa define a senha e informa nome e sobrenome no Keycloak,
    ```

24. **Fluxo 9.9, passo 4 (o prazo do link)** (linhas 1992–1994). Localizar as três linhas

    ```markdown
    4. **O prazo precisa estar alinhado ao link do Keycloak.** Um link de ações ainda válido para um membro já
       `Expired` produziria um aceite sem vaga reservada — a pessoa entraria por uma porta que a Gateway já
       fechou no contador.
    ```

    e substituí-las por:

    ```markdown
    4. **O prazo do link é da Gateway, e a expiração desabilita o usuário.** A Gateway define o tempo de vida do
       link de ações (política de convite, padrão de 7 dias) e o passa ao Keycloak em cada envio, alinhado ao
       prazo do convite. Como o usuário nasce habilitado, a expiração também o desabilita no Keycloak: senão, um
       link ainda válido para um membro já `Expired` produziria um aceite sem vaga reservada — a pessoa entraria
       por uma porta que a Gateway já fechou no contador.
    ```

25. **Fluxo 9.9, passo 7 (cancelamento)** (linha 2001). Localizar
    ``7. **O cancelamento leva a `Revoked`** (§9.9, I-2), libera a vaga e desabilita o usuário no Keycloak. Só é``
    e substituir por:

    ```markdown
    7. **O cancelamento leva a `Revoked`** (§9.9, I-2), libera a vaga, desabilita o usuário no Keycloak e revoga
       as sessões dele. Só é
    ```

26. **Tabela de infraestrutura, PostgreSQL** (linha 2274). Localizar
    ``| **PostgreSQL** | Banco de governança da Gateway. Guarda vínculos e regras — **nunca** dados pessoais nem senhas (§6, §10.3) |``
    e substituir por:

    ```markdown
    | **PostgreSQL** | Banco de governança da Gateway. Guarda vínculos e regras — **nunca** senhas, e dados pessoais só na exceção temporária do e-mail do administrador inicial, apagado na ativação ou na falha do provisionamento (§6, §10.3, RN-019) |
    ```

27. **"Reduzir o valor do alvo"** (linha 2437). Localizar
    ``A Gateway guarda apenas o identificador do usuário (`sub`) e os dados de governança (§6). Um vazamento do banco da Gateway expõe vínculos e regras, não identidades nem credenciais.``
    e substituir por:

    ```markdown
    A Gateway guarda apenas o identificador do usuário (`sub`) e os dados de governança (§6) — com uma única exceção, o e-mail do administrador inicial, guardado no tenant só enquanto ele está `Pending` e apagado na ativação ou na falha (RN-019). Um vazamento do banco da Gateway expõe vínculos e regras, não identidades nem credenciais.
    ```

28. **Linha evolutiva, diagrama** (linha 2623). Localizar `    E --> F["Especificação v2.4<br/>VIGENTE"]` e substituir por:

    ```text
        E --> F["Especificação v2.4<br/>corrige premissas do Keycloak"]
        F --> G["Especificação v2.5<br/>consumidor do provisionamento"]
        G --> H["Especificação v2.6<br/>VIGENTE"]
    ```

    E, no mesmo bloco, localizar
    `    E -.->|"a implementacao le o codigo<br/>do Keycloak e corrige premissas"| F` e substituir por:

    ```text
        E -.->|"a implementacao le o codigo<br/>do Keycloak e corrige premissas"| F
        G -.->|"a fatia C le o codigo do Keycloak:<br/>o convidado nasce habilitado"| H
    ```

29. **Linha evolutiva, "Como ler os documentos"** (linha 2656). Localizar o parágrafo (uma linha só) que começa com
    `**Como ler os documentos.** A **v2.4 é a fonte da verdade**` e substituí-lo inteiro por:

    ```markdown
    **v2.4 → v2.5 → v2.6: cada fatia devolve à spec o que a execução verificou.** A v2.5 registrou o consumidor do provisionamento: transporte em processo até a fatia do broker e a decisão de desistir no próprio handler. A v2.6 registrou o convite do administrador inicial e duas erratas — o convidado nasce **habilitado**, porque o Keycloak recusa enviar o e-mail e aceitar o clique de um usuário desabilitado, e o destinatário do client assertion é o endereço público do Keycloak, não o de transporte —, além de uma exceção declarada e limitada à regra de dados pessoais (RN-019). **Nenhum ADR foi revogado.**

    **Como ler os documentos.** A **v2.6 é a fonte da verdade** — é a única aprovada para implementação. Da v2.0 à v2.5, as versões são preservadas como estavam, **intocadas**, porque o valor delas agora é mostrar a evolução; e a revisão crítica é o registro do que produziu a v2.1. O documento de origem mostra de onde tudo partiu.
    ```

Conferir:

Run: `grep -n "especificacao-arquitetural-v2.4\|(v2.4),\|Keycloak \*\*desabilitado\*\*\|cria usuario desabilitado\|Nao ha sessao a revogar\|(desabilitado, com as\|nunca\*\* dados pessoais\|3. marca o tenant\|Passo 3: grava organizationId\|v2.4 é a fonte" docs/documentacao-negocio.md`
Expected: nenhuma linha.

Run: `grep -n "v2.6" docs/documentacao-negocio.md`
Expected: o link da fonte da verdade (linha 4), a nota da versão 1.3, a rastreabilidade, o passo 1 do fluxo 9.9,
o diagrama e os dois parágrafos da linha evolutiva.

**Fora do escopo desta tarefa, registrar no handoff:** o documento de negócio ainda descreve o transporte do
provisionamento pelo RabbitMQ e o retry pelo MassTransit (fluxo 9.1: participante `MQ` do diagrama e a linha
"Keycloak (durante os três passos)" da tabela de falhas), que a v2.5 já tinha trocado pelo transporte em processo.
Não é ponto da fatia C; fica como pendência.

- [ ] **Passo 23: README**

1. **Estado do projeto.** Localizar o parágrafo em negrito da linha 17 até o fim do parágrafo que termina com o link
   do handoff da fatia B — isto é, de
   `**M0/M1 em andamento — vertical de registro, fundação Keycloak e consumidor do provisionamento entregues` até
   ``[handoff do consumidor do provisionamento](docs/superpowers/specs/2026-09-26-consumidor-provisionamento-handoff.md).``
   — e substituir o trecho inteiro por (trocar `AAAA-MM-DD` pela data do handoff do Passo 26):

   ```markdown
   **M0/M1 em andamento — vertical de registro, fundação Keycloak, consumidor do provisionamento e convite do
   admin inicial entregues.**

   O repositório parte do template [CleanStart](https://github.com/Joseleno/CleanStart) e já traz a fundação
   funcionando — Clean Architecture em quatro camadas, Outbox transacional, cache de dois níveis, middlewares
   de correlação e segurança, testes de arquitetura e CI. **O domínio do IdentityGateway já existe:** o
   agregado `Tenant` e `POST /api/v1/tenants` (PR #1) gravam o tenant e publicam `TenantRegistered` no
   Outbox; a fundação Keycloak (PR #2) acrescentou o realm `identity-gateway` com Organizations e a autenticação
   `private_key_jwt` do service account; o consumidor do provisionamento (PR #3) consome o evento pelo próprio
   Outbox e decide entre repetir e desistir pela janela de provisionamento; e esta branch fecha o provisionamento
   da §9.1: o tenant só fica `Active` depois que o admin inicial é convidado no Keycloak — o e-mail de convite
   chega ao mailpit, com um link que abre no navegador —, e a ativação reserva a vaga do admin e registra o
   membro num commit só. **Próximo passo:** o resto do M1 (suspensão, encerramento, reconciliação, retry manual).
   O roadmap está em [`docs/especificacao-arquitetural-v2.6.md`](docs/especificacao-arquitetural-v2.6.md) §16
   (referência normativa atual — as anteriores ficam como registro histórico), e o estado detalhado no
   [handoff do convite do admin inicial](docs/superpowers/specs/AAAA-MM-DD-convite-admin-inicial-handoff.md).
   ```

2. **Contagem de testes.** Localizar a linha
   `# Toda a suíte — 331 testes, 0 skips (113 domínio, 49 application, 29 arquitetura, 114 integração, 26 funcional)`
   e substituir por (com os números reais do Passo 25, no mesmo formato):

   ```bash
   # Toda a suíte — <números reais do passo da suíte>
   ```

   Exemplo do formato final: `# Toda a suíte — NNN testes, 0 skips (NNN domínio, NN application, NN arquitetura, NNN integração, NN funcional)`.

3. **Aviso de volumes e `--build`.** No primeiro bloco de "Rodando local", localizar as duas linhas

   ```bash
   # As dependências, as migrations e a API junto
   docker compose up -d
   ```

   e substituí-las por:

   ```bash
   # As dependências, as migrations e a API junto (--build: a imagem da API acompanha o código)
   docker compose up -d --build
   ```

   Depois, localizar `Para recomeçar do zero: `docker compose down -v`.` (fim do parágrafo "Chave e realm andam
   juntos") e substituir por:

   ```markdown
   Para recomeçar do zero: `docker compose down -v`.

   **Já tinha subido o compose antes do convite do admin inicial? Rode `docker compose down -v` uma vez.** O realm só
   é importado na primeira subida, e num volume antigo faltam o papel `tenant-admin`, a permissão `manage-users` do
   service account, o User Profile e o SMTP. O `/health/ready` da API detecta isso e responde 503 com uma mensagem
   que manda rodar `docker compose down -v`. Depois, `docker compose up -d --build`, para a imagem da API não ficar
   para trás do código.
   ```

4. **URLs com o compose de pé.** Localizar
   `em `http://localhost:16686`.` (fim do parágrafo "Com o compose de pé") e substituir por:

   ```markdown
   em `http://localhost:16686`. Os e-mails que o Keycloak envia — convites e ações obrigatórias — vão para o
   mailpit, em `http://localhost:8025`.
   ```

5. **Mailpit na tabela do Keycloak.** Localizar a linha
   `| Senha | gerada na primeira subida: `docker compose logs gateway-keys` |`, mantê-la, e inserir logo depois dela:

   ```markdown
   | E-mails (mailpit) | http://localhost:8025 (só no localhost): os e-mails de convite do Keycloak |
   ```

6. **Mailpit no comando da IDE.** Localizar `docker compose up -d postgres redis keycloak` e substituir por:

   ```powershell
   docker compose up -d postgres redis mailpit keycloak
   ```

   E localizar
   `O `appsettings.Development.json` já aponta `Keycloak:Admin:BaseUrl` para `http://localhost:8081`.` e substituir por:

   ```markdown
   O `appsettings.Development.json` já aponta `Keycloak:Admin:BaseUrl` para `http://localhost:8081`, que é também
   o endereço público do Keycloak (`KC_HOSTNAME`): pela IDE, o `Keycloak:Admin:PublicBaseUrl` não é necessário. Use
   `localhost`, não `127.0.0.1` — o Keycloak compara o destinatário do assertion por texto.
   ```

7. **Demonstração.** Localizar a seção inteira, do título
   `### Demonstração: o tenant é provisionado quando o Keycloak volta` até a cerca que fecha o bloco de código que
   termina em `# {"tenantId":"…","status":"Active",…}` (a linha `---` logo abaixo fica), e substituí-la por:

   ````markdown
   ### Demonstração: o tenant é provisionado quando o Keycloak volta, e o admin recebe o convite

   A API aceita o tenant com o Keycloak fora do ar e o provisiona sozinha quando ele volta (spec §16). Provisionar
   inclui convidar o administrador inicial: o Keycloak manda o e-mail de convite, que cai no mailpit. Pressupõe o
   compose de pé (`docker compose up -d --build`, acima). O token é de platform-admin, assinado com a chave de
   desenvolvimento do compose — o mesmo formato que a API valida hoje:

   ```bash
   b64url() { openssl base64 -A | tr '+/' '-_' | tr -d '='; }
   agora=$(date +%s)
   cabecalho=$(printf '{"alg":"HS256","typ":"JWT"}' | b64url)
   corpo=$(printf '{"sub":"0199a000-0000-7000-8000-000000000001","roles":"platform-admin","iss":"identitygateway","aud":"identitygateway-api","nbf":%d,"exp":%d}' "$agora" "$((agora + 3600))" | b64url)
   assinatura=$(printf '%s.%s' "$cabecalho" "$corpo" | openssl dgst -sha256 -hmac "chave-de-desenvolvimento-nao-use-em-producao" -binary | b64url)
   TOKEN="$cabecalho.$corpo.$assinatura"

   # E-mail e slug únicos a cada execução: um e-mail já usado por outra conta do Keycloak faz o provisionamento
   # falhar de propósito (a mesma pessoa não administra dois tenants), e o slug é único e imutável.
   EMAIL="admin+$(date +%s)@acme.test"
   SLUG="acme-${EMAIL//[^0-9]/}"

   docker compose stop keycloak

   curl -si -X POST http://localhost:8080/api/v1/tenants \
     -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
     -d "{\"name\":\"Acme\",\"slug\":\"$SLUG\",\"planCode\":\"free\",\"initialAdminEmail\":\"$EMAIL\"}"
   # 202 Accepted, com Location: /api/v1/tenants/{id}/provisioning

   curl -s http://localhost:8080/api/v1/tenants/{id}/provisioning -H "Authorization: Bearer $TOKEN"
   # {"tenantId":"…","status":"Pending",…}

   docker compose start keycloak
   # em cerca de um minuto — o teto do backoff do Outbox:
   curl -s http://localhost:8080/api/v1/tenants/{id}/provisioning -H "Authorization: Bearer $TOKEN"
   # {"tenantId":"…","status":"Active",…}
   ```

   Agora abra http://localhost:8025. O mailpit mostra o e-mail de convite para o endereço de `$EMAIL`. Clique no
   link: ele abre o Keycloak em `http://localhost:8081`, numa página de confirmação; siga, defina a senha e informe
   nome e sobrenome. O link vale 7 dias (`Invitations:LinkLifetime`).

   O mesmo, sem sair do terminal — o que o job `Compose` da CI confere a cada PR:

   ```bash
   id=$(curl -sG http://localhost:8025/api/v1/search --data-urlencode "query=to:\"$EMAIL\"" | jq -r '.messages[0].ID')
   curl -s "http://localhost:8025/api/v1/message/$id" | jq -r '.Text' \
     | grep -o 'http://localhost:8081/realms/identity-gateway/login-actions/action-token?key=[^[:space:]]*'
   # http://localhost:8081/realms/identity-gateway/login-actions/action-token?key=eyJ…
   ```

   O e-mail do admin fica guardado no tenant só até a ativação: com o tenant `Active`, a coluna
   `tenants.initial_admin_email` volta a ser nula, e o endereço passa a existir só no Keycloak.
   ````

   Observação para quem executa: `SLUG="acme-${EMAIL//[^0-9]/}"` reaproveita o timestamp do e-mail (só os
   dígitos), para os dois serem únicos juntos; `--data-urlencode` escapa o `+` do e-mail, que numa URL crua viraria
   espaço. A verificação ao vivo da Tarefa 12 rodou este roteiro; se ela registrou algum ajuste de comando, aplicá-lo
   aqui antes do commit.

8. **Tabela "Por onde começar a ler".** Localizar
   ``| [**Especificação arquitetural v2.5**](docs/especificacao-arquitetural-v2.5.md) | A referência de implementação: domínio, endpoints, ADRs, código de referência |``
   e substituir por:

   ```markdown
   | [**Especificação arquitetural v2.6**](docs/especificacao-arquitetural-v2.6.md) | A referência de implementação: domínio, endpoints, ADRs, código de referência |
   ```

9. **Link dos ADRs.** Localizar
   `Dez ADRs, com o texto completo na [especificação §4](docs/especificacao-arquitetural-v2.5.md#4-decisões-arquiteturais-adrs).`
   e substituir por:

   ```markdown
   Dez ADRs, com o texto completo na [especificação §4](docs/especificacao-arquitetural-v2.6.md#4-decisões-arquiteturais-adrs).
   ```

10. **Linha evolutiva.** Localizar
    `ideia → v2.0 → [revisão crítica: 33 achados] → v2.1 → [documentação de negócio] → v2.2 → v2.3 → v2.4 → v2.5`
    e substituir por:

    ```text
    ideia → v2.0 → [revisão crítica: 33 achados] → v2.1 → [documentação de negócio] → v2.2 → v2.3 → v2.4 → v2.5 → v2.6
    ```

    Localizar a linha
    `- **v2.5** registrou a fatia B: transporte em processo até o broker e a decisão de desistir no handler.`, mantê-la,
    e inserir logo depois dela:

    ```markdown
    - **v2.6** registrou a fatia C, o convite do admin inicial, e duas erratas verificadas no código do Keycloak: o
      convidado nasce habilitado (o Keycloak recusa e-mail e clique de usuário desabilitado), e o destinatário do
      client assertion é o endereço público do Keycloak, não o de transporte.
    ```

11. **Stack.** Localizar as três linhas

    ```markdown
    **No compose hoje:** .NET 10 · PostgreSQL · Redis · EF Core 10 · Carter · Serilog · OpenTelemetry ·
    Seq · Jaeger · xUnit v3

    **Entram no M0:** Keycloak 26 · RabbitMQ · Mailpit
    ```

    e substituí-las por:

    ```markdown
    **No compose hoje:** .NET 10 · PostgreSQL · Redis · Keycloak 26.7.4 · Mailpit · EF Core 10 · Carter ·
    Serilog · OpenTelemetry · Seq · Jaeger · xUnit v3 · Testcontainers

    **Entra no M0:** RabbitMQ
    ```

Conferir:

Run: `grep -n "v2.5\|admin@acme.com\|acme-demo\|331 testes\|Entram no M0" README.md`
Expected: só a linha evolutiva (`→ v2.5 → v2.6`) e o item `- **v2.5** registrou a fatia B…`.

Run: `grep -n "8025\|mailpit\|down -v\|--build" README.md`
Expected: a nota do mailpit em "Com o compose de pé", a linha da tabela do Keycloak, o comando da IDE, o aviso de
volumes, `docker compose up -d --build` em "Rodando local" e na demonstração, e os passos do mailpit na
demonstração.

- [ ] **Passo 24: CONTRIBUTING**

Localizar (linha 108) `A [especificação arquitetural v2.3](docs/especificacao-arquitetural-v2.3.md) descreve as camadas, os`
e substituir por:

```markdown
A [especificação arquitetural v2.6](docs/especificacao-arquitetural-v2.6.md) descreve as camadas, os
```

Run: `grep -n "especificacao-arquitetural" CONTRIBUTING.md`
Expected: uma linha só, apontando `docs/especificacao-arquitetural-v2.6.md`.

- [ ] **Passo 25: Suíte completa**

Docker Desktop ligado (sem ele, integração e funcionais falham com `DockerUnavailableException` — ambiente, não
regressão).

Run: `dotnet build IdentityGateway.slnx`
Expected: `0 Aviso(s)`, `0 Erro(s)`.

Run: `dotnet test`
Expected: 0 falhas, 0 skips em todos os projetos. Anotar o total por projeto
(`IdentityGateway.Domain.UnitTests`, `IdentityGateway.Application.UnitTests`, `IdentityGateway.ArchitectureTests`,
`IdentityGateway.Infrastructure.IntegrationTests`, `IdentityGateway.Api.FunctionalTests`) e o total geral: entram
no README (Passo 23, item 2) e no handoff (Passo 26). Se algum teste falhar, parar e diagnosticar; não commitar
documentação sobre suíte vermelha.

Voltar ao README e trocar `<números reais do passo da suíte>` pelos números anotados, no formato
`NNN testes, 0 skips (NNN domínio, NN application, NN arquitetura, NNN integração, NN funcional)`.

Run: `grep -n "<números reais" README.md`
Expected: nenhuma linha.

- [ ] **Passo 26: Handoff**

Run: `date +%F` — a data do dia é o prefixo do arquivo.

Criar `docs/superpowers/specs/AAAA-MM-DD-convite-admin-inicial-handoff.md` com o conteúdo abaixo, no formato do
[handoff da fatia B](../specs/2026-09-26-consumidor-provisionamento-handoff.md). Preencher cada `<…>` com o que foi
**observado** — commits (`git log --oneline main..HEAD`), números da suíte do Passo 25, resultado de cada mutação
registrado nas Tarefas 1–11 e a verificação ao vivo da Tarefa 12. Nada de valor esperado no lugar de valor
observado; o que não foi feito fica escrito como não feito. Trocar também `AAAA-MM-DD` no link do README
(Passo 23, item 1) pelo nome real do arquivo.

````markdown
# Handoff — convite do admin inicial entregue

> **Data:** AAAA-MM-DD · **Marco:** M1 (fatia C) · **Status:** implementada, build e suíte completa verdes,
> verificação ao vivo confirmada. Push e PR aguardam autorização do usuário.
> **Onde parou:** as 13 tarefas do plano estão commitadas na branch `feat/convite-admin-inicial`; falta enviar a
> branch, abrir o PR contra `main`, acompanhar a CI e mesclar.
>
> Sucede o [handoff do consumidor do provisionamento](2026-09-26-consumidor-provisionamento-handoff.md) (fatia B,
> PR #3) e o PR #4 (serviço `migrate` no compose). Design da fatia:
> [`2026-09-29-convite-admin-inicial-design.md`](2026-09-29-convite-admin-inicial-design.md). Referência normativa:
> [`especificacao-arquitetural-v2.6.md`](../../especificacao-arquitetural-v2.6.md).

---

## Estado do repositório

| O quê | Estado |
|---|---|
| Branch | `feat/convite-admin-inicial`, 2 commits de design (`1b07a63`, `24f7cfa`), o commit do plano (`<hash>`) e **<N> commits** das Tarefas 1–13 sobre `main` (`a439e40`) |
| `main` | Não tocada — recebe o merge pelo PR |
| Working tree | Limpa |
| Docker | Rodando; usado nas tarefas de integração (Testcontainers: PostgreSQL, Keycloak e mailpit reais), na verificação ao vivo da Tarefa 12 (projeto isolado `igverif`) e na suíte completa do Passo 25 da Tarefa 13 |
| Push / PR | **Pendentes de autorização.** Nada foi enviado |

<Uma linha por frente de commits, como no handoff da fatia B: planejamento, Tarefas 1–12 (quantos `feat`, `fix`,
`test`, `refactor`, `chore`), Tarefa 13 (`docs`).>

**Aviso para quem já tem volumes do compose: rode `docker compose down -v` uma vez, depois
`docker compose up -d --build`.** O realm só é importado na primeira subida (`IGNORE_EXISTING`), e num volume
antigo faltam o papel `tenant-admin`, o `manage-users` do service account, o User Profile com o `tenant_id` e o
SMTP. O `/health/ready` responde 503 mandando rodar `docker compose down -v`; o README e o corpo do PR repetem o
aviso.

## O que a fatia entregou

O provisionamento da §9.1 fechado, menos a reconciliação: o tenant só fica `Active` depois de garantir a
Organization e o convite do `initialAdminEmail` com o papel `tenant-admin`, e a ativação reserva a vaga do admin,
cria o `Member` em `Invited` e apaga o e-mail num commit só. O e-mail de convite sai pelo SMTP do Keycloak e cai no
mailpit, com um link que abre no navegador.

| Camada | Entregue |
|---|---|
| Domain | `Tenant.Register(name, slug, plan, initialAdminEmail, registeredAt)`; `Tenant.InitialAdminEmail` (`Email?`); `Tenant.HasSeatAvailable`; `Tenant.CompleteProvisioning(externalOrganizationId, adminUserId, invitedAt)` devolvendo o `Member` (valida tudo antes de mudar; recusa `Active` e `ProvisioningFailed`); `MarkProvisioned` removido; `MarkProvisioningFailed` apaga o e-mail; `Member` (aggregate root, fábrica `internal` `Member.Invite`), `MemberId` (v7), `MemberStatus` (`Invited`, `Active`, `Deactivated`, `Expired`, `Revoked`, `Erased`), `ExternalUserId`, `RoleName` com `RoleName.TenantAdmin`; `Email.ToString()` sem o endereço; `Email.Of` com a regra do validador do `POST`; `DomainErrors.Email.Invalido` sem ecoar o valor |
| Application | `IIdentityProvider.EnsureInvitedUserAsync(organizationId, tenantId, InviteData, ct)`; `InviteData(Email, RoleName, LinkLifetime)` com `ToString` sem o e-mail; `IInvitationPolicy.LinkLifetime`; `IMemberRepository.Add`; `RegisterTenantCommand` passa o e-mail ao `Tenant`; `ProvisionTenantHandler` com a verificação prévia (sem e-mail ou sem vaga → `ProvisioningFailed` sem tocar o Keycloak), as duas chamadas no mesmo `try`, `CompleteProvisioning` e `IMemberRepository.Add` |
| Infrastructure | `KeycloakIdentityProvider.EnsureInvitedUserAsync` nos cinco passos (buscar com `exact=true` e `briefRepresentation=false`, criar habilitado com as duas ações e o `tenant_id`, `409` com uma nova busca só, vincular, papel pelo id das listas do usuário, `execute-actions-email` só com `UPDATE_PASSWORD` pendente); `InvitationOptions` (seção `Invitations`, `LinkLifetime` padrão `7.00:00:00`, positivo, segundos inteiros, ≤ 30 dias); `KeycloakAdminOptions.PublicBaseUrl` e `AssertionAudience`; token endpoint e Admin API só pelo `BaseUrl`; `AllowInsecureHttp` só em Development; `KeycloakHealthCheck` exige `manage-users` no token; catálogo de planos recusa `maxUsers < 1` |
| Persistence | Coluna `tenants.initial_admin_email` (`varchar(254)`, anulável); tabela `members` (`id`, `tenant_id` FK, `external_user_id`, `status` texto, `invited_at`, auditoria) com índice único `(tenant_id, external_user_id)`; migration `ConviteDoAdminInicial`, só de expansão; `DbSet<Member>` `internal` |
| Realm | Papel `tenant-admin`; service account com `manage-organizations` e `manage-users`; User Profile declarando `tenant_id` só para `admin`, sem `unmanagedAttributePolicy`; `smtpServer` por ambiente com `connectionTimeout` 2000, `timeout` 3000 e `writeTimeout` 3000; `resetPasswordAllowed: false`; `adminEventsEnabled: true`; `RegrasDoRealmTests` com as regras novas |
| Compose e CI | `mailpit` (`axllent/mailpit:v1.31.3`, UI/API em `127.0.0.1:8025`, SMTP só interno, healthcheck `readyz`); Keycloak com `KC_HOSTNAME=http://localhost:8081`, backchannel dinâmico e `SMTP_*`; API com `Keycloak__Admin__PublicBaseUrl`; Postgres, Redis e Seq só em `127.0.0.1`; teste de arquitetura `KC_HOSTNAME` == `PublicBaseUrl`; job `Compose` confere o e-mail no mailpit, o prefixo do link e um `GET 200` nele |
| Development | `appsettings.Development.json` com `Database:EnableSensitiveDataLogging=false` e Serilog `Microsoft.EntityFrameworkCore: Warning` |
| Testes | Mailpit no `KeycloakFixture` (rede Testcontainers, `KC_HOSTNAME=http://keycloak.test:8081`); papéis efetivos do service account; vazamento do e-mail (logs em `Trace` e exporter OpenTelemetry em memória); E2E com commit perdido gerando exatamente dois e-mails; atomicidade com a linha pré-inserida em `members` |
| Documentos | Especificação v2.6 (§0 nova e as seções da §8 do design); documento de negócio 1.3 alinhado à v2.6; README (andamento, mailpit, aviso de `down -v`, demonstração com o convite, contagem); CONTRIBUTING apontando a v2.6; este handoff |

## Suíte completa

`dotnet build IdentityGateway.slnx`: **<0> avisos, <0> erros.**

`dotnet test` (solução inteira, Docker rodando): **<total> total, <0> falhas, <0> skips.**

| Projeto | Total | Falhas | Skips |
|---|---|---|---|
| `IdentityGateway.Domain.UnitTests` | <n> | 0 | 0 |
| `IdentityGateway.Application.UnitTests` | <n> | 0 | 0 |
| `IdentityGateway.ArchitectureTests` | <n> | 0 | 0 |
| `IdentityGateway.Infrastructure.IntegrationTests` | <n> | 0 | 0 |
| `IdentityGateway.Api.FunctionalTests` | <n> | 0 | 0 |
| **Total** | **<n>** | **0** | **0** |

## Prova por mutação

Toda mutação foi aplicada, confirmada vermelha, revertida byte a byte e reconfirmada verde antes do commit. As
linhas são as da §5.3 do design; a coluna "Resultado" traz o observado — o teste que ficou vermelho e a mensagem —,
ou "não executada" com o motivo.

| Mutação | Deve ser pega por | Resultado |
|---|---|---|
| Tirar a reserva de vaga de `CompleteProvisioning` | Domínio | <preencher> |
| Mudar o tenant antes de validar a vaga | Domínio ("lança sem ter mudado nada") | <preencher> |
| Não apagar o e-mail (na ativação e na falha) | Domínio e E2E | <preencher> |
| Pôr o e-mail no `TenantRegistered` | Funcional (conteúdo do Outbox) | <preencher> |
| Aceitar usuário sem `tenant_id`, ou com o de outro tenant ("existe" no lugar de "igual") | Integração | <preencher> |
| Tirar o limite de uma volta depois do `409` | Integração (laço) | <preencher> |
| Tirar o escape do e-mail na query | Integração (e-mails com `+`) | <preencher> |
| Remover o `exact=true` | Integração (`pre.{x}`) | <preencher> |
| Remover o `briefRepresentation=false` | Integração (atributo ausente, reaproveitamento falha) | <preencher> |
| Remover o `lifespan`, ou usar `.Seconds` no lugar de `.TotalSeconds` | Integração (`exp − iat`) | <preencher> |
| Pular o passo 3 ou o 4 quando o usuário já existe | Integração (retomada parcial) | <preencher> |
| Enviar o e-mail sem checar `UPDATE_PASSWORD` | Integração (usuário aceito) | <preencher> |
| Tirar `VERIFY_EMAIL`, ou criar com `enabled=false` | Integração (leitura crua) | <preencher> |
| Remover a declaração do atributo no User Profile, ou trocar a política para `ENABLED` | Integração e arquitetura | <preencher> |
| Usar o `BaseUrl` no `aud` | Integração (todos os testes de Keycloak) | <preencher> |
| Usar o `PublicBaseUrl` no transporte | Integração e CI | <preencher> |
| O handler gravar o tenant antes de adicionar o `Member` | PostgreSQL (atomicidade) | <preencher> |
| O `try` cobrir só a primeira chamada ao Keycloak | Application | <preencher> |
| Pôr o e-mail num log ou numa mensagem de exceção | Vazamento do e-mail | <preencher> |
| Tirar o `KC_HOSTNAME` do compose | CI e arquitetura | <preencher> |

## Verificação ao vivo (Tarefa 12)

**Roteiro da CI rodado localmente, no projeto isolado `igverif`** (`docker compose -p igverif …`, volumes próprios,
sem tocar os do projeto padrão). Registrar:

| Passo | Horário | Resultado |
|---|---|---|
| `docker compose -p igverif up -d --build --wait` | <hh:mm:ss> | <serviços `healthy`; `/health/ready` → `Healthy`> |
| `POST /api/v1/tenants` (e-mail `admin+<timestamp>@acme.test`, slug único) | <hh:mm:ss> | <`202 Accepted`, `Location: …`> |
| `GET .../provisioning` até `Active` | <hh:mm:ss> | <corpo observado> |
| Busca no mailpit (`/api/v1/search?query=to:"…"`) | <hh:mm:ss> | <quantas mensagens> |
| Link extraído do campo `Text` | — | <prefixo `http://localhost:8081/realms/identity-gateway/login-actions/action-token?key=` confirmado> |
| `GET` no link | <hh:mm:ss> | <`200`, sem página de erro nem de expirado> |
| `docker compose -p igverif down` e segunda subida | <hh:mm:ss> | <a API volta `ready`: os one-shots são idempotentes> |
| `docker compose -p igverif down -v` | <hh:mm:ss> | <volumes do projeto isolado removidos> |

**Prova vermelha sem `KC_HOSTNAME`.** Com o `KC_HOSTNAME` do `keycloak` e o `Keycloak__Admin__PublicBaseUrl` da `api`
removidos (o estado de antes da fatia; revertidos depois, conferido com `git diff --exit-code docker-compose.yml`), o
mesmo roteiro: <o que falhou — o teste de arquitetura `ComposeTemKcHostnameIgualAoPublicBaseUrl` e, no roteiro, o
link com `http://keycloak:8080`; colar a mensagem observada>.

**Observações das mutações que não ficam vermelhas por desenho** (Tarefas 8 e 11): <sem `briefRepresentation=false`,
só o teste de forma da URI fica vermelho — o `GET /users` da 26.7.4 já devolve a representação completa; sem o
`IsConcurrencyToken()` do `xmin`, a entrega concorrente continua falhando pelo índice único de `members`>.

<Horário e log da API que confirmam a sequência (EventIds), como no handoff da fatia B.>

## Decisões tomadas durante a execução

| Decisão | Custo se errado |
|---|---|
| <uma linha por decisão das Tarefas 1–13 que não estava no plano: ajustes de analisador, snippets do plano que não compilaram, nomes de chave confirmados, comandos corrigidos na verificação ao vivo> | <…> |
| Timeouts do `smtpServer`: as chaves `connectionTimeout`, `timeout` e `writeTimeout` foram confirmadas em `DefaultEmailSenderProvider.java` L149-151 da 26.7.4, o que fecha a pendência da §3.4 do design | Nenhum: são as chaves que o provider lê |

## Pendências

**Dívidas registradas no design (§7), que continuam abertas:**
- **E-mail duplicado**, em qualquer falha entre o envio e o commit — provado pelo E2E de commit perdido (exatamente
  dois e-mails).
- **Link duplicado continua válido depois do aceite**, e permite trocar a senha do admin por até 7 dias.
- **O `Member` do admin fica `Invited` até a sincronização do ADR-007** (M4). A expiração do M2 **não pode** chegar
  antes dela sem outra forma de saber do aceite.
- **Admin órfão:** e-mail enviado e janela esgotada antes do commit deixam o tenant `ProvisioningFailed` com um
  usuário habilitado, com `tenant-admin` e com link válido. O retry manual e a reconciliação precisam tratar isso.
- **Tenants registrados antes desta fatia** vão para `ProvisioningFailed` e só saem pelo retry manual, informando o
  e-mail.
- **E-mail digitado errado entrega o tenant a um estranho**, sem revogação pela API. Runbook provisório: desabilitar
  o usuário no Keycloak. A operação de plataforma para trocar ou reenviar o convite do admin fica para o M1/M2.
- **A mesma pessoa não administra dois tenants** (ADR-009 e unicidade de e-mail no realm).
- **SMTP, papel, User Profile e permissões do realm só no primeiro import** — daí o `docker compose down -v`.
- **Retenção real do e-mail apagado:** o apagamento é lógico; WAL, dead tuples e backups guardam o valor.
- **notification-hub:** depende de o hub aceitar SMTP ou de uma extensão no Keycloak.
- **M4:** a conta convidada sem senha é alvo de vínculo automático no primeiro login federado. Nunca ligar vínculo
  sem verificação.
- **Um membro existente pode tomar de antemão o e-mail do futuro admin**, se a troca de e-mail não exigir
  verificação. Não verificado na 26.7.4.
- Herdada: quem perde a corrida de slug recebe `500`, e não `409`.

**Documento de negócio:** ainda descreve o transporte do provisionamento pelo RabbitMQ e o retry pelo MassTransit
(fluxo 9.1), que a v2.5 já tinha trocado pelo transporte em processo. Fora do escopo da fatia C.

<Pendências menores levantadas nos relatórios das tarefas: cobertura ausente, comentários, nomes.>

## Próximo passo

1. Autorizar o push e abrir o PR contra `main`, com o aviso de `docker compose down -v` no corpo.
2. Acompanhar a CI (`gh pr checks <n>`) — `Build`, `Testes`, `Imagem Docker` e `Compose`, que agora confere o
   e-mail no mailpit e abre o link. Se algo falhar, corrigir na mesma branch.
3. Mesclar. Depois: `git checkout main && git pull --ff-only`, apagar a branch local e a remota, e acrescentar o
   número do PR na linha da fatia C da §16 da v2.6.
4. Escolher a próxima fatia do M1 — suspensão, encerramento, reconciliação (que precisa tratar o admin órfão) ou o
   retry manual de `ProvisioningFailed` (que recebe o e-mail de novo) — e abrir o brainstorming.

## Como retomar

Docker Desktop costuma estar desligado ao abrir a sessão: sem ele, os testes de integração e funcionais falham com
`DockerUnavailableException` (ambiente, não regressão). Quem já tinha o compose de pé antes desta fatia precisa de
`docker compose down -v` uma vez.
````

- [ ] **Passo 27: Commit**

Run: `git status --short`
Expected: exatamente estes cinco caminhos — `A`/`??` para `docs/especificacao-arquitetural-v2.6.md` e para o
handoff, `M` para `docs/documentacao-negocio.md`, `README.md` e `CONTRIBUTING.md`. Nada em `src/`, `tests/`,
`keycloak/` nem `docker-compose.yml`.

```bash
git add docs README.md CONTRIBUTING.md
git commit -m "docs: especificacao v2.6, documento de negocio, README e handoff da fatia C

A v2.6 registra o convite do admin inicial: o e-mail temporario no
tenant, a ativacao com a vaga e o Member num commit so, o convite pela
Admin API com o tenant_id declarado no User Profile, o manage-users no
service account e o mailpit no compose, com duas erratas (o convidado
nasce habilitado; o aud do assertion e o emissor publico). O documento
de negocio passa a apontar a v2.6, o README ganha a demonstracao com o
e-mail no mailpit e o handoff traz a suite e a tabela de mutacoes."
```

Sem nenhum trailer: nada de `Co-Authored-By`, nada de "Generated with", nenhuma menção a IA, Claude ou Anthropic.

Run: `git log -1 --format=%B | grep -ci "co-authored\|generated with\|claude\|anthropic"`
Expected: `0`.

Push e PR ficam para autorização do usuário.

