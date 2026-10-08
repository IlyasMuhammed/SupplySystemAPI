using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class A34_LeadTimes : Migration
    {
        /// <inheritdoc />
        // A34 M2 (A34-PB-01, D-10, D-11) — lead-time management in Inventory instead of lookups (Lookups never migrates).
        //  * inventory.ProductVariants: 7 nullable overrides. The existing LeadTimeDays becomes the supplier override
        //    (D-11), so there are 7 new columns, not 8. NULL = use the fallback; every existing variant starts NULL.
        //  * inventory.LeadTimeDefaults: one row per organization (unique OrganizationId), with the §5.4 column defaults
        //    1/3/1/0/0/0. No rows are inserted (D-10: a missing row reads as the system defaults; PUT upserts), and
        //    nothing is copied from Product.LeadTimeDays (D-12: it is the read-time fallback).
        // Guarded, because every API start replays migrations on the drifted shared database. The column defaults live
        // only here, not in the EF model (see LeadTimeDefaultsMap).
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF COL_LENGTH('inventory.ProductVariants', 'ManufacturingLeadTimeDays') IS NULL
                    ALTER TABLE [inventory].[ProductVariants] ADD [ManufacturingLeadTimeDays] int NULL;
                IF COL_LENGTH('inventory.ProductVariants', 'ManufacturingBufferDays') IS NULL
                    ALTER TABLE [inventory].[ProductVariants] ADD [ManufacturingBufferDays] int NULL;
                IF COL_LENGTH('inventory.ProductVariants', 'QualityInspectionDays') IS NULL
                    ALTER TABLE [inventory].[ProductVariants] ADD [QualityInspectionDays] int NULL;
                IF COL_LENGTH('inventory.ProductVariants', 'InternalTransferDays') IS NULL
                    ALTER TABLE [inventory].[ProductVariants] ADD [InternalTransferDays] int NULL;
                IF COL_LENGTH('inventory.ProductVariants', 'PickPackDays') IS NULL
                    ALTER TABLE [inventory].[ProductVariants] ADD [PickPackDays] int NULL;
                IF COL_LENGTH('inventory.ProductVariants', 'ShippingLeadTimeDays') IS NULL
                    ALTER TABLE [inventory].[ProductVariants] ADD [ShippingLeadTimeDays] int NULL;
                IF COL_LENGTH('inventory.ProductVariants', 'SalesBufferDays') IS NULL
                    ALTER TABLE [inventory].[ProductVariants] ADD [SalesBufferDays] int NULL;
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'inventory.LeadTimeDefaults', N'U') IS NULL
                    CREATE TABLE [inventory].[LeadTimeDefaults] (
                        [Id]                      int              NOT NULL IDENTITY(1, 1),
                        [Uuid]                    uniqueidentifier NOT NULL,
                        [OrganizationId]          uniqueidentifier NOT NULL,
                        [PickPackDays]            int NOT NULL CONSTRAINT [DF_LeadTimeDefaults_PickPackDays]            DEFAULT (1),
                        [ShippingLeadTimeDays]    int NOT NULL CONSTRAINT [DF_LeadTimeDefaults_ShippingLeadTimeDays]    DEFAULT (3),
                        [SalesBufferDays]         int NOT NULL CONSTRAINT [DF_LeadTimeDefaults_SalesBufferDays]         DEFAULT (1),
                        [ManufacturingBufferDays] int NOT NULL CONSTRAINT [DF_LeadTimeDefaults_ManufacturingBufferDays] DEFAULT (0),
                        [QualityInspectionDays]   int NOT NULL CONSTRAINT [DF_LeadTimeDefaults_QualityInspectionDays]   DEFAULT (0),
                        [InternalTransferDays]    int NOT NULL CONSTRAINT [DF_LeadTimeDefaults_InternalTransferDays]    DEFAULT (0),
                        [CreatedBy]               int       NOT NULL,
                        [CreatedDate]             datetime2 NOT NULL,
                        [ModifiedBy]              int       NULL,
                        [ModifiedDate]            datetime2 NULL,
                        [RowVersion]              rowversion NOT NULL,
                        CONSTRAINT [PK_LeadTimeDefaults] PRIMARY KEY ([Id])
                    );
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes
                               WHERE name = 'IX_LeadTimeDefaults_OrganizationId'
                                 AND object_id = OBJECT_ID('inventory.LeadTimeDefaults'))
                    CREATE UNIQUE INDEX [IX_LeadTimeDefaults_OrganizationId]
                        ON [inventory].[LeadTimeDefaults] ([OrganizationId]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes
                               WHERE name = 'IX_LeadTimeDefaults_Uuid'
                                 AND object_id = OBJECT_ID('inventory.LeadTimeDefaults'))
                    CREATE UNIQUE INDEX [IX_LeadTimeDefaults_Uuid]
                        ON [inventory].[LeadTimeDefaults] ([Uuid]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'inventory.LeadTimeDefaults', N'U') IS NOT NULL
                    DROP TABLE [inventory].[LeadTimeDefaults];
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH('inventory.ProductVariants', 'ManufacturingLeadTimeDays') IS NOT NULL
                    ALTER TABLE [inventory].[ProductVariants] DROP COLUMN [ManufacturingLeadTimeDays];
                IF COL_LENGTH('inventory.ProductVariants', 'ManufacturingBufferDays') IS NOT NULL
                    ALTER TABLE [inventory].[ProductVariants] DROP COLUMN [ManufacturingBufferDays];
                IF COL_LENGTH('inventory.ProductVariants', 'QualityInspectionDays') IS NOT NULL
                    ALTER TABLE [inventory].[ProductVariants] DROP COLUMN [QualityInspectionDays];
                IF COL_LENGTH('inventory.ProductVariants', 'InternalTransferDays') IS NOT NULL
                    ALTER TABLE [inventory].[ProductVariants] DROP COLUMN [InternalTransferDays];
                IF COL_LENGTH('inventory.ProductVariants', 'PickPackDays') IS NOT NULL
                    ALTER TABLE [inventory].[ProductVariants] DROP COLUMN [PickPackDays];
                IF COL_LENGTH('inventory.ProductVariants', 'ShippingLeadTimeDays') IS NOT NULL
                    ALTER TABLE [inventory].[ProductVariants] DROP COLUMN [ShippingLeadTimeDays];
                IF COL_LENGTH('inventory.ProductVariants', 'SalesBufferDays') IS NOT NULL
                    ALTER TABLE [inventory].[ProductVariants] DROP COLUMN [SalesBufferDays];
                """);
        }
    }
}
