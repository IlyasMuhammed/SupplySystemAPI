using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddCarrierCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "carrier_credentials",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CarrierAccountId = table.Column<int>(type: "int", nullable: false),
                    CredentialKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EncryptedValue = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDelete = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_carrier_credentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_carrier_credentials_carrier_accounts_CarrierAccountId",
                        column: x => x.CarrierAccountId,
                        principalSchema: "logistics",
                        principalTable: "carrier_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_carrier_credentials_CarrierAccountId_CredentialKey",
                schema: "logistics",
                table: "carrier_credentials",
                columns: new[] { "CarrierAccountId", "CredentialKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_carrier_credentials_UUID",
                schema: "logistics",
                table: "carrier_credentials",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "carrier_credentials",
                schema: "logistics");
        }
    }
}
