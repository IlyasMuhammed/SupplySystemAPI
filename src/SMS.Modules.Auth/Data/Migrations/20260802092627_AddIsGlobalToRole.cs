using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Auth.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIsGlobalToRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Roles_RoleCode",
                schema: "auth",
                table: "Roles");

            migrationBuilder.AlterColumn<Guid>(
                name: "OrganizationId",
                schema: "auth",
                table: "Roles",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<bool>(
                name: "IsGlobal",
                schema: "auth",
                table: "Roles",
                type: "bit",
                nullable: false,
                defaultValue: true);

            // Every role that existed before this migration is the shared seeded catalog (this
            // feature — org-owned custom roles — didn't exist yet) — the column default above
            // already marks them IsGlobal=1; this clears the leftover SCM-DEMO OrganizationId
            // stamp so a global role's ownership reads as "none" rather than "SCM-DEMO specifically".
            migrationBuilder.Sql("UPDATE auth.Roles SET OrganizationId = NULL WHERE IsGlobal = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsGlobal",
                schema: "auth",
                table: "Roles");

            migrationBuilder.AlterColumn<Guid>(
                name: "OrganizationId",
                schema: "auth",
                table: "Roles",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_RoleCode",
                schema: "auth",
                table: "Roles",
                column: "RoleCode",
                unique: true);
        }
    }
}
