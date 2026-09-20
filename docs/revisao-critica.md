# Revisão crítica da especificação — IdentityGateway v2.0

> Revisão por três frentes independentes (negócio, arquitetura, adversarial) sobre
> `especificacao-arquitetural.md` v2.0, tendo `documenta_o_arquitetural.md` como documento de origem.
>
> **Data:** 2026-09-20 · **Status:** completo.
> **Postura acordada:** as decisões já fechadas (ADR-001 a ADR-009) entraram como hipóteses a testar,
> não como premissas.

---

## Veredito

A spec está **acima da média** para um projeto de portfólio: a separação Control Plane / Data Plane é
coerente, os ADRs são decisões reais com consequências assumidas, e a seção 12.1 é material de referência
que resiste a verificação.

Ela **não está pronta para implementar como escrita** — não por fragilidade de desenho, mas por um conjunto
de afirmações sobre a API real do Keycloak que não se sustentam, e por duas lacunas no ciclo de vida do tenant.

**Nenhum ADR precisou ser revogado.** Todos os achados são corrigíveis dentro do desenho atual.

**Total:** 14 achados críticos, 16 relevantes, 3 menores, 8 contradições internas. A frente adversarial examinou
ainda 8 pontos que **resistiram ao ataque** — registrados em [O que a revisão NÃO conseguiu atacar](#o-que-a-revisão-não-conseguiu-atacar),
seção que calibra o peso de todo o resto.

**As duas correções de maior alcance, se houver tempo para poucas:**

1. **CI-8** — reescrever uma linha da §18 converte C2, C12 e CI-1 de achados de revisão em falhas automáticas do
   critério de pronto.
2. **C1 + C2 juntos** — corrigir o claim `tenant_id` isoladamente reabre o sistema com o IDOR de sub-recurso ativo.

---

## O tema central

Três revisores independentes, com ângulos diferentes, convergiram para a mesma fronteira: **decisões que a
Gateway toma no seu próprio banco e presume refletidas no Keycloak, mas que não chegam lá.**

| Achado | Frente que encontrou | O que a Gateway assume | O que o Keycloak faz |
|---|---|---|---|
| `tenant_id` aninhado | arquitetura + adversarial | claim plano, pronto para comparar | objeto aninhado chaveado por alias |
| Suspensão de tenant | negócio | tenant suspenso não opera | usuários seguem logando normalmente |
| Revogação de sessões | arquitetura | desativação encerra o acesso | offline tokens sobrevivem ao logout |
| Busca por alias | arquitetura | "consultar antes de criar" é idempotente | alias não é campo de busca |

É um padrão, não coincidência: a spec modela com rigor o lado .NET e **assume** o lado Keycloak.

---

## Achados críticos

`✅` = verificado contra documentação oficial ou código-fonte durante esta revisão.

### C1. O claim `tenant_id` não existe como claim plano ✅

**Frentes:** arquitetura (#1) + adversarial · **Onde:** §12, §11.7

O mapper nativo (`OrganizationMembershipMapper`) emite um claim `organization` como **objeto aninhado
chaveado pelo alias**:

```json
"organization": { "acme-corp": { "id": "f8d3c4e1-...", "groups": ["/Engineering/Backend"] } }
```

Pela Armadilha 1 que a **própria §12.1 documenta**, objeto aninhado vira um único claim com o JSON inteiro
como valor e `ValueType: "JSON"`. Logo `context.User.FindFirst("tenant_id")` retorna `null`, e o
`SameTenantRequirement` — a defesa contra IDOR entre tenants — nunca chega a dar `Succeed`.

A spec cai exatamente na armadilha que ela própria explica, e na peça de isolamento.

**A sequência de falha é o perigo real.** No dia 1, todas as rotas com `{tenantId}` retornam 403 e
M2/M4/M5/M6 ficam inúteis. O sintoma "tudo nega" força uma correção às pressas **no handler de isolamento,
com a suíte vermelha** — a pior condição possível para editar esse código. As três correções ingênuas são
todas mais rápidas de escrever que a certa:

| Correção ingênua | Resultado |
|---|---|
| `.First().Name` | devolve o alias; a rota tem GUID → 403 persiste → mexe de novo, sob mais pressão |
| `.Any(p => p.Name == routeTenant)` | acesso a **qualquer** organização do usuário (ADR-009 é convenção da Gateway, não do Keycloak) |
| `org.Contains(routeTenant)` | substring sobre o JSON bruto: slug `acme` casa dentro de `acme-corp` |

A terceira é **forjável por entrada do usuário**: o `Slug` vem no corpo do `POST /tenants`, então basta
escolher um slug que seja prefixo de outro tenant.

**Correção:** não depender do mapper de organização. Sincronizar um atributo de usuário `tenant_id` +
**User Attribute mapper plano** — o mecanismo que o ADR-004 já usa para `tier`. Mais `context.Fail()` em todo
caminho de rejeição (ver C3) e um teste gêmeo ao de `roles`, afirmando `ValueType != "JSON"`.

**Custo:** gravar o atributo no provisionamento **e** no fluxo 9.4, onde existe janela em que o usuário loga
sem `tenant_id`.

---

### C2. IDOR por id de sub-recurso — e o teste que dá falsa confiança

**Frente:** adversarial (Ataque 1) · **Onde:** §11.7, §10.1, §13

`SameTenantRequirement` compara **apenas** o `{tenantId}` da rota com o claim. Nada valida que o `{memberId}`,
`{clientId}` ou permission-set da rota **pertence** àquele tenant.

**Cenário:**

1. Ana é `tenant-admin` do tenant A (`tenant_id = A`).
2. Ana descobre o `memberId` de Bruno, do tenant B. GUIDs vazam: URL compartilhada, log, trilha de auditoria, ticket de suporte.
3. `DELETE /api/v1/tenants/A/members/{memberId-do-Bruno}` — exclusão definitiva LGPD.
4. O requirement passa: a rota diz A, o token diz A. ✅
5. O handler carrega `Member` por id. `Member` é **aggregate root separado**, que apenas *referencia*
   `TenantId` (§6.1). Sem filtro por tenant — que a spec nunca exige — Bruno é apagado do Keycloak.

Mesmo vetor em `PUT .../roles`, `POST .../clients/{clientId}/rotate-credentials` (DoS no parceiro) e
`GET .../effective-permissions`.

**O agravante:** a suíte da §13 percorre rotas com `{tenantId}` testando "token de A → rota de B" — o vetor
que **já está protegido**. Ela fica verde e nunca testa "token de A, rota de A, sub-recurso de B". O teste
que o projeto exibe como principal prova de isolamento valida o caso coberto e ignora o exposto.

**Correção:** todo carregamento de sub-recurso filtra por tenant no repositório — `GetAsync(tenantId, memberId)`
como **única** assinatura, sem sobrecarga só por id; global query filter como rede; e a suíte passa a gerar,
por rota com sub-recurso, um caso "id de outro tenant → 404".

> ⚠️ **Sequenciamento:** corrigir C1 isoladamente **reabre o sistema com C2 ativo**. Os dois andam juntos, antes do M2.

---

### C3. `FindOrganizationByAliasAsync` não existe — a idempotência do ADR-006 não fecha ✅

**Frente:** arquitetura (#2) · **Onde:** §11.6, ADR-006

`GET /organizations` aceita `search` + `exact`, mas o match exato é sobre **o nome da organização ou um de
seus domínios**. Alias não é campo de busca.

O método sustenta **duas** funções no código da §11.6, e ambas caem:

- a consulta prévia ("consultar antes de criar");
- a **recuperação pós-409**, dentro do `catch (KeycloakConflictException)`.

**Consequência:** a segunda entrega da mensagem toma 409 → não recupera o id → lança → o tenant vai para
`ProvisioningFailed` **tendo sido provisionado com sucesso**. O estado órfão que o ADR-006 existe para
impedir é produzido pelo próprio mecanismo de proteção.

**Correção:** gravar um atributo custom na Organization (`gateway_tenant_id`) e buscar via `searchQuery`
(formato `key:value`) — chave estável, imune a rename, e que de quebra resolve a correlação de que o job de
reconciliação da §9.1 precisa. Buscar por `name` não serve: `Name` é livre e `Slug` é normalizado.
**A confirmar:** se `searchQuery` filtra atributos de Organization.

---

### C4. As duas proteções contra Organizations duplicadas falham pelo mesmo cenário ✅

**Frente:** arquitetura (#5) · **Onde:** §11.8, §17 (anti-pattern 8), §11.6

| Camada de proteção | Estado |
|---|---|
| Primária — não fazer retry em POST | `DisableForUnsafeHttpMethods()` **existe** e o código da §11.8 está correto, **mas** a issue dotnet/extensions **#6708** relata que o retry acontece mesmo assim, 3× |
| Secundária — "consultar antes de criar" | apoiada numa busca que não existe (C3) |

O anti-pattern nº 8 proíbe retry em POST para a Admin API justamente porque "pode criar recursos duplicados".
A spec declara o anti-pattern e confia numa API que pode não cumpri-lo, com uma rede de segurança furada. O
Keycloak não oferece `Idempotency-Key` como terceira rede.

**Correção:** (a) fixar versão mínima do pacote e acompanhar a #6708; (b) **não confiar na configuração** —
teste de integração que **conta chamadas** via `DelegatingHandler` (a §13 já prevê esse handler para simular
falha; basta estendê-lo); (c) `DisableFor(HttpMethod.Post, ...)` explícito além do helper; (d) corrigir C3.

---

### C5. O contador de vagas diverge permanentemente por design

**Frente:** adversarial (Ataque 2) · **Onde:** §11.1, §6.1

`ReleaseSeat()` é `ActiveMembers = Math.Max(0, ActiveMembers - 1)` — não é idempotente e não consulta o
estado do membro.

**Cenário:** tenant com `MaxUsers = 10` e `ActiveMembers = 10`. O admin chama `deactivate` → 9. A resposta
HTTP se perde (timeout de balanceador) e o cliente repete. Se `Member.Deactivate()` for idempotente e
retornar sucesso — comportamento natural num desenho "garanta que" — o handler chama `ReleaseSeat()` de novo
→ **8, com 9 ativos reais**.

`xmin` **não protege**: concorrência otimista detecta escrita simultânea, não repetida. Repita N vezes e o
contador chega a 0 com o tenant cheio — o limite de plano deixa de existir.

`Math.Max(0, ...)` não é salvaguarda: é **o que esconde a divergência**, impedindo o valor negativo que
denunciaria o bug.

Agravante: a reconciliação da §9.1 compara tenants `Active` com Organizations — **nunca** compara contador
com contagem real de membros. A divergência é invisível por design.

**Correção:** decremento apenas como consequência da transição efetiva de estado do membro; retry explícito
em `DbUpdateConcurrencyException` (ver C11); job que compare `ActiveMembers` com `COUNT`; remover o
`Math.Max`, que apaga a evidência.

---

### C6. A queda da Gateway derruba as APIs de negócio — contradição interna

**Frente:** adversarial (Ataque 3) · **Onde:** §2.1 vs §9.6, ADR-002, §19

> **§2.1:** *"Se ela cair, logins e requisições de negócio continuam funcionando"*
> **§9.6, passo 5:** *"Se a Gateway estiver indisponível e não houver cache, a decisão é **negar**"*

**Raio de impacto (delimitado):**

| Dimensão | Impacto |
|---|---|
| Instâncias | só as com cache frio para o par (`sub`, `tenant_id`) |
| Endpoints | só os de nível 2 (`RequirePermission`); nível 1 segue funcionando |
| Usuários | ~20% das requisições com 5 réplicas e round-robin |
| Código HTTP | **403 Forbidden**, não 503 |
| Duração | até a Gateway voltar; sem degradação graciosa, sem stale |

**Agravante 1 — o código HTTP mente.** Responde 403 ("você não tem permissão") quando a causa real é "uma
dependência caiu". O suporte investiga permissões; o plantão procura uma mudança de papéis que não houve.

**Agravante 2 — vira rotina, não incidente.** Deploy coordenado: a Gateway sobe junto das Resource APIs e
leva ~40s a mais (migration + `ValidateOnStart` + `ready` checando Postgres, RabbitMQ e metadata OIDC).
Nesses 40s, todas as Resource APIs têm cache vazio e a Gateway está fora → **403 em 100% dos endpoints de
nível 2, a cada deploy**.

**Correção:** (a) corrigir a redação de §2.1 e do ADR-002 — *"requisições que dependem apenas de papéis
globais continuam funcionando; permissões finas dependem de cache quente ou da Gateway disponível"*;
(b) **503 + `Retry-After`** em vez de 403 quando a negação vem de indisponibilidade — distinguir "negado" de
"não sei"; (c) stale-while-revalidate com teto de idade + warm-up (a Resource API só entra em rotação após
falar com a Gateway uma vez); (d) registrar o limite na §19.

Fail-closed é a escolha **certa** para autorização. O defeito é a spec afirmar o contrário em dois lugares e
omitir o limite.

---

### C7. O ADR-007 promete deduplicação que o event store não permite

**Frente:** arquitetura (#6) · **Onde:** ADR-007, §9.4, §19

Os eventos da Admin API **não têm id estável**, então "checkpoint e deduplicação" não é implementável como
está escrito.

**Cenário:** o poller lê `dateFrom=T`, `first=0&max=100`. Entre a página 1 e a página 2 chegam 20 eventos
novos, que deslocam a janela e **empurram 20 eventos para fora da página lida** — perda silenciosa, sem erro.
Pior: se `eventsExpiration` for menor que um downtime do poller, os eventos do intervalo deixam de existir.
O fluxo 9.4 para de registrar membros nascidos em login federado e **o limite de plano é contornado sem
rastro** — o que fecha o círculo com N10.

**Correção:** dedup por **tupla** (`time`, `type`, `userId`, `clientId`, hash dos `details`) com janela móvel;
checkpoint por timestamp com **sobreposição deliberada** (reler os últimos N segundos e descartar por tupla,
trocando reprocesso por não-perda); **alarme quando `now - checkpoint > eventsExpiration × 0,5`**, único sinal
possível de perda iminente. Registrar na §19 que a sincronização é *at-least-once com perda possível sob
downtime prolongado*, e que o Event Listener SPI é o que remove essa limitação.

---

### C8. `offline_access` vem ligado por padrão e sobrevive ao logout

**Frente:** arquitetura (#7) · **Onde:** §9.5, §5, §10.3

`POST .../users/{id}/logout` remove sessões online, mas **não remove sessões offline**. E `offline_access`
está no `default-roles` de todo usuário do realm — ele aparece no **próprio token de exemplo da §12.1 do
documento**. A evidência estava impressa na spec o tempo todo.

Qualquer usuário que tenha obtido um offline token continua conseguindo refresh **depois** de a Gateway
"revogar suas sessões". A §9.5 promete exatamente o contrário, e isso também esvazia a suspensão de tenant (N6).

**Correção:** **remover `offline_access` do `default-roles` no realm de bootstrap** — o projeto não usa offline
tokens; é hardening de uma linha e demonstrável por teste. É a melhor relação custo-benefício de toda a revisão.
Se for mantido, chamar também a remoção de sessões offline por client e declarar a janela.

Acrescentar à §19: *o access token já emitido sobrevive até 5 minutos à desativação — é o preço da validação
stateless.* Consequência correta do ADR-002, hoje não escrita.

---

### C9. Não existe onboarding do primeiro tenant-admin

**Frente:** negócio (#1) · **Onde:** §8, §9.1, M1/M2

`POST /tenants` é `platform-admin`. Convidar membro exige `tenant-admin` + `SameTenantRequirement`. Depois que
o tenant vira `Active`, **ninguém pode convidar o primeiro membro**: não existe tenant-admin ainda, e o
platform-admin não passa no `SameTenantRequirement` da rota de membros. O fluxo 9.1 termina em "Tenant →
Active" e o assunto morre ali.

É a primeira pergunta que qualquer avaliador faz: *"criei um tenant, e agora quem entra nele?"*

**Correção (recomendada):** `POST /tenants` passa a exigir `initialAdminEmail`; o consumidor de provisionamento
convida esse usuário já como `tenant-admin` antes de marcar `Active`. O tenant nasce operável por construção,
reusando a máquina idempotente do ADR-006.

A alternativa — deixar platform-admin furar o `SameTenantRequirement` — abre um bypass que contradiz o princípio 5.

---

### C10. O tenant não tem estado terminal — não existe offboarding

**Frente:** negócio (#2) · **Onde:** §6.2, §8, §19

A máquina de estados é `Pending → Active ⇄ Suspended`, mais `ProvisioningFailed`. Não há `Terminated` nem
`DELETE /tenants/{id}`. Um tenant provisionado é eterno; a Organization nunca é removida; o slug (imutável e
único) fica preso para sempre.

**A inconsistência de compliance:** a spec implementa direito ao esquecimento **por membro** (§9.5, LGPD art. 18),
mas não tem como encerrar o contrato de um cliente inteiro — que é exatamente o cenário em que a LGPD é
invocada de verdade.

**Correção:** estado `Terminated`, alcançável apenas a partir de `Suspended` (evita exclusão acidental de tenant
ativo); `DELETE /tenants/{tenantId}` (platform-admin + step-up); fluxo assíncrono "9.7 — encerramento de tenant"
espelhando o 9.1. Reusa toda a máquina de Outbox já especificada.

---

### C11. Handler não chama `context.Fail()` em nenhum caminho de rejeição

**Frente:** adversarial (#3) · **Onde:** §11.7

Três caminhos retornam `Task.CompletedTask` sem decidir: `Resource is not HttpContext`, `routeTenant` nulo, e
comparação falsa. Em ASP.NET Core, um requirement sem `Succeed` e sem `Fail` é apenas "ainda não satisfeito" —
**qualquer outro handler registrado para o mesmo requirement pode satisfazê-lo**, e só `Fail()` sobrevive a um
`Succeed` alheio.

Isso não é hipotético: a §8 exige que platform-admin acesse `GET /tenants/{tenantId}` de qualquer tenant, e a
forma natural de implementar isso é um segundo handler que dá `Succeed`. **A spec já contém o requisito que
transforma a omissão em furo.**

O comentário *"Fail closed: se qualquer lado estiver ausente, o acesso é negado"* descreve a intenção, não o código.

**Correção:** `context.Fail(new AuthorizationFailureReason(this, "..."))` nos três caminhos.

---

### C12. Rotas sem `{tenantId}` escapam do requirement e da suíte

**Frente:** adversarial (#4) · **Onde:** §11.7, §13

**Cenário:** alguém adiciona `GET /api/v1/members/{memberId}` (busca global, conveniência de suporte) com a
policy `TenantAdmin`. O `routeTenant` é nulo → o handler não decide (C11) → o acesso depende de quem mais está
registrado. E a suíte, que "percorre rotas com `{tenantId}`", **por construção nunca vê esse endpoint**.

**Correção:** teste de startup que varre o `EndpointDataSource` e falha se um endpoint com policy de tenant
**não** tiver `{tenantId}` no template — o inverso exato do teste já descrito na §13.

---

### C13. Bootstrap do primeiro `platform-admin` não especificado

**Frente:** adversarial (#5) · **Onde:** ausente em §10.2 e §15

`platform-admin` não é atribuível pela API (§11.2) e é exigido por `POST /tenants`. A única origem restante é o
`realm-identity-gateway.json`, **versionado num repositório público de portfólio**. Ou ele contém uma credencial
literal — e ela é pública — ou não contém, e o procedimento não existe.

**Correção:** senha aleatória gerada no `docker compose up` com `UPDATE_PASSWORD` obrigatório; teste que falha se
o JSON de bootstrap contiver credencial literal; ADR próprio.

---

### C14. Sem leader election: poller e reconciliação rodam em N réplicas

**Frente:** adversarial (#8) · **Onde:** ADR-007, §9.1; ausente em §7 e §15

Três réplicas da API = três `BackgroundService` lendo o mesmo checkpoint, buscando os mesmos eventos e
disparando `RegisterExternalMember` em triplicata. E como os eventos não têm id estável (C7), **a dedup do
ADR-007 não salva**. Três instâncias de reconciliação também podem reverter o trabalho uma da outra.

Duas falhas independentes que se compõem: o polling não consegue deduplicar, e nada garante que só um poller rode.

**Correção:** `pg_try_advisory_lock` por job; unique constraint em `(TenantId, ExternalUserId)` como rede final;
ADR próprio.

---

## Achados relevantes

### A3. Feature flag `organization` ausente — o M0 não sobe ✅
**Arquitetura (#3)** · §15, M0 · Falta `KC_FEATURES=organization` (**singular**). O container sobe, o import do
realm passa, e `POST .../organizations` responde 404.
→ Fixar a flag no compose, verificar se o toggle por realm também é necessário, e adicionar smoke test no M0 que
falhe em 404.

### A4. `InviteData` sem casa definida
**Arquitetura (#4)** · §11.3, §7 · Não aparece na §6 nem na §7. Se carrega e-mail e nome, carrega **dado pessoal** —
e a §6 promete que a Gateway não guarda dados pessoais. É o objeto de passagem que um implementador persiste sem
pensar, derrubando o argumento do ADR-003.
→ `record` em `Application/Abstractions/Identity` marcado como efêmero; `ExternalOrganizationId` no lugar da
`string` crua; regra de arquitetura proibindo entidade EF de referenciar tipos desse namespace.

### A8. "Api depende de Infrastructure só no composition root" não é verificável
**Arquitetura (#8)** · §7, §13 · Ferramentas de arquitetura operam sobre assemblies e tipos, não sobre "este
arquivo pode". Com a referência no `.csproj`, qualquer endpoint injeta `DbContext` sem que nada detecte.
→ Inverter a estrutura: a Api **não referencia** Infrastructure; um projeto de bootstrap compõe as duas. A regra
vira estrutural em vez de depender de disciplina. Fixar **ArchUnitNET**, não "A ou B".

### A9. `private_key_jwt` declarado, mecanismo .NET não especificado
**Arquitetura (#9)** · §10.2, §11.8 · A §11.8 mostra um handler obtendo token "via client credentials"; a §10.2
exige `private_key_jwt`. Não há suporte pronto em `Microsoft.Extensions.Http`: é preciso montar e assinar o client
assertion a cada pedido de token — onde mora o risco (reuso de `jti`, `exp` longo, chave mal guardada).
*(detalhamento interrompido)*

### A9b. Papéis de realm e ADR-009: motivo incompleto
**Arquitetura (K9)** · A afirmação é verdadeira, mas o motivo está errado: papéis de realm são globais, sem vínculo
com Organization. O isolamento vem do ADR-009 **mais** o `SameTenantRequirement` — não do Keycloak, como o ADR
sugere. Consequência não listada: `tenant-admin` de A e de B são o **mesmo role object**; papéis por tenant são
impossíveis nesse modelo.

### A14. OpenAPI 3.1 do .NET 10 não entrega o que a §18 exige
**Arquitetura (K14)** · Entrega schemas, `[ProducesResponseType]` e XML docs. **Não entrega** OAuth2 com flows/PKCE
(exige `IOpenApiDocumentTransformer`) nem exemplos de request/response (exige transformer sobre `OpenApiExample`) —
e a §18 exige exemplos como critério de pronto. Scalar é pacote de terceiro.

### N4. Nenhum caminho de auto-serviço
**Negócio (#4)** · §8, §1.1 · Todo tenant nasce de um `POST /tenants` de platform-admin. Para portfólio é escolha
legítima, mas a spec não diz que é escolha — um avaliador lê como esquecimento.
→ Uma linha em "Fora do escopo" declarando o modelo sales-led. Custo zero.

### N5. Regra de vagas ambígua: `Invited` ocupa vaga?
**Negócio (#5)** · §6.1, §11.1 · A spec nunca diz em que transição `ReserveSeat` é chamado. Se no convite, convite
não aceito trava a vaga para sempre (não há expiração — N7). Se na ativação, N convites simultâneos estouram o plano
na aceitação — que chega pelo polling do ADR-007, tarde demais para recusar.
→ Fixar que `Invited` **ocupa** vaga, liberada em `Deactivated`, `Erased` e **expiração do convite**. Isso torna N7
pré-requisito, não extra.

### N6. Suspensão de tenant não define o efeito sobre os membros
**Negócio (#6)** · §6.2, §8 · Os usuários do tenant suspenso **continuam logando e usando as Resource APIs**: o token
é emitido pelo Keycloak e validado localmente (ADR-002), e a suspensão não toca o Keycloak. Um inadimplente suspenso
segue operando. "Suspender" é só uma flag no banco da Gateway — e a spec argumenta contra flags sem efeito.
→ O consumidor de `TenantSuspended` desabilita os usuários **e revoga sessões** (reusa `RevokeSessionsAsync`, que já
existe em `IIdentityProvider`); na reativação, restaura só quem estava `Active`, o que exige persistir esse estado no
`Member`. Ver C8 sobre o alcance real da revogação.

### N7. Ciclo de vida do convite inexistente
**Negócio (#7)** · §6.1, §8 · Sem expiração, sem reenvio, sem cancelamento. `Invited` é estado sem saída automática.
Reenvio de convite é o ticket de suporte nº 1 de qualquer plataforma multi-tenant.
→ `InvitedAt` + expiração por job (alinhada ao prazo do link do Keycloak) liberando a vaga; `POST .../resend-invite`
e `DELETE` do convite pendente.

### N8. Downgrade de plano abaixo dos ativos sem regra
**Negócio (#8)** · §8, §6.1 · `PATCH /tenants/{id}` permite trocar o plano; a invariante diz que ativos nunca excedem
`MaxUsers`. Um tenant com 40 ativos migrando de 50 para 10 viola a invariante no instante da troca.
→ Rejeitar com `409` + Problem Details nomeando quantos membros precisam ser desativados antes. Preserva a invariante,
não destrói dados do cliente, e vira um bom teste de domínio abrindo o M1.

### N9. ADR-009: custo de negócio subdeclarado
**Negócio (#9)** · A decisão está **certa** para a v1 (multi-tenant por usuário desmontaria o `SameTenantRequirement`,
que é a peça de segurança que o projeto quer exibir). Falta a consequência comercial escrita: um consultor que atende
dois clientes precisa de duas contas; um MSP não usa a plataforma.
→ Parágrafo "Consequência de negócio" no ADR-009. Só texto, custo zero.

### N10. O plano não limita nada em tenant federado
**Negócio (#10)** · §9.4, §19 · Basta ter federação para ter usuários ilimitados — e federação é o que se vende ao
plano maior. O limite fura exatamente onde mais importa comercialmente. Agravado por C7: o mecanismo que deveria
*detectar* o estouro também perde eventos.
→ Janela de tolerância (ex.: 7 dias) após `TenantOverSubscribed`, findada a qual os excedentes são desativados via
`SetUserEnabledAsync`. Sem authenticator customizado em Java.

### X7. Retry por conflito de `xmin` é prometido e não existe
**Adversarial (#7)** · §11.1 · Duas reservas concorrentes: a segunda lança `DbUpdateConcurrencyException`, nada a
captura, vira 500. O comentário *"a segunda é reprocessada com o valor atual"* não tem implementação em lugar nenhum
da spec. No caminho do consumidor MassTransit funciona **por acidente do broker**, não por desenho — e some no
caminho HTTP síncrono.
→ Behavior no pipeline de comandos com 3 tentativas recarregando o aggregate; esgotado, 409 em Problem Details.

### X9. Migrations com múltiplas instâncias subindo em paralelo
**Adversarial (#9)** · ausente · Rolling update sobe 3 pods simultâneos; se cada um roda `Migrate()` no startup, duas
transações DDL concorrem — a segunda falha e o pod entra em CrashLoopBackOff, ou aplica parcialmente. A spec nunca
diz quem aplica migration.
→ Migration como passo separado do pipeline (init container/job), nunca no startup da API.

### X10. Rollback de migration deixa Organizations órfãs e habilitadas
**Adversarial (#10)** · ADR-006, §9.1 · Deploy ruim é revertido: a migration volta, o schema volta, mas as
Organizations criadas no intervalo continuam no Keycloak. A reconciliação só olha `Active → Organization`, **nunca o
inverso** — são órfãs permanentes. E ficam *habilitadas*, logo usuários daquele domínio logam num tenant que a Gateway
não conhece.
→ Reconciliação bidirecional; criar Organization com `Enabled: false`, habilitando só no último passo do provisionamento.

### X12. `Idempotency-Key` por 24h sem local definido
**Adversarial (#12)** · §10.3 · A spec não diz onde a resposta fica guardada. Se for em memória — o padrão quando não
se especifica — a chave gravada na instância 1 não protege a instância 2, e **a idempotência não existe em produção
multi-réplica**. O duplo POST de C5 passa direto.
→ Store compartilhado (Postgres com índice em `(key, expires_at)` + job de limpeza). Redis não está no compose: ou
entra, ou não é opção.

---

## Achados menores

- **X11 · Auditoria append-only sem expurgo** (§10.3) — recebe entrada por ação de escrita **e** por evento
  administrativo do Keycloak; cresce monotonicamente, consultas por tenant degradam, backup incha. Nenhuma política em
  nenhuma seção. → Particionar por mês, retenção documentada.
- **N11 · ADR-005 sem UI para Permission Sets** — o desenho está bem resolvido; o recurso só é demonstrável via
  Scalar/curl. Sugere mover M5 para depois de M3/M7.
- **N12 · ADR-001, risco de compliance a nomear** — cliente com política de senha própria exigiria realm dedicado,
  mitigado na prática porque esse cliente tipicamente usa IdP federado, e aí a política é dele. Boa resposta de
  entrevista; deveria estar escrita.

---

## Verificações externas realizadas

Feitas pelo coordenador contra documentação oficial e código-fonte, porque o revisor de arquitetura não tinha acesso à
web nesta sessão.

| Item | Suspeita do revisor | Veredito |
|---|---|---|
| Organizations GA na 26.x | dúvida sobre preview | ✅ **GA na 26.0**, mas exige `--features organization` (singular) |
| Claim de organização | "emite id ou alias?" | ✅ **Nenhum dos dois**: objeto aninhado chaveado por alias → **C1** |
| Busca por alias | "talvez não exista" | ✅ Confirmado: `exact` casa **nome ou domínio**, não alias → **C3** |
| `DisableForUnsafeHttpMethods()` | "provavelmente não existe" | ❌ **Existe** e o código está correto — mas bug aberto #6708 → **C4** |
| `roles` no `DefaultInboundClaimTypeMap` | "talvez só `role` singular" | ❌ **Ambos presentes** (`ClaimTypeMapping.cs:121-122`); §12.1 correta |

**Calibração:** as suspeitas sobre Keycloak se confirmaram (3/3); as sobre .NET se refutaram a favor da spec (0/2).
Os itens .NET restantes sem verificação (K8 `private_key_jwt`, K10 step-up, K14 OpenAPI, K15 Testcontainers) foram
graduados um degrau abaixo por esse viés.

**A §12.1 sai ilesa da revisão.** É o trecho mais vendável do documento e resiste à verificação: o comportamento de
ponto no nome do claim, a config do `oidc-usermodel-realm-role-mapper`, o mecanismo do `MapInboundClaims` e a tabela
de combinações estão todos corretos. A ironia de C1 é que a spec explica a armadilha com precisão e depois pisa nela
em outro campo.

**Fontes:** [Keycloak 26.0.0](https://www.keycloak.org/2024/10/keycloak-2600-released) ·
[OrganizationMembershipMapper](https://www.keycloak.org/docs-api/latest/javadocs/org/keycloak/organization/protocol/mappers/oidc/OrganizationMembershipMapper.html) ·
[Discussion #32364](https://github.com/keycloak/keycloak/discussions/32364) ·
[Organization Groups](https://www.keycloak.org/2026/04/org-groups) ·
[Admin REST API](https://www.keycloak.org/docs-api/latest/rest-api/index.html) ·
[DisableForUnsafeHttpMethods](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.http.resilience.httpretrystrategyoptionsextensions.disableforunsafehttpmethods) ·
[dotnet/extensions#6708](https://github.com/dotnet/extensions/issues/6708)

---

## Corte mínimo demonstrável

Proposta do revisor de negócio. As quatro competências declaradas na §1 são provadas por:

| Marco | Conteúdo | Competência |
|---|---|---|
| **M0** | CleanStart, Compose (com a flag de A3), bootstrap do realm, health checks, CI com testes de arquitetura, **auditoria e observabilidade desde já** | (b) Clean Arch |
| **M1** | Tenant com Outbox, provisionamento idempotente (C3, C4), `202` + status, reconciliação, **tenant-admin inicial (C9)**, suspensão com efeito real (N6) | (c) consistência |
| **M2** | Convite com ciclo completo (N7), desativação com revogação (C8), exclusão LGPD, `RoleAssignmentPolicy`, vagas com regra explícita (N5, C5), downgrade rejeitado (N8), **suíte negativa incluindo sub-recursos (C2, C12)** | (d) isolamento |
| **M3** | `Client.AspNetCore` + `SampleResourceApi`, testes de formato de claim (§12.1 + o gêmeo de C1) | (a) OAuth2/OIDC |
| **M7'** | Step-up, rate limiting, README com a arquitetura e os limites honestos | fecha (a) |

**Mover auditoria e OpenTelemetry de M7 para M0/M1** — são transversais; retrofitá-los depois custa mais.

**Cortar primeiro:** sincronização por polling (ADR-007 — pior custo por unidade de prova, e agravada por C7 e C14);
CRUD de Permission Sets (M5 — o desenho prova, o CRUD só executa); clients M2M com `private_key_jwt` (M6 — mais
configuração de Keycloak que arquitetura).

**Nunca cortar:** os testes negativos de autorização parametrizados por rota e os testes de formato de claim. É o que
separa o repositório de "mais um CRUD sobre Keycloak".

---

## Decisões que travam o início

Respostas que só o autor pode dar, na ordem em que bloqueiam:

1. **Tenant-admin inicial: dentro do `POST /tenants` ou exceção de platform-admin?** (C9) — trava o M1.
2. **`Invited` ocupa vaga?** (N5) — trava o modelo de domínio e os testes que abrem o M1.
3. **Suspensão desabilita os usuários no Keycloak?** (N6) — se sim, `Member` precisa guardar o estado pré-suspensão;
   decidir antes de modelar o aggregate.
4. **Tenant tem estado terminal na v1?** (C10) — se ficar de fora, vai para a §19 como limite declarado, não ausente.
5. **A federação (M4) entra?** Sem ela, o fluxo 9.4 e metade do ADR-007 saem junto, e o discovery vira um endpoint
   barato que resolve tenant por domínio.
6. **Quanto tempo há disponível?** Abaixo de ~8 semanas em tempo parcial, adotar o corte mínimo.
7. **Qual o cenário de demonstração?** README com `curl` reproduzível, vídeo ou conversa ao vivo — muda o que vale
   a pena implementar.

---

## Nota sobre os dois documentos

A rastreabilidade entre eles é um **ativo do portfólio**, não ruído:

- *"Realms dedicados **ou** Groups/Organizations"* (origem §2.1) → **ADR-001**
- *"Protocol Mappers **ou** enriquecimento customizado na Gateway"* (origem §2.3) → **ADR-004**
- O `RequireClaim("roles", "tenant-admin")` do exemplo da origem §4.1 é **o próprio caso que a §12.1 da v2.0 disseca**

A sequência *escolha em aberto → decisão registrada → armadilha explicada* vale ser apontada no README, em vez de
deixar o documento de origem como arquivo solto.

---

## Contradições internas da spec

Afirmações que o **próprio documento desmente** em outro ponto. Não são erros técnicos — são inconsistências entre
o que a spec promete e o que ela mesma especifica depois. C6 (§2.1 vs §9.6) é a primeira da série e está acima,
entre os críticos.

### CI-1. A regra de isolamento vale para tenant-admin e não vale para M2M

**§17, anti-pattern 7:** *"Todo acesso a recurso de um tenant compara o tenant da rota com o do token"* · **§10.1:**
o `SameTenantRequirement` é amarrado apenas à família `TenantAdmin`.
**§8:** `GET .../effective-permissions` → *"tenant-admin; **clients M2M com escopo `gateway.permissions.read`**"*.

Um token de Client Credentials de uma Resource API não tem por que carregar `tenant_id` — ela serve todos os
tenants, premissa do ADR-002 e do fluxo 9.6. O dilema é fechado: ou o requirement roda e **nenhuma** Resource API
lê permissões (o fluxo 9.6 morre no passo 3), ou não roda e **qualquer** client M2M com o escopo lê permissões de
qualquer usuário de qualquer tenant, bastando informar o `{tenantId}` na rota.

Distinto de C2: lá o vetor é o sub-recurso de outro tenant dentro de uma rota cujo tenant confere; aqui o próprio
par (rota, token) é irrestrito, porque não há tenant no token com que comparar.

**Cede a §8.** O escopo precisa de modelo de confiança próprio: client M2M vinculado a um tenant, ou client de
plataforma com escopo global tratado como credencial de infraestrutura, com auditoria por chamada e rotação.
**Buraco de desenho** — é a única rota da §8 que expõe governança de todos os tenants sem defesa especificada.

### CI-2. O segredo que "fica apenas no Keycloak" é guardado por 24h pela Gateway

**§17, anti-pattern 3:** segredos M2M *"são devolvidos uma única vez e ficam apenas no Keycloak"*.
**§10.3:** *"POSTs com `Idempotency-Key` **guardam a resposta** por 24 horas"*.

`POST .../clients` e `rotate-credentials` são POSTs de escrita cujo corpo de resposta **é o `client_secret`** (§9.3).
O store de idempotência persiste o corpo; o corpo é a credencial. A Gateway passa a guardar segredos em claro por
24h — o que o anti-pattern 3 proíbe e de que o argumento de compliance do ADR-003 depende.

Dois desdobramentos: *"devolvido uma única vez"* deixa de ser verdade (replay com a mesma chave dentro de 24h
devolve o segredo — e a `Idempotency-Key` é **escolhida pelo cliente**, logo circula em logs de proxy e coleções de
API); e cruza com X12 — como a spec não diz onde o store vive, a discussão sobre o local acontecerá sem ninguém
lembrar que o conteúdo é credencial.

**Cede a §10.3.** Guardar apenas `(chave, status, Location)`; no replay de resposta com segredo, `409` ou `200` sem
o campo sensível. Escrever como regra: *nenhum valor de credencial entra no store de idempotência, na auditoria ou
no log*. Refinamento à parte: §2 e §5 dizem *"nunca vê uma senha"* — verdade para senha de usuário; a Gateway **vê
e transporta** credencial de client.

### CI-3. "Sem ponto único de falha" convive com Keycloak em instância única

**ADR-002:** *"sem ponto único de falha no login nem no tráfego de negócio"*.
**§1.1 / §19:** alta disponibilidade do Keycloak fora do escopo; *"o Keycloak roda em instância única"*.

O SPOF não foi eliminado, foi **deslocado**. Com o Keycloak fora, nenhum login acontece, nenhum token é renovado e,
decorridos os 5 minutos do access token, **o Data Plane inteiro para** — a validação local depende de token vivo,
não de Keycloak vivo. O realm único (ADR-001) ainda concentra o raio de impacto.

**Cede o ADR-002:** *"sem ponto único de falha **adicional**; o Keycloak permanece dependência crítica de ambos"*.
Acrescentar à §19 a janela real — hoje o número dos 5 minutos não está escrito como limite em lugar nenhum.

### CI-4. Step-up: a §5 diz que a Gateway documenta; a §10.1 diz que ela exige

**§5:** IdentityGateway *"**Documenta** os níveis exigidos por operação sensível"*.
**§10.1:** operações destrutivas *"**exigem step-up**: o token precisa ter `acr` de nível elevado"*.

Documentar e verificar são responsabilidades diferentes; a §10.1 e o M7 descrevem verificação, logo a linha da §5
está errada, não incompleta. A consequência é que **o mecanismo não aparece em lugar nenhum**: a §10.1 cita três
famílias de policy e nenhuma é de `acr`; a §12 não valida `acr`; a §13 não tem teste; a §18 não exige.

O ponto mais delicado ficou sem especificação: **o que fazer quando o token não tem o `acr` exigido**. A resposta
certa não é 403 — é instruir o cliente a refazer o login com `acr_values` (equivalente OIDC do
`insufficient_user_authentication`), senão o usuário fica sem caminho de recuperação.

**Cede a §5** (*"Define e **verifica**"*), mais uma quarta família de policy (`StepUpRequirement`) e o contrato de
resposta na §10.1. **Buraco de desenho:** o M7 é hoje uma linha de roadmap sem especificação atrás.

### CI-5. Princípio "fail closed" vs. fluxo 9.4

**§3, princípio 4:** *"Fail closed. Na dúvida […] a resposta é negar."* · **§6.1, invariante:** *"o número de
membros ativos nunca excede `Plan.MaxUsers`"*.
**§9.4, passo 4:** *"Se o limite do plano foi atingido, **o membro é registrado**"*.

Não há dúvida nenhuma: a regra é conhecida, a invariante é explícita e o sistema escolhe violá-la. Em §11.1
`ReserveSeat()` devolve `SeatLimitReached` — retorno que o fluxo 9.4 ignora.

**Cedem o princípio 4 e a §6.1, não a §9.4** — a §9.4 está tecnicamente certa: o usuário já foi autenticado pelo
IdP do cliente, e recusar o registro criaria usuário sem membership, pior que o problema. Reescrever a invariante
como *"nenhuma operação **da API** eleva `ActiveMembers` acima do limite; a absorção externa pode ultrapassar e
marca o tenant como `OverSubscribed`"*. **Uma invariante que o sistema sabe violar precisa ser reescrita, senão o
primeiro teste de domínio sério a derruba.**

### CI-6. "Nenhuma falha deixa estado órfão sem ser detectada" vs. a reconciliação que só olha `Active`

**ADR-006:** *"nenhuma falha do Keycloak deixa estado órfão **sem ser detectada**"*.
**§9.1:** o job compara *"os tenants **`Active`** com as Organizations existentes"*.

O órfão que o ADR-006 existe para cobrir nasce **antes** de o tenant virar `Active`: Organization criada, gravação
do id falhou (a própria §11.6 descreve esse caso). Esse tenant fica em `Pending`/`ProvisioningFailed` — e a
reconciliação, por definição, não o examina. **O caso 100% coberto é aquele em que nada deu errado.**

O filtro por `Active` é também o que torna X10 insolúvel mesmo com reconciliação bidirecional: Organization órfã
cujo tenant está em `ProvisioningFailed` fica invisível dos dois lados.

**Cede a §9.1:** varrer `Pending`, `ProvisioningFailed` **e** `Active`, e no sentido inverso Organizations sem
tenant correspondente.

### CI-7. ADR-004 diz "exclusivamente Protocol Mappers"; a §12.1 entrega transformação no .NET

**ADR-004 / §17 anti-pattern 5:** *"Claims vêm **somente** de Protocol Mappers"*.
**§12.1, Solução B:** o `Client.AspNetCore` registra `KeycloakRealmRolesTransformation` **incondicionalmente**.

A transformação sintetiza claims `roles` que não estavam no token, em todas as APIs consumidoras, por padrão. A
defesa "o anti-pattern fala da Gateway" não sustenta o advérbio: "somente" não admite escopo implícito.

**Contradição de escopo/redação, não buraco** — o código é defensivo (só age se o claim plano falta, clona o
principal, falha fechado em JSON malformado). **Cedem o ADR-004 e o anti-pattern 5:** *"nenhum claim é emitido ou
assinado fora do Keycloak; a derivação local é permitida no Data Plane apenas para achatar formato"*.
**Recomendação adicional:** tornar o registro **opt-in** — ligado por padrão, ele mascara a ausência do mapper e faz
o teste da §12.1 deixar de falhar quando alguém remove o scope. A rede de segurança desarma o alarme.

### CI-8. "Toda regra de isolamento tem um teste negativo" vs. o critério de pronto condicional

**§3, princípio 5:** *"**Toda** regra de isolamento tem um teste negativo correspondente."*
**§18:** *"teste negativo de autorização, **se a rota tiver `{tenantId}`**"*.

O princípio quantifica sobre **regras**; o critério quantifica sobre **rotas com um formato de template**. Toda
regra que não se manifeste como `{tenantId}` — vínculo sub-recurso↔tenant (C2), rota sem `{tenantId}` (C12), escopo
M2M (CI-1) — passa no critério de pronto sem teste negativo. **O gate que o projeto exibe como prova do princípio 5
é mais estreito que o princípio, e é o gate que decide o que entra.**

**Cede a §18:** *"teste negativo para cada regra de isolamento que a funcionalidade toca: tenant da rota,
pertencimento de cada sub-recurso ao tenant e escopo de client M2M"*. **Correção de uma linha com o maior alcance
de toda a revisão** — converte C2, C12 e CI-1 de achados em falhas automáticas de gate.

---

## O que a revisão NÃO conseguiu atacar

Seção deliberada: calibra o peso de todo o resto. Formato "tentei X, esperava Y, mas a spec previne em Z".

1. **ADR-003 / ROPC.** Procurei os três caminhos por onde uma senha encosta na Gateway: endpoint de "criar usuário
   com senha inicial", `POST /auth/login` de conveniência, e realm de teste com ROPC ligado para facilitar a suíte.
   Nenhum existe. O terceiro é o que importa — é por onde o ROPC volta na prática — e a spec o fecha explicitamente
   num comentário da §12.1: *"os testes usam Client Credentials; o ROPC continua desabilitado também no realm de
   teste"*. **Antecipar o atalho de teste é o que faz a decisão valer.**

2. **ADR-004 / claims por mapper.** Tentei provar que o login depende da Gateway em runtime — se `tier` ou
   `tenant_id` fossem resolvidos por mapper customizado consultando a Gateway, a queda dela derrubaria a emissão de
   token e o ADR-002 cairia junto. A consequência do ADR-004 fecha isso (*"nenhum mapper customizado consulta a
   Gateway durante a emissão"*). Pior caso: claim defasado até o refresh, e está escrito.
   *Ressalva:* C1 mostra que o **mecanismo escolhido** para `tenant_id` não funciona — defeito de implementação do
   ADR, não do ADR.

3. **ADR-002 / Data Plane.** Procurei introspection, `userinfo`, validação remota, consulta de papéis — qualquer
   chamada síncrona à Gateway por requisição. Nada. O único ponto em que ela entra no caminho é o cache frio, que é
   C6 e cujo raio já está delimitado. Não há um segundo ponto.

4. **Outbox como mecanismo (ADR-006).** ⚠️ **Leitura importante:** o `RegisterTenantHandler` (§11.4) não toca o
   Keycloak — INSERT + Outbox na mesma transação; o consumidor (§11.5) é fino; `MarkProvisioned` tem guarda de
   idempotência; o `catch (KeycloakConflictException)` prevê entrega duplicada concorrente. **O padrão está certo.**
   C3 e C4 atacam a *busca* de que ele depende e o *retry do HTTP*, não o Outbox. Quem ler C3/C4 isoladamente pode
   concluir que o ADR-006 está errado — não está.

5. **§12.1.** Verificada contra documentação oficial e código-fonte pelo coordenador; saiu ilesa. O ângulo restante
   era a Solução B como porta para claims forjados: ela lê `realm_access` **do token já validado**, nunca do
   request, só age quando o claim plano falta, clona o principal e nega papéis em `JsonException`. Sem entrada. O
   que apareceu foi de escopo, e virou CI-7.

6. **`RoleAssignmentPolicy` (§11.2).** Quatro escalações tentadas, quatro barradas: pedir `platform-admin`
   (checagem explícita); atribuir acima do teto (`aboveCeiling`, que devolve **a lista** dos recusados, melhor que
   negar em bloco); atribuir em outro tenant (barrado antes, e a ordem das checagens está certa — cross-tenant
   primeiro); lista vazia (sem efeito colateral).
   Dois resíduos que **não** são achado: atribuir papel igual ao próprio é delegação normal; e `PUT .../roles`
   permite lockout intra-tenant, fora do modelo de ameaça declarado.
   **Não verificável pelo documento:** de onde vem `actor.HighestRole`. Se derivar do claim `roles`, a corretude da
   policy depende de C1 e C11 estarem resolvidos — *a regra é sólida, sua entrada ainda não é*.

7. **Superfície anônima.** A §8 expõe exatamente duas rotas não autenticadas: `POST /auth/discovery` e os health
   checks. Não há signup nem verificação de e-mail exposta — a superfície de registro, onde nasce a maior parte dos
   abusos de plataforma multi-tenant, não existe. E o discovery já prevê o ataque óbvio: §9.2 dá **resposta de
   formato idêntico** para domínio desconhecido, mais rate limit restrito. *Não avaliado:* timing e diferença de
   payload dependem de implementação.

8. **§19, rigor em declarar limites.** Teste inverso: procurar limite **real e não declarado**, que é o que faz um
   avaliador desconfiar do documento inteiro. Os seis itens são custos verdadeiros e nenhum é maquiado — o de
   políticas de senha por realm, em particular, é o que a maioria das specs esconde. As omissões encontradas (janela
   de 5 min em CI-3, desativação em C8, cache frio em C6, perda de eventos em C7) são todas **consequências de
   decisões que a §19 já assume**. **A seção está incompleta, não desonesta** — nada sugere que a spec esconda custo.

**Calibração:** dos oito alvos, sete resistiram inteiros e um (`RoleAssignmentPolicy`) resistiu com dependência de
entrada não verificável pelo documento. Onde a spec cai, cai por **assumir comportamento do Keycloak** e por
afirmações categóricas em §2.1, ADR-002 e ADR-006 que as seções posteriores não sustentam. **Não há erro de
raciocínio arquitetural:** as oito contradições se corrigem dentro do desenho atual, sem revogar nenhum ADR, e cinco
delas (CI-2, CI-3, CI-5, CI-7, CI-8) são alterações de texto e de um gate.

---

## Pendências remanescentes

Do **revisor de arquitetura** (frente encerrada antes de fechar):

- Fim do achado A9 (`private_key_jwt` — mecanismo .NET não especificado) e eventuais achados adicionais.
- **Lacunas da arquitetura** não sistematizadas: versionamento de API além do path, contrato e evolução dos eventos
  de integração, tecnologia de cache e onde vive, cold start do `Client.AspNetCore`, secrets management concreto.
  *(parcialmente coberto por C14, X9 e X12)*

**A confirmar na documentação:**

- Se `searchQuery` filtra atributos de Organization — base da correção de C3.
- Se o toggle de Organizations por realm é necessário além da feature flag de build (A3).
