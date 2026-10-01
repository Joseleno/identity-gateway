# Fatia D — Tokens do Keycloak

> **Data:** 2026-09-30 · **Marco:** M0 (e a primeira rota de tenant do M1) · **Status:** as seções 1 a 3 do design
> (escopo, validação e policies, realm e bootstrap) foram aprovadas em conversa, seção por seção. A parte de testes
> (§5) e a de harness, compose, CI e README (§4.6, §4.7) não foram apresentadas uma a uma: passam a ser aprovadas
> pela revisão desta spec escrita. Revisado por dois times de especialistas antes da escrita, por cinco especialistas
> depois dela e por verificação ao vivo. Os achados estão incorporados abaixo (§12), e as decisões do autor sobre a
> última revisão estão na §8.
> **Referência normativa:** [`especificacao-arquitetural-v2.6.md`](../../especificacao-arquitetural-v2.6.md). Esta
> fatia produz a **v2.7**, com as mudanças listadas na §9.
> **Sucede:** [handoff do convite do admin inicial](2026-09-30-convite-admin-inicial-handoff.md) (fatia C, PR #5,
> mesclado em 2026-09-30).
> **Entrega em dois PRs** (D-i): a **D1** leva o Keycloak de ponta a ponta até o `POST /tenants`; a **D2** leva a
> rota de tenant. Um design, um plano em duas partes (§6).

---

## 1. Por que esta fatia existe

A fatia C criou o admin do tenant: um usuário no Keycloak com o atributo `tenant_id`, o papel `tenant-admin` e o
e-mail de ações. Mas a API só aceita o JWT simétrico do template, assinado com uma chave de desenvolvimento que o
README e a CI publicam (`Jwt:SigningKey`), e não existe nenhuma rota que um `tenant-admin` possa chamar. O critério
do M0, "primeiro `curl` com token do Keycloak", segue aberto, e o M2 inteiro depende disso.

Esta fatia troca a validação do token: sai o HS256, entra o access token do Keycloak, validado por metadados, com
emissor, audiência, client de origem e forma conferidos. O realm passa a emitir o token que a §10.1 e a §12
descrevem: `roles` plano só com o catálogo, `tenant_id` plano e a audiência `identity-gateway-api` só para quem fala
com a Gateway. O primeiro platform-admin passa a nascer sem senha, convidado por e-mail. E entra a primeira rota de
tenant, `GET /api/v1/tenants/{tenantId}`, protegida em quatro camadas.

**Critério de sucesso da D1:** com o compose de pé, o mailpit tem **um** e-mail de convite para o platform-admin. Ele
define a senha pelo link, obtém um token pelo device flow com `curl` e faz `POST /tenants` → `202` → o tenant chega a
`Active`. O token da receita HS256 antiga leva `401`. A demonstração com o Keycloak parado continua valendo, desde que
a API já tenha validado um token antes (§4.7). O convite do admin do tenant (critério da fatia C) continua provado:
o e-mail chega ao mailpit e o link abre a página de ações. O job `Compose` da CI prova tudo isso na topologia do
compose.

**Critério de sucesso da D2:** o admin convidado conclui o convite, obtém o token pelo device flow e lê o próprio
tenant: `GET /api/v1/tenants/{id}` → `200`, com exatamente as chaves do contrato (nenhuma de e-mail). Qualquer outro
GUID → `403`. O platform-admin → `403` (D-b). Um usuário com `tenant_id` herdado de um grupo → `403`, barrado pela
pertença no banco (D-f).

## 2. Decisões

### 2.1. Decisões do autor

| # | Decisão | Alternativa descartada, e por quê |
|---|---|---|
| D-a | **Escopo: tokens do Keycloak mais a primeira rota de tenant**, `GET /api/v1/tenants/{tenantId}`: o admin convidado lê o próprio tenant e leva `403` no alheio | Só os tokens: o claim `tenant_id` seria configuração sem prova de consumo |
| D-b | **O platform-admin recebe `403` em `GET /tenants/{tenantId}` até existir a tabela de auditoria.** O override da §10.1 exige auditoria, e fica para a fatia E | Override agora, sem auditoria: o único acesso cruzado entre tenants do produto sem trilha |
| D-c | **Token de usuário na demonstração pelo Device Authorization Grant** (RFC 8628), num client público de demonstração. A senha continua digitada só no Keycloak. ROPC continua proibido (ADR-003) | ROPC: proibido. Authorization Code + PKCE no terminal: exige copiar o `code` da barra do navegador. Impersonation pelo master: não exercita a jornada real |
| D-e | **A audiência `identity-gateway-api` num scope `gateway-api` fora dos defaults do realm**, atribuído só aos clients que falam com a Gateway. A API confere o `azp` contra uma **lista de clients permitidos por ambiente**, e o client de demonstração só entra em Development | O `aud` num scope default: todo client do realm, inclusive o service account, os clients do M6 e qualquer client de teste, passa a emitir token aceito. Reproduzido ao vivo com um token ROPC de um client criado pela Admin API (§3.1) |
| D-f | **A rota de tenant exige que o `sub` seja `Member` daquele tenant no banco da Gateway**, além do claim. O realm não tem grupos, e os papéis do catálogo nunca são compostos. Limite: a pertença não contém quem tem a chave da Gateway, que com o `manage-users` toma a conta de um `Member` real (ADR-011) | Confiar só no claim: o mapper de atributo recua para o atributo de mesmo nome de um grupo, e o próprio service account da Gateway forjou um `tenant_id` assim, ao vivo (§3.1) |
| D-g | **Separação de funções: a policy `TenantAdmin` nega quando o token traz `platform-admin`** | Aceitar a combinação: o D-b só valeria para a conta que não acumula papéis, e o `manage-users` do service account atribui `platform-admin` (§10.2) |
| D-h | **O platform-admin nasce no JSON do realm** (`${PLATFORM_ADMIN_EMAIL}`, só `platform-admin`, sem senha). Um one-shot dispara o `execute-actions-email` **uma vez**. O reenvio é só por comando explícito. O one-shot nunca promove uma conta pré-existente. Com volume antigo, sai com `1` pedindo `docker compose down -v`. A `api` depende dele | O one-shot criar o usuário e atribuir o papel (D-d, a primeira versão): a lógica de runtime em shell promoveu um `tenant-admin` a `platform-admin` ao vivo, porque o CSV do `kcadm` omite os atributos (§3.2). Senha gerada e impressa no log (§15 da v2.6): contraria o próprio ADR-003 |
| D-i | **Dividir em D1 e D2**: a D1 com o realm, o bootstrap, o harness, a validação, o fim do HS256, o app de CI e o README; a D2 com a rota e as quatro camadas, **sem mexer no realm**. Um design, um plano em duas partes, dois PRs | Não dividir: cerca de 16 tarefas num PR, e uma premissa externa falsa no bootstrap seguraria a rota. Aceitar HS256 e Keycloak juntos por uma fatia: mantém viva a chave publicada, que é o risco que a fatia existe para matar, e cria código que a fatia seguinte apaga |
| D-j | **O `MemberRequirement` aceita só `Invited` e `Active`.** Todo outro estado, inclusive um que o enum ganhar depois, nega | Qualquer status: abre por padrão para `Deactivated`, `Expired`, `Revoked` e `Erased`, que o enum já declara, e a primeira fatia que produzir um deles herdaria a brecha em silêncio |
| D-k | **Tenant inexistente leva `403`**: a policy nega antes do handler, porque não há `Member` num tenant que não existe. A rota não usa `404` nesta fatia; o contrato do override (fatia E) decide se o usa | Responder `404` ao próprio tenant inexistente: a rota não tem como usá-lo, porque é inalcançável com o `MemberRequirement`, e o teste que o afirmasse só ficaria verde com o requirement quebrado |
| D-l | **A v2.7 registra só o que a fatia D implementa, mais as erratas.** A sequência de fatias, a regra da suspensão sobre membros `Invited`, a revisão da §12 do Data Plane e o bootstrap de produção com TOTP ficam como propostas (§7, §8), decididas no brainstorming da fatia que as implementar. Pela mesma regra, o RabbitMQ fora do M0 também fica como proposta | Levar as propostas à v2.7: normatiza um roadmap e regras que o autor não decidiu nesta fatia |
| D-m | **O link de definição de senha do platform-admin vale 4 h** (`lifespan=14400`). Depois, o comando de reenvio | Um dia: o link continua trocando a senha depois do aceite (§3.2), num mailpit sem autenticação |
| D-n | **Rotação do refresh token nesta fatia** (`revokeRefreshToken: true`, `refreshTokenMaxReuse: 0`): um refresh token já usado é recusado | Relocar: custaria outro `down -v`, e a §10.3 da v2.6 já promete a rotação |
| D-o | **O link de 7 dias do convite do admin do tenant (fatia C) é risco aceito, com dono:** a fatia F trata a revogação e avalia encurtar o prazo. Nada muda no código na D | Encurtar ou revogar agora: muda o comportamento da fatia C sem a operação de troca do convite, que é da F |

### 2.2. Decisões técnicas do controlador

| # | Decisão | Alternativa descartada, e por quê |
|---|---|---|
| DT1 | **`AccessTokenValidationOptions` pública e neutra quanto ao provedor**, preenchida pelo adaptador do Keycloak: endereço interno dos metadados, emissor público (o mesmo valor do `AssertionAudience` da fatia C), audiência e lista de `azp` | A Api ler `KeycloakAdminOptions`: quebra `AApiNaoConheceOKeycloak`. A Api refazer a conta `{PublicBaseUrl ?? BaseUrl}/realms/{Realm}`: duas derivações que podem divergir, e o formato de URL do Keycloak vazando para a Api (ADR-008) |
| DT2 | **`IssuerValidator` estrito**: só o emissor público exato, por igualdade ordinal | `ValidIssuer`: não restringe, porque o JwtBearer aceita também o `issuer` do discovery (§3.3, visto no spike e ao vivo) |
| DT3 | **`azp`, `typ` e `sub` conferidos na autenticação** (`OnTokenValidated`), não numa policy | Requirement na policy padrão: a `DefaultPolicy` não se soma a uma policy nomeada, e o `POST /tenants`, que tem `PlatformAdmin`, escaparia |
| DT4 | **A lista de `azp` fica fora do `appsettings.json` base.** O `ValidateOnStart` recusa o client de demonstração fora de Development, e há testes com o host em `Production` | A lista no arquivo base: o `IConfiguration` mescla arrays por índice, e um segundo item do base sobreviveria à configuração de produção. "Demo só em Development" garantido só pelo arquivo certo: verde vacuoso, porque toda a suíte roda em Development |
| DT5 | **RS256 só; `ClockSkew` de 30 s; `BackchannelTimeout` de 5 s; `RefreshInterval` de 30 s; `RequireHttpsMetadata = !AllowInsecureHttp`** | Os padrões da biblioteca: 5 min de tolerância dobram a vida do token do ADR-005; 60 s de backchannel com a busca serializada empilham os pedidos; `!IsDevelopment()` solto sobe a app e dá `500` em todo pedido, inclusive `/health/live` (§3.3) |
| DT6 | **Keycloak fora com metadados frios = `401`, com log.** A API sobe e se recupera sozinha | `503` com `Retry-After` na falha de configuração: coerente com o argumento da §9.6, e fica registrado na v2.7 como alternativa. Não entra porque exige distinguir, no `OnAuthenticationFailed`, falha de configuração de token inválido, e a demonstração passa a esquentar o cache antes (§4.7) |
| DT7 | **Harness único por device flow** (testes, CI e README), num projeto de suporte que é **biblioteca**; o app de CI em `tools/`, com `#:project` e `#:property PublishAot=false` | Authorization Code + PKCE nos testes: exige um client de teste com outra forma de token, o verde vacuoso da lição da fatia C. Bash com cookie jar no job: duplica o harness. O projeto de suporte como projeto de teste: `CS7022` no app; o AOT padrão: `IL2026`/`IL3050` com `TreatWarningsAsErrors` (§3.3) |
| DT8 | **O marcador do one-shot é um atributo do realm, gravado antes do envio.** Usuário que já concluiu → saída `0`. Reenvio só manual. A gravação pelo `kcadm` é o spike que resta antes do plano (§8) | Arquivo num volume: ao vivo, a gravação falhou por permissão depois do envio, e cada subida reenviou. Um arquivo também não acompanha o realm: apagar só um dos dois volumes dessincroniza |
| DT9 | **O client de demonstração fica no JSON do realm**, que é bootstrap local; produção evolui pelo Terraform, sem ele (registrado). O demo recebe também o scope `acr` | Um fragmento separado criado pelo one-shot: mais uma peça para uma garantia que a lista de `azp` já dá na Gateway. O `acr` depois: o step-up precisa dele, e acrescentá-lo custaria outro `down -v` |
| DT10 | **O service account `identity-gateway` mantém `fullScopeAllowed=true`**, com `defaultClientScopes` explícitos `basic` e `roles`, sem `gateway-*` | `fullScopeAllowed=false`: a Admin API passa a responder `403` e o health check perde o `resource_access` (visto ao vivo). `false` com `clientScopeMappings`: funciona, mas é uma segunda lista igual para manter, e o ganho fica para quando houver outro papel no service account |
| DT11 | **Os clients que chamam a Gateway não recebem `profile` nem `email`**: o token não carrega e-mail nem nome. `NameClaimType = "sub"` | O e-mail no token: ele iria a histórico de shell, a proxies e à demonstração, contra o D15 da fatia C |
| DT12 | **Na CI, o "um e-mail só" é provado com `docker compose run --rm --no-deps platform-admin-invite` com o convite ainda pendente**, e o Keycloak parado é testado no próprio job, com `stop`/`start`, a mesma sequência do README (§4.7). Cada passo longo tem `timeout-minutes` próprio. Toda asserção de status é exata | Contar zero e-mails depois de `down`/`up`: vacuoso, porque o mailpit volta vazio e o convite já foi aceito, e o one-shot sem marcador também não reenviaria. O Keycloak pausado na coleção de testes: o fixture é compartilhado e paralelo, e pausá-lo derrubaria as outras classes |
| DT13 | **O override futuro do platform-admin é uma policy própria (`TenantReadAccess`)**, com um só handler, registrada já na v2.7 | Um `PlatformAdminOverrideHandler` a mais no `SameTenantRequirement`, como a v2.6 descreve: o `Fail()` veta qualquer `Succeed`, o override nunca funcionaria, e a correção "natural" seria afrouxar o `Fail()` |
| DT14 | **ADR-011: a autorização da Gateway nas rotas de governança é token mais pertença no banco.** O Data Plane continua exposto ao `tenant_id` por grupo, e isso fica escrito. O ADR diz também o limite: a pertença não contém quem tem a chave da Gateway (§9) | Registrar a D-f só na §10.1: é decisão nova com um limite que atravessa o Data Plane, e a §18 pede ADR nesse caso |

## 3. Fatos externos verificados

Keycloak na tag **26.7.4** (commit `aa9fe3fb`): leitura de código pelos analistas e, depois, containers descartáveis
com o JSON proposto (o que funcionou está na §4.4). ASP.NET Core: `Microsoft.AspNetCore.Authentication.JwtBearer`
**10.0.12** e `Microsoft.IdentityModel.*` **8.19.2**, as versões que o repositório resolve, lidas nas tags e
exercitadas num app descartável com um OIDC falso em loopback e, depois, contra o Keycloak real. Na revisão da spec
escrita, uma réplica do compose com o JSON final da §4.4 e o one-shot da §4.5 confirmou os tokens, o import e as três
premissas que eram spike (§3.2).

Os caminhos de arquivo do Keycloak omitem o módulo e o prefixo `src/main/java/org/keycloak/`. O módulo é `services`,
salvo estes: `KeycloakModelUtils.java` e `RequiredActionFactory.java` em `server-spi-private`,
`DefaultExportImportManager.java` em `model/storage-private`, `TokenUtil.java` em `core` e `OAuth2DeviceConfig.java`
em `server-spi`. O `UserAttributeMapper.java` citado é o do pacote `protocol/oidc/mappers` (há três com esse nome).

**[ao vivo]** marca o que foi executado num container; **[código]** o que foi só lido.

### 3.1. Clients, scopes e claims

- **Device flow** [ao vivo]: o atributo de client `oauth2.device.authorization.grant.enabled=true` basta; um client
  público sem standard flow funciona. O `/auth/device` responde `interval: 5` e `expires_in` igual ao prazo do
  device code: `600` com o padrão do realm, `300` com o `oauth2.device.code.lifespan` do demo (§3.2). O
  `verification_uri` sai no host público. **O consentimento é forçado** no device flow, mesmo com
  `consentRequired=false`. O token sai com os mesmos scopes e mappers, e o `iss` é o frontend.
- **`slow_down`** [ao vivo]: um poll antes do `interval` leva `slow_down`, **mesmo com a autorização já concedida**,
  e o servidor não aumenta o intervalo (`DeviceGrantType.java` L190-193, L254-257). Com o login por HTTP concluído
  antes do primeiro poll, o fluxo inteiro leva de 4,5 a 6,5 s, em seis execuções.
- **Declarar `clientScopes` no JSON desliga a criação dos scopes embutidos** (`profile`, `email`, `roles`, `basic`,
  `acr`, `web-origins`...). A correção é o atributo de realm `"CreateDefaultClientScopes": "true"`, que não aparece
  na documentação pública [ao vivo] e **não é persistido**: o import o remove (`RealmManager.java` L739-743). Omitir
  `defaultDefaultClientScopes` deixa como padrão do realm os embutidos que ele cria (`RealmManager.java` L668-670;
  `OIDCLoginProtocolFactory.java` L374-377, L526).
- **Ordem do import** [código + ao vivo]: os clients embutidos nascem antes dos scopes do JSON e não recebem os
  defaults do JSON; os clients declarados no JSON sem `defaultClientScopes` recebem (`RealmManager.java` L597-735;
  `DefaultExportImportManager.java` L378-400). `defaultDefaultClientScopes` só referencia scopes do próprio JSON
  (L382-390).
