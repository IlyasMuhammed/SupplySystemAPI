using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Material.Migrations
{
    /// <inheritdoc />
    public partial class A37_BomUsageAndModifiedAt : Migration
    {
        /// <inheritdoc />
        // A37 D-11 / D-16 — material.bill_of_materials.BomUsage (every existing BOM is UNIVERSAL through the DEFAULT) and
        // ModifiedAt on the header and lines, backfilled from the header's UpdatedAt. Additive and guarded: every API
        // start replays migrations on the drifted shared database. Backfills run through EXEC (a batch that adds a column
        // cannot name it in a statement compiled with it) run only in the batch that adds the column. New rows default to SYSUTCDATETIME() (writers outside EF).
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF COL_LENGTH('material.bill_of_materials', 'BomUsage') IS NULL
                    ALTER TABLE [material].[bill_of_materials] ADD [BomUsage] nvarchar(30) NOT NULL
                        CONSTRAINT [DF_bill_of_materials_BomUsage] DEFAULT 'UNIVERSAL';
                IF COL_LENGTH('material.bill_of_materials', 'ModifiedAt') IS NULL
                BEGIN
                    ALTER TABLE [material].[bill_of_materials] ADD [ModifiedAt] datetime2 NOT NULL
                        CONSTRAINT [DF_bill_of_materials_ModifiedAt] DEFAULT SYSUTCDATETIME();
                    EXEC('UPDATE [material].[bill_of_materials] SET [ModifiedAt] = [UpdatedAt];');
                END
                IF COL_LENGTH('material.bill_of_material_lines', 'ModifiedAt') IS NULL
                BEGIN
                    ALTER TABLE [material].[bill_of_material_lines] ADD [ModifiedAt] datetime2 NOT NULL
                        CONSTRAINT [DF_bill_of_material_lines_ModifiedAt] DEFAULT SYSUTCDATETIME();
                    EXEC('UPDATE l SET [ModifiedAt] = b.[UpdatedAt] FROM [material].[bill_of_material_lines] l
                          JOIN [material].[bill_of_materials] b ON b.[Id] = l.[BomId];');
                END
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_bill_of_materials_OrganizationId_ModifiedAt'
                               AND object_id = OBJECT_ID('material.bill_of_materials'))
                    EXEC('CREATE INDEX [IX_bill_of_materials_OrganizationId_ModifiedAt]
                          ON [material].[bill_of_materials] ([OrganizationId], [ModifiedAt]);');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_bill_of_materials_OrganizationId_ModifiedAt'
                           AND object_id = OBJECT_ID('material.bill_of_materials'))
                    DROP INDEX [IX_bill_of_materials_OrganizationId_ModifiedAt] ON [material].[bill_of_materials];
                """);
            foreach (var (table, column) in new[]
            {
                ("bill_of_material_lines", "ModifiedAt"), ("bill_of_materials", "ModifiedAt"), ("bill_of_materials", "BomUsage")
            })
                migrationBuilder.Sql($"""
                    IF OBJECT_ID('material.DF_{table}_{column}', 'D') IS NOT NULL
                        ALTER TABLE [material].[{table}] DROP CONSTRAINT [DF_{table}_{column}];
                    IF COL_LENGTH('material.{table}', '{column}') IS NOT NULL
                        ALTER TABLE [material].[{table}] DROP COLUMN [{column}];
                    """);
        }
    }
}