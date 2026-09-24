using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using IdentityGateway.Api.Services;
using Microsoft.AspNetCore.Http;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// Prova que o usuário do token é identificado nas duas formas do claim de identidade.
/// </summary>
/// <remarks>
/// <para>
/// <b>Existe por causa de uma regressão que nada acusaria.</b> A validação do JWT passou a rodar com
/// <c>MapInboundClaims = false</c>, porque o remapeamento automático quebrava a policy <c>PlatformAdmin</c>:
/// o handler traduzia <c>roles</c> para a URI longa antes de a policy comparar, e o <c>403</c> vinha mesmo
/// com o token correto.
/// </para>
/// <para>
/// O efeito colateral é que o <c>sub</c> <b>também</b> deixa de virar
/// <see cref="ClaimTypes.NameIdentifier"/>. Lendo só a forma longa, o <c>HttpCurrentUser</c> devolvia nulo
/// para <b>todo</b> usuário autenticado — e a suíte inteira continuava verde, porque nenhuma entidade
/// implementa <c>IAuditable</c> ainda. O primeiro agregado auditável gravaria <c>CreatedBy</c> nulo, em
/// silêncio, e o rastro de autoria nasceria vazio.
/// </para>
/// <para>
/// É a mesma classe de defeito que a policy teve: código que parece certo e que nenhum teste exercita. Por
/// isso o teste vive aqui desde já, antes de existir a entidade auditável que dependeria dele.
/// </para>
/// </remarks>
public sealed class HttpCurrentUserTests
{
    private static HttpCurrentUser ComClaim(string tipo, string valor)
    {
        DefaultHttpContext contexto = new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(tipo, valor)], "Bearer")),
        };

        return new HttpCurrentUser(new HttpContextAccessor { HttpContext = contexto });
    }

    [Fact]
    public void ComOClaimCurto_IdentificaOUsuario()
    {
        // É esta a forma que chega hoje, com MapInboundClaims desligado. Era exatamente o caso que devolvia
        // nulo antes da correção.
        var esperado = Guid.CreateVersion7();

        HttpCurrentUser usuario = ComClaim(JwtRegisteredClaimNames.Sub, esperado.ToString());

        usuario.Id.Should().Be(esperado);
    }

    [Fact]
    public void ComOClaimLongo_IdentificaOUsuario()
    {
        // A forma longa continua coberta: um IdP externo pode emitir o claim já nela, e é o que valeria se o
        // remapeamento voltasse a ser ligado.
        var esperado = Guid.CreateVersion7();

        HttpCurrentUser usuario = ComClaim(ClaimTypes.NameIdentifier, esperado.ToString());

        usuario.Id.Should().Be(esperado);
    }

    [Fact]
    public void ComClaimMalformado_TrataComoAnonimo()
    {
        // Claim malformado é dado externo: derrubar a requisição por causa dele seria pior que tratar a
        // operação como anônima.
        HttpCurrentUser usuario = ComClaim(JwtRegisteredClaimNames.Sub, "nao-e-um-guid");

        usuario.Id.Should().BeNull();
    }

    [Fact]
    public void SemHttpContext_TrataComoAnonimo()
    {
        // Fora de uma requisição — um job, por exemplo — não há usuário, e isso é estado legítimo, não falha.
        HttpCurrentUser usuario = new(new HttpContextAccessor { HttpContext = null });

        usuario.Id.Should().BeNull();
        usuario.IsAuthenticated.Should().BeFalse();
    }
}
