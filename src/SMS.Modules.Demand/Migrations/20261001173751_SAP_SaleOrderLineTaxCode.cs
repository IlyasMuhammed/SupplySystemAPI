using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Demand.Migrations
{
    /// <inheritdoc />
    public partial class SAP_SaleOrderLineTaxCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TaxCode",
                schema: "demand",
                table: "sale_order_lines",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TaxCodeUuid",
                schema: "demand",
                table: "sale_order_lines",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TaxCode",
                schema: "demand",
                table: "sale_order_lines");

            migrationBuilder.DropColumn(
                name: "TaxCodeUuid",
                schema: "demand",
                table: "sale_order_lines");
        }
    }
}
