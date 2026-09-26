using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Material.Migrations
{
    /// <inheritdoc />
    public partial class AddBillOfMaterials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bill_of_materials",
                schema: "material",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TraceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BomNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ProductUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductVariantUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    BaseQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    BaseUom = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    WarehouseUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SubmittedBy = table.Column<int>(type: "int", nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ApprovedBy = table.Column<int>(type: "int", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RejectedBy = table.Column<int>(type: "int", nullable: true),
                    RejectedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RejectionReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ActivatedBy = table.Column<int>(type: "int", nullable: true),
                    ActivatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ObsoletedBy = table.Column<int>(type: "int", nullable: true),
                    ObsoletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bill_of_materials", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "bill_of_material_lines",
                schema: "material",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BomId = table.Column<int>(type: "int", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    MaterialVariantUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MaterialProductUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    Uom = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ScrapPercentage = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    IsCritical = table.Column<bool>(type: "bit", nullable: false),
                    AlternateVariantUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    WarehouseUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bill_of_material_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_bill_of_material_lines_bill_of_materials_BomId",
                        column: x => x.BomId,
                        principalSchema: "material",
                        principalTable: "bill_of_materials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bill_of_material_lines_BomId_Sequence",
                schema: "material",
                table: "bill_of_material_lines",
                columns: new[] { "BomId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_bill_of_material_lines_OrganizationId",
                schema: "material",
                table: "bill_of_material_lines",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_bill_of_material_lines_OrganizationId_MaterialProductUuid",
                schema: "material",
                table: "bill_of_material_lines",
                columns: new[] { "OrganizationId", "MaterialProductUuid" });

            migrationBuilder.CreateIndex(
                name: "IX_bill_of_material_lines_UUID",
                schema: "material",
                table: "bill_of_material_lines",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bill_of_materials_OrganizationId",
                schema: "material",
                table: "bill_of_materials",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_bill_of_materials_OrganizationId_BomNumber",
                schema: "material",
                table: "bill_of_materials",
                columns: new[] { "OrganizationId", "BomNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bill_of_materials_OrganizationId_ProductUuid_ProductVariantUuid_Version",
                schema: "material",
                table: "bill_of_materials",
                columns: new[] { "OrganizationId", "ProductUuid", "ProductVariantUuid", "Version" },
                unique: true,
                filter: "[ProductVariantUuid] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_bill_of_materials_OrganizationId_ProductUuid_Status",
                schema: "material",
                table: "bill_of_materials",
                columns: new[] { "OrganizationId", "ProductUuid", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_bill_of_materials_TraceId",
                schema: "material",
                table: "bill_of_materials",
                column: "TraceId");

            migrationBuilder.CreateIndex(
                name: "IX_bill_of_materials_UUID",
                schema: "material",
                table: "bill_of_materials",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bill_of_material_lines",
                schema: "material");

            migrationBuilder.DropTable(
                name: "bill_of_materials",
                schema: "material");
        }
    }
}
