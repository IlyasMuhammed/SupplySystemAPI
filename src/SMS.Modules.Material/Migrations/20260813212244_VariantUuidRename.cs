using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Material.Migrations
{
    /// <inheritdoc />
    public partial class VariantUuidRename : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ProductUuid",
                schema: "material",
                table: "stock_reservations",
                newName: "VariantUuid");

            migrationBuilder.RenameColumn(
                name: "ProductUuid",
                schema: "material",
                table: "material_issue_voucher_lines",
                newName: "VariantUuid");

            migrationBuilder.RenameColumn(
                name: "ProductUuid",
                schema: "material",
                table: "material_issue_request_lines",
                newName: "VariantUuid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "VariantUuid",
                schema: "material",
                table: "stock_reservations",
                newName: "ProductUuid");

            migrationBuilder.RenameColumn(
                name: "VariantUuid",
                schema: "material",
                table: "material_issue_voucher_lines",
                newName: "ProductUuid");

            migrationBuilder.RenameColumn(
                name: "VariantUuid",
                schema: "material",
                table: "material_issue_request_lines",
                newName: "ProductUuid");
        }
    }
}
