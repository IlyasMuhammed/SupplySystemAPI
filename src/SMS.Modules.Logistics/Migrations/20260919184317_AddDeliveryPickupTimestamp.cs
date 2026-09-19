using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryPickupTimestamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PickedUpAt",
                schema: "logistics",
                table: "delivery_orders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PickedUpBy",
                schema: "logistics",
                table: "delivery_orders",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PickedUpAt",
                schema: "logistics",
                table: "delivery_orders");

            migrationBuilder.DropColumn(
                name: "PickedUpBy",
                schema: "logistics",
                table: "delivery_orders");
        }
    }
}
