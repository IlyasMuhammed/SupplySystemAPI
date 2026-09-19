using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Demand.Migrations
{
    /// <inheritdoc />
    public partial class CreateSaleOrderConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sale_order_config",
                schema: "demand",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AutoPoEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    SupplierSelectionMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AutoPoApprovalMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DropShipEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    SelfPickupEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    DefaultFulfillmentMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ReservationTtlHours = table.Column<int>(type: "int", nullable: false, defaultValue: 72),
                    PartialFulfillmentAllowed = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    EmailIntimationEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    IntimationDepartmentId = table.Column<int>(type: "int", nullable: true),
                    IntimationCcEmails = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ShipmentRequiredDefault = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sale_order_config", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sale_order_config_OrganizationId",
                schema: "demand",
                table: "sale_order_config",
                column: "OrganizationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sale_order_config_Uuid",
                schema: "demand",
                table: "sale_order_config",
                column: "Uuid",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sale_order_config",
                schema: "demand");
        }
    }
}
