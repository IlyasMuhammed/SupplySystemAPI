using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Suppliers.Migrations
{
    /// <summary>
    /// A35 P2-06 (M4, docs/multi-currency/ADDENDUM-35-ANALYSIS.md D-9) — <c>BusinessPartners.DefaultSaleCurrency
    /// uniqueidentifier NULL</c> (unenforced scalar id into lookups.Currencies; NULL = the organization's sale base).
    /// The default purchase currency is the existing <c>PreferredCurrency</c> column, so it is not added. Guarded so a
    /// re-run on the shared, drifted database (every API start migrates it) is a no-op.
    /// </summary>
    public partial class A35_PartnerDefaultSaleCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "IF COL_LENGTH(N'suppliers.BusinessPartners', N'DefaultSaleCurrency') IS NULL " +
                "ALTER TABLE [suppliers].[BusinessPartners] ADD [DefaultSaleCurrency] uniqueidentifier NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "IF COL_LENGTH(N'suppliers.BusinessPartners', N'DefaultSaleCurrency') IS NOT NULL " +
                "ALTER TABLE [suppliers].[BusinessPartners] DROP COLUMN [DefaultSaleCurrency];");
        }
    }
}
