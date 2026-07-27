using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Notifications.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppMessageStatusTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ErrorCode",
                schema: "notifications",
                table: "whatsapp_message_logs",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorMessage",
                schema: "notifications",
                table: "whatsapp_message_logs",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StatusUpdatedAt",
                schema: "notifications",
                table: "whatsapp_message_logs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_whatsapp_message_logs_ProviderMessageId",
                schema: "notifications",
                table: "whatsapp_message_logs",
                column: "ProviderMessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_whatsapp_message_logs_ProviderMessageId",
                schema: "notifications",
                table: "whatsapp_message_logs");

            migrationBuilder.DropColumn(
                name: "ErrorCode",
                schema: "notifications",
                table: "whatsapp_message_logs");

            migrationBuilder.DropColumn(
                name: "ErrorMessage",
                schema: "notifications",
                table: "whatsapp_message_logs");

            migrationBuilder.DropColumn(
                name: "StatusUpdatedAt",
                schema: "notifications",
                table: "whatsapp_message_logs");
        }
    }
}
