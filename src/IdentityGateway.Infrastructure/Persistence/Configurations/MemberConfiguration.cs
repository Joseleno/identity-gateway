using IdentityGateway.Domain.Members;
using IdentityGateway.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IdentityGateway.Infrastructure.Persistence.Configurations;

/// <summary>
/// Mapeia o agregado <see cref="Member"/> na tabela <c>members</c>.
/// </summary>
/// <remarks>
/// <para>
/// O EF materializa pelo construtor privado do <see cref="Member"/>: todos os parâmetros são conversões de valor único,
/// casados por nome — diferente do <c>Tenant</c>, que precisa de um construtor à parte por causa do <c>Plan</c>.
/// </para>
/// <para>
/// <b>O índice único em <c>(tenant_id, external_user_id)</c></b> é o que a entrega concorrente duplicada encontra se
/// escapar do <c>xmin</c> do tenant: um usuário é membro de um tenant uma vez só.
/// </para>
/// </remarks>
internal sealed class MemberConfiguration : IEntityTypeConfiguration<Member>
{
    public void Configure(EntityTypeBuilder<Member> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("members");

        builder.HasKey(membro => membro.Id);

        builder.Property(membro => membro.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, valor => new MemberId(valor))
            .ValueGeneratedNever();

        builder.Property(membro => membro.TenantId)
            .HasColumnName("tenant_id")
            .HasConversion(id => id.Value, valor => new TenantId(valor))
            .IsRequired();

        // Restrict, e não Cascade: tenant é encerrado, nunca apagado (§9.8), e um DELETE em cascata apagaria o
        // histórico de membros em silêncio.
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(membro => membro.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(membro => membro.ExternalUserId)
            .HasColumnName("external_user_id")
            .HasMaxLength(255)
            .HasConversion(sub => sub.Value, valor => ExternalUserId.From(valor))
            .IsRequired();

        builder.Property(membro => membro.Status)
            .HasColumnName("status")
            .HasMaxLength(20)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(membro => membro.InvitedAt)
            .HasColumnName("invited_at")
            .IsRequired();

        // Auditoria do template: preenchida pelo AuditableInterceptor, nunca pelo domínio.
        builder.Property(membro => membro.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(membro => membro.UpdatedAt)
            .HasColumnName("updated_at");

        builder.Property(membro => membro.CreatedBy)
            .HasColumnName("created_by");

        builder.Property(membro => membro.UpdatedBy)
            .HasColumnName("updated_by");

        builder.HasIndex(membro => new { membro.TenantId, membro.ExternalUserId })
            .IsUnique()
            .HasDatabaseName("ix_members_tenant_id_external_user_id");

        builder.Ignore(membro => membro.DomainEvents);
    }
}
