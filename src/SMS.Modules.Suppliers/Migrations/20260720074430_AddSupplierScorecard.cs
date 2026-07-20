using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Suppliers.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierScorecard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GrnScoreDetails",
                schema: "suppliers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GrnId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveryPoints = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    QuantityPoints = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    QualityPoints = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    PricePoints = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    DocumentationPoints = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    TotalRawScore = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    WeightedScore = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    ScoredAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GrnScoreDetails", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScorecardDimensionWeights",
                schema: "suppliers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DimensionCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DimensionName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    WeightPercentage = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    MaxPoints = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScorecardDimensionWeights", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SupplierScoreSnapshots",
                schema: "suppliers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PeriodStart = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PeriodEnd = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DeliveryScore = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    QuantityScore = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    QualityScore = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    PriceScore = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    DocumentationScore = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    TotalScore = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    Grade = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    GrnCount = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierScoreSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GrnScoreDetails_GrnId",
                schema: "suppliers",
                table: "GrnScoreDetails",
                column: "GrnId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScorecardDimensionWeights_DimensionCode",
                schema: "suppliers",
                table: "ScorecardDimensionWeights",
                column: "DimensionCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierScoreSnapshots_SupplierId_PeriodStart_PeriodEnd",
                schema: "suppliers",
                table: "SupplierScoreSnapshots",
                columns: new[] { "SupplierId", "PeriodStart", "PeriodEnd" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierScoreSnapshots_UUID",
                schema: "suppliers",
                table: "SupplierScoreSnapshots",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GrnScoreDetails",
                schema: "suppliers");

            migrationBuilder.DropTable(
                name: "ScorecardDimensionWeights",
                schema: "suppliers");

            migrationBuilder.DropTable(
                name: "SupplierScoreSnapshots",
                schema: "suppliers");
        }
    }
}
