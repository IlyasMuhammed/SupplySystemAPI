using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Finance.Migrations
{
    /// <inheritdoc />
    public partial class AddMasterFinancialLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "master_financial_ledger",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SequenceNo = table.Column<int>(type: "int", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
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
                    table.PrimaryKey("PK_master_financial_ledger", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_master_financial_ledger_EntryDate",
                schema: "finance",
                table: "master_financial_ledger",
                column: "EntryDate");

            migrationBuilder.CreateIndex(
                name: "IX_master_financial_ledger_ReferenceId",
                schema: "finance",
                table: "master_financial_ledger",
                column: "ReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_master_financial_ledger_SequenceNo",
                schema: "finance",
                table: "master_financial_ledger",
                column: "SequenceNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_master_financial_ledger_SupplierId",
                schema: "finance",
                table: "master_financial_ledger",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_master_financial_ledger_TransactionType",
                schema: "finance",
                table: "master_financial_ledger",
                column: "TransactionType");

            migrationBuilder.CreateIndex(
                name: "IX_master_financial_ledger_UUID",
                schema: "finance",
                table: "master_financial_ledger",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "master_financial_ledger",
                schema: "finance");
        }
    }
}
