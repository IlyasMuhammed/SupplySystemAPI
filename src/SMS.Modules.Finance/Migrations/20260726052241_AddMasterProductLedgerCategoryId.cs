using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Finance.Migrations
{
    /// <inheritdoc />
    public partial class AddMasterProductLedgerCategoryId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CategoryId",
                schema: "finance",
                table: "master_product_ledger",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_master_product_ledger_CategoryId",
                schema: "finance",
                table: "master_product_ledger",
                column: "CategoryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_master_product_ledger_CategoryId",
                schema: "finance",
                table: "master_product_ledger");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                schema: "finance",
                table: "master_product_ledger");
        }
    }
}
