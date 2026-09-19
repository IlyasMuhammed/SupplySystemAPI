using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryWarehouseReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ShipFromWarehouseUuid",
                schema: "logistics",
                table: "delivery_orders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ShipToWarehouseUuid",
                schema: "logistics",
                table: "delivery_orders",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShipFromWarehouseUuid",
                schema: "logistics",
                table: "delivery_orders");

            migrationBuilder.DropColumn(
                name: "ShipToWarehouseUuid",
                schema: "logistics",
                table: "delivery_orders");
        }
    }
}
