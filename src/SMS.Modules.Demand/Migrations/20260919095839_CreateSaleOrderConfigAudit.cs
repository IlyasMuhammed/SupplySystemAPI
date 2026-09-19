using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Demand.Migrations
{
    /// <inheritdoc />
    public partial class CreateSaleOrderConfigAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sale_order_config_audit",
                schema: "demand",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SaleOrderConfigId = table.Column<int>(type: "int", nullable: false),
                    FieldChanged = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OldValue = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ChangedBy = table.Column<int>(type: "int", nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sale_order_config_audit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sale_order_config_audit_sale_order_config_SaleOrderConfigId",
                        column: x => x.SaleOrderConfigId,
                        principalSchema: "demand",
                        principalTable: "sale_order_config",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sale_order_config_audit_OrganizationId",
                schema: "demand",
                table: "sale_order_config_audit",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_sale_order_config_audit_SaleOrderConfigId",
                schema: "demand",
                table: "sale_order_config_audit",
                column: "SaleOrderConfigId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sale_order_config_audit",
                schema: "demand");
        }
    }
}
