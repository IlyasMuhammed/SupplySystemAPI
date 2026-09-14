using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Warehouse.Migrations
{
    /// <inheritdoc />
    public partial class AddSroAcknowledgmentLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sro_acknowledgment_links",
                schema: "warehouse",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReturnOrderId = table.Column<int>(type: "int", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "PENDING"),
                    GeneratedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FirstOpenedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConsumedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConsumedIp = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                    AccessCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    SupplierEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    PortalLinkUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    EmailSentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AckRemarks = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AckReceivedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sro_acknowledgment_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sro_acknowledgment_links_supplier_return_orders_ReturnOrderId",
                        column: x => x.ReturnOrderId,
                        principalSchema: "warehouse",
                        principalTable: "supplier_return_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sro_acknowledgment_links_OrganizationId",
                schema: "warehouse",
                table: "sro_acknowledgment_links",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_sro_acknowledgment_links_ReturnOrderId",
                schema: "warehouse",
                table: "sro_acknowledgment_links",
                column: "ReturnOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_sro_acknowledgment_links_TokenHash",
                schema: "warehouse",
                table: "sro_acknowledgment_links",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sro_acknowledgment_links",
                schema: "warehouse");
        }
    }
}
