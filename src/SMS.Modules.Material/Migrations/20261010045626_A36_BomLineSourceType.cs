using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Material.Migrations
{
    /// <inheritdoc />
    public partial class A36_BomLineSourceType : Migration
    {
        /// <inheritdoc />
        // A36 M2 (A36-P1-06, D-4) — where a BOM line's input comes from. Additive only: every existing line becomes
        // STOCK through the DEFAULT constraint; the subcontract supplier is a nullable business-partner UUID (no FK,
        // cross-module). Guarded, because every API start replays migrations on the drifted shared database.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF COL_LENGTH('material.bill_of_material_lines', 'SourceType') IS NULL
                    ALTER TABLE [material].[bill_of_material_lines] ADD [SourceType] nvarchar(20) NOT NULL
                        CONSTRAINT [DF_bill_of_material_lines_SourceType] DEFAULT 'STOCK';
                IF COL_LENGTH('material.bill_of_material_lines', 'SubcontractSupplierUuid') IS NULL
                    ALTER TABLE [material].[bill_of_material_lines] ADD [SubcontractSupplierUuid] uniqueidentifier NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF OBJECT_ID('material.DF_bill_of_material_lines_SourceType', 'D') IS NOT NULL
                    ALTER TABLE [material].[bill_of_material_lines] DROP CONSTRAINT [DF_bill_of_material_lines_SourceType];
                IF COL_LENGTH('material.bill_of_material_lines', 'SourceType') IS NOT NULL
                    ALTER TABLE [material].[bill_of_material_lines] DROP COLUMN [SourceType];
                IF COL_LENGTH('material.bill_of_material_lines', 'SubcontractSupplierUuid') IS NOT NULL
                    ALTER TABLE [material].[bill_of_material_lines] DROP COLUMN [SubcontractSupplierUuid];
                """);
        }
    }
}
