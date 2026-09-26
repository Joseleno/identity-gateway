# IdentityGateway

> API de governança de identidade multi-tenant em .NET 10, sobre o Keycloak.

O Keycloak sabe autenticar pessoas. Ele não sabe que um tenant tem um plano contratado com
limite de usuários, que um cliente inadimplente precisa ser suspenso com efeito imediato, ou
que encerrar um contrato exige um estado terminal auditável. Essas são regras de negócio — e o
IdentityGateway é a camada que as torna explícitas, versionadas e testáveis.

A frase que resume o desenho inteiro: **a Gateway decide quem pode o quê, mas não participa de
nenhum login e de nenhuma requisição de negócio.**

---

## Estado do projeto

**M0/M1 em andamento — vertical de registro, fundação Keycloak e consumidor do provisionamento entregues
nesta branch.**

O repositório parte do template [CleanStart](https://github.com/Joseleno/CleanStart) e já traz a fundação
funcionando — Clean Architecture em quatro camadas, Outbox transacional, cache de dois níveis, middlewares
de correlação e segurança, testes de arquitetura e CI. **O domínio do IdentityGateway já existe:** o
agregado `Tenant` e `POST /api/v1/tenants` (PR #1) gravam o tenant e publicam `TenantRegistered` no
Outbox; a fundação Keycloak (PR #2) acrescentou o realm `identity-gateway` com Organizations, autenticação
`private_key_jwt` do service account e a porta `IIdentityProvider.EnsureOrganizationAsync` provada ponta a
ponta contra um Keycloak real; esta branch fecha o ciclo — o `ProvisionTenantHandler` consome o evento pelo
próprio Outbox (transporte em processo até a fatia do broker), decide entre repetir e desistir pela janela
de provisionamento, e `GET /api/v1/tenants/{tenantId}/provisioning` deixa o estado consultável. **Próximo
passo: a fatia C**, o convite do admin inicial. O roadmap está em
[`docs/especificacao-arquitetural-v2.5.md`](docs/especificacao-arquitetural-v2.5.md) §16 (referência
normativa atual — as anteriores ficam como registro histórico), e o estado detalhado no
[handoff do consumidor do provisionamento](docs/superpowers/specs/2026-09-26-consumidor-provisionamento-handoff.md).

| Marco | Entrega | Estado |
|---|---|---|
| **M0** · Fundação | Compose, bootstrap do realm, health checks, CI | 🔨 em andamento |
| **M1** · Tenants | Registro, provisionamento via Outbox, suspensão, encerramento | 🔨 em andamento |
| **M2** · Membros e papéis | Convite, desativação, exclusão LGPD, `RoleAssignmentPolicy` | ⬜ |
| **M3** · Data Plane | `Client.AspNetCore` e o conteúdo do `SampleResourceApi`, que hoje é só esqueleto | ⬜ |
| **M4** · Federação | Domínios, IdP por tenant, discovery | ⬜ |
| **M5** · Permissões finas | Permission sets, cache com invalidação por evento | ⬜ |
| **M6** · M2M | Clients com `private_key_jwt`, rotação | ⬜ |
| **M7** · Hardening | Step-up, rate limiting, README com `curl` reproduzível | ⬜ |

---

## Rodando local

Precisa de .NET 10 e Docker. O Docker não é opcional: os testes de integração sobem Postgres e um Keycloak
26.7.4 (um contêiner por assembly) por Testcontainers.

```bash
# Toda a suíte — 331 testes, 0 skips (113 domínio, 49 application, 29 arquitetura, 114 integração, 26 funcional)
dotnet test

# As dependências, e a API junto
docker compose up -d

# Só na primeira subida (volume novo): a API migra sob pedido, nunca sozinha — StartupTasks só aplica
# migrations com --migrate, porque migrar automaticamente é perigoso com várias réplicas no ar ao mesmo tempo.
docker compose run --rm api --migrate
```

Com o compose de pé: a API responde em `http://localhost:8080`, `/health/live` e `/health/ready`
respondem `200`, os logs estruturados vão para o Seq em `http://localhost:5341` e os traces para o Jaeger
em `http://localhost:16686`.

### Keycloak

O compose sobe o Keycloak 26.7.4 com o realm `identity-gateway` importado de `keycloak/bootstrap/`.

| O quê | Onde |
|---|---|
| Console | http://localhost:8081 (só no localhost) |
| Usuário | `admin` |
| Senha | gerada na primeira subida: `docker compose logs gateway-keys` |

Nenhuma credencial fica no repositório: a chave da Gateway e a senha do admin são geradas pelo serviço
`gateway-keys` num volume, na primeira subida. O log só mostra a senha nessa primeira subida; depois,
recupere do volume — o nome leva o prefixo do projeto do compose (o nome da pasta), como no `private.pem`
abaixo:

```bash
docker run --rm -v identitygateway_gateway-keys:/k alpine cat /k/keycloak/admin-password
```

**Chave e realm andam juntos.** O realm é importado só na primeira subida. Se só o volume `gateway-keys` for apagado,
a chave nova não bate com o certificado registrado, e o `/health/ready` da API responde 503 com `invalid_client` no
log. Para recomeçar do zero: `docker compose down -v`.

### Rodar a API pela IDE

A API exige a configuração do Keycloak para subir. Com o compose rodando só as dependências:

```powershell
docker compose up -d postgres redis keycloak
$pem = docker run --rm -v identitygateway_gateway-keys:/k alpine cat /k/api/private.pem | Out-String
dotnet user-secrets set "Keycloak:Admin:PrivateKeyPem" $pem --project src/IdentityGateway.Api
```

O `appsettings.Development.json` já aponta `Keycloak:Admin:BaseUrl` para `http://localhost:8081`. O nome do volume
leva o prefixo do projeto do compose (o nome da pasta); confira com `docker volume ls`.

Rodando pela IDE ou com `dotnet run --project src/IdentityGateway.Api`, a API sobe em
`https://localhost:7206` e a raiz redireciona para a documentação Scalar.

**Zero skips.** Os 6 que a fundação herdava do esqueleto (guardas de arquitetura sem tipo para inspecionar,
e testes de `401` sem endpoint protegido) fecharam com a vertical de registro (PR #1) e com esta fatia.

### Demonstração: o tenant é provisionado quando o Keycloak volta

A API aceita o tenant com o Keycloak fora do ar e o provisiona sozinha quando ele volta (spec §16). Pressupõe o
compose de pé e as migrations já aplicadas (`docker compose up -d` + `docker compose run --rm api --migrate`,
acima). O token é de platform-admin, assinado com a chave de desenvolvimento do compose — o mesmo formato que a
API valida hoje:

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

---

## Por onde começar a ler

| Documento | O que responde |
|---|---|
| [**Documentação de negócio**](docs/documentacao-negocio.md) | **Comece aqui.** O que a solução faz, para quem e como funciona — com 16 diagramas |
| [**Especificação arquitetural v2.5**](docs/especificacao-arquitetural-v2.5.md) | A referência de implementação: domínio, endpoints, ADRs, código de referência |
| [**Revisão crítica**](docs/revisao-critica.md) | Os 33 achados que produziram as correções |

---

## A arquitetura em um parágrafo

Dois planos com ciclos de vida independentes. O **Control Plane** é a Gateway: tenants, membros,
papéis, permissões, domínios, IdPs federados e aplicações OIDC, tudo como recurso REST. O
**Data Plane** são as APIs de negócio, que validam o token localmente com as chaves públicas do
Keycloak (JWKS), sem consultar ninguém. Credenciais são digitadas exclusivamente no Keycloak, e
os tokens são emitidos por ele direto para a aplicação cliente — a Gateway nunca vê uma senha e
nunca está no caminho do token.

A consequência prática: **se a Gateway cair, logins continuam funcionando**, e as requisições de
negócio que dependem só de papéis globais também.

### O limite, dito na cara

O ponto único de falha não foi eliminado — **foi deslocado**. Com o Keycloak fora do ar, nenhum
login acontece e nenhum token é renovado; decorridos os 5 minutos de vida do access token, o Data
Plane inteiro para. O ADR-002 tira a Gateway do caminho crítico; ele não torna o sistema
resiliente à queda do Keycloak. Registrar isso faz parte do projeto.

---

## Decisões arquiteturais

Dez ADRs, com o texto completo na [especificação §4](docs/especificacao-arquitetural-v2.5.md#4-decisões-arquiteturais-adrs).

| ADR | Decisão |
|---|---|
| 001 | Tenancy com Organizations em realm único |
| 002 | Gateway fora do caminho de emissão e validação de tokens |
| 003 | Sem ROPC e sem manipulação de credenciais |
| 004 | Claims vêm exclusivamente de Protocol Mappers |
| 005 | Papéis globais no token, permissões finas na Gateway |
| 006 | Consistência via Outbox, provisionamento idempotente e reconciliação |
| 007 | Sincronização Keycloak → Gateway por leitura de eventos |
| 008 | Integração com o Keycloak isolada atrás de uma porta |
| 009 | Na v1, um usuário pertence a um único tenant |
| 010 | Um só executor por job de fundo, via advisory lock |

---

## Como a especificação chegou aqui

A linha evolutiva é parte do que este repositório demonstra. Os documentos **se sucedem, não
competem** — cada versão fecha pontos que a anterior deixou em aberto, e **nenhum ADR foi revogado
em nenhum dos saltos**.

```
ideia → v2.0 → [revisão crítica: 33 achados] → v2.1 → [documentação de negócio] → v2.2 → v2.3 → v2.4 → v2.5
```

- **v2.1** incorporou a revisão crítica — três frentes independentes, 33 achados e 8 contradições.
  Duas delas mereceram destaque: o critério de pronto era mais estreito que o princípio que deveria
  provar (CI-8), e uma dupla de falhas de isolamento que precisavam ser corrigidas **juntas**, já
  que corrigir uma isoladamente reabriria o sistema com a outra ativa (C1 + C2).
- **v2.2** nasceu ao escrever a documentação de negócio, que percorreu a spec inteira e encontrou
  nove lacunas — entre elas um limite de plano declarado que nenhuma operação verificava.
- **v2.3** fechou as lacunas de arquitetura e reconciliou a spec com o template
  [CleanStart](https://github.com/Joseleno/CleanStart), base de todos os projetos.
- **v2.4** corrigiu premissas erradas sobre o Keycloak, verificadas contra o código-fonte da versão
  26.7.4, na fatia da fundação Keycloak.
- **v2.5** registrou a fatia B: transporte em processo até o broker e a decisão de desistir no handler.

Vale registrar o que a revisão **não** conseguiu derrubar: dos oito alvos examinados, sete
resistiram inteiros. E das cinco afirmações verificadas contra documentação oficial, duas
**inocentaram** a especificação. Uma revisão que só reporta achados não permite distinguir "a spec
é frágil" de "o revisor foi agressivo".

---

## Stack

**No compose hoje:** .NET 10 · PostgreSQL · Redis · EF Core 10 · Carter · Serilog · OpenTelemetry ·
Seq · Jaeger · xUnit v3

**Entram no M0:** Keycloak 26 · RabbitMQ · Mailpit

Parte do template [CleanStart](https://github.com/Joseleno/CleanStart).

---

## Licença

MIT