- **Com o `aud` num scope default** [ao vivo]: um client criado pela Admin API, público e com direct grant, emitiu
  por ROPC um token com `aud: ["identity-gateway-api","account"]`, todos os papéis do usuário e o `tenant_id`. O
  service account com os defaults do realm saiu com `aud: ["identity-gateway-api","realm-management"]`.
- **O `sub` vem do scope `basic`** [ao vivo]: um client de controle sem `basic` emitiu access token **sem `sub` e
  sem `auth_time`**, e o ID token manteve o `sub` (`OIDCLoginProtocolFactory.java` L515-526).
- **Claims por client com o JSON da §4.4** [ao vivo]:
  - demo, `tenant-admin`: `aud: "identity-gateway-api"` (string), `azp: "identity-gateway-demo"`, `sub`,
    `typ: "Bearer"`, `acr: "1"` (texto, do scope `acr`), `tenant_id` plano, `roles: ["tenant-admin"]`,
    `scope: "openid"`. **Sem** `email`, `preferred_username`, `name`, `realm_access`, `resource_access`. O refresh
    token lista `acr` no `scope`.
  - demo, `platform-admin`: `roles: ["platform-admin"]`, sem `tenant_id`.
  - service account: `aud: "realm-management"`, `resource_access.realm-management.roles` com os dois papéis, **sem**
    `identity-gateway-api`. A Admin API respondeu a todas as chamadas do provisionamento.
  - Pedir `scope=openid profile email offline_access` no device flow: aceito, e os extras são ignorados em silêncio.
    O refresh token sai com `typ: "Refresh"`, não `Offline`.
- **`roles` plano só com o catálogo** [ao vivo]: com `fullScopeAllowed=true`, o claim traz também
  `default-roles-identity-gateway` e todos os papéis do usuário. Com `false` e os scope mappings do `gateway-roles`
  só com o catálogo, traz só o catálogo. Usuário sem papel do catálogo recebe o token **sem** o claim `roles`.
- **`fullScopeAllowed=false` no service account** [ao vivo]: o `resource_access` some e a Admin API responde `403`,
  porque ela exige `user.hasRole(role) && client.hasScope(role)` (`AdminAuth.java` L81-90;
  `TokenManager.java` L502-536).
- **`tenant_id` forjável por grupo** [código + ao vivo]: sem o atributo no usuário, o `oidc-usermodel-attribute-mapper`
  usa o atributo de mesmo nome do **primeiro** grupo que o tiver, subindo aos pais, e não há configuração que desligue
  isso (`KeycloakModelUtils.java` L714-744 em `server-spi-private`; `UserAttributeMapper.java` L96-104). O
  `manage-users` gerencia grupos e mapeia qualquer papel que não seja de administração (`GroupPermissions.java`
  L274-275; `RolePermissions.java` L322-323). Ao vivo, o **token do service account da Gateway** criou um grupo com
  `tenant_id`, mapeou `tenant-admin` **e** `platform-admin` no grupo, criou um usuário com senha e o pôs no grupo; o
  device flow desse usuário saiu com os dois papéis e o `tenant_id` forjado. Atributo de Organization não vaza.
- **`multivalued=false`** pega o primeiro valor, com um log de aviso (`OIDCAttributeMapperHelper.java` L165-181): a
  API nunca vê dois `tenant_id` num token real.
- **`typ`** [ao vivo]: o **cabeçalho** é `"JWT"` no access token e no ID token; o **claim** `typ` distingue:
  `"Bearer"` e `"ID"` (`TokenUtil.java` L49, L65). O ID token do demo tem `aud` igual ao client. O refresh token é
  HS512 com `typ: "Refresh"`.
- **Audience mapper**: com um valor só, `aud` sai como string; `access.token.claim` precisa ser `"true"` explícito;
  com `id.token.claim: "false"` ele fica fora do ID token (`OIDCAttributeMapperHelper.java` L412-414).
- **`offline_access` e `uma_authorization`** [ao vivo]: declarar os dois papéis e o scope `offline_access` os tira do
  papel padrão, que fica `[manage-account, view-profile]` (`RealmManager.java` L664-666; `KeycloakModelUtils.java`
  L1296-1304).
- **Usuário importado pelo JSON com `realmRoles`** [ao vivo] recebe só esses papéis, sem `default-roles-*`, e
  username e e-mail em minúsculas.
- **`admin-cli` do realm** [código]: nasce público, com `directAccessGrantsEnabled=true` e *lightweight access token*;
  mapper sem `lightweight.claim` não entra no token dele (`RealmManager.java` L227-239;
  `AbstractOIDCProtocolMapper.java` L89-93).
- **Discovery pelo endereço interno** [ao vivo] devolve o `issuer` público e o `jwks_uri` e o `token_endpoint` no host
  da requisição.
- **Chaves passivas** [ao vivo]: o JWKS publica as chaves `ACTIVE` e `PASSIVE`, não as desabilitadas
  (`JWKSServerUtils.java` L39). Ativar uma chave nova faz os tokens novos saírem com o `kid` novo, e os antigos
  continuam válidos.
- **Placeholders de i18n** (`${role_offline-access}`, `${offlineAccessScopeConsentText}`) reprovam o
  `TodoPlaceholderEPuro`. Descrições em texto puro importam sem erro e mantêm o efeito [ao vivo].

### 3.2. Links de ações e bootstrap

- **O link do `execute-actions-email` resolve o `VERIFY_EMAIL`** [ao vivo]: ao entrar pelo link, o Keycloak marca o
  e-mail como verificado (`ExecuteActionsActionTokenHandler.java` L118-122). As páginas são: informação → nova senha →
  perfil (nome e sobrenome, exigidos pelo User Profile) → "Your account has been updated". Depois, o device flow só
  pede o login (identity-first em dois passos quando há Organization no realm) e o consentimento.
- **Links antigos continuam válidos, e um link antigo trocou a senha de uma conta já ativa** [ao vivo]. Cinco
  e-mails, cinco links: todos abriam a página de ações ao mesmo tempo. Concluído o quinto, o **primeiro** ainda levou
  ao formulário de senha, e a senha nova passou a valer. Cada token só morre pelo próprio uso, por expiração, por
  usuário desabilitado ou removido, ou por troca de e-mail (`RequiredActionFactory.java` L69-71;
  `LoginActionsService.java` L708-711; `ExecuteActionsActionTokenHandler.java` L129-143;
  `AbstractActionTokenHandler.java` L109-114; `LoginActionsServiceChecks.java` L124-133). **Vale também para o convite
  da fatia C** (§8).
- **`kcadm.sh` da imagem do Keycloak** [ao vivo]: autentica no master com `KC_CLI_PASSWORD` no ambiente do processo e
  `--config /tmp/kcadm.config`, sem prompt; cada chamada sobe uma JVM, e uma rodada completa leva cerca de 8 s. **O
  CSV omite objetos aninhados**: uma trava de `tenant_id` escrita com `--fields attributes --format csv` não disparou,
  e o `add-roles` promoveu um `tenant-admin` a `platform-admin`. **O `--fields` esvazia os aninhados também no
  JSON** [ao vivo, revisão]: `--fields id,attributes` devolve `"attributes" : { }`, no usuário e no realm; só
  `attributes(*)` ou a ausência de `--fields` trazem os valores. Um protótipo do one-shot que lia o marcador com
  `--fields attributes` reenviou o convite; com `attributes(*)`, a trava de `tenant_id` e a do marcador dispararam.
- **A imagem do Keycloak não tem `jq`, `awk` nem `python3`** [ao vivo]: tem `sh`, `bash`, `grep`, `sed`, `tr`, `cut`,
  `sort`, `date`, `head` e `mktemp`. O protótipo leu a saída indentada do `kcadm` (uma chave por linha) com `sed` e
  `grep`.
- **E-mail e caixa** [ao vivo]: o import grava username e e-mail em minúsculas, e a busca `email=...&exact=true` não
  diferencia maiúsculas. Uma comparação exata com um `PLATFORM_ADMIN_EMAIL` em maiúsculas falharia.
- **`kcadm update realms/... -s attributes.X=...` reescreve o realm inteiro** [ao vivo + código]: faz GET, mescla e
  PUT da representação. Na réplica, apareceram nos atributos os `client*Session*` e o `scimApiEnabled` com os valores
  padrão, e o SMTP, as Organizations, o `registrationAllowed` e o `bruteForceProtected` ficaram intactos. Um `-f` com
  um JSON só com o atributo **apaga** os demais atributos do realm (`DefaultExportImportManager.java` L797-807). Cada
  gravação gera um evento administrativo `UPDATE REALM`.
- **`docker compose run --rm --no-deps platform-admin-invite`** [ao vivo, revisão] com o convite pendente roda sobre
  os mesmos volumes e a mesma rede, sai `0` sem reenviar, e o `run` devolve o código de saída do container (`0` e `1`
  vistos).
- **O refresh token sobrevive a `docker compose stop keycloak` e `start`** [ao vivo, revisão], com o Postgres e as
  sessões persistentes da 26.7.4: o refresh emitido antes do `stop` rendeu um access token novo depois do `start`, com
  o mesmo `sub`, `aud`, `azp` e `roles`. O refresh vale 30 min de inatividade (`refresh_expires_in: 1800`, o SSO
  Session Idle padrão). A verificação foi sem `revokeRefreshToken` (ver os não verificados, no fim da §3).
- **O `oauth2.device.code.lifespan` vale no client importado** [ao vivo, revisão]: `expires_in: 300`; um device code
  sem aprovação deu `authorization_pending` em t ≈ 288 s e `expired_token` em t ≈ 308 s.
- **Volume antigo** [ao vivo]: `Realm 'identity-gateway' already exists. Import skipped` (`IGNORE_EXISTING`); o
  one-shot, conferindo o scope `gateway-api`, saiu com `1` e a instrução do `down -v`.
- **Marcador em arquivo** [ao vivo]: com o usuário padrão da imagem (uid 1000) num volume do root, o e-mail saiu e a
  gravação falhou; três execuções, três e-mails. É o motivo do DT8.
- **Placeholder no usuário do JSON** [ao vivo]: `${PLATFORM_ADMIN_EMAIL}` em `username` e `email` foi substituído no
  import, e o usuário nasceu sem credencial, com as duas ações obrigatórias e só o papel `platform-admin`. Variável
  ausente fica literal, sem erro (fato da fatia C).

### 3.3. ASP.NET Core

- **O `ValidIssuer` não restringe** [spike + ao vivo]: com um `ConfigurationManager`, `Validators.ValidateIssuerAsync`
  aceita o token se `configuration.Issuer == iss`, antes de olhar `ValidIssuer` e `ValidIssuers`
  (`Validators.cs` L308-319). Um `ValidIssuer` errado passou. O `IssuerValidator` tem precedência sobre tudo e não
  recebe a configuração (`Validators.cs` L282-289). Ele deve lançar `SecurityTokenInvalidIssuerException`, que é
  recuperável e dispara um refresh limitado pelo `RefreshInterval` (`TokenUtilities.cs` L289-295).
- **Keycloak fora com metadados frios = `401`, não `500`** [spike]: a falha de busca é engolida
  (`JsonWebTokenHandler.ValidateToken.cs` L572-580), e a validação falha por falta de chave. O aviso sai pelo
  `EventSource` do IdentityModel, não pelo `ILogger`: sem um log nosso, o Keycloak fora vira `401` em silêncio. Sem
  configuração, cada pedido tenta de novo, **em fila** atrás de um semáforo (`ConfigurationManager.cs` L225-227),
  com o `BackchannelTimeout` padrão de 60 s (`JwtBearerOptions.cs` L94); três pedidos com 2 s de timeout
  terminaram em 2, 4 e 6 s. Quando o Keycloak volta, o primeiro pedido já passa.
- **Cache de metadados com o Keycloak parado** [ao vivo]: com a configuração já carregada, um token de `kid`
  conhecido recebeu `200` durante **8 min 8 s** de `docker pause`, e depois com `docker stop`, inclusive além do
  prazo automático de refresh; as falhas de refresh não invalidam o cache. Um `kid` desconhecido leva `401`.
- **Rotação de chave** [spike + ao vivo]: o **primeiro** pedido com um `kid` novo leva `401` mesmo com o Keycloak no
  ar, porque o refresh roda em segundo plano (`ConfigurationManager.cs` L486-500, L616); o segundo passa. Com o
  `RefreshInterval` padrão de 5 min, uma segunda rotação dentro do intervalo fica em `401` até o fim dele.
- **`RequireHttpsMetadata` incoerente com o `MetadataAddress`** [spike]: a checagem roda no `PostConfigure`, no
  primeiro pedido (`JwtBearerPostConfigureOptions.cs` L47-50). A app sobe, e **todo** pedido, anônimo ou não, dá
  `500`.
- **Claims** [spike]: com `MapInboundClaims=false`, o array `roles` vira um claim por elemento; `IsInRole` funciona
  com `RoleClaimType = "roles"`.
- **Autorização** [spike]: o `resource` do requirement é o `HttpContext` (`AuthorizationMiddleware.cs` L183-193);
  `GetRouteValue("tenantId")` devolve o texto cru da URL, mesmo com `:guid`. O mesmo tenant em maiúsculas ou no
  formato `N` passou quando a comparação foi por `Guid`. Uma policy de tenant numa rota sem `{tenantId}` dá `403`.
  `Fail()` derruba qualquer `Succeed`, e `InvokeHandlersAfterFailure` é `true` por padrão: todos os handlers rodam.
  O `DefaultAuthorizationService` percorre os handlers na ordem da DI; o `PassThroughAuthorizationHandler`, que roda
  os requirements que são o próprio handler, entra pelo `AddAuthorization` com `TryAddEnumerable` [código]. O
  `AuthorizationHandlerContext` não tem `CancellationToken`. Sem `IAuthorizationMiddlewareResultHandler` próprio, o
  `403` sai sem corpo e o `401` só com o `WWW-Authenticate`; o projeto não tem `AddProblemDetails` [código].
- **NetArchTest 1.3.2 enxerga corpos de método e closures** [spike]: a regra contra `SymmetricSecurityKey`, rodada
  contra o `IdentityGateway.Api.dll` atual, reprova `DependencyInjection` e `JwtTokenService`. O teste não nasce
  vacuoso. Mas o NetArchTest resolve dependências por **tipo**, não por membro: `IssuerSigningKey`,
  `IssuerSigningKeys`, `SignatureValidator`, `IssuerSigningKeyResolver` e os `Validate*` são propriedades de
  `TokenValidationParameters`, e uma regra sobre elas ficaria verde vacuosa. Constantes (`JwtRegisteredClaimNames.Sub`)
  são embutidas no IL e também não aparecem [código].
- **File-based app no repositório** [réplica ao vivo, com cópias dos `Directory.*.props`]: `#:project` apontando para
  um projeto sob `tests/` herda `xunit.v3`, `OutputType=Exe` e `IsTestProject=true`, e dá `CS7022` (dois `Main`). O
  `PublishAot=true` padrão, com `TreatWarningsAsErrors`, transforma o JSON por reflexão em `IL2026`/`IL3050`. As duas
  correções (§4.6) foram verificadas, e um projeto de teste que usa o projeto de suporte continua passando.
- **O `IConfiguration` mescla arrays por índice**, não por substituição [código do framework].

**NÃO VERIFICADO** (vira spike antes do plano, §8, ou limite na v2.7):
- o marcador `platformAdminInviteSentAt` como atributo do realm, gravado e lido pelo `kcadm`, como premissa de
  segurança do one-shot: é o spike que resta (§8);
- o refresh token com `revokeRefreshToken` ligado depois de reiniciar o Keycloak: a verificação do `stop`/`start` foi
  sem a rotação. A CI da D1 o prova (§4.7); se falhar, o app e o README obtêm um token novo pelo device flow depois do
  `start`;
- o cache de metadados além de ~9 min com o Keycloak fora (inferência pelo comportamento observado);
- o header `at+jwt` (atributo `access.token.header.type.rfc9068`, desligado por padrão), que não é usado;
- se o mailpit valida o `Host` contra *DNS rebinding*;
- herdado da fatia C: se a troca de e-mail pela account console exige verificação na 26.7.4.

## 4. Design

### 4.1. Componentes e fluxo

```
docker compose up
  gateway-keys ──► keycloak (import: realm + platform-admin sem senha) ──► platform-admin-invite (kcadm, uma vez)
                                                                                   │ e-mail de ações ──► mailpit
                                                                                   ▼
                                                                                  api

Navegador ──link do mailpit──► Keycloak: senha e perfil (VERIFY_EMAIL resolvido pelo link)
curl/app ──device flow (identity-gateway-demo)──► access token RS256, 5 min:
    iss = http://localhost:8081/realms/identity-gateway   aud = identity-gateway-api   azp = identity-gateway-demo
    sub, typ = Bearer, roles = [catálogo], tenant_id (só o admin de tenant)
       │ Authorization: Bearer
       ▼
API ── JwtBearer: metadados em http://keycloak:8080/.../.well-known, emissor público estrito, RS256, aud, 30 s
    ── OnTokenValidated: azp na lista, typ = Bearer, sub GUID
    ── FallbackPolicy: autenticado
    ├─ POST /tenants, GET /tenants/{id}/provisioning ──► PlatformAdmin                                  (D1)
    └─ GET /tenants/{tenantId} ──► TenantAdmin = tenant-admin ∧ ¬platform-admin ∧ mesmo tenant ∧ Member (D2)
```

### 4.2. Validação na API

**`AccessTokenValidationOptions`** (`Infrastructure/Configuration`, pública, seção `Keycloak:Auth`). A Api só a lê;
não conhece o Keycloak.
- Da configuração: `Audience` (padrão `identity-gateway-api`) e `AllowedClients` (a lista de `azp`).
- Derivados pelo adaptador do Keycloak em `AddKeycloakIdentity`, com setter `internal`, que o binder não preenche:
  - `Issuer` = `{PublicBaseUrl ?? BaseUrl}/realms/{Realm}`, **a mesma propriedade** que alimenta o `aud` do
    assertion. O `KeycloakAdminOptions` ganha `Issuer`, e `AssertionAudience` passa a ser `=> Issuer`;
  - `MetadataAddress` = `{BaseUrl}/realms/{Realm}/.well-known/openid-configuration`, pelo endereço interno, sem
    `Authority`;
  - `RequireHttpsMetadata` = `!AllowInsecureHttp`, a regra que já é validada na subida (`AllowInsecureHttp` só em
    Development).
