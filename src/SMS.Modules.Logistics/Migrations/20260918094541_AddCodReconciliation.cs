using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddCodReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cod_collections",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConsignmentId = table.Column<int>(type: "int", nullable: false),
                    CarrierId = table.Column<int>(type: "int", nullable: true),
                    CarrierName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ExpectedAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    CollectedAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    CollectedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CollectionReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RemittedAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SettledAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    WriteOffReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsDelete = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cod_collections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cod_collections_carriers_CarrierId",
                        column: x => x.CarrierId,
                        principalSchema: "logistics",
                        principalTable: "carriers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cod_collections_consignments_ConsignmentId",
                        column: x => x.ConsignmentId,
                        principalSchema: "logistics",
                        principalTable: "consignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cod_remittances",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CodCollectionId = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cod_remittances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cod_remittances_cod_collections_CodCollectionId",
                        column: x => x.CodCollectionId,
                        principalSchema: "logistics",
                        principalTable: "cod_collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cod_collections_CarrierId",
                schema: "logistics",
                table: "cod_collections",
                column: "CarrierId");

            migrationBuilder.CreateIndex(
                name: "IX_cod_collections_ConsignmentId",
                schema: "logistics",
                table: "cod_collections",
                column: "ConsignmentId",
                unique: true,
                filter: "[IsDelete] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_cod_collections_OrganizationId_Status_CarrierId",
                schema: "logistics",
                table: "cod_collections",
                columns: new[] { "OrganizationId", "Status", "CarrierId" });

            migrationBuilder.CreateIndex(
                name: "IX_cod_collections_UUID",
                schema: "logistics",
                table: "cod_collections",
                column: "UUID",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cod_remittances_CodCollectionId",
                schema: "logistics",
                table: "cod_remittances",
                column: "CodCollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_cod_remittances_OrganizationId_Reference",
                schema: "logistics",
                table: "cod_remittances",
                columns: new[] { "OrganizationId", "Reference" });

            migrationBuilder.CreateIndex(
                name: "IX_cod_remittances_UUID",
                schema: "logistics",
                table: "cod_remittances",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cod_remittances",
                schema: "logistics");

            migrationBuilder.DropTable(
                name: "cod_collections",
                schema: "logistics");
        }
    }
}
