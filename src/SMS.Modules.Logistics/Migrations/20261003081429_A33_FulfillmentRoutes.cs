using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class A33_FulfillmentRoutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ApprovedBy",
                schema: "logistics",
                table: "delivery_orders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FulfillmentRouteCode",
                schema: "logistics",
                table: "delivery_orders",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FulfillmentRouteUuid",
                schema: "logistics",
                table: "delivery_orders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RouteSteps",
                schema: "logistics",
                table: "delivery_orders",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "fulfillment_routes",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false),
                    RequiresPacking = table.Column<bool>(type: "bit", nullable: false),
                    RequiresShipping = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fulfillment_routes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "fulfillment_route_steps",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FulfillmentRouteId = table.Column<int>(type: "int", nullable: false),
                    StepCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StepOrder = table.Column<int>(type: "int", nullable: false),
                    IsMandatory = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fulfillment_route_steps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_fulfillment_route_steps_fulfillment_routes_FulfillmentRouteId",
                        column: x => x.FulfillmentRouteId,
                        principalSchema: "logistics",
                        principalTable: "fulfillment_routes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_orders_OrganizationId_FulfillmentRouteUuid",
                schema: "logistics",
                table: "delivery_orders",
                columns: new[] { "OrganizationId", "FulfillmentRouteUuid" },
                filter: "[FulfillmentRouteUuid] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_fulfillment_route_steps_FulfillmentRouteId_StepCode",
                schema: "logistics",
                table: "fulfillment_route_steps",
                columns: new[] { "FulfillmentRouteId", "StepCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fulfillment_route_steps_FulfillmentRouteId_StepOrder",
                schema: "logistics",
                table: "fulfillment_route_steps",
                columns: new[] { "FulfillmentRouteId", "StepOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fulfillment_route_steps_OrganizationId",
                schema: "logistics",
                table: "fulfillment_route_steps",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_fulfillment_routes_OrganizationId_Code",
                schema: "logistics",
                table: "fulfillment_routes",
                columns: new[] { "OrganizationId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fulfillment_routes_UUID",
                schema: "logistics",
                table: "fulfillment_routes",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_fulfillment_routes_OrganizationId_RequiresShipping_Default",
                schema: "logistics",
                table: "fulfillment_routes",
                columns: new[] { "OrganizationId", "RequiresShipping" },
                unique: true,
                filter: "[IsDefault] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fulfillment_route_steps",
                schema: "logistics");

            migrationBuilder.DropTable(
                name: "fulfillment_routes",
                schema: "logistics");

            migrationBuilder.DropIndex(
                name: "IX_delivery_orders_OrganizationId_FulfillmentRouteUuid",
                schema: "logistics",
                table: "delivery_orders");

            migrationBuilder.DropColumn(
                name: "ApprovedBy",
                schema: "logistics",
                table: "delivery_orders");

            migrationBuilder.DropColumn(
                name: "FulfillmentRouteCode",
                schema: "logistics",
                table: "delivery_orders");

            migrationBuilder.DropColumn(
                name: "FulfillmentRouteUuid",
                schema: "logistics",
                table: "delivery_orders");

            migrationBuilder.DropColumn(
                name: "RouteSteps",
                schema: "logistics",
                table: "delivery_orders");
        }
    }
}
