using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class AddAllocationEngineTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AllocationDemands",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DemandType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DemandUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DemandLineUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Reference = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    VariantUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RequiredQty = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ConsumedQty = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    RequiredDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DocumentDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AllocationDemands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AllocationRules",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuleName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PriorityOrder = table.Column<int>(type: "int", nullable: false),
                    DemandTypeFilter = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    SortField = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SortDirection = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AllocationRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AllocationSupplies",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplyType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SupplyUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplyLineUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Reference = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    VariantUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpectedQty = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ReceivedQty = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ExpectedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AllocationSupplies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AllocationRecords",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DemandId = table.Column<int>(type: "int", nullable: false),
                    VariantUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AllocatedQty = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ConsumedQty = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    SupplyType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SupplyId = table.Column<int>(type: "int", nullable: true),
                    AllocationType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PriorityScore = table.Column<int>(type: "int", nullable: false),
                    RequiredDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AllocatedBy = table.Column<int>(type: "int", nullable: false),
                    AllocatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReleasedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReleaseReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AllocationRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AllocationRecords_AllocationDemands_DemandId",
                        column: x => x.DemandId,
                        principalSchema: "inventory",
                        principalTable: "AllocationDemands",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AllocationRecords_AllocationSupplies_SupplyId",
                        column: x => x.SupplyId,
                        principalSchema: "inventory",
                        principalTable: "AllocationSupplies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AllocationDemands_OrganizationId",
                schema: "inventory",
                table: "AllocationDemands",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_AllocationDemands_OrganizationId_DemandType_DemandUuid_DemandLineUuid",
                schema: "inventory",
                table: "AllocationDemands",
                columns: new[] { "OrganizationId", "DemandType", "DemandUuid", "DemandLineUuid" });

            migrationBuilder.CreateIndex(
                name: "IX_AllocationDemands_OrganizationId_VariantUuid_Status",
                schema: "inventory",
                table: "AllocationDemands",
                columns: new[] { "OrganizationId", "VariantUuid", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AllocationDemands_Uuid",
                schema: "inventory",
                table: "AllocationDemands",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AllocationRecords_DemandId_Status",
                schema: "inventory",
                table: "AllocationRecords",
                columns: new[] { "DemandId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AllocationRecords_OrganizationId",
                schema: "inventory",
                table: "AllocationRecords",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_AllocationRecords_OrganizationId_VariantUuid_WarehouseUuid_Status",
                schema: "inventory",
                table: "AllocationRecords",
                columns: new[] { "OrganizationId", "VariantUuid", "WarehouseUuid", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AllocationRecords_SupplyId",
                schema: "inventory",
                table: "AllocationRecords",
                column: "SupplyId");

            migrationBuilder.CreateIndex(
                name: "IX_AllocationRecords_Uuid",
                schema: "inventory",
                table: "AllocationRecords",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AllocationRules_OrganizationId_PriorityOrder",
                schema: "inventory",
                table: "AllocationRules",
                columns: new[] { "OrganizationId", "PriorityOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_AllocationRules_Uuid",
                schema: "inventory",
                table: "AllocationRules",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AllocationSupplies_OrganizationId",
                schema: "inventory",
                table: "AllocationSupplies",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_AllocationSupplies_OrganizationId_SupplyType_SupplyUuid_SupplyLineUuid",
                schema: "inventory",
                table: "AllocationSupplies",
                columns: new[] { "OrganizationId", "SupplyType", "SupplyUuid", "SupplyLineUuid" });

            migrationBuilder.CreateIndex(
                name: "IX_AllocationSupplies_OrganizationId_VariantUuid_Status",
                schema: "inventory",
                table: "AllocationSupplies",
                columns: new[] { "OrganizationId", "VariantUuid", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AllocationSupplies_Uuid",
                schema: "inventory",
                table: "AllocationSupplies",
                column: "Uuid",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AllocationRecords",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "AllocationRules",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "AllocationDemands",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "AllocationSupplies",
                schema: "inventory");
        }
    }
}