- `ValidateOnStart`: `Audience` obrigatório; fora de Development, `AllowedClients` **não pode** conter
  `identity-gateway-demo`, com mensagem neutra. Lista vazia é permitida e fail-closed: a API sobe, recusa todo token de
  usuário e registra um aviso na subida. O casamento é por igualdade ordinal; `azp` ausente, vazio ou que não seja
  texto leva a `401`.
- `appsettings.json` **não** tem a lista. `appsettings.Development.json` lista o `identity-gateway-demo`, e o compose,
  que roda em Development, herda. Produção informa a lista pelo ambiente.
- A documentação de `PublicBaseUrl` muda: ele alimenta o `aud` do assertion **e** o emissor aceito, e continua
  nunca discado.

**JwtBearer** (`Api/DependencyInjection`, por `AddOptions<JwtBearerOptions>(scheme).Configure<IOptions<...>>`, que
roda antes do `JwtBearerPostConfigureOptions`):
- `MetadataAddress`, `RequireHttpsMetadata`, `BackchannelTimeout` 5 s, `RefreshInterval` 30 s,
  `MapInboundClaims = false`;
- `IssuerValidator` estrito, `internal static` para ter teste próprio;
- `ValidAudience`, `ValidAlgorithms = [RS256]`, `ValidateLifetime`, `RequireExpirationTime`, `ClockSkew` 30 s;
- `NameClaimType = "sub"` (o username é o e-mail, e `preferred_username` o levaria a logs), `RoleClaimType = "roles"`;
- `IncludeErrorDetails = false` em **todo** ambiente: o `WWW-Authenticate` ecoaria o `iss` e o `aud` recusados, que
  vêm do token (um `iss` com caractere de controle faria o Kestrel recusar o cabeçalho, e o `401` viraria `500`). O
  diagnóstico sai pelo log do `OnAuthenticationFailed`, e não sobra ramo por ambiente.

```csharp
internal static IssuerValidator EmissorEstrito(string esperado) => (issuer, _, _) =>
    string.Equals(issuer, esperado, StringComparison.Ordinal)
        ? issuer
        : throw new SecurityTokenInvalidIssuerException("Emissor do token não é o do realm configurado.")
        {
            InvalidIssuer = issuer,
        };
```

**`OnTokenValidated`** chama `context.Fail` (→ `401`) se:
- o `azp` não estiver na lista;
- o claim `typ` não for `"Bearer"`. O cabeçalho não distingue (§3.1), e o ID token já cairia na audiência: é defesa
  em profundidade;
- o `sub` estiver ausente ou não for GUID no formato `D`.

**`OnAuthenticationFailed`** registra, por `LoggerMessage` como o resto do projeto, um `Warning` quando a exceção é de
chave ou de configuração (com metadados frios, a falha chega como falha de chave, §3.3), e o tipo da exceção em
`Debug` nos demais casos, nunca o token. É o log do DT6, com teste em `Production` (§5.1). A lista de tipos fica no
plano.

**Autorização global:** `FallbackPolicy` = usuário autenticado. `/health/live`, `/health/ready`, o OpenAPI e o Scalar
de Development e o redirect de `/` ganham `AllowAnonymous` explícito. Um teste enumera os endpoints e exige, em cada
um, policy nomeada ou anonimato declarado. Efeito visível: um caminho não mapeado, sem token, passa a receber `401`.

**Problem Details para `401` e `403`.** Hoje nada os produz (§3.3). Entram `AddProblemDetails()` e um
`IAuthorizationMiddlewareResultHandler` em `Api/Authorization`:
- `Forbidden` → `403` com **um** Problem Details fixo (`type`, `title` e `detail` constantes, sem nada que distinga o
  motivo, com o `correlationId`), escrito por uma função só, `RespostasDeAutorizacao.Proibido(HttpContext)`, que o
  módulo da rota também usa (§4.3);
- `Challenged` → delega ao `ChallengeAsync` padrão (mantém o `WWW-Authenticate`) e escreve um Problem Details `401`
  fixo;
- os demais casos → o `AuthorizationMiddlewareResultHandler` padrão.

O teste "o mesmo Problem Details" compara `status`, `type`, `title` e `detail`, sem `traceId`, `correlationId` nem
`instance`, que variam por requisição.

**`ICurrentUser`** não muda: já lê o `sub`. O remarks "autenticação ainda não configurada" sai. O `tenant_id` não
entra nele nesta fatia: a rota usa o `tenantId` da rota, já autorizado pela policy.

**Sai:** `JwtOptions` e o registro dele na Infrastructure, a seção `Jwt` dos appsettings, `Jwt__SigningKey` do
compose, o `JwtTokenService` e o uso de `System.IdentityModel.Tokens.Jwt` no código da Api: o `ChaveDaParticao`
(`DependencyInjection.cs`) e o `HttpCurrentUser` (`Services/HttpCurrentUser.cs`, que usa
`JwtRegisteredClaimNames.Sub`) passam a usar `"sub"`. O `Api.csproj` não tem `PackageReference` para esse pacote: ele
chega transitivo pelo JwtBearer e continua copiado, e não há o que tirar do `.csproj`. O `IssuerValidator` usa
`Microsoft.IdentityModel.Tokens`, também transitivo; o plano decide se a Api declara o `PackageReference` explícito
no `Directory.Packages.props`, na versão 8.19.2 que já resolve. A `IdentityGatewayApiFactory` é reescrita (§5.5). Os
remarks que diziam "troque `IssuerSigningKey` por `Authority`, é a única mudança" são reescritos.

### 4.3. Policies e a rota (D2)

**`Policies`** nasce em `Api/Authorization/Policies.cs`, e as duas rotas existentes trocam o literal
`"PlatformAdmin"` pela constante. **`Policies.PlatformAdmin`** continua `RequireClaim("roles", "platform-admin")`.

**`Policies.TenantAdmin`**, quatro requirements em `Api/Authorization`, cada um com `Fail()` em **todo** caminho que
não é sucesso:

| Requirement | Sucesso só quando |
|---|---|
| `RoleRequirement("tenant-admin")` | o claim `roles` contém `tenant-admin`. É um handler próprio, e não o `RequireClaim`, porque o `RequireClaim` só deixa de dar `Succeed` e não chama `Fail()`: um handler que aprovasse tudo o satisfaria |
| `NotPlatformAdminRequirement` | o claim `roles` **não** contém `platform-admin` (D-g) |
| `SameTenantRequirement` | o `Resource` é `HttpContext`, a rota tem `tenantId`, há **exatamente um** claim `tenant_id`, os dois são GUID no formato `D` (`Guid.TryParseExact`) e iguais como `Guid` |
| `MemberRequirement` | o `sub` é `Member` do tenant da rota com status em `{Invited, Active}` (D-f, D-j) |

- `InvokeHandlersAfterFailure = false`, e o handler do `MemberRequirement` sai sem consultar o banco se o contexto
  já falhou. Assim o banco só é consultado para quem já passou nas três camadas do token, e o tempo de resposta não
  vira oráculo do vínculo `(sub, tenant)`.
- **A ordem de registro é explícita.** Os três primeiros são o próprio handler (`AuthorizationHandler<T>,
  IAuthorizationRequirement`) e rodam dentro do `PassThroughAuthorizationHandler`, que o `AddAuthorization` registra
  (§3.3). O handler do `MemberRequirement` precisa de DI e entra **depois** dele: se entrasse antes, rodaria primeiro,
  com o contexto ainda sem falha, e consultaria o banco para qualquer `tenantId`. Um método só,
  `AddAutorizacaoDaGateway` (`Api/Authorization`), chama o `AddAuthorization` (policies, `FallbackPolicy`,
  `InvokeHandlersAfterFailure`) e só então registra o handler, `Scoped`; produção e testes unitários usam o mesmo
  método. Um teste funcional, com a DI real e uma porta falsa que conta as chamadas, prova que a porta não é
  consultada com o papel ausente, com outro tenant nem com `platform-admin`. O teste do caminho positivo (`200`) pega
  um registro esquecido.
- `InvokeHandlersAfterFailure` é global: vale para todas as policies. Com ele falso, um handler que viesse depois de
  um `Fail()` não roda, e por isso a negação não pode ser auditada por handler. Nota para a fatia E: a auditoria de
  acesso e de negação nasce fora dos handlers (no `IAuthorizationMiddlewareResultHandler` da §4.2 ou no endpoint).
- O "claim múltiplo" é defesa de forma: com `multivalued=false`, o Keycloak nunca emite dois (§3.1). O ramo é
  testado com o OIDC falso.
- A pertença é lida por uma **porta da Application com o tenant na assinatura** (§6.4), no padrão `I*Queries` do
  repositório: `IMemberQueries.GetStatusAsync(TenantId, ExternalUserId, CancellationToken)`, que devolve
  `MemberStatus?`, em `Application/Common/Abstractions`, implementada em
  `Infrastructure/Persistence/Queries/MemberQueries.cs` com `AsNoTracking()` e registrada ao lado do `ITenantQueries`.
  O `CancellationToken` é o `HttpContext.RequestAborted` (o `Resource` já é o `HttpContext`). O `sub` é passado ao
  `ExternalUserId` sem normalizar, como o Keycloak o emite (minúsculo). A policy usa a porta, e não o Mediator, porque
  a Api só fala com o Mediator dentro de `Api.Modules`; os behaviors não se aplicam a essa leitura. Um teste de
  arquitetura proíbe o requirement de depender da Infrastructure. **O `IMemberRepository` não muda**: continua só
  com `Add`.

**`GET /api/v1/tenants/{tenantId:guid}`**, no `TenantsModule`, com `RequireAuthorization(Policies.TenantAdmin)`:
- `401` sem token ou com token inválido;
- `403` para platform-admin (D-b), tenant-admin de outro tenant, token sem `tenant-admin`, `sub` que não é `Member`
  com status aceito, e tenant inexistente (D-k). Todo `403` é o mesmo Problem Details da §4.2, sem nada que distinga
  o motivo: a fatia E vai auditar negações, e a suíte trava o contrato desde já;
- `200` com **exatamente** as chaves `tenantId`, `name`, `slug`, `status`, `plan` (`tier`, `maxUsers`,
  `maxClients`), `occupiedSeats` e `registeredAt`. **Nunca o e-mail.** Não há estado em que o e-mail esteja
  preenchido e a rota responda `200`: o `Member` só nasce no `CompleteProvisioning`, que ativa o tenant e apaga o
  e-mail. Por isso a prova é o conjunto de chaves, e não a ausência do e-mail num corpo em que ele já seria nulo;
- tipos, no padrão do `GET .../provisioning`: `GetTenantQuery` e o handler em `Application/Tenants/GetTenant/`; a
  porta ganha `ITenantQueries.GetDetailsAsync(TenantId, ct)`, que projeta o read model `TenantDetailsView` (sem
  `InitialAdminEmail`, lendo o `Plan` pelo `ComplexProperty`), e o handler o converte no DTO `TenantDetailsResponse`,
  com `status` e `tier` em texto (`ToString()`, como no handler existente). Um teste por reflexão trava os dois: nenhuma
  propriedade do tipo `Email` nem com `Email` no nome. O read model serve à listagem da fatia E;
- tenant não achado no handler, o que a policy já torna impossível: o handler devolve `TenantErrors.NotFound`, como o
  padrão, e o módulo **não usa o `ParaOk`** nesta rota (ele traduziria o erro no status de não encontrado, contra a
  D-k): faz o `Match` com `onFailure` igual a `RespostasDeAutorizacao.Proibido`, o mesmo `403` da §4.2. O `Domain` não
  ganha `ErrorType` novo. A rota não usa `404` nesta fatia. Um teste unitário do mapeamento, sem HTTP, confere que a
  falha vira esse `403`;
- no OpenAPI, como as rotas atuais: `Produces<TenantDetailsResponse>(200)`, `ProducesProblem(401)` e
  `ProducesProblem(403)`, sem exemplos por resposta (o `AddOpenApi()` nativo não tem API simples para isso);
- os nomes de `TenantStatus` viram contrato público, e um teste trava os de `TenantStatus` e de `MemberStatus`;
- o teste de subida da §13 da v2.6 entra com a primeira policy de tenant: ele varre o `EndpointDataSource` e reprova
  se um endpoint com `TenantAdmin` (e, no futuro, `TenantReadAccess`) não tiver `{tenantId}` no template;
- o `GET .../provisioning` continua respondendo `200` com o estado, e não `303`: o platform-admin leva `403` na rota
  nova. O comentário do `TenantsModule` sobre "`GET /tenants/{id}`, que ainda não existe" muda.

**O override, na fatia E (DT13).** A v2.7 registra que o override é uma policy própria, `TenantReadAccess`, com **um**
handler que decide os dois caminhos: platform-admin, com a auditoria gravada fora do handler (a nota acima); ou
tenant-admin ∧ ¬platform-admin ∧ mesmo tenant ∧ `Member`. A rota troca `TenantAdmin` por `TenantReadAccess`, e
`SameTenantRequirement` e `TenantAdmin` mantêm o `Fail()` incondicional.

### 4.4. Realm (D1)

Tudo o que muda no `realm-identity-gateway.json` entra na D1, para a D2 não exigir outro `down -v` nem ensinar o
one-shot a distinguir dois realms.

- **Catálogo:** `platform-admin`, `tenant-admin`, `financial-manager` e `reader`, **nunca compostos**. O
  `checkAdminRoles` do Keycloak olha só o nome do papel: um papel do catálogo composto com papéis de
  `realm-management` seria atribuível pelo `manage-users`. `offline_access` e `uma_authorization` são declarados só
  para sair do papel padrão; nenhum client oferece `offline_access`, e o papel padrão fica
  `[manage-account, view-profile]`, sem papel do catálogo.
- **Três scopes novos, nenhum default do realm:**
  - `gateway-roles`: mapper `oidc-usermodel-realm-role-mapper`, claim `roles`, multivalorado, só no access token; os
    scope mappings são só os quatro papéis do catálogo;
  - `gateway-tenant`: mapper `oidc-usermodel-attribute-mapper`, `tenant_id` plano, `multivalued` e `aggregate.attrs`
    em `"false"`;
  - `gateway-api`: só o `oidc-audience-mapper` com `identity-gateway-api`, `access.token.claim: "true"`,
    `id.token.claim: "false"` e sem `lightweight.claim`.
- **`"CreateDefaultClientScopes": "true"`** nos atributos do realm. Como o atributo não persiste, a presença dos
  embutidos é provada no fixture, não no realm importado.
- **Clients com scopes explícitos**, para nenhum herdar os defaults:
  - `identity-gateway` (service account): `basic` e `roles`, sem `gateway-*`, `fullScopeAllowed: true` (DT10). O
    `roles` é obrigatório: é dele que vem o `resource_access` que o health check lê;
  - `identity-gateway-demo`: público, só device flow (sem standard flow, implícito, direct grant nem service account,
    sem `redirectUris`), `basic`, `acr`, `gateway-roles`, `gateway-tenant` e `gateway-api`, sem `profile` nem
    `email`, `optionalClientScopes` vazio, `fullScopeAllowed: false`, `oauth2.device.code.lifespan` de 300 s. A
    descrição diz "demonstração local; não vai ao Terraform de produção".
- **Realm:** `accessTokenLifespan: 300` explícito (já é o padrão, e fica travado); `registrationAllowed: false`;
  `bruteForceProtected: true`; `resetPasswordAllowed: false` (desde a fatia C); nenhum grupo nem `defaultGroups`;
  **rotação do refresh token** (D-n): `revokeRefreshToken: true` e `refreshTokenMaxReuse: 0`, e um refresh token já
  usado é recusado. Quem renova (o harness, o app de CI e a demonstração) guarda sempre o refresh token novo.
- **Usuário do bootstrap:** `username` e `email` = `${PLATFORM_ADMIN_EMAIL}`, `enabled`, `emailVerified: false`,
  `requiredActions` `UPDATE_PASSWORD` e `VERIFY_EMAIL`, `realmRoles` só `platform-admin`, sem `credentials`, sem
  atributos e sem grupos.
- Descrições em texto puro, sem chaves de i18n.

O trecho abaixo é a parte do JSON final que a fatia muda. Ela foi importada e exercitada ao vivo (§3) com o `acr` no
demo, o `oauth2.device.code.lifespan`, `registrationAllowed` e `bruteForceProtected`, na réplica da revisão. Só
`revokeRefreshToken` e `refreshTokenMaxReuse` entraram depois dela (D-n): são chaves comuns do realm, conferidas pela
prova viva do realm no fixture e pelo teste do refresh reusado (§5.1). O `smtpServer`, o User Profile e o service
account ficam como na fatia C.

