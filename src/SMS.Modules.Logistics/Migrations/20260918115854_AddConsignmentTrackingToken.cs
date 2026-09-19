using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddConsignmentTrackingToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TrackingToken",
                schema: "logistics",
                table: "consignments",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TrackingTokenIssuedAt",
                schema: "logistics",
                table: "consignments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_consignments_TrackingToken",
                schema: "logistics",
                table: "consignments",
                column: "TrackingToken",
                unique: true,
                filter: "[TrackingToken] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_consignments_TrackingToken",
                schema: "logistics",
                table: "consignments");

            migrationBuilder.DropColumn(
                name: "TrackingToken",
                schema: "logistics",
                table: "consignments");

            migrationBuilder.DropColumn(
                name: "TrackingTokenIssuedAt",
                schema: "logistics",
                table: "consignments");
        }
    }
}
