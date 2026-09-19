using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddCarrierInvoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "carrier_invoices",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CarrierId = table.Column<int>(type: "int", nullable: false),
                    CarrierName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    InvoiceNumber = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    InvoiceDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DueDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Currency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TaxAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsDelete = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_carrier_invoices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_carrier_invoices_carriers_CarrierId",
                        column: x => x.CarrierId,
                        principalSchema: "logistics",
                        principalTable: "carriers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "carrier_invoice_lines",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CarrierInvoiceId = table.Column<int>(type: "int", nullable: false),
                    LineNo = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    AwbNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ConsignmentReference = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ChargeCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ServiceCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ChargeableWeightKg = table.Column<decimal>(type: "decimal(18,3)", nullable: true),
                    ShipDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_carrier_invoice_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_carrier_invoice_lines_carrier_invoices_CarrierInvoiceId",
                        column: x => x.CarrierInvoiceId,
                        principalSchema: "logistics",
                        principalTable: "carrier_invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_carrier_invoice_lines_CarrierInvoiceId_LineNo",
                schema: "logistics",
                table: "carrier_invoice_lines",
                columns: new[] { "CarrierInvoiceId", "LineNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_carrier_invoice_lines_OrganizationId_AwbNumber",
                schema: "logistics",
                table: "carrier_invoice_lines",
                columns: new[] { "OrganizationId", "AwbNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_carrier_invoice_lines_UUID",
                schema: "logistics",
                table: "carrier_invoice_lines",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_carrier_invoices_CarrierId",
                schema: "logistics",
                table: "carrier_invoices",
                column: "CarrierId");

            migrationBuilder.CreateIndex(
                name: "IX_carrier_invoices_OrganizationId_CarrierId_InvoiceNumber",
                schema: "logistics",
                table: "carrier_invoices",
                columns: new[] { "OrganizationId", "CarrierId", "InvoiceNumber" },
                unique: true,
                filter: "[IsDelete] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_carrier_invoices_OrganizationId_Status_InvoiceDate",
                schema: "logistics",
                table: "carrier_invoices",
                columns: new[] { "OrganizationId", "Status", "InvoiceDate" });

            migrationBuilder.CreateIndex(
                name: "IX_carrier_invoices_UUID",
                schema: "logistics",
                table: "carrier_invoices",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "carrier_invoice_lines",
                schema: "logistics");

            migrationBuilder.DropTable(
                name: "carrier_invoices",
                schema: "logistics");
        }
    }
}
