using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class RetireRatePerKgAndProofOfDeliveryUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProofOfDeliveryUrl",
                schema: "logistics",
                table: "shipments");

            migrationBuilder.DropColumn(
                name: "RatePerKg",
                schema: "logistics",
                table: "carriers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProofOfDeliveryUrl",
                schema: "logistics",
                table: "shipments",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RatePerKg",
                schema: "logistics",
                table: "carriers",
                type: "decimal(18,4)",
                nullable: true);
        }
    }
}
