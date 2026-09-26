using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Material.Migrations
{
    /// <inheritdoc />
    public partial class AddProductionCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "production_orders",
                schema: "material",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TraceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductionNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ProductUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductVariantUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BomId = table.Column<int>(type: "int", nullable: false),
                    BomVersion = table.Column<int>(type: "int", nullable: false),
                    PlannedQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ProducedQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    AcceptedQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    RejectedQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ScrappedQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    WarehouseUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OutputWarehouseUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SourceUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceLineUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceReference = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ParentProductionOrderId = table.Column<int>(type: "int", nullable: true),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    RequiredDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PlannedStartDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ActualStartDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ActualEndDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    MaterialReadiness = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_orders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_production_orders_bill_of_materials_BomId",
                        column: x => x.BomId,
                        principalSchema: "material",
                        principalTable: "bill_of_materials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_orders_production_orders_ParentProductionOrderId",
                        column: x => x.ParentProductionOrderId,
                        principalSchema: "material",
                        principalTable: "production_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supply_requirements",
                schema: "material",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TraceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplyNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ProductUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VariantUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuantityRequired = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    QuantityOrdered = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    QuantityReceived = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    DemandSourceType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DemandSourceUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DemandReference = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    SupplyMethod = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SupplySourceType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    SupplySourceUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SupplySourceLineUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SupplySourceReference = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    WarehouseUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequiredDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supply_requirements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "production_material_issues",
                schema: "material",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IssueNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ProductionOrderId = table.Column<int>(type: "int", nullable: false),
                    WarehouseUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IssueType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConfirmedBy = table.Column<int>(type: "int", nullable: true),
                    ConfirmedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReversedBy = table.Column<int>(type: "int", nullable: true),
                    ReversedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_material_issues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_production_material_issues_production_orders_ProductionOrderId",
                        column: x => x.ProductionOrderId,
                        principalSchema: "material",
                        principalTable: "production_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_material_requirements",
                schema: "material",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductionOrderId = table.Column<int>(type: "int", nullable: false),
                    BomLineId = table.Column<int>(type: "int", nullable: true),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    MaterialProductUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MaterialVariantUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NetQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ScrapAllowance = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    RequiredQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ReservedQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    PlannedQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    IssuedQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ReturnedQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    WastageQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ConsumedQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ShortageQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    Uom = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    WarehouseUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsCritical = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    RequiredDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AllocationDemandUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_material_requirements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_production_material_requirements_production_orders_ProductionOrderId",
                        column: x => x.ProductionOrderId,
                        principalSchema: "material",
                        principalTable: "production_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "production_material_issue_lines",
                schema: "material",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IssueId = table.Column<int>(type: "int", nullable: false),
                    RequirementId = table.Column<int>(type: "int", nullable: false),
                    MaterialVariantUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    Uom = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    UnitCost = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    BatchNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_material_issue_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_production_material_issue_lines_production_material_issues_IssueId",
                        column: x => x.IssueId,
                        principalSchema: "material",
                        principalTable: "production_material_issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_production_material_issue_lines_production_material_requirements_RequirementId",
                        column: x => x.RequirementId,
                        principalSchema: "material",
                        principalTable: "production_material_requirements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_production_material_issue_lines_IssueId",
                schema: "material",
                table: "production_material_issue_lines",
                column: "IssueId");

            migrationBuilder.CreateIndex(
                name: "IX_production_material_issue_lines_OrganizationId",
                schema: "material",
                table: "production_material_issue_lines",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_production_material_issue_lines_RequirementId",
                schema: "material",
                table: "production_material_issue_lines",
                column: "RequirementId");

            migrationBuilder.CreateIndex(
                name: "IX_production_material_issue_lines_UUID",
                schema: "material",
                table: "production_material_issue_lines",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_material_issues_OrganizationId",
                schema: "material",
                table: "production_material_issues",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_production_material_issues_OrganizationId_IssueNumber",
                schema: "material",
                table: "production_material_issues",
                columns: new[] { "OrganizationId", "IssueNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_material_issues_ProductionOrderId_Status",
                schema: "material",
                table: "production_material_issues",
                columns: new[] { "ProductionOrderId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_production_material_issues_UUID",
                schema: "material",
                table: "production_material_issues",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_material_requirements_AllocationDemandUuid",
                schema: "material",
                table: "production_material_requirements",
                column: "AllocationDemandUuid");

            migrationBuilder.CreateIndex(
                name: "IX_production_material_requirements_OrganizationId",
                schema: "material",
                table: "production_material_requirements",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_production_material_requirements_OrganizationId_MaterialVariantUuid_WarehouseUuid_ShortageQuantity",
                schema: "material",
                table: "production_material_requirements",
                columns: new[] { "OrganizationId", "MaterialVariantUuid", "WarehouseUuid", "ShortageQuantity" });

            migrationBuilder.CreateIndex(
                name: "IX_production_material_requirements_ProductionOrderId_Status",
                schema: "material",
                table: "production_material_requirements",
                columns: new[] { "ProductionOrderId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_production_material_requirements_UUID",
                schema: "material",
                table: "production_material_requirements",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_orders_BomId",
                schema: "material",
                table: "production_orders",
                column: "BomId");

            migrationBuilder.CreateIndex(
                name: "IX_production_orders_OrganizationId",
                schema: "material",
                table: "production_orders",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_production_orders_OrganizationId_ProductionNumber",
                schema: "material",
                table: "production_orders",
                columns: new[] { "OrganizationId", "ProductionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_orders_OrganizationId_ProductUuid",
                schema: "material",
                table: "production_orders",
                columns: new[] { "OrganizationId", "ProductUuid" });

            migrationBuilder.CreateIndex(
                name: "IX_production_orders_OrganizationId_SourceType_SourceUuid",
                schema: "material",
                table: "production_orders",
                columns: new[] { "OrganizationId", "SourceType", "SourceUuid" });

            migrationBuilder.CreateIndex(
                name: "IX_production_orders_OrganizationId_Status_RequiredDate",
                schema: "material",
                table: "production_orders",
                columns: new[] { "OrganizationId", "Status", "RequiredDate" });

            migrationBuilder.CreateIndex(
                name: "IX_production_orders_ParentProductionOrderId",
                schema: "material",
                table: "production_orders",
                column: "ParentProductionOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_production_orders_TraceId",
                schema: "material",
                table: "production_orders",
                column: "TraceId");

            migrationBuilder.CreateIndex(
                name: "IX_production_orders_UUID",
                schema: "material",
                table: "production_orders",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supply_requirements_OrganizationId",
                schema: "material",
                table: "supply_requirements",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_supply_requirements_OrganizationId_DemandSourceType_DemandSourceUuid",
                schema: "material",
                table: "supply_requirements",
                columns: new[] { "OrganizationId", "DemandSourceType", "DemandSourceUuid" });

            migrationBuilder.CreateIndex(
                name: "IX_supply_requirements_OrganizationId_SupplyNumber",
                schema: "material",
                table: "supply_requirements",
                columns: new[] { "OrganizationId", "SupplyNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supply_requirements_OrganizationId_VariantUuid_Status",
                schema: "material",
                table: "supply_requirements",
                columns: new[] { "OrganizationId", "VariantUuid", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_supply_requirements_SupplySourceLineUuid",
                schema: "material",
                table: "supply_requirements",
                column: "SupplySourceLineUuid");

            migrationBuilder.CreateIndex(
                name: "IX_supply_requirements_TraceId",
                schema: "material",
                table: "supply_requirements",
                column: "TraceId");

            migrationBuilder.CreateIndex(
                name: "IX_supply_requirements_UUID",
                schema: "material",
                table: "supply_requirements",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "production_material_issue_lines",
                schema: "material");

            migrationBuilder.DropTable(
                name: "supply_requirements",
                schema: "material");

            migrationBuilder.DropTable(
                name: "production_material_issues",
                schema: "material");

            migrationBuilder.DropTable(
                name: "production_material_requirements",
                schema: "material");

            migrationBuilder.DropTable(
                name: "production_orders",
                schema: "material");
        }
    }
}
