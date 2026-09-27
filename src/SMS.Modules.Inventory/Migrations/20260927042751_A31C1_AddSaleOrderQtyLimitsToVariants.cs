using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class A31C1_AddSaleOrderQtyLimitsToVariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "SaleOrderMaxQty",
                schema: "inventory",
                table: "ProductVariants",
                type: "decimal(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SaleOrderMinQty",
                schema: "inventory",
                table: "ProductVariants",
                type: "decimal(18,4)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SaleOrderMaxQty",
                schema: "inventory",
                table: "ProductVariants");

            migrationBuilder.DropColumn(
                name: "SaleOrderMinQty",
                schema: "inventory",
                table: "ProductVariants");
        }
    }
}
