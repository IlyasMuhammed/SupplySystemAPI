using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Demand.Migrations
{
    /// <inheritdoc />
    public partial class RenamePoLineProductUuidToVariantUuid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ProductUuid",
                schema: "demand",
                table: "purchase_order_lines",
                newName: "VariantUuid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "VariantUuid",
                schema: "demand",
                table: "purchase_order_lines",
                newName: "ProductUuid");
        }
    }
}
