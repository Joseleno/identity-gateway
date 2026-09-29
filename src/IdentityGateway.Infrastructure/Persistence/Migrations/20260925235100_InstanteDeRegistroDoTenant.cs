using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityGateway.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InstanteDeRegistroDoTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // As linhas existentes (só há dados de desenvolvimento) recebem o instante da migration; o default sai logo
            // em seguida, porque dali em diante o domínio sempre informa o valor. Deixar o mínimo de DateTimeOffset que
            // o EF gera faria todo tenant antigo parecer registrado no ano 1 — e o provisionamento o daria como
            // expirado na primeira tentativa.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "registered_at",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.Sql("ALTER TABLE tenants ALTER COLUMN registered_at DROP DEFAULT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "registered_at",
                table: "tenants");
        }
    }
}
