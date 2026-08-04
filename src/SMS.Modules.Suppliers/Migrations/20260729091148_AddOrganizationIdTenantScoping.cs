using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Suppliers.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationIdTenantScoping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Suppliers_SupplierCode",
                schema: "suppliers",
                table: "Suppliers");

            migrationBuilder.DropIndex(
                name: "IX_ScorecardDimensionWeights_DimensionCode",
                schema: "suppliers",
                table: "ScorecardDimensionWeights");

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "suppliers",
                table: "SupplierTypes",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "suppliers",
                table: "SupplierTypeMappings",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "suppliers",
                table: "SupplierScoreSnapshots",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "suppliers",
                table: "Suppliers",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "suppliers",
                table: "SupplierIndustryMappings",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "suppliers",
                table: "SupplierDocuments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "suppliers",
                table: "SupplierContacts",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "suppliers",
                table: "SupplierCategories",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "suppliers",
                table: "SupplierBankDetails",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "suppliers",
                table: "ScorecardDimensionWeights",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "suppliers",
                table: "GrnScoreDetails",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.CreateIndex(
                name: "IX_SupplierTypes_OrganizationId",
                schema: "suppliers",
                table: "SupplierTypes",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierTypeMappings_OrganizationId",
                schema: "suppliers",
                table: "SupplierTypeMappings",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierScoreSnapshots_OrganizationId",
                schema: "suppliers",
                table: "SupplierScoreSnapshots",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_OrganizationId",
                schema: "suppliers",
                table: "Suppliers",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_OrganizationId_SupplierCode",
                schema: "suppliers",
                table: "Suppliers",
                columns: new[] { "OrganizationId", "SupplierCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierIndustryMappings_OrganizationId",
                schema: "suppliers",
                table: "SupplierIndustryMappings",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierDocuments_OrganizationId",
                schema: "suppliers",
                table: "SupplierDocuments",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierContacts_OrganizationId",
                schema: "suppliers",
                table: "SupplierContacts",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCategories_OrganizationId",
                schema: "suppliers",
                table: "SupplierCategories",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBankDetails_OrganizationId",
                schema: "suppliers",
                table: "SupplierBankDetails",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_ScorecardDimensionWeights_OrganizationId",
                schema: "suppliers",
                table: "ScorecardDimensionWeights",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_ScorecardDimensionWeights_OrganizationId_DimensionCode",
                schema: "suppliers",
                table: "ScorecardDimensionWeights",
                columns: new[] { "OrganizationId", "DimensionCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GrnScoreDetails_OrganizationId",
                schema: "suppliers",
                table: "GrnScoreDetails",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SupplierTypes_OrganizationId",
                schema: "suppliers",
                table: "SupplierTypes");

            migrationBuilder.DropIndex(
                name: "IX_SupplierTypeMappings_OrganizationId",
                schema: "suppliers",
                table: "SupplierTypeMappings");

            migrationBuilder.DropIndex(
                name: "IX_SupplierScoreSnapshots_OrganizationId",
                schema: "suppliers",
                table: "SupplierScoreSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_Suppliers_OrganizationId",
                schema: "suppliers",
                table: "Suppliers");

            migrationBuilder.DropIndex(
                name: "IX_Suppliers_OrganizationId_SupplierCode",
                schema: "suppliers",
                table: "Suppliers");

            migrationBuilder.DropIndex(
                name: "IX_SupplierIndustryMappings_OrganizationId",
                schema: "suppliers",
                table: "SupplierIndustryMappings");

            migrationBuilder.DropIndex(
                name: "IX_SupplierDocuments_OrganizationId",
                schema: "suppliers",
                table: "SupplierDocuments");

            migrationBuilder.DropIndex(
                name: "IX_SupplierContacts_OrganizationId",
                schema: "suppliers",
                table: "SupplierContacts");

            migrationBuilder.DropIndex(
                name: "IX_SupplierCategories_OrganizationId",
                schema: "suppliers",
                table: "SupplierCategories");

            migrationBuilder.DropIndex(
                name: "IX_SupplierBankDetails_OrganizationId",
                schema: "suppliers",
                table: "SupplierBankDetails");

            migrationBuilder.DropIndex(
                name: "IX_ScorecardDimensionWeights_OrganizationId",
                schema: "suppliers",
                table: "ScorecardDimensionWeights");

            migrationBuilder.DropIndex(
                name: "IX_ScorecardDimensionWeights_OrganizationId_DimensionCode",
                schema: "suppliers",
                table: "ScorecardDimensionWeights");

            migrationBuilder.DropIndex(
                name: "IX_GrnScoreDetails_OrganizationId",
                schema: "suppliers",
                table: "GrnScoreDetails");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "suppliers",
                table: "SupplierTypes");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "suppliers",
                table: "SupplierTypeMappings");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "suppliers",
                table: "SupplierScoreSnapshots");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "suppliers",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "suppliers",
                table: "SupplierIndustryMappings");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "suppliers",
                table: "SupplierDocuments");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "suppliers",
                table: "SupplierContacts");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "suppliers",
                table: "SupplierCategories");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "suppliers",
                table: "SupplierBankDetails");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "suppliers",
                table: "ScorecardDimensionWeights");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "suppliers",
                table: "GrnScoreDetails");

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_SupplierCode",
                schema: "suppliers",
                table: "Suppliers",
                column: "SupplierCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScorecardDimensionWeights_DimensionCode",
                schema: "suppliers",
                table: "ScorecardDimensionWeights",
                column: "DimensionCode",
                unique: true);
        }
    }
}
