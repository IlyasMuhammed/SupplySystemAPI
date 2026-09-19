using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Finance.Migrations
{
    /// <inheritdoc />
    public partial class CreateCustomerPaymentTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customer_payments",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PartnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PartnerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PaymentNumber = table.Column<string>(type: "nvarchar(25)", maxLength: 25, nullable: false),
                    PaymentDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    PaymentMethod = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ChequeNumber = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    BankReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CurrencyCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false, defaultValue: "PKR"),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "RECEIVED"),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_payments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "payment_allocations",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerPaymentId = table.Column<int>(type: "int", nullable: false),
                    SalesInvoiceId = table.Column<int>(type: "int", nullable: false),
                    AllocatedAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    AllocatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AllocatedBy = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_allocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_payment_allocations_customer_payments_CustomerPaymentId",
                        column: x => x.CustomerPaymentId,
                        principalSchema: "finance",
                        principalTable: "customer_payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_payment_allocations_sales_invoices_SalesInvoiceId",
                        column: x => x.SalesInvoiceId,
                        principalSchema: "finance",
                        principalTable: "sales_invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customer_payments_OrganizationId",
                schema: "finance",
                table: "customer_payments",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_customer_payments_OrganizationId_PartnerId_PaymentDate",
                schema: "finance",
                table: "customer_payments",
                columns: new[] { "OrganizationId", "PartnerId", "PaymentDate" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_payments_OrganizationId_PaymentNumber",
                schema: "finance",
                table: "customer_payments",
                columns: new[] { "OrganizationId", "PaymentNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customer_payments_UUID",
                schema: "finance",
                table: "customer_payments",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_allocations_CustomerPaymentId",
                schema: "finance",
                table: "payment_allocations",
                column: "CustomerPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_payment_allocations_OrganizationId",
                schema: "finance",
                table: "payment_allocations",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_payment_allocations_SalesInvoiceId",
                schema: "finance",
                table: "payment_allocations",
                column: "SalesInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_payment_allocations_UUID",
                schema: "finance",
                table: "payment_allocations",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_allocations",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "customer_payments",
                schema: "finance");
        }
    }
}
