using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class AddVariantSupplierPreferredAndVendorPartNo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPreferred",
                schema: "inventory",
                table: "VariantSuppliers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "VendorPartNo",
                schema: "inventory",
                table: "VariantSuppliers",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPreferred",
                schema: "inventory",
                table: "VariantSuppliers");

            migrationBuilder.DropColumn(
                name: "VendorPartNo",
                schema: "inventory",
                table: "VariantSuppliers");
        }
    }
}
