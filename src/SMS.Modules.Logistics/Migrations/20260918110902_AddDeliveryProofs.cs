using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryProofs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "delivery_proofs",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConsignmentId = table.Column<int>(type: "int", nullable: false),
                    ConsignmentStopId = table.Column<int>(type: "int", nullable: true),
                    ReceivedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Relationship = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DeliveredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Location = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TrackingEventId = table.Column<int>(type: "int", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsDelete = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_proofs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_delivery_proofs_consignment_stops_ConsignmentStopId",
                        column: x => x.ConsignmentStopId,
                        principalSchema: "logistics",
                        principalTable: "consignment_stops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delivery_proofs_consignment_tracking_events_TrackingEventId",
                        column: x => x.TrackingEventId,
                        principalSchema: "logistics",
                        principalTable: "consignment_tracking_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_delivery_proofs_consignments_ConsignmentId",
                        column: x => x.ConsignmentId,
                        principalSchema: "logistics",
                        principalTable: "consignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "delivery_proof_files",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveryProofId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    SizeBytes = table.Column<int>(type: "int", nullable: false),
                    Sha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    IsDelete = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_proof_files", x => x.Id);
                    table.ForeignKey(
                        name: "FK_delivery_proof_files_delivery_proofs_DeliveryProofId",
                        column: x => x.DeliveryProofId,
                        principalSchema: "logistics",
                        principalTable: "delivery_proofs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_proof_files_DeliveryProofId_Sha256",
                schema: "logistics",
                table: "delivery_proof_files",
                columns: new[] { "DeliveryProofId", "Sha256" },
                unique: true,
                filter: "[IsDelete] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_proof_files_UUID",
                schema: "logistics",
                table: "delivery_proof_files",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_delivery_proofs_ConsignmentId_ConsignmentStopId",
                schema: "logistics",
                table: "delivery_proofs",
                columns: new[] { "ConsignmentId", "ConsignmentStopId" },
                unique: true,
                filter: "[IsDelete] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_proofs_ConsignmentStopId",
                schema: "logistics",
                table: "delivery_proofs",
                column: "ConsignmentStopId");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_proofs_TrackingEventId",
                schema: "logistics",
                table: "delivery_proofs",
                column: "TrackingEventId",
                unique: true,
                filter: "[TrackingEventId] IS NOT NULL AND [IsDelete] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_proofs_UUID",
                schema: "logistics",
                table: "delivery_proofs",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "delivery_proof_files",
                schema: "logistics");

            migrationBuilder.DropTable(
                name: "delivery_proofs",
                schema: "logistics");
        }
    }
}
