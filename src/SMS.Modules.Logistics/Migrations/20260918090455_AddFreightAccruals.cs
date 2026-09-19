using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddFreightAccruals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "freight_accruals",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConsignmentId = table.Column<int>(type: "int", nullable: false),
                    CarrierId = table.Column<int>(type: "int", nullable: true),
                    CarrierName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AccruedAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    QuoteSource = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    BookedAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    BookedCurrency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AccruedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AccruedBy = table.Column<int>(type: "int", nullable: false),
                    ReleasedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReleaseReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsDelete = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_freight_accruals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_freight_accruals_carriers_CarrierId",
                        column: x => x.CarrierId,
                        principalSchema: "logistics",
                        principalTable: "carriers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_freight_accruals_consignments_ConsignmentId",
                        column: x => x.ConsignmentId,
                        principalSchema: "logistics",
                        principalTable: "consignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_freight_accruals_CarrierId",
                schema: "logistics",
                table: "freight_accruals",
                column: "CarrierId");

            migrationBuilder.CreateIndex(
                name: "IX_freight_accruals_ConsignmentId",
                schema: "logistics",
                table: "freight_accruals",
                column: "ConsignmentId",
                unique: true,
                filter: "[IsDelete] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_freight_accruals_OrganizationId_Status_CarrierId",
                schema: "logistics",
                table: "freight_accruals",
                columns: new[] { "OrganizationId", "Status", "CarrierId" });

            migrationBuilder.CreateIndex(
                name: "IX_freight_accruals_UUID",
                schema: "logistics",
                table: "freight_accruals",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "freight_accruals",
                schema: "logistics");
        }
    }
}