```json
{
  "accessTokenLifespan": 300,
  "registrationAllowed": false,
  "bruteForceProtected": true,
  "revokeRefreshToken": true,
  "refreshTokenMaxReuse": 0,
  "attributes": { "CreateDefaultClientScopes": "true" },
  "roles": {
    "realm": [
      { "name": "tenant-admin", "description": "Administrador de um tenant. O tenant vem do atributo tenant_id do usuário." },
      { "name": "platform-admin", "description": "Operador da plataforma. Não pertence a nenhum tenant." },
      { "name": "financial-manager", "description": "Gestor financeiro de um tenant." },
      { "name": "reader", "description": "Leitura dentro de um tenant." },
      { "name": "offline_access", "description": "Declarado só para ficar fora do papel padrão: o projeto não emite offline tokens." },
      { "name": "uma_authorization", "description": "Declarado só para ficar fora do papel padrão: o projeto não usa UMA." }
    ]
  },
  "clients": [
    {
      "clientId": "identity-gateway",
      "fullScopeAllowed": true,
      "defaultClientScopes": ["basic", "roles"],
      "optionalClientScopes": []
    },
    {
      "clientId": "identity-gateway-demo",
      "name": "Demonstração por curl (device flow)",
      "description": "Client público de demonstração, SÓ do ambiente local: Device Authorization Grant e nada mais.",
      "publicClient": true,
      "standardFlowEnabled": false,
      "implicitFlowEnabled": false,
      "directAccessGrantsEnabled": false,
      "serviceAccountsEnabled": false,
      "fullScopeAllowed": false,
      "attributes": { "oauth2.device.authorization.grant.enabled": "true", "oauth2.device.code.lifespan": "300" },
      "defaultClientScopes": ["basic", "acr", "gateway-roles", "gateway-tenant", "gateway-api"],
      "optionalClientScopes": []
    }
  ],
  "users": [
    {
      "username": "${PLATFORM_ADMIN_EMAIL}",
      "email": "${PLATFORM_ADMIN_EMAIL}",
      "enabled": true,
      "emailVerified": false,
      "requiredActions": ["UPDATE_PASSWORD", "VERIFY_EMAIL"],
      "realmRoles": ["platform-admin"]
    }
  ],
  "clientScopes": [
    {
      "name": "gateway-roles",
      "protocol": "openid-connect",
      "attributes": { "include.in.token.scope": "false", "display.on.consent.screen": "false" },
      "protocolMappers": [{
        "name": "roles-plano", "protocol": "openid-connect", "protocolMapper": "oidc-usermodel-realm-role-mapper",
        "config": { "claim.name": "roles", "jsonType.label": "String", "multivalued": "true",
                    "access.token.claim": "true", "id.token.claim": "false", "userinfo.token.claim": "false",
                    "introspection.token.claim": "true" }
      }]
    },
    {
      "name": "gateway-tenant",
      "protocol": "openid-connect",
      "attributes": { "include.in.token.scope": "false", "display.on.consent.screen": "false" },
      "protocolMappers": [{
        "name": "tenant-id-plano", "protocol": "openid-connect", "protocolMapper": "oidc-usermodel-attribute-mapper",
        "config": { "user.attribute": "tenant_id", "claim.name": "tenant_id", "jsonType.label": "String",
                    "multivalued": "false", "aggregate.attrs": "false", "access.token.claim": "true",
                    "id.token.claim": "false", "userinfo.token.claim": "false", "introspection.token.claim": "true" }
      }]
    },
    {
      "name": "gateway-api",
      "protocol": "openid-connect",
      "attributes": { "include.in.token.scope": "false", "display.on.consent.screen": "false" },
      "protocolMappers": [{
        "name": "audiencia-identity-gateway-api", "protocol": "openid-connect", "protocolMapper": "oidc-audience-mapper",
        "config": { "included.custom.audience": "identity-gateway-api", "access.token.claim": "true",
                    "id.token.claim": "false", "introspection.token.claim": "true" }
      }]
    },
    {
      "name": "offline_access",
      "protocol": "openid-connect",
      "attributes": { "display.on.consent.screen": "false" }
    }
  ],
  "scopeMappings": [
    { "clientScope": "gateway-roles", "roles": ["platform-admin", "tenant-admin", "financial-manager", "reader"] }
  ]
}
```

**`RegrasDoRealmTests`**, regras novas sobre o JSON, cada uma provada por mutação:
- catálogo exato (os quatro papéis mais `offline_access` e `uma_authorization`), nenhum com `composite`, sem
  `defaultRole`; substitui o `PapelDeRealmTenantAdminExiste`;
- `CreateDefaultClientScopes` igual a `"true"` sempre que houver `clientScopes`;
- os três scopes existem, e nenhum está em `defaultDefaultClientScopes` nem em `defaultOptionalClientScopes`;
- o `gateway-roles` com um mapper só, as chaves acima e os scope mappings iguais ao catálogo; o `gateway-tenant` com
  `aggregate.attrs: "false"`;
- o audience mapper só no `gateway-api`, com `id.token.claim: "false"` e sem `lightweight.claim`;
- todo client declara `fullScopeAllowed`, `directAccessGrantsEnabled` (falso em todos), `defaultClientScopes` e
  `optionalClientScopes`; só o `identity-gateway` tem service account, e ele não tem `gateway-*` e tem `roles`;
- o demo só com device flow, `basic` nos defaults (senão o token sai sem `sub`), os três `gateway-*`, `acr`, sem
  `profile` nem `email`, sem `redirectUris`, `fullScopeAllowed: false` e o prazo do device code;
- nenhum outro client com o atributo do device flow;
- sem `groups` e sem `defaultGroups`;
- o scope `offline_access` declarado, sem mappers, fora de `defaultDefaultClientScopes` e de
  `defaultOptionalClientScopes`, e em nenhum client: o import recria o papel no padrão se faltar o papel **ou** o
  scope (`RealmManager.java` L664-666);
- `accessTokenLifespan` igual a 300; `registrationAllowed` falso; `bruteForceProtected` verdadeiro;
  `revokeRefreshToken` verdadeiro e `refreshTokenMaxReuse` igual a 0;
- exatamente um usuário além do service account, com `username` e `email` iguais a `${PLATFORM_ADMIN_EMAIL}`, só
  `platform-admin`, sem atributos, grupos nem papéis de client, e com as duas ações obrigatórias;
- o `NenhumaChaveDeCredencial` passa a percorrer também o JSON de `kc.user.profile.config` (dívida da fatia C).

As regras vigentes continuam, inclusive a de `components` só com o User Profile e o `TodoPlaceholderEPuro`, sem
afrouxar a regex.

### 4.5. Bootstrap do platform-admin (D1)

**`PLATFORM_ADMIN_EMAIL`**, com padrão `platform-admin@identity-gateway.local`, numa âncora do compose usada pelo
`keycloak` e pelo one-shot. O entrypoint do `keycloak` ganha `test -n "$$PLATFORM_ADMIN_EMAIL"`, como os três do
SMTP: sem valor, o import gravaria o placeholder literal. O mesmo entrypoint recusa um valor com maiúsculas, porque o
import grava o e-mail em minúsculas (§3.2); ainda assim, o one-shot e o app comparam o e-mail em minúsculas. Na CI, a
variável fica no `env:` do job, que o compose e o app leem igual. Um teste de arquitetura confere que o padrão é o
mesmo no compose, no app e no README, e que é minúsculo.

**One-shot `platform-admin-invite`**: imagem do Keycloak (`kcadm.sh` oficial, sem `apk add`), script inline em
`sh -euc` sem `-x`, `depends_on` do `keycloak` saudável (que já depende do `mailpit`). A credencial é a do **admin do
master**, lida de `/keys/admin-password` na subpasta `keycloak` do `gateway-keys`, montada só para leitura; o script
faz `KC_CLI_PASSWORD="$(cat /keys/admin-password)"; export KC_CLI_PASSWORD` dentro do próprio `sh`, nunca como
variável declarada no compose (que ficaria no `docker inspect`), usa `--config /tmp/kcadm.config` e o apaga por
`trap 'rm -f /tmp/kcadm.config' EXIT`, armado antes do primeiro `kcadm`, para a remoção valer também na falha. Nunca a
chave da Gateway: o `manage-users` dela atribuiria `platform-admin`, e a criação da conta de plataforma apareceria nos
eventos de administração como obra da Gateway. Nenhuma resposta do `kcadm` é ecoada, nem o link.

**Leitura das respostas.** A imagem não tem `jq`, `awk` nem `python3` (§3.2): o script lê com `grep` e `sed` o JSON
indentado do `kcadm`, uma chave por linha, nunca o CSV. Toda leitura de `attributes`, do usuário e do realm, é feita
sem `--fields` ou com `attributes(*)`: com `--fields attributes`, o objeto vem vazio, e a trava de `tenant_id` e a
leitura do marcador ficariam vacuosas. Cada trava tem prova por mutação ao vivo no plano (marcador presente → sem
envio; `tenant_id`, grupo ou papel extra no usuário → `1`).

Passos:
0. Login no master (`kcadm config credentials`). Recusado, sai com `1` e uma mensagem própria: "credencial do master
   recusada: o admin do compose foi alterado? `docker compose down -v`". O login vem antes de tudo, inclusive da
   leitura do marcador, e por isso trocar a senha ou apagar o admin do master do compose (o console da 26.x o chama de
   temporário e sugere trocá-lo) quebra toda subida seguinte, com a `api` junto. Fica no README e na §8.
1. O realm tem o scope `gateway-api`? Senão, sai com `1`: "realm anterior à fatia D (o import é `IGNORE_EXISTING`):
   `docker compose down -v`".
2. O realm tem o atributo `platformAdminInviteSentAt`? Então "convite já enviado em <data>; nada a fazer", saída
   `0`.
3. Busca o usuário por `email=<PLATFORM_ADMIN_EMAIL>&exact=true` e exige **exatamente um**. Confere: papéis de realm
   exatamente `platform-admin`, nenhum papel de client (`clientMappings` vazio no `role-mappings`), nenhum atributo
   `tenant_id`, nenhum grupo, e-mail igual ao configurado, comparado em minúsculas. Qualquer divergência sai com `1` e
   uma mensagem neutra. O one-shot **nunca** atribui papel.
4. Sem `UPDATE_PASSWORD` pendente, o convite já foi concluído: "já concluído; nada a enviar", saída `0`.
5. Grava `platformAdminInviteSentAt` (UTC) no realm **antes** de enviar, com `kcadm update realms/identity-gateway -s
   attributes.platformAdminInviteSentAt=...` (GET, mescla e PUT; nunca `-f` com o atributo sozinho, que apaga os
   demais atributos, §3.2), e então `execute-actions-email?lifespan=14400` (4 h, D-m) com `UPDATE_PASSWORD` e
   `VERIFY_EMAIL`. Se o envio falhar, sai com `1` apontando o comando de reenvio. Resultado: no máximo um link
   automático. Cada gravação gera um evento administrativo `UPDATE REALM`.

**Reenvio:** `docker compose run --rm -e REENVIAR=1 platform-admin-invite`, documentado no cabeçalho do serviço e no
README. Executa os passos 0, 1, 3, 4 e 5; só o 2 (o marcador) é pulado. Recusa rodar, com saída `1`, se o convite já
foi concluído (a regra do D12 da fatia C), avisa que os links anteriores ainda não usados continuam válidos até
expirar e regrava o marcador.

**A `api` ganha `platform-admin-invite: condition: service_completed_successfully`.** Assim o
`docker compose up --wait api` roda o one-shot, e um volume antigo falha no `up` com a causa no log, em vez de a API
subir e todo token dar `401`. O `KeycloakHealthCheck` não muda: ele pega o volume de antes da fatia C, e o one-shot
pega o de antes da D. Quem já tem volumes precisa de `docker compose down -v`, pela quarta vez; o README, o corpo do
PR e o handoff avisam.

### 4.6. Harness e o app de CI (D1)

**Projeto `tests/IdentityGateway.Testing.Keycloak`**, biblioteca e não projeto de teste:
- `IsTestProject=false`, `OutputType=Library`, `PackageReference Remove` de `xunit.v3`,
  `Microsoft.Testing.Extensions.CodeCoverage` e `AwesomeAssertions`, e `xunit.v3.extensibility.core` (novo no
  `Directory.Packages.props`, na mesma versão do `xunit.v3`, hoje 4.0.1, por causa do pinning transitivo) para o
  `IAsyncLifetime`. Entra no `.slnx`, e o job Build o compila;
- sem referência a `src/`: o `CriarProvider` e o `SlugUnico` ficam como extensões nos projetos de teste;
- conteúdo: o `KeycloakFixture` (sai do projeto de integração, com as dependências da §5.4), o
  `ClienteDoMailpit(Uri)` (filtro por destinatário exato, comparado em minúsculas, e extração do link pelo campo
  `Text`, a regra que a CI tem hoje em `jq`) e o harness de login.

**Harness de login por HTTP**, só device flow:
- recebe o endereço público e o de transporte (nos testes, `keycloak.test:8081` → porta mapeada; na CI,
  `localhost:8081` → `127.0.0.1:8081`) e reescreve a autoridade num `DelegatingHandler`, com `HtmlDecode` no
  `action`;
- cookies num jar manual (o Keycloak marca `Secure` e o `CookieContainer` não os devolve por `http`), apagando os
  vencidos;
- laço guiado pelo formulário, por id: `kc-form-login` (username e/ou senha, o que houver: identity-first em um ou
  dois passos, conforme exista Organization naquele instante), `kc-passwd-update-form`, `kc-update-profile-form`,
  consentimento (`accept`, com o `code` oculto), página de informação com o link de prosseguir, e o caminho "sem
  página de login" quando já há sessão. Limite de páginas; página desconhecida vira exceção com a etapa, o título,
  o id do formulário e os **nomes** dos campos, nunca os valores nem o HTML;
- entradas: `ConcluirLinkDeAcoesAsync(link, novaSenha)` e `TokenPorDispositivoAsync(usuario, senha)`, que conclui o
  login e o consentimento **antes** do primeiro poll, respeita o `interval`, soma 5 s a cada `slow_down` e trata
  `expired_token` e `access_denied` como falha imediata. Devolve também o `refresh_token` e oferece a renovação, que
  devolve sempre o refresh token novo: com a rotação (D-n), o antigo é recusado;
- log por `Action<string>`, sem `ITestOutputHelper`.

**O `KeycloakFixture`** passa `PLATFORM_ADMIN_EMAIL` ao container e **perde o ROPC**: o `TokenDeUsuarioComumAsync`,
com um chamador só (`UsuarioComum_NaoAlteraOTenantIdPelaAccountApi`), sai. Esse teste precisa de um token aceito pela
Account REST API (`manage-account` e `aud=account`, que vêm do scope `roles`), que o demo não emite; o fixture cria
pelo master, uma vez, um client público **só com device flow**, com o scope `roles`, usado só por ele e nunca
declarado no JSON. Some junto a pendência "cria um client por chamada e não o remove". O `grant_type=password` do
`admin-cli` do **master** continua: é a infraestrutura do Testcontainers, fora do realm da aplicação e fora do
ADR-003, e a v2.7 diz isso.

**Um platform-admin por teste.** No Testcontainers não há one-shot, e o link de ações é de uso único: com um
platform-admin só, dois testes paralelos disputariam o link, e o segundo receberia "Action expired". O fixture
oferece `NovoPlatformAdminAsync()`, que cria pelo master um usuário com e-mail único, só `platform-admin` e as duas
ações obrigatórias, dispara o `execute-actions-email`, lê o link no mailpit e o conclui pelo harness com uma senha
gerada; cada teste obtém o próprio token pelo device flow. O platform-admin do JSON fica para a prova viva do realm
importado (leitura crua pelo master) e para a CI, que consome o link dele.

**App `tools/jornada-compose.cs`** (.NET 10, arquivo único), fora de `tests/`, que herdaria o `xunit.v3`:

```csharp
#:project ../tests/IdentityGateway.Testing.Keycloak
// Ferramenta de CI, nunca publicada: sem o AOT padrão dos file-based apps, que transformaria o JSON por reflexão
// em erro (TreatWarningsAsErrors).
#:property PublishAot=false
```

- Sem `#:package`: tudo vem pelo `#:project`, e um teste de arquitetura proíbe `#:package` em `tools/*.cs`.
- Configuração por ambiente, com os padrões do compose: `IG_API`, `IG_KEYCLOAK_PUBLICO` (`http://localhost:8081`),
  `IG_KEYCLOAK_TRANSPORTE` (`http://127.0.0.1:8081`), `IG_MAILPIT` e `PLATFORM_ADMIN_EMAIL`. Na CI, sempre
  `127.0.0.1`: `localhost` pode tentar `::1` primeiro.
- Fases, uma por invocação:
  - `convites --esperado N`: conta as mensagens cujo destinatário é **exatamente** o `PLATFORM_ADMIN_EMAIL`
    (comparado em minúsculas) e confere que o link aponta para o endereço público, sem imprimi-lo. Espera até N ou
    até o prazo e, ao atingir, aguarda um intervalo curto antes de afirmar "exatamente N" (obrigatório para N = 0);
  - `jornada` (D1): link do platform-admin → device flow (o app registra o número de passos do login e **exige** um
    passo só: o platform-admin entra antes de existir qualquer Organization) → a receita HS256 antiga leva `401` →
    `POST /tenants` → `202` → `Active` (até 90 s) → **o convite do admin do tenant** (critério da fatia C, que sai do
    `jq` do YAML para cá): pelo menos uma mensagem para o e-mail único do admin (o e-mail duplicado entre envio e
    commit é dívida herdada), link no endereço público e `GET` do link → `200` com a página de ações
    (`kc-info-message`, sem `kc-error-message`), sem concluí-lo. A D2 troca essa
    checagem pela conclusão: link do admin convidado → device flow → `GET /tenants/{id}` → `200` com exatamente as
    chaves do contrato (§4.3) → `GET` de um GUID aleatório → `403` → platform-admin, com token renovado, → `403`;
  - `antes-de-parar`, `com-keycloak-parado` e `depois-de-voltar` (D1): um `GET` autenticado; com o Keycloak parado,
    `POST /tenants` → `202` e `Pending`; de volta, renova o token e espera `Active`.
- **O que cada fase grava para a seguinte.** Nenhuma fase depende de senha em memória de outra: a senha morre com o
  processo que a gerou. O estado passa por um arquivo só, com permissão `0600`, num diretório temporário (na CI,
  `$RUNNER_TEMP`; fora dela, o diretório temporário do usuário, informado em `IG_ESTADO`), apagado no passo de limpeza:
  - `jornada` grava, no fim, o refresh token do platform-admin (o último, depois de toda renovação);
  - `antes-de-parar` lê o refresh token, renova (e regrava o refresh token novo, por causa da rotação), faz o `GET`
    autenticado e grava o **access token** novo, que vale 5 min e cobre a janela do Keycloak parado;
  - `com-keycloak-parado` lê o access token (não pode renovar com o Keycloak fora), confere a pré-condição
    (`/health/ready` diferente de `200`), faz o `POST /tenants` e grava o `Location`;
  - `depois-de-voltar` lê o refresh token e o `Location`, renova e espera o `Active` com prazo derivado do backoff do
    Outbox e do `AttemptTimeout`.
  O arquivo guarda credenciais de um Keycloak descartável (refresh e access token), e isso fica dito.
