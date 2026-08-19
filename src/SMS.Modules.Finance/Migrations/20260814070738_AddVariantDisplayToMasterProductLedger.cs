using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Finance.Migrations
{
    /// <inheritdoc />
    public partial class AddVariantDisplayToMasterProductLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Sku",
                schema: "finance",
                table: "master_product_ledger",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VariantName",
                schema: "finance",
                table: "master_product_ledger",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Sku",
                schema: "finance",
                table: "master_product_ledger");

            migrationBuilder.DropColumn(
                name: "VariantName",
                schema: "finance",
                table: "master_product_ledger");
        }
    }
}
