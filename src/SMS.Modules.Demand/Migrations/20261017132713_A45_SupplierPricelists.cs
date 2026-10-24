using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Demand.Migrations
{
    /// <inheritdoc />
    public partial class A45_SupplierPricelists : Migration
    {
        /// <inheritdoc />
        // A45 M1/M2/M3 (D-14) — hand-guarded and idempotent: every API start runs migrations against the shared database, which
        // may already hold any part of this. Each statement is its own batch so a guard never depends on an earlier ALTER's parse.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── M2: purchase_order_lines price-source columns ──
            migrationBuilder.Sql(@"
IF COL_LENGTH('demand.purchase_order_lines', 'PriceSource') IS NULL
    ALTER TABLE [demand].[purchase_order_lines] ADD [PriceSource] tinyint NOT NULL CONSTRAINT [DF_purchase_order_lines_PriceSource] DEFAULT 0;");
            migrationBuilder.Sql(@"
IF COL_LENGTH('demand.purchase_order_lines', 'SystemResolvedPrice') IS NULL
    ALTER TABLE [demand].[purchase_order_lines] ADD [SystemResolvedPrice] decimal(18,4) NULL;");
            migrationBuilder.Sql(@"
IF COL_LENGTH('demand.purchase_order_lines', 'SupplierPricelistUuid') IS NULL
    ALTER TABLE [demand].[purchase_order_lines] ADD [SupplierPricelistUuid] uniqueidentifier NULL;");
            migrationBuilder.Sql(@"
IF COL_LENGTH('demand.purchase_order_lines', 'PriceOverrideReason') IS NULL
    ALTER TABLE [demand].[purchase_order_lines] ADD [PriceOverrideReason] nvarchar(500) NULL;");
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_POLines_PriceSource' AND parent_object_id = OBJECT_ID('demand.purchase_order_lines'))
    ALTER TABLE [demand].[purchase_order_lines] ADD CONSTRAINT [CK_POLines_PriceSource] CHECK ([PriceSource] IN (0, 1, 2, 3));");
            // M3 — existing lines read as Manual. The column default already did that; this only repairs a row a partial earlier run left odd.
            migrationBuilder.Sql(@"
UPDATE [demand].[purchase_order_lines] SET [PriceSource] = 0 WHERE [PriceSource] NOT IN (0, 1, 2, 3);");

            // ── M1: demand.SupplierPricelists ──
            migrationBuilder.Sql(@"
IF OBJECT_ID('demand.SupplierPricelists', 'U') IS NULL
CREATE TABLE [demand].[SupplierPricelists]
(
    [Id]                int              IDENTITY(1,1) NOT NULL,
    [UUID]              uniqueidentifier NOT NULL,
    [OrganizationId]    uniqueidentifier NOT NULL,
    [ProductUuid]       uniqueidentifier NOT NULL,
    [VariantUuid]       uniqueidentifier NULL,
    [SupplierUuid]      uniqueidentifier NOT NULL,
    [VendorProductCode] nvarchar(100)    NULL,
    [VendorProductName] nvarchar(255)    NULL,
    [MinQuantity]       decimal(18,4)    NOT NULL,
    [UnitPrice]         decimal(18,4)    NOT NULL,
    [CurrencyId]        uniqueidentifier NOT NULL,
    [PricingDateBasis]  tinyint          NOT NULL,
    [ValidFrom]         date             NOT NULL,
    [ValidTo]           date             NOT NULL,
    [LeadTimeDays]      int              NULL,
    [Priority]          int              NOT NULL,
    [Notes]             nvarchar(500)    NULL,
    [IsActive]          bit              NOT NULL,
    [CreatedBy]         int              NOT NULL,
    [CreatedDate]       datetime2        NOT NULL,
    [ModifiedBy]        int              NULL,
    [ModifiedDate]      datetime2        NULL,
    [RowVersion]        rowversion       NOT NULL,
    CONSTRAINT [PK_SupplierPricelists] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [CK_SupplierPricelists_Price]     CHECK ([UnitPrice] > 0),
    CONSTRAINT [CK_SupplierPricelists_MinQty]    CHECK ([MinQuantity] >= 0),
    CONSTRAINT [CK_SupplierPricelists_Dates]     CHECK ([ValidTo] >= [ValidFrom]),
    CONSTRAINT [CK_SupplierPricelists_DateBasis] CHECK ([PricingDateBasis] IN (0, 1))
);");
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SupplierPricelists_Lookup' AND object_id = OBJECT_ID('demand.SupplierPricelists'))
    CREATE INDEX [IX_SupplierPricelists_Lookup] ON [demand].[SupplierPricelists]
        ([OrganizationId], [SupplierUuid], [ProductUuid], [IsActive], [ValidFrom], [ValidTo])
        INCLUDE ([VariantUuid], [UnitPrice], [MinQuantity], [Priority], [PricingDateBasis], [CurrencyId], [LeadTimeDays]);");
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SupplierPricelists_Supplier' AND object_id = OBJECT_ID('demand.SupplierPricelists'))
    CREATE INDEX [IX_SupplierPricelists_Supplier] ON [demand].[SupplierPricelists] ([OrganizationId], [SupplierUuid], [IsActive])
        INCLUDE ([ProductUuid]);");
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SupplierPricelists_Variant' AND object_id = OBJECT_ID('demand.SupplierPricelists'))
    CREATE INDEX [IX_SupplierPricelists_Variant] ON [demand].[SupplierPricelists] ([OrganizationId], [VariantUuid], [SupplierUuid], [IsActive])
        WHERE [VariantUuid] IS NOT NULL;");
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UQ_SupplierPricelists_NoDuplicate' AND object_id = OBJECT_ID('demand.SupplierPricelists'))
    CREATE UNIQUE INDEX [UQ_SupplierPricelists_NoDuplicate] ON [demand].[SupplierPricelists]
        ([OrganizationId], [ProductUuid], [VariantUuid], [SupplierUuid], [MinQuantity], [ValidFrom], [ValidTo])
        WHERE [IsActive] = 1;");
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_SupplierPricelists_UUID' AND object_id = OBJECT_ID('demand.SupplierPricelists'))
    CREATE UNIQUE INDEX [UX_SupplierPricelists_UUID] ON [demand].[SupplierPricelists] ([UUID]);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"IF OBJECT_ID('demand.SupplierPricelists', 'U') IS NOT NULL DROP TABLE [demand].[SupplierPricelists];");
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_POLines_PriceSource' AND parent_object_id = OBJECT_ID('demand.purchase_order_lines'))
    ALTER TABLE [demand].[purchase_order_lines] DROP CONSTRAINT [CK_POLines_PriceSource];");
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_purchase_order_lines_PriceSource')
    ALTER TABLE [demand].[purchase_order_lines] DROP CONSTRAINT [DF_purchase_order_lines_PriceSource];");
            foreach (var column in new[] { "PriceOverrideReason", "PriceSource", "SupplierPricelistUuid", "SystemResolvedPrice" })
                migrationBuilder.Sql($@"
IF COL_LENGTH('demand.purchase_order_lines', '{column}') IS NOT NULL
    ALTER TABLE [demand].[purchase_order_lines] DROP COLUMN [{column}];");
        }
    }
}