- Nunca imprime access ou refresh token, `device_code`, `user_code`, `verification_uri_complete`, link de ação,
  senha nem HTML. As senhas são geradas em memória (`RandomNumberGenerator`). Ele mesmo faz as chamadas à API, sem
  passar token ao `curl`. O `catch` do topo corta as query strings das mensagens. `::add-mask::` só com
  `GITHUB_ACTIONS=true`.
- Toda asserção de status é exata (`== 403`), nunca "diferente de `200`": com 5 min de token, um `401` por
  vencimento viraria verde. O app renova antes de cada uso tardio.
- Saída: uma linha por etapa com o tempo; em falha, `::error title=<etapa>::<motivo>`, o título da página do
  Keycloak e os nomes dos campos, a tabela de etapas em `$GITHUB_STEP_SUMMARY` e um código de saída por família (10
  mailpit, 20 formulário, 30 device flow, 40 API, 50 prazo). Cada fase tem prazo interno, e o `HttpClient`, 10 s.

### 4.7. Compose, CI e README (D1, com o passo da D2)

**Compose:**
- o one-shot e a dependência da §4.5; `PLATFORM_ADMIN_EMAIL` na âncora;
- sai `Jwt__SigningKey`. O `MetadataAddress` e o emissor já saem do `BaseUrl` e do `PublicBaseUrl` que estão lá, e
  a lista de `azp` vem do `appsettings.Development.json`;
- a `api` publica em `127.0.0.1:8080`, e o Jaeger em `127.0.0.1:16686` e `127.0.0.1:4317`: agora circulam tokens
  reais. O `DependenciasComDadoPessoalPublicamSoEmLocalhost` cobre as três portas, com prova por mutação;
- o cabeçalho perde "a chave JWT é fixa" e ganha a frase do `down -v` da fatia D.

**Job `Compose`:**
- `defaults: run: shell: bash` no workflow, o que roda cada passo com `-eo pipefail`. O que resta em bash usa
  `|| true` onde a ausência é tratada logo abaixo;
- `setup-dotnet` e o cache do NuGet, com a mesma chave dos outros jobs; o app compila **antes** do Docker
  (`dotnet build -c Release tools/jornada-compose.cs`), para um erro de compilação falhar em segundos. O job Build
  também o compila;
- `timeout-minutes` em cada passo longo: um passo que estoura o próprio prazo é **falha**, e o diagnóstico roda. O do
  job fica em 20;
- passos, na ordem:
  1. `up -d --build --wait api` e o `ready`;
  2. **o convite sai uma vez só**: `convites --esperado 1`, `docker compose run --rm -T --no-deps
     platform-admin-invite` (saída `0`), `convites --esperado 1`. Com o convite pendente e o mailpit vivo, um one-shot
     sem marcador reenviaria e a contagem iria a 2 (a réplica da revisão viu exatamente isso com a leitura vacuosa do
     marcador, §3.2);
  3. `jornada`, que inclui o convite do admin do tenant (§4.6);
  4. **Keycloak parado**: `antes-de-parar`, `docker compose stop keycloak`, `com-keycloak-parado`,
     `docker compose start keycloak` com espera do `healthy`, `depois-de-voltar`. Trava o cache de metadados na
     topologia real e é a mesma sequência da demonstração nº 1 do README, que assim ganha prova automática. Fica
     `stop`/`start`, e não `pause`/`unpause`, porque o refresh depois do reinício virou fato (§3.2), porque o
     `stop` recusa a conexão na hora em vez de pendurá-la, e porque o README sem prova automática repetiria o buraco
     admitido na fatia C. O cache com o Keycloak pausado já foi verificado ao vivo (§3.3). Custo: cerca de 30 s de
     reinício. A rotação do refresh depois do reinício, que não foi verificada (fim da §3), é provada aqui;
  5. **segunda subida** sobre os mesmos volumes: `down`, `up --wait api`, `ready`, o one-shot com saída `0` e "já
     enviado" no log (lido por `grep -q ... <<< "$(docker compose logs platform-admin-invite)"`, para o `pipefail` não
     transformar o `SIGPIPE` em falha), `convites --esperado 0` (o mailpit não tem volume). Prova idempotência de
     subida, não o marcador;
  6. diagnóstico com `if: failure()`: `ps -a` primeiro, e logs **nomeados um a um**: `platform-admin-invite`, `api` e
     `keycloak`. O `gateway-keys` nunca entra (ele imprime a senha do master na primeira subida), nem no log do job nem
     em artefato. Do mailpit, só metadados (destinatário, assunto, data), nunca o corpo. Nenhum log com token, link,
     código ou senha;
  7. limpeza com `if: always()`: `down -v` e remoção do arquivo de estado do app.
- sai do YAML: o HS256 montado à mão (vira a asserção negativa dentro do app), o bloco em `jq` do convite (vira a
  checagem dentro da `jornada`) e o `localhost` nos `curl`. O tempo estimado do job passa de 2 min 20 s para cerca de
  5 min 30 s.

**README:**
- a demonstração nº 1 por device flow com `curl`, na ordem que funciona: abrir o mailpit, concluir o convite do
  platform-admin, device flow, **um `GET` autenticado antes do `stop`** (a API guarda as chaves no primeiro token),
  `docker compose stop keycloak`, `POST /tenants` → `202`, `docker compose start keycloak`, renovar o token pelo
  refresh, `GET .../provisioning` → `Active`. O texto explica o porquê da ordem, que o token vale 5 min, que o refresh
  vale 30 min de inatividade e que cada renovação devolve um refresh token novo (o usado é recusado);
- o passo da D2: o convite do admin num navegador em **janela anônima** (senão o cookie de SSO do platform-admin faz
  o segundo device flow sair como platform-admin, e o `403` parece defeito), o segundo device flow na mesma janela,
  `200` no próprio tenant, `403` num GUID qualquer, e `403` com o token do platform-admin, com o motivo (D-b);
- notas fixas: o mailpit local não tem autenticação, e o link do convite toma a conta enquanto não expira (o do
  platform-admin vale 4 h; depois, o comando de reenvio); o token no histórico do shell (usar `read -rs` ou o próprio
  app); o comando de reenvio; se o primeiro `up` falhou no envio do e-mail, o convite só sai pelo reenvio; o
  `down -v`; o console do master em `127.0.0.1:8081`, e que não se troca nem se apaga o admin do master do compose (o
  one-shot faz login com ele a cada subida; se mudou, `down -v`);
- a receita HS256 sai, e a contagem de testes e o andamento mudam.

## 5. Testes

### 5.1. Por nível

| Nível | O que prova | PR |
|---|---|---|
| Arquitetura | As regras do realm (§4.4). Nenhuma camada de produção com `SymmetricSecurityKey` (o NetArchTest vê tipos, e este é um tipo; vermelho antes da remoção, §3.3), e sem os tipos de `System.IdentityModel.Tokens.Jwt` na Api (o `JwtTokenService` reprova hoje; a constante `JwtRegisteredClaimNames.Sub` não aparece no IL, e por isso a troca por `"sub"` no `ChaveDaParticao` e no `HttpCurrentUser` vem da lista da §4.2, não da regra). Sem `ShowPII` nem `LogCompleteSecurityArtifact`. Sem `IClaimsTransformation` nem segundo esquema de autenticação. `AApiNaoConheceOKeycloak` verde. `tools/*.cs` sem `#:package`. Portas em `127.0.0.1`. O padrão de `PLATFORM_ADMIN_EMAIL` igual no compose, no app e no README, e minúsculo. A tag do Keycloak igual entre fixture e compose; o caminho novo do fixture em `ComposeEFixtureUsamAMesmaTagDoMailpit` | D1 |
| Arquitetura | Os nomes de `TenantStatus` e `MemberStatus` travados. O `MemberRequirement` não depende da Infrastructure. Por reflexão, nenhuma propriedade de `TenantDetailsView` e de `TenantDetailsResponse` do tipo `Email` ou com `Email` no nome. **Teste de subida da §13 da v2.6** (funcional, sobre o `EndpointDataSource` do host): todo endpoint com policy de tenant tem `{tenantId}` no template | D2 |
| Configuração | `AccessTokenValidationOptions.Issuer == KeycloakAdminOptions.AssertionAudience`, com `PublicBaseUrl ≠ BaseUrl` e com `PublicBaseUrl` nulo; `MetadataAddress` pelo `BaseUrl`; `RequireHttpsMetadata` segue o `AllowInsecureHttp`. **Opções resolvidas em execução** (não regra estática, §3.3): as `JwtBearerOptions` do esquema, lidas por `IOptionsMonitor<JwtBearerOptions>.Get("Bearer")`, têm `IssuerSigningKey` nulo, `IssuerSigningKeys` vazio, `SignatureValidator` e `IssuerSigningKeyResolver` nulos, `ValidateIssuer`, `ValidateAudience`, `ValidateLifetime` e `ValidateIssuerSigningKey` verdadeiros, `ValidAlgorithms` igual a `[RS256]` e `IncludeErrorDetails` falso | D1 |
| Host em `Production` | Com a factory em `Production` (§5.5): sobe com erro se a lista tiver o demo, se o `BaseUrl` for `http` ou se `AllowInsecureHttp` for verdadeiro (é também a prova da guarda `HttpSoEmDesenvolvimento`, pendente da fatia C). E, **com pedidos**, não só a subida: `RequireHttpsMetadata` verdadeiro e `BackchannelTimeout` de 5 s nas opções resolvidas; um token com `aud` errada leva `401` sem `error_description` no `WWW-Authenticate`; com a lista de `azp` vazia, a API sobe, registra o aviso e um token válido do demo leva `401`; com o `BaseUrl` num listener que aceita a conexão e nunca responde (metadados frios), um token leva `401` em menos de ~7 s (o padrão de 60 s reprovaria), com o `Warning` do DT6 capturado e sem o token no log | D1 |
| Funcional, OIDC falso | Um Kestrel em loopback serve discovery e JWKS de uma chave RSA de teste, só por configuração (`BaseUrl` aponta para ele; desenho na §5.5). O discovery anuncia um emissor **diferente** do configurado (`PublicBaseUrl`), e os tokens positivos usam o configurado. O emissor de teste imita o token real por padrão (`aud` string, `sub` GUID, `typ` `Bearer`, `azp` do demo, `acr` `"1"`, `roles` array, `tenant_id` string, 5 min) e aceita payload livre para os casos malformados. `CreateClientAutenticado` mantém a assinatura: os 16 testes que o usam não mudam no texto. A `FallbackPolicy`: um caminho não mapeado, sem token, leva `401`. A suíte negativa da §5.2 | D1 e D2 |
| Unitário da autorização | No `Api.FunctionalTests` (o único com `InternalsVisibleTo` da Api), em classes sem fixture, que não sobem Testcontainers (§5.5). `IAuthorizationService` montado pelo mesmo `AddAutorizacaoDaGateway` da produção, com a policy `TenantAdmin` real e um `HandlerQueAprovaTudo`: cada caminho de falha dos quatro requirements termina com `Failure.FailCalled`. `[Theory]` sobre `Enum.GetValues<MemberStatus>()` com a tabela esperada escrita à mão, pela porta falsa; um estado novo reprova até alguém decidir. A falha do handler da query vira o mesmo `403` (mapeamento do módulo, §4.3) | D2 |
| Funcional, ordem dos handlers | Com a DI real e um `IMemberQueries` falso que conta as chamadas: com o papel ausente, com outro tenant e com `platform-admin`, a porta não é consultada (§4.3) | D2 |
| Integração, PostgreSQL | `IMemberQueries` pelo tenant e pelo `sub`, sem achar o membro de outro tenant; `ITenantQueries.GetDetailsAsync` com a tradução do `Plan` e sem o e-mail na projeção | D2 |
| Integração, Keycloak real | Presença dos scopes embutidos e papel padrão `[manage-account, view-profile]`. Forma do token do demo (`sub` GUID, `roles` só do catálogo, `tenant_id`, `aud` string, `azp`, `typ`, `acr`, sem `email` nem `preferred_username`); o "`roles` ⊆ catálogo" com um usuário criado pela Admin API, que tem `default-roles-*`, conferindo antes, pelo master, que ele tem um papel fora do catálogo (o platform-admin do JSON não tem, e o teste seria vacuoso); o service account com `manage-users` e sem `identity-gateway-api`. **Rotação**: um refresh token já usado é recusado. O harness: o link zera as ações (leitura crua pelo master: `emailVerified` e `requiredActions` vazio), o login com Organization e o caminho com sessão; o login em um passo é provado só na CI, onde é determinístico (§4.6), porque no realm compartilhado a existência de Organization depende da ordem dos testes. Caracterização: grupo com `tenant_id` dá o claim a um usuário sem o atributo, para avisar se um upgrade mudar isso. O teste da Account API pelo client de device flow do fixture, cujo token **não** traz `identity-gateway-api` (prova, no realm vivo, que `gateway-api` não é default) | D1 |
| Coleção com Keycloak real na API | `ICollectionFixture` em `Api.FunctionalTests` com uma `ApiComKeycloakFactory` sem a chave de teste. Cada teste usa o próprio platform-admin (`NovoPlatformAdminAsync`, §4.6). **Ponte de contrato**: os tipos dos claims de um token real são os do emissor de teste. Platform-admin real → `POST /tenants` → `202`. **Audiência**: a factory põe na lista de `azp` também o client de device flow do fixture, que não tem `gateway-api`; o token dele tem `azp` aceito e não tem `identity-gateway-api` no `aud`, e o `401` só pode vir da audiência. Token do service account e token ROPC do `admin-cli` do realm, com um usuário de teste → `401`, com asserções sobre a forma do próprio token (`aud` sem `identity-gateway-api`, `azp` fora da lista), porque o `401` sozinho é sobredeterminado. Factory com o `PublicBaseUrl` errado → `401` | D1 |
| Coleção com Keycloak real na API | A `ApiComKeycloakFactory` liga o Outbox (`Outbox:Enabled=true`; o motivo do desligamento na factory comum, a corrida com asserções sobre a tabela do Outbox, não vale para uma coleção serial com banco próprio) e espera o `Active` com prazo explícito. O admin convidado conclui o convite e lê o próprio tenant (`200`, com exatamente as chaves do contrato); outro tenant `403` (afirmando antes que o `tenant_id` do token ≠ rota); platform-admin `403`; o ataque do grupo reproduzido pelo master (usuário sem `tenant_id`, com `tenant-admin`, num grupo com o `tenant_id` de um tenant existente), afirmando antes que o token **traz** o `tenant_id` herdado → `403` pelo `MemberRequirement` | D2 |
| Vazamento do e-mail | Estendido a tokens de forma Keycloak que carreguem `email` e `preferred_username` (forjados no OIDC falso): sucesso, `403`, expirado, `aud` errada e assinatura inválida, conferindo corpo, `WWW-Authenticate`, **log e trace**. Captura: um sink do Serilog em memória, com nível mínimo `Debug` inclusive em `Microsoft.AspNetCore.Authentication`, e um exportador OpenTelemetry em memória (§5.5). Nos logs e nos spans, procura o e-mail em texto, o e-mail em base64url (nos três alinhamentos possíveis, porque ele vai dentro do payload do token), o token cru e o segmento do payload. O mesmo coletor serve ao aviso da lista vazia e ao log do DT6 | D1 |
| Vazamento do e-mail | O `200` da rota com exatamente as chaves do contrato (§4.3) | D2 |
| CI (`Compose`) | O roteiro da §4.7 | D1 e D2 |

### 5.2. Suíte negativa

`F` = funcional com o OIDC falso; `U` = unitário da autorização; `K` = Keycloak real; `P` = host em `Production`. Os
casos de autenticação rodam nas **duas** rotas protegidas que existirem (`POST /tenants` na D1, e também o `GET` na
D2). Cada classe fica bem abaixo das 100 requisições por minuto do limitador, e os tokens forjados levam `sub` único.

| Caso | Esperado | Nível | PR |
|---|---|---|---|
| Sem token; token malformado; `Bearer` vazio; esquema `Basic` | `401` | F | D1 |
| Vencido há 2 min (acima dos 30 s, abaixo dos 5 min do padrão); `nbf` 2 min no futuro; sem `exp` | `401` | F | D1 |
| Caminho não mapeado, sem token | `401` | F | D1 |
| `aud` = `account`; sem `aud` | `401` | F | D1 |
| `aud` = `["identity-gateway-api","account"]` (controle) | sucesso | F | D1 |
| `iss` forasteiro; `iss` = o `BaseUrl` interno; com barra final, sufixo ou maiúsculas | `401` | F | D1 |
| **`iss` igual ao anunciado pelo discovery, diferente do configurado** | `401` | F | D1 |
| Outra chave RSA com o mesmo `kid`; `alg=none`; HS256 com a chave pública como segredo; **a receita HS256 antiga** | `401` | F | D1 |
| Claim `typ` = `ID` com a audiência certa | `401` | F | D1 |
| `azp` fora da lista; `azp` ausente; `azp` vazio; `azp` em array; `azp` numérico | `401` | F | D1 |
| Sem `sub`; `sub` não-GUID; `sub` no formato `N`; `sub` em array; `typ` em array | `401` | F | D1 |
| Host em `Production` com o demo na lista, com `BaseUrl` `http` ou com `AllowInsecureHttp` | a subida falha | P | D1 |
| Host em `Production`: `aud` errada | `401` sem `error_description` | P | D1 |
| Host em `Production` com a lista de `azp` vazia: token válido do demo | `401`, e o aviso na subida | P | D1 |
| Host em `Production` com metadados frios (listener mudo) | `401` em menos de ~7 s, com o `Warning` e sem o token no log | P | D1 |
| Token do client de device flow do fixture (`azp` na lista da factory, sem `identity-gateway-api`) | `401` | K | D1 |
| Token do service account; token ROPC do `admin-cli`; `PublicBaseUrl` errado | `401`, com a forma do token afirmada | K | D1 |
| Refresh token já usado | recusado pelo Keycloak | K | D1 |
| Platform-admin sem `tenant_id`; platform-admin com o `tenant_id` do tenant | `403` | F | D2 |
| **`platform-admin` + `tenant-admin` do próprio tenant** (D-g) | `403` | F + U | D2 |
| Tenant-admin de outro tenant **existente**; de outro tenant **inexistente** | `403` | F (+ K no existente) | D2 |
| Próprio tenant (`tenant_id` do claim) inexistente no banco (D-k) | `403` | F | D2 |
| `roles` sem `tenant-admin`; sem o claim `roles` | `403` | F + U | D2 |
| `tenant_id` ausente; vazio; não-GUID; com espaços, chaves ou formato `N` | `403` | F + U | D2 |
| `tenant_id` = `[próprio, outro]` e `[outro, próprio]`; `[próprio, próprio]` | `403` | F + U | D2 |
| `Resource` que não é `HttpContext`; rota sem `tenantId` | falha com `FailCalled` | U | D2 |
| `sub` sem `Member`; `Member` em cada status fora de `{Invited, Active}` | `403` | U (+ F sem `Member`) | D2 |
| `tenant_id` herdado de um grupo | `403` | K | D2 |
| Próprio tenant com o GUID da rota em maiúsculas; no formato `N` (controles) | `200` | F | D2 |
| Próprio tenant | `200` com **exatamente** as chaves do contrato | F + K + CI | D2 |
| Endpoint com policy de tenant sem `{tenantId}` no template | o teste de subida reprova | F | D2 |
| Todo `403` da rota | o mesmo Problem Details (`status`, `type`, `title`, `detail`) | F | D2 |

