using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Suppliers.Migrations
{
    /// <inheritdoc />
    public partial class RenameSuppliersToBusinessPartners : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SupplierBankDetails_Suppliers_SupplierId",
                schema: "suppliers",
                table: "SupplierBankDetails");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierContacts_Suppliers_SupplierId",
                schema: "suppliers",
                table: "SupplierContacts");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierDocuments_Suppliers_SupplierId",
                schema: "suppliers",
                table: "SupplierDocuments");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierIndustryMappings_Suppliers_SupplierId",
                schema: "suppliers",
                table: "SupplierIndustryMappings");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierTypeMappings_Suppliers_SupplierId",
                schema: "suppliers",
                table: "SupplierTypeMappings");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Suppliers",
                schema: "suppliers",
                table: "Suppliers");

            migrationBuilder.RenameTable(
                name: "Suppliers",
                schema: "suppliers",
                newName: "BusinessPartners",
                newSchema: "suppliers");

            // Rename-if-present, create-if-missing rather than a blind RenameIndex: finding F35
            // (see TenantIndexRepair) already found the shared database missing indexes that its
            // migration history claims were created — the same drift turned up here for
            // IX_Suppliers_UUID and IX_Suppliers_OrganizationId_SupplierCode. A database built
            // straight from the migrations (every LocalDB used to test this) still takes the
            // rename branch; only a drifted one falls through to create fresh.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('suppliers.BusinessPartners') AND name = N'IX_Suppliers_UUID')
                    EXEC sp_rename N'suppliers.BusinessPartners.IX_Suppliers_UUID', N'IX_BusinessPartners_UUID', 'INDEX';
                ELSE IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('suppliers.BusinessPartners') AND name = N'IX_BusinessPartners_UUID')
                    CREATE UNIQUE INDEX IX_BusinessPartners_UUID ON suppliers.BusinessPartners (UUID);

                IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('suppliers.BusinessPartners') AND name = N'IX_Suppliers_OrganizationId_SupplierCode')
                    EXEC sp_rename N'suppliers.BusinessPartners.IX_Suppliers_OrganizationId_SupplierCode', N'IX_BusinessPartners_OrganizationId_SupplierCode', 'INDEX';
                ELSE IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('suppliers.BusinessPartners') AND name = N'IX_BusinessPartners_OrganizationId_SupplierCode')
                    CREATE UNIQUE INDEX IX_BusinessPartners_OrganizationId_SupplierCode ON suppliers.BusinessPartners (OrganizationId, SupplierCode);

                IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('suppliers.BusinessPartners') AND name = N'IX_Suppliers_OrganizationId')
                    EXEC sp_rename N'suppliers.BusinessPartners.IX_Suppliers_OrganizationId', N'IX_BusinessPartners_OrganizationId', 'INDEX';
                ELSE IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('suppliers.BusinessPartners') AND name = N'IX_BusinessPartners_OrganizationId')
                    CREATE INDEX IX_BusinessPartners_OrganizationId ON suppliers.BusinessPartners (OrganizationId);
                """);

            migrationBuilder.AddPrimaryKey(
                name: "PK_BusinessPartners",
                schema: "suppliers",
                table: "BusinessPartners",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierBankDetails_BusinessPartners_SupplierId",
                schema: "suppliers",
                table: "SupplierBankDetails",
                column: "SupplierId",
                principalSchema: "suppliers",
                principalTable: "BusinessPartners",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierContacts_BusinessPartners_SupplierId",
                schema: "suppliers",
                table: "SupplierContacts",
                column: "SupplierId",
                principalSchema: "suppliers",
                principalTable: "BusinessPartners",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierDocuments_BusinessPartners_SupplierId",
                schema: "suppliers",
                table: "SupplierDocuments",
                column: "SupplierId",
                principalSchema: "suppliers",
                principalTable: "BusinessPartners",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierIndustryMappings_BusinessPartners_SupplierId",
                schema: "suppliers",
                table: "SupplierIndustryMappings",
                column: "SupplierId",
                principalSchema: "suppliers",
                principalTable: "BusinessPartners",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierTypeMappings_BusinessPartners_SupplierId",
                schema: "suppliers",
                table: "SupplierTypeMappings",
                column: "SupplierId",
                principalSchema: "suppliers",
                principalTable: "BusinessPartners",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SupplierBankDetails_BusinessPartners_SupplierId",
                schema: "suppliers",
                table: "SupplierBankDetails");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierContacts_BusinessPartners_SupplierId",
                schema: "suppliers",
                table: "SupplierContacts");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierDocuments_BusinessPartners_SupplierId",
                schema: "suppliers",
                table: "SupplierDocuments");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierIndustryMappings_BusinessPartners_SupplierId",
                schema: "suppliers",
                table: "SupplierIndustryMappings");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierTypeMappings_BusinessPartners_SupplierId",
                schema: "suppliers",
                table: "SupplierTypeMappings");

            migrationBuilder.DropPrimaryKey(
                name: "PK_BusinessPartners",
                schema: "suppliers",
                table: "BusinessPartners");

            migrationBuilder.RenameTable(
                name: "BusinessPartners",
                schema: "suppliers",
                newName: "Suppliers",
                newSchema: "suppliers");

            migrationBuilder.RenameIndex(
                name: "IX_BusinessPartners_UUID",
                schema: "suppliers",
                table: "Suppliers",
                newName: "IX_Suppliers_UUID");

            migrationBuilder.RenameIndex(
                name: "IX_BusinessPartners_OrganizationId_SupplierCode",
                schema: "suppliers",
                table: "Suppliers",
                newName: "IX_Suppliers_OrganizationId_SupplierCode");

            migrationBuilder.RenameIndex(
                name: "IX_BusinessPartners_OrganizationId",
                schema: "suppliers",
                table: "Suppliers",
                newName: "IX_Suppliers_OrganizationId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Suppliers",
                schema: "suppliers",
                table: "Suppliers",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierBankDetails_Suppliers_SupplierId",
                schema: "suppliers",
                table: "SupplierBankDetails",
                column: "SupplierId",
                principalSchema: "suppliers",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierContacts_Suppliers_SupplierId",
                schema: "suppliers",
                table: "SupplierContacts",
                column: "SupplierId",
                principalSchema: "suppliers",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierDocuments_Suppliers_SupplierId",
                schema: "suppliers",
                table: "SupplierDocuments",
                column: "SupplierId",
                principalSchema: "suppliers",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierIndustryMappings_Suppliers_SupplierId",
                schema: "suppliers",
                table: "SupplierIndustryMappings",
                column: "SupplierId",
                principalSchema: "suppliers",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierTypeMappings_Suppliers_SupplierId",
                schema: "suppliers",
                table: "SupplierTypeMappings",
                column: "SupplierId",
                principalSchema: "suppliers",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
