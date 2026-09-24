using IdentityGateway.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IdentityGateway.Infrastructure.Persistence.Configurations;

/// <summary>
/// Mapeia o agregado <see cref="Tenant"/> na tabela <c>tenants</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>O agregado não tem construtor sem parâmetro, e não precisa.</b> O EF materializa pelo construtor de
/// três parâmetros do <see cref="Tenant"/>, casando <c>id</c>, <c>name</c> e <c>slug</c> com as propriedades
/// de mesmo nome — os três são conversões de valor único. O que fica de fora é escrito no campo de apoio,
/// como já aconteceria por causa do <c>private set</c>.
/// </para>
/// <para>
/// <b>O <c>Plan</c> não entra no construtor porque o EF recusa vincular value object de múltiplos campos</b>
/// — nem como <c>ComplexProperty</c>, nem como <c>OwnsOne</c> (dotnet/efcore#31621). Por isso o
/// <see cref="Tenant"/> tem dois construtores: este, que o ORM usa, e o de quatro parâmetros, que o domínio
/// usa e que mantém o plano obrigatório. A razão completa está no XML doc de cada um.
/// </para>
/// </remarks>
internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("tenants");

        builder.HasKey(tenant => tenant.Id);

        builder.Property(tenant => tenant.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, valor => new TenantId(valor))
            .ValueGeneratedNever();

        builder.Property(tenant => tenant.Name)
            .HasColumnName("name")
            .HasMaxLength(200)
            .IsRequired();

        // Conversor e não owned type: value object de campo único, e o owned criaria um tipo aninhado no
        // modelo sem ganho nenhum. A volta usa Create(...).Value porque o dado gravado já foi validado na
        // escrita — falhar aqui seria corrupção, e deve estourar.
        builder.Property(tenant => tenant.Slug)
            .HasColumnName("slug")
            .HasMaxLength(63)
            .HasConversion(slug => slug.Value, valor => TenantSlug.Create(valor).Value)
            .IsRequired();

        // Índice único e global, sem filtro: Tenant não é ISoftDeletable, então não há linha excluída a
        // excluir. Global porque o slug vira o alias da Organization no Keycloak e potencialmente subdomínio —
        // o espaço de nomes é do sistema inteiro. É ele que fecha a janela entre o SELECT de unicidade do
        // handler e o INSERT.
        builder.HasIndex(tenant => tenant.Slug)
            .IsUnique()
            .HasDatabaseName("ix_tenants_slug");

        // Complex type achatado na mesma tabela, e não OwnsOne: Plan é parâmetro do construtor do Tenant, e
        // owned type é navegação — o EF Core recusa vincular navegação a parâmetro de construtor
        // ("No suitable constructor was found", descoberto ao gerar esta migration). Complex type (EF Core 8+)
        // não é navegação nem tem identidade própria; por isso se comporta, para o construtor, como qualquer
        // outra propriedade escalar. A §6.1 define Plan como value object, e achatá-lo à mão exigiria
        // propriedades espelho no agregado só para o ORM.
        builder.ComplexProperty(tenant => tenant.Plan, plano =>
        {
            plano.Property(valor => valor.Tier)
                .HasColumnName("plan_tier")
                .HasMaxLength(20)
                .HasConversion<string>()
                .IsRequired();

            plano.Property(valor => valor.MaxUsers)
                .HasColumnName("plan_max_users")
                .IsRequired();

            plano.Property(valor => valor.MaxClients)
                .HasColumnName("plan_max_clients")
                .IsRequired();
        });

        builder.Property(tenant => tenant.Status)
            .HasColumnName("status")
            .HasMaxLength(20)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(tenant => tenant.ExternalOrganizationId)
            .HasColumnName("external_organization_id");

        builder.Property(tenant => tenant.OccupiedSeats)
            .HasColumnName("occupied_seats")
            .IsRequired();

        builder.Property(tenant => tenant.OverSubscribed)
            .HasColumnName("over_subscribed")
            .IsRequired();

        // Concorrência otimista por xmin, que a §6.1 exige para OccupiedSeats. Propriedade de sombra porque
        // xmin é coluna de sistema do PostgreSQL: o domínio não deve carregar um campo de versão que só o ORM
        // entende. O efeito é o WHERE xmin = @original no UPDATE.
        //
        // ATENÇÃO ao regerar a migration inicial: mesmo com HasColumnType("xid"), o EF emite a coluna xmin no
        // CreateTable, e ela foi removida à mão de CriacaoDeTenants — criá-la falharia, porque o PostgreSQL já
        // a tem em toda tabela. Não é problema recorrente: depois desta migration, xmin está nos dois lados do
        // snapshot que o differ compara, então nunca mais reaparece. Só volta a morder quem apagar e refizer
        // esta migration do zero.
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        // Os domain events são levantados pelo agregado e coletados pelo DomainEventInterceptor; não são
        // estado persistido.
        builder.Ignore(tenant => tenant.DomainEvents);
    }
}