### 5.3. Prova por mutação

Cada teste novo é confirmado no estado vermelho, e a tabela vai no handoff de cada PR. Aplicar, ver vermelho (erro de
compilação não conta), reverter byte a byte, ver verde. Ficaram de fora as mutações equivalentes (`RoleClaimType`;
`NameClaimType`, que nenhum código lê; `ValidAlgorithms`, que a biblioteca já cobre para o HS256; devolver `404` no
handler, que a rota não usa e que a policy torna inalcançável, D-k; e, cada um sozinho,
`InvokeHandlersAfterFailure = true` ou o handler do `MemberRequirement` sem olhar o `HasFailed`, que se mascaram: só o
par é pego) e as que só repetiriam uma regra estática do realm.

| Mutação | Deve ser pega por | PR |
|---|---|---|
| Remover o `IssuerValidator` (manter `ValidIssuer`) | F: `iss` do discovery ≠ configurado (entre os F, **só ele**: os outros casos de `iss` seguem `401` pela validação padrão); K: `PublicBaseUrl` errado | D1 |
| Validador frouxo: comparar com o `BaseUrl`, `StartsWith` ou `OrdinalIgnoreCase` | F: todos os sucessos (`BaseUrl` ≠ `PublicBaseUrl` na factory); barra final, sufixo e maiúsculas | D1 |
| `ValidateAudience=false` | F: `aud` errada; K: token do client de device flow do fixture, com `azp` aceito e sem `identity-gateway-api`; opções resolvidas | D1 |
| `ClockSkew` padrão, ou `ValidateLifetime=false` | F: vencido há 2 min | D1 |
| Tirar a checagem do `azp`, ou aceitar qualquer `azp` com a lista vazia | F: `azp` fora, nas duas rotas; P: lista vazia com token válido | D1 |
| Ler o `azp` com `FindFirst` (aceita o array) | F: `azp` em array | D1 |
| Aceitar o demo fora de Development (tirar a recusa do `ValidateOnStart`) | P | D1 |
| Tirar a checagem do `typ`, ou a do `sub`; `TryParse` no lugar de `TryParseExact("D")` | F: `typ` = `ID`; sem `sub`; `sub` no formato `N` | D1 |
| `SymmetricSecurityKey` de volta em produção | Arquitetura, e só ela: a receita HS256 antiga leva `401` com ou sem a mutação (`ValidAlgorithms`, `iss` e `aud` antigos) | D1 |
| `IssuerSigningKey`, `IssuerSigningKeys`, `SignatureValidator` ou `IssuerSigningKeyResolver` fixos | Configuração: opções resolvidas em execução | D1 |
| `RequireHttpsMetadata=false` fixo | P: opções resolvidas em `Production` | D1 |
| A guarda `HttpSoEmDesenvolvimento` sempre verdadeira | P: a subida com `BaseUrl` `http` deixa de falhar | D1 |
| `IncludeErrorDetails=true` | Configuração: opções resolvidas; P: `error_description` no `WWW-Authenticate` | D1 |
| `BackchannelTimeout` padrão | P: metadados frios passam de ~7 s | D1 |
| Tirar o log do `OnAuthenticationFailed`, ou logar o token | P: metadados frios sem o `Warning`; vazamento: o token no log | D1 |
| `MapInboundClaims=true` | F: todos os `202` e `200` | D1 |
| Tirar a `FallbackPolicy` | F: caminho não mapeado sem token deixa de levar `401`. O teste que enumera os endpoints não a pega: ele confere metadata, que a mutação não muda | D1 |
| `aud` num scope default do realm, ou `gateway-*` nos defaults do service account | Regras do realm; K: o token do client de device flow do fixture sem `identity-gateway-api`, e a forma do token do service account | D1 |
| Tirar `CreateDefaultClientScopes` | Regra do realm; K: scopes embutidos presentes e `sub` no token | D1 |
| `fullScopeAllowed=true` no demo | Regra do realm; K: `roles` ⊆ catálogo, com o usuário que tem `default-roles-*` | D1 |
| `revokeRefreshToken` falso | Regra do realm; K: refresh token reusado recusado | D1 |
| Tirar o scope `offline_access` do JSON | Regra do realm; K: papel padrão `[manage-account, view-profile]` | D1 |
| One-shot sem o marcador, ou lendo o marcador com `--fields attributes` | CI: `convites --esperado 1` depois do `run` | D1 |
| `return` no lugar de `Fail()` num caminho de qualquer um dos quatro requirements | U, **e só ele**: sem outro handler, a rota continua `403` | D2 |
| Registrar o handler do `MemberRequirement` antes do `AddAuthorization` | Funcional da ordem dos handlers: a porta é consultada | D2 |
| Comparar o tenant como texto, ou usar o primeiro, o último ou "algum" `tenant_id` | F: GUID em maiúsculas e no formato `N`; as duas ordens do claim duplo | D2 |
| Tirar o `NotPlatformAdminRequirement` | F + U: `platform-admin` + `tenant-admin` | D2 |
| Tirar o `MemberRequirement`, ou aceitar qualquer status | K: ataque do grupo, com o `tenant_id` herdado afirmado no token; U: `[Theory]` sobre o enum | D2 |
| Consultar o tenant antes de autorizar | F: outro tenant inexistente `403`; próprio inexistente `403` | D2 |
| `InitialAdminEmail` no `TenantDetailsView` ou no `TenantDetailsResponse` | Arquitetura: reflexão; F + K + CI: o conjunto exato de chaves do `200` (uma chave a mais reprova, mesmo nula) | D2 |
| Policy de tenant numa rota sem `{tenantId}` | Teste de subida da §13 | D2 |
| Outbox desligado na `ApiComKeycloakFactory` | K: o tenant não chega a `Active` no prazo | D2 |

Registrado de antemão: na fatia C, o ambiente de execução recusou rodar a mutação da guarda https. Se recusar de
novo, o roteiro manual vai no handoff e a mutação não vira critério de aceite.

### 5.4. Testes existentes afetados

- **Deixam de compilar:** `DependencyInjectionTests.ChaveJwtCurta_FalhaAoValidar`, que sai, substituído pelos testes
  da `AccessTokenValidationOptions`; a `IdentityGatewayApiFactory` (`using IdentityGateway.Api.Security`,
  `Jwt:SigningKey` e o `JwtTokenService`), que é **reescrita** (§5.5), e não só muda no texto.
- **Limpeza das chaves `Jwt:*`:** `DependencyInjectionTests`, `ComposicaoDoProvisionamento` e
  `KeycloakHealthCheckTests`.
- **Reescritos:** `SegurancaTests.ComTokenInvalido_Retorna401`, que hoje recusa um HS256 por falta de chave simétrica
  e passaria a provar pouco: vira um RS256 de outra chave com o mesmo `kid`. O comentário de `SegurancaTests` sobre o
  `GET` anônimo fica desatualizado com a `FallbackPolicy` (caminho não mapeado sem token passa a `401`).
  `KeycloakRealTests.UsuarioComum_NaoAlteraOTenantIdPelaAccountApi`: o token passa a vir do client de device flow do
  fixture.
- **Mudam com o fixture:** todo o projeto de integração passa a usar o `KeycloakFixture` do projeto de suporte, com o
  namespace novo nos 11 arquivos que o usam; `RegrasDoAmbienteLocalTests` (caminho do fixture, portas novas e o
  `ValorNoCompose`, que usa `SingleOrDefault` e ficaria ambíguo se um one-shot repetisse uma variável). O fixture
  leva junto as dependências dele:
  - `ChavesDeTeste` e `ParDeChaves`, hoje `internal`, vão para a biblioteca e ficam públicas: o construtor do fixture
    usa a chave no `GATEWAY_CLIENT_CERT`, e a coleção da Api precisa do PEM;
  - `RaizDoRepositorio` vai junto;
  - o `LinkDoConviteAsync` usa AwesomeAssertions, que a biblioteca remove: passa a lançar exceção própria;
  - a regex `LinkDeAcoes`, fixa em `keycloak.test`, passa a receber o host público;
  - o `[assembly: AssemblyFixture]` fica em cada projeto de teste, não na biblioteca;
  - o `SlugUnico` (usa `IdentityGateway.Domain.Tenants`) fica como extensão nos projetos de teste;
  - o token do service account nos testes K precisa de uma assertion `private_key_jwt`, e o
    `ClientAssertionFactory` é `internal`: `InternalsVisibleTo` para o projeto que o usa, ou um helper na biblioteca.
- **Ficam compilando, fora da regra:** `ChaveDaParticaoTests` e `HttpCurrentUserTests` usam
  `System.IdentityModel.Tokens.Jwt`, que continua transitivo; a regra é só sobre produção.
- **Ficam verdes sem mudança:** os 16 testes funcionais com `CreateClientAutenticado`;
  `KeycloakRealTests.HealthCheck_ComKeycloakDePe_Healthy` e os do `KeycloakHealthCheck`, com o service account em
  `basic` e `roles`; `ServiceAccount_PapeisEfetivosSaoExatamenteOsDois`.
- **Mudam no texto:** `RegrasDoRealmTests.PapelDeRealmTenantAdminExiste` vira a regra do catálogo; os comentários de
  `HttpCurrentUser` (que também troca a constante por `"sub"`, §4.2), `DependencyInjection` e do `UserSecretsId` da
  Api (`Api.csproj`), e o comentário do `Directory.Packages.props` sobre o pacote JWT.
- **Contagem:** 491 → cerca de 570 (D1 com cerca de 50 a 55 novos, D2 com cerca de 25 a 30): a revisão acrescentou
  os testes em `Production` com pedidos, a captura de log e trace, o platform-admin por teste e a ordem dos handlers.

### 5.5. Infraestrutura dos testes

**A factory com o OIDC falso** (`IdentityGatewayApiFactory`, reescrita):
- o emissor de teste gera uma chave RSA em memória com um `kid` fixo; um Kestrel em loopback serve o discovery
  (com o emissor divergente) e o JWKS dessa chave, e responde não encontrado a todo o resto. A factory guarda o
  emissor, e o `CreateClientAutenticado` assina com ele, mantendo a assinatura;
- o Kestrel sobe no `InitializeAsync`, **antes** de qualquer acesso a `Services` ou `CreateClient`: o
  `WebApplicationFactory` aplica as `UseSetting` de forma preguiçosa, e o `BaseUrl` precisa existir quando ele as
  aplicar;
- com o `BaseUrl` no OIDC falso, o `/health/ready` fala com ele: a busca de token do service account recebe "não
  encontrado" e o `KeycloakHealthCheck` fica `Unhealthy`, como hoje com o `127.0.0.1:9`. O plano confere que nenhum
  teste funcional espera outra coisa;
- a captura de log e trace do teste de vazamento: um sink do Serilog em memória, configurado pelos settings, e um
  exportador OpenTelemetry em memória, registrado por `ConfigureTestServices`. É uma exceção declarada à regra "só
  configuração" da factory.

**O host em `Production`** é uma factory derivada, `ApiEmProducaoFactory`, parametrizada pela configuração do caso:
- `UseEnvironment("Production")` e tudo o que a subida exige fora de Development, pela configuração;
- o OIDC falso serve também **HTTPS** em loopback, com um certificado autoassinado gerado em memória, porque
  `RequireHttpsMetadata` é verdadeiro. Só esta factory, por `ConfigureTestServices`, troca o
  `BackchannelHttpHandler` das `JwtBearerOptions` por um que confia **exatamente** nesse certificado (por
  impressão digital): é a segunda exceção declarada à regra "só configuração", e não toca as opções que o teste
  confere;
- o caso dos metadados frios aponta o `BaseUrl` para um listener TCP que aceita a conexão e nunca responde.

**Onde ficam os testes unitários de autorização:** no `Api.FunctionalTests`, o único projeto com `InternalsVisibleTo`
da Api, em classes sem `IClassFixture`, que não sobem Testcontainers nem a factory. Eles montam o
`IAuthorizationService` com o mesmo `AddAutorizacaoDaGateway` da produção (§4.3).

## 6. Divisão em D1 e D2

**D1, cerca de 11 tarefas, um PR:** o projeto de suporte e o fixture movido; o realm inteiro (catálogo, scopes,
clients, usuário do bootstrap, regras) e a prova viva dele; o harness de device flow com a conclusão do link e os
testes de forma do token; a `AccessTokenValidationOptions`; a troca da autenticação com o OIDC falso e o fim do
HS256; as checagens de `azp`, `typ` e `sub` e a suíte negativa de autenticação; a coleção com Keycloak real até o
`POST /tenants`; o one-shot e o compose; o app de CI, o job e o README; a v2.7, o documento de negócio 1.4, o
CONTRIBUTING e o handoff. **CI verde com o `POST /tenants` pelo platform-admin via device flow**, e o critério do M0
fecha.

**D2, cerca de 4 tarefas, um PR a partir da `main` depois do merge da D1:** os requirements, a policy `TenantAdmin`
e o `AddAutorizacaoDaGateway` com os unitários e o teste da ordem dos handlers; a porta `IMemberQueries`, a
implementação e o teste no PostgreSQL; a rota, o `TenantDetailsView`, o `TenantDetailsResponse`, o OpenAPI, o teste de
subida da §13 e a suíte negativa de autorização; a coleção real estendida com o Outbox ligado, o passo do admin
convidado no app, no job e no README, e o fechamento dos itens "(D2, planejado)" da v2.7.

A revisão da spec escrita acrescentou à D1 os testes em `Production` com pedidos, a captura de log e trace, o
platform-admin por teste, o Problem Details de `401` e `403` e a rotação do refresh token. A contagem de cerca de 11
tarefas fica como estimativa a refazer no plano.

**A D2 não mexe no realm, no compose nem no bootstrap.** Tudo o que ela consome (`gateway-tenant`, o catálogo, o demo
com os quatro scopes) já entra na D1. Uma mudança de realm na D2 exigiria um segundo `down -v` e ensinaria o one-shot
a distinguir o realm da D1 do da D2.

**Ordem dentro da D1:** as tarefas de maior risco externo (bootstrap e app de CI) o mais cedo que a ordem permite,
logo depois do realm e do harness, para uma premissa falsa aparecer antes da troca da autenticação. A CI só roda no
PR, então a branch pode passar por um trecho em que o job `Compose` ficaria vermelho, desde que o HEAD do PR esteja
verde. **O `tenant_id` sem consumidor entre as fatias não abre brecha:** nenhuma rota confia no claim até a D2, e a
D-f chega junto com a rota que precisa dela.

**Documentos:** a v2.7 e o documento de negócio 1.4 entram com a D1. Eles descrevem o design inteiro, mas o que só a
D2 entrega (a rota, as policies de tenant, a pertença, a F-02 com `403`, as RN-028 e RN-029 e a matriz do
platform-admin) fica marcado **"(D2, planejado)"**, para a `main` nunca descrever como existente uma rota que ainda não
existe. A D2 **fecha** esses itens: tira as marcas, corrige na v2.7 (por errata no §0) e no documento de negócio o que
a execução desmentir, atualiza a linha de andamento da §16, o README e a matriz, e escreve o próprio handoff.

## 7. Onde esta fatia cai no roadmap

Fecha o critério do M0 "primeiro `curl` com token do Keycloak" e entrega o que a lista de pendências do M0 pedia do
realm: scopes, Audience Mapper, catálogo, remoção do `offline_access` e o platform-admin, agora sem senha gerada.
Continuam pendentes no M0 a auditoria, o armazenamento de eventos do realm e o RabbitMQ. Do M1, a D2 entrega a
leitura do próprio tenant.

**Tudo o que segue nesta seção é proposta (D-l):** não entra na v2.7, e cada item é decidido no brainstorming da
fatia que o implementar.

**Sequência proposta depois da D**, pelas dependências:

