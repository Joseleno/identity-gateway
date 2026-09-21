using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdentityGateway.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CriacaoDeTenants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    external_organization_id = table.Column<string>(type: "text", nullable: true),
                    occupied_seats = table.Column<int>(type: "integer", nullable: false),
                    over_subscribed = table.Column<bool>(type: "boolean", nullable: false),
                    plan_max_clients = table.Column<int>(type: "integer", nullable: false),
                    plan_max_users = table.Column<int>(type: "integer", nullable: false),
                    plan_tier = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenants_slug",
                table: "tenants",
                column: "slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
