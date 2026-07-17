using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Demand.Migrations
{
    /// <inheritdoc />
    public partial class AddPrLineDisbursementTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisbursedMirIds",
                schema: "demand",
                table: "pr_lines",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<decimal>(
                name: "DisbursedQty",
                schema: "demand",
                table: "pr_lines",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisbursedMirIds",
                schema: "demand",
                table: "pr_lines");

            migrationBuilder.DropColumn(
                name: "DisbursedQty",
                schema: "demand",
                table: "pr_lines");
        }
    }
}
