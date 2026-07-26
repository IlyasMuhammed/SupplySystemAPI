using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Finance.Migrations
{
    /// <inheritdoc />
    public partial class AddMasterProductLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "master_product_ledger",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LedgerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    ProductCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ProductName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CategoryName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    WarehouseId = table.Column<int>(type: "int", nullable: false),
                    WarehouseName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TransactionDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TransactionType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ReferenceType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ReferenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReferenceNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    QuantityIn = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    QuantityOut = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    UnitCost = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    TotalValue = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    SourceType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SourceName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DestinationType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DestinationName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_master_product_ledger", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_master_product_ledger_LedgerId",
                schema: "finance",
                table: "master_product_ledger",
                column: "LedgerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_master_product_ledger_ProductId",
                schema: "finance",
                table: "master_product_ledger",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_master_product_ledger_ReferenceId",
                schema: "finance",
                table: "master_product_ledger",
                column: "ReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_master_product_ledger_TransactionDate",
                schema: "finance",
                table: "master_product_ledger",
                column: "TransactionDate");

            migrationBuilder.CreateIndex(
                name: "IX_master_product_ledger_TransactionType",
                schema: "finance",
                table: "master_product_ledger",
                column: "TransactionType");

            migrationBuilder.CreateIndex(
                name: "IX_master_product_ledger_WarehouseId",
                schema: "finance",
                table: "master_product_ledger",
                column: "WarehouseId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "master_product_ledger",
                schema: "finance");
        }
    }
}
