using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddCarrierCommandLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "carrier_commands",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommandType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RequestFingerprint = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ConsignmentId = table.Column<int>(type: "int", nullable: false),
                    CarrierAccountId = table.Column<int>(type: "int", nullable: true),
                    ProviderKey = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    FirstAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LeaseExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AwbNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CarrierReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TrackingUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Cost = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    CostCurrency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: true),
                    CarrierErrorCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RawResponse = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FailureDetail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ResolvedBy = table.Column<int>(type: "int", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolutionNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_carrier_commands", x => x.Id);
                    table.ForeignKey(
                        name: "FK_carrier_commands_carrier_accounts_CarrierAccountId",
                        column: x => x.CarrierAccountId,
                        principalSchema: "logistics",
                        principalTable: "carrier_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_carrier_commands_consignments_ConsignmentId",
                        column: x => x.ConsignmentId,
                        principalSchema: "logistics",
                        principalTable: "consignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_carrier_commands_CarrierAccountId",
                schema: "logistics",
                table: "carrier_commands",
                column: "CarrierAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_carrier_commands_ConsignmentId",
                schema: "logistics",
                table: "carrier_commands",
                column: "ConsignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_carrier_commands_OrganizationId_CommandType_IdempotencyKey",
                schema: "logistics",
                table: "carrier_commands",
                columns: new[] { "OrganizationId", "CommandType", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_carrier_commands_OrganizationId_Status",
                schema: "logistics",
                table: "carrier_commands",
                columns: new[] { "OrganizationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_carrier_commands_UUID",
                schema: "logistics",
                table: "carrier_commands",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "carrier_commands",
                schema: "logistics");
        }
    }
}
