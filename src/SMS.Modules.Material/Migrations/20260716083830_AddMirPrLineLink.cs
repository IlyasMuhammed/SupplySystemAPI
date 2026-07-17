using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Material.Migrations
{
    /// <inheritdoc />
    public partial class AddMirPrLineLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PrLineId",
                schema: "material",
                table: "material_issue_request_lines",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_material_issue_request_lines_PrLineId",
                schema: "material",
                table: "material_issue_request_lines",
                column: "PrLineId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_material_issue_request_lines_PrLineId",
                schema: "material",
                table: "material_issue_request_lines");

            migrationBuilder.DropColumn(
                name: "PrLineId",
                schema: "material",
                table: "material_issue_request_lines");
        }
    }
}
