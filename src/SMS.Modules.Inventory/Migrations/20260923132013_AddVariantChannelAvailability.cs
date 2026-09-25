using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class AddVariantChannelAvailability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAvailableForMirMiv",
                schema: "inventory",
                table: "ProductVariants",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsAvailableForPos",
                schema: "inventory",
                table: "ProductVariants",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsAvailableForProduction",
                schema: "inventory",
                table: "ProductVariants",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsAvailableForRetail",
                schema: "inventory",
                table: "ProductVariants",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsAvailableForServices",
                schema: "inventory",
                table: "ProductVariants",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsAvailableForMirMiv",
                schema: "inventory",
                table: "ProductVariants");

            migrationBuilder.DropColumn(
                name: "IsAvailableForPos",
                schema: "inventory",
                table: "ProductVariants");

            migrationBuilder.DropColumn(
                name: "IsAvailableForProduction",
                schema: "inventory",
                table: "ProductVariants");

            migrationBuilder.DropColumn(
                name: "IsAvailableForRetail",
                schema: "inventory",
                table: "ProductVariants");

            migrationBuilder.DropColumn(
                name: "IsAvailableForServices",
                schema: "inventory",
                table: "ProductVariants");
        }
    }
}
