using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryExceptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "delivery_exceptions",
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
                    ExceptionType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TrackingEventId = table.Column<int>(type: "int", nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AssignedToUserId = table.Column<int>(type: "int", nullable: true),
                    AssignedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedBy = table.Column<int>(type: "int", nullable: true),
                    Resolution = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsDelete = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_exceptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_delivery_exceptions_carriers_CarrierId",
                        column: x => x.CarrierId,
                        principalSchema: "logistics",
                        principalTable: "carriers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delivery_exceptions_consignment_tracking_events_TrackingEventId",
                        column: x => x.TrackingEventId,
                        principalSchema: "logistics",
                        principalTable: "consignment_tracking_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_delivery_exceptions_consignments_ConsignmentId",
                        column: x => x.ConsignmentId,
                        principalSchema: "logistics",
                        principalTable: "consignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_exceptions_CarrierId",
                schema: "logistics",
                table: "delivery_exceptions",
                column: "CarrierId");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_exceptions_ConsignmentId",
                schema: "logistics",
                table: "delivery_exceptions",
                column: "ConsignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_exceptions_OrganizationId_Status_Severity",
                schema: "logistics",
                table: "delivery_exceptions",
                columns: new[] { "OrganizationId", "Status", "Severity" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_exceptions_TrackingEventId",
                schema: "logistics",
                table: "delivery_exceptions",
                column: "TrackingEventId",
                unique: true,
                filter: "[TrackingEventId] IS NOT NULL AND [IsDelete] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_exceptions_UUID",
                schema: "logistics",
                table: "delivery_exceptions",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "delivery_exceptions",
                schema: "logistics");
        }
    }
}
