using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Warehouse.Migrations
{
    /// <inheritdoc />
    public partial class AddRequiresInspectionToGrnLine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequiresInspection",
                schema: "warehouse",
                table: "grn_lines",
                type: "bit",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequiresInspection",
                schema: "warehouse",
                table: "grn_lines");
        }
    }
}
