# Fatia C — Convite do admin inicial

> **Data:** 2026-09-29 · **Marco:** M1 · **Status:** design aprovado em conversa, seção por seção; aguardando
> revisão da spec escrita.
> **Referência normativa:** [`especificacao-arquitetural-v2.5.md`](../../especificacao-arquitetural-v2.5.md). Esta
> fatia produz a **v2.6**, com as mudanças listadas na §8 abaixo.
> **Sucede:** [handoff do consumidor do provisionamento](2026-09-26-consumidor-provisionamento-handoff.md) (fatia B,
> PR #3, mesclado em 2026-09-29) e o PR #4 (serviço `migrate` no compose, mesclado em 2026-09-29).

---

## 1. Por que esta fatia existe

A fatia B provisiona o tenant sem o segundo passo da §9.1: garante a Organization e marca `Active`, mas ninguém é
convidado. O tenant nasce **trancado**. `POST /tenants` é `platform-admin`, convidar membros exige `tenant-admin`
**daquele tenant**, e esse admin não existe. O `initialAdminEmail` é validado no `POST` e descartado.

Esta fatia fecha a §9.1: o provisionamento garante a Organization, garante o convite do `initialAdminEmail` com o
papel `tenant-admin` e só então marca o tenant `Active`, com a vaga do admin ocupada e o `Member` criado.

**Critério de sucesso:** com o compose de pé, `POST /tenants` → `202` → o tenant chega a `Active` → o e-mail de
convite aparece no mailpit, com um link que abre no navegador (`http://localhost:8081/...`) e leva o convidado a
definir a senha. A demonstração do Keycloak fora do ar continua valendo: o convite sai quando ele volta.

## 2. Decisões

| # | Decisão | Alternativa descartada, e por quê |
|---|---|---|
| D1 | **O e-mail fica temporariamente no tenant** (`InitialAdminEmail`, coluna anulável), fora do evento, e é apagado na mesma transação que ativa. Exceção explícita e limitada à regra "dados pessoais só no Keycloak": só existe em tenant `Pending` ou `ProvisioningFailed` | Cifrado em repouso: gestão de chave para um dado que vive horas. Tabela própria: segunda fonte de estado do provisionamento. No evento: levaria o e-mail ao Outbox e, com o broker, ao RabbitMQ. Convite síncrono no `POST`: quebra o `202` com o Keycloak fora do ar |
| D2 | **Ativar e reservar a vaga no mesmo commit.** No Keycloak, o convite acontece antes da ativação; no domínio, uma operação só: `Active` → reserva da vaga → `Member` em `Invited` → e-mail apagado | `ReserveSeat` aceitar `Pending`: enfraquece a invariante para todo chamador futuro. Admin fora do contador: membro grátis e caso especial eterno nas vagas, no downgrade (N8) e na expiração |
| D3 | **Convite pela Admin API: criar o usuário, vincular à Organization, atribuir o papel e enviar `execute-actions-email`** (§3) | `invite-user` nativo de Organization: para e-mail novo, o usuário só passa a existir no registro, e o `Member` nasceria sem `sub` |
| D4 | **O convidado nasce habilitado**, com as ações obrigatórias `UPDATE_PASSWORD` e `VERIFY_EMAIL`. Corrige a §9.9 | Nascer desabilitado, como a §9.9 dizia: o Keycloak recusa usuário desabilitado no envio e no clique (§3) |
| D5 | **E-mail já em uso por outra conta é falha permanente.** O usuário é correlacionado pelo atributo `gateway_tenant_id`, como a Organization: com o atributo deste tenant, é tentativa anterior e é reaproveitado; sem ele, `IdentityProviderInconsistencyException` e `ProvisioningFailed` | Reaproveitar usuário sem Organization: poderia entregar o tenant ao platform-admin ou a uma conta abandonada, sem convite. Recusar no `POST`: exige o Keycloak no ar e ainda precisaria desta regra para a corrida |
| D6 | **O service account recebe `manage-users`**, com decisão explícita e um teste que garante que ele não tem mais nada além de `manage-organizations` e `manage-users` | Permissões granulares v2 do Keycloak: "só os usuários das Organizations da Gateway" não é um recorte direto, e seria outra premissa externa a verificar. O M2 precisa de `manage-users` de qualquer forma |
| D7 | **E-mail pelo SMTP do Keycloak, com mailpit em desenvolvimento e na CI. O notification-hub fica para depois** (§3.3) | Extensão Java no Keycloak chamando o hub: SPI interno, sem garantia entre versões. Entrada SMTP no hub dentro desta fatia: mudança grande em outro repositório bloqueando esta |
| D8 | **Endereço público separado do transporte.** `KC_HOSTNAME` público no Keycloak; na Gateway, `Keycloak:Admin:PublicBaseUrl` só para o `aud` do assertion | Sem `KC_HOSTNAME`: o link do e-mail sai com o host interno (§3.2). `frontendUrl` no realm: muda o mesmo emissor, sem o backchannel dinâmico, e teria de variar por ambiente dentro do JSON de import |
| D9 | **O prazo do link é passado pela Gateway** (`Invitations:LinkLifetime`, padrão 7 dias), alinhado ao prazo do convite da §9.9 | O padrão do realm (12 h): desalinhado dos 7 dias do ciclo do convite |

## 3. Fatos externos verificados

Verificados em 2026-09-29 lendo o código-fonte do Keycloak na tag **26.7.4** (commit `aa9fe3fb`). É leitura de
código, e nada foi exercitado num container. Os testes de integração da §5 são a prova em execução.

### 3.1. Convite

- `PUT /users/{id}/execute-actions-email` recusa usuário **desabilitado** com `400 User is disabled` e usuário sem
  e-mail com `400 User email missing` (`UserResource.java` L1300-1308). No clique, `checkIsUserValid` recusa
  usuário desabilitado (`LoginActionsServiceChecks.java` L131-133), e nada no fluxo o habilita. **A §9.9 estava
  errada.**
- O `lifespan` é por chamada, em segundos. O padrão é `actionTokenGeneratedByAdminLifespan` do realm, 12 h
  (`UserResource.java` L1332-1334). Sem `client_id`, o link leva ao client `account`.
- `invite-user` de Organization **não cria usuário** para e-mail novo: o usuário nasce no registro
  (`OrganizationInvitationResource.java`; `RegistrationUserCreation.java` L154-160).
- `POST /users` devolve o id no `Location` do `201`, e username ou e-mail repetido dá `409`
  (`UserResource.java` L285-286, L182-183). Username igual ao e-mail é aceito.
- `GET /users?email=...&exact=true` compara por igualdade, em minúsculas, e inclui usuários desabilitados. Sem
  `exact`, a busca vira `LIKE %x%` (`JpaUserProvider.java` L1284-1291).
- `POST /organizations/{id}/members` aceita usuário desabilitado, responde `201` e dá `409` se ele já for membro
  (`JpaOrganizationProvider.java` L198-230).
- **Não verificado:** se reatribuir um papel de realm a quem já o tem é inofensivo. A prova fica no teste de
  integração.

### 3.2. Link e emissor

- O link do e-mail é montado a partir da URL de **frontend** (`UserResource.java` L1056; `HostnameV2Provider.java`
  L107-124). Sem `KC_HOSTNAME` e com `start-dev`, ela é a URL da requisição de admin, e o link sai com
  `http://keycloak:8080`. Trocar o host à mão também não funciona: o clique é validado contra o emissor gravado no
  token (`LoginActionsService.java` L620-623).
- Com `KC_HOSTNAME=http://localhost:8081` e `KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true`:
  - o link, o `iss` dos tokens e o issuer do discovery passam a usar `localhost:8081`, mesmo com a requisição
    chegando por `keycloak:8080`;
  - a Admin API continua respondendo por `keycloak:8080`, sem redirecionar (`AdminRoot.java` L182-206);
  - o `aud` do client assertion precisa ser o emissor público. Com `keycloak:8080`, o Keycloak responde
    `Invalid token audience` (`JWTClientValidator.java` L29-38; `AbstractBaseJWTValidator.java` L126-133).
- **Consequência:** a regra da v2.5 (§10.2) "em produção, o `BaseUrl` da Gateway precisa ser igual ao
  `KC_HOSTNAME`" só vale se a Gateway chamar o Keycloak pelo endereço público. A regra certa: o `aud` é o emissor
  público, e o transporte pode ser o endereço interno.

### 3.3. Transporte do e-mail e o notification-hub

- O Keycloak só envia e-mail por SMTP (`DefaultEmailSenderProvider.java` L89). Trocar o transporte exige um
  provider num **SPI interno** (`EmailSenderSpi.isInternal() == true`), que gera alerta a cada subida.
- **Nenhum endpoint devolve o link sem enviar o e-mail.** `execute-actions-email` e `invite-user` sempre enviam e
  respondem `204`. O link do convite é gravado no banco, mas o campo está marcado como obsoleto e não aparece mais
  nas respostas (`OrganizationInvitationRepresentation.java` L91-105). A Gateway não pode gerar o token, porque
  ele é assinado com as chaves do realm (`DefaultActionToken.java` L172).
- O notification-hub (`../notification-hub`) só recebe pedidos por HTTP, sem entrada SMTP.
- **Conclusão:** para o convite sair pelo hub, ou o hub passa a aceitar SMTP, ou o Keycloak ganha uma extensão
  Java. As duas ficam fora desta fatia (D7). Com o SMTP configurável por ambiente, plugar o hub depois é trocar
  configuração, sem código na Gateway.

## 4. Design

### 4.1. Componentes e fluxo

```
POST /tenants ──► RegisterTenantHandler ──► Tenant.Register(..., initialAdminEmail)   [e-mail na coluna, não no evento]
                                              │
                           Outbox (tenant-registered: TenantId + Slug)
                                              │
                                              ▼
                               ProvisionTenantHandler
                                 1. EnsureOrganizationAsync          ──► Keycloak: Organization
                                 2. EnsureInvitedUserAsync           ──► Keycloak: usuário, vínculo, papel, e-mail
                                 3. tenant.CompleteProvisioning(...) ──► Active + vaga + Member(Invited) + e-mail apagado
                                              │
                                        um commit só
```

### 4.2. Domínio

**`Tenant`**
- `InitialAdminEmail` (`Email?`, value object já existente): recebido por `Register`. `Register` passa a exigir o
  e-mail. O evento `TenantRegistered` **não** muda.
- `CompleteProvisioning(string externalOrganizationId, ExternalUserId adminUserId, DateTimeOffset invitedAt)`
  devolve o `Member`. Numa operação: exige `Pending`, marca `Active` com o id da Organization e levanta
  `TenantActivated`, reserva a vaga e cria o `Member` em `Invited`. Por último, apaga `InitialAdminEmail`.
  - Se a reserva falhar, o método lança `DomainInvariantViolation`. A subida já recusa plano sem vaga (§4.5), então
    a falha é erro de programação.
  - O `MarkProvisioned` atual deixa de ter chamador fora dos testes e sai. Os testes dele migram para
    `CompleteProvisioning`.
- A leitura de `InitialAdminEmail` fica disponível para o handler. O domínio não a expõe em nenhum evento.

**`Member`** (aggregate root novo, no mínimo que o convite do admin exige)
- `MemberId`, `TenantId`, `ExternalUserId` (o `sub`), `MemberStatus` (só `Invited` nesta fatia) e `InvitedAt`.
- O papel `tenant-admin` vive no Keycloak (ADR-005) e não é duplicado no `Member`.
- Criado só por `Tenant.CompleteProvisioning` nesta fatia (fábrica `internal` ao domínio), para que nenhum membro
  nasça sem a vaga reservada.
- Nenhum evento de domínio: `MemberInvited` ainda não tem consumidor. É a mesma regra que a fatia B aplicou a
  `MarkProvisioningFailed`.

**`ExternalUserId`**: value object do `sub`, como a §11.3 já nomeia.

### 4.3. Porta e adaptador do Keycloak

```csharp
Task<ExternalUserId> EnsureInvitedUserAsync(
    string organizationId, TenantId tenantId, InviteData convite, CancellationToken cancellationToken);

public sealed record InviteData(Email Email, TimeSpan LinkLifetime);
```

Mesmo contrato de erro de `EnsureOrganizationAsync`: só `IdentityProviderInconsistencyException` é permanente, e
o resto é infraestrutura. Os passos no `KeycloakIdentityProvider`, cada um idempotente:

1. `GET /users?email=<e-mail>&exact=true`.
   - Achou um usuário: se o atributo `gateway_tenant_id` for este tenant, é o nosso e segue para o passo 3.
   - Senão, `IdentityProviderInconsistencyException`.
2. `POST /users` com username = e-mail, e-mail, `enabled=true`, `requiredActions=[UPDATE_PASSWORD, VERIFY_EMAIL]`
   e o atributo `gateway_tenant_id`. O id vem do `Location`. Um `409` significa que outro processo criou o
   usuário entre a busca e a criação; aí volta ao passo 1.
3. `POST /organizations/{id}/members` com o id. O `409` (já é membro) conta como sucesso.
4. Atribuição do papel de realm `tenant-admin`.
5. `PUT /users/{id}/execute-actions-email?lifespan=<segundos>` com `[UPDATE_PASSWORD, VERIFY_EMAIL]`.
   - `400 User is disabled`: alguém desabilitou o nosso usuário à mão, e vira `IdentityProviderInconsistencyException`.
   - `500` (SMTP fora do ar): transitório.

**O e-mail pode sair mais de uma vez.** O passo 5 não é idempotente, porque cada chamada envia um e-mail com um
link novo. Se o commit falhar depois do envio, a nova tentativa envia outro, e os dois links funcionam. É
coerente com a entrega "pelo menos uma vez" do Outbox, e fica registrado na §19.

### 4.4. `ProvisionTenantHandler`

1. Carrega o tenant. Fora de `Pending`, encerra sem efeito (como hoje).
2. `InitialAdminEmail` nulo, o que só acontece com tenant registrado antes desta fatia: `MarkProvisioningFailed`
   e log. Ativá-lo sem admin recriaria o tenant trancado.
3. No mesmo `try` de hoje: `EnsureOrganizationAsync`, depois `EnsureInvitedUserAsync`. A classificação de erros
   não muda: inconsistência leva a `ProvisioningFailed`; qualquer outro erro, se a janela estiver esgotada e não
   houver cancelamento, também leva; senão, a exceção sobe e o Outbox tenta de novo.
4. `tenant.CompleteProvisioning(organização, sub, relogio.UtcNow)`; o `Member` devolvido vai para
   `IMemberRepository.Add`.
5. O commit do `TransactionBehavior` grava tudo junto.

**O e-mail nunca aparece em log nem em mensagem de exceção.** Os logs levam o `tenantId` e o `sub`.

### 4.5. Configuração

- `Invitations:LinkLifetime` (padrão `7.00:00:00`), validado na subida: positivo, em segundos inteiros, e no
  máximo 30 dias. O teto é arbitrário e protege contra um link que valha, na prática, para sempre.
- `Keycloak:Admin:PublicBaseUrl` (opcional): quando informado, é a base do `aud` do assertion. Quando omitido,
  vale o `BaseUrl`, e nada muda para quem roda a Gateway pela IDE. A regra de `https` que já vale para o
  `BaseUrl` vale para ele também.
- O catálogo de planos passa a recusar `maxUsers < 1` na subida.

### 4.6. Persistência

- `tenants.initial_admin_email` (`varchar(254)`, anulável).
- Tabela `members`:
  - colunas `id`, `tenant_id` (FK), `external_user_id`, `status` (texto), `invited_at` e os campos de auditoria
    do template;
  - índice único em `(tenant_id, external_user_id)`.
- `IMemberRepository` com assinaturas que exigem o tenant (§6.4). Nesta fatia, só `Add`.
- Uma migration só.

### 4.7. Realm, compose e CI

- **Realm (`realm-identity-gateway.json`)**
  - Papel de realm `tenant-admin`.
  - `manage-users` no service account.
  - `smtpServer` com placeholders de ambiente (`${SMTP_HOST}`, `${SMTP_PORT}`, `${SMTP_FROM}`), no mesmo
    mecanismo do `${GATEWAY_CLIENT_CERT}`. Sem o prefixo `KC_`, que o Keycloak lê como opção dele.
  - O import só roda na primeira subida: trocar o SMTP de um ambiente existente exige `docker compose down -v` ou
    a Admin API. Isso fica documentado. Em produção, a configuração do realm é do Terraform (§15), com
    autenticação e TLS no SMTP. O `mailpit` sem autenticação é só de desenvolvimento.
- **Compose**
  - `mailpit` com versão fixada: SMTP só na rede interna, interface em `127.0.0.1:8025`.
  - No Keycloak, `KC_HOSTNAME=http://localhost:8081`, `KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true` e as variáveis de
    SMTP.
  - Na api, `Keycloak__Admin__PublicBaseUrl=http://localhost:8081`.
  - Os comentários sobre "`BaseUrl` igual ao `KC_HOSTNAME`" são corrigidos.
- **Job `Compose` da CI:** depois do `Active`, consulta a API do mailpit e exige uma mensagem para o
  `initialAdminEmail` com um link que comece com `http://localhost:8081/`. A segunda subida sobre os mesmos
  volumes continua provando que a Gateway autentica.

## 5. Testes

### 5.1. Por nível

| Nível | O que prova |
|---|---|
| Domínio | `CompleteProvisioning`: ativa, reserva exatamente uma vaga, cria o `Member` com `sub` e `InvitedAt`, apaga o e-mail, levanta `TenantActivated` uma vez e recusa fora de `Pending`. `Register` guarda o e-mail, e `TenantRegistered` continua sem ele |
| Application | Cada linha de erro da §4.3 e da §4.4: permanente; transitório antes e depois da janela; tenant sem e-mail; cancelamento. Um teste garante que nenhum log contém o e-mail |
| Integração, Keycloak real (Testcontainers com `KC_HOSTNAME` fictício, por exemplo `http://keycloak.test:8081`, diferente do endereço de transporte na porta mapeada, o que exercita a separação do D8) | `EnsureInvitedUserAsync` cria o usuário com atributo, ações obrigatórias, vínculo e papel. Chamado duas vezes, devolve o mesmo `sub` e não duplica nada, o que prova que reatribuir o papel é inofensivo. Usuário pré-existente sem o atributo gera inconsistência. O e-mail chega a um mailpit em container, com o link no endereço público. O assertion com `PublicBaseUrl` autentica por `keycloak:8080`. O service account tem exatamente `manage-organizations` e `manage-users` |
| Integração, PostgreSQL | Migration, mapeamento do `Member`, índice único e atomicidade do commit único |
| Funcional | `POST /tenants` → `Active`, com o `Member` gravado e `initial_admin_email` nulo |
| CI (`Compose`) | O e-mail de convite chega ao mailpit, com o link público |

### 5.2. Prova por mutação

Como nas fatias anteriores, cada teste novo é confirmado no estado vermelho. A tabela de mutações vai no handoff.
No mínimo:
- tirar a reserva de vaga de `CompleteProvisioning`;
- não apagar o e-mail;
- aceitar usuário sem o atributo;
- remover o `exact=true`;
- remover o `lifespan`;
- usar o `BaseUrl` no `aud`;
- tirar o `KC_HOSTNAME` do compose.

## 6. Onde esta fatia cai no roadmap

Fecha o M1 no que toca o provisionamento (§9.1). Continuam pendentes no M1: suspensão, encerramento, reconciliação,
retry manual e downgrade de plano. O ciclo de vida completo do convite (expiração, reenvio, cancelamento, `POST
/members`) é o M2, e esta fatia deixa o `Member` e `EnsureInvitedUserAsync` prontos para ele.

## 7. Dívidas e questões abertas registradas

- **E-mail duplicado** quando o commit falha depois do envio (§4.3).
- **Tokens de convite antigos continuam válidos** até expirar. Isso vem da leitura do código e não foi verificado;
  pesa no reenvio do M2.
- **SMTP do realm só no primeiro import** (§4.7).
- **notification-hub:** a integração depende de o hub aceitar SMTP ou de uma extensão no Keycloak (§3.3).
- Herdada: quem perde a corrida de slug recebe `500`, e não `409`.

## 8. Mudanças na especificação (v2.6)

Arquivo novo `docs/especificacao-arquitetural-v2.6.md`, com a seção "O que mudou da v2.5 para a v2.6":

| Onde | Mudança |
|---|---|
| §6.1 | `Member` mínimo entregue; o papel vive no Keycloak |
| §9.1 | As duas pendências fechadas (D1, D2); o e-mail já em uso é falha permanente (D5) |
| §9.9 | Errata: o convidado nasce **habilitado**, com as ações obrigatórias; o prazo do link é passado na chamada (D9) |
| §10.2 | Errata: o `aud` é o emissor público; o transporte pode ser interno (§3.2) |
| §10.3 | Exceção de LGPD: o e-mail temporário do admin inicial (D1) |
| §11.3 | `EnsureInvitedUserAsync` com a assinatura desta fatia |
| §15 | `mailpit`, `KC_HOSTNAME` e SMTP por ambiente |
| §16 | Andamento: fatia C entregue |
| §19 | Limites: e-mail duplicado possível; SMTP só no primeiro import; `manage-users` no realm inteiro (D6) |

## 9. Fora do escopo

`POST /members` e os convites comuns; expiração, reenvio e cancelamento; `GET` de membros; retry manual de
`ProvisioningFailed`; o evento `MemberInvited`; o notification-hub; a API validando tokens do Keycloak; os demais
papéis do catálogo.

## 10. Entregáveis

- O código e os testes descritos na §4 e na §5, com a tabela de mutações preenchida no handoff.
- A migration de `initial_admin_email` e de `members`.
- `docs/especificacao-arquitetural-v2.6.md`.
- README: a demonstração passa a incluir o e-mail de convite no mailpit.
- O handoff da fatia ao final, com o resultado da verificação ao vivo.
