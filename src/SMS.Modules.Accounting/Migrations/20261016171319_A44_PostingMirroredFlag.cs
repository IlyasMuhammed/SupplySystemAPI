using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Accounting.Migrations
{
    /// <summary>
    /// A44 D-49b (REV option B) - <c>FinancialPostingTargets.MirroredFromTarget</c>: a Failed/DeadLetter copied from the
    /// QuickBooks outbox, which the Accounting retry job does not re-send. <b>Guarded and idempotent</b>: added (NOT NULL,
    /// default 0 for existing rows) only when the table exists and the column is missing.
    /// </summary>
    public partial class A44_PostingMirroredFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'[finance].[FinancialPostingTargets]', N'U') IS NOT NULL
                   AND COL_LENGTH(N'[finance].[FinancialPostingTargets]', N'MirroredFromTarget') IS NULL
                    ALTER TABLE [finance].[FinancialPostingTargets] ADD [MirroredFromTarget] bit NOT NULL
                        CONSTRAINT [DF_FinancialPostingTargets_MirroredFromTarget] DEFAULT CAST(0 AS bit);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'[finance].[DF_FinancialPostingTargets_MirroredFromTarget]', N'D') IS NOT NULL
                    ALTER TABLE [finance].[FinancialPostingTargets] DROP CONSTRAINT [DF_FinancialPostingTargets_MirroredFromTarget];
                """);
            migrationBuilder.DropColumn(name: "MirroredFromTarget", schema: "finance", table: "FinancialPostingTargets");
        }
    }
}
