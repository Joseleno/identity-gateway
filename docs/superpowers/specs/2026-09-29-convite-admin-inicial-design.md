# Fatia C — Convite do admin inicial

> **Data:** 2026-09-29 · **Marco:** M1 · **Status:** design aprovado em conversa, seção por seção. A spec escrita
> passou por uma revisão de seis especialistas (arquitetura, segurança, Keycloak, testes, DevOps e coerência
> documental), e os achados estão incorporados abaixo (§11). Aguarda a revisão do autor.
> **Referência normativa:** [`especificacao-arquitetural-v2.5.md`](../../especificacao-arquitetural-v2.5.md). Esta
> fatia produz a **v2.6**, com as mudanças listadas na §8.
> **Sucede:** [handoff do consumidor do provisionamento](2026-09-26-consumidor-provisionamento-handoff.md) (fatia B,
> PR #3, mesclado em 2026-09-29) e o PR #4 (serviço `migrate` no compose, mesclado em 2026-09-29).

---

## 1. Por que esta fatia existe

A fatia B provisiona o tenant sem o segundo passo da §9.1: garante a Organization e marca `Active`, mas ninguém é
convidado. O tenant nasce **trancado**. `POST /tenants` é `platform-admin`, convidar membros exige `tenant-admin`
**daquele tenant**, e esse admin não existe. O `initialAdminEmail` é validado no `POST` e descartado.

Esta fatia fecha o provisionamento da §9.1: garante a Organization, garante o convite do `initialAdminEmail` com o
papel `tenant-admin` e só então marca o tenant `Active`, com a vaga do admin ocupada e o `Member` criado. A
reconciliação, que a §9.1 também descreve, continua pendente.

**Critério de sucesso:** com o compose de pé, `POST /tenants` → `202` → o tenant chega a `Active` → o e-mail de
convite aparece no mailpit, com um link que abre no navegador (`http://localhost:8081/...`). O convidado define a
senha e informa nome e sobrenome (D16). A demonstração do Keycloak fora do ar continua valendo: o convite sai quando
ele volta.

## 2. Decisões

| # | Decisão | Alternativa descartada, e por quê |
|---|---|---|
| D1 | **O e-mail fica temporariamente no tenant** (`InitialAdminEmail`, coluna anulável), fora do evento. É apagado na transação que ativa **ou** que marca `ProvisioningFailed` (D13). Exceção explícita e limitada à regra "dados pessoais só no Keycloak": só existe em tenant `Pending` | Cifrado em repouso: gestão de chave para um dado que vive horas. Tabela própria: segunda fonte de estado do provisionamento. No evento: levaria o e-mail ao Outbox e, com o broker, ao RabbitMQ. Convite síncrono no `POST`: quebra o `202` com o Keycloak fora do ar |
| D2 | **Ativar e reservar a vaga numa operação de domínio só, num commit só.** No Keycloak, o convite acontece antes da ativação; no domínio, numa operação: `Active` → vaga → `Member` em `Invited` → e-mail apagado | `ReserveSeat` aceitar `Pending`: enfraquece a invariante para todo chamador futuro. Admin fora do contador: membro grátis e caso especial eterno nas vagas, no downgrade (N8) e na expiração |
| D3 | **Convite pela Admin API: criar o usuário, vincular à Organization, atribuir o papel e enviar `execute-actions-email`** (§3.1) | `invite-user` nativo de Organization: para e-mail novo, o usuário só passa a existir no registro, e o `Member` nasceria sem `sub` |
| D4 | **O convidado nasce habilitado**, com as ações obrigatórias `UPDATE_PASSWORD` e `VERIFY_EMAIL`. Corrige a §9.9 | Nascer desabilitado, como a §9.9 dizia: o Keycloak recusa usuário desabilitado no envio e no clique (§3.1) |
| D5 | **E-mail já em uso por outra conta é falha permanente.** O usuário é correlacionado pelo atributo de usuário **`tenant_id`**, o mesmo que a §12.2 já define como fonte do claim. Com o `tenant_id` deste tenant, é uma tentativa anterior e é reaproveitado; senão, `IdentityProviderInconsistencyException` e `ProvisioningFailed` | Reaproveitar usuário sem Organization: poderia entregar o tenant ao platform-admin ou a uma conta abandonada. Recusar no `POST`: exige o Keycloak no ar e ainda precisaria desta regra. Um segundo atributo só para correlação (`gateway_tenant_id`, na primeira versão deste design): duplicaria o `tenant_id` da §12.2, que precisa ser gravado de qualquer forma |
| D6 | **O service account recebe `manage-users`.** O vínculo à Organization exige `manage-organizations` **e** `manage-users` (§3.1). O alcance real fica nomeado na §19 (§3.4), e um teste garante que os papéis **efetivos** do service account, com compostos expandidos, são exatamente esses dois | Permissões granulares v2: "só os usuários das Organizations da Gateway" não é um recorte direto, e seria outra premissa externa. O M2 precisa de `manage-users` de qualquer forma |
| D7 | **E-mail pelo SMTP do Keycloak, com mailpit em desenvolvimento e na CI. O notification-hub fica para depois** (§3.3) | Extensão Java no Keycloak chamando o hub: SPI interno, sem garantia entre versões. Entrada SMTP no hub dentro desta fatia: mudança grande em outro repositório bloqueando esta |
| D8 | **Endereço público separado do transporte.** `KC_HOSTNAME` público no Keycloak. Na Gateway, `Keycloak:Admin:PublicBaseUrl` alimenta **só** o `aud` do assertion; o token endpoint e a Admin API continuam no `BaseUrl` | Sem `KC_HOSTNAME`: o link sai com o host interno (§3.2). `frontendUrl` no realm: muda o mesmo emissor, sem o backchannel dinâmico, e o emissor ficaria preso ao JSON versionado (o SMTP também vem do ambiente, mas não define identidade de token) |
| D9 | **O prazo do link vem de uma política de convite** (`IInvitationPolicy.LinkLifetime`, padrão 7 dias, alinhado à §9.9) e é passado ao Keycloak em cada chamada. Nesta fatia, o valor é global; o prazo por tenant da §9.9 chega no M2, pela mesma porta | O padrão do realm (12 h): desalinhado do ciclo do convite. Prazo por tenant agora: não há onde configurá-lo antes do M2 |
| D10 | **Os atributos `tenant_id` do usuário são declarados no User Profile do realm, com `view` e `edit` só para `admin`.** A `unmanagedAttributePolicy` fica desligada, e `ENABLED` fica proibido | Sem declarar: o Keycloak descarta o atributo em silêncio (§3.1). Com `unmanagedAttributePolicy: ENABLED`, o conserto que qualquer busca sugere: o próprio usuário passaria a editar o `tenant_id` pela account console, e sequestraria a correlação e o isolamento (§3.4) |
| D11 | **O papel vai na porta** (`InviteData.Role`, value object `RoleName`), e o handler passa `tenant-admin` | Papel fixo no adaptador: regra de negócio escondida na Infrastructure, e o M2 convida com outros papéis |
| D12 | **O passo 5 (enviar o e-mail) só acontece se o usuário ainda tiver `UPDATE_PASSWORD` pendente** | Enviar sempre: um retry depois do aceite mandaria a um admin já ativo um link que troca a senha dele (§3.4) |
| D13 | **`MarkProvisioningFailed` também apaga o e-mail.** O retry manual, quando existir, recebe o e-mail de novo no corpo, e isso também permite corrigir um e-mail digitado errado | Guardar o e-mail em `ProvisioningFailed`: retenção sem prazo, porque esse estado não tem saída automática (§19) |
| D14 | **Um tenant sem vaga livre falha antes de tocar o Keycloak.** O handler verifica a vaga antes do convite e marca `ProvisioningFailed` | Deixar a falha para `CompleteProvisioning`: o `Plan` é gravado no tenant, dado antigo escapa da validação do catálogo, e cada retry reenviaria o convite até esgotar a janela |
| D15 | **O e-mail não vaza por log, exceção, resposta nem coluna de erro, inclusive em Development** (§4.8) | Confiar num teste só da Application: não enxerga o EF, o `HttpClient`, o `OutboxProcessor` nem o `ExceptionHandlingMiddleware` |
| D16 | **O convidado informa nome e sobrenome no primeiro acesso**, pela ação `VERIFY_PROFILE` que o perfil padrão já exige | Tornar os dois opcionais no perfil: mudança de perfil sem necessidade; o nome é dado pessoal que o próprio usuário informa ao Keycloak, que é o lugar dele |

## 3. Fatos externos verificados

Verificados em 2026-09-29 lendo o código-fonte do Keycloak na tag **26.7.4** (commit `aa9fe3fb`), primeiro pelos
analistas e depois conferidos pelos revisores de Keycloak e de segurança. É leitura de código: nada foi exercitado
num container. Os testes de integração da §5 são a prova em execução.

### 3.1. Convite e usuário

- `PUT /users/{id}/execute-actions-email` recusa usuário **desabilitado** com `400 User is disabled` e usuário sem
  e-mail com `400 User email missing` (`UserResource.java` L1302-1308). No clique, `checkIsUserValid` recusa usuário
  desabilitado (`LoginActionsServiceChecks.java` L131-133), e nada no fluxo o habilita. **A §9.9 estava errada.**
- O `lifespan` é por chamada, em segundos. O padrão é `actionTokenGeneratedByAdminLifespan` do realm, 12 h
  (`UserResource.java` L1332-1334). Sem `client_id`, o link leva ao client `account`. O envio exige SMTP, e a falha
  vira `500` (`UserResource.java` L1073-1075).
- `invite-user` de Organization **não cria usuário** para e-mail novo: o usuário nasce no registro
  (`OrganizationInvitationResource.java`; `RegistrationUserCreation.java` L154-160).
- `POST /users` devolve o id no `Location` do `201` (`UsersResource.java` L180). Username ou e-mail repetido dá
  `409` (`UserResource.java` L285-286; `UsersResource.java` L181-182). Username igual ao e-mail é aceito.
  Ações obrigatórias desconhecidas são **ignoradas em silêncio** no `POST` (`UserResource.java` L307-321).
- **Atributo não declarado no User Profile é descartado em silêncio** no `POST /users`. Com
  `unmanagedAttributePolicy` nula, que é o padrão e o que o realm tem hoje, só atributos do perfil são gravados
  (`DefaultAttributes.java` L406-412, L500-506). O perfil padrão declara só `username`, `email`, `firstName` e
  `lastName` (`keycloak-default-user-profile.json`). O perfil é declarável no JSON do realm, em `components`, com
  o provider `declarative-user-profile` e a chave `kc.user.profile.config` (`DefaultExportImportManager.java`
  L466-469; `DeclarativeUserProfileProvider.java` L69, L434).
- No perfil padrão, `firstName` e `lastName` são obrigatórios para o papel `user`, e o `VERIFY_PROFILE` os pede
  depois da senha (`VerifyUserProfile.java` L58-69).
- `GET /users?email=...&exact=true` compara por igualdade, em minúsculas, e inclui desabilitados; sem `exact`, vira
  `LIKE %x%` (`JpaUserProvider.java` L1284-1291). A forma resumida não traz atributos
  (`DefaultUserProfile.java` L237-241), então a busca passa `briefRepresentation=false`.
- `POST /organizations/{id}/members` aceita usuário desabilitado, responde `201` e dá `409` se ele já for membro
  (`JpaOrganizationProvider.java` L198-230). Exige **as duas** permissões: `manage-organizations` e
  `manage-users` (`OrganizationMemberResource.java` L106, L110).
- **Atribuir papel de realm exige o id do papel** no corpo (`RoleMapperResource.java` L268-270). Ler o papel por
  `GET /roles/{nome}` exige `view-realm` (`RoleContainerResource.java` L262; `RolePermissions.java` L396-398). Os
  endpoints do próprio usuário (`GET /users/{id}/role-mappings/realm` e `.../realm/available`) exigem só a
  visão de usuários, que `manage-users` cobre (`UserResource.java` L735-736). **Reatribuir é inofensivo**
  (`UserAdapter.java` L538-539).
- `VERIFY_EMAIL` é redundante, mas inofensivo: o clique já marca o e-mail como verificado
  (`ExecuteActionsActionTokenHandler.java` L120-122; `VerifyEmail.java` L105-117).

### 3.2. Link e emissor

- O link é montado a partir da URL de **frontend** (`UserResource.java` L1056; `HostnameV2Provider.java`
  L107-124). Sem `KC_HOSTNAME` e com `start-dev`, ela é a URL da requisição de admin, e o link sai com
  `http://keycloak:8080`. Trocar o host à mão não funciona: o clique é validado contra o emissor gravado no token
  (`LoginActionsService.java` L620-623).
- Com `KC_HOSTNAME=http://localhost:8081` e `KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true`:
  - o link, o `iss` e o issuer do discovery usam `localhost:8081`, mesmo com a requisição chegando por
    `keycloak:8080`;
  - a Admin API continua respondendo por `keycloak:8080`, sem redirecionar (`AdminRoot.java` L182-206);
  - o `aud` do client assertion precisa ser o emissor público, comparado por igualdade exata de texto. Com
    `keycloak:8080`, e também com `127.0.0.1:8081`, o Keycloak responde `Invalid token audience`
    (`JWTClientValidator.java` L29-38; `AbstractBaseJWTValidator.java` L126-133).
- Hoje o `KeycloakAdminOptions.Issuer` alimenta ao mesmo tempo o `aud` (`ClientAssertionFactory`) e a URL do
  token (`KeycloakTokenClient.cs:49`). Trocar só o `Issuer` mandaria a chamada do token para `localhost:8081` de
  dentro do container.
- **Consequência:** a regra da v2.5 (§10.2) "em produção, o `BaseUrl` precisa ser igual ao `KC_HOSTNAME`" só vale
  se a Gateway chamar o Keycloak pelo endereço público. A regra certa: o `aud` é o emissor público, e o transporte
  pode ser o endereço interno.

### 3.3. Transporte do e-mail e o notification-hub

- O Keycloak só envia e-mail por SMTP (`DefaultEmailSenderProvider.java` L89). Trocar o transporte exige um
  provider num **SPI interno** (`EmailSenderSpi.isInternal() == true`).
- **Nenhum endpoint devolve o link sem enviar o e-mail**, e o campo que o guardava deixou de ser exposto
  (`OrganizationInvitationRepresentation.java` L91-105). A Gateway não pode gerar o token, que é assinado com as
  chaves do realm (`DefaultActionToken.java` L172).
- O notification-hub (`../notification-hub`) só recebe pedidos por HTTP, sem entrada SMTP.
- **Conclusão:** para o convite sair pelo hub, ou o hub passa a aceitar SMTP, ou o Keycloak ganha uma extensão
  Java. Ambas ficam fora desta fatia (D7). Com o SMTP vindo do ambiente, plugar o hub depois é trocar
  configuração, sem código na Gateway.
- Placeholders no JSON do realm são substituídos no texto inteiro antes do parse, e variável ausente fica
  **literal**, sem erro (`AbstractFileBasedImportProvider.java` L33-45; `StringPropertyReplacer.java` L66-69).

### 3.4. Segurança

- **Link não usado continua válido depois do aceite.** Cada link é de uso único, mas os outros links emitidos para
  o mesmo usuário continuam valendo até expirar, sem revogação por usuário (`DefaultActionToken.java` L54-60;
  `LoginActionsService.java` L708-711). Um link duplicado permite trocar a senha do admin por até 7 dias. O D12
  impede o envio depois do aceite, mas não invalida um duplicado enviado antes.
- **Alcance do `manage-users`:** o service account atribui qualquer papel que não seja de administração, inclusive
  `platform-admin`, e também os papéis de administração que ele mesmo tem (`RolePermissions.java` L154-156,
  L322-323). Troca senhas e desabilita usuários, inclusive platform-admins. Quem tiver a chave da Gateway pode
  criar uma conta própria com esses papéis, e o acesso sobrevive à rotação da chave.
- O link abre primeiro uma página de confirmação numa sessão nova (`LoginActionsService.java` L92-106), o que
  neutraliza a pré-busca de scanners de e-mail.

**Pendente de verificação no plano:** o nome das chaves de timeout do `smtpServer` na 26.7.4, para manter o envio
abaixo do `AttemptTimeout` da Gateway (§4.5).

## 4. Design

### 4.1. Componentes e fluxo

```
POST /tenants ──► RegisterTenantHandler ──► Tenant.Register(..., initialAdminEmail)   [e-mail na coluna, não no evento]
                                              │
                           Outbox (tenant-registered: TenantId + Slug)
                                              │
                                              ▼
                               ProvisionTenantHandler
                                 0. sem e-mail ou sem vaga ──► MarkProvisioningFailed (sem tocar o Keycloak)
                                 1. EnsureOrganizationAsync          ──► Keycloak: Organization
                                 2. EnsureInvitedUserAsync           ──► Keycloak: usuário, vínculo, papel, e-mail
                                 3. tenant.CompleteProvisioning(...) ──► Active + vaga + Member(Invited) + e-mail apagado
                                              │
                                        um commit só
```

### 4.2. Domínio

**`Tenant`**
- `InitialAdminEmail` (`Email?`, value object existente, `private set`): exigido por `Register`. O evento
  `TenantRegistered` **não** muda.
- `CompleteProvisioning(string externalOrganizationId, ExternalUserId adminUserId, DateTimeOffset invitedAt)`
  devolve o `Member`. **Valida tudo antes de mudar qualquer coisa:** exige `Pending` e vaga livre. Depois, aplica
  numa operação: `Active` com o id da Organization, `TenantActivated`, a vaga (por um método privado que não
  exige `Active`, porque `ReserveSeat` continua exigindo), o `Member` em `Invited` e o e-mail apagado.
  - Recusa `Active` e `ProvisioningFailed`. É uma **inversão** em relação ao `MarkProvisioned`, que aceitava os
    dois. Os testes de `MarkProvisioned` não migram um a um: os de aceitação desses estados passam a ser de
    recusa.
  - Falta de vaga aqui é `DomainInvariantViolation`, porque o handler já verificou antes (D14).
  - `MarkProvisioned` sai. A documentação de `MarkProvisioningFailed`, que o cita, é atualizada.
- `HasSeatAvailable` (leitura), para o handler verificar a vaga antes de tocar o Keycloak (D14).
- `MarkProvisioningFailed` passa a apagar `InitialAdminEmail` (D13).

**`Member`** (aggregate root novo, no mínimo que o convite do admin exige)
- `Id` (`MemberId`: `readonly record struct` com `Guid.CreateVersion7()`, como `TenantId`, porque os
  ArchitectureTests reprovam `Guid` cru como identidade de raiz), `TenantId`, `ExternalUserId` (o `sub`), `Status`
  (tipo `MemberStatus`, com os seis valores da §6.1, persistido como texto) e `InvitedAt` (UTC).
- O papel vive no Keycloak (ADR-005) e não é duplicado no `Member` **nesta fatia**. A §6.1 prevê "papéis do
  catálogo global" no `Member`, e essa decisão fica para o M2, com `PUT .../roles`.
- A fábrica é `internal` ao Domain e só `Tenant.CompleteProvisioning` a chama, para que nenhum membro nasça sem a
  vaga reservada. Ela valida `TenantId` e `ExternalUserId` não vazios e normaliza `InvitedAt` para UTC. Um teste de
  arquitetura garante que o `Member` não tem construtor nem fábrica públicos.
- Nenhum evento de domínio. `MemberInvited` ainda não teria consumidor. `TenantActivated` também não tem, mas
  existe desde o PR #1 e é mapeado para `null` pelo roteador do Outbox; não se cria outro evento assim.

**Value objects novos**
- `ExternalUserId`: o `sub`, como a §11.3 já nomeia.
- `RoleName`: nome de papel do catálogo. Nesta fatia, só `tenant-admin` é usado.

### 4.3. Porta e adaptador do Keycloak

```csharp
Task<ExternalUserId> EnsureInvitedUserAsync(
    string organizationId, TenantId tenantId, InviteData invite, CancellationToken cancellationToken);

public sealed record InviteData(Email Email, RoleName Role, TimeSpan LinkLifetime)
{
    // O ToString gerado do record imprimiria o e-mail (D15).
    public override string ToString() => $"InviteData {{ Role = {Role}, LinkLifetime = {LinkLifetime} }}";
}
```

A assinatura diverge da §11.3 (`EnsureInvitedUserAsync(organizationId, InviteData, ct)`): acrescenta o `TenantId`,
que é a chave da correlação. É o mesmo padrão do item I1 da v2.5 para `EnsureOrganizationAsync`.

Mesmo contrato de erro de `EnsureOrganizationAsync`: só `IdentityProviderInconsistencyException` é permanente, e o
resto é infraestrutura. Nenhuma exceção carrega o e-mail na mensagem; elas levam o `tenantId`. Os passos no
`KeycloakIdentityProvider`, cada um idempotente:

1. **Buscar.** `GET /users?email=<e-mail escapado>&exact=true&briefRepresentation=false`.
   - Achou um usuário com `tenant_id` igual a este tenant: é o nosso, e segue para o passo 3.
   - Achou um usuário sem esse valor: `IdentityProviderInconsistencyException`.
2. **Criar.** `POST /users` com username igual ao e-mail, o e-mail, `enabled=true`,
   `requiredActions=[UPDATE_PASSWORD, VERIFY_EMAIL]` e `attributes.tenant_id=[<tenantId>]`. O id vem do `Location`.
   - `409`: **uma** nova busca, por e-mail e por `username=<e-mail>&exact=true`. Se um resultado tiver o nosso
     `tenant_id`, segue com ele. Senão, `IdentityProviderInconsistencyException`. Nunca volta em laço: o `409`
     também sai quando outro usuário tem username igual ao nosso e-mail e outro e-mail.
3. **Vincular.** `POST /organizations/{id}/members` com o id. O `409` (já é membro) conta como sucesso.
4. **Papel.** `GET /users/{id}/role-mappings/realm`. Se o papel já estiver lá, o passo termina. Senão, busca o id
   em `GET /users/{id}/role-mappings/realm/available` e faz o `POST` com `{id, name}`. Se o papel não aparecer em
   nenhuma das duas listas, ele não existe no realm, e isso vira `IdentityProviderInconsistencyException`: o realm
   não é o que a Gateway espera, e repetir não corrige.
5. **E-mail (D12).** Se o usuário lido ou criado ainda tiver `UPDATE_PASSWORD` nas ações obrigatórias,
   `PUT /users/{id}/execute-actions-email?lifespan=<segundos>` com `[UPDATE_PASSWORD, VERIFY_EMAIL]`. Senão, o
   convite já foi aceito, e nada é enviado.
   - `400 User is disabled`: alguém desabilitou o nosso usuário à mão, e vira `IdentityProviderInconsistencyException`.
   - `500` (SMTP fora do ar): transitório.
   - Timeout: transitório, mas **não** significa "não enviado", porque o Keycloak envia dentro da requisição. O
     `PUT` fica fora do retry automático da resiliência (`DisableForUnsafeHttpMethods`), como o `POST`.

**O e-mail pode sair mais de uma vez.** Qualquer exceção depois do passo 5 e antes do commit faz a mensagem voltar:
falha de commit, conflito de concorrência (`xmin`), violação do índice único de `members`. A nova tentativa reenvia,
porque o usuário ainda tem `UPDATE_PASSWORD` pendente. É coerente com a entrega "pelo menos uma vez" do Outbox, e
fica na §19.

### 4.4. `ProvisionTenantHandler`

1. Carrega o tenant. Fora de `Pending`, encerra sem efeito (como hoje).
2. **Antes de tocar o Keycloak:**
   - `InitialAdminEmail` nulo, o que só acontece com tenant registrado antes desta fatia: `MarkProvisioningFailed`
     e log.
   - `!tenant.HasSeatAvailable`: `MarkProvisioningFailed` e log (D14).
3. No mesmo `try` de hoje, que passa a cobrir as duas chamadas: `EnsureOrganizationAsync`, depois
   `EnsureInvitedUserAsync(organização, tenant.Id, new InviteData(e-mail, RoleName.TenantAdmin, politica.LinkLifetime))`.
   A classificação não muda: inconsistência leva a `ProvisioningFailed`; qualquer outro erro, com a janela
   esgotada e sem cancelamento, também leva; senão, a exceção sobe e o Outbox tenta de novo.
4. `tenant.CompleteProvisioning(organização, sub, relogio.UtcNow)`; o `Member` devolvido vai para
   `IMemberRepository.Add`.
5. O commit do `TransactionBehavior` grava tudo junto.

### 4.5. Configuração

- **`IInvitationPolicy.LinkLifetime`** (Application), implementada por `Invitations:LinkLifetime` (padrão
  `7.00:00:00`). Validado na subida: positivo, em segundos inteiros e no máximo 30 dias. O teto é arbitrário e
  protege contra um link que valha, na prática, para sempre.
- **`KeycloakAdminOptions`** ganha duas propriedades distintas:
  - `AssertionAudience`: `{PublicBaseUrl ?? BaseUrl}/realms/{Realm}`, usada **só** como `aud` do assertion;
  - o token endpoint e a Admin API continuam derivados **só** do `BaseUrl`.
  - `PublicBaseUrl` é opcional, absoluta, sem query nem fragmento, e nunca é discada. Omitida, vale o `BaseUrl`,
    e nada muda para quem roda a Gateway pela IDE com `BaseUrl=http://localhost:8081`. Usar `127.0.0.1` no lugar
    de `localhost` quebra o `aud`, porque a comparação é por texto, e isso fica documentado.
  - `AllowInsecureHttp` passa a ser recusado fora de `Development`. Hoje ele não depende do ambiente, e "transporte
    interno" convidaria a usar `http` em produção com o bearer de `manage-users`.
- **Planos:** o catálogo recusa `maxUsers < 1` na subida. Isso reverte o "zero é permitido" do `Plan`, e o
  comentário do `Plan` é atualizado. O `Plan` continua aceitando zero, porque pode vir de dado antigo, e o
  handler trata esse caso (D14).
- **Timeout de SMTP do realm** abaixo do `AttemptTimeout` (10 s) da Gateway, com as chaves confirmadas no plano
  (§3.4).

### 4.6. Persistência

- `tenants.initial_admin_email` (`varchar(254)`, anulável).
- Tabela `members`:
  - colunas `id`, `tenant_id` (FK), `external_user_id`, `status` (texto), `invited_at` e os campos de auditoria do
    template;
  - índice único em `(tenant_id, external_user_id)`.
- `IMemberRepository` com assinaturas que exigem o tenant (§6.4). Nesta fatia, só `Add(Member)`, porque o `Member`
  carrega o `TenantId`. `GetAsync(TenantId, MemberId)`, `ListAsync(TenantId)` e o teste que proíbe a sobrecarga
  só por id ficam para o M2. `DbSet<Member>` é `internal`, como o de tenants.
- Uma migration só, só de expansão: a api antiga convive com o schema novo, e o serviço `migrate` roda antes da
  nova.

### 4.7. Realm, compose e CI

**Realm (`realm-identity-gateway.json`)**
- Papel de realm `tenant-admin`.
- `manage-users` no service account.
- Um componente de User Profile (`org.keycloak.userprofile.UserProfileProvider`, `declarative-user-profile`) que
  declara o atributo `tenant_id`, com `view` e `edit` só para `admin`, sem `unmanagedAttributePolicy` (D10). O
  texto da configuração não usa `${...}`, que o import substituiria.
- `smtpServer` com placeholders puros (`${SMTP_HOST}`, `${SMTP_PORT}`, `${SMTP_FROM}`), sem o prefixo `KC_`, que o
  Keycloak lê como opção dele. `auth`, `ssl` e `starttls` como `"false"` literais.
- `resetPasswordAllowed: false` explícito: "esqueci a senha" daria acesso ao convidado habilitado fora do ciclo do
  convite.
- `adminEventsEnabled: true`, para que atribuições de papel pelo service account fiquem registradas (§10.3).
- **`RegrasDoRealmTests`** muda:
  - `components` deixa de ser chave proibida em absoluto. Passa a ser aceito só com o provider de User Profile, e
    provedores de chave continuam proibidos, que era o motivo da regra.
  - Novas regras: o atributo `tenant_id` declarado só com `admin`; nenhuma `unmanagedAttributePolicy`;
    `resetPasswordAllowed` falso; o service account com exatamente `manage-organizations` e `manage-users`, em
    qualquer ordem.

**Compose**
- `mailpit` com versão fixada e registrada em comentário, como o `seq`. SMTP só na rede interna, sem porta
  publicada. Interface e API em `127.0.0.1:8025`. Healthcheck pelo `readyz` do próprio mailpit, confirmado na versão
  fixada.
- `keycloak`:
  - `depends_on: mailpit` saudável;
  - `KC_HOSTNAME=http://localhost:8081` e `KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true`;
  - `SMTP_HOST`, `SMTP_PORT` e `SMTP_FROM`, com `test -n` no entrypoint, como o `test -s` do certificado, para não
    gravar placeholder literal.
- `api`: `Keycloak__Admin__PublicBaseUrl=http://localhost:8081`.
- A porta `8081` aparece em três lugares (porta publicada, `KC_HOSTNAME` e `PublicBaseUrl`). Um teste de
  arquitetura lê o `docker-compose.yml` e exige que os dois últimos sejam iguais.
- Postgres, Redis e Seq passam a publicar só em `127.0.0.1`: agora há dado pessoal no banco e, em falha, nos logs.
- Os comentários sobre "`BaseUrl` igual ao `KC_HOSTNAME`" são corrigidos.

**Quem já tem volumes precisa de `docker compose down -v`.** O import só roda na primeira subida, e num volume
antigo faltam o papel, o `manage-users`, o User Profile e o SMTP. Para isso falhar alto, e não virar 24 h de retry:
- o `KeycloakHealthCheck` confere se o token do service account traz `manage-users` em
  `resource_access.realm-management.roles`. Se não trouxer, responde `503` com uma mensagem que manda rodar
  `docker compose down -v`;
- o README, o corpo do PR e o handoff avisam, e recomendam `docker compose up -d --build`.

**Job `Compose` da CI**, depois do `Active`:
1. Poll curto (10 × 2 s) em `GET /api/v1/search?query=to:"<e-mail>"` do mailpit, exigindo **ao menos uma**
   mensagem, nunca exatamente uma.
2. Lê o campo `Text` da mensagem (o HTML traz `&amp;`) e extrai o link.
3. Exige o prefixo completo `http://localhost:8081/realms/identity-gateway/login-actions/action-token?key=`.
4. Faz `GET` no link e exige `200` sem a página de erro nem a de expirado. Isso prova o critério de sucesso da §1,
   e não só o formato do link.
5. Em falha, grava também a listagem do mailpit.

O e-mail e o slug são únicos por execução (`admin+<timestamp>@acme.test`), por causa do D5. A segunda subida
continua provando que a Gateway autentica.

### 4.8. O e-mail fora de logs, exceções e respostas (D15)

- `Email.ToString()` não devolve o endereço; quem precisa do valor usa `Email.Value`. `InviteData` sobrescreve o
  `ToString` (§4.3).
- `DomainErrors.Email.Invalido` deixa de ecoar o valor.
- As exceções do adaptador levam o `tenantId`, nunca o e-mail. Isso importa porque o `OutboxProcessor` grava a
  mensagem da exceção em `outbox_messages.error`.
- Em Development, o compose desliga o `EnableSensitiveDataLogging` e sobe a categoria
  `Microsoft.EntityFrameworkCore` para `Warning`. Hoje ele registraria os parâmetros de um comando que falha e, no
  caminho feliz, o valor antigo da coluna ao apagá-la.
- A query `?email=` fica fora dos logs do `HttpClient`. A redação da query pelo `HttpClientFactory` e pelo OTel não
  foi verificada neste repositório, e o teste da §5 decide.
- Nenhum read model nem resposta expõe `InitialAdminEmail`, inclusive o `GET .../provisioning`.
- O validador do `POST` e `Email.Of` passam a usar a mesma regra. Hoje o validador aceita `a@b` e o `Email.Of`
  recusa.

## 5. Testes

### 5.1. Por nível

| Nível | O que prova |
|---|---|
| Domínio | `CompleteProvisioning`: ativa, reserva **exatamente uma** vaga, cria o `Member` com `sub` e `InvitedAt` em UTC, apaga o e-mail e levanta `TenantActivated` uma vez. Recusa `Active` e `ProvisioningFailed`. Sem vaga, lança **sem ter mudado nada**. `MarkProvisioningFailed` apaga o e-mail. `Register` exige o e-mail, e `TenantRegistered` continua sem ele. `Email.ToString()` não contém o endereço. `Plan` com `maxUsers` zero é aceito; `HasSeatAvailable` é falso |
| Arquitetura | O `Member` sem construtor nem fábrica públicos; `MemberId` tipado; as regras novas do realm (§4.7); `KC_HOSTNAME` igual ao `PublicBaseUrl` no compose |
| Application | Cada linha de erro da §4.3 e da §4.4, vindo de **cada uma** das duas chamadas ao Keycloak. Depois de falha transitória no convite, o tenant continua `Pending` e com o e-mail. Sem e-mail e sem vaga: o Keycloak nunca é chamado. O `InviteData` recebe o papel `tenant-admin`, o prazo da política e a organização do passo 1. `CompleteProvisioning` recebe `relogio.UtcNow`. `IMemberRepository.Add` é chamado uma vez com o `Member` devolvido |
| Configuração | `Invitations:LinkLifetime` recusa zero, fração de segundo e mais de 30 dias. `PublicBaseUrl` omitido cai no `BaseUrl` no `aud`, e presente não muda o token endpoint. `AllowInsecureHttp` é recusado fora de Development. O catálogo recusa `maxUsers < 1` |
| Integração, Keycloak real | Detalhado na §5.2 |
| Integração, PostgreSQL | Migration, mapeamento do `Member` e índice único. **Atomicidade:** pré-inserir por SQL uma linha em `members` com o mesmo `(tenant_id, sub)` que o provedor devolverá. Isso faz falhar o **último** comando do commit, e o teste exige tenant `Pending`, e-mail preenchido e nenhum `tenant-activated` no Outbox |
| Funcional | O `POST` grava o e-mail normalizado em `tenants.initial_admin_email`, e o `content` do `tenant-registered` no Outbox **não** contém o e-mail. O caminho até `Active` não cabe aqui, porque a factory desliga o Outbox e o Keycloak, e fica no E2E de integração |
| E2E de integração (Postgres + Keycloak + mailpit) | `POST` → `Active`, com o `Member` gravado, `initial_admin_email` nulo, um usuário no Keycloak e o e-mail no mailpit. **Commit perdido:** depois do commit perdido, o segundo ciclo termina com um usuário, uma linha em `members` e **exatamente dois** e-mails para aquele destinatário, porque a §4.3 declara o duplo envio |
| Vazamento do e-mail (D15) | Na composição real, com todas as categorias em `Trace`, o log de dados sensíveis do EF ligado e um exporter OTel em memória. Roda o caminho feliz e falhas injetadas (inconsistência, `409`, `400 disabled`, `500` e falha no commit). Exige que houve registros das categorias do `HttpClient` com `users`, para provar que o canal foi capturado, e que nenhum log, span, `outbox_messages.error` nem ProblemDetails contém o e-mail. Na Application, um logger de captura passa por todos os ramos, com contagem maior que zero em cada um |
| CI (`Compose`) | O roteiro da §4.7: e-mail no mailpit, link público e `GET` no link com `200` |

### 5.2. Integração com o Keycloak real

- **Fixture:** um só `KeycloakFixture`, agora com uma rede Testcontainers, um mailpit (alias `mailpit`, mesma tag
  do compose, espera no `readyz`), `SMTP_*` explícitos e `KC_HOSTNAME=http://keycloak.test:8081` com o backchannel
  dinâmico. Todas as composições de teste passam o `PublicBaseUrl` correspondente. Assim, **todos** os testes de
  Keycloak exercitam a separação do D8, e os que já existem continuam verdes.
- **E-mails de teste únicos e com `+`** (`admin+{guid}@acme.test`), o que exercita o escape da query em todo teste
  e evita a dependência de ordem que o D5 criaria. O mailpit compartilhado é sempre filtrado por destinatário.
- **Leituras cruas pelo master** (como o `LerOrganizacaoCruaAsync` já faz), nunca pelo DTO do adaptador:
  - o usuário tem o `tenant_id`, `enabled` e as duas ações obrigatórias gravadas, já que o Keycloak ignora ação
    desconhecida em silêncio;
  - `organizations/{id}/members` contém o usuário;
  - `users/{id}/role-mappings/realm` contém `tenant-admin`.
- **Idempotência e retomada parcial:**
  - chamado duas vezes, devolve o mesmo `sub`, sem duplicar nada, e envia um e-mail só se o primeiro não tiver
    sido aceito;
  - com o usuário pré-criado pelo master, com o `tenant_id` mas sem vínculo e sem papel, a chamada completa
    vínculo, papel e e-mail;
  - com o usuário sem `UPDATE_PASSWORD` pendente (aceito), nada é enviado (D12).
- **Inconsistências:**
  - usuário pré-existente sem `tenant_id`;
  - usuário com o `tenant_id` de **outro** tenant;
  - usuário com username igual ao e-mail e outro e-mail, que provoca o `409` do passo 2, termina em inconsistência
    e não entra em laço;
  - `pre.{x}@acme.test` existente e convite para `{x}@acme.test`: prova o `exact=true`;
  - o nosso usuário desabilitado pelo master (`400`);
  - papel ausente no realm.
- **Corrida do `409`:** interceptar o primeiro `GET`, devolvendo vazio, faz o `POST` real responder `409` e o
  usuário ser reencontrado. O `500` do SMTP é injetado por interceptação, porque derrubar o mailpit compartilhado
  quebraria os testes paralelos. Que "SMTP fora do ar = `500`" vem da leitura de código fica registrado.
- **Link:** extraído do e-mail no mailpit. O teste decodifica o token do parâmetro `key` e exige
  `exp − iat ≈ LinkLifetime` e `sub` igual ao devolvido. Com o `KC_HOSTNAME` fixo, reescreve a autoridade do link
  para a porta mapeada e o abre, exigindo a página de ações e não a de erro.
- **User Profile:** um usuário comum, autenticado, não consegue alterar o `tenant_id` pela Account REST API.
- **Service account:** papéis **efetivos** (`role-mappings/clients/{id}/composite` e os de realm), comparados sem
  depender de ordem, iguais a `manage-organizations` e `manage-users`. Ausência de `impersonation`, `realm-admin`,
  `manage-realm`, `manage-clients` e `manage-identity-providers`. O teste que hoje espera `403` em `GET /users`
  passa a provar outro negativo, `403` em `GET .../clients` e em `PUT` do realm.
- **Health check:** um token sem `manage-users` gera `503`.

### 5.3. Prova por mutação

Cada teste novo é confirmado no estado vermelho, e a tabela vai no handoff. No mínimo:

| Mutação | Deve ser pega por |
|---|---|
| Tirar a reserva de vaga de `CompleteProvisioning` | Domínio |
| Mudar o tenant antes de validar a vaga | Domínio ("lança sem ter mudado nada") |
| Não apagar o e-mail (na ativação e na falha) | Domínio e E2E |
| Pôr o e-mail no `TenantRegistered` | Funcional (conteúdo do Outbox) |
| Aceitar usuário sem `tenant_id`, ou com o de outro tenant ("existe" no lugar de "igual") | Integração |
| Tirar o limite de uma volta depois do `409` | Integração (laço) |
| Tirar o escape do e-mail na query | Integração (e-mails com `+`) |
| Remover o `exact=true` | Integração (`pre.{x}`) |
| Remover o `briefRepresentation=false` | Integração (atributo ausente, reaproveitamento falha) |
| Remover o `lifespan`, ou usar `.Seconds` no lugar de `.TotalSeconds` | Integração (`exp − iat`) |
| Pular o passo 3 ou o 4 quando o usuário já existe | Integração (retomada parcial) |
| Enviar o e-mail sem checar `UPDATE_PASSWORD` | Integração (usuário aceito) |
| Tirar `VERIFY_EMAIL`, ou criar com `enabled=false` | Integração (leitura crua) |
| Remover a declaração do atributo no User Profile, ou trocar a política para `ENABLED` | Integração e arquitetura |
| Usar o `BaseUrl` no `aud` | Integração (todos os testes de Keycloak) |
| Usar o `PublicBaseUrl` no transporte | Integração e CI |
| O handler gravar o tenant antes de adicionar o `Member` | PostgreSQL (atomicidade) |
| O `try` cobrir só a primeira chamada ao Keycloak | Application |
| Pôr o e-mail num log ou numa mensagem de exceção | Vazamento do e-mail |
| Tirar o `KC_HOSTNAME` do compose | CI e arquitetura |

### 5.4. Testes existentes afetados

- **Deixam de compilar** (`Register` e `MarkProvisioned`): `TenantTests`, `ProvisionTenantHandlerTests`,
  `AtomicidadeDoRegistroTests`, `MapeamentoDeTenantTests`, `SchemaDeTenantsTests`, `TenantRepositoryTests` e
  `OutboxProcessorTests`.
- **Ficam vermelhos:** `RegrasDoRealmTests` (service account só com `manage-organizations`) e `KeycloakRealTests`
  (`403` em usuários).
- **Mudam com o fixture:** `KeycloakFixture`, `KeycloakHealthCheckTests`, `ComposicaoDoProvisionamento` (que hoje
  usa `admin@acme.com` fixo), `ClientAssertionFactoryTests`, `KeycloakAdminOptionsTests`, `PlanCatalogTests` e,
  provavelmente, `DependencyInjectionTests`.
- Os funcionais (`RegistroDeTenantTests` e `ProvisionamentoDeTenantTests`) já mandam o e-mail e não quebram.

## 6. Onde esta fatia cai no roadmap

Fecha o provisionamento da §9.1, menos a reconciliação. Continuam pendentes no M1: suspensão, encerramento,
reconciliação, retry manual e downgrade de plano. O ciclo de vida completo do convite (expiração, reenvio,
cancelamento, `POST /members`) é o M2, e esta fatia deixa prontos para ele o `Member`, `EnsureInvitedUserAsync`
com o papel na porta e a `IInvitationPolicy`.

A pendência da fatia A sobre o e-mail mostrar o `name` da Organization fica sem efeito: o `execute-actions-email`
não usa o template de convite de Organization.

## 7. Dívidas e questões abertas registradas

- **E-mail duplicado**, em qualquer falha entre o envio e o commit (§4.3).
- **Link duplicado continua válido depois do aceite**, e permite trocar a senha do admin por até 7 dias (§3.4).
- **O `Member` do admin fica `Invited` até a sincronização do ADR-007** (M4). A expiração do M2 **não pode** chegar
  antes dela sem outra forma de saber do aceite, senão expira um admin que já aceitou.
- **Admin órfão:** se o e-mail sai e a janela esgota antes do commit, o tenant fica `ProvisioningFailed` com um
  usuário habilitado, com `tenant-admin` e com link válido. O retry manual e a reconciliação precisam tratar isso,
  desabilitando o usuário ou reaproveitando-o.
- **Tenants registrados antes desta fatia** vão para `ProvisioningFailed` e só saem pelo retry manual, informando o
  e-mail (D13).
- **E-mail digitado errado entrega o tenant a um estranho**, e não há revogação pela API: o platform-admin não opera
  rotas de membro (§10.1). O runbook provisório é desabilitar o usuário no Keycloak. Uma operação de plataforma para
  trocar ou reenviar o convite do admin inicial fica para o M1/M2.
- **A mesma pessoa não administra dois tenants:** o segundo `POST` com o mesmo e-mail cai em `ProvisioningFailed`.
  É consequência do ADR-009 e da unicidade de e-mail no realm.
- **SMTP, papel, User Profile e permissões do realm só no primeiro import** (§4.7).
- **Retenção real do e-mail apagado:** "apagado" é lógico. WAL, dead tuples e backups guardam o valor pela
  retenção deles.
- **notification-hub:** a integração depende de o hub aceitar SMTP ou de uma extensão no Keycloak (§3.3).
- **M4:** a conta convidada sem senha é alvo de vínculo automático no primeiro login federado. Nunca ligar vínculo
  sem verificação.
- **Um membro existente pode tomar de antemão o e-mail do futuro admin**, se a troca de e-mail não exigir
  verificação, e o D5 derruba o provisionamento. Se a troca de e-mail vem ligada por padrão na 26.7.4 não foi
  verificado.
- Herdada: quem perde a corrida de slug recebe `500`, e não `409`.

## 8. Mudanças na especificação (v2.6)

Arquivo novo `docs/especificacao-arquitetural-v2.6.md`, com a seção "O que mudou da v2.5 para a v2.6":

| Onde | Mudança |
|---|---|
| §5 e §6 (abertura) | A exceção de LGPD do e-mail do admin inicial (D1, D13), também aqui e não só na §10.3 |
| §6.1 | `Tenant` com `InitialAdminEmail`; `Member` mínimo entregue, com o papel no Keycloak nesta fatia; a vaga do admin é reservada na ativação, depois do envio do e-mail (D2) |
| §6.3 | `MemberInvited` continua no catálogo, mas o admin inicial nasce sem ele |
| §8 | Sem rota nova; nota sobre a operação de plataforma futura para o convite do admin (§7) |
| §9.1 | Diagrama com o convite e sem o RabbitMQ; bloco "Duas decisões pendentes" **removido**, com as decisões D1, D2 e D5 no lugar |
| §9.9 | Errata: o convidado nasce **habilitado**; a expiração (M2) passa a desabilitar o usuário; o cancelamento desabilita **e** revoga sessões; o prazo do link vem da política (D9); o reenvio respeita o D12 |
| §10.2 | Errata: o `aud` é o emissor público e o transporte pode ser interno; o raio de dano da chave agora inclui `manage-users` (§3.4); `AllowInsecureHttp` só em Development |
| §10.3 | Exceção de LGPD e sua retenção real; `resetPasswordAllowed` falso; eventos de administração ligados |
| §11.1 | Código de referência do `Tenant`: `Register` com e-mail, `CompleteProvisioning` no lugar de `MarkProvisioned` |
| §11.3 | `EnsureInvitedUserAsync(organizationId, tenantId, InviteData, ct)`, com o papel no `InviteData` |
| §11.4 | O command com `InitialAdminEmail` |
| §11.5 | O handler com a verificação prévia, o convite, o `IMemberRepository` e o `CompleteProvisioning` |
| §11.6 | Os cinco passos do adaptador e a tabela de erros com `409`, `400 disabled` e `500` |
| §11.9 | `IMemberRepository` só com `Add` nesta fatia |
| §12.2 | O `tenant_id` do usuário é gravado no convite e também serve de correlação (D5); é declarado no User Profile, só para admin (D10) |
| §13 | O mailpit nos testes; o teste de papéis efetivos do service account; o teste de vazamento do e-mail |
| §15 | `mailpit` no compose (a v2.5 já o listava sem ele existir); `KC_HOSTNAME`; SMTP por ambiente; o bootstrap com `tenant-admin`, `manage-users` e User Profile; `IGNORE_EXISTING` vale para tudo isso; o job da CI confere o e-mail e o link; portas em `127.0.0.1` |
| §16 | Andamento: fatia C entregue; o catálogo de papéis pendente passa a ter o `tenant-admin` |
| §19 | Limites: os itens da §7 desta spec, incluindo o alcance do `manage-users` e duas réplicas gerando dois e-mails |

## 9. Fora do escopo

`POST /members` e os convites comuns; expiração, reenvio e cancelamento; `GET` de membros; retry manual de
`ProvisioningFailed`; a operação de plataforma para trocar o convite do admin; o evento `MemberInvited`; o
notification-hub; a API validando tokens do Keycloak; os demais papéis do catálogo; papéis no `Member`; o prazo de
convite por tenant.

## 10. Entregáveis

- O código e os testes descritos na §4 e na §5, com a tabela de mutações preenchida no handoff.
- A migration de `initial_admin_email` e de `members`.
- `docs/especificacao-arquitetural-v2.6.md` (§8).
- `docs/documentacao-negocio.md` alinhado à v2.6. Hoje ele aponta a v2.4, e passam a ser falsos: o usuário
  "desabilitado", "nenhum dado pessoal no banco" (RN-019), o passo 3 do provisionamento, "não há sessão a revogar"
  e o prazo do link.
- README:
  - narrativa do andamento e links para a v2.6;
  - linha evolutiva e stack;
  - o mailpit na tabela do Keycloak e no comando da IDE;
  - o aviso de `down -v` e `--build`;
  - a demonstração com e-mail e slug únicos e o passo "abra http://localhost:8025";
  - a contagem de testes.
- `CONTRIBUTING.md`: o link da spec, que ainda aponta a v2.3.
- Comentários de código que ficam falsos: `KeycloakAdminOptions` (regra do `BaseUrl` e "só
  `manage-organizations`"), `RegisterTenantCommand` ("validado e descartado"), `Tenant` (cita `MarkProvisioned`),
  `Plan` ("zero é permitido"), `IIdentityProvider` ("outras cinco da §11.3") e `docker-compose.yml`.
- O handoff da fatia ao final, com o resultado da verificação ao vivo.

## 11. Revisão por especialistas (2026-09-29)

Seis revisores, em paralelo e só lendo, sobre a primeira versão deste design. Achados que mudaram decisões:

| Achado | Revisores | Onde entrou |
|---|---|---|
| O User Profile descarta o atributo de usuário em silêncio, e o conserto intuitivo (`ENABLED`) permite sequestrar um tenant | Segurança e Keycloak (no código); Testes e Coerência (a apontar) | D10, §3.1, §4.7 |
| O `tenant_id` da §12.2 faltava | Coerência e Segurança | D5 |
| Atribuir papel pelo nome exige `view-realm` | Keycloak | §3.1, §4.3 passo 4 |
| Laço sem fim no `409` do `POST /users` | Testes, Keycloak e Coerência | §4.3 passo 2 |
| `CompleteProvisioning` mudava o estado antes de validar | Arquitetura | §4.2, D14 |
| O papel fixo no adaptador; o prazo sem porta | Arquitetura e Coerência | D11, D9 |
| O e-mail vazava em Development, e o teste planejado era vacuoso | Segurança e Testes | D15, §4.8, §5.1 |
| Link reenviado depois do aceite troca a senha do admin | Segurança | D12, §3.4 |
| Alcance real do `manage-users`; o vínculo à Organization exige as duas permissões | Segurança e Keycloak | D6, §3.4 |
| O `PublicBaseUrl` não pode mudar o token endpoint | DevOps e Keycloak | D8, §4.5 |
| O mailpit não subiria na CI; volumes antigos quebrariam em silêncio; a demonstração quebraria na segunda vez | DevOps e Testes | §4.7 |
| Testes que passariam verdes com as mutações da própria spec; teste funcional inviável; atomicidade vacuosa | Testes | §5 |
| Mais de quinze trechos da v2.5 desatualizados, o documento de negócio e o README | Coerência | §8, §10 |
