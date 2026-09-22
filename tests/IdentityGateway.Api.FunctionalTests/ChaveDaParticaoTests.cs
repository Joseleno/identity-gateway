using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace IdentityGateway.Api.FunctionalTests;

/// <summary>
/// Prova que o limitador de requisições particiona por usuário, e não por IP, quando há token.
/// </summary>
/// <remarks>
/// <para>
/// <b>É o segundo efeito colateral de <c>MapInboundClaims = false</c></b>, e escapou da primeira varredura.
/// Com o remapeamento desligado, o <c>sub</c> não vira <see cref="ClaimTypes.NameIdentifier"/>: lendo só a
/// forma longa, <b>todo</b> usuário autenticado cairia na partição por IP, e os que estivessem atrás do mesmo
/// NAT dividiriam uma cota só — um cliente abusivo consumindo a cota dos outros, que é exatamente o que
/// particionar por usuário existe para evitar.
/// </para>
/// <para>
/// Nenhum teste de limitação existia, então o defeito passaria calado. A regra foi extraída da lambda de
/// registro justamente para poder ser exercitada aqui.
/// </para>
/// </remarks>
public sealed class ChaveDaParticaoTests
{
    private static DefaultHttpContext ComClaim(string tipo, string valor)
    {
        DefaultHttpContext contexto = new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(tipo, valor)], "Bearer")),
        };

        contexto.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");

        return contexto;
    }

    [Fact]
    public void ComOClaimCurto_ParticionaPeloUsuario()
    {
        // A forma que chega hoje. Era este o caso que caía no IP antes da correção.
        var usuario = Guid.CreateVersion7();

        string chave = DependencyInjection.ChaveDaParticao(ComClaim(JwtRegisteredClaimNames.Sub, usuario.ToString()));

        chave.Should().Be(usuario.ToString());
    }

    [Fact]
    public void ComOClaimLongo_ParticionaPeloUsuario()
    {
        var usuario = Guid.CreateVersion7();

        string chave = DependencyInjection.ChaveDaParticao(ComClaim(ClaimTypes.NameIdentifier, usuario.ToString()));

        chave.Should().Be(usuario.ToString());
    }

    [Fact]
    public void SemToken_ParticionaPeloIp()
    {
        // Quem ainda não se autenticou é particionado por IP, que é o melhor identificador antes do login.
        DefaultHttpContext anonimo = new();
        anonimo.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");

        string chave = DependencyInjection.ChaveDaParticao(anonimo);

        chave.Should().Be("203.0.113.7");
    }

    [Fact]
    public void SemTokenESemIp_TemChaveDeUltimoRecurso()
    {
        // Sem identificador nenhum, todos dividem uma cota — mas o limitador continua funcionando, que é
        // melhor que estourar ao montar a partição.
        string chave = DependencyInjection.ChaveDaParticao(new DefaultHttpContext());

        chave.Should().Be("desconhecido");
    }
}
