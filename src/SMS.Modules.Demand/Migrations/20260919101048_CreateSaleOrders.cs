using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Demand.Migrations
{
    /// <inheritdoc />
    public partial class CreateSaleOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sale_orders",
                schema: "demand",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TraceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SoNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PartnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpectedDeliveryDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CurrencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Subtotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TaxAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    GrandTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RequiresShipment = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    DeliveryMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ShippingAddressId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IntimationDepartmentId = table.Column<int>(type: "int", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sale_orders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sale_order_lines",
                schema: "demand",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SaleOrderId = table.Column<int>(type: "int", nullable: false),
                    VariantUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    DiscountPercent = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    TaxPercent = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    LineTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    FulfilledQty = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    InvoicedQty = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    FulfillmentMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    AvailableQtyAtConfirm = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    DeficitQty = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    LinkedPoId = table.Column<int>(type: "int", nullable: true),
                    SelectedSupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Margin = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    MarginPercent = table.Column<decimal>(type: "decimal(5,2)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sale_order_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sale_order_lines_sale_orders_SaleOrderId",
                        column: x => x.SaleOrderId,
                        principalSchema: "demand",
                        principalTable: "sale_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sale_order_lines_OrganizationId",
                schema: "demand",
                table: "sale_order_lines",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_sale_order_lines_SaleOrderId",
                schema: "demand",
                table: "sale_order_lines",
                column: "SaleOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_sale_order_lines_UUID",
                schema: "demand",
                table: "sale_order_lines",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sale_order_lines_VariantUuid_Status",
                schema: "demand",
                table: "sale_order_lines",
                columns: new[] { "VariantUuid", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_sale_orders_OrganizationId_PartnerId",
                schema: "demand",
                table: "sale_orders",
                columns: new[] { "OrganizationId", "PartnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_sale_orders_OrganizationId_SoNumber",
                schema: "demand",
                table: "sale_orders",
                columns: new[] { "OrganizationId", "SoNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sale_orders_OrganizationId_Status_OrderDate",
                schema: "demand",
                table: "sale_orders",
                columns: new[] { "OrganizationId", "Status", "OrderDate" });

            migrationBuilder.CreateIndex(
                name: "IX_sale_orders_TraceId",
                schema: "demand",
                table: "sale_orders",
                column: "TraceId");

            migrationBuilder.CreateIndex(
                name: "IX_sale_orders_UUID",
                schema: "demand",
                table: "sale_orders",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sale_order_lines",
                schema: "demand");

            migrationBuilder.DropTable(
                name: "sale_orders",
                schema: "demand");
        }
    }
}
