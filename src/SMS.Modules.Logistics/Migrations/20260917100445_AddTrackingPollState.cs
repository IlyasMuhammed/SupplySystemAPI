using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackingPollState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastTrackingEventAt",
                schema: "logistics",
                table: "consignments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StuckReason",
                schema: "logistics",
                table: "consignments",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StuckSince",
                schema: "logistics",
                table: "consignments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrackingLastError",
                schema: "logistics",
                table: "consignments",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TrackingLastPolledAt",
                schema: "logistics",
                table: "consignments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TrackingNextPollAt",
                schema: "logistics",
                table: "consignments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TrackingPollFailures",
                schema: "logistics",
                table: "consignments",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_consignments_OrganizationId_StuckSince",
                schema: "logistics",
                table: "consignments",
                columns: new[] { "OrganizationId", "StuckSince" });

            migrationBuilder.CreateIndex(
                name: "IX_consignments_Status_TrackingNextPollAt",
                schema: "logistics",
                table: "consignments",
                columns: new[] { "Status", "TrackingNextPollAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_consignments_OrganizationId_StuckSince",
                schema: "logistics",
                table: "consignments");

            migrationBuilder.DropIndex(
                name: "IX_consignments_Status_TrackingNextPollAt",
                schema: "logistics",
                table: "consignments");

            migrationBuilder.DropColumn(
                name: "LastTrackingEventAt",
                schema: "logistics",
                table: "consignments");

            migrationBuilder.DropColumn(
                name: "StuckReason",
                schema: "logistics",
                table: "consignments");

            migrationBuilder.DropColumn(
                name: "StuckSince",
                schema: "logistics",
                table: "consignments");

            migrationBuilder.DropColumn(
                name: "TrackingLastError",
                schema: "logistics",
                table: "consignments");

            migrationBuilder.DropColumn(
                name: "TrackingLastPolledAt",
                schema: "logistics",
                table: "consignments");

            migrationBuilder.DropColumn(
                name: "TrackingNextPollAt",
                schema: "logistics",
                table: "consignments");

            migrationBuilder.DropColumn(
                name: "TrackingPollFailures",
                schema: "logistics",
                table: "consignments");
        }
    }
}
