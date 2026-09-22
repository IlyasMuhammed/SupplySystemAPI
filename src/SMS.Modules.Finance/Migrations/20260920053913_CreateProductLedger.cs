using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Finance.Migrations
{
    /// <inheritdoc />
    public partial class CreateProductLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "product_ledger",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VariantUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SequenceNo = table.Column<int>(type: "int", nullable: false),
                    EntryDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EntryType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ReferenceType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ReferenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReferenceNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PartnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    UnitCost = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    TotalCost = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Direction = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    RunningQty = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    RunningValue = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Narration = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_ledger", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_product_ledger_OrganizationId_ProductUuid_EntryDate",
                schema: "finance",
                table: "product_ledger",
                columns: new[] { "OrganizationId", "ProductUuid", "EntryDate" });

            migrationBuilder.CreateIndex(
                name: "IX_product_ledger_OrganizationId_VariantUuid_EntryDate",
                schema: "finance",
                table: "product_ledger",
                columns: new[] { "OrganizationId", "VariantUuid", "EntryDate" });

            migrationBuilder.CreateIndex(
                name: "IX_product_ledger_OrganizationId_VariantUuid_SequenceNo",
                schema: "finance",
                table: "product_ledger",
                columns: new[] { "OrganizationId", "VariantUuid", "SequenceNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_product_ledger_UUID",
                schema: "finance",
                table: "product_ledger",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "product_ledger",
                schema: "finance");
        }
    }
}
