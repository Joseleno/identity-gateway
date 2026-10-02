using IdentityGateway.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace IdentityGateway.Api.Authentication;

/// <summary>
/// Avisa, na subida, quando a lista de clients permitidos está vazia.
/// </summary>
/// <remarks>
/// Lista vazia é configuração válida e fechada: a API sobe e recusa todo token de usuário. Sem o aviso, o primeiro
/// sintoma seria um <c>401</c> em cada pedido, apontando para o token de quem chama — e o motivo estaria na
/// configuração de quem opera. Um <c>IHostedService</c>, e não uma linha no <c>Program.cs</c>, para rodar em toda
/// forma de subir o host, inclusive nos testes.
/// </remarks>
internal sealed class AvisoDeClientsPermitidos(
    IOptions<AccessTokenValidationOptions> validacao, ILoggerFactory loggers) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (validacao.Value.AllowedClients.Count == 0)
        {
            AutenticacaoLogs.NenhumClientPermitido(loggers.CreateLogger(ValidacaoDoAccessToken.Categoria));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
