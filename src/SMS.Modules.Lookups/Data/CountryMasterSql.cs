namespace SMS.Modules.Lookups.Data;

/// <summary>A51 P1 — the country-master columns on lookups.Countries, guarded (run by the seeder at every start and by the migration).
/// Each column is added only when missing; the table itself is skipped when absent (EnsureCreated builds it with the columns).</summary>
internal static class CountryMasterSql
{
    public const string Up = """
        IF OBJECT_ID(N'[lookups].[Countries]', N'U') IS NOT NULL
        BEGIN
            IF COL_LENGTH('lookups.Countries', 'CurrencyCode') IS NULL
                ALTER TABLE [lookups].[Countries] ADD [CurrencyCode] nvarchar(5) NULL;
            IF COL_LENGTH('lookups.Countries', 'DefaultTimezone') IS NULL
                ALTER TABLE [lookups].[Countries] ADD [DefaultTimezone] nvarchar(50) NULL;
            IF COL_LENGTH('lookups.Countries', 'FiscalYearStartMonth') IS NULL
                ALTER TABLE [lookups].[Countries] ADD [FiscalYearStartMonth] int NOT NULL
                    CONSTRAINT [DF_Countries_FiscalYearStartMonth] DEFAULT 1;
            IF COL_LENGTH('lookups.Countries', 'DateFormat') IS NULL
                ALTER TABLE [lookups].[Countries] ADD [DateFormat] nvarchar(20) NOT NULL
                    CONSTRAINT [DF_Countries_DateFormat] DEFAULT N'DD/MM/YYYY';
            IF COL_LENGTH('lookups.Countries', 'PhoneCode') IS NULL
                ALTER TABLE [lookups].[Countries] ADD [PhoneCode] nvarchar(5) NULL;
            IF COL_LENGTH('lookups.Countries', 'SortOrder') IS NULL
                ALTER TABLE [lookups].[Countries] ADD [SortOrder] int NOT NULL
                    CONSTRAINT [DF_Countries_SortOrder] DEFAULT 0;
        END;
        """;

    public const string Down = """
        IF OBJECT_ID('lookups.DF_Countries_FiscalYearStartMonth', 'D') IS NOT NULL
            ALTER TABLE [lookups].[Countries] DROP CONSTRAINT [DF_Countries_FiscalYearStartMonth];
        IF OBJECT_ID('lookups.DF_Countries_DateFormat', 'D') IS NOT NULL
            ALTER TABLE [lookups].[Countries] DROP CONSTRAINT [DF_Countries_DateFormat];
        IF OBJECT_ID('lookups.DF_Countries_SortOrder', 'D') IS NOT NULL
            ALTER TABLE [lookups].[Countries] DROP CONSTRAINT [DF_Countries_SortOrder];
        IF COL_LENGTH('lookups.Countries', 'CurrencyCode') IS NOT NULL ALTER TABLE [lookups].[Countries] DROP COLUMN [CurrencyCode];
        IF COL_LENGTH('lookups.Countries', 'DefaultTimezone') IS NOT NULL ALTER TABLE [lookups].[Countries] DROP COLUMN [DefaultTimezone];
        IF COL_LENGTH('lookups.Countries', 'FiscalYearStartMonth') IS NOT NULL ALTER TABLE [lookups].[Countries] DROP COLUMN [FiscalYearStartMonth];
        IF COL_LENGTH('lookups.Countries', 'DateFormat') IS NOT NULL ALTER TABLE [lookups].[Countries] DROP COLUMN [DateFormat];
        IF COL_LENGTH('lookups.Countries', 'PhoneCode') IS NOT NULL ALTER TABLE [lookups].[Countries] DROP COLUMN [PhoneCode];
        IF COL_LENGTH('lookups.Countries', 'SortOrder') IS NOT NULL ALTER TABLE [lookups].[Countries] DROP COLUMN [SortOrder];
        """;
}
