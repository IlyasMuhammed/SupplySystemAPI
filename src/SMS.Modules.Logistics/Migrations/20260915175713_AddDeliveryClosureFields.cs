using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryClosureFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ClosedAt",
                schema: "logistics",
                table: "delivery_orders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ClosedBy",
                schema: "logistics",
                table: "delivery_orders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClosureReason",
                schema: "logistics",
                table: "delivery_orders",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClosedAt",
                schema: "logistics",
                table: "delivery_orders");

            migrationBuilder.DropColumn(
                name: "ClosedBy",
                schema: "logistics",
                table: "delivery_orders");

            migrationBuilder.DropColumn(
                name: "ClosureReason",
                schema: "logistics",
                table: "delivery_orders");
        }
    }
}
