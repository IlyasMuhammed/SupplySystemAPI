using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Finance.Migrations
{
    /// <inheritdoc />
    public partial class AllowPayablesWithoutPurchaseOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "PoUuid",
                schema: "finance",
                table: "invoices",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<string>(
                name: "PoNumber",
                schema: "finance",
                table: "invoices",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20);

            migrationBuilder.AddColumn<string>(
                name: "SourceType",
                schema: "finance",
                table: "invoices",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceUuid",
                schema: "finance",
                table: "invoices",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "PoLineUuid",
                schema: "finance",
                table: "invoice_lines",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_SourceType_SourceUuid",
                schema: "finance",
                table: "invoices",
                columns: new[] { "SourceType", "SourceUuid" },
                unique: true,
                filter: "[SourceUuid] IS NOT NULL AND [IsDelete] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_invoices_SourceType_SourceUuid",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "SourceType",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "SourceUuid",
                schema: "finance",
                table: "invoices");

            migrationBuilder.AlterColumn<Guid>(
                name: "PoUuid",
                schema: "finance",
                table: "invoices",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "PoNumber",
                schema: "finance",
                table: "invoices",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "PoLineUuid",
                schema: "finance",
                table: "invoice_lines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
