using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Material.Migrations
{
    /// <inheritdoc />
    public partial class AddQualityAndFgr : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "quality_inspections",
                schema: "material",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InspectionNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ProductionOrderId = table.Column<int>(type: "int", nullable: false),
                    InspectedQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    AcceptedQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    RejectedQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    HoldQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ReworkQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    OverallResult = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    InspectedBy = table.Column<int>(type: "int", nullable: false),
                    InspectedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quality_inspections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_quality_inspections_production_orders_ProductionOrderId",
                        column: x => x.ProductionOrderId,
                        principalSchema: "material",
                        principalTable: "production_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "finished_goods_receipts",
                schema: "material",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FgrNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ProductionOrderId = table.Column<int>(type: "int", nullable: false),
                    QualityInspectionId = table.Column<int>(type: "int", nullable: false),
                    WarehouseUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TotalQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ReceivedBy = table.Column<int>(type: "int", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_finished_goods_receipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_finished_goods_receipts_production_orders_ProductionOrderId",
                        column: x => x.ProductionOrderId,
                        principalSchema: "material",
                        principalTable: "production_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_finished_goods_receipts_quality_inspections_QualityInspectionId",
                        column: x => x.QualityInspectionId,
                        principalSchema: "material",
                        principalTable: "quality_inspections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "quality_inspection_lines",
                schema: "material",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InspectionId = table.Column<int>(type: "int", nullable: false),
                    CheckName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Result = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    QuantityChecked = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    DefectCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quality_inspection_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_quality_inspection_lines_quality_inspections_InspectionId",
                        column: x => x.InspectionId,
                        principalSchema: "material",
                        principalTable: "quality_inspections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_finished_goods_receipts_OrganizationId",
                schema: "material",
                table: "finished_goods_receipts",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_finished_goods_receipts_OrganizationId_FgrNumber",
                schema: "material",
                table: "finished_goods_receipts",
                columns: new[] { "OrganizationId", "FgrNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_finished_goods_receipts_ProductionOrderId_Status",
                schema: "material",
                table: "finished_goods_receipts",
                columns: new[] { "ProductionOrderId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_finished_goods_receipts_QualityInspectionId",
                schema: "material",
                table: "finished_goods_receipts",
                column: "QualityInspectionId");

            migrationBuilder.CreateIndex(
                name: "IX_finished_goods_receipts_UUID",
                schema: "material",
                table: "finished_goods_receipts",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_quality_inspection_lines_InspectionId",
                schema: "material",
                table: "quality_inspection_lines",
                column: "InspectionId");

            migrationBuilder.CreateIndex(
                name: "IX_quality_inspection_lines_OrganizationId",
                schema: "material",
                table: "quality_inspection_lines",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_quality_inspection_lines_UUID",
                schema: "material",
                table: "quality_inspection_lines",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_quality_inspections_OrganizationId",
                schema: "material",
                table: "quality_inspections",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_quality_inspections_OrganizationId_InspectionNumber",
                schema: "material",
                table: "quality_inspections",
                columns: new[] { "OrganizationId", "InspectionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_quality_inspections_ProductionOrderId",
                schema: "material",
                table: "quality_inspections",
                column: "ProductionOrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_quality_inspections_UUID",
                schema: "material",
                table: "quality_inspections",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "finished_goods_receipts",
                schema: "material");

            migrationBuilder.DropTable(
                name: "quality_inspection_lines",
                schema: "material");

            migrationBuilder.DropTable(
                name: "quality_inspections",
                schema: "material");
        }
    }
}
