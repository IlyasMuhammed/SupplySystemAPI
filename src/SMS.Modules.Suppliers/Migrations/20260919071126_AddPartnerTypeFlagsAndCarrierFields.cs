using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Suppliers.Migrations
{
    /// <inheritdoc />
    public partial class AddPartnerTypeFlagsAndCarrierFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsCarrier",
                schema: "suppliers",
                table: "BusinessPartners",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsCustomer",
                schema: "suppliers",
                table: "BusinessPartners",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsServiceProvider",
                schema: "suppliers",
                table: "BusinessPartners",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsVendor",
                schema: "suppliers",
                table: "BusinessPartners",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "PartnerType",
                schema: "suppliers",
                table: "BusinessPartners",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "VENDOR");

            migrationBuilder.AddColumn<string>(
                name: "ServiceCategories",
                schema: "suppliers",
                table: "BusinessPartners",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VehicleTypes",
                schema: "suppliers",
                table: "BusinessPartners",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BusinessPartners_OrganizationId_IsVendor_IsCustomer_IsCarrier_IsServiceProvider",
                schema: "suppliers",
                table: "BusinessPartners",
                columns: new[] { "OrganizationId", "IsVendor", "IsCustomer", "IsCarrier", "IsServiceProvider" });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessPartners_OrganizationId_PartnerType_IsActive",
                schema: "suppliers",
                table: "BusinessPartners",
                columns: new[] { "OrganizationId", "PartnerType", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BusinessPartners_OrganizationId_IsVendor_IsCustomer_IsCarrier_IsServiceProvider",
                schema: "suppliers",
                table: "BusinessPartners");

            migrationBuilder.DropIndex(
                name: "IX_BusinessPartners_OrganizationId_PartnerType_IsActive",
                schema: "suppliers",
                table: "BusinessPartners");

            migrationBuilder.DropColumn(
                name: "IsCarrier",
                schema: "suppliers",
                table: "BusinessPartners");

            migrationBuilder.DropColumn(
                name: "IsCustomer",
                schema: "suppliers",
                table: "BusinessPartners");

            migrationBuilder.DropColumn(
                name: "IsServiceProvider",
                schema: "suppliers",
                table: "BusinessPartners");

            migrationBuilder.DropColumn(
                name: "IsVendor",
                schema: "suppliers",
                table: "BusinessPartners");

            migrationBuilder.DropColumn(
                name: "PartnerType",
                schema: "suppliers",
                table: "BusinessPartners");

            migrationBuilder.DropColumn(
                name: "ServiceCategories",
                schema: "suppliers",
                table: "BusinessPartners");

            migrationBuilder.DropColumn(
                name: "VehicleTypes",
                schema: "suppliers",
                table: "BusinessPartners");
        }
    }
}
