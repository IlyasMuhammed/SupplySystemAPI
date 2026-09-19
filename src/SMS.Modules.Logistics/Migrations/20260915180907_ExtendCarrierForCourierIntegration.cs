using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class ExtendCarrierForCourierIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultCurrency",
                schema: "logistics",
                table: "carriers",
                type: "nchar(3)",
                fixedLength: true,
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntegrationMode",
                schema: "logistics",
                table: "carriers",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true,
                defaultValue: "MANUAL");

            migrationBuilder.AddColumn<string>(
                name: "ProviderKey",
                schema: "logistics",
                table: "carriers",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true,
                defaultValue: "MANUAL");

            migrationBuilder.AddColumn<string>(
                name: "ScacCode",
                schema: "logistics",
                table: "carriers",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultCurrency",
                schema: "logistics",
                table: "carriers");

            migrationBuilder.DropColumn(
                name: "IntegrationMode",
                schema: "logistics",
                table: "carriers");

            migrationBuilder.DropColumn(
                name: "ProviderKey",
                schema: "logistics",
                table: "carriers");

            migrationBuilder.DropColumn(
                name: "ScacCode",
                schema: "logistics",
                table: "carriers");
        }
    }
}
