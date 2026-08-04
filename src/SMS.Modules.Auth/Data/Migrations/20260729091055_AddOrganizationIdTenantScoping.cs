using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Auth.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationIdTenantScoping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Departments_Name",
                schema: "auth",
                table: "Departments");

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "auth",
                table: "UserSessions",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "auth",
                table: "UserPermissions",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AlterColumn<Guid>(
                name: "OrganizationId",
                schema: "auth",
                table: "UserAccounts",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "auth",
                table: "Roles",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "auth",
                table: "RolePermissions",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "auth",
                table: "Departments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_OrganizationId",
                schema: "auth",
                table: "UserSessions",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_UserPermissions_OrganizationId",
                schema: "auth",
                table: "UserPermissions",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_UserAccounts_OrganizationId",
                schema: "auth",
                table: "UserAccounts",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_OrganizationId",
                schema: "auth",
                table: "Roles",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_OrganizationId",
                schema: "auth",
                table: "RolePermissions",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Departments_OrganizationId",
                schema: "auth",
                table: "Departments",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Departments_OrganizationId_Name",
                schema: "auth",
                table: "Departments",
                columns: new[] { "OrganizationId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserSessions_OrganizationId",
                schema: "auth",
                table: "UserSessions");

            migrationBuilder.DropIndex(
                name: "IX_UserPermissions_OrganizationId",
                schema: "auth",
                table: "UserPermissions");

            migrationBuilder.DropIndex(
                name: "IX_UserAccounts_OrganizationId",
                schema: "auth",
                table: "UserAccounts");

            migrationBuilder.DropIndex(
                name: "IX_Roles_OrganizationId",
                schema: "auth",
                table: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_RolePermissions_OrganizationId",
                schema: "auth",
                table: "RolePermissions");

            migrationBuilder.DropIndex(
                name: "IX_Departments_OrganizationId",
                schema: "auth",
                table: "Departments");

            migrationBuilder.DropIndex(
                name: "IX_Departments_OrganizationId_Name",
                schema: "auth",
                table: "Departments");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "auth",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "auth",
                table: "UserPermissions");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "auth",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "auth",
                table: "RolePermissions");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "auth",
                table: "Departments");

            migrationBuilder.AlterColumn<Guid>(
                name: "OrganizationId",
                schema: "auth",
                table: "UserAccounts",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.CreateIndex(
                name: "IX_Departments_Name",
                schema: "auth",
                table: "Departments",
                column: "Name",
                unique: true);
        }
    }
}
