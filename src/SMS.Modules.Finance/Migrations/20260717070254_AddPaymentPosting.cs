using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Finance.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentPosting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CreditNoteUuid",
                schema: "finance",
                table: "supplier_payments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentType",
                schema: "finance",
                table: "supplier_payments",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "STANDARD");

            migrationBuilder.AddColumn<DateTime>(
                name: "PostedAt",
                schema: "finance",
                table: "supplier_payments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PaidAmount",
                schema: "finance",
                table: "invoices",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "supplier_advance_payments",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierPaymentUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginalAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    AvailableBalance = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_advance_payments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_advance_payments_SupplierId",
                schema: "finance",
                table: "supplier_advance_payments",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_advance_payments_UUID",
                schema: "finance",
                table: "supplier_advance_payments",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "supplier_advance_payments",
                schema: "finance");

            migrationBuilder.DropColumn(
                name: "CreditNoteUuid",
                schema: "finance",
                table: "supplier_payments");

            migrationBuilder.DropColumn(
                name: "PaymentType",
                schema: "finance",
                table: "supplier_payments");

            migrationBuilder.DropColumn(
                name: "PostedAt",
                schema: "finance",
                table: "supplier_payments");

            migrationBuilder.DropColumn(
                name: "PaidAmount",
                schema: "finance",
                table: "invoices");
        }
    }
}
