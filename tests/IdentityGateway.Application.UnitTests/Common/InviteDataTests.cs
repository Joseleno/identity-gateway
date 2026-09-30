using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.ValueObjects;

namespace IdentityGateway.Application.UnitTests.Common;

public sealed class InviteDataTests
{
    [Fact]
    public void ToString_TemPapelEPrazoMasNaoOEmail()
    {
        // O ToString gerado do record imprimiria o e-mail (D15) — e o Email.ToString já não o devolve, mas o record
        // não pode depender disso para não vazar. Por isso o campo nem aparece: sem o NotContain("Email"), o ToString
        // gerado ("Email = Email(***)") também passaria, e o teste não provaria a sobrescrita.
        InviteData convite = new(Email.Of("segredo@acme.test").Value, RoleName.TenantAdmin, TimeSpan.FromDays(7));

        string texto = convite.ToString();

        texto.Should().NotContain("segredo").And.NotContain("Email")
            .And.Contain("tenant-admin").And.Contain("7.00:00:00");
    }
}
