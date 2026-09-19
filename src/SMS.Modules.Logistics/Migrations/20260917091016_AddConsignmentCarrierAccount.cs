using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddConsignmentCarrierAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CarrierAccountId",
                schema: "logistics",
                table: "consignments",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_consignments_CarrierAccountId",
                schema: "logistics",
                table: "consignments",
                column: "CarrierAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_consignments_carrier_accounts_CarrierAccountId",
                schema: "logistics",
                table: "consignments",
                column: "CarrierAccountId",
                principalSchema: "logistics",
                principalTable: "carrier_accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_consignments_carrier_accounts_CarrierAccountId",
                schema: "logistics",
                table: "consignments");

            migrationBuilder.DropIndex(
                name: "IX_consignments_CarrierAccountId",
                schema: "logistics",
                table: "consignments");

            migrationBuilder.DropColumn(
                name: "CarrierAccountId",
                schema: "logistics",
                table: "consignments");
        }
    }
}
