using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;

namespace IdentityGateway.Domain.UnitTests.Members;

/// <summary>
/// A fábrica do <see cref="Member"/> é interna: quem convida é o <c>Tenant</c>, que reserva a vaga antes.
/// </summary>
public sealed class MemberTests
{
    private static readonly DateTimeOffset Instante = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Invite_NasceInvitedComTenantESub()
    {
        var tenant = TenantId.New();

        var membro = Member.Invite(tenant, ExternalUserId.From("sub-1"), Instante);

        membro.TenantId.Should().Be(tenant);
        membro.ExternalUserId.Should().Be(ExternalUserId.From("sub-1"));
        membro.Status.Should().Be(MemberStatus.Invited);
        membro.InvitedAt.Should().Be(Instante);
        membro.DomainEvents.Should().BeEmpty("MemberInvited não tem consumidor nesta fatia (spec §4.2)");
    }

    [Fact]
    public void Invite_NormalizaInvitedAtParaUtc()
    {
        // Mesmo motivo do Tenant.RegisteredAt: o Npgsql recusa offset diferente de zero numa coluna timestamptz.
        DateTimeOffset emBrasilia = new(2026, 9, 29, 9, 0, 0, TimeSpan.FromHours(-3));

        var membro = Member.Invite(TenantId.New(), ExternalUserId.From("sub-1"), emBrasilia);

        membro.InvitedAt.Offset.Should().Be(TimeSpan.Zero);
        membro.InvitedAt.Should().Be(emBrasilia);
    }

    [Fact]
    public void Invite_ComTenantIdVazio_Lanca()
    {
        Action convidar = () => Member.Invite(new TenantId(Guid.Empty), ExternalUserId.From("sub-1"), Instante);

        convidar.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Invite_SemSub_Lanca()
    {
        Action convidar = () => Member.Invite(TenantId.New(), null!, Instante);

        convidar.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Invite_GeraIdentidadesDistintas()
    {
        var tenant = TenantId.New();

        var um = Member.Invite(tenant, ExternalUserId.From("sub-1"), Instante);
        var outro = Member.Invite(tenant, ExternalUserId.From("sub-2"), Instante);

        um.Id.Should().NotBe(outro.Id);
    }
}
