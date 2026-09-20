# Testes de arquitetura

As regras de arquitetura do projeto como teste executável. Quando um destes reprova, a resposta é **mover o código** —
nunca afrouxar o teste.

A mensagem de falha nomeia os tipos violadores (ver `ArchTestResultExtensions`): `IsSuccessful.Should().BeTrue()`
diria apenas "expected true, found false", que informa que a arquitetura foi violada mas não onde.

## Implementadas

| # | Regra | Teste |
|---|---|---|
| 1 | Domain não depende de Application, Infrastructure nem Api | `Domain_NaoDependeDeNenhumaOutraCamada` |
| 2 | Domain não referencia EF Core | `Domain_NaoReferenciaEfCore` |
| 3 | Application não referencia Infrastructure nem Api | `Application_NaoDependeDeInfrastructureNemDeApi` |
| 3 | Application não referencia EF Core | `Application_NaoReferenciaEfCore` |
| 4 | Infrastructure não referencia Api | `Infrastructure_NaoDependeDeApi` |
| — | Fora de `Common/Messaging` e `Common/Behaviors`, nada referencia `Mediator` | `ForaDosMarcadores_NadaReferenciaOMediator...` |

## Em `Skip` até o primeiro agregado

| # | Regra | Teste |
|---|---|---|
| 7 | Entidade não expõe setter público | `Entidades_NaoExpoemSetterPublico` |
| — | Raiz de agregado expõe coleção somente leitura | `RaizesDeAgregado_ExpoemColecoesSomenteLeitura` |
| 5 | Handlers são `sealed` | `Handlers_SaoSealed` |
| 6 | Commands e queries são `record` | `CommandsEQueries_SaoRecord` |

As quatro **existem e estão escritas**; o que falta é o que inspecionar. Cada uma abre afirmando
`NotBeEmpty` — sem agregado nem handler no assembly, a regra passaria varrendo zero tipos, e um verde
vazio é pior que um vermelho: afirma que a arquitetura foi verificada quando nada foi. A guarda dispara, e
por isso estão em `Skip` com o motivo no atributo, em vez de vermelhas permanentes que ninguém mais lê.

**Reativam-se removendo o `Skip`** quando o primeiro agregado do M0 entrar — é o teste de que a fundação
está sendo usada, não só presente.

Cada regra ativa foi verificada **reprovando**, não só passando: introduzir `DbContext` no Domain faz a
regra 2 falhar nomeando o tipo. A falha é específica, não em bloco.

As regras de domínio usam reflexão direta (`RegrasDeDominioTests`), não o NetArchTest: a pergunta é sobre o
**membro** ("esta propriedade tem setter público?") e a API do NetArchTest opera sobre tipos. Ambas começam
afirmando que encontraram algo para inspecionar — sem isso, um namespace renomeado faria o teste passar
vazio.

`private set` e `init` são permitidos de propósito: o primeiro é como o método de domínio atribui, o segundo só
atua na construção. O que a regra proíbe é o setter **acessível de fora**.

A regra de mensageria **ignora código gerado** (`CompilerGeneratedAttribute`, `GeneratedCodeAttribute` e o
namespace `Mediator` dentro do assembly). O source generator do Mediator emite o pipeline inteiro na Application,
e ele referencia o namespace dele em toda assinatura porque é o trabalho dele — incluí-lo faria a regra acusar
~40 violações que ninguém escreveu e ninguém pode corrigir.

## O guard contra teste vazio

Toda regra que varre tipos começa afirmando que **encontrou algo para inspecionar**
(`handlers.Should().NotBeEmpty()`). Sem isso, um namespace renomeado ou uma base trocada fariam o teste passar
sem verificar nada, e ninguém perceberia — é o verde vacuoso que esta suíte existe para evitar.

**O guard está trabalhando agora.** As quatro regras da seção anterior estão em `Skip` exatamente porque ele
reprovou: o domínio ainda não tem agregado nem handler, e sem isso elas varreriam zero tipos. É o
comportamento desejado — a regra se recusa a dar um verde que não significaria nada. Saem do `Skip` quando o
primeiro agregado do M0 entrar.

Nota para quem escrever regra sobre `record`: ele não tem marca própria em metadados. O sinal confiável é o
método sintetizado `<Clone>$`, que o compilador gera para todo record e para nada mais.