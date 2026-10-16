using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Integration.Migrations
{
    /// <summary>
    /// docs/quickbooks/APP-CREDENTIALS.md — Intuit app keys in the database (platform row + one per organization) and the
    /// app each connection / state token is pinned to. Guarded: the API migrates the shared database on start, and a
    /// re-run or a drifted database must not fail.
    /// </summary>
    public partial class QBI_AppCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF COL_LENGTH(N'integration.OAuthStateTokens', N'AppClientId') IS NULL
                    ALTER TABLE [integration].[OAuthStateTokens] ADD [AppClientId] nvarchar(200) NULL;
                IF COL_LENGTH(N'integration.Connections', N'AppClientId') IS NULL
                    ALTER TABLE [integration].[Connections] ADD [AppClientId] nvarchar(200) NULL;
                IF COL_LENGTH(N'integration.Connections', N'AppSource') IS NULL
                    ALTER TABLE [integration].[Connections] ADD [AppSource] nvarchar(20) NULL;
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'integration.QuickBooksAppCredentials', N'U') IS NULL
                BEGIN
                    CREATE TABLE [integration].[QuickBooksAppCredentials] (
                        [Id] int NOT NULL IDENTITY(1, 1),
                        [Uuid] uniqueidentifier NOT NULL,
                        [OrganizationId] uniqueidentifier NULL,
                        [ClientId] nvarchar(200) NOT NULL,
                        [EncryptedClientSecret] nvarchar(2000) NOT NULL,
                        [Environment] nvarchar(20) NOT NULL,
                        [RedirectUri] nvarchar(500) NULL,
                        [FrontendReturnUrl] nvarchar(500) NULL,
                        [CreatedAt] datetime2 NOT NULL,
                        [CreatedBy] int NULL,
                        [ModifiedAt] datetime2 NULL,
                        [ModifiedBy] int NULL,
                        [RowVersion] rowversion NOT NULL,
                        CONSTRAINT [PK_QuickBooksAppCredentials] PRIMARY KEY ([Id])
                    );
                END;
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QuickBooksAppCredentials_OrganizationId'
                               AND object_id = OBJECT_ID(N'integration.QuickBooksAppCredentials'))
                    -- D-1: one NULL (the platform row) and one row per organization.
                    CREATE UNIQUE INDEX [IX_QuickBooksAppCredentials_OrganizationId]
                        ON [integration].[QuickBooksAppCredentials] ([OrganizationId]);
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QuickBooksAppCredentials_Uuid'
                               AND object_id = OBJECT_ID(N'integration.QuickBooksAppCredentials'))
                    CREATE UNIQUE INDEX [IX_QuickBooksAppCredentials_Uuid]
                        ON [integration].[QuickBooksAppCredentials] ([Uuid]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'integration.QuickBooksAppCredentials', N'U') IS NOT NULL
                    DROP TABLE [integration].[QuickBooksAppCredentials];
                IF COL_LENGTH(N'integration.OAuthStateTokens', N'AppClientId') IS NOT NULL
                    ALTER TABLE [integration].[OAuthStateTokens] DROP COLUMN [AppClientId];
                IF COL_LENGTH(N'integration.Connections', N'AppClientId') IS NOT NULL
                    ALTER TABLE [integration].[Connections] DROP COLUMN [AppClientId];
                IF COL_LENGTH(N'integration.Connections', N'AppSource') IS NOT NULL
                    ALTER TABLE [integration].[Connections] DROP COLUMN [AppSource];
                """);
        }
    }
}
