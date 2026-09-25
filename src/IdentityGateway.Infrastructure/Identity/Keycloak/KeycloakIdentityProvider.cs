using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Tenants;
using Microsoft.Extensions.Logging;

namespace IdentityGateway.Infrastructure.Identity.Keycloak;

/// <summary>
/// Implementação de <see cref="IIdentityProvider"/> sobre o Keycloak.
/// </summary>
/// <remarks>
/// <para>
/// <b>Consulta antes de criar</b>, pelo atributo <see cref="AtributoDoTenant"/>. Uma tentativa anterior pode ter
/// criado a Organization e falhado antes de gravar o id no banco da Gateway: sem a consulta, a repetição da mensagem
/// criaria outra.
/// </para>
/// <para>
/// <b>Transient</b>, como o cliente tipado que envolve. Não depende de <c>DbContext</c>, e não há motivo para viver o
/// escopo inteiro.
/// </para>
/// </remarks>
internal sealed class KeycloakIdentityProvider(
    KeycloakAdminClient admin,
    ILogger<KeycloakIdentityProvider> logger) : IIdentityProvider
{
    /// <summary>
    /// Atributo que correlaciona a Organization ao tenant: estável, escolhido por nós, imune a rename — e o que
    /// distingue a nossa Organization de outra com o mesmo alias criada fora da Gateway.
    /// </summary>
    internal const string AtributoDoTenant = "gateway_tenant_id";

    public async Task<string> EnsureOrganizationAsync(
        TenantId tenantId, TenantSlug slug, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string valor = tenantId.Value.ToString();

        OrganizationRepresentation? existente =
            await admin.FindOrganizationByAttributeAsync(AtributoDoTenant, valor, cancellationToken);

        if (existente?.Id is { } idExistente)
        {
            KeycloakLogs.OrganizacaoJaExistia(logger, tenantId.Value);
            return idExistente;
        }

        try
        {
            string id = await admin.CreateOrganizationAsync(
                new OrganizationRepresentation(
                    Id: null,
                    Name: slug.Value,
                    Alias: slug.Value,
                    Description: name,
                    Enabled: true,
                    Attributes: new() { [AtributoDoTenant] = [valor] }),
                cancellationToken);

            KeycloakLogs.OrganizacaoCriada(logger, tenantId.Value);
            return id;
        }
        catch (KeycloakConflictException)
        {
            OrganizationRepresentation? vencedora =
                await admin.FindOrganizationByAttributeAsync(AtributoDoTenant, valor, cancellationToken);

            if (vencedora?.Id is { } idVencedora)
            {
                // Duas entregas da mesma mensagem concorreram, e a outra criou primeiro.
                KeycloakLogs.CorridaResolvida(logger, tenantId.Value);
                return idVencedora;
            }

            // O 409 é de uma Organization que NÃO é deste tenant. Repetir não resolve.
            KeycloakLogs.ConflitoNaoCorrelacionado(logger, tenantId.Value);

            throw new IdentityProviderInconsistencyException(
                $"Alias '{slug.Value}' em uso por Organization não correlacionada ao tenant {tenantId.Value}.");
        }
    }
}
