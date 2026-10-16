using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Accounting.Migrations
{
    /// <summary>
    /// A44 D-49b (QA-2) - <c>FinancialPostingTargets.ExternalDocNumber</c> (the QuickBooks DocNumber once the outbox has
    /// synced the record). <b>Guarded and idempotent</b>: added only when the table exists and the column is missing.
    /// </summary>
    public partial class A44_PostingExternalDocNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'[finance].[FinancialPostingTargets]', N'U') IS NOT NULL
                   AND COL_LENGTH(N'[finance].[FinancialPostingTargets]', N'ExternalDocNumber') IS NULL
                    ALTER TABLE [finance].[FinancialPostingTargets] ADD [ExternalDocNumber] nvarchar(100) NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "ExternalDocNumber", schema: "finance", table: "FinancialPostingTargets");
        }
    }
}
