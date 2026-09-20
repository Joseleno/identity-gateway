# Especificação Arquitetural: Multi-Tenant Identity & Auth Gateway (.NET 10 + Keycloak)

## 1. Visão Geral e Princípios

Este documento especifica a arquitetura de backend para uma **API-First Multi-Tenant Authentication & Identity Gateway** construída em **.NET 10** e integrada com o **Keycloak** como Provedor de Identidade (IdP).

A API atua como uma camada de abstração e orquestração de governança corporativa (*Facade/BFF Pattern*), permitindo que múltiplas aplicações clientes (Web, Mobile, Serviços M2M) autentiquem usuários, gerenciem tenants e validem permissões de forma centralizada e desacoplada.

```
┌─────────────────────────────────────────────────────────────────────────┐
│                           Aplicações Clientes                           │
│                 (SPA / Mobile / Third-Party Services / M2M)             │
└────────────────────┬────────────────────────────────┬───────────────────┘
                     │                                │
      Login / Token  │ (OIDC / PKCE)                  │ Requisições com JWT
      Management     │                                │
                     ▼                                ▼
┌────────────────────────────────────────┐  ┌────────────────────────────┐
│      Auth Gateway API (.NET 10)        │  │   Demais Microserviços     │
│   (Orquestração & Admin Management)    │  │       (Resource APIs)      │
└────────────────────┬───────────────────┘  └─────────────┬──────────────┘
                     │                                    │
                     │ Keycloak Admin REST API            │ Validação Local de
                     │ (Management / Provisioning)        │ JWT via JWKS
                     ▼                                    ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                             Keycloak Server                             │
│                  (Core OIDC / OAuth 2.0 / User Store)                   │
└─────────────────────────────────────────────────────────────────────────┘

```

### Princípios Arquiteturais Chave:

1. **API-First & Stateless:** Toda a exposição de recursos de gestão é feita via endpoints RESTful protegidos, permitindo fácil integração por qualquer ecossistema frontend ou backend.

2. **Separação de Responsabilidades (Control Plane vs. Data Plane):**

   * **Control Plane (Auth Gateway API):** Responsável pelo gerenciamento de tenants, criação de usuários, atribuição de permissões e orquestração de políticas.

   * **Data Plane (Validation):** As APIs de negócio validam os tokens emitidos pelo Keycloak de forma autônoma via chaves públicas (JWKS), eliminando gargalos e SPOF (*Single Point of Failure*).

## 2. Funcionalidades da Auth Gateway API (.NET 10)

### 2.1. Gestão e Provisionamento de Tenants (Multi-Tenancy)

* **Criação Dinâmica de Tenants:**

  * Provisionamento de isolamento através de *Realms* dedicados ou *Groups/Organizations* dentro de um Realm global do Keycloak.

  * Mapeamento automático de configurações padrão (Políticas de senha, Expiração de token, Temas de login e IdPs federados).

* **Gestão de Metadata do Tenant:**

  * Armazenamento e associação de parâmetros customizados de negócio do tenant (planos, limites de usuários, status de assinatura) no banco de dados da Gateway API, vinculando ao `tenant_id`.

### 2.2. Gestão Unificada de Usuários e Perfis (Identity Management)

* **Provisionamento de Usuários via API:**

  * Endpoints REST para criação, atualização, desativação e exclusão (*Soft Delete*) de usuários no Keycloak através da **Keycloak Admin REST API**.

* **Gestão de Roles e Group Mappings:**

  * Atribuição de permissões dinâmicas por tenant.

  * Agrupamento de permissões (*Roles*) alinhado às necessidades do tenant (*ex.: TenantAdmin, FinancialManager, Reader*).

### 2.3. Enriquecimento e Customização de Tokens (Token Enhancement)

* **Injeção de Custom Claims:**

  * Utilização de *Protocol Mappers* no Keycloak ou enriquecimento customizado na Gateway para injetar dados como `tenant_id`, `permissions[]`, `organization_code` e `tier_level` diretamente no JWT (`access_token`).

## 3. Onde Entra o Keycloak e Protocolos de Autenticação

### 3.1. Papel do Keycloak no Ecossistema

O Keycloak atua estritamente como o **Provedor de Identidade Primário (Identity Provider - IdP)** e **Servidor de Autorização (Authorization Server)**.

| **Componente** | **Responsabilidade do Keycloak** | **Responsabilidade da Auth Gateway (.NET 10)** | 
| **Armazenamento de Senhas** | Hash seguro (Argon2 / PBKDF2), expiração e resets | Nenhuma (A API nunca manipula senhas em plaintext) | 
| **Emissão de Tokens** | Assinatura digital de tokens JWT (RS256/ES256) | Solcitação/Orquestração de credenciais e escopos | 
| **MFA & TOTP** | Execução e validação de 2FA/TOTP | Exigência via políticas de *Step-Up Auth* | 
| **Admin Operations** | Execução das operações de diretório | Consumo tipado via `HttpClient` da Admin REST API | 

### 3.2. Integração com OAuth 2.0 e OpenID Connect (OIDC)

A arquitetura adota rigorosamente os padrões da RFC 6749 (OAuth 2.0) e especificações OpenID Connect:

