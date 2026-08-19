using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Demand.Migrations
{
    /// <inheritdoc />
    public partial class AddVendorResponseLineCanSupply : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CanSupply",
                schema: "demand",
                table: "vendor_response_lines",
                type: "bit",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CanSupply",
                schema: "demand",
                table: "vendor_response_lines");
        }
    }
}
