using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class AddBulkRateOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BulkOperationId",
                schema: "inventory",
                table: "SupplierRateHistory",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BulkRateOperations",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    AffectedCount = table.Column<int>(type: "int", nullable: false),
                    TotalImpactAmount = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ChangeReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    PerformedBy = table.Column<int>(type: "int", nullable: false),
                    PerformedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsUndone = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BulkRateOperations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierRateHistory_BulkOperationId",
                schema: "inventory",
                table: "SupplierRateHistory",
                column: "BulkOperationId");

            migrationBuilder.CreateIndex(
                name: "IX_BulkRateOperations_OrganizationId_PerformedAt",
                schema: "inventory",
                table: "BulkRateOperations",
                columns: new[] { "OrganizationId", "PerformedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BulkRateOperations_Uuid",
                schema: "inventory",
                table: "BulkRateOperations",
                column: "Uuid",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierRateHistory_BulkRateOperations_BulkOperationId",
                schema: "inventory",
                table: "SupplierRateHistory",
                column: "BulkOperationId",
                principalSchema: "inventory",
                principalTable: "BulkRateOperations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SupplierRateHistory_BulkRateOperations_BulkOperationId",
                schema: "inventory",
                table: "SupplierRateHistory");

            migrationBuilder.DropTable(
                name: "BulkRateOperations",
                schema: "inventory");

            migrationBuilder.DropIndex(
                name: "IX_SupplierRateHistory_BulkOperationId",
                schema: "inventory",
                table: "SupplierRateHistory");

            migrationBuilder.DropColumn(
                name: "BulkOperationId",
                schema: "inventory",
                table: "SupplierRateHistory");
        }
    }
}
