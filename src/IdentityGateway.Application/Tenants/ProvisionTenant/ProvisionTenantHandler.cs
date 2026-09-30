using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Common.Messaging;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using Microsoft.Extensions.Logging;

namespace IdentityGateway.Application.Tenants.ProvisionTenant;

/// <summary>
/// Provisiona o tenant: garante a Organization, convida o admin inicial e ativa — ou marca <c>ProvisioningFailed</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Contrato com quem despacha.</b> Falha transitória dentro da janela sai como a exceção original, e quem
/// despacha repete. Todo desfecho terminal — ativo, falha permanente, janela esgotada, mensagem repetida, tenant
/// inexistente — é <see cref="Result.Success()"/>, e a mensagem sai da fila. O commit é do <c>TransactionBehavior</c>.
/// </para>
/// <para>
/// <b>O que repetir não corrige é recusado antes de tocar o Keycloak:</b> tenant sem o e-mail do admin (registrado
/// antes da fatia C) e tenant cujo plano não comporta o admin (D14). Deixar a falta de vaga para
/// <c>CompleteProvisioning</c> faria cada retry reenviar o convite até esgotar a janela.
/// </para>
/// <para>
/// <b>O mesmo <c>try</c> cobre as duas chamadas ao provedor.</b> A classificação é uma só: inconsistência vai direto a
/// <c>ProvisioningFailed</c>; qualquer outro erro, com a janela esgotada e sem cancelamento, também; senão, a exceção
/// sobe. Um convite que falhou deixa o tenant em <c>Pending</c>, com o e-mail, para a próxima entrega.
/// </para>
/// <para>
/// <b>A decisão de desistir mora aqui, e não no transporte:</b> sobrevive à troca do despacho em processo pelo
/// broker sem mudar, e o relógio falso a torna testável.
/// </para>
/// </remarks>
public sealed class ProvisionTenantHandler(
    ITenantRepository tenants,
    IMemberRepository membros,
    IIdentityProvider identidade,
    IProvisioningPolicy politica,
    IInvitationPolicy convites,
    IDateTimeProvider relogio,
    ILogger<ProvisionTenantHandler> logger)
    : ICommandHandler<ProvisionTenantCommand>
{
    public async ValueTask<Result> Handle(ProvisionTenantCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Tenant? tenant = await tenants.GetAsync(command.TenantId, cancellationToken);

        if (tenant is null)
        {
            ProvisioningLogs.TenantInexistente(logger, command.TenantId.Value);
            return Result.Success();
        }

        // Só Pending. Active é mensagem repetida; ProvisioningFailed é decisão registrada, que só o retry manual
        // desfaz — devolvendo o tenant a Pending antes de reenfileirar. Reprovisionar um Failed aqui apagaria a
        // decisão em silêncio.
        if (tenant.Status != TenantStatus.Pending)
        {
            ProvisioningLogs.ForaDePending(logger, tenant.Id.Value, tenant.Status);
            return Result.Success();
        }

        if (tenant.InitialAdminEmail is not { } email)
        {
            ProvisioningLogs.SemEmailDoAdmin(logger, tenant.Id.Value);
            tenant.MarkProvisioningFailed();
            return Result.Success();
        }

        if (!tenant.HasSeatAvailable)
        {
            ProvisioningLogs.SemVagaParaOAdmin(logger, tenant.Id.Value, tenant.Plan.MaxUsers);
            tenant.MarkProvisioningFailed();
            return Result.Success();
        }

        string organizacao;
        ExternalUserId admin;

        try
        {
            organizacao = await identidade.EnsureOrganizationAsync(
                tenant.Id, tenant.Slug, tenant.Name, cancellationToken);

            admin = await identidade.EnsureInvitedUserAsync(
                organizacao,
                tenant.Id,
                new InviteData(email, RoleName.TenantAdmin, convites.LinkLifetime),
                cancellationToken);
        }
        catch (IdentityProviderInconsistencyException excecao)
        {
            ProvisioningLogs.FalhaPermanente(logger, tenant.Id.Value, excecao);
            tenant.MarkProvisioningFailed();
            return Result.Success();
        }
        catch (Exception excecao) when (!cancellationToken.IsCancellationRequested && JanelaEsgotada(tenant))
        {
            // O filtro olha o token, e não o tipo da exceção — e as duas famílias que podem chegar aqui provam por
            // quê. O timeout do AddStandardResilienceHandler que envolve a Admin API chega como
            // TimeoutRejectedException, que não deriva de OperationCanceledException; o timeout cru de
            // HttpClient.Timeout do cliente do token endpoint (sem resiliência) chega como TaskCanceledException,
            // que É um OperationCanceledException. Um filtro por tipo teria que acompanhar as duas, e ainda erraria
            // a próxima. Só o desligamento do host fica de fora: a mensagem volta no próximo ciclo.
            ProvisioningLogs.JanelaEsgotada(logger, tenant.Id.Value, politica.MaxPendingDuration.TotalHours, excecao);
            tenant.MarkProvisioningFailed();
            return Result.Success();
        }

        // Ativação, vaga, Member e e-mail apagado numa operação de domínio; tenant e Member no mesmo commit (D2).
        Member membro = tenant.CompleteProvisioning(organizacao, admin, relogio.UtcNow);
        membros.Add(membro);

        ProvisioningLogs.Provisionado(logger, tenant.Id.Value);

        return Result.Success();
    }

    // Fechada no fim: RegisteredAt + janela já conta como esgotada.
    private bool JanelaEsgotada(Tenant tenant) =>
        relogio.UtcNow >= tenant.RegisteredAt + politica.MaxPendingDuration;
}
