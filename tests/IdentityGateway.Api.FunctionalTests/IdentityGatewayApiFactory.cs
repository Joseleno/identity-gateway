using System.Net.Http.Headers;
using System.Security.Cryptography;
using IdentityGateway.Api.FunctionalTests.Logs;
using IdentityGateway.Api.FunctionalTests.Oidc;
using IdentityGateway.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// Sobe a Api inteira em memória, com PostgreSQL e Redis em container e um provedor OIDC falso em loopback.
/// </summary>
/// <remarks>
/// <para>
/// <b>A Api de verdade, não uma montagem de teste.</b> O <c>WebApplicationFactory</c> executa o <c>Program.cs</c>
/// real — os mesmos middlewares, o mesmo pipeline de behaviors, a mesma DI. O que se substitui é só a
/// configuração: as connection strings apontam para os containers, e o <c>Keycloak:Admin:BaseUrl</c>, para o OIDC
/// falso.
/// </para>
/// <para>
/// <b>A autenticação é a de produção.</b> O JwtBearer busca os metadados e as chaves no OIDC falso, por HTTP, como
/// buscaria no Keycloak; os tokens dos testes são assinados com a chave que esse servidor publica. Nenhum esquema de
/// autenticação é trocado e nenhuma chave é posta nas opções — um teste aqui exercita emissor, audiência, prazo,
/// algoritmo e assinatura de verdade.
/// </para>
/// <para>
/// <b>O emissor que a Api aceita é diferente do que o discovery anuncia</b> (<see cref="EmissorPublico"/> contra o
/// endereço do OIDC falso). É a mesma separação do compose — endereço público e de transporte —, e é o que deixa a
/// suíte negativa distinguir o emissor estrito da validação padrão da biblioteca.
/// </para>
/// <para>
/// <b>Sem depender do docker-compose de desenvolvimento</b>: os containers sobem e caem com a suíte, e nenhum serviço
/// local precisa estar rodando. Quem prova a Api contra o Keycloak real é a <c>ApiComKeycloakFactory</c>.
/// </para>
/// </remarks>
public sealed class IdentityGatewayApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>O <c>Keycloak:Admin:PublicBaseUrl</c> dos testes. Não resolve — e não precisa: nunca é discado.</summary>
    public const string EnderecoPublicoDoKeycloak = "http://keycloak.publico.test:8081";

    /// <summary>O único emissor que a Api sob teste aceita.</summary>
    public const string EmissorPublico = EnderecoPublicoDoKeycloak + "/realms/identity-gateway";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("identitygateway_functional")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

    private readonly string _canalDeLogs = Guid.NewGuid().ToString("N");

    private OidcFalso? _oidc;

    /// <summary>
    /// Chave fictícia do service account: a Api valida a configuração do Keycloak na subida, mas nenhum teste
    /// funcional obtém o token do service account. Gerada em memória — nunca um <c>.pem</c> versionado.
    /// </summary>
    private static readonly string ChaveFicticia = GerarChave();

    private static string GerarChave()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }

    /// <summary>Quem assina os tokens dos testes, com a chave que o OIDC falso publica.</summary>
    internal EmissorDeTeste Emissor { get; } = new(EmissorPublico);

    /// <summary>O provedor OIDC falso para o qual a Api aponta.</summary>
    internal OidcFalso Oidc =>
        _oidc ?? throw new InvalidOperationException("O OIDC falso só existe depois do InitializeAsync.");

    /// <summary>O que esta Api registrou em log, visto pelo teste.</summary>
    public ColetorDeLogsDaApi Logs => ColetorDeLogsDaApi.DoCanal(_canalDeLogs);

    public async ValueTask InitializeAsync()
    {
        // O OIDC falso sobe ANTES de qualquer acesso a Services ou CreateClient: o WebApplicationFactory aplica as
        // UseSetting de forma preguiçosa, e o BaseUrl (que leva a porta escolhida pelo sistema) precisa existir quando
        // ele as aplicar.
        _oidc = await OidcFalso.IniciarAsync(Emissor);

        // Em paralelo: são independentes, e subir em série dobra o tempo de arranque da suíte.
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

        // Aplica as migrations de verdade — é o mesmo caminho que a aplicação usa em produção. EnsureCreated
        // montaria o schema do modelo e passaria mesmo com a migration quebrada.
        using IServiceScope scope = Services.CreateScope();
        AppDbContext contexto = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await contexto.Database.MigrateAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());

        if (_oidc is not null)
        {
            await _oidc.DisposeAsync();
        }

        Emissor.Dispose();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");

        // Só a configuração é substituída, não o registro de serviços: trocar implementação aqui faria o teste
        // exercitar uma composição que não existe em produção.
        builder.UseSetting("Database:ConnectionString", _postgres.GetConnectionString());
        builder.UseSetting("Redis:ConnectionString", _redis.GetConnectionString());

        // O despachante do outbox fica desligado nos testes funcionais. Ele competiria com o teste pela mesma
        // tabela: um teste que confira a mensagem gerada por um caso de uso veria a limpeza de processadas
        // antigas apagá-la entre a requisição e a asserção — uma falha intermitente, dependente de tempo, que
        // apareceria na CI e não aqui. Quem exercita o despachante é o teste de integração, que o chama
        // diretamente. Note que isto continua sendo configuração, não troca de registro.
        builder.UseSetting("Outbox:Enabled", "false");

        // O "Keycloak" desta suíte é o OIDC falso: serve o discovery e o JWKS, e responde 404 ao resto. A validação
        // do token funciona de verdade; o token do service account não sai (o token endpoint não existe), e por isso
        // o /health/ready fica Unhealthy aqui — o ready com Keycloak é coberto pelos testes de integração, pela
        // coleção com Keycloak real e pelo job de compose da CI.
        builder.UseSetting("Keycloak:Admin:BaseUrl", Oidc.BaseUrl);

        // Diferente do BaseUrl de propósito: o emissor aceito sai daqui, e os metadados, do BaseUrl. O
        // AllowInsecureHttp e a lista de clients (identity-gateway-demo) vêm do appsettings.Development.json.
        builder.UseSetting("Keycloak:Admin:PublicBaseUrl", EnderecoPublicoDoKeycloak);
        builder.UseSetting("Keycloak:Admin:PrivateKeyPem", ChaveFicticia);

        // O coletor de logs em memória, pelo mesmo ReadFrom.Configuration do Program.cs. É configuração, não troca de
        // registro; o índice alto não colide com os sinks dos appsettings.
        builder.UseSetting("Serilog:Using:0", typeof(ColetorDeLogsDaApi).Assembly.GetName().Name);
        builder.UseSetting("Serilog:WriteTo:9:Name", nameof(ColetorDeLogsDaApiExtensions.ColetorEmMemoria));
        builder.UseSetting("Serilog:WriteTo:9:Args:canal", _canalDeLogs);

        // O WebApplicationFactory entrega as UseSetting ao Program.cs como argumentos de linha de comando, um por
        // chave — inclusive as seções intermediárias, com valor vazio ("--Serilog:WriteTo:9="). O Serilog lê um item
        // de WriteTo que tem valor de texto como o nome do sink, sem argumentos: com "" ali, ele não acha método
        // nenhum e descarta o coletor em silêncio (só o SelfLog diz). O nulo abaixo, num provedor acrescentado depois
        // da linha de comando, devolve à seção o valor que uma seção tem; o nome e o canal continuam vindo de cima.
        builder.ConfigureAppConfiguration((_, configuracao) => configuracao.AddInMemoryCollection(SecaoDoColetor));
    }

    private static readonly Dictionary<string, string?> SecaoDoColetor = new() { ["Serilog:WriteTo:9"] = null };

    /// <summary>
    /// Cria um cliente HTTP com um token válido no cabeçalho <c>Authorization</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Um token de verdade, validado de verdade</b> — e não um handler de autenticação falso, que faria os testes
    /// passarem sem nunca exercitar emissor, audiência, expiração e assinatura. O token tem a forma do que o client
    /// de demonstração do Keycloak emite (<see cref="EmissorDeTeste"/>).
    /// </para>
    /// </remarks>
    /// <param name="usuarioId">
    /// O usuário do token (o <c>sub</c>), ou nulo para gerar um. É este identificador que a auditoria grava em
    /// <c>CreatedBy</c>, então um teste que confira autoria precisa informá-lo.
    /// </param>
    /// <param name="roles">Os papéis do claim <c>roles</c>. Sem nenhum, o token sai sem o claim.</param>
    public HttpClient CreateClientAutenticado(Guid? usuarioId = null, params string[] roles)
    {
        HttpClient cliente = CreateClient();

        cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", Emissor.Emitir(usuarioId ?? Guid.CreateVersion7(), roles));

        return cliente;
    }

    /// <summary>
    /// Executa uma ação com um escopo de DI próprio.
    /// </summary>
    /// <remarks>
    /// Para preparar estado que não tem endpoint que o crie, e para conferir o que foi persistido: inserir ou
    /// ler por SQL cru deixaria o teste dependente do nome das colunas em vez do modelo.
    /// </remarks>
    public async Task ComEscopoAsync(Func<AppDbContext, Task> acao)
    {
        ArgumentNullException.ThrowIfNull(acao);

        using IServiceScope scope = Services.CreateScope();
        AppDbContext contexto = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await acao(contexto);
    }
}
