using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Finance.Migrations
{
    /// <inheritdoc />
    public partial class CreateSalesInvoiceTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sales_invoices",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TraceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceNumber = table.Column<string>(type: "nvarchar(25)", maxLength: 25, nullable: false),
                    SaleOrderUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SaleOrderNumber = table.Column<string>(type: "nvarchar(25)", maxLength: 25, nullable: false),
                    PartnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PartnerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    InvoiceDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DueDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Subtotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TaxAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    GrandTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    AmountPaid = table.Column<decimal>(type: "decimal(18,2)", nullable: false, defaultValue: 0m),
                    BalanceDue = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "DRAFT"),
                    CurrencyCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false, defaultValue: "PKR"),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    IsDelete = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_invoices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sales_invoice_lines",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SalesInvoiceId = table.Column<int>(type: "int", nullable: false),
                    LineNo = table.Column<int>(type: "int", nullable: false),
                    SoLineUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VariantUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    DiscountPercent = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    TaxPercent = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    LineTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_invoice_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sales_invoice_lines_sales_invoices_SalesInvoiceId",
                        column: x => x.SalesInvoiceId,
                        principalSchema: "finance",
                        principalTable: "sales_invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sales_invoice_lines_OrganizationId",
                schema: "finance",
                table: "sales_invoice_lines",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_sales_invoice_lines_SalesInvoiceId_LineNo",
                schema: "finance",
                table: "sales_invoice_lines",
                columns: new[] { "SalesInvoiceId", "LineNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_invoice_lines_SoLineUuid",
                schema: "finance",
                table: "sales_invoice_lines",
                column: "SoLineUuid");

            migrationBuilder.CreateIndex(
                name: "IX_sales_invoice_lines_UUID",
                schema: "finance",
                table: "sales_invoice_lines",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_invoices_OrganizationId",
                schema: "finance",
                table: "sales_invoices",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_sales_invoices_OrganizationId_DueDate_BalanceDue",
                schema: "finance",
                table: "sales_invoices",
                columns: new[] { "OrganizationId", "DueDate", "BalanceDue" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_invoices_OrganizationId_InvoiceNumber",
                schema: "finance",
                table: "sales_invoices",
                columns: new[] { "OrganizationId", "InvoiceNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_invoices_OrganizationId_PartnerId_Status",
                schema: "finance",
                table: "sales_invoices",
                columns: new[] { "OrganizationId", "PartnerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_invoices_OrganizationId_SaleOrderUuid",
                schema: "finance",
                table: "sales_invoices",
                columns: new[] { "OrganizationId", "SaleOrderUuid" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_invoices_TraceId",
                schema: "finance",
                table: "sales_invoices",
                column: "TraceId");

            migrationBuilder.CreateIndex(
                name: "IX_sales_invoices_UUID",
                schema: "finance",
                table: "sales_invoices",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sales_invoice_lines",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "sales_invoices",
                schema: "finance");
        }
    }
}
