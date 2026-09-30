using IdentityGateway.Application.Common.Abstractions;
using IdentityGateway.Domain.Members;
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
/// <b>O convite segue a mesma ideia, pelo atributo de usuário <see cref="AtributoDoUsuario"/></b>: buscar, criar,
/// vincular, atribuir o papel e enviar, cada passo idempotente, para que uma entrega que caiu no meio seja retomada
/// pela próxima sem duplicar nada.
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

    /// <summary>
    /// Atributo de usuário que correlaciona o convidado ao tenant — o mesmo <c>tenant_id</c> que a §12.2 define como
    /// fonte do claim. Declarado no User Profile do realm só para <c>admin</c> (D10): sem a declaração, o Keycloak o
    /// descartaria em silêncio.
    /// </summary>
    internal const string AtributoDoUsuario = "tenant_id";

    private const string AtualizarSenha = "UPDATE_PASSWORD";
    private const string VerificarEmail = "VERIFY_EMAIL";

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

    public async Task<ExternalUserId> EnsureInvitedUserAsync(
        string organizationId, TenantId tenantId, InviteData invite, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        ArgumentNullException.ThrowIfNull(invite);

        UserRepresentation usuario = await ObterOuCriarUsuarioAsync(invite.Email.Value, tenantId, cancellationToken);
        string id = usuario.Id!;

        // Passo 3: o 409 (já é membro) é tratado como sucesso no cliente.
        await admin.AddOrganizationMemberAsync(organizationId, id, cancellationToken);

        await GarantirPapelAsync(id, invite.Role, tenantId, cancellationToken);

        // Passo 5 (D12): só com UPDATE_PASSWORD pendente. Sem ele, o convite foi aceito, e um link trocaria a senha de
        // um admin já ativo.
        if (usuario.RequiredActions?.Contains(AtualizarSenha) != true)
        {
            KeycloakLogs.ConviteJaAceito(logger, tenantId.Value);
            return ExternalUserId.From(id);
        }

        try
        {
            await admin.ExecuteActionsEmailAsync(
                id, [AtualizarSenha, VerificarEmail], (int)invite.LinkLifetime.TotalSeconds, cancellationToken);
        }
        catch (KeycloakBadRequestException excecao)
        {
            KeycloakLogs.EnvioRecusado(logger, tenantId.Value);

            throw new IdentityProviderInconsistencyException(
                $"O Keycloak recusou o envio do convite do tenant {tenantId.Value}: o usuário foi desabilitado ou "
                + "está sem e-mail.",
                excecao);
        }

        KeycloakLogs.ConviteEnviado(logger, tenantId.Value);
        return ExternalUserId.From(id);
    }

    /// <summary>Passos 1 e 2: acha o nosso usuário pelo e-mail ou o cria; conta alheia é inconsistência.</summary>
    private async Task<UserRepresentation> ObterOuCriarUsuarioAsync(
        string email, TenantId tenantId, CancellationToken cancellationToken)
    {
        string tenant = tenantId.Value.ToString();
        IReadOnlyList<UserRepresentation> achados = await admin.FindUsersByEmailAsync(email, cancellationToken);

        if (achados.Count > 0)
        {
            UserRepresentation nosso = achados.FirstOrDefault(usuario => EhDoTenant(usuario, tenant))
                                       ?? throw ContaAlheia(tenantId);

            KeycloakLogs.UsuarioJaExistia(logger, tenantId.Value);
            return nosso;
        }

        UserRepresentation novo = new(
            Id: null,
            Username: email,
            Email: email,
            Enabled: true,
            RequiredActions: [AtualizarSenha, VerificarEmail],
            Attributes: new() { [AtributoDoUsuario] = [tenant] });

        try
        {
            string id = await admin.CreateUserAsync(novo, cancellationToken);
            KeycloakLogs.UsuarioCriado(logger, tenantId.Value);

            return novo with { Id = id };
        }
        catch (KeycloakConflictException)
        {
            // UMA reconsulta, por e-mail e por username, e nunca em laço: o 409 também sai quando outro usuário tem
            // username igual ao nosso e-mail — e nesse caso repetir o POST daria 409 para sempre.
            List<UserRepresentation> candidatos =
            [
                .. await admin.FindUsersByEmailAsync(email, cancellationToken),
                .. await admin.FindUsersByUsernameAsync(email, cancellationToken),
            ];

            UserRepresentation vencedor = candidatos.FirstOrDefault(usuario => EhDoTenant(usuario, tenant))
                                          ?? throw ContaAlheia(tenantId);

            // Duas entregas da mesma mensagem concorreram, e a outra criou primeiro.
            KeycloakLogs.CorridaDoUsuarioResolvida(logger, tenantId.Value);
            return vencedor;
        }
    }

    /// <summary>Passo 4: o papel pelos endpoints do próprio usuário (sem <c>view-realm</c>).</summary>
    private async Task GarantirPapelAsync(
        string userId, RoleName papel, TenantId tenantId, CancellationToken cancellationToken)
    {
        IReadOnlyList<RoleRepresentation> atribuidos = await admin.GetUserRealmRolesAsync(userId, cancellationToken);

        if (atribuidos.Any(atribuido => atribuido.Name == papel.Value))
        {
            return;
        }

        IReadOnlyList<RoleRepresentation> disponiveis =
            await admin.GetAvailableUserRealmRolesAsync(userId, cancellationToken);
        RoleRepresentation? alvo = disponiveis.FirstOrDefault(disponivel => disponivel.Name == papel.Value);

        if (alvo is null)
        {
            // Nem atribuído nem disponível: o papel não existe no realm. O realm não é o que a Gateway espera, e
            // repetir não corrige.
            KeycloakLogs.PapelAusente(logger, papel.Value, tenantId.Value);

            throw new IdentityProviderInconsistencyException(
                $"O papel de realm '{papel.Value}' do convite do tenant {tenantId.Value} não existe no realm.");
        }

        await admin.AddUserRealmRolesAsync(userId, [alvo], cancellationToken);
        KeycloakLogs.PapelAtribuido(logger, papel.Value, tenantId.Value);
    }

    // Igual, e não "existe": o tenant_id de outro tenant não é tentativa anterior deste.
    private static bool EhDoTenant(UserRepresentation usuario, string tenant) =>
        usuario.Attributes is not null
        && usuario.Attributes.TryGetValue(AtributoDoUsuario, out List<string>? valores)
        && valores is [var unico]
        && unico == tenant;

    /// <summary>Registra a conta alheia e devolve a exceção para quem chama lançar.</summary>
    /// <remarks>
    /// Sem o e-mail na mensagem nem no log (D15): o <c>OutboxProcessor</c> grava a mensagem da exceção em
    /// <c>outbox_messages.error</c>.
    /// </remarks>
    private IdentityProviderInconsistencyException ContaAlheia(TenantId tenantId)
    {
        KeycloakLogs.UsuarioNaoCorrelacionado(logger, tenantId.Value);

        return new IdentityProviderInconsistencyException(
            $"O e-mail ou o username do convite do tenant {tenantId.Value} pertence a um usuário não correlacionado.");
    }
}
