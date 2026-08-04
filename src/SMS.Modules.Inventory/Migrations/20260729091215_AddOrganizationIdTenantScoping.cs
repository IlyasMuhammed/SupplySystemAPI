using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationIdTenantScoping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Warehouses_Code",
                schema: "inventory",
                table: "Warehouses");

            migrationBuilder.DropIndex(
                name: "IX_StockAdjustments_AdjNumber",
                schema: "inventory",
                table: "StockAdjustments");

            migrationBuilder.DropIndex(
                name: "IX_Products_Sku",
                schema: "inventory",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_ProductCategories_Code",
                schema: "inventory",
                table: "ProductCategories");

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "inventory",
                table: "Zones",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "inventory",
                table: "Warehouses",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "inventory",
                table: "StockAdjustments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "inventory",
                table: "Shelves",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "inventory",
                table: "Racks",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "inventory",
                table: "ProductSubCategories",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "inventory",
                table: "Products",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "inventory",
                table: "ProductCategories",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "inventory",
                table: "InventoryLedgerEntries",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "inventory",
                table: "InventoryItems",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "inventory",
                table: "Bins",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.CreateIndex(
                name: "IX_Zones_OrganizationId",
                schema: "inventory",
                table: "Zones",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Warehouses_OrganizationId",
                schema: "inventory",
                table: "Warehouses",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Warehouses_OrganizationId_Code",
                schema: "inventory",
                table: "Warehouses",
                columns: new[] { "OrganizationId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustments_OrganizationId",
                schema: "inventory",
                table: "StockAdjustments",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustments_OrganizationId_AdjNumber",
                schema: "inventory",
                table: "StockAdjustments",
                columns: new[] { "OrganizationId", "AdjNumber" },
                unique: true,
                filter: "[AdjNumber] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Shelves_OrganizationId",
                schema: "inventory",
                table: "Shelves",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Racks_OrganizationId",
                schema: "inventory",
                table: "Racks",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductSubCategories_OrganizationId",
                schema: "inventory",
                table: "ProductSubCategories",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_OrganizationId",
                schema: "inventory",
                table: "Products",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_OrganizationId_Sku",
                schema: "inventory",
                table: "Products",
                columns: new[] { "OrganizationId", "Sku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductCategories_OrganizationId",
                schema: "inventory",
                table: "ProductCategories",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductCategories_OrganizationId_Code",
                schema: "inventory",
                table: "ProductCategories",
                columns: new[] { "OrganizationId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLedgerEntries_OrganizationId",
                schema: "inventory",
                table: "InventoryLedgerEntries",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_OrganizationId",
                schema: "inventory",
                table: "InventoryItems",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Bins_OrganizationId",
                schema: "inventory",
                table: "Bins",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Zones_OrganizationId",
                schema: "inventory",
                table: "Zones");

            migrationBuilder.DropIndex(
                name: "IX_Warehouses_OrganizationId",
                schema: "inventory",
                table: "Warehouses");

            migrationBuilder.DropIndex(
                name: "IX_Warehouses_OrganizationId_Code",
                schema: "inventory",
                table: "Warehouses");

            migrationBuilder.DropIndex(
                name: "IX_StockAdjustments_OrganizationId",
                schema: "inventory",
                table: "StockAdjustments");

            migrationBuilder.DropIndex(
                name: "IX_StockAdjustments_OrganizationId_AdjNumber",
                schema: "inventory",
                table: "StockAdjustments");

            migrationBuilder.DropIndex(
                name: "IX_Shelves_OrganizationId",
                schema: "inventory",
                table: "Shelves");

            migrationBuilder.DropIndex(
                name: "IX_Racks_OrganizationId",
                schema: "inventory",
                table: "Racks");

            migrationBuilder.DropIndex(
                name: "IX_ProductSubCategories_OrganizationId",
                schema: "inventory",
                table: "ProductSubCategories");

            migrationBuilder.DropIndex(
                name: "IX_Products_OrganizationId",
                schema: "inventory",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_OrganizationId_Sku",
                schema: "inventory",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_ProductCategories_OrganizationId",
                schema: "inventory",
                table: "ProductCategories");

            migrationBuilder.DropIndex(
                name: "IX_ProductCategories_OrganizationId_Code",
                schema: "inventory",
                table: "ProductCategories");

            migrationBuilder.DropIndex(
                name: "IX_InventoryLedgerEntries_OrganizationId",
                schema: "inventory",
                table: "InventoryLedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_InventoryItems_OrganizationId",
                schema: "inventory",
                table: "InventoryItems");

            migrationBuilder.DropIndex(
                name: "IX_Bins_OrganizationId",
                schema: "inventory",
                table: "Bins");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "inventory",
                table: "Zones");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "inventory",
                table: "Warehouses");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "inventory",
                table: "StockAdjustments");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "inventory",
                table: "Shelves");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "inventory",
                table: "Racks");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "inventory",
                table: "ProductSubCategories");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "inventory",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "inventory",
                table: "ProductCategories");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "inventory",
                table: "InventoryLedgerEntries");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "inventory",
                table: "InventoryItems");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "inventory",
                table: "Bins");

            migrationBuilder.CreateIndex(
                name: "IX_Warehouses_Code",
                schema: "inventory",
                table: "Warehouses",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustments_AdjNumber",
                schema: "inventory",
                table: "StockAdjustments",
                column: "AdjNumber",
                unique: true,
                filter: "[AdjNumber] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Products_Sku",
                schema: "inventory",
                table: "Products",
                column: "Sku",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductCategories_Code",
                schema: "inventory",
                table: "ProductCategories",
                column: "Code",
                unique: true);
        }
    }
}
