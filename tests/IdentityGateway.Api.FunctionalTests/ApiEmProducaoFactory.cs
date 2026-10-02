using System.Security.Cryptography;
using IdentityGateway.Api.FunctionalTests.Logs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// A Api com o ambiente <c>Production</c>, parametrizada pela configuração de cada caso.
/// </summary>
/// <remarks>
/// <para>
/// <b>Existe porque toda a suíte roda em Development.</b> Os ramos que só valem fora dele — a recusa do client de
/// demonstração, o <c>https</c> obrigatório nos metadados, a lista de clients vazia — nunca seriam exercitados, e um
/// "só em Development" garantido só pelo arquivo certo é verde vacuoso.
/// </para>
/// <para>
/// <b>Sem containers.</b> Os testes daqui terminam na subida ou na autenticação, antes de qualquer acesso ao banco: a
/// connection string aponta para a porta de descarte, e o despachante do Outbox fica desligado.
/// </para>
/// <para>
/// <b>Uma exceção declarada à regra "só configuração":</b> quando o caso usa o OIDC falso em HTTPS, o handler do canal
/// de metadados é trocado por um que confia <b>exatamente</b> no certificado autoassinado dele. Não toca nas opções
/// que os testes conferem (<c>RequireHttpsMetadata</c>, o prazo, a validação).
/// </para>
/// </remarks>
internal sealed class ApiEmProducaoFactory(
    IReadOnlyDictionary<string, string?> configuracao, HttpMessageHandler? metadados = null)
    : WebApplicationFactory<Program>
{
    private static readonly string ChaveFicticia = GerarChave();

    private static readonly Dictionary<string, string?> SecaoDoColetor = new() { ["Serilog:WriteTo:9"] = null };

    private readonly string _canalDeLogs = Guid.NewGuid().ToString("N");

    /// <summary>O que esta Api registrou em log.</summary>
    public ColetorDeLogsDaApi Logs => ColetorDeLogsDaApi.DoCanal(_canalDeLogs);

    /// <summary>A configuração mínima de um caso: os endereços do provedor e o que mais ele precisar.</summary>
    public static IReadOnlyDictionary<string, string?> Configuracao(
        string baseUrl, string? publicBaseUrl, params (string Chave, string? Valor)[] extras)
    {
        Dictionary<string, string?> valores = new() { ["Keycloak:Admin:BaseUrl"] = baseUrl };

        if (publicBaseUrl is not null)
        {
            valores["Keycloak:Admin:PublicBaseUrl"] = publicBaseUrl;
        }

        foreach ((string chave, string? valor) in extras)
        {
            valores[chave] = valor;
        }

        return valores;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Production");

        // O que a subida exige e nenhum caso exercita: banco (nunca alcançado) e a chave do service account.
        builder.UseSetting("Database:ConnectionString", "Host=127.0.0.1;Port=9;Database=x;Username=u;Password=p");
        builder.UseSetting("Outbox:Enabled", "false");
        builder.UseSetting("Keycloak:Admin:PrivateKeyPem", ChaveFicticia);

        builder.UseSetting("Serilog:Using:0", typeof(ColetorDeLogsDaApi).Assembly.GetName().Name);
        builder.UseSetting("Serilog:WriteTo:9:Name", nameof(ColetorDeLogsDaApiExtensions.ColetorEmMemoria));
        builder.UseSetting("Serilog:WriteTo:9:Args:canal", _canalDeLogs);

        // A mesma máscara da IdentityGatewayApiFactory, pelo mesmo motivo: as UseSetting chegam ao Program.cs como
        // argumentos de linha de comando, a seção intermediária vem com valor vazio, e o Serilog a leria como nome de
        // sink — descartando o coletor em silêncio.
        builder.ConfigureAppConfiguration((_, appConfiguration) => appConfiguration.AddInMemoryCollection(SecaoDoColetor));

        foreach ((string chave, string? valor) in configuracao)
        {
            builder.UseSetting(chave, valor);
        }

        if (metadados is not null)
        {
            // Configure, e não PostConfigure: o canal de metadados é montado no PostConfigure do JwtBearer, a partir
            // do handler que encontrar.
            builder.ConfigureTestServices(services => services.Configure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme, jwt => jwt.BackchannelHttpHandler = metadados));
        }
    }

    private static string GerarChave()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }
}
