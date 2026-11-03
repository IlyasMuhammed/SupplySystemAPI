using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Integration.Migrations
{
    /// <summary>
    /// A49 G-5 / G-15 (docs/hrm/ADDENDUM-49-ANALYSIS.md D-8) — the cost center → QuickBooks Class/Location table, and a disabled + held
    /// sync-control row for every HRM document type on every existing connection, because "no row = enabled" (A44 BR-29) would otherwise
    /// send payroll to QuickBooks the day the JE path ships. Guarded: everything only when missing, so re-running changes nothing and a
    /// row an accountant has since enabled stays enabled. Additive.
    /// </summary>
    public partial class A49_CostCenterQboMappings : Migration
    {
        /// <summary>Kept as constants so the tests run exactly this on LocalDB.</summary>
        internal const string CreateTableSql = """
            IF OBJECT_ID(N'integration.CostCenterQboMappings', N'U') IS NULL
            BEGIN
                CREATE TABLE [integration].[CostCenterQboMappings] (
                    [Id] int NOT NULL IDENTITY(1, 1),
                    [Uuid] uniqueidentifier NOT NULL,
                    [OrganizationId] uniqueidentifier NOT NULL,
                    [ConnectionId] int NOT NULL,
                    [CostCenterCode] nvarchar(20) NOT NULL,
                    [CostCenterName] nvarchar(100) NOT NULL,
                    [QboClassRef] nvarchar(50) NULL,
                    [QboClassName] nvarchar(100) NULL,
                    [QboDepartmentRef] nvarchar(50) NULL,
                    [QboDepartmentName] nvarchar(100) NULL,
                    [IsActive] bit NOT NULL,
                    [CreatedAt] datetime2 NOT NULL,
                    [CreatedBy] int NULL,
                    [ModifiedAt] datetime2 NULL,
                    [ModifiedBy] int NULL,
                    CONSTRAINT [PK_CostCenterQboMappings] PRIMARY KEY ([Id])
                );
            END;
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CostCenterQboMappings_ConnectionId_CostCenterCode'
                           AND object_id = OBJECT_ID(N'integration.CostCenterQboMappings'))
                CREATE UNIQUE INDEX [IX_CostCenterQboMappings_ConnectionId_CostCenterCode]
                    ON [integration].[CostCenterQboMappings] ([ConnectionId], [CostCenterCode]);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CostCenterQboMappings_OrganizationId'
                           AND object_id = OBJECT_ID(N'integration.CostCenterQboMappings'))
                CREATE INDEX [IX_CostCenterQboMappings_OrganizationId]
                    ON [integration].[CostCenterQboMappings] ([OrganizationId]);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CostCenterQboMappings_Uuid'
                           AND object_id = OBJECT_ID(N'integration.CostCenterQboMappings'))
                CREATE UNIQUE INDEX [IX_CostCenterQboMappings_Uuid]
                    ON [integration].[CostCenterQboMappings] ([Uuid]);
            """;

        internal const string SeedSyncControlSql = """
            IF OBJECT_ID(N'integration.SyncControlConfigs', N'U') IS NOT NULL AND OBJECT_ID(N'integration.Connections', N'U') IS NOT NULL
            INSERT INTO [integration].[SyncControlConfigs] ([OrganizationId], [ConnectionId], [DocumentType], [IsEnabled], [HoldWhenDisabled], [DisplayLabel])
            SELECT c.[OrganizationId], c.[Id], v.[DocumentType], 0, 1, v.[Label]
            FROM [integration].[Connections] c
            CROSS JOIN (VALUES
                (N'PayrollRun', N'Payroll runs'), (N'PayrollPayment', N'Salary payments'), (N'LoanDisbursement', N'Loan disbursements'),
                (N'Expense', N'Expense claims'), (N'ExpensePayment', N'Expense payments'), (N'StatutoryRemittance', N'Statutory remittances'),
                (N'FinalSettlement', N'Final settlements'), (N'PayrollProvision', N'Payroll provisions')
            ) AS v([DocumentType], [Label])
            WHERE NOT EXISTS (SELECT 1 FROM [integration].[SyncControlConfigs] s WHERE s.[ConnectionId] = c.[Id] AND s.[DocumentType] = v.[DocumentType]);
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CreateTableSql);
            migrationBuilder.Sql(SeedSyncControlSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM [integration].[SyncControlConfigs] WHERE [DocumentType] IN
                    (N'PayrollRun', N'PayrollPayment', N'LoanDisbursement', N'Expense', N'ExpensePayment', N'StatutoryRemittance', N'FinalSettlement', N'PayrollProvision');
                """);
            migrationBuilder.DropTable(
                name: "CostCenterQboMappings",
                schema: "integration");
        }
    }
}
