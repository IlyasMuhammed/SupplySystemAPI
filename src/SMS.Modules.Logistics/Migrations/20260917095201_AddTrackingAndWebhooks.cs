using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackingAndWebhooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastStatusEventAt",
                schema: "logistics",
                table: "consignments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "carrier_webhook_deliveries",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CarrierAccountId = table.Column<int>(type: "int", nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DedupeKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    BodySha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    EventCount = table.Column<int>(type: "int", nullable: false),
                    RecordedCount = table.Column<int>(type: "int", nullable: false),
                    DuplicateCount = table.Column<int>(type: "int", nullable: false),
                    UnmatchedCount = table.Column<int>(type: "int", nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_carrier_webhook_deliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_carrier_webhook_deliveries_carrier_accounts_CarrierAccountId",
                        column: x => x.CarrierAccountId,
                        principalSchema: "logistics",
                        principalTable: "carrier_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "consignment_tracking_events",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConsignmentId = table.Column<int>(type: "int", nullable: false),
                    Milestone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CarrierStatus = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Location = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SignedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EventKey = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    AppliedStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consignment_tracking_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_consignment_tracking_events_consignments_ConsignmentId",
                        column: x => x.ConsignmentId,
                        principalSchema: "logistics",
                        principalTable: "consignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_carrier_webhook_deliveries_CarrierAccountId_DedupeKey",
                schema: "logistics",
                table: "carrier_webhook_deliveries",
                columns: new[] { "CarrierAccountId", "DedupeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_carrier_webhook_deliveries_OrganizationId_Status",
                schema: "logistics",
                table: "carrier_webhook_deliveries",
                columns: new[] { "OrganizationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_carrier_webhook_deliveries_UUID",
                schema: "logistics",
                table: "carrier_webhook_deliveries",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_consignment_tracking_events_ConsignmentId_EventKey",
                schema: "logistics",
                table: "consignment_tracking_events",
                columns: new[] { "ConsignmentId", "EventKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_consignment_tracking_events_ConsignmentId_OccurredAt",
                schema: "logistics",
                table: "consignment_tracking_events",
                columns: new[] { "ConsignmentId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_consignment_tracking_events_UUID",
                schema: "logistics",
                table: "consignment_tracking_events",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "carrier_webhook_deliveries",
                schema: "logistics");

            migrationBuilder.DropTable(
                name: "consignment_tracking_events",
                schema: "logistics");

            migrationBuilder.DropColumn(
                name: "LastStatusEventAt",
                schema: "logistics",
                table: "consignments");
        }
    }
}
