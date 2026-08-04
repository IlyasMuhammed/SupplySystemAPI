using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Auth.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationAndInviteFieldsToUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InviteToken",
                schema: "auth",
                table: "UserAccounts",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InviteTokenExpiresAt",
                schema: "auth",
                table: "UserAccounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "auth",
                table: "UserAccounts",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InviteToken",
                schema: "auth",
                table: "UserAccounts");

            migrationBuilder.DropColumn(
                name: "InviteTokenExpiresAt",
                schema: "auth",
                table: "UserAccounts");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "auth",
                table: "UserAccounts");
        }
    }
}
