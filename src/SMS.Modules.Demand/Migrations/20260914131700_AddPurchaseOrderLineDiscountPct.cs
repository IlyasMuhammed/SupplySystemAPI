using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Demand.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseOrderLineDiscountPct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "LineDiscountPct",
                schema: "demand",
                table: "purchase_order_lines",
                type: "decimal(5,2)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LineDiscountPct",
                schema: "demand",
                table: "purchase_order_lines");
        }
    }
}
