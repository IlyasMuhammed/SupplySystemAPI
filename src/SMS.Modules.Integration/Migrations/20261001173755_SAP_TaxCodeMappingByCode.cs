using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Integration.Migrations
{
    /// <inheritdoc />
    public partial class SAP_TaxCodeMappingByCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guarded, not a blind DropIndex: this reaches the shared database on API start, and a
            // database that drifted must not stop the API from starting.
            migrationBuilder.Sql(
                "IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TaxCodeMappings_ConnectionId_TaxPercent' " +
                "AND object_id = OBJECT_ID(N'[integration].[TaxCodeMappings]')) " +
                "DROP INDEX [IX_TaxCodeMappings_ConnectionId_TaxPercent] ON [integration].[TaxCodeMappings];");

            migrationBuilder.AddColumn<string>(
                name: "SourceTaxCode",
                schema: "integration",
                table: "TaxCodeMappings",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxCodeMappings_ConnectionId_SourceTaxCode",
                schema: "integration",
                table: "TaxCodeMappings",
                columns: new[] { "ConnectionId", "SourceTaxCode" },
                unique: true,
                filter: "[SourceTaxCode] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TaxCodeMappings_ConnectionId_TaxPercent",
                schema: "integration",
                table: "TaxCodeMappings",
                columns: new[] { "ConnectionId", "TaxPercent" },
                unique: true,
                filter: "[SourceTaxCode] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaxCodeMappings_ConnectionId_SourceTaxCode",
                schema: "integration",
                table: "TaxCodeMappings");

            migrationBuilder.DropIndex(
                name: "IX_TaxCodeMappings_ConnectionId_TaxPercent",
                schema: "integration",
                table: "TaxCodeMappings");

            migrationBuilder.DropColumn(
                name: "SourceTaxCode",
                schema: "integration",
                table: "TaxCodeMappings");

            migrationBuilder.CreateIndex(
                name: "IX_TaxCodeMappings_ConnectionId_TaxPercent",
                schema: "integration",
                table: "TaxCodeMappings",
                columns: new[] { "ConnectionId", "TaxPercent" },
                unique: true);
        }
    }
}
