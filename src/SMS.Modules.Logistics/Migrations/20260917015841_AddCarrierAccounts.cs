using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddCarrierAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "carrier_accounts",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CarrierId = table.Column<int>(type: "int", nullable: false),
                    AccountName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    AccountNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    IsSandbox = table.Column<bool>(type: "bit", nullable: false),
                    DefaultServiceCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CodEnabled = table.Column<bool>(type: "bit", nullable: true),
                    LabelsEnabled = table.Column<bool>(type: "bit", nullable: true),
                    TrackingEnabled = table.Column<bool>(type: "bit", nullable: true),
                    CancellationEnabled = table.Column<bool>(type: "bit", nullable: true),
                    PickupBookingEnabled = table.Column<bool>(type: "bit", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    IsDelete = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_carrier_accounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_carrier_accounts_carriers_CarrierId",
                        column: x => x.CarrierId,
                        principalSchema: "logistics",
                        principalTable: "carriers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_carrier_accounts_CarrierId",
                schema: "logistics",
                table: "carrier_accounts",
                column: "CarrierId");

            migrationBuilder.CreateIndex(
                name: "IX_carrier_accounts_OrganizationId_CarrierId_AccountName",
                schema: "logistics",
                table: "carrier_accounts",
                columns: new[] { "OrganizationId", "CarrierId", "AccountName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_carrier_accounts_OrganizationId_CarrierId_IsDefault",
                schema: "logistics",
                table: "carrier_accounts",
                columns: new[] { "OrganizationId", "CarrierId", "IsDefault" });

            migrationBuilder.CreateIndex(
                name: "IX_carrier_accounts_UUID",
                schema: "logistics",
                table: "carrier_accounts",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "carrier_accounts",
                schema: "logistics");
        }
    }
}
