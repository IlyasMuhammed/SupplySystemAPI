using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceAgnosticStockReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StockReservations",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InventoryItemId = table.Column<int>(type: "int", nullable: false),
                    VariantUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseId = table.Column<int>(type: "int", nullable: false),
                    ReservedQty = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    SourceType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SourceUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceLineUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsFlagged = table.Column<bool>(type: "bit", nullable: false),
                    FlaggedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReservedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReservedBy = table.Column<int>(type: "int", nullable: false),
                    ReleasedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReleasedBy = table.Column<int>(type: "int", nullable: true),
                    ReleaseReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockReservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StockReservations_InventoryItems_InventoryItemId",
                        column: x => x.InventoryItemId,
                        principalSchema: "inventory",
                        principalTable: "InventoryItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_InventoryItemId_Status",
                schema: "inventory",
                table: "StockReservations",
                columns: new[] { "InventoryItemId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_OrganizationId_SourceType_SourceUuid",
                schema: "inventory",
                table: "StockReservations",
                columns: new[] { "OrganizationId", "SourceType", "SourceUuid" });

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_UUID",
                schema: "inventory",
                table: "StockReservations",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StockReservations",
                schema: "inventory");
        }
    }
}
