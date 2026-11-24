using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SMS.Modules.Onboarding.Data;

#nullable disable

namespace SMS.Modules.Onboarding.Migrations
{
    /// <summary>
    /// A51 P1 - schema <c>onboarding</c>: Industries and CountryIndustries (unique code; unique (country, industry)).
    /// <b>Guarded and idempotent</b> like A50 (every API start migrates the shared, drifted database): the schema, each table and
    /// each index is created only when missing. Additive only. The rows are seeded by <c>IndustrySeeder</c> at startup.
    /// </summary>
    [DbContext(typeof(OnboardingDbContext))]
    [Migration("20261114090100_A51_Industries")]
    public partial class A51_Industries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF SCHEMA_ID(N'onboarding') IS NULL EXEC(N'CREATE SCHEMA [onboarding];');");

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'[onboarding].[Industries]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [onboarding].[Industries] (
                        [Id] int NOT NULL IDENTITY,
                        [Code] nvarchar(30) NOT NULL,
                        [Name] nvarchar(100) NOT NULL,
                        [Description] nvarchar(500) NULL,
                        [IsActive] bit NOT NULL,
                        [SortOrder] int NOT NULL,
                        [CreatedAt] datetime2 NOT NULL,
                        [CreatedBy] int NULL,
                        [ModifiedAt] datetime2 NOT NULL,
                        [ModifiedBy] int NULL,
                        CONSTRAINT [PK_Industries] PRIMARY KEY ([Id])
                    );
                END;
                """);
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'[onboarding].[CountryIndustries]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [onboarding].[CountryIndustries] (
                        [Id] int NOT NULL IDENTITY,
                        [CountryCode] nvarchar(5) NOT NULL,
                        [IndustryCode] nvarchar(30) NOT NULL,
                        [IsActive] bit NOT NULL,
                        [CreatedAt] datetime2 NOT NULL,
                        [CreatedBy] int NULL,
                        CONSTRAINT [PK_CountryIndustries] PRIMARY KEY ([Id])
                    );
                END;
                """);
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'[onboarding].[Industries]', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_Industries_Code' AND object_id = OBJECT_ID(N'[onboarding].[Industries]'))
                    CREATE UNIQUE INDEX [UQ_Industries_Code] ON [onboarding].[Industries] ([Code]);
                """);
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'[onboarding].[CountryIndustries]', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_CountryIndustries_Key' AND object_id = OBJECT_ID(N'[onboarding].[CountryIndustries]'))
                    CREATE UNIQUE INDEX [UQ_CountryIndustries_Key] ON [onboarding].[CountryIndustries] ([CountryCode], [IndustryCode]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF OBJECT_ID(N'[onboarding].[CountryIndustries]', N'U') IS NOT NULL DROP TABLE [onboarding].[CountryIndustries];");
            migrationBuilder.Sql("IF OBJECT_ID(N'[onboarding].[Industries]', N'U') IS NOT NULL DROP TABLE [onboarding].[Industries];");
        }
    }
}
