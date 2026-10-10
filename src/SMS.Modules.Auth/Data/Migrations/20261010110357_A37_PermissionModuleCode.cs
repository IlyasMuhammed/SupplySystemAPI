using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Auth.Data.Migrations
{
    /// <summary>
    /// A37 D-14 (docs/module-registry/ADDENDUM-37-ANALYSIS.md) — auth.Permissions.ModuleCode, the module a permission
    /// belongs to. Filled by AuthDataSeeder from the code-prefix map (SMS.Shared ModuleCodeMap) on every start. Additive
    /// and guarded: every API start migrates the drifted shared database.
    /// </summary>
    public partial class A37_PermissionModuleCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF COL_LENGTH('auth.Permissions', 'ModuleCode') IS NULL
                    ALTER TABLE [auth].[Permissions] ADD [ModuleCode] nvarchar(50) NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF COL_LENGTH('auth.Permissions', 'ModuleCode') IS NOT NULL
                    ALTER TABLE [auth].[Permissions] DROP COLUMN [ModuleCode];
                """);
        }
    }
}
