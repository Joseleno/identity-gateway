using System.Reflection;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Tenants.GetTenant;
using IdentityGateway.Domain.ValueObjects;

namespace IdentityGateway.ArchitectureTests;

/// <summary>
/// Regras sobre o que as leituras expõem.
/// </summary>
public sealed class RegrasDeLeituraTests
{
    /// <summary>
    /// A leitura de um tenant não carrega e-mail: nem na projeção que sai do banco, nem na resposta da API.
    /// </summary>
    /// <remarks>
    /// O e-mail do admin inicial é dado pessoal e fica na linha do tenant só até a ativação. Um
    /// <c>InitialAdminEmail</c> acrescentado ao read model "para a listagem" passaria a sair em toda leitura. O teste
    /// olha o tipo e o nome de cada propriedade, inclusive as dos tipos aninhados.
    /// </remarks>
    [Fact]
    public void LeituraDeTenant_NaoCarregaEmail()
    {
        Type[] tipos = [typeof(TenantDetailsView), typeof(TenantDetailsResponse), typeof(TenantPlanResponse)];

        string[] comEmail =
        [
            .. tipos
                .SelectMany(tipo => tipo.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Select(propriedade => (Tipo: tipo, Propriedade: propriedade)))
                .Where(item => item.Propriedade.PropertyType == typeof(Email)
                    || item.Propriedade.Name.Contains("Email", StringComparison.OrdinalIgnoreCase)
                    || item.Propriedade.Name.Contains("Mail", StringComparison.OrdinalIgnoreCase))
                .Select(item => $"{item.Tipo.Name}.{item.Propriedade.Name}"),
        ];

        tipos.SelectMany(tipo => tipo.GetProperties()).Should().NotBeEmpty("sem propriedades, a regra passaria vazia");
        comEmail.Should().BeEmpty("o e-mail do admin inicial não é atributo do tenant para quem o lê");
    }
}
