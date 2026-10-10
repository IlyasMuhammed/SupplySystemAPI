using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Material.Migrations
{
    /// <inheritdoc />
    public partial class A36_ServiceOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "service_orders",
                schema: "material",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TraceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServiceNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ServiceProductUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServiceVariantUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BomId = table.Column<int>(type: "int", nullable: true),
                    BomVersion = table.Column<int>(type: "int", nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    WarehouseUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedUserId = table.Column<int>(type: "int", nullable: true),
                    AssignedRoleId = table.Column<int>(type: "int", nullable: true),
                    EstimatedHours = table.Column<decimal>(type: "decimal(8,2)", nullable: true),
                    ActualHours = table.Column<decimal>(type: "decimal(8,2)", nullable: true),
                    ScheduledDate = table.Column<DateTime>(type: "date", nullable: true),
                    ScheduledTime = table.Column<TimeSpan>(type: "time(0)", nullable: true),
                    ActualStartDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ActualEndDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SourceType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SourceUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceLineUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceReference = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    InvoicingPolicy = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    BillingModel = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    MaterialReadiness = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CompletionNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CustomerSignature = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_orders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_service_orders_bill_of_materials_BomId",
                        column: x => x.BomId,
                        principalSchema: "material",
                        principalTable: "bill_of_materials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service_ledger_entries",
                schema: "material",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TraceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServiceOrderId = table.Column<int>(type: "int", nullable: false),
                    ServiceNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EntryType = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ProductUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VariantUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ProductType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    Uom = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    WarehouseUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceDocumentType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SourceDocumentUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceDocumentNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    MovementType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    TransactionDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_ledger_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_service_ledger_entries_service_orders_ServiceOrderId",
                        column: x => x.ServiceOrderId,
                        principalSchema: "material",
                        principalTable: "service_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service_material_issues",
                schema: "material",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IssueNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ServiceOrderId = table.Column<int>(type: "int", nullable: false),
                    WarehouseUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IssueType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_material_issues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_service_material_issues_service_orders_ServiceOrderId",
                        column: x => x.ServiceOrderId,
                        principalSchema: "material",
                        principalTable: "service_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service_material_requirements",
                schema: "material",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServiceOrderId = table.Column<int>(type: "int", nullable: false),
                    BomLineId = table.Column<int>(type: "int", nullable: true),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    SourceType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    MaterialProductUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MaterialVariantUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NetQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ScrapAllowance = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    RequiredQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ReservedQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    IssuedQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ConsumedQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ReturnedQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ShortageQuantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    Uom = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    WarehouseUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsCritical = table.Column<bool>(type: "bit", nullable: false),
                    IsAdhoc = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    RequiredDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AddedBy = table.Column<int>(type: "int", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SubcontractSupplierUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AllocationDemandUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_material_requirements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_service_material_requirements_service_orders_ServiceOrderId",
                        column: x => x.ServiceOrderId,
                        principalSchema: "material",
                        principalTable: "service_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "service_material_issue_lines",
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
                    UnitCost = table.Column<decimal>(type: "decimal(18,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_material_issue_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_service_material_issue_lines_service_material_issues_IssueId",
                        column: x => x.IssueId,
                        principalSchema: "material",
                        principalTable: "service_material_issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_service_material_issue_lines_service_material_requirements_RequirementId",
                        column: x => x.RequirementId,
                        principalSchema: "material",
                        principalTable: "service_material_requirements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_service_ledger_entries_OrganizationId",
                schema: "material",
                table: "service_ledger_entries",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_service_ledger_entries_OrganizationId_ProductUuid_EntryType_TransactionDate",
                schema: "material",
                table: "service_ledger_entries",
                columns: new[] { "OrganizationId", "ProductUuid", "EntryType", "TransactionDate" });

            migrationBuilder.CreateIndex(
                name: "IX_service_ledger_entries_ServiceOrderId_EntryType_TransactionDate",
                schema: "material",
                table: "service_ledger_entries",
                columns: new[] { "ServiceOrderId", "EntryType", "TransactionDate" });

            migrationBuilder.CreateIndex(
                name: "IX_service_material_issue_lines_IssueId",
                schema: "material",
                table: "service_material_issue_lines",
                column: "IssueId");

            migrationBuilder.CreateIndex(
                name: "IX_service_material_issue_lines_OrganizationId",
                schema: "material",
                table: "service_material_issue_lines",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_service_material_issue_lines_RequirementId",
                schema: "material",
                table: "service_material_issue_lines",
                column: "RequirementId");

            migrationBuilder.CreateIndex(
                name: "IX_service_material_issue_lines_UUID",
                schema: "material",
                table: "service_material_issue_lines",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_service_material_issues_OrganizationId",
                schema: "material",
                table: "service_material_issues",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_service_material_issues_OrganizationId_IssueNumber",
                schema: "material",
                table: "service_material_issues",
                columns: new[] { "OrganizationId", "IssueNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_service_material_issues_ServiceOrderId_IssueType",
                schema: "material",
                table: "service_material_issues",
                columns: new[] { "ServiceOrderId", "IssueType" });

            migrationBuilder.CreateIndex(
                name: "IX_service_material_issues_UUID",
                schema: "material",
                table: "service_material_issues",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_service_material_requirements_AllocationDemandUuid",
                schema: "material",
                table: "service_material_requirements",
                column: "AllocationDemandUuid");

            migrationBuilder.CreateIndex(
                name: "IX_service_material_requirements_MaterialProductUuid_MaterialVariantUuid",
                schema: "material",
                table: "service_material_requirements",
                columns: new[] { "MaterialProductUuid", "MaterialVariantUuid" });

            migrationBuilder.CreateIndex(
                name: "IX_service_material_requirements_OrganizationId",
                schema: "material",
                table: "service_material_requirements",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_service_material_requirements_OrganizationId_MaterialVariantUuid_WarehouseUuid_ShortageQuantity",
                schema: "material",
                table: "service_material_requirements",
                columns: new[] { "OrganizationId", "MaterialVariantUuid", "WarehouseUuid", "ShortageQuantity" },
                filter: "[ShortageQuantity] > 0");

            migrationBuilder.CreateIndex(
                name: "IX_service_material_requirements_ServiceOrderId_Status",
                schema: "material",
                table: "service_material_requirements",
                columns: new[] { "ServiceOrderId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_service_material_requirements_UUID",
                schema: "material",
                table: "service_material_requirements",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_service_orders_AssignedUserId",
                schema: "material",
                table: "service_orders",
                column: "AssignedUserId",
                filter: "[AssignedUserId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_service_orders_BomId",
                schema: "material",
                table: "service_orders",
                column: "BomId");

            migrationBuilder.CreateIndex(
                name: "IX_service_orders_OrganizationId",
                schema: "material",
                table: "service_orders",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_service_orders_OrganizationId_CustomerUuid",
                schema: "material",
                table: "service_orders",
                columns: new[] { "OrganizationId", "CustomerUuid" });

            migrationBuilder.CreateIndex(
                name: "IX_service_orders_OrganizationId_ServiceNumber",
                schema: "material",
                table: "service_orders",
                columns: new[] { "OrganizationId", "ServiceNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_service_orders_OrganizationId_ServiceProductUuid",
                schema: "material",
                table: "service_orders",
                columns: new[] { "OrganizationId", "ServiceProductUuid" });

            migrationBuilder.CreateIndex(
                name: "IX_service_orders_OrganizationId_SourceType_SourceUuid",
                schema: "material",
                table: "service_orders",
                columns: new[] { "OrganizationId", "SourceType", "SourceUuid" });

            migrationBuilder.CreateIndex(
                name: "IX_service_orders_OrganizationId_Status_ScheduledDate",
                schema: "material",
                table: "service_orders",
                columns: new[] { "OrganizationId", "Status", "ScheduledDate" });

            migrationBuilder.CreateIndex(
                name: "IX_service_orders_TraceId",
                schema: "material",
                table: "service_orders",
                column: "TraceId");

            migrationBuilder.CreateIndex(
                name: "IX_service_orders_UUID",
                schema: "material",
                table: "service_orders",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "service_ledger_entries",
                schema: "material");

            migrationBuilder.DropTable(
                name: "service_material_issue_lines",
                schema: "material");

            migrationBuilder.DropTable(
                name: "service_material_issues",
                schema: "material");

            migrationBuilder.DropTable(
                name: "service_material_requirements",
                schema: "material");

            migrationBuilder.DropTable(
                name: "service_orders",
                schema: "material");
        }
    }
}
