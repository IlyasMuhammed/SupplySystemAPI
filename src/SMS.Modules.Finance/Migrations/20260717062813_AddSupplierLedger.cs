using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Finance.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "supplier_ledger_entries",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SequenceNo = table.Column<int>(type: "int", nullable: false),
                    TransactionType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ReferenceType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ReferenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReferenceNo = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    EntryDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DebitAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    CreditAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    BalanceAfter = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Narration = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_ledger_entries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_ledger_entries_SupplierId_EntryDate",
                schema: "finance",
                table: "supplier_ledger_entries",
                columns: new[] { "SupplierId", "EntryDate" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_ledger_entries_SupplierId_SequenceNo",
                schema: "finance",
                table: "supplier_ledger_entries",
                columns: new[] { "SupplierId", "SequenceNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_ledger_entries_UUID",
                schema: "finance",
                table: "supplier_ledger_entries",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "supplier_ledger_entries",
                schema: "finance");
        }
    }
}
