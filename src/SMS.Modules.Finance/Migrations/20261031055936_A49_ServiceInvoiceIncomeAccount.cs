using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Finance.Migrations
{
    /// <summary>
    /// A49 E-13: a service invoice line's own income account. <b>Guarded and idempotent</b> (every API start migrates the shared, drifted
    /// database): additive, nullable, added only when missing. Verified on LocalDB only.
    /// </summary>
    public partial class A49_ServiceInvoiceIncomeAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"IF OBJECT_ID('finance.sales_invoice_lines') IS NOT NULL AND COL_LENGTH('finance.sales_invoice_lines', 'IncomeAccountCode') IS NULL
ALTER TABLE [finance].[sales_invoice_lines] ADD [IncomeAccountCode] nvarchar(30) NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"IF COL_LENGTH('finance.sales_invoice_lines', 'IncomeAccountCode') IS NOT NULL
ALTER TABLE [finance].[sales_invoice_lines] DROP COLUMN [IncomeAccountCode];");
        }
    }
}
