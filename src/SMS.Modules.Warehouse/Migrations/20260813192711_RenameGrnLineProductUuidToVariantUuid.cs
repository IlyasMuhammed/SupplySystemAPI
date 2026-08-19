using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Warehouse.Migrations
{
    /// <inheritdoc />
    public partial class RenameGrnLineProductUuidToVariantUuid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ProductUuid",
                schema: "warehouse",
                table: "grn_lines",
                newName: "VariantUuid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "VariantUuid",
                schema: "warehouse",
                table: "grn_lines",
                newName: "ProductUuid");
        }
    }
}