| # | Fatia | Marco | Entrega | Por que nesta posição |
|---|---|---|---|---|
| E | Auditoria e override | M0/M1 | Tabela de auditoria *append-only*; entrada no `POST /tenants`; `TenantReadAccess` em `GET /tenants/{tenantId}` (fecha a D-b); `GET /tenants` só `PlatformAdmin`, auditado; `409` na corrida de slug | A §18 exige auditoria em toda escrita, e o M0 a prometeu "desde já" |
| F | Retry manual e convite do admin inicial | M1 | Retry de `ProvisioningFailed`; operação de plataforma para trocar ou reenviar o convite do admin, marcando o `Member` antigo `Revoked`; admin órfão; tenants anteriores à v2.6 | Com a D, quem recebe um convite errado lê o tenant; com o M2, controla. Vem antes de `POST /members` |
| G | M3 · Data Plane, nível 1 | M3 | `Client.AspNetCore` com a §12 revista; `SampleResourceApi` com audiência própria | Valida o realm do lado consumidor antes de M2 e M6 construírem em cima |
| H | Detecção de aceite e ADR-010 | recorte do M4 | Advisory lock por job, inclusive no `OutboxWorker`; `Invited` → `Active` quando o convite é aceito | Pré-requisito da suspensão e da expiração |
| I | Reconciliação | M1 | Bidirecional, inclusive o admin órfão | Usa o lock de H e o código de F |
| J | Suspensão e reativação | M1 | Membros desabilitados e sessões revogadas | Depende de H |
| J2 | Downgrade de plano (N8) | M1 | `PATCH /tenants/{tenantId}`, com a regra das vagas ocupadas | Consta do M1 na v2.6 e não tinha fatia; mexe no mesmo estado do tenant que J |
| K, L | M2a e M2b | M2 | Membros (convite, leitura, reenvio, cancelamento); papéis e desativação; expiração | Dependem de F e de H |
| M | Step-up | M7' puxado | `StepUpRequirement` | Antes do encerramento e da exclusão LGPD |
| N | Encerramento e exclusão LGPD | M1 + M2 | `DELETE /tenants/{id}`, `DELETE .../members/{id}` | Dependem de J e de M |
| O | Rate limiting e README final | M7' | Por tenant; as duas demonstrações; Scalar com PKCE | Ponto de "apresentável" |
| P, Q, R, S | Broker; M5; M4; M6 | — | RabbitMQ antes do primeiro consumidor externo; permissões finas; federação; M2M | Depois do apresentável |

**Proposta: RabbitMQ fora da lista do M0.** Nenhum consumidor fora do processo existe antes do M5, e o transporte em
processo atende às fatias até lá. A fatia do broker entraria antes do M5, com a escolha da biblioteca. Até a decisão,
a v2.7 mantém o RabbitMQ entre as pendências do M0.

**Proposta: a suspensão cobre também o admin `Invited`.** A §9.7 desabilita no Keycloak só os membros `Active`, e o
admin inicial fica `Invited` até a detecção de aceite. A fatia D transforma esse admin num chamador real da API, e o
`MemberRequirement` aceita `Invited` (D-j). Uma suspensão entregue antes de H deixaria o admin do tenant inadimplente
logando e passando na `TenantAdmin`. Por isso H vem antes de J na sequência, e a regra proposta para a fatia da
suspensão é: ou a suspensão cobre `Invited` e `Active`, ou a detecção de aceite é pré-requisito dela. Proposta junto:
as policies de escrita do M2 conferem também `Tenant.Status == Active`; ler um tenant `Suspended` pela rota desta
fatia é aceitável e informativo.

**Proposta: revisão da §12 (Data Plane), para a fatia G.** O código de referência do Data Plane passaria a usar
metadados internos, `IssuerValidator` estrito, `NameClaimType = "sub"`, só RS256 e audiência por API num scope próprio
fora dos defaults, com o `azp` opcional (a audiência por API já recorta). É consequência da D-e, do DT2 e do DT11, mas
o Data Plane não é implementado nesta fatia.

**Proposta: bootstrap de produção do primeiro platform-admin.** Por runbook ou Terraform com trilha de auditoria, com
`CONFIGURE_TOTP` exigido; o one-shot é só do compose. Decide-se na fatia que levar o realm a produção.

## 8. Dívidas e questões abertas registradas

**Spike antes do plano** (container descartável, como na verificação). As três premissas que eram spike (o
`run --rm --no-deps` sem reenvio, o refresh depois de `stop`/`start` e o prazo do device code) foram verificadas ao
vivo na revisão e estão na §3.2 como fatos. Resta uma:
- **o marcador como atributo do realm** (DT8), premissa de segurança do one-shot: gravar
  `platformAdminInviteSentAt` com `kcadm update realms/identity-gateway -s attributes.platformAdminInviteSentAt=...`,
  lê-lo com `attributes(*)` e sem `--fields`, conferir que sobrevive a `stop`/`start` do Keycloak e que o PUT do `-s`
  não altera o `smtpServer`, o `bruteForceProtected`, o `revokeRefreshToken` nem os demais atributos do realm. Se
  falhar, a alternativa é um `PUT /admin/realms/{realm}` com a representação lida inteira, verificado do mesmo jeito.

**Achado ao vivo: links de ações antigos trocam a senha de uma conta ativa** (§3.2). Cada link emitido é uma
credencial de troca de senha até expirar, mesmo depois de o convite ser aceito por outro link. Vale para o one-shot
(no máximo um link automático, de 4 h, mais os reenvios manuais) e **para o convite da fatia C** (link de 7 dias, e
reenvios por retry do Outbox). O D12 da C impede o envio depois do aceite, não um link anterior. Depois do aceite:
- **revogam de fato:** remover o usuário (o token referencia o id, e o uso dá usuário não encontrado; no compose,
  `down -v`), ou **desabilitar ou remover** a chave HMAC antiga do realm, que derruba também todos os refresh tokens.
  Só rebaixá-la a passiva não basta: chave passiva continua verificando;
- **bloqueiam só enquanto durarem, e são reversíveis:** desabilitar o usuário (reabilitado, todo link antigo volta a
  funcionar) e trocar o e-mail (a checagem compara com o e-mail atual; voltar ao e-mail antigo ressuscita os links).

Vai para a §19, para o README e para o comando de reenvio. **O link de 7 dias do convite do admin do tenant é risco
aceito nesta fatia, com dono (D-o):** a fatia F, que troca e reenvia o convite, trata a revogação (remover o usuário
substituído, e não só marcar o `Member` `Revoked`) e avalia encurtar o prazo. O `MemberRequirement` já corta o acesso
de um `Member` `Revoked` à **API**, mas não a conta no Keycloak. Nada muda no código na D.

**Limites que a v2.7 registra:**
- **D-b:** platform-admin com `403` na rota até a fatia E.
- **O Data Plane continua exposto ao `tenant_id` por grupo** (ADR-011). A regra "nenhum grupo" é conferida no JSON;
  um grupo criado em runtime, inclusive por quem tem a chave da Gateway, não é visto. Quem decide entre uma
  reconciliação que detecte grupos com `tenant_id` e um mapper que não recue é a fatia do Data Plane.
- **A pertença não contém quem tem a chave da Gateway** (ADR-011): com o `manage-users`, ele troca a senha ou o
  e-mail de um `Member` real e passa na `TenantAdmin` com a conta dele. A pertença protege contra o recuo do mapper
  para o atributo de grupo e contra a forja por grupo sem tomar conta alheia, que é silenciosa (a troca de senha é
  ruidosa: o dono perde o acesso). A trilha do ataque só existe com os eventos de administração ligados, sem
  representação.
- **`Invited` passa no `MemberRequirement`** por causa do ADR-007, e sai da lista quando o aceite for detectado.
- **E-mail digitado errado agravado:** o destinatário passa a ler o tenant pela API.
- **Consentimento forçado no device flow; phishing de device code:** o client público é o vetor clássico, por isso
  fica só no ambiente local e só na lista de Development.
- **`CreateDefaultClientScopes` não é documentado:** reverificar a cada troca de tag do Keycloak.
- **O `ValidIssuer` não restringe:** entra na tabela de diagnóstico da §12.1.
- **Rotação de chave:** cada réplica recusa o primeiro pedido com o `kid` novo. Runbook: publicar a chave nova como
  passiva, esperar as réplicas recarregarem (o `AutomaticRefreshInterval`, 12 h por padrão, ou reinício), ativar, e
  manter a antiga habilitada por pelo menos a vida do token mais o `ClockSkew`.
- **Keycloak fora com metadados frios = `401`**, que aponta o sintoma para o token e não para a dependência, contra o
  argumento da §9.6; `503` fica registrado como alternativa. O `/health/ready` cobre o Keycloak inteiro fora do ar,
  mas não um `jwks_uri` inalcançável.
- **A `api` depende de um one-shot que depende do Keycloak saudável:** `docker compose up` com o Keycloak parado não
  sobe a API. A demonstração nº 1 para o Keycloak **depois** do `up`, e não quebra.
- **O one-shot faz login no master a cada subida, antes de ler o marcador:** trocar a senha ou apagar o admin do
  master do compose quebra toda subida seguinte, e a `api` não sobe, mesmo com o convite concluído há semanas. O
  one-shot sai com mensagem própria, e o README avisa: não trocar nem apagar esse admin; se mudou, `down -v`.
- **`PLATFORM_ADMIN_EMAIL` fica fixado no primeiro import** (`IGNORE_EXISTING`). A recuperação do platform-admin sem
  "esqueci a senha" só existe pelo console do master. O bootstrap de produção é proposta (§7).
- **O marcador vive no realm:** se o usuário for recriado à mão num realm que já tem o marcador, só o reenvio manual
  envia. O mesmo vale para um envio que falhou: o marcador já estava gravado, a subida seguinte sai `0` sem e-mail, e o
  convite só sai pelo reenvio.
- **A lista de `azp` é estática:** vale para os clients interativos que chamam a Gateway. Clients M2M de tenant nunca
  entram nela e não recebem `gateway-api`. Um client de tenant chamando a Gateway, no M6, exige decisão nova
  (consulta ao banco de "client pertence ao tenant", nunca prefixo de nome). Fora de Development, a lista fica vazia
  até existir um client administrativo.
- **Volume antigo pela quarta vez**; `down -v` obrigatório.
- **Mailpit sem autenticação em `127.0.0.1:8025`:** o link do platform-admin toma a conta enquanto não expira (4 h).

**Dívidas herdadas que seguem:** e-mail duplicado entre envio e commit; admin órfão; tenants anteriores à v2.6; a
mesma pessoa em dois tenants; retenção real do e-mail apagado; notification-hub; vínculo federado no M4; corrida
residual do `POST` de papel; slug perdedor com `500`; a troca de e-mail sem verificação, não verificada na 26.7.4.

**Não entra, e segue pendente:** o armazenamento de eventos do realm, porque a seção do realm aprovada não o lista. O
custo é outro `down -v` quando entrar, e a §16 da v2.7 o mantém entre as pendências do M0. Os eventos de login
guardariam o username, que é o e-mail, e a retenção precisa ser decidida junto. A rotação do refresh token saiu desta
lista: entra nesta fatia (D-n).

**Decisões do controlador e do autor.** O controlador fechou os detalhes que nem o rascunho nem a conversa fecharam,
seguindo a recomendação do relatório correspondente. Depois da revisão da spec escrita, o autor decidiu A1 a A4 (D-l a
D-o) e o controlador ajustou os demais. Estado de cada item:
- a `AccessTokenValidationOptions` em `Infrastructure/Configuration`, seção `Keycloak:Auth`; só o tipo é neutro, e a
  recusa do demo é regra do adaptador;
- a lista de `azp` no `appsettings.Development.json`, e vazia fora de Development como fail-closed com aviso (e não
  recusa na subida), agora com teste em `Production` que manda um token e leva `401`;
- o requirement de papel como handler próprio, `InvokeHandlersAfterFailure = false` (global, e por isso a auditoria de
  negação da fatia E nasce fora dos handlers) e o `MemberRequirement` sem consulta quando o contexto já falhou, com a
  ordem de registro explícita no `AddAutorizacaoDaGateway` e um teste funcional que a prova;
- **mudou:** a porta é `IMemberQueries.GetStatusAsync(TenantId, ExternalUserId, ct)`, no padrão `I*Queries`, com o
  `RequestAborted` como `CancellationToken`; o `IMemberRepository` não muda;
- todo `403` da rota com o mesmo Problem Details, agora com mecanismo (`AddProblemDetails` e um
  `IAuthorizationMiddlewareResultHandler`), também para o `401`; o tenant não achado no handler vira o mesmo `403` pelo
  `Match` do módulo, sem `ParaOk`; o read model é o `TenantDetailsView`, e o DTO, o `TenantDetailsResponse`;
- o client de device flow do fixture, só em runtime, para o teste da Account API e, agora, para o `401` por audiência
  com Keycloak real;
- **mudou (A2, D-m):** o link do one-shot vale 4 h; o nome `platformAdminInviteSentAt` e a busca por `exact=true` com
  exatamente um resultado ficam;
- o `oauth2.device.code.lifespan` de 300 s no demo, agora verificado ao vivo;
- `profile` e `email` fora só dos clients que chamam a Gateway; os clients de tenant do M6 decidem no M6, com os dois
  como opcionais;
- **mudou:** `IncludeErrorDetails = false` em todo ambiente; o teste do `admin-cli` por ROPC e o teste que enumera os
  endpoints ficam, e a `FallbackPolicy` passa a ser provada pelo caminho não mapeado;
- sem aquecimento dos metadados na subida: a demonstração faz um `GET` autenticado antes do `stop`. A CI trava o cache
  com o Keycloak parado, não a necessidade do aquecimento (a `jornada` já aqueceu o cache);
- **mudou:** `stop`/`start` também na CI, a mesma sequência do README, porque o refresh depois do reinício virou fato;
- **mudou (A3, D-n):** a rotação do refresh token entra nesta fatia; o armazenamento de eventos continua fora;
- **mudou (A1, D-l):** a v2.7 registra só o que a D implementa e as erratas; a sequência de fatias (agora com o
  downgrade de plano, N8), a regra da suspensão sobre membros `Invited`, a revisão da §12 do Data Plane, o bootstrap
  de produção com TOTP e o RabbitMQ fora do M0 viram propostas (§7);
- **mudou (A4, D-o):** o link de 7 dias do convite da fatia C é risco aceito, com a fatia F como dona;
- **mudou:** a v2.7 e o documento de negócio 1.4 entram com a D1, com os itens da D2 marcados "(D2, planejado)", e a
  D2 os fecha, inclusive corrigindo a v2.7 (§6).

## 9. Mudanças na especificação (v2.7)

Arquivo novo `docs/especificacao-arquitetural-v2.7.md`, com a seção "O que mudou da v2.6 para a v2.7". **Nenhum ADR
revogado**: o ADR-003 ganha um complemento e entra o **ADR-011**. A v2.7 registra **só o que a fatia D implementa,
mais as erratas** (D-l): as propostas da §7 não entram. O que só a D2 entrega fica marcado **"(D2, planejado)"**, e a
D2 fecha esses itens (§6). A coluna "v2.6" dá as linhas da v2.6 que cada mudança torna falsas ou incompletas.

**Erratas:**
- **E1** (§15, v2.6:2136): "o JSON escapa o `&` como `&`" → "o JSON escapa o `&` como `\u0026`", como no
  comentário da CI (`.github/workflows/ci.yml`). Dívida da fatia C. A sequência se perdeu porque a ferramenta de
  escrita dos agentes decodifica o escape ao gravar: **a tarefa da v2.7 grava este trecho por shell** e confere com
  `grep -c 'u0026'` na v2.7 e nesta spec (pelo menos uma ocorrência em cada).
- **E2** (§12.1, v2.6:1917-1918): "o ROPC continua desabilitado também no realm de teste" era falso: o fixture fazia
  ROPC num client criado em runtime, e o `admin-cli` embutido do realm tem direct grant.
- **E3** (§10.1, §11.7, v2.6:714 e :1499-1501): o `PlatformAdminOverrideHandler` "satisfaz o `SameTenantRequirement`"
  nunca funcionou com o `Fail()` do claim ausente; o comentário do "segundo handler" como motivo do `Fail()` sai
  (DT13).
- **E4** (§11.7, v2.6:1474): o `RegisterTenantCommand` do código de referência sem o e-mail, velho desde a v2.6.
- **E5** (§12.1, v2.6:1811): "a lista substitui o conjunto padrão"; na verdade, declarar `clientScopes` desliga a
  criação dos embutidos, e a correção é `CreateDefaultClientScopes`.
- **E6** (§12.1, v2.6:1824): os papéis padrão "não interferem"; eles entravam no claim, e agora saem.
- **E7** (§9.5 e §15, v2.6:630, :2117 e :2138): o bootstrap descrevia como presentes os scopes, o Audience Mapper, o
  armazenamento de eventos e a remoção do `offline_access`, que o JSON não tinha.
- **E8** (§12.1, §12.2, v2.6:1919-1933 e :2024): o teste de referência usa métodos que não existem
  (`GetClientCredentialsTokenAsync`, `GetTokenForUserAsync`) e a rota `/tenants/tenant-a/members`, com slug no lugar
  do GUID.

