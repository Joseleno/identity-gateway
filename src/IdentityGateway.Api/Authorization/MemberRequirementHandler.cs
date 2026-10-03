using System.Security.Claims;
using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using Microsoft.AspNetCore.Authorization;

namespace IdentityGateway.Api.Authorization;

/// <summary>
/// Confere no banco se o <c>sub</c> do token é membro do tenant da rota, em <c>Invited</c> ou <c>Active</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Só consulta o banco para quem já passou nas camadas do token.</b> Se o contexto já falhou — papel ausente,
/// conta de plataforma, outro tenant —, sai sem consultar: o tempo de resposta não pode virar oráculo do vínculo entre
/// um <c>sub</c> e um tenant. Isso depende de este handler rodar <b>depois</b> dos outros três, e é o registro que
/// garante (ver <see cref="AutorizacaoDaGateway"/>).
/// </para>
/// <para>
/// <b>Pela porta <see cref="IMemberQueries"/>, e não pelo Mediator:</b> a Api só fala com o Mediator dentro dos
/// módulos. Os behaviors do pipeline não se aplicam a esta leitura.
/// </para>
/// <para>
/// <b><c>Invited</c> passa.</b> O aceite do convite acontece no Keycloak, e a Gateway ainda não o detecta: quem
/// concluiu o convite e entrou continua <c>Invited</c> aqui. Sai da lista quando o aceite for sincronizado.
/// </para>
/// </remarks>
internal sealed class MemberRequirementHandler(IMemberQueries members) : AuthorizationHandler<MemberRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, MemberRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        // Exatamente UM claim sub, como no tenant_id. O ClaimsPrincipal acha claims sem olhar a caixa do nome, e a
        // autenticação validou o "sub" do JSON: com um "SUB" ao lado, "o primeiro" poderia ser o que ninguém validou.
        if (context.HasFailed
            || context.Resource is not HttpContext http
            || !Guid.TryParse(http.GetRouteValue(SameTenantRequirement.ParametroDaRota)?.ToString(), out Guid tenantId)
            || context.User.FindAll("sub").Take(2).ToArray() is not [Claim { Value: var sub }]
            || string.IsNullOrWhiteSpace(sub))
        {
            context.Fail(new AuthorizationFailureReason(this, "A pertença não pôde ser verificada."));
            return;
        }

        // O sub vai como o Keycloak o emite, sem mudar a caixa (o ExternalUserId.From só apara as pontas). O
        // CancellationToken é o da requisição: o contexto de autorização não tem um.
        MemberStatus? status = await members.GetStatusAsync(
            new TenantId(tenantId), ExternalUserId.From(sub), http.RequestAborted);

        // Lista fechada: um estado que o enum ganhe depois nega, até alguém decidir.
        if (status is MemberStatus.Invited or MemberStatus.Active)
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(new AuthorizationFailureReason(this, "O ator não é membro do tenant."));
        }
    }
}
