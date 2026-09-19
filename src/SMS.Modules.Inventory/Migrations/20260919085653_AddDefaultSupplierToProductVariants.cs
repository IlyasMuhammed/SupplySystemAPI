using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class AddDefaultSupplierToProductVariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DefaultSupplierId",
                schema: "inventory",
                table: "ProductVariants",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LeadTimeDays",
                schema: "inventory",
                table: "ProductVariants",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductVariants_DefaultSupplierId",
                schema: "inventory",
                table: "ProductVariants",
                column: "DefaultSupplierId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProductVariants_DefaultSupplierId",
                schema: "inventory",
                table: "ProductVariants");

            migrationBuilder.DropColumn(
                name: "DefaultSupplierId",
                schema: "inventory",
                table: "ProductVariants");

            migrationBuilder.DropColumn(
                name: "LeadTimeDays",
                schema: "inventory",
                table: "ProductVariants");
        }
    }
}
