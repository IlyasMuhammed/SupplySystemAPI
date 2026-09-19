using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantOrganizationIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_VariantAttributeValues_OrganizationId",
                schema: "inventory",
                table: "VariantAttributeValues",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierRateHistory_OrganizationId",
                schema: "inventory",
                table: "SupplierRateHistory",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VariantAttributeValues_OrganizationId",
                schema: "inventory",
                table: "VariantAttributeValues");

            migrationBuilder.DropIndex(
                name: "IX_SupplierRateHistory_OrganizationId",
                schema: "inventory",
                table: "SupplierRateHistory");
        }
    }
}
