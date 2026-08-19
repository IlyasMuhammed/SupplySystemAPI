using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Finance.Migrations
{
    /// <inheritdoc />
    public partial class VariantIdRename : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ProductId",
                schema: "finance",
                table: "master_product_ledger",
                newName: "VariantId");

            migrationBuilder.RenameIndex(
                name: "IX_master_product_ledger_ProductId",
                schema: "finance",
                table: "master_product_ledger",
                newName: "IX_master_product_ledger_VariantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "VariantId",
                schema: "finance",
                table: "master_product_ledger",
                newName: "ProductId");

            migrationBuilder.RenameIndex(
                name: "IX_master_product_ledger_VariantId",
                schema: "finance",
                table: "master_product_ledger",
                newName: "IX_master_product_ledger_ProductId");
        }
    }
}
