using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Lookups.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationIdTenantScoping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "lookups",
                table: "PoDocumentTemplates",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<bool>(
                name: "IsGlobal",
                schema: "lookups",
                table: "LookupValues",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "lookups",
                table: "LookupValues",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PoDocumentTemplates_OrganizationId",
                schema: "lookups",
                table: "PoDocumentTemplates",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_LookupValues_OrganizationId",
                schema: "lookups",
                table: "LookupValues",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PoDocumentTemplates_OrganizationId",
                schema: "lookups",
                table: "PoDocumentTemplates");

            migrationBuilder.DropIndex(
                name: "IX_LookupValues_OrganizationId",
                schema: "lookups",
                table: "LookupValues");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "lookups",
                table: "PoDocumentTemplates");

            migrationBuilder.DropColumn(
                name: "IsGlobal",
                schema: "lookups",
                table: "LookupValues");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "lookups",
                table: "LookupValues");
        }
    }
}
