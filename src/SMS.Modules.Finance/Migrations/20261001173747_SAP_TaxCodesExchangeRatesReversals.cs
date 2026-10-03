using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Finance.Migrations
{
    /// <inheritdoc />
    public partial class SAP_TaxCodesExchangeRatesReversals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BaseCurrencyCode",
                schema: "finance",
                table: "sales_invoices",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BaseGrandTotal",
                schema: "finance",
                table: "sales_invoices",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancellationReason",
                schema: "finance",
                table: "sales_invoices",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAt",
                schema: "finance",
                table: "sales_invoices",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CancelledBy",
                schema: "finance",
                table: "sales_invoices",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ExchangeRate",
                schema: "finance",
                table: "sales_invoices",
                type: "decimal(18,8)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxCode",
                schema: "finance",
                table: "sales_invoice_lines",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TaxCodeUuid",
                schema: "finance",
                table: "sales_invoice_lines",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BaseCurrencyCode",
                schema: "finance",
                table: "invoices",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BaseTotalAmount",
                schema: "finance",
                table: "invoices",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ExchangeRate",
                schema: "finance",
                table: "invoices",
                type: "decimal(18,8)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReversalReason",
                schema: "finance",
                table: "invoices",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReversedAt",
                schema: "finance",
                table: "invoices",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReversedBy",
                schema: "finance",
                table: "invoices",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxCode",
                schema: "finance",
                table: "invoices",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TaxCodeUuid",
                schema: "finance",
                table: "invoices",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxPercent",
                schema: "finance",
                table: "invoices",
                type: "decimal(5,2)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "exchange_rates",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromCurrencyCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ToCurrencyCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Rate = table.Column<decimal>(type: "decimal(18,8)", nullable: false),
                    EffectiveDate = table.Column<DateTime>(type: "date", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "MANUAL"),
                    Notes = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    IsDelete = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_exchange_rates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tax_codes",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    RatePercent = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    Usage = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false, defaultValue: "BOTH"),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tax_codes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_exchange_rates_OrganizationId_FromCurrencyCode_ToCurrencyCode_EffectiveDate",
                schema: "finance",
                table: "exchange_rates",
                columns: new[] { "OrganizationId", "FromCurrencyCode", "ToCurrencyCode", "EffectiveDate" },
                unique: true,
                filter: "[IsDelete] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_exchange_rates_Uuid",
                schema: "finance",
                table: "exchange_rates",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tax_codes_OrganizationId_Code",
                schema: "finance",
                table: "tax_codes",
                columns: new[] { "OrganizationId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tax_codes_OrganizationId_IsActive_Usage",
                schema: "finance",
                table: "tax_codes",
                columns: new[] { "OrganizationId", "IsActive", "Usage" });

            migrationBuilder.CreateIndex(
                name: "IX_tax_codes_Uuid",
                schema: "finance",
                table: "tax_codes",
                column: "Uuid",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "exchange_rates",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "tax_codes",
                schema: "finance");

            migrationBuilder.DropColumn(
                name: "BaseCurrencyCode",
                schema: "finance",
                table: "sales_invoices");

            migrationBuilder.DropColumn(
                name: "BaseGrandTotal",
                schema: "finance",
                table: "sales_invoices");

            migrationBuilder.DropColumn(
                name: "CancellationReason",
                schema: "finance",
                table: "sales_invoices");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                schema: "finance",
                table: "sales_invoices");

            migrationBuilder.DropColumn(
                name: "CancelledBy",
                schema: "finance",
                table: "sales_invoices");

            migrationBuilder.DropColumn(
                name: "ExchangeRate",
                schema: "finance",
                table: "sales_invoices");

            migrationBuilder.DropColumn(
                name: "TaxCode",
                schema: "finance",
                table: "sales_invoice_lines");

            migrationBuilder.DropColumn(
                name: "TaxCodeUuid",
                schema: "finance",
                table: "sales_invoice_lines");

            migrationBuilder.DropColumn(
                name: "BaseCurrencyCode",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "BaseTotalAmount",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "ExchangeRate",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "ReversalReason",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "ReversedAt",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "ReversedBy",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "TaxCode",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "TaxCodeUuid",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "TaxPercent",
                schema: "finance",
                table: "invoices");
        }
    }
}
