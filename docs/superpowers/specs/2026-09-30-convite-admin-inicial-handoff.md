# Handoff — convite do admin inicial entregue

> **Data:** 2026-09-30 · **Marco:** M1 (fatia C) · **Status:** implementada, build e suíte completa verdes,
> verificação ao vivo confirmada. Push e PR aguardam autorização do usuário.
> **Onde parou:** as 13 tarefas do plano estão commitadas na branch `feat/convite-admin-inicial`; falta a revisão
> final da branch, enviar a branch, abrir o PR contra `main`, acompanhar a CI e mesclar.
>
> Sucede o [handoff do consumidor do provisionamento](2026-09-26-consumidor-provisionamento-handoff.md) (fatia B,
> PR #3) e o PR #4 (serviço `migrate` no compose). Design da fatia:
> [`2026-09-29-convite-admin-inicial-design.md`](2026-09-29-convite-admin-inicial-design.md). Referência normativa:
> [`especificacao-arquitetural-v2.6.md`](../../especificacao-arquitetural-v2.6.md).

---

## Pendente para o autor: uma prova por mutação que não foi executada

**A mutação da guarda `HttpSoEmDesenvolvimento` (Tarefa 7) não foi provada.** A guarda recusa `AllowInsecureHttp`
e `PublicBaseUrl` em `http` fora de `Development`. A mutação foi aplicada, mas a proteção automática do ambiente de
execução **negou rodar os testes** com a verificação de https enfraquecida, e ninguém a contornou. A guarda
commitada está correta — conferida por leitura: só `IsDevelopment()` passa —, e a leitura dos testes confirma que
`AllowInsecureHttpForaDeDevelopment_FalhaAoValidar` e `PublicBaseUrlHttpForaDeDevelopment_FalhaAoValidar` a pegariam.
Mas o vermelho deles sob a mutação **não foi observado**. Roteiro para fechar:

1. Em `src/IdentityGateway.Infrastructure/Identity/Keycloak/KeycloakServiceCollectionExtensions.cs`, pôr `return true;`
   na primeira linha do corpo de `HttpSoEmDesenvolvimento`.
2. `dotnet test tests/IdentityGateway.Infrastructure.IntegrationTests --filter-class "*KeycloakAdminOptionsTests"`
3. Ver os dois testes `…ForaDeDevelopment_FalhaAoValidar` vermelhos (o controle positivo `HttpsEmProducao_Aceita`
   continua verde).
4. Reverter e conferir `git diff` vazio.

---

## Estado do repositório

| O quê | Estado |
|---|---|
| Branch | `feat/convite-admin-inicial`, 2 commits de design (`1b07a63`, `24f7cfa`), o commit do plano (`efa76ea`) e **17 commits** das Tarefas 1–13 sobre `main` (`a439e40`, o merge do PR #4) |
| `main` | Não tocada — recebe o merge pelo PR |
| Working tree | Limpa depois do commit desta tarefa |
| Docker | Rodando; usado nas tarefas de integração (Testcontainers: PostgreSQL, Keycloak e mailpit reais), na verificação ao vivo da Tarefa 12 (projeto isolado `igverif`, derrubado com `down -v` no fim) e na suíte completa da Tarefa 13. Os volumes `identitygateway_*` do autor não foram tocados |
| Push / PR | **Pendentes de autorização.** Nada foi enviado |

Quatro frentes de commits compõem a branch: **planejamento** (3 `docs` — design, design revisado e plano),
**Tarefas 1–10** (11 — **9** `feat`, 1 `test` da onda de correção da Tarefa 8 e 1 `fix` do vazamento no
EF), **Tarefas 11–12** (5 — 2 `test`, 2 `fix` de produção achados pelo teste de entrega concorrente, 1 `ci`) e a
**Tarefa 13** (1 `docs`: especificação v2.6, documento de negócio, README, CONTRIBUTING e este handoff).

**Aviso para quem já tem volumes do compose: rode `docker compose down -v` uma vez, depois
`docker compose up -d --build`.** O realm só é importado na primeira subida (`IGNORE_EXISTING`), e num volume
antigo faltam o papel `tenant-admin`, o `manage-users` do service account, o User Profile com o `tenant_id` e o
SMTP. O `/health/ready` responde 503 com uma descrição neutra do que falta (o token sem `manage-users`); o README e o corpo do PR repetem o
aviso.

## O que a fatia entregou

O provisionamento da §9.1 fechado, menos a reconciliação: o tenant só fica `Active` depois de garantir a
Organization e o convite do `initialAdminEmail` com o papel `tenant-admin`, e a ativação reserva a vaga do admin,
cria o `Member` em `Invited` e apaga o e-mail num commit só. O e-mail de convite sai pelo SMTP do Keycloak e cai no
mailpit, com um link que abre no navegador.

| Camada | Entregue |
|---|---|
| Domain | `Tenant.Register(name, slug, plan, initialAdminEmail, registeredAt)`; `Tenant.InitialAdminEmail` (`Email?`); `Tenant.HasSeatAvailable`; `Tenant.CompleteProvisioning(externalOrganizationId, adminUserId, invitedAt)` devolvendo o `Member` (valida tudo antes de mudar; recusa `Active` e `ProvisioningFailed`); `MarkProvisioned` removido; `MarkProvisioningFailed` apaga o e-mail; `Member` (aggregate root, fábrica `internal` `Member.Invite`), `MemberId` (v7), `MemberStatus` (`Invited`, `Active`, `Deactivated`, `Expired`, `Revoked`, `Erased`), `ExternalUserId`, `RoleName` com `RoleName.TenantAdmin`; `Email.ToString()` sem o endereço; `Email.Of` com a regra do validador do `POST`; `DomainErrors.Email.Invalido` sem ecoar o valor |
| Application | `IIdentityProvider.EnsureInvitedUserAsync(organizationId, tenantId, InviteData, ct)`; `InviteData(Email, RoleName, LinkLifetime)` com `ToString` sem o e-mail; `IInvitationPolicy.LinkLifetime`; `IMemberRepository.Add`; `RegisterTenantCommand` passa o e-mail ao `Tenant`; `ProvisionTenantHandler` com a verificação prévia (sem e-mail ou sem vaga → `ProvisioningFailed` sem tocar o Keycloak, logs `1105` e `1106`), as duas chamadas no mesmo `try`, `CompleteProvisioning` e `IMemberRepository.Add` |
| Infrastructure | `KeycloakIdentityProvider.EnsureInvitedUserAsync` nos cinco passos (buscar com `exact=true` e `briefRepresentation=false`, criar habilitado com as duas ações e o `tenant_id`, `409` com uma nova busca só, vincular, papel pelo id das listas do usuário, `execute-actions-email` só com `UPDATE_PASSWORD` pendente), **mais as duas correções de corrida** (abaixo): `KeycloakAdminClient.IsOrganizationMemberAsync` e a releitura dos papéis atribuídos, com os logs `2214` e `2215`; `InvitationOptions` (seção `Invitations`, `LinkLifetime` padrão `7.00:00:00`, positivo, segundos inteiros, ≤ 30 dias); `KeycloakAdminOptions.PublicBaseUrl` e `AssertionAudience`; token endpoint e Admin API só pelo `BaseUrl`; `AllowInsecureHttp` só em Development; `KeycloakHealthCheck` exige `manage-users` no token; catálogo de planos recusa `maxUsers < 1` |
| Persistence | Coluna `tenants.initial_admin_email` (`varchar(254)`, anulável); tabela `members` (`id`, `tenant_id` FK `Restrict`, `external_user_id`, `status` texto, `invited_at`, auditoria) com índice único `(tenant_id, external_user_id)`; migration `ConviteDoAdminInicial`, só de expansão; `DbSet<Member>` `internal` |
| Realm | Papel `tenant-admin`; service account com `manage-organizations` e `manage-users`; User Profile declarando `tenant_id` só para `admin`, sem `unmanagedAttributePolicy`; `smtpServer` por ambiente com `connectionTimeout` 2000, `timeout` 3000 e `writeTimeout` 3000; `resetPasswordAllowed: false`; `adminEventsEnabled: true`; `RegrasDoRealmTests` com as regras novas |
| Compose e CI | `mailpit` (`axllent/mailpit:v1.31.3`, UI/API em `127.0.0.1:8025`, SMTP só interno, healthcheck `readyz`); Keycloak com `KC_HOSTNAME=http://localhost:8081`, backchannel dinâmico, `SMTP_*` e `test -n` no entrypoint; API com `Keycloak__Admin__PublicBaseUrl`; Postgres, Redis e Seq só em `127.0.0.1`; testes de arquitetura `ComposeTemKcHostnameIgualAoPublicBaseUrl`, `ComposeEFixtureUsamAMesmaTagDoMailpit` e `DependenciasComDadoPessoalPublicamSoEmLocalhost`; job `Compose` com e-mail e slug únicos, confere o e-mail no mailpit, o prefixo do link e um `GET 200` nele |
| Development | `appsettings.Development.json` com `Database:EnableSensitiveDataLogging=false` e Serilog `Microsoft.EntityFrameworkCore: Warning`; teste de arquitetura `Development_NaoRegistraDadosSensiveisDoEf` |
| Testes | Mailpit no `KeycloakFixture` (rede Testcontainers, `KC_HOSTNAME=http://keycloak.test:8081`); papéis efetivos do service account; vazamento do e-mail (logs em `Trace` e exporter OpenTelemetry em memória, com amostragem restrita ao rastro de cada teste); E2E com commit perdido gerando exatamente dois e-mails; **atomicidade pela mudança do `xmin` durante o convite** e colisão no índice único de `members`; entrega concorrente (duas entregas sincronizadas no Keycloak → um `Member`, uma vaga, um usuário) |
| Documentos | Especificação v2.6 (§0 nova, com C1–C18, e as seções que a fatia tocou); documento de negócio 1.3 alinhado à v2.6; README (andamento, mailpit, aviso de `down -v`, demonstração com o convite, contagem); CONTRIBUTING apontando a v2.6; este handoff |

## O que mudou em relação ao plano

O plano foi escrito antes da execução. O que a execução encontrou, e que a v2.6 registra:

1. **Duas correções de corrida no adaptador** (`f6b7bb6`, `396a290`), achadas pelo teste de entrega concorrente
   contra o Keycloak real. Em 48 rodadas de diagnóstico antes das correções: 43 `Active`, **4 `ProvisioningFailed`**
   e 1 `Pending`.
   - **Papel:** as duas entregas leem os atribuídos sem o `tenant-admin`; uma atribui; a outra lê os disponíveis
     depois disso e não acha o papel, porque "disponíveis" exclui o já atribuído — e concluía "papel ausente", uma
     inconsistência permanente. Agora, papel ausente dos disponíveis leva a **uma** releitura dos atribuídos antes
     de concluir ausência (log `EventId 2214`).
   - **Vínculo:** o vínculo do Keycloak consulta e depois insere (`JpaOrganizationProvider.addMember`, L214-221 da
     26.7.4), e o perdedor de dois `POST` concorrentes recebe **400** (a `ModelException` do `INSERT` repetido,
     `OrganizationMemberResource.addMember` L122-123), não 409. Agora, no 400, **uma** leitura da pertença,
     `GET /organizations/{id}/members/{userId}` (`OrganizationMemberResource` L211-231), antes de propagar (log
     `EventId 2215`). O endpoint foi verificado no fonte da 26.7.4 e ao vivo: o service account com
     `manage-organizations` e `manage-users` recebe 200 para membro e 404, não 403, para não membro.
   - Os `EventId` 2214 e 2215 ficam **fora da faixa 2205–2213** que o plano previa para o convite; nasceram das
     correções e são únicos no `KeycloakLogs`.
   - Os dois `fix:` mudaram código de produção na Tarefa 11, cujo brief dizia que nenhum código de produção mudaria.
     A decisão foi do controlador: a spec exige provisionamento idempotente, e uma corrida benigna não pode terminar
     em `ProvisioningFailed`.
2. **Corrida residual não corrigida:** quando as duas entregas veem o papel como disponível, as duas fazem
   `POST .../role-mappings/realm`, e a perdedora recebe 400 (`RoleMapperResource.java` L278-280). É transitória —
   não leva a `ProvisioningFailed`, e o ciclo seguinte completa —, e foi observada 1 vez em 60 rodadas com
   interceptação. A correção cabe no molde da do vínculo. Registrada na §19 da v2.6.
3. **A entrega concorrente também gera dois convites**, o mesmo duplo envio já declarado para o commit perdido:
   as duas entregas passam pelo passo 5 antes de qualquer commit. Registrado na §19 da v2.6.
4. **O design errou a ordem do lote do EF (§5.1).** A ordem real, capturada com um logger temporário, é
   `INSERT INTO members` → `INSERT INTO outbox_messages` → `UPDATE tenants … WHERE id AND xmin`. Uma linha
   pré-inserida em `members` derruba o **primeiro** comando, e o PostgreSQL não executa o resto — não prova rollback
   de nada. O teste de atomicidade (`UltimoComandoDoCommitFalha_NadaDaAtivacaoEGravado`) passou a mudar o `xmin` do
   tenant por outra conexão durante `EnsureInvitedUserAsync`: o `UPDATE` final casa zero linhas, e o teste exige o
   tenant `Pending`, com o e-mail, zero vagas, **zero linhas em `members`** e nenhum `tenant-activated`. O teste da
   pré-inserção ficou, renomeado para o que prova (`MemberJaExistenteNoIndiceUnico_TenantNaoAtivaENadaEGravado`: a
   colisão no índice não ativa o tenant). Consequência na entrega concorrente: quem derruba a segunda entrega é o
   índice único de `members`, não o `xmin`. Registrado na §13 da v2.6.
5. **O `${SMTP_FROM}` literal derruba o import do realm.** O plano supunha que um placeholder `${SMTP_*}` sem valor
   ficaria como texto literal, sem erro, como o `${GATEWAY_CLIENT_CERT}`. Na 26.7.4, o import falha com
   `ERROR: Invalid sender address '${SMTP_FROM}'` e o container sai com código 1 — e o `KeycloakFixture` inteiro cai
   junto. Por isso o `test -n` das três variáveis no entrypoint do compose, e as três `WithEnvironment` de SMTP no
   fixture desde a Tarefa 6. Registrado na §15 da v2.6.

## Suíte completa

`dotnet build IdentityGateway.slnx`: **0 avisos, 0 erros.**

`dotnet test` (solução inteira, Docker rodando, HEAD `3ea93b7`, antes do commit desta tarefa, que só muda
documentação): **491 total, 0 falhas, 0 skips.** Os totais por projeto vêm de uma segunda execução, projeto a
projeto, com `--no-build`.

| Projeto | Total | Falhas | Skips |
|---|---|---|---|
| `IdentityGateway.Domain.UnitTests` | 160 | 0 | 0 |
| `IdentityGateway.Application.UnitTests` | 70 | 0 | 0 |
| `IdentityGateway.ArchitectureTests` | 41 | 0 | 0 |
| `IdentityGateway.Infrastructure.IntegrationTests` | 189 | 0 | 0 |
| `IdentityGateway.Api.FunctionalTests` | 31 | 0 | 0 |
| **Total** | **491** | **0** | **0** |

Depois das edições desta tarefa, `IdentityGateway.ArchitectureTests` rodou de novo: 41/41.

## Prova por mutação

Toda mutação executada foi aplicada, confirmada vermelha (erro de compilação não conta), revertida byte a byte
(`git diff` vazio ou `cmp` com cópia) e reconfirmada verde antes do commit. As linhas são as da §5.3 do design; a
coluna "Resultado" traz o observado nos relatórios das tarefas — o teste que ficou vermelho e a mensagem —, ou
"não executada" com o motivo.

| Mutação | Deve ser pega por | Resultado |
|---|---|---|
| Tirar a reserva de vaga de `CompleteProvisioning` | Domínio | Vermelho (T3): comentado `OcuparVaga();`, 6 falhas em `TenantTests` — o teste da ativação e os de `ReserveSeat` que contam a vaga do admin |
| Mudar o tenant antes de validar a vaga | Domínio ("lança sem ter mudado nada") | Vermelho (T3): atribuições antes do `if (!HasSeatAvailable)`, 1 falha, `SemVaga_LancaSemTerMudadoNada` |
| Não apagar o e-mail (na ativação e na falha) | Domínio e E2E | Vermelho. Domínio (T3): sem `InitialAdminEmail = null` em `CompleteProvisioning`, 1 falha; em `MarkProvisioningFailed`, 1 falha (`…ApagaOEmail`). E2E (T11): `TenantPendente_ViraActiveComMemberEmailApagadoUsuarioEConvite`, coluna diferente de `<nulo>` |
| Pôr o e-mail no `TenantRegistered` | Funcional (conteúdo do Outbox) | Vermelho (T11): `ComandoValido_GravaOEmailNormalizadoForaDoEvento` — "Did not expect evento {… "adminEmail": "admin+…@acme.test" …}" |
| Aceitar usuário sem `tenant_id`, ou com o de outro tenant ("existe" no lugar de "igual") | Integração | Vermelho (T8). "Existe" (`valores.Count > 0`): `UsuarioComTenantIdDeOutroTenant_LancaInconsistencia` e `UsuarioDeOutroTenant_LancaInconsistenciaSemOEmail`. Sem `tenant_id` aceito (`Attributes is null \|\| …`): `UsuarioPreExistenteSemTenantId_LancaInconsistencia`, `UsernameIgualAoEmailEmOutroUsuario_…` e `ConflitoSemNossoUsuarioNaReconsulta_…` |
| Tirar o limite de uma volta depois do `409` | Integração (laço) | Vermelho (T8). Unidade: `ConflitoSemNossoUsuarioNaReconsulta_…`. Integração: no commit original o teste ficava pendurado (sessão abortada por timeout, 0 testes concluídos); depois da correção `4d8db20` (500 a partir do segundo `POST /users`), `UsernameIgualAoEmailEmOutroUsuario_LancaInconsistenciaSemLaco` fica vermelho em segundos — "Expected … IdentityProviderInconsistencyException …, but found HttpRequestException: … 500" |
| Tirar o escape do e-mail na query | Integração (e-mails com `+`) | Vermelho (T8): `ChamadoDuasVezes_…`, `Convite_BuscaComEmailEscapadoExactEBriefRepresentationFalse` e mais 8 — todos os que reencontram o usuário |
| Remover o `exact=true` | Integração (`pre.{x}`) | Vermelho (T8): `PreXExistente_ConviteParaXCriaOutroUsuario` e `Convite_BuscaComEmailEscapado…` |
| Remover o `briefRepresentation=false` | Integração (atributo ausente, reaproveitamento falha) | Vermelho **só no teste de forma da URI** (T8): `Convite_BuscaComEmailEscapadoExactEBriefRepresentationFalse`. Nenhum teste de integração fica vermelho: o `GET /users` da 26.7.4 já devolve a representação completa, e a mutação é equivalente contra essa versão. O parâmetro fica explícito contra mudança de padrão |
| Remover o `lifespan`, ou usar `.Seconds` no lugar de `.TotalSeconds` | Integração (`exp − iat`) | Vermelho (T8). `.Seconds`: `LinkDoEmail_…` ("found 0L") e `UsuarioNovo_…EnviaComOPrazo`. Parâmetro trocado por `lifespan_ignorado` (o Keycloak ignora e usa o padrão): `LinkDoEmail_…` ("found 43200L", as 12 h do realm) e `UsuarioNovo_…` |
| Pular o passo 3 ou o 4 quando o usuário já existe | Integração (retomada parcial) | Vermelho (T8), com a mutação **reescrita**: a do plano ("`return` logo depois do `UsuarioJaExistia`") era o que o código já fazia. A usada pula vínculo e papel só quando o usuário foi achado: `UsuarioPreCriadoComNossoTenantSemVinculoNemPapel_CompletaTudo`, `QuedaNoVinculo_…`, `QuedaNoPapel_…` e 3 de unidade; `ConviteNovo_…` verde, o que mostra que ela só pega a retomada |
| Enviar o e-mail sem checar `UPDATE_PASSWORD` | Integração (usuário aceito) | Vermelho (T8): `UsuarioQueJaAceitou_NenhumEmailEnviado`, `UsuarioQueJaAceitou_NaoEnviaEmail` e `Convite_BuscaComEmailEscapado…` |
| Tirar `VERIFY_EMAIL`, ou criar com `enabled=false` | Integração (leitura crua) | Vermelho (T8). Sem `VERIFY_EMAIL`: `ConviteNovo_…` (leitura crua) e `UsuarioNovo_…`. `Enabled: false`: `ConviteNovo_…`, `NossoUsuarioDesabilitado_…` e mais 10 (o envio dá 400) |
| Remover a declaração do atributo no User Profile, ou trocar a política para `ENABLED` | Integração e arquitetura | Vermelho. Sem a declaração (T8): `ConviteNovo_…` e mais 10 (o atributo é descartado em silêncio) e `UsuarioComum_…`. `edit` com `user` (T8): só `UsuarioComum_NaoAlteraOTenantIdPelaAccountApi` ("Expected sequestro.IsSuccessStatusCode to be False, but found True"); na arquitetura (T6), `AtributoTenantIdDeclaradoSoParaAdmin`. `unmanagedAttributePolicy: ENABLED` (T6): `PerfilSemUnmanagedAttributePolicy` — **só na arquitetura**; contra o Keycloak real não foi executada |
| Usar o `BaseUrl` no `aud` | Integração (todos os testes de Keycloak) | Vermelho (T7): 18 de 147 — 16 testes contra o Keycloak real com `400 invalid_client: Invalid token audience` e 2 unitários do `AssertionAudience` |
| Usar o `PublicBaseUrl` no transporte | Integração e CI | Vermelho na integração (T7): 19 de 147, `Este host não é conhecido. (keycloak.test:8081)`. Na CI, não executada como mutação |
| O handler gravar o tenant antes de adicionar o `Member` | PostgreSQL (atomicidade) | Vermelho (T11): `SaveChangesAsync` entre `CompleteProvisioning` e `membros.Add` → `MemberJaExistenteNoIndiceUnico_…`, "Expected depois.Status to be Pending, but found Active". O teste do `xmin` fica verde sob ela, pelo motivo certo: o `UPDATE` do tenant falha no `SaveChanges` intermediário, antes do `Add`. Mutação acrescentada, commit sem transação: `UltimoComandoDoCommitFalha_…` vermelho, "Expected MembrosAsync(...) to be empty because o INSERT do Member executou e foi desfeito" |
| O `try` cobrir só a primeira chamada ao Keycloak | Application | Vermelho (T9): 6 — `Inconsistencia_MarcaFailedSemEsperarAJanela [convite]`, `FalhaTransitoriaDepoisDaJanela_MarcaFailed [convite]`, `TimeoutDaResilienciaDepoisDaJanela_MarcaFailed`, `Keycloak403DentroEForaDaJanela_…` e dois ramos de `CadaRamoQueRegistra_…`; a exceção escapa do handler |
| Pôr o e-mail num log ou numa mensagem de exceção | Vazamento do e-mail | Vermelho. Exceção (T10): exatamente `Inconsistencia_NadaVaza` e `ConflitoNoPost_NadaVaza`. Log do adaptador (T10): `CaminhoFeliz_NadaVaza` ("usuário do convite do tenant … criado (admin+…"). Log da Application (T9): `CadaRamoQueRegistra_TemLogENenhumContemOEmail [sem vaga]` |
| Tirar o `KC_HOSTNAME` do compose | CI e arquitetura | Vermelho (T12), removidos `KC_HOSTNAME`, `KC_HOSTNAME_BACKCHANNEL_DYNAMIC` e `Keycloak__Admin__PublicBaseUrl`: `ComposeTemKcHostnameIgualAoPublicBaseUrl` ("Expected hostname not to be <null> because sem KC_HOSTNAME o link do convite sai com http://keycloak:8080") e, no roteiro da CI no `igverif`, "O link do convite não usa o endereço público: 'http://keycloak:8080/realms/identity-gateway/login-actions/action-token'" (exit 1) |

**Demais mutações executadas nas tarefas**, além das da §5.3:

| Tarefa | Mutação | Resultado |
|---|---|---|
| T1 | `Email.ToString() => Value`; validador com `.EmailAddress()`; `Invalido(valor)` ecoando o valor; parte local de 65; mensagem do validador com `{PropertyValue}` | Vermelhas: `ToString_NaoContemOEndereco`; `EmailInvalido_Falha` (2 casos); `ErroDeEnderecoInvalido_NaoEcoaOValor`; `Of_ParteLocalDe64_AceitaEDe65_Recusa`; `EmailInvalido_MensagemNaoEcoaOValor` |
| T2 | `Member.Invite` público; sem `ToUniversalTime`; `AggregateRoot<Guid>` | Vermelhas: `Member_NaoTemConstrutorNemFabricaPublicos`; `Invite_NormalizaInvitedAtParaUtc`; `TodaRaizDeAgregadoTemIdentidadeTipada` |
| T3 | `CompleteProvisioning` aceitando `ProvisioningFailed` | Vermelha, 1 falha |
| T4 | **Substituída:** a do plano (remover o `HasIndex` inteiro) faz o EF criar um índice por convenção e o `PendingModelChangesWarning` derruba o fixture — vermelho pelo motivo errado. A usada tira só o `IsUnique()` (config, migration, Designer, snapshot) | Vermelhas: `IndiceUnico_CobreTenantESub` ("to contain UNIQUE") e `IndiceUnico_RecusaOMesmoSubNoMesmoTenant` ("Expected PostgresException, none thrown") |
| T4 | `HasMaxLength(254)` → 200 | Vermelhas: `EmailDe254Caracteres_SobreviveAoRoundTrip` (`22001: value too long`) e `InitialAdminEmail_AnulavelDe254` |
| T5 | Sem a checagem de segundos inteiros; `<=` → `<` no prazo máximo; `MaxUsers >= 0` | Vermelhas: `PrazoDoLinkInvalido_FalhaAoValidar("00:00:01.500")`; `PrazoDoLinkValido_ChegaInteiroAPolitica("30.00:00:00")`; `PlanoSemVagas_FalhaAoValidar` |
| T6 | `realm-admin` no service account; `displayName` com `${username}`; timeout 3000 → 10000 | Vermelhas: `ServiceAccountComManageOrganizationsEManageUsers`, e contra o Keycloak real `ServiceAccount_PapeisEfetivosSaoExatamenteOsDois` (22 papéis efetivos) e `ServiceAccount_RecebeProibidoEmClientsENaConfiguracaoDoRealm`; `TodoPlaceholderEPuro`; `SmtpPorAmbienteComTimeoutsAbaixoDoDaGateway` |
| T7 | `HttpSoEmDesenvolvimento` sempre verdadeira | **Não executada** — ver o destaque no topo |
| T7 | Health check `Healthy` sem conferir `manage-users` | Vermelha: `TokenSemManageUsers_UnhealthyMandandoRecriarOsVolumes` |
| T8 | `InviteData` sem o `ToString` sobrescrito | Vermelha: `ToString_TemPapelEPrazoMasNaoOEmail` |
| T8 | `DisableForUnsafeHttpMethods` comentado | Vermelhas: `PutDoEnvioComRespostaPerdida_…`, `PostComRespostaPerdida_NaoERepetido`, `SmtpFora_…`, `QuedaNoVinculo_…`, `QuedaNoPapel_…` |
| T9 | Vaga verificada depois do `try`; `platform-admin` no lugar de `TenantAdmin`; sem `membros.Add` (como `GC.KeepAlive(membros)`, porque apagar a linha não compila); `DateTimeOffset.UtcNow` no lugar do relógio; log `SemEmailDoAdmin` apagado | Vermelhas: `SemVaga_MarcaFailedSemTocarOKeycloak`; `TenantPendente_GaranteOrganizacaoConvidaEAtivaComOMember` (3×, com as mensagens do NSubstitute e do `InvitedAt`) e `SmtpForaEDeVolta_…`; `CadaRamoQueRegistra_… [sem e-mail]` |
| T10 | `EnableSensitiveDataLogging: true`; redação de URI desligada (`DOTNET_SYSTEM_NET_HTTP_DISABLEURIREDACTION=1`), com e sem a asserção de log; override do EF em `Information`; raiz do rastro não gravada; `{PropertyValue}` no validador | Vermelhas: regra de arquitetura e 6/6 de vazamento; `CaminhoFeliz_NadaVaza` pelo log e depois pelo span (`url.full` com a query); `RegrasDoAmbienteLocalTests`; pré-condição de spans; `EmailMalFormado_400SemOValor` |
| T11 | Sem a guarda "só `Pending`" no handler | Vermelha: `DuasEntregasConcorrentes_…` ("ProcessedOn … found <null>") |
| T11 | Sem o `IsConcurrencyToken()` do `xmin` (observação) | `DuasEntregasConcorrentes_…` **verde por desenho** (o índice único age primeiro); `UltimoComandoDoCommitFalha_…` vermelho ("Expected a DbUpdateConcurrencyException to be thrown, but no exception was thrown") |
| T11 | Sem a releitura dos papéis; sem a leitura da pertença | Vermelhas: os 2 testes novos do papel; os 4 do vínculo, inclusive os 2 contra o Keycloak real |
| T12 | Tag do mailpit `v1.31.2`; Redis em todas as interfaces; `127.0.0.1:1025` publicado; `PublicBaseUrl` com `127.0.0.1`; backchannel dinâmico `false`; sem `SMTP_HOST` | Vermelhas: `ComposeEFixtureUsamAMesmaTagDoMailpit`; `DependenciasComDadoPessoalPublicamSoEmLocalhost` (2×); `ComposeTemKcHostnameIgualAoPublicBaseUrl` (2×); o `test -n` do entrypoint encerrou o container (`exit=1`) no `igverif` |

## Verificação ao vivo (Tarefa 12)

**Roteiro da CI rodado localmente, no projeto isolado `igverif`** (`docker compose -p igverif …`, volumes próprios,
sem tocar os do projeto padrão). Antes de subir, nada escutava nas portas do compose e o projeto `identitygateway`
não existia. `docker compose -p igverif up -d --build --wait --wait-timeout 300 api`: todos os serviços `healthy`, e
`/health/ready` respondeu `Healthy`. O roteiro é o do job `Compose`, com `| tr -d '\r'` nas saídas do `jq` (o `jq`
nativo do Windows emite CRLF); o link nunca foi impresso.

| Execução | POST | `Active` | Assunto no mailpit | `GET` no link | Resultado |
|---|---|---|---|---|---|
| 1ª subida | `202` (14:47:11Z) | 14:47:18Z | Update Your Account | `200` | "Convite no mailpit (1 mensagem(ns)); o link público abriu a página de ações." |
| 2ª subida, mesmos volumes (`down` + `up`) | `202` (14:48:15Z) | 14:48:22Z | Update Your Account | `200` | verde, outro e-mail e outro slug: os one-shots são idempotentes |
| Sem `KC_HOSTNAME` (prova vermelha) | `202` (14:49:23Z) | 14:49:30Z | Update Your Account | — | "O link do convite não usa o endereço público: 'http://keycloak:8080/realms/identity-gateway/login-actions/action-token'" (exit 1) |
| Revertido, `keycloak` e `api` recriados | `202` (14:50:17Z) | 14:50:24Z | Update Your Account | `200` | verde |
| Depois da prova do `test -n`, recriados de novo | — | — | Update Your Account | `200` | verde |

A resposta do `GET .../provisioning` não traz o e-mail (só `tenantId`, `status` e `registeredAt`). O `GET` no link
voltou com a página de ações (`kc-info-message`, sem `kc-error-message`); a interface do mailpit não foi aberta num
navegador. Encerramento: `docker compose -p igverif down -v` removeu `igverif_gateway-keys`, `igverif_postgres-data`
e a rede; os volumes `identitygateway_*` do autor continuam intactos.

**Prova vermelha sem `KC_HOSTNAME`.** Com o `KC_HOSTNAME` e o `KC_HOSTNAME_BACKCHANNEL_DYNAMIC` do `keycloak` e o
`Keycloak__Admin__PublicBaseUrl` da `api` removidos (o estado de antes da fatia; revertidos depois, conferido com
`cmp`), o teste de arquitetura `ComposeTemKcHostnameIgualAoPublicBaseUrl` falhou ("Expected hostname not to be <null>
because sem KC_HOSTNAME o link do convite sai com http://keycloak:8080.") e o roteiro falhou com o link em
`http://keycloak:8080` (linha 3 da tabela).

**Observações das mutações que não ficam vermelhas por desenho** (Tarefas 8 e 11): sem `briefRepresentation=false`,
só o teste de forma da URI fica vermelho — o `GET /users` da 26.7.4 já devolve a representação completa; sem o
`IsConcurrencyToken()` do `xmin`, a entrega concorrente continua falhando pelo índice único de `members`, e só o
teste de atomicidade fica vermelho.

**Não registrado:** os `EventId` do log da API durante o roteiro (o handoff da fatia B os trazia). A sequência foi
confirmada pelos horários do `POST` e do `Active` e pelo e-mail no mailpit. A demonstração do README — com o Keycloak
parado e religado — não foi executada nesta fatia; a parte do mailpit dela é o mesmo roteiro da tabela acima.

## Decisões tomadas durante a execução

| Decisão | Custo se errado |
|---|---|
| T1–T12: ajustes pedidos pelos analisadores sob `TreatWarningsAsErrors` — `var` no lugar do tipo explícito (`IDE0007`), campos `static readonly` no lugar de arrays constantes (`CA1861`), `MemberRepository` concreto num teste (`CA1859`), `using var` no `JsonDocument` | Nenhum comportamental |
| T6: o SMTP do `KeycloakFixture` (`SMTP_HOST=mailpit`, `SMTP_PORT=1025`, `SMTP_FROM=convites@identity-gateway.test`) entrou na Tarefa 6, antes do mailpit da Tarefa 7, porque o `${SMTP_FROM}` literal derruba o import (acima) | Nenhum: a Tarefa 7 encontrou as linhas prontas |
| T4: a mutação do índice único trocada por tirar só o `IsUnique()` (acima) | Nenhum: a unicidade continua provada |
| T8: `UsuarioDoTenant` com `$$$` no lugar do `$$` do plano, que não compila (`CS9007`); `InviteDataTests` com `.NotContain("Email")`, sem o qual o teste passava com o `ToString` gerado; o corpo da criação comparado por `JsonDocument`, porque o encoder escapa o `+` | Nenhum: as asserções ficaram mais fortes |
| T8: a mutação 7 do plano reescrita para introduzir o defeito que descreve (acima) | Uma mutação mal escolhida, pega na revisão |
| T8 (correção): o teste do "nunca em laço" ganhou um `500` a partir do segundo `POST /users`, para ficar vermelho em segundos em vez de pendurar a CI | Nenhum |
| T9: testes do handler com `TheoryData` de delegates rotulados, sem `switch` nem string mágica; comentários de "porquê" restaurados em `TenantTests` | As linhas das theories não são enumeradas na descoberta do xUnit (delegates não serializáveis); a execução e o relatório por linha funcionam |
| T10: o teste de spans usa `ParentBasedSampler(AlwaysOffSampler)` e uma raiz de rastro por teste, porque o `ActivityListener` é do processo e os testes paralelos contaminavam a coleção (`Collection was modified`) | Um span emitido fora do fluxo `await` do teste não é verificado; hoje o provisionamento não faz nada destacado |
| T10: a redação da query pelo .NET 9+ é o que tira o `?email=` dos logs e spans do `HttpClient`; o teste de vazamento a fixa (mutações com a redação desligada) | Nenhum enquanto o teste existir |
| T11: correções de produção numa tarefa de testes, os `EventId` 2214 e 2215 fora da faixa, e o teste de atomicidade refeito pelo `xmin` (acima) | Duas releituras a mais num caminho raro |
| T12: `[GeneratedRegex]` nos testes do compose; `| tr -d '\r'` também na CI; referências ao plano tiradas dos comentários do compose e da CI | Nenhum |
| Timeouts do `smtpServer`: as chaves `connectionTimeout`, `timeout` e `writeTimeout` foram confirmadas em `DefaultEmailSenderProvider.java` L149-151 da 26.7.4, o que fecha a pendência da §3.4 do design | Nenhum: são as chaves que o provider lê |
| T13: o README ganhou `| tr -d '\r'` nos dois `jq` da demonstração, o ajuste que a verificação ao vivo registrou | Nenhum no Linux |

## Pendências

**Para o autor decidir:**
- **A prova por mutação da guarda `HttpSoEmDesenvolvimento`** — roteiro no topo deste handoff.
- **(Resolvido na revisão final.)** A mensagem do `KeycloakHealthCheck` prescrevia `docker compose down -v` em
  qualquer ambiente e citava "fatia C". Virou uma descrição neutra: o token sem `manage-users` em
  `resource_access.realm-management.roles`, com a indicação de que, em desenvolvimento local, o realm só é importado
  na primeira subida do compose (ver o README).
- **A API (`8080`) e o Jaeger (`16686` e `4317`) continuam publicados em todas as interfaces** no compose. Postgres,
  Redis, Seq, Keycloak e mailpit ficaram só em `127.0.0.1`; a API e o Jaeger ficaram fora do escopo da fatia. Em falha,
  os traces do Jaeger podem carregar a URL de uma chamada; hoje a query `?email=` sai redigida (acima).

**Registradas na v2.6 (§19), que continuam abertas:**
- **E-mail duplicado**, em qualquer falha entre o envio e o commit — provado pelo E2E de commit perdido (exatamente
  dois e-mails) — **e também numa entrega concorrente**, porque as duas entregas enviam antes de qualquer commit.
- **Corrida residual no `POST` de papel:** a perdedora recebe 400, transitório; converge no ciclo seguinte, sem
  `ProvisioningFailed`. Correção no molde da do vínculo.
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

**Correção do design da fatia (`2026-09-29-convite-admin-inicial-design.md`, §5.1):** a linha "Integração,
PostgreSQL" ainda diz que a linha pré-inserida em `members` faz falhar o último comando do commit. A v2.6 (§13) e
este handoff registram a ordem real; o design fica como estava, como registro do que se planejou.

**Documento de negócio:** ainda descreve o transporte do provisionamento pelo RabbitMQ e o retry pelo MassTransit
(fluxo 9.1: o participante `MQ` do diagrama, os atores e a linha "Keycloak (durante os três passos)" da tabela de
falhas), que a v2.5 já tinha trocado pelo transporte em processo. Fora do escopo da fatia C; as seções da v2.6 que
ainda citam MassTransit ou RabbitMQ fora do que a fatia tocou também ficaram como estavam.

**Pendências menores levantadas nas revisões das tarefas** (nenhuma bloqueante):
- *Regras de valor:* as regexes de `Email` e de `RoleName` usam `$` em vez de `\z` (`RoleName` aceita
  `"tenant-admin\n"`; o `Email` só é seguro pela ordem trim-antes-do-regex) — conferir também o slug; `ExternalUserId`
  sem o limite de 255 da coluna.
- *Cobertura ausente:* rótulo de domínio com 64 caracteres recusado; a FK `Restrict` de `members` → `tenants`;
  pré-condição de coluna preenchida em `Ativacao_ApagaAColunaDoEmail`; catálogo com `maxClients` 0 aceito e −1
  recusado; nenhum teste trava os seis nomes de `MemberStatus`; `Member_NaoTemConstrutorNemFabricaPublicos` não pega
  fábrica pública que devolva `Result<Member>` ou `Task<Member>`; nenhum teste prova que a reconsulta depois do `409`
  é **por username**; `NossoUsuarioDesabilitado_…` não distingue 400 no envio de `tenant_id` perdido; falha fechada do
  health check sem `IHostEnvironment` registrado; caminho `403` da leitura da pertença; nenhuma mutação isola o canal
  `outbox_messages.error`; o `GET` de provisioning do teste funcional sem asserção do `200`; `unmanagedAttributePolicy:
  ENABLED` só provada na arquitetura.
- *Testes que podem ficar mais estritos:* a rodada concorrente afirma "no máximo um commit" (passa com zero) — trocar
  por exatamente um; em 3 cenários do vazamento, a pré-condição de log ou span pode ser satisfeita pelo `POST /users`
  da própria fixture — exigir `/users?`; as formas procuradas não pegam o e-mail escapado em JSON (`+`); a
  asserção de log por ramo do handler não fixa o `EventId`; a regex de `RegrasDoAmbienteLocalTests` com `\s*`
  atravessa quebra de linha, e a recusa de portas só pega a forma entre aspas; `NenhumaChaveDeCredencial` não percorre
  o JSON embutido em `kc.user.profile.config`.
- *Robustez:* no `KeycloakHealthCheck`, `EnumerateArray`/`GetString` podem lançar fora do filtro do `catch` (continua
  fechado, perde a descrição); o esquema do `PublicBaseUrl` é julgado por `Uri` num lugar e por `StartsWith` no outro;
  se a leitura da pertença falhar, o 400 original some do diagnóstico; os passos da CI rodam sem `pipefail` (o
  `tr -d '\r'` mascara falha de `jq`/`curl` — diagnóstico errado, sem verde falso).
- *Higiene:* comentários de código e de teste que citam artefatos do plano ("Tarefa 7", "Foco de revisão 3/5",
  "fatia C", inclusive na mensagem do health check) — trocar pelo nome da coisa; o comentário de
  `TimeoutDaResilienciaDepoisDaJanela_MarcaFailed` perdeu o porquê (`TimeoutRejectedException` não deriva de
  `OperationCanceledException`); rótulos do `TheoryData` misturam idioma; linha em branco sobrando no fim do JSON do
  realm e apagada em `RegisterTenantValidatorTests`; asserts redundantes em `KeycloakRealTests`; a regex `LinkDeAcoes`
  do fixture repete host e realm das constantes; `TokenDeUsuarioComumAsync` cria um client por chamada e não o remove;
  linhas longas de `Tenant.Register(...)` nos testes de integração; o cabeçalho do compose ("duas dependências e duas
  ferramentas") está desatualizado desde o Keycloak.

## Próximo passo

1. Revisão final da branch inteira, com a lista de pendências menores acima.
2. Autorizar o push e abrir o PR contra `main`, com o aviso de `docker compose down -v` no corpo.
3. Acompanhar a CI (`gh pr checks <n>`) — `Build`, `Testes`, `Imagem Docker` e `Compose`, que agora confere o
   e-mail no mailpit e abre o link. Se algo falhar, corrigir na mesma branch.
4. Mesclar. Depois: `git checkout main && git pull --ff-only`, apagar a branch local e a remota, e acrescentar o
   número do PR na linha da fatia C da §16 da v2.6.
5. Escolher a próxima fatia do M1 — suspensão, encerramento, reconciliação (que precisa tratar o admin órfão) ou o
   retry manual de `ProvisioningFailed` (que recebe o e-mail de novo) — e abrir o brainstorming.

## Como retomar

Docker Desktop costuma estar desligado ao abrir a sessão: sem ele, os testes de integração e funcionais falham com
`DockerUnavailableException` (ambiente, não regressão). Quem já tinha o compose de pé antes desta fatia precisa de
`docker compose down -v` uma vez.
