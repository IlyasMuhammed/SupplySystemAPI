using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Accounting.Migrations
{
    /// <summary>
    /// A49 G-4 (docs/hrm/ADDENDUM-49-ANALYSIS.md D-7) — the accounting-default CHECK constraints admit the 16 HR account roles and the
    /// Payroll category. No rows are written: AccountingInitializer adds them, unassigned, only for an organization with MODULE_HRM on.
    /// Guarded (every API start migrates the shared, drifted database): each constraint is replaced only when it does not already
    /// allow the HR values, and created when it is missing. Existing rows all satisfy the wider lists.
    /// </summary>
    public partial class A49_HrDefaultKeys : Migration
    {
        private const string CategoryWithHr = "[Category] IN ('Receivables', 'Payables', 'Banking', 'Revenue', 'CostOfSales', 'Inventory', 'Tax', 'Equity', 'Payroll')";
        private const string CategoryOld = "[Category] IN ('Receivables', 'Payables', 'Banking', 'Revenue', 'CostOfSales', 'Inventory', 'Tax', 'Equity')";
        private const string KeyOld = "[DefaultKey] IN ('DEFAULT_RECEIVABLE', 'DEFAULT_PAYABLE', 'DEFAULT_CASH', 'DEFAULT_BANK', 'DEFAULT_REVENUE', 'DEFAULT_DISCOUNT_GIVEN', 'DEFAULT_EXCHANGE_GAIN_LOSS', 'DEFAULT_ROUNDING', 'DEFAULT_COGS', 'DEFAULT_PURCHASE', 'DEFAULT_DISCOUNT_RECEIVED', 'DEFAULT_INVENTORY', 'DEFAULT_TAX_RECEIVABLE', 'DEFAULT_TAX_PAYABLE', 'DEFAULT_RETAINED_EARNINGS', 'DEFAULT_OPENING_BALANCE')";
        private const string KeyWithHr = "[DefaultKey] IN ('DEFAULT_RECEIVABLE', 'DEFAULT_PAYABLE', 'DEFAULT_CASH', 'DEFAULT_BANK', 'DEFAULT_REVENUE', 'DEFAULT_DISCOUNT_GIVEN', 'DEFAULT_EXCHANGE_GAIN_LOSS', 'DEFAULT_ROUNDING', 'DEFAULT_COGS', 'DEFAULT_PURCHASE', 'DEFAULT_DISCOUNT_RECEIVED', 'DEFAULT_INVENTORY', 'DEFAULT_TAX_RECEIVABLE', 'DEFAULT_TAX_PAYABLE', 'DEFAULT_RETAINED_EARNINGS', 'DEFAULT_OPENING_BALANCE', 'DEFAULT_SALARY_EXPENSE', 'DEFAULT_OVERTIME_EXPENSE', 'DEFAULT_BONUS_EXPENSE', 'DEFAULT_EMPLOYER_CONTRIB_EXPENSE', 'DEFAULT_GRATUITY_EXPENSE', 'DEFAULT_LEAVE_ENCASHMENT_EXPENSE', 'DEFAULT_SALARY_PAYABLE', 'DEFAULT_EMPLOYEE_TAX_PAYABLE', 'DEFAULT_EOBI_PAYABLE', 'DEFAULT_PF_PAYABLE', 'DEFAULT_SOCIAL_SECURITY_PAYABLE', 'DEFAULT_OTHER_DEDUCTION_PAYABLE', 'DEFAULT_EXPENSE_CLAIM_PAYABLE', 'DEFAULT_GRATUITY_PROVISION', 'DEFAULT_EMPLOYEE_LOAN_RECEIVABLE', 'DEFAULT_EMPLOYEE_ADVANCE')";

        private static string Replace(string constraint, string marker, string definition) => $@"
IF OBJECT_ID(N'finance.AccountingDefaults', N'U') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'{constraint}' AND parent_object_id = OBJECT_ID(N'finance.AccountingDefaults') AND definition NOT LIKE N'%{marker}%')
        ALTER TABLE [finance].[AccountingDefaults] DROP CONSTRAINT [{constraint}];
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'{constraint}' AND parent_object_id = OBJECT_ID(N'finance.AccountingDefaults'))
        ALTER TABLE [finance].[AccountingDefaults] ADD CONSTRAINT [{constraint}] CHECK ({definition});
END";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Replace("CK_AccountingDefaults_Category", "Payroll", CategoryWithHr));
            migrationBuilder.Sql(Replace("CK_AccountingDefaults_Key", "DEFAULT_SALARY_EXPENSE", KeyWithHr));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM [finance].[AccountingDefaults] WHERE [Category] = N'Payroll';");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AccountingDefaults_Category",
                schema: "finance",
                table: "AccountingDefaults");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AccountingDefaults_Key",
                schema: "finance",
                table: "AccountingDefaults");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AccountingDefaults_Category",
                schema: "finance",
                table: "AccountingDefaults",
                sql: CategoryOld);

            migrationBuilder.AddCheckConstraint(
                name: "CK_AccountingDefaults_Key",
                schema: "finance",
                table: "AccountingDefaults",
                sql: KeyOld);
        }
    }
}
