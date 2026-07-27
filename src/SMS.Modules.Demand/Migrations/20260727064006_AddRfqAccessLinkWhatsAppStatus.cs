using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Demand.Migrations
{
    /// <inheritdoc />
    public partial class AddRfqAccessLinkWhatsAppStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WhatsAppProviderMessageId",
                schema: "demand",
                table: "rfq_access_links",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WhatsAppStatus",
                schema: "demand",
                table: "rfq_access_links",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WhatsAppStatusUpdatedAt",
                schema: "demand",
                table: "rfq_access_links",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_rfq_access_links_WhatsAppProviderMessageId",
                schema: "demand",
                table: "rfq_access_links",
                column: "WhatsAppProviderMessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_rfq_access_links_WhatsAppProviderMessageId",
                schema: "demand",
                table: "rfq_access_links");

            migrationBuilder.DropColumn(
                name: "WhatsAppProviderMessageId",
                schema: "demand",
                table: "rfq_access_links");

            migrationBuilder.DropColumn(
                name: "WhatsAppStatus",
                schema: "demand",
                table: "rfq_access_links");

            migrationBuilder.DropColumn(
                name: "WhatsAppStatusUpdatedAt",
                schema: "demand",
                table: "rfq_access_links");
        }
    }
}