1. **Authorization Code Flow com PKCE (Proof Key for Code Exchange):**

   * Padrão obrigatório para aplicações interativas (Web Single Page Apps, Mobile).

   * A aplicação redireciona o usuário para o Keycloak, obtém o `code` autorizativo e troca pelo `access_token`, `id_token` e `refresh_token`.

2. **Client Credentials Grant:**

   * Utilizado para comunicação **Machine-to-Machine (M2M)** entre microsserviços parceiros e a Auth Gateway API.

3. **RefreshToken Rotation:**

   * Garantia de reutilização única de refresh tokens com expiração configurada por tenant.

### 3.3. Orquestração de Single Sign-On (SSO) e Identity Federation

#### Single Sign-On (SSO):

* Sessão centralizada mantida pelo Keycloak. Uma vez autenticado em uma aplicação do ecossistema, o usuário navega entre os sistemas da organização sem necessidade de re-autenticação.

#### Identity Federation (Brokering):

* **SAML 2.0 / OIDC External IdPs:** Permite que tenants corporativos utilizem seus próprios provedores de identidade (ex.: Azure AD/Entra ID, Okta, Google Workspace).

* **Orquestração na API:** A Auth Gateway identifica o tenant pelo domínio/e-mail no início do fluxo e redireciona automaticamente a requisição OIDC para o `kc_idp_hint` correto no Keycloak.

```
[ Usuário: user@empresaA.com ] ──► [ Auth Gateway API ] ──(Identifica Tenant A)──► [ Keycloak ]
                                                                                       │
                                                                                       ▼ (Redirect para IdP externo)
                                                                            [ Azure AD do Tenant A ]

```

## 4. Arquitetura de Validação de Segurança no .NET 10

### 4.1. Configuração do JWT Bearer em Microserviços

As APIs do ecossistema validam as requisições assincronamente através de chaves públicas obtidas do endpoint JWKS do Keycloak (`/.well-known/openid-configuration`).

Exemplo de abstração do pipeline no .NET 10 (`Program.cs`):

```
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Keycloak:RealmUrl"];
        options.Audience = builder.Configuration["Keycloak:Audience"];
        options.RequireHttpsMetadata = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization(options =>
{
    // Política baseada em atributo de Tenant e Permissões customizadas
    options.AddPolicy("TenantAdminOnly", policy =>
        policy.RequireClaim("tenant_id")
              .RequireClaim("roles", "tenant-admin"));
});

```

## 5. Limites Claros: O que EVITAR e O que NÃO Fazer (Anti-Patterns)

Para manter os padrões de mercado e a segurança no nível corporativo, as seguintes práticas são **estritamente proibidas** na arquitetura:

### ❌ 1. Não utilizar Resource Owner Password Credentials Grant (ROPC)

* **O que é:** Enviar usuário e senha via formulário/JSON para a Auth Gateway API e repassar ao Keycloak.

* **Por que evitar:** Este fluxo foi descontinuado no OAuth 2.1. Ele expõe credenciais brutas ao backend da aplicação, anula recursos de MFA nativos do Keycloak e quebra a confiança do modelo OIDC.

* **Solução:** O login interativo deve sempre utilizar redirecionamento via *Authorization Code Flow + PKCE*.

### ❌ 2. Não intermediar a validação de tokens nas APIs de Negócio

* **O que é:** Fazer com que os outros microserviços façam uma chamada REST para a Auth Gateway a cada requisição para validar se um JWT é válido.

* **Por que evitar:** Cria um gargalo massivo de I/O, latência desnecessária e converte o ecossistema em um monólito distribuído (*Single Point of Failure*).

* **Solução:** Validação local e *stateless* do token JWT usando a chave pública do Keycloak via biblioteca padrão (`Microsoft.AspNetCore.Authentication.JwtBearer`).

### ❌ 3. Não armazenar senhas ou segredos de usuários no Banco da API

* **O que é:** Salvar credenciais ou cópias de hashes de senhas no banco de dados local do .NET.

* **Por que evitar:** Viola compliance (LGPD/GDPR/HIPAA) e duplica responsabilidades que são exclusivas do Keycloak.

* **Solução:** Delegation total do gerenciamento de segredos para o Keycloak.

### ❌ 4. Não misturar lógica de domínio com a SDK do Keycloak Admin

* **O que é:** Fazer chamadas diretas ao Keycloak Admin API dentro de Controllers ou Handlers de Regras de Negócio.

* **Por que evitar:** Acopla fortemente seu backend às especificidades da API REST do Keycloak.

* **Solução:** Isolar a integração com o Keycloak atrás de uma interface de infraestrutura (ex.: `IIdentityManagementService`).

## 6. Roteiro de Implementação Recomendado para o GitHub

1. **Infraestrutura Local (Docker Compose):**

   * Conteinerização do Keycloak, PostgreSQL (banco do Keycloak e banco da Gateway) e aplicação .NET 10.

   * Exportação do Realm inicial (`realm-export.json`) configurado com clients, roles e mappers.

2. **Camada de Integração (`Keycloak.Admin.SDK`):**

   * Implementação de cliente tipado no .NET 10 via `HttpClient` com resiliência via `Microsoft.Extensions.Http.Resilience` (Polly).

3. **Documentação OpenAPI 3.1:**

   * Utilização dos novos recursos nativos do .NET 10 para gerar documentação Swagger/OpenAPI interativa com suporte ao esquema de segurança OAuth2.