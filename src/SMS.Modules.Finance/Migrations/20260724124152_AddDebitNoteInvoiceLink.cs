using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Finance.Migrations
{
    /// <inheritdoc />
    public partial class AddDebitNoteInvoiceLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApplicationStatus",
                schema: "finance",
                table: "debit_notes",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "PENDING");

            migrationBuilder.AddColumn<DateTime>(
                name: "AppliedAt",
                schema: "finance",
                table: "debit_notes",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppliedToInvoiceNumber",
                schema: "finance",
                table: "debit_notes",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AppliedToInvoiceUuid",
                schema: "finance",
                table: "debit_notes",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CarriedForwardAmount",
                schema: "finance",
                table: "debit_notes",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceNumber",
                schema: "finance",
                table: "debit_notes",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InvoiceUuid",
                schema: "finance",
                table: "debit_notes",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApplicationStatus",
                schema: "finance",
                table: "debit_notes");

            migrationBuilder.DropColumn(
                name: "AppliedAt",
                schema: "finance",
                table: "debit_notes");

            migrationBuilder.DropColumn(
                name: "AppliedToInvoiceNumber",
                schema: "finance",
                table: "debit_notes");

            migrationBuilder.DropColumn(
                name: "AppliedToInvoiceUuid",
                schema: "finance",
                table: "debit_notes");

            migrationBuilder.DropColumn(
                name: "CarriedForwardAmount",
                schema: "finance",
                table: "debit_notes");

            migrationBuilder.DropColumn(
                name: "InvoiceNumber",
                schema: "finance",
                table: "debit_notes");

            migrationBuilder.DropColumn(
                name: "InvoiceUuid",
                schema: "finance",
                table: "debit_notes");
        }
    }
}
