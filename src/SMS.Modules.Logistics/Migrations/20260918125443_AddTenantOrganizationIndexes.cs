using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantOrganizationIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_rate_card_lanes_OrganizationId",
                schema: "logistics",
                table: "rate_card_lanes",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_rate_card_breaks_OrganizationId",
                schema: "logistics",
                table: "rate_card_breaks",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_pick_list_lines_OrganizationId",
                schema: "logistics",
                table: "pick_list_lines",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_package_contents_OrganizationId",
                schema: "logistics",
                table: "package_contents",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_proofs_OrganizationId",
                schema: "logistics",
                table: "delivery_proofs",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_proof_files_OrganizationId",
                schema: "logistics",
                table: "delivery_proof_files",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_consignment_tracking_events_OrganizationId",
                schema: "logistics",
                table: "consignment_tracking_events",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_consignment_stops_OrganizationId",
                schema: "logistics",
                table: "consignment_stops",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_consignment_labels_OrganizationId",
                schema: "logistics",
                table: "consignment_labels",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_consignment_deliveries_OrganizationId",
                schema: "logistics",
                table: "consignment_deliveries",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_consignment_charges_OrganizationId",
                schema: "logistics",
                table: "consignment_charges",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_carrier_credentials_OrganizationId",
                schema: "logistics",
                table: "carrier_credentials",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_rate_card_lanes_OrganizationId",
                schema: "logistics",
                table: "rate_card_lanes");

            migrationBuilder.DropIndex(
                name: "IX_rate_card_breaks_OrganizationId",
                schema: "logistics",
                table: "rate_card_breaks");

            migrationBuilder.DropIndex(
                name: "IX_pick_list_lines_OrganizationId",
                schema: "logistics",
                table: "pick_list_lines");

            migrationBuilder.DropIndex(
                name: "IX_package_contents_OrganizationId",
                schema: "logistics",
                table: "package_contents");

            migrationBuilder.DropIndex(
                name: "IX_delivery_proofs_OrganizationId",
                schema: "logistics",
                table: "delivery_proofs");

            migrationBuilder.DropIndex(
                name: "IX_delivery_proof_files_OrganizationId",
                schema: "logistics",
                table: "delivery_proof_files");

            migrationBuilder.DropIndex(
                name: "IX_consignment_tracking_events_OrganizationId",
                schema: "logistics",
                table: "consignment_tracking_events");

            migrationBuilder.DropIndex(
                name: "IX_consignment_stops_OrganizationId",
                schema: "logistics",
                table: "consignment_stops");

            migrationBuilder.DropIndex(
                name: "IX_consignment_labels_OrganizationId",
                schema: "logistics",
                table: "consignment_labels");

            migrationBuilder.DropIndex(
                name: "IX_consignment_deliveries_OrganizationId",
                schema: "logistics",
                table: "consignment_deliveries");

            migrationBuilder.DropIndex(
                name: "IX_consignment_charges_OrganizationId",
                schema: "logistics",
                table: "consignment_charges");

            migrationBuilder.DropIndex(
                name: "IX_carrier_credentials_OrganizationId",
                schema: "logistics",
                table: "carrier_credentials");
        }
    }
}
