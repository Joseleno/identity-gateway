using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityGateway.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConviteDoAdminInicial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "initial_admin_email",
                table: "tenants",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_user_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    invited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_members", x => x.id);
                    table.ForeignKey(
                        name: "FK_members_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_members_tenant_id_external_user_id",
                table: "members",
                columns: new[] { "tenant_id", "external_user_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "members");

            migrationBuilder.DropColumn(
                name: "initial_admin_email",
                table: "tenants");
        }
    }
}
