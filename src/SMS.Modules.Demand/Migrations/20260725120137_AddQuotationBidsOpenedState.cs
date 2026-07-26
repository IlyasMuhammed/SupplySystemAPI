using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Demand.Migrations
{
    /// <inheritdoc />
    public partial class AddQuotationBidsOpenedState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BidsOpenedAt",
                schema: "demand",
                table: "quotations",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BidsOpenedBy",
                schema: "demand",
                table: "quotations",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BidsOpenedAt",
                schema: "demand",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "BidsOpenedBy",
                schema: "demand",
                table: "quotations");
        }
    }
}
