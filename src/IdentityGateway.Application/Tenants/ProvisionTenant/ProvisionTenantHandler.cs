using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Application.Common.Messaging;
using IdentityGateway.Domain.Common;
using IdentityGateway.Domain.Tenants;
using Microsoft.Extensions.Logging;

namespace IdentityGateway.Application.Tenants.ProvisionTenant;

/// <summary>
/// Provisiona o tenant: garante a Organization e marca <c>Active</c> — ou <c>ProvisioningFailed</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Contrato com quem despacha.</b> Falha transitória dentro da janela sai como a exceção original, e quem
/// despacha repete. Todo desfecho terminal — ativo, falha permanente, janela esgotada, mensagem repetida, tenant
/// inexistente — é <see cref="Result.Success()"/>, e a mensagem sai da fila. O commit é do <c>TransactionBehavior</c>.
/// </para>
/// <para>
/// <b>A decisão de desistir mora aqui, e não no transporte:</b> sobrevive à troca do despacho em processo pelo
/// broker sem mudar, e o relógio falso a torna testável.
/// </para>
/// </remarks>
public sealed class ProvisionTenantHandler(
    ITenantRepository tenants,
    IIdentityProvider identidade,
    IProvisioningPolicy politica,
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

        string organizacao;

        try
        {
            organizacao = await identidade.EnsureOrganizationAsync(
                tenant.Id, tenant.Slug, tenant.Name, cancellationToken);
        }
        catch (IdentityProviderInconsistencyException excecao)
        {
            ProvisioningLogs.FalhaPermanente(logger, tenant.Id.Value, excecao);
            tenant.MarkProvisioningFailed();
            return Result.Success();
        }
        catch (Exception excecao) when (!cancellationToken.IsCancellationRequested && JanelaEsgotada(tenant))
        {
            // O filtro olha o token, e não o tipo da exceção: o timeout da resiliência chega como
            // TaskCanceledException — um OperationCanceledException — e um filtro por tipo o tiraria da janela. Só o
            // desligamento do host fica de fora: a mensagem volta no próximo ciclo.
            ProvisioningLogs.JanelaEsgotada(logger, tenant.Id.Value, politica.MaxPendingDuration.TotalHours, excecao);
            tenant.MarkProvisioningFailed();
            return Result.Success();
        }

        tenant.MarkProvisioned(organizacao);
        ProvisioningLogs.Provisionado(logger, tenant.Id.Value);

        return Result.Success();
    }

    // Fechada no fim: RegisteredAt + janela já conta como esgotada.
    private bool JanelaEsgotada(Tenant tenant) =>
        relogio.UtcNow >= tenant.RegisteredAt + politica.MaxPendingDuration;
}
