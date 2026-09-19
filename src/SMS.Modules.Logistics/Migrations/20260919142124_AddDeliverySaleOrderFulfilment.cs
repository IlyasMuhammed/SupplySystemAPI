using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliverySaleOrderFulfilment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeliveryMode",
                schema: "logistics",
                table: "delivery_orders",
                type: "nvarchar(15)",
                maxLength: 15,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PickupAuthorization",
                schema: "logistics",
                table: "delivery_orders",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PickupPersonIdNumber",
                schema: "logistics",
                table: "delivery_orders",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PickupPersonIdType",
                schema: "logistics",
                table: "delivery_orders",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PickupPersonName",
                schema: "logistics",
                table: "delivery_orders",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SaleOrderUuid",
                schema: "logistics",
                table: "delivery_orders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SoLineUuid",
                schema: "logistics",
                table: "delivery_order_lines",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_delivery_orders_SaleOrderUuid_Status",
                schema: "logistics",
                table: "delivery_orders",
                columns: new[] { "SaleOrderUuid", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_delivery_orders_SaleOrderUuid_Status",
                schema: "logistics",
                table: "delivery_orders");

            migrationBuilder.DropColumn(
                name: "DeliveryMode",
                schema: "logistics",
                table: "delivery_orders");

            migrationBuilder.DropColumn(
                name: "PickupAuthorization",
                schema: "logistics",
                table: "delivery_orders");

            migrationBuilder.DropColumn(
                name: "PickupPersonIdNumber",
                schema: "logistics",
                table: "delivery_orders");

            migrationBuilder.DropColumn(
                name: "PickupPersonIdType",
                schema: "logistics",
                table: "delivery_orders");

            migrationBuilder.DropColumn(
                name: "PickupPersonName",
                schema: "logistics",
                table: "delivery_orders");

            migrationBuilder.DropColumn(
                name: "SaleOrderUuid",
                schema: "logistics",
                table: "delivery_orders");

            migrationBuilder.DropColumn(
                name: "SoLineUuid",
                schema: "logistics",
                table: "delivery_order_lines");
        }
    }
}
