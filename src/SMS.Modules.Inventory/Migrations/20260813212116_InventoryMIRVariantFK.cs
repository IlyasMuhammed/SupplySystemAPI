using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class InventoryMIRVariantFK : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InventoryItems_Products_ProductId",
                schema: "inventory",
                table: "InventoryItems");

            migrationBuilder.DropForeignKey(
                name: "FK_InventoryLedgerEntries_Products_ProductId",
                schema: "inventory",
                table: "InventoryLedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_InventoryItems_ProductId_WarehouseId",
                schema: "inventory",
                table: "InventoryItems");

            migrationBuilder.RenameColumn(
                name: "ProductId",
                schema: "inventory",
                table: "StockAdjustments",
                newName: "VariantId");

            migrationBuilder.RenameColumn(
                name: "ProductId",
                schema: "inventory",
                table: "InventoryLedgerEntries",
                newName: "VariantId");

            migrationBuilder.RenameIndex(
                name: "IX_InventoryLedgerEntries_ProductId_WarehouseId_CreatedAt",
                schema: "inventory",
                table: "InventoryLedgerEntries",
                newName: "IX_InventoryLedgerEntries_VariantId_WarehouseId_CreatedAt");

            migrationBuilder.RenameColumn(
                name: "ProductId",
                schema: "inventory",
                table: "InventoryItems",
                newName: "VariantId");

            migrationBuilder.RenameIndex(
                name: "IX_InventoryItems_ProductId_WarehouseId_BatchNumber_SerialNumber",
                schema: "inventory",
                table: "InventoryItems",
                newName: "IX_InventoryItems_VariantId_WarehouseId_BatchNumber_SerialNumber");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_VariantId_WarehouseId",
                schema: "inventory",
                table: "InventoryItems",
                columns: new[] { "VariantId", "WarehouseId" },
                unique: true,
                filter: "[BatchNumber] IS NULL AND [SerialNumber] IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryItems_ProductVariants_VariantId",
                schema: "inventory",
                table: "InventoryItems",
                column: "VariantId",
                principalSchema: "inventory",
                principalTable: "ProductVariants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryLedgerEntries_ProductVariants_VariantId",
                schema: "inventory",
                table: "InventoryLedgerEntries",
                column: "VariantId",
                principalSchema: "inventory",
                principalTable: "ProductVariants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InventoryItems_ProductVariants_VariantId",
                schema: "inventory",
                table: "InventoryItems");

            migrationBuilder.DropForeignKey(
                name: "FK_InventoryLedgerEntries_ProductVariants_VariantId",
                schema: "inventory",
                table: "InventoryLedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_InventoryItems_VariantId_WarehouseId",
                schema: "inventory",
                table: "InventoryItems");

            migrationBuilder.RenameColumn(
                name: "VariantId",
                schema: "inventory",
                table: "StockAdjustments",
                newName: "ProductId");

            migrationBuilder.RenameColumn(
                name: "VariantId",
                schema: "inventory",
                table: "InventoryLedgerEntries",
                newName: "ProductId");

            migrationBuilder.RenameIndex(
                name: "IX_InventoryLedgerEntries_VariantId_WarehouseId_CreatedAt",
                schema: "inventory",
                table: "InventoryLedgerEntries",
                newName: "IX_InventoryLedgerEntries_ProductId_WarehouseId_CreatedAt");

            migrationBuilder.RenameColumn(
                name: "VariantId",
                schema: "inventory",
                table: "InventoryItems",
                newName: "ProductId");

            migrationBuilder.RenameIndex(
                name: "IX_InventoryItems_VariantId_WarehouseId_BatchNumber_SerialNumber",
                schema: "inventory",
                table: "InventoryItems",
                newName: "IX_InventoryItems_ProductId_WarehouseId_BatchNumber_SerialNumber");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_ProductId_WarehouseId",
                schema: "inventory",
                table: "InventoryItems",
                columns: new[] { "ProductId", "WarehouseId" });

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryItems_Products_ProductId",
                schema: "inventory",
                table: "InventoryItems",
                column: "ProductId",
                principalSchema: "inventory",
                principalTable: "Products",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryLedgerEntries_Products_ProductId",
                schema: "inventory",
                table: "InventoryLedgerEntries",
                column: "ProductId",
                principalSchema: "inventory",
                principalTable: "Products",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
