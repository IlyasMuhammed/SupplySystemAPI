using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Demand.Migrations
{
    /// <inheritdoc />
    public partial class MoveBudgetCodeToLineLevel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BudgetCode",
                schema: "demand",
                table: "quotation_lines",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BudgetCode",
                schema: "demand",
                table: "purchase_order_lines",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            // Backfill: copy each PO's header-level BudgetCode onto every one of its lines
            // before dropping the header column, so existing data isn't silently lost — same
            // pattern as MovePrBudgetCodeToLine.
            migrationBuilder.Sql(@"
                UPDATE pol
                SET pol.BudgetCode = po.BudgetCode
                FROM demand.purchase_order_lines pol
                JOIN demand.purchase_orders po ON po.Id = pol.PurchaseOrderId
                WHERE po.BudgetCode IS NOT NULL;
            ");

            migrationBuilder.DropColumn(
                name: "BudgetCode",
                schema: "demand",
                table: "purchase_orders");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BudgetCode",
                schema: "demand",
                table: "purchase_orders",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            // Best-effort restore: take the first line's BudgetCode per PO.
            migrationBuilder.Sql(@"
                UPDATE po
                SET po.BudgetCode = fl.BudgetCode
                FROM demand.purchase_orders po
                CROSS APPLY (
                    SELECT TOP 1 BudgetCode
                    FROM demand.purchase_order_lines
                    WHERE PurchaseOrderId = po.Id AND BudgetCode IS NOT NULL
                    ORDER BY LineNo
                ) fl;
            ");

            migrationBuilder.DropColumn(
                name: "BudgetCode",
                schema: "demand",
                table: "quotation_lines");

            migrationBuilder.DropColumn(
                name: "BudgetCode",
                schema: "demand",
                table: "purchase_order_lines");
        }
    }
}
