using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class AddVariantSupplierAndRateHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VariantSuppliers",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VariantId = table.Column<int>(type: "int", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VendorUnitCost = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    LeadTimeDays = table.Column<int>(type: "int", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CurrencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MinOrderValue = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    DiscountTiers = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    QuotationRef = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LastReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastReviewedBy = table.Column<int>(type: "int", nullable: true),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VariantSuppliers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VariantSuppliers_ProductVariants_VariantId",
                        column: x => x.VariantId,
                        principalSchema: "inventory",
                        principalTable: "ProductVariants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SupplierRateHistory",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VariantSupplierId = table.Column<int>(type: "int", nullable: false),
                    FieldChanged = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OldValue = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ChangeReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ChangedBy = table.Column<int>(type: "int", nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierRateHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierRateHistory_VariantSuppliers_VariantSupplierId",
                        column: x => x.VariantSupplierId,
                        principalSchema: "inventory",
                        principalTable: "VariantSuppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierRateHistory_VariantSupplierId",
                schema: "inventory",
                table: "SupplierRateHistory",
                column: "VariantSupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_VariantSuppliers_OrganizationId_VariantId_SupplierId",
                schema: "inventory",
                table: "VariantSuppliers",
                columns: new[] { "OrganizationId", "VariantId", "SupplierId" });

            migrationBuilder.CreateIndex(
                name: "IX_VariantSuppliers_Uuid",
                schema: "inventory",
                table: "VariantSuppliers",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VariantSuppliers_VariantId",
                schema: "inventory",
                table: "VariantSuppliers",
                column: "VariantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupplierRateHistory",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "VariantSuppliers",
                schema: "inventory");
        }
    }
}