| Onde | v2.6 | Mudança |
|---|---|---|
| Cabeçalho e §0 | :3-4, :8 | Versão 2.7; as decisões desta spec renumeradas, com um prefixo que não colida com os nomes dos PRs D1 e D2; as erratas E1…E8 |
| §2.1 | :240, :256 | A seta (2) ganha o device flow, só no client de demonstração local |
| §3 (princípio 5), §13, §16 (M2), §18 | :274, :2048, :2060, :2154, :2210 | "As regras são três" continua: a pertença no banco reforça, na Gateway, a regra (a), e não é uma quarta regra (D2, planejado) |
| §4, ADR-003 | :295-298 | Complemento "fluxos permitidos": PKCE para aplicações, Client Credentials para M2M, device flow no realm da aplicação só no `identity-gateway-demo` (local, fora do Terraform); nos testes, um client de device flow criado em runtime pelo fixture, sem `gateway-api`, só para a Account API. ROPC: nenhum client declarado no JSON tem direct grant; o `admin-cli` embutido mantém o direct grant, com token leve e sem audiência, e a Gateway recusa o token dele (teste com Keycloak real). Alcance: nenhum client do realm emite, por senha, token aceito pela Gateway; o `kcadm` do one-shot e o Testcontainers usam a senha do master, fora do ADR-003, declarados. O harness submete o formulário do Keycloak com uma senha que ele mesmo definiu, e a credencial nunca passa pela Gateway: não é ROPC |
| §4, ADR-005 | :309-310 | 5 min configurados no realm (`300`); só o catálogo no claim |
| §4, ADR-011 (novo) | — | A autorização da Gateway nas rotas de governança é token mais pertença no banco (D2, planejado). Motivo: o mapper de atributo recua para o atributo de grupo, e o `manage-users` fabrica esse grupo. **Limites:** o Data Plane não tem a pertença, e confia no claim e na regra "nenhum grupo"; e a pertença **não contém quem tem a chave da Gateway**, que com o `manage-users` troca a senha ou o e-mail de um `Member` real e passa com a conta dele. Ela protege contra o recuo do mapper e a forja por grupo sem tomar conta alheia. Essa trilha só existe com os eventos de administração ligados, sem representação. A pertença é consultada só nas rotas de governança, não por requisição de negócio (o ADR-002 continua valendo) |
| §5 | :355 | A Gateway aceita só o algoritmo do realm, RS256 |
| §6.3 | :437-438 | A hierarquia `platform-admin` > `tenant-admin` é o **teto** da `RoleAssignmentPolicy`, não herança de acesso (D-g; D2, planejado) |
| §6.4, §11.9 | :441-449, :1546-1567 | A leitura da pertença pela porta `IMemberQueries.GetStatusAsync(TenantId, ExternalUserId, ct)`, com o tenant na assinatura; o `IMemberRepository` continua só com `Add` (:1554) (D2, planejado) |
| §7 | :479-495 | `Api/Authorization` (policies, requirements e o result handler do Problem Details); `Infrastructure/Configuration` com a `AccessTokenValidationOptions`; `tests/IdentityGateway.Testing.Keycloak`; o app de CI em `tools/`. A árvore nunca listou `Api/Security`, e não há o que tirar dela |
| §8 | :528, :548, :550 | Duas linhas no lugar de uma: `GET /tenants`, só `PlatformAdmin`, auditado, na fatia E; `GET /tenants/{tenantId}`, tenant-admin do próprio tenant e `Member`, platform-admin `403` até o `TenantReadAccess` (desvio D-b, com a fatia que o fecha). "Sem rota nova" vira a rota nova: campos exatos, `401` e `403` (inclusive tenant inexistente), sem e-mail, `TenantStatus` como contrato (D2, planejado). Rotas anônimas com `AllowAnonymous` explícito sob a `FallbackPolicy` autenticada |
| §9.2 | :603 | Nota: a demonstração usa o device flow, e as aplicações usam PKCE |
| §9.5 | :630 | Entregue: `offline_access` e `uma_authorization` fora do papel padrão (E7) |
| §10.1 | :709-735 | O `aud` vem do scope `gateway-api`, fora dos defaults (:709). Validação: lista de `azp`, `iss` estrito, `typ`, `sub` GUID, RS256, `ClockSkew` 30 s, `IncludeErrorDetails` falso, Problem Details em `401` e `403`. `TenantAdmin` = `tenant-admin` ∧ ¬`platform-admin` ∧ mesmo tenant (GUID, valor único) ∧ `Member` em `{Invited, Active}`, com `Fail()` em todo caminho (:710-711; D2, planejado). E3: o override como policy própria `TenantReadAccess`, entregue com a auditoria (:714-716). A lista estática de `azp` não cobre clients de tenant do M6 (:726-735). O client de tenant recebe o `tenant_id` só se o adaptador do M6 anexar o `gateway-tenant` (:732) |
| §10.2 | :795 | Nota: o one-shot usa o admin do master; a Gateway continua nunca usando. O raio de dano do `manage-users` inclui fabricar um `tenant_id` por grupo e tomar a conta de um `Member` real |
| §10.3 | :802-806 | Access token de 300 s; **rotação do refresh token entregue** (`revokeRefreshToken`, `refreshTokenMaxReuse: 0`; :805); o token não carrega e-mail nem nome; `NameClaimType = "sub"`; `registrationAllowed` falso e `bruteForceProtected` |
| §11.7 | :1469-1544 | E3, E4. Código de referência novo: os quatro requirements, GUID de valor único, sem "segundo handler"; o teste de subida que o comentário de :1520-1521 promete entra com a rota (D2, planejado) |
| §11.8 | :1601-1654 | A validação pela `AccessTokenValidationOptions`, preenchida pelo adaptador, com código de referência: metadados internos, `IssuerValidator` estrito, `BackchannelTimeout`, `RefreshInterval`, `OnTokenValidated`, `IncludeErrorDetails` falso; o `AddAutorizacaoDaGateway` com a ordem de registro dos handlers (D2, planejado) |
| §12.1 | :1715-1953 | Exemplo de token sem `preferred_username` (:1715-1731). O JSON com `CreateDefaultClientScopes`, scopes fora dos defaults e `defaultClientScopes` explícitos por client (E5, :1764-1811). O token só com o catálogo (E6, :1813-1824). O scope `roles` fora dos clients de usuário: o `aud` explícito dispensa o *audience resolve*, e o `realm_access` só duplicaria o `roles`; a Solução B fica inerte nesses clients (:1826-1886). Testes reais pelo harness (E2, E8, :1910-1939). Diagnóstico (:1941-1953): sai o "conferir `defaultDefaultClientScopes`" (:1945); entram `ValidIssuer` que não restringe, `aud` ausente, `default-roles` no claim, embutidos sumidos, `sub` ausente sem `basic` |
| §12.2 | :1996-2033 | `gateway-tenant` fora dos defaults; o adaptador do M6 anexa `gateway-tenant` (e `gateway-api`, se o client chamar a Gateway) explicitamente; o recuo do mapper para o atributo de grupo; nenhum grupo no realm; teste real pelo harness (E8) |
| §13 | :2041-2062 | Linhas novas: OIDC falso com emissor divergente; suíte negativa em tabela; coleção com Keycloak real atravessando a API e a ponte de contrato; testes em `Production` com pedidos; opções do JwtBearer conferidas em execução; captura de log e trace no teste de vazamento; sem `SymmetricSecurityKey`; regras novas do realm; ROPC do `admin-cli` → `401`; Keycloak parado na CI com `stop`/`start`. Da D2 (planejado): handler que aprova tudo, `[Theory]` sobre o `MemberStatus`, nomes dos status travados e o teste de subida de :2049, que ganha objeto |
| §14 | :2071 | Keycloak fora = `401` com log; `503` registrado como alternativa |
| §15 | :2100-2142 | One-shot `platform-admin-invite`; a `api` depende dele; `PLATFORM_ADMIN_EMAIL` com `test -n` e minúsculo; sai `Jwt__SigningKey`; `api` e Jaeger em `127.0.0.1`; o que a fatia D acrescentou ao bootstrap (E7, :2117-2119, :2138); a senha do master ganha um segundo consumidor, o one-shot, que monta `/keys/admin-password` (:2130); volume antigo: o one-shot sai com `1` (:2132); job `Compose` com o app C#, device flow, `pipefail`, "um e-mail só", o convite do admin do tenant e o Keycloak parado com `stop`/`start` (E1, :2136). A frase "exigindo ao menos uma mensagem, nunca exatamente uma" de :2136 passa a valer só para o convite do admin do tenant; o do platform-admin é contado exato. Bootstrap do platform-admin pela D-h (:2140); Scalar com OAuth2 pendente, com destino no M7' (:2142) |
| §16 | :2152, :2161-2179 | M0 sem "senha gerada" e com **três** client scopes, não dois (:2152); a linha da fatia C ganha "(PR #5)" (:2168); linha da fatia D (D1 e D2); pendências do M0 revistas: saem as que a D entrega (scopes, Audience Mapper, catálogo, `offline_access`, platform-admin, rotação do refresh token), e continuam a auditoria, o armazenamento de eventos do realm e o RabbitMQ; demonstração nº 1 com o `GET` autenticado antes do `stop` e dentro dos 5 min do token (:2176-2179); demonstração nº 2 com o `403` já possível (D2, planejado); passos no navegador, consequência do próprio ADR-003 |
| §17 | :2185, :2197 | "Sempre PKCE" ganha a exceção nomeada; anti-pattern 7: na Gateway, também a pertença no banco (D2, planejado) |
| §19 | :2218-2260 | Os limites da §8 desta spec. Os itens que já existem são **reescritos, sem duplicar**: o `depends_on` do Keycloak (:2235); o link duplicado (:2248), agora com a confirmação ao vivo, a extensão ao one-shot e a lista certa do que revoga de fato e do que é reversível; o e-mail digitado errado (:2252). Entram como não verificados o cache de metadados além de ~9 min e a validação do `Host` pelo mailpit contra *DNS rebinding* |

## 10. Fora do escopo

O override auditado e a tabela de auditoria (fatia E); `GET /tenants` (listagem); o step-up; o armazenamento de
eventos do realm; o RabbitMQ; o Scalar com OAuth2/PKCE; Authorization Code + PKCE em qualquer client do realm; os
clients de tenant do M6; a detecção de aceite do convite; a revogação de links de ações anteriores (risco aceito, com a
fatia F como dona, D-o); `TenantId` no `ICurrentUser`; health check dedicado ao JWKS; o M2; as propostas da §7.

## 11. Entregáveis

- O código e os testes da §4 e da §5, em dois PRs (§6), com a tabela de mutações preenchida no handoff de cada um.
- `docs/especificacao-arquitetural-v2.7.md` (§9), com a D1, com os itens da D2 marcados "(D2, planejado)". A tarefa
  da v2.7 grava a errata E1 por shell e confere `grep -c 'u0026'` na v2.7 e nesta spec, porque a ferramenta de
  escrita dos agentes decodifica a sequência; e carrega a coluna de linhas da v2.6 da §9.
- `docs/documentacao-negocio.md` **1.4**, alinhado à v2.7, com a D1 e com as mesmas marcas "(D2, planejado)" nos
  itens da D2, que a D2 fecha:
  - cabeçalho com a versão, a fonte e a nota da versão; "27 regras transversais (RN-001..RN-027)" passa a 29;
  - persona do platform-admin: "consulta qualquer tenant" temporariamente falso (D-b); nasce por convite por e-mail,
    sem senha gerada; não age como tenant-admin (D-g). Persona do tenant-admin: a primeira rota e o que a protege;
  - F-02: sai o texto do "segundo handler"; outro tenant **e tenant inexistente** passam a `403` (a rota não usa
    `404`, D-k); o platform-admin fica com `403` temporário;
  - RN-001 sem a justificativa do "segundo handler"; **RN-028**, pertença do ator ao tenant no banco da Gateway, com
    a lista de status; **RN-029**, separação de funções;
  - RN-010 (a hierarquia é teto), RN-011 (o bootstrap por e-mail), RN-019 (o token não carrega e-mail nem nome),
    RN-025 (a pertença só nas rotas de governança); "três regras" de isolamento mantidas, com a pertença como reforço
    da regra (a);
  - matriz: F-02 do platform-admin ⚠️ temporário, e a nota 1 com a pertença;
  - roadmap e demonstrações: passos no navegador, a nº 1 com o `GET` antes do `stop`, a nº 2 com o `403` já possível
    e o resto no M2; a **decisão 7.7** ("README com `curl` que qualquer avaliador roda sozinho") reescrita, porque os
    passos no navegador a mudam; a seta (2) dos diagramas ganha o device flow de demonstração;
  - limites (7.3) e o resumo "Limites conhecidos" do apêndice: os da §8 desta spec e os da v2.6 que faltam;
  - ADR-003: o custo aceito reescrito, o device flow como exceção de demonstração, o harness que não é ROPC; "Por que
    a Gateway nunca vê uma senha" e a checagem adversarial com a frase sobre o realm de testes precisa;
  - modelo de segurança: o override como policy própria, adiado, com o `Fail()` intacto;
  - "Ambiente como código": sem RabbitMQ nem API de exemplo no compose, e o bootstrap por e-mail; o fluxo 9.1 sem
    RabbitMQ e MassTransit (dívida da fatia C).
- README: andamento e links para a v2.7; a demonstração por device flow (§4.7) sem a receita HS256, e o parágrafo de
  abertura dela ("o mesmo formato que a API valida hoje"); as notas fixas (link de 4 h, admin do master, falha no
  envio); o aviso do `down -v`; a contagem de testes; "Dez ADRs" passa a onze, com o ADR-011 na tabela; a linha
  evolutiva e o item da v2.7; "Entra no M0: RabbitMQ" fica como pendência, com a proposta da §7 citada.
- `CONTRIBUTING.md`: o link da spec (hoje na v2.6) e a linha do projeto de suporte
  `tests/IdentityGateway.Testing.Keycloak` na tabela de onde vai cada teste.
- Comentários que ficam falsos: `DependencyInjection` da Api, `HttpCurrentUser`, `IdentityGatewayApiFactory`,
  `KeycloakAdminOptions` (`PublicBaseUrl`), `TenantsModule`, `SegurancaTests` (o `GET` anônimo), o `UserSecretsId` da
  Api, o do `Directory.Packages.props` sobre o pacote JWT e o cabeçalho do `docker-compose.yml`.
- **O que a fatia D absorve das dívidas da C:** a mutação da guarda https tentada (teste em `Production`, §5.3; se o
  ambiente recusar, o roteiro manual vai no handoff); a frase do `&` corrigida na v2.7 (E1); `pipefail` no job; `api`
  e Jaeger em `127.0.0.1`; os nomes de `TenantStatus` e `MemberStatus` travados; o `NenhumaChaveDeCredencial` dentro
  do User Profile; o ROPC do fixture removido.
- Os handoffs da D1 e da D2, com o resultado da verificação ao vivo e do spike do marcador.

## 12. Revisão por especialistas (2026-09-30)

Duas rodadas de revisão só de leitura antes da escrita (segurança, ASP.NET Core, realm, testes e roadmap; depois
segurança, escopo, CI e coerência documental), uma verificação ao vivo no Keycloak 26.7.4 e, sobre a spec escrita,
uma terceira rodada (segurança, .NET, Keycloak, com réplica ao vivo do compose, testes e coerência). Achados que
mudaram decisões:

| Achado | Quem | Onde entrou |
|---|---|---|
| O `aud` num scope default vale para qualquer client, inclusive por ROPC | Segurança, realm e roadmap; reproduzido ao vivo | D-e, §4.4 |
| O `tenant_id` recua para o atributo de grupo, e o service account o forja | Segurança e realm; ao vivo | D-f, DT14, §3.1 |
| Platform-admin com `tenant-admin` passaria na `TenantAdmin` | Segurança | D-g |
| O one-shot reenviando vira reset periódico da senha; "garantir o papel" promoveu um tenant-admin | Segurança e realm; ao vivo | D-h, §4.5 |
| O marcador em volume falhou por permissão e reenviou a cada subida | Segurança (2ª) e CI; ao vivo | DT8 |
| O motivo para não dividir não se sustentava | Escopo | D-i, §6 |
| `Member` em qualquer status abre por padrão | Segurança (2ª) e coerência | D-j |
| Responder `404` ao próprio tenant inexistente era inalcançável, e a rota não o usa | Coerência | D-k |
| A Api não pode ler o `AssertionAudience` | ASP.NET e testes | DT1 |
| O `ValidIssuer` não restringe, e o teste só pega a mutação com emissor divergente | ASP.NET e testes; ao vivo | DT2, §5.2 |
| A lista de `azp` abriria produção pela mescla de arrays; os ramos de produção nunca eram testados | Segurança (2ª) | DT4, §5.1 |
| `fullScopeAllowed=false` no service account derruba a Admin API | Realm e segurança; ao vivo | DT10 |
| `BackchannelTimeout` e `RequireHttpsMetadata`: fila de 60 s e `500` em todo pedido | ASP.NET | DT5 |
| O `ClockSkew` e o `acr` tinham sumido do design revisado | Coerência e segurança (2ª) | DT5, DT9 |
| O override descrito na v2.6 nunca funcionou com o `Fail()` | Segurança, escopo e coerência | DT13, E3 |
| Dois grants no harness e nenhum client para o PKCE | Escopo e coerência | DT7 |
| O app de arquivo único quebra com `CS7022` e `IL2026`/`IL3050` | CI; réplica ao vivo | DT7, §4.6 |
| O "um e-mail só" depois de `down`/`up` era vacuoso | CI | DT12, §4.7 |
| Pausar o Keycloak do fixture derrubaria as outras classes | CI | DT12 |
| O cache de metadados sobrevive ao Keycloak parado; chaves passivas no JWKS; `typ` no claim, não no cabeçalho; o link resolve o `VERIFY_EMAIL`; o `sub` vem do `basic` | Verificação ao vivo | §3, §4.2, §4.4 |
| Links antigos trocam a senha de uma conta ativa | Verificação ao vivo | §3.2, §8 |
| A suspensão não alcança o admin `Invited` | Segurança (2ª) e roadmap | §7 |
| Placeholders de i18n reprovam a regra do realm | Realm e testes | §4.4 |
| A v2.6, o documento de negócio e o README com dezenas de trechos desatualizados, e oito erratas | Roadmap e coerência | §9, §11 |
| **3ª rodada:** a errata E1 tinha perdido a sequência de escape que corrige | Coerência | §9, §11 |
| A v2.7 normatizava decisões que o autor não tomou (sequência, suspensão, §12, bootstrap de produção) | Coerência | D-l, §7 |
| O caso "tenant `Pending` → `200` sem e-mail" é impossível, e o "sem e-mail" num tenant `Active` é vacuoso | Testes | §4.3, §5 |
| As fases do Keycloak parado não passavam estado entre processos | Testes | §4.6 |
| Nada produzia Problem Details para `401`/`403`, e o `ParaOk` traduziria o tenant não achado contra a D-k | .NET | §4.2, §4.3 |
| A garantia "banco só depois das três camadas" depende da ordem de registro dos handlers | .NET e segurança | §4.3, §5.1 |
| O NetArchTest não vê propriedades nem constantes | .NET e testes | §3.3, §5.1 |
| `--fields` esvazia `attributes` também no JSON; a imagem não tem `jq`; o login no master precede tudo | Keycloak; ao vivo | §3.2, §4.5, §8 |
| As três premissas de spike passaram ao vivo; resta o marcador | Keycloak; ao vivo | §3.2, §8, DT12 |
| Testemunhas erradas de mutação (`FallbackPolicy`, `ValidateAudience`, `SymmetricSecurityKey`, `RequireHttpsMetadata`) e ramos de `Production` testados só na subida | Testes | §5 |
| A revogação de links listava alavancas reversíveis; a pertença não contém quem tem a chave da Gateway | Segurança | §8, §9 (ADR-011) |
| A D1 tirava da CI a prova do convite do admin do tenant; o log da CI levava a senha do master | Testes, coerência e segurança | §4.6, §4.7 |
| Link de 4 h, rotação do refresh token e o risco aceito do link de 7 dias | Segurança e coerência; decisões do autor | D-m, D-n, D-o |
