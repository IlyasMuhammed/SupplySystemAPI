using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class ProductVariantSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // FSD Addendum 26 (PV-001) — project is still in development, no production data.
            // Every transactional table that references product_id (directly or transitively)
            // is wiped clean here so the variant-first model starts from zero, rather than trying
            // to backfill variant_id onto historical rows that predate variants entirely.
            // Order is strict child-before-parent across 5 modules' real DB-enforced FKs (verified
            // against every IEntityTypeConfiguration in the solution, not just the FSD's own
            // abridged table list — 4 tables below aren't in the FSD text but have real FKs into
            // the listed tables and would block the deletes otherwise: payments, StockAdjustments,
            // supplier_return_orders, supplier_return_order_lines).
            migrationBuilder.Sql(@"
                -- Material module
                DELETE FROM material.miv_line_batch_serials;
                DELETE FROM material.material_return_detail;
                DELETE FROM material.wastage;
                DELETE FROM material.material_consumption;
                DELETE FROM material.material_return;
                DELETE FROM material.material_issue_voucher_lines;
                DELETE FROM material.material_issue_vouchers;
                DELETE FROM material.project_cost_ledger;
                DELETE FROM material.department_cost_ledger;
                DELETE FROM material.stock_reservations;
                DELETE FROM material.mir_line_approvals;
                DELETE FROM material.material_issue_request_lines;
                DELETE FROM material.material_issue_requests;

                -- Finance module
                DELETE FROM finance.payments;
                DELETE FROM finance.supplier_payment_lines;
                DELETE FROM finance.supplier_payments;
                DELETE FROM finance.invoice_lines;
                DELETE FROM finance.invoices;
                DELETE FROM finance.supplier_ledger_entries;
                DELETE FROM finance.master_product_ledger;
                DELETE FROM finance.master_financial_ledger;

                -- Warehouse module
                DELETE FROM warehouse.supplier_return_order_lines;
                DELETE FROM warehouse.supplier_return_orders;
                DELETE FROM warehouse.grn_lines;
                DELETE FROM warehouse.grns;

                -- Demand module
                DELETE FROM demand.purchase_order_pr_links;
                DELETE FROM demand.purchase_order_lines;
                DELETE FROM demand.purchase_orders;

                -- Inventory module (Products itself last — everything above references it)
                DELETE FROM inventory.StockAdjustments;
                DELETE FROM inventory.InventoryLedgerEntries;
                DELETE FROM inventory.InventoryItems;
                DELETE FROM inventory.Products;
            ");

            migrationBuilder.DropColumn(
                name: "Barcode",
                schema: "inventory",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "LastPurchasePrice",
                schema: "inventory",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "UnitCost",
                schema: "inventory",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "UnitPrice",
                schema: "inventory",
                table: "Products");

            migrationBuilder.CreateTable(
                name: "ProductVariants",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    VariantName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Barcode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    PurchasePrice = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    SellingPrice = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    LastPurchasePrice = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    WeightKg = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    Dimensions = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    ReorderPoint = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductVariants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductVariants_Products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "inventory",
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProductVariants_OrganizationId_Barcode",
                schema: "inventory",
                table: "ProductVariants",
                columns: new[] { "OrganizationId", "Barcode" },
                unique: true,
                filter: "[Barcode] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProductVariants_OrganizationId_Sku",
                schema: "inventory",
                table: "ProductVariants",
                columns: new[] { "OrganizationId", "Sku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductVariants_ProductId",
                schema: "inventory",
                table: "ProductVariants",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductVariants_Uuid",
                schema: "inventory",
                table: "ProductVariants",
                column: "Uuid",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductVariants",
                schema: "inventory");

            migrationBuilder.AddColumn<string>(
                name: "Barcode",
                schema: "inventory",
                table: "Products",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LastPurchasePrice",
                schema: "inventory",
                table: "Products",
                type: "decimal(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitCost",
                schema: "inventory",
                table: "Products",
                type: "decimal(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitPrice",
                schema: "inventory",
                table: "Products",
                type: "decimal(18,4)",
                nullable: true);
        }
    }
}
