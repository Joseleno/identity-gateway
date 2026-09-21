using System.Diagnostics.CodeAnalysis;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Tenants;
using IdentityGateway.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace IdentityGateway.Infrastructure.IntegrationTests.Configuration;

/// <summary>
/// Cobre a resolução de plano por código.
/// </summary>
/// <remarks>
/// O catálogo vem da configuração para que mudar um limite comercial seja editar o appsettings e reiniciar —
/// não uma migration nem um release de engenharia.
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1859:Use concrete types when possible for improved performance",
    Justification = "O teste exercita o catálogo PELA PORTA que a Application consome. Trocar por PlanCatalog "
        + "provaria a classe concreta e não o contrato — e é o contrato que o handler usa.")]
public sealed class PlanCatalogTests
{
    private static PlanCatalog Catalogo()
    {
        PlanOptions opcoes = new()
        {
            ["free"] = new PlanDefinition { Tier = PlanTier.Free, MaxUsers = 5, MaxClients = 1 },
            ["standard"] = new PlanDefinition { Tier = PlanTier.Standard, MaxUsers = 50, MaxClients = 5 },
        };

        return new PlanCatalog(Options.Create(opcoes));
    }

    [Fact]
    public void Find_ResolveOPlanoConhecido()
    {
        IPlanCatalog catalogo = Catalogo();

        Plan? plano = catalogo.Find("free");

        plano.Should().Be(new Plan(PlanTier.Free, 5, 1));
    }

    [Theory]
    [InlineData("FREE")]
    [InlineData("Free")]
    public void Find_IgnoraACaixa(string codigo)
    {
        // O codigo vem do corpo da requisicao: exigir a caixa exata transformaria 'Free' em 400 sem razao.
        IPlanCatalog catalogo = Catalogo();

        Plan? plano = catalogo.Find(codigo);

        plano.Should().Be(new Plan(PlanTier.Free, 5, 1));
    }

    [Fact]
    public void Find_DevolveNuloParaCodigoDesconhecido()
    {
        IPlanCatalog catalogo = Catalogo();

        Plan? plano = catalogo.Find("inexistente");

        plano.Should().BeNull();
    }
}
