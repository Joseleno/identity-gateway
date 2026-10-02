using System.Security.Claims;
using IdentityGateway.Api.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityGateway.Api.FunctionalTests.Autorizacao;

/// <summary>
/// Monta a autorização da Gateway fora do HTTP: o mesmo <c>AddAutorizacaoDaGateway</c> da produção, num contêiner só
/// com ele.
/// </summary>
/// <remarks>
/// Sem host, sem banco e sem container: estes testes rodam em milissegundos e exercitam o que o teste por HTTP não
/// distingue — por HTTP, um requirement que só deixa de aprovar e um que veta respondem o mesmo <c>403</c>.
/// </remarks>
internal static class MontagemDaAutorizacao
{
    /// <summary>O serviço de autorização, com as policies e a ordem de handlers da produção.</summary>
    /// <param name="ajustar">O que o teste acrescenta ao contêiner <b>depois</b> do registro da produção.</param>
    public static ServiceProvider Montar(Action<IServiceCollection>? ajustar = null)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddAutorizacaoDaGateway();
        ajustar?.Invoke(services);

        return services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>Um usuário autenticado com os claims que o token do Keycloak traria.</summary>
    public static ClaimsPrincipal Usuario(
        IEnumerable<string> roles, IEnumerable<string> tenantIds, string? sub = "5f1c0a2e-7c1d-4a55-9b0e-2f6f3c9d1a10")
    {
        List<Claim> claims = [.. roles.Select(role => new Claim("roles", role))];
        claims.AddRange(tenantIds.Select(tenantId => new Claim("tenant_id", tenantId)));

        if (sub is not null)
        {
            claims.Add(new Claim("sub", sub));
        }

        return new ClaimsPrincipal(
            new ClaimsIdentity(claims, authenticationType: "teste", nameType: "sub", roleType: "roles"));
    }

    /// <summary>O <c>HttpContext</c> de um pedido a uma rota com <c>{tenantId}</c>, como o roteamento o entrega.</summary>
    /// <param name="tenantIdDaRota">O texto cru do parâmetro, ou nulo para uma rota sem ele.</param>
    public static DefaultHttpContext Pedido(string? tenantIdDaRota)
    {
        DefaultHttpContext contexto = new();

        if (tenantIdDaRota is not null)
        {
            contexto.Request.RouteValues[SameTenantRequirement.ParametroDaRota] = tenantIdDaRota;
        }

        return contexto;
    }
}

/// <summary>
/// Um handler que aprova todo requirement ainda pendente — o pior vizinho que um requirement pode ter.
/// </summary>
/// <remarks>
/// É o que torna o <c>Fail()</c> observável: com ele no contêiner, um requirement que só deixasse de dar
/// <c>Succeed</c> seria satisfeito aqui, e a policy passaria. Só o <c>Fail()</c> sobrevive a ele.
/// </remarks>
internal sealed class HandlerQueAprovaTudo : IAuthorizationHandler
{
    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        foreach (IAuthorizationRequirement requirement in context.PendingRequirements.ToList())
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
