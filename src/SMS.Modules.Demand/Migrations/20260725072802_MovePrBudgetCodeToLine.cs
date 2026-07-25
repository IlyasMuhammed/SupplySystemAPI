using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Demand.Migrations
{
    /// <inheritdoc />
    public partial class MovePrBudgetCodeToLine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BudgetCode",
                schema: "demand",
                table: "pr_lines",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            // Backfill: copy each PR's header-level BudgetCode onto every one of its lines
            // before dropping the header column, so existing data isn't silently lost.
            migrationBuilder.Sql(@"
                UPDATE pl
                SET pl.BudgetCode = pr.BudgetCode
                FROM demand.pr_lines pl
                JOIN demand.purchase_requisitions pr ON pr.Id = pl.PurchaseRequisitionId
                WHERE pr.BudgetCode IS NOT NULL;
            ");

            migrationBuilder.DropColumn(
                name: "BudgetCode",
                schema: "demand",
                table: "purchase_requisitions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BudgetCode",
                schema: "demand",
                table: "purchase_requisitions",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            // Best-effort restore: take the first line's BudgetCode per PR.
            migrationBuilder.Sql(@"
                UPDATE pr
                SET pr.BudgetCode = fl.BudgetCode
                FROM demand.purchase_requisitions pr
                CROSS APPLY (
                    SELECT TOP 1 BudgetCode
                    FROM demand.pr_lines
                    WHERE PurchaseRequisitionId = pr.Id AND BudgetCode IS NOT NULL
                    ORDER BY LineNo
                ) fl;
            ");

            migrationBuilder.DropColumn(
                name: "BudgetCode",
                schema: "demand",
                table: "pr_lines");
        }
    }
}
