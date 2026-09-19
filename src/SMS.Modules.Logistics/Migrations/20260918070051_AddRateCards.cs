using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddRateCards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rate_cards",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CarrierId = table.Column<int>(type: "int", nullable: false),
                    ServiceCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MinimumCharge = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    FuelSurchargePercent = table.Column<decimal>(type: "decimal(9,4)", nullable: true),
                    CodFeePercent = table.Column<decimal>(type: "decimal(9,4)", nullable: true),
                    CodFeeMinimum = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    IsDelete = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rate_cards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_rate_cards_carriers_CarrierId",
                        column: x => x.CarrierId,
                        principalSchema: "logistics",
                        principalTable: "carriers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "rate_card_lanes",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RateCardId = table.Column<int>(type: "int", nullable: false),
                    OriginCountryIso = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    OriginPostcodePrefix = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    DestinationCountryIso = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    DestinationPostcodePrefix = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    IsDelete = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rate_card_lanes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_rate_card_lanes_rate_cards_RateCardId",
                        column: x => x.RateCardId,
                        principalSchema: "logistics",
                        principalTable: "rate_cards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rate_card_breaks",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RateCardLaneId = table.Column<int>(type: "int", nullable: false),
                    FromWeightKg = table.Column<decimal>(type: "decimal(18,3)", nullable: false),
                    Basis = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    IsDelete = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rate_card_breaks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_rate_card_breaks_rate_card_lanes_RateCardLaneId",
                        column: x => x.RateCardLaneId,
                        principalSchema: "logistics",
                        principalTable: "rate_card_lanes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_rate_card_breaks_RateCardLaneId_FromWeightKg",
                schema: "logistics",
                table: "rate_card_breaks",
                columns: new[] { "RateCardLaneId", "FromWeightKg" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rate_card_breaks_UUID",
                schema: "logistics",
                table: "rate_card_breaks",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rate_card_lanes_RateCardId_OriginCountryIso_OriginPostcodePrefix_DestinationCountryIso_DestinationPostcodePrefix",
                schema: "logistics",
                table: "rate_card_lanes",
                columns: new[] { "RateCardId", "OriginCountryIso", "OriginPostcodePrefix", "DestinationCountryIso", "DestinationPostcodePrefix" },
                unique: true,
                filter: "[OriginCountryIso] IS NOT NULL AND [OriginPostcodePrefix] IS NOT NULL AND [DestinationCountryIso] IS NOT NULL AND [DestinationPostcodePrefix] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_rate_card_lanes_UUID",
                schema: "logistics",
                table: "rate_card_lanes",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rate_cards_CarrierId",
                schema: "logistics",
                table: "rate_cards",
                column: "CarrierId");

            migrationBuilder.CreateIndex(
                name: "IX_rate_cards_OrganizationId_CarrierId_ServiceCode_EffectiveFrom",
                schema: "logistics",
                table: "rate_cards",
                columns: new[] { "OrganizationId", "CarrierId", "ServiceCode", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_rate_cards_UUID",
                schema: "logistics",
                table: "rate_cards",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rate_card_breaks",
                schema: "logistics");

            migrationBuilder.DropTable(
                name: "rate_card_lanes",
                schema: "logistics");

            migrationBuilder.DropTable(
                name: "rate_cards",
                schema: "logistics");
        }
    }
}
