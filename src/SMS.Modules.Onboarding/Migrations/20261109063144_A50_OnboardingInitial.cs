using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Onboarding.Migrations
{
    /// <summary>
    /// A50 (D-2, API-CONTRACT §1) - schema <c>onboarding</c>: SetupDefaults, TenantSetupLogs, OrganizationSetups, SeededRecords,
    /// OnboardingChecklists (+Steps), DocumentTemplates, EmailTemplates and DemoRecords, with their CHECK constraints and indexes.
    /// <para>
    /// <b>Guarded and idempotent</b> (every API start migrates the shared, drifted database): the schema, each table and each
    /// index is created only when missing. Tables are created parents first (FKs are declared inside CREATE TABLE). Additive only.
    /// </para>
    /// <para>
    /// <b>D-6 backfill:</b> every organization that exists in <c>tenant.Organizations</c> when this runs gets an OrganizationSetups
    /// row as Completed with note LEGACY, so no current customer is sent to the wizard. Skipped when the table is missing; rows
    /// that already exist are left alone. Dynamic SQL so the batch compiles without the tenant schema.
    /// </para>
    /// </summary>
    public partial class A50_OnboardingInitial : Migration
    {
        private static void Table(MigrationBuilder mb, string name, string body) =>
            mb.Sql($"IF OBJECT_ID(N'[onboarding].[{name}]', N'U') IS NULL\nBEGIN\n    CREATE TABLE [onboarding].[{name}] (\n{body}\n    );\nEND;");

        private static void Index(MigrationBuilder mb, string table, string name, string ddl) =>
            mb.Sql($"IF OBJECT_ID(N'[onboarding].[{table}]', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{name}' AND object_id = OBJECT_ID(N'[onboarding].[{table}]'))\n    {ddl};");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF SCHEMA_ID(N'onboarding') IS NULL EXEC(N'CREATE SCHEMA [onboarding];');");

            Table(migrationBuilder, "SetupDefaults", """
                [Id] int NOT NULL IDENTITY,
                [Uuid] uniqueidentifier NOT NULL,
                [CountryCode] nvarchar(10) NOT NULL,
                [IndustryType] nvarchar(50) NOT NULL,
                [DataCategory] nvarchar(40) NOT NULL,
                [DisplayName] nvarchar(200) NOT NULL,
                [DataPayload] nvarchar(max) NOT NULL,
                [Version] int NOT NULL,
                [IsActive] bit NOT NULL,
                [Notes] nvarchar(1000) NULL,
                [IsSystemPack] bit NOT NULL,
                [CreatedAt] datetime2 NOT NULL,
                [CreatedBy] int NULL,
                [ModifiedAt] datetime2 NOT NULL,
                [ModifiedBy] int NULL,
                [RowVersion] rowversion NOT NULL,
                CONSTRAINT [PK_SetupDefaults] PRIMARY KEY ([Id]),
                CONSTRAINT [CK_SetupDefaults_Version] CHECK ([Version] >= 1)
                """);

            Table(migrationBuilder, "TenantSetupLogs", """
                [Id] bigint NOT NULL IDENTITY,
                [OrganizationId] uniqueidentifier NOT NULL,
                [CountryCode] nvarchar(10) NOT NULL,
                [IndustryType] nvarchar(50) NOT NULL,
                [DataCategory] nvarchar(40) NOT NULL,
                [SetupDefaultId] int NULL,
                [ResolvedCountryCode] nvarchar(10) NULL,
                [ResolvedIndustryType] nvarchar(50) NULL,
                [DefaultsVersion] int NOT NULL,
                [RecordsSeeded] int NOT NULL,
                [Action] nvarchar(20) NOT NULL,
                [Message] nvarchar(2000) NULL,
                [AppliedAt] datetime2 NOT NULL,
                [AppliedBy] int NULL,
                CONSTRAINT [PK_TenantSetupLogs] PRIMARY KEY ([Id]),
                CONSTRAINT [CK_TenantSetupLogs_Action] CHECK ([Action] IN ('Seed', 'Merge', 'Overwrite', 'FullReset', 'Skipped', 'Failed'))
                """);

            Table(migrationBuilder, "OrganizationSetups", """
                [OrganizationId] uniqueidentifier NOT NULL,
                [CountryCode] nvarchar(10) NULL,
                [IndustryType] nvarchar(50) NULL,
                [SetupStatus] nvarchar(20) NOT NULL,
                [SetupStep] int NOT NULL,
                [StepDataJson] nvarchar(max) NULL,
                [DefaultsSnapshotJson] nvarchar(max) NULL,
                [CompletedAt] datetime2 NULL,
                [CompletedBy] int NULL,
                [Note] nvarchar(500) NULL,
                [CreatedAt] datetime2 NOT NULL,
                [ModifiedAt] datetime2 NOT NULL,
                [RowVersion] rowversion NOT NULL,
                CONSTRAINT [PK_OrganizationSetups] PRIMARY KEY ([OrganizationId]),
                CONSTRAINT [CK_OrganizationSetups_Status] CHECK ([SetupStatus] IN ('NotStarted', 'InProgress', 'Completed', 'Skipped'))
                """);

            Table(migrationBuilder, "SeededRecords", """
                [Id] bigint NOT NULL IDENTITY,
                [OrganizationId] uniqueidentifier NOT NULL,
                [Category] nvarchar(40) NOT NULL,
                [NaturalKey] nvarchar(200) NOT NULL,
                [EntityRef] nvarchar(100) NOT NULL,
                [Version] int NOT NULL,
                [Fingerprint] nvarchar(100) NOT NULL,
                [Reused] bit NOT NULL,
                [ResetCount] int NOT NULL,
                [SeededAt] datetime2 NOT NULL,
                CONSTRAINT [PK_SeededRecords] PRIMARY KEY ([Id])
                """);

            Table(migrationBuilder, "OnboardingChecklists", """
                [OrganizationId] uniqueidentifier NOT NULL,
                [Dismissed] bit NOT NULL,
                [DismissedAt] datetime2 NULL,
                [DismissedBy] int NULL,
                [CreatedAt] datetime2 NOT NULL,
                CONSTRAINT [PK_OnboardingChecklists] PRIMARY KEY ([OrganizationId])
                """);

            Table(migrationBuilder, "OnboardingChecklistSteps", """
                [Id] int NOT NULL IDENTITY,
                [OrganizationId] uniqueidentifier NOT NULL,
                [StepCode] nvarchar(40) NOT NULL,
                [Title] nvarchar(200) NOT NULL,
                [Link] nvarchar(300) NULL,
                [RequiresModule] nvarchar(60) NULL,
                [SortOrder] int NOT NULL,
                [CompletedAt] datetime2 NULL,
                [CompletedBy] int NULL,
                [AutoCompleted] bit NOT NULL,
                CONSTRAINT [PK_OnboardingChecklistSteps] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_OnboardingChecklistSteps_OnboardingChecklists_OrganizationId] FOREIGN KEY ([OrganizationId])
                    REFERENCES [onboarding].[OnboardingChecklists] ([OrganizationId]) ON DELETE CASCADE
                """);

            Table(migrationBuilder, "DocumentTemplates", """
                [Id] int NOT NULL IDENTITY,
                [Uuid] uniqueidentifier NOT NULL,
                [OrganizationId] uniqueidentifier NOT NULL,
                [TemplateType] nvarchar(30) NOT NULL,
                [Name] nvarchar(200) NOT NULL,
                [HeaderHtml] nvarchar(max) NULL,
                [BodyHtml] nvarchar(max) NULL,
                [FooterHtml] nvarchar(max) NULL,
                [LogoAttachmentUuid] uniqueidentifier NULL,
                [PrimaryColor] nvarchar(7) NULL,
                [ShowBankDetails] bit NOT NULL,
                [BankDetailsText] nvarchar(2000) NULL,
                [TermsAndConditions] nvarchar(4000) NULL,
                [IsDefault] bit NOT NULL,
                [IsSeeded] bit NOT NULL,
                [IsActive] bit NOT NULL,
                [CreatedAt] datetime2 NOT NULL,
                [CreatedBy] int NULL,
                [ModifiedAt] datetime2 NOT NULL,
                [ModifiedBy] int NULL,
                CONSTRAINT [PK_DocumentTemplates] PRIMARY KEY ([Id]),
                CONSTRAINT [CK_DocumentTemplates_Type] CHECK ([TemplateType] IN ('SalesInvoice', 'Quotation', 'PurchaseOrder', 'DeliveryNote', 'PaymentReceipt', 'CustomerStatement'))
                """);

            Table(migrationBuilder, "EmailTemplates", """
                [Id] int NOT NULL IDENTITY,
                [Uuid] uniqueidentifier NOT NULL,
                [OrganizationId] uniqueidentifier NOT NULL,
                [TemplateType] nvarchar(30) NOT NULL,
                [Name] nvarchar(200) NOT NULL,
                [SubjectTemplate] nvarchar(500) NOT NULL,
                [BodyHtml] nvarchar(max) NOT NULL,
                [IsDefault] bit NOT NULL,
                [IsSeeded] bit NOT NULL,
                [IsActive] bit NOT NULL,
                [CreatedAt] datetime2 NOT NULL,
                [CreatedBy] int NULL,
                [ModifiedAt] datetime2 NOT NULL,
                [ModifiedBy] int NULL,
                CONSTRAINT [PK_EmailTemplates] PRIMARY KEY ([Id]),
                CONSTRAINT [CK_EmailTemplates_Type] CHECK ([TemplateType] IN ('InvoiceSent', 'QuotationSent', 'POToSupplier', 'PaymentReminder', 'PaymentReceived', 'WelcomeEmail', 'PayslipSent'))
                """);

            Table(migrationBuilder, "DemoRecords", """
                [Id] bigint NOT NULL IDENTITY,
                [OrganizationId] uniqueidentifier NOT NULL,
                [EntityType] nvarchar(50) NOT NULL,
                [EntityRef] nvarchar(100) NOT NULL,
                [Contributor] nvarchar(100) NULL,
                [CreatedAt] datetime2 NOT NULL,
                CONSTRAINT [PK_DemoRecords] PRIMARY KEY ([Id])
                """);

            Index(migrationBuilder, "SetupDefaults", "IX_SetupDefaults_Uuid",
                "CREATE UNIQUE INDEX [IX_SetupDefaults_Uuid] ON [onboarding].[SetupDefaults] ([Uuid])");
            Index(migrationBuilder, "SetupDefaults", "UQ_SD_Key",
                "CREATE UNIQUE INDEX [UQ_SD_Key] ON [onboarding].[SetupDefaults] ([CountryCode], [IndustryType], [DataCategory])");
            Index(migrationBuilder, "TenantSetupLogs", "IX_TSL_Org",
                "CREATE INDEX [IX_TSL_Org] ON [onboarding].[TenantSetupLogs] ([OrganizationId], [DataCategory], [AppliedAt])");
            Index(migrationBuilder, "TenantSetupLogs", "IX_TSL_SetupDefault",
                "CREATE INDEX [IX_TSL_SetupDefault] ON [onboarding].[TenantSetupLogs] ([SetupDefaultId])");
            Index(migrationBuilder, "SeededRecords", "UQ_SeededRecords_Key",
                "CREATE UNIQUE INDEX [UQ_SeededRecords_Key] ON [onboarding].[SeededRecords] ([OrganizationId], [Category], [NaturalKey])");
            Index(migrationBuilder, "OnboardingChecklistSteps", "UQ_OnboardingChecklistSteps_Code",
                "CREATE UNIQUE INDEX [UQ_OnboardingChecklistSteps_Code] ON [onboarding].[OnboardingChecklistSteps] ([OrganizationId], [StepCode])");
            Index(migrationBuilder, "DocumentTemplates", "IX_DocumentTemplates_Uuid",
                "CREATE UNIQUE INDEX [IX_DocumentTemplates_Uuid] ON [onboarding].[DocumentTemplates] ([Uuid])");
            Index(migrationBuilder, "DocumentTemplates", "IX_DocumentTemplates_Type",
                "CREATE INDEX [IX_DocumentTemplates_Type] ON [onboarding].[DocumentTemplates] ([OrganizationId], [TemplateType])");
            Index(migrationBuilder, "DocumentTemplates", "UQ_DocumentTemplates_Default",
                "CREATE UNIQUE INDEX [UQ_DocumentTemplates_Default] ON [onboarding].[DocumentTemplates] ([OrganizationId], [TemplateType]) WHERE [IsDefault] = 1");
            Index(migrationBuilder, "EmailTemplates", "IX_EmailTemplates_Uuid",
                "CREATE UNIQUE INDEX [IX_EmailTemplates_Uuid] ON [onboarding].[EmailTemplates] ([Uuid])");
            Index(migrationBuilder, "EmailTemplates", "IX_EmailTemplates_Type",
                "CREATE INDEX [IX_EmailTemplates_Type] ON [onboarding].[EmailTemplates] ([OrganizationId], [TemplateType])");
            Index(migrationBuilder, "EmailTemplates", "UQ_EmailTemplates_Default",
                "CREATE UNIQUE INDEX [UQ_EmailTemplates_Default] ON [onboarding].[EmailTemplates] ([OrganizationId], [TemplateType]) WHERE [IsDefault] = 1");
            Index(migrationBuilder, "DemoRecords", "UQ_DemoRecords_Ref",
                "CREATE UNIQUE INDEX [UQ_DemoRecords_Ref] ON [onboarding].[DemoRecords] ([OrganizationId], [EntityType], [EntityRef])");

            // D-6 — existing organizations are already set up: never redirect them to the wizard.
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'[tenant].[Organizations]', N'U') IS NOT NULL AND OBJECT_ID(N'[onboarding].[OrganizationSetups]', N'U') IS NOT NULL
                    EXEC(N'INSERT INTO [onboarding].[OrganizationSetups]
                              ([OrganizationId], [CountryCode], [IndustryType], [SetupStatus], [SetupStep], [CompletedAt], [Note], [CreatedAt], [ModifiedAt])
                          SELECT o.[Id], NULL, NULL, N''Completed'', 0, SYSUTCDATETIME(), N''LEGACY'', SYSUTCDATETIME(), SYSUTCDATETIME()
                          FROM [tenant].[Organizations] o
                          WHERE NOT EXISTS (SELECT 1 FROM [onboarding].[OrganizationSetups] s WHERE s.[OrganizationId] = o.[Id]);');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "DemoRecords", schema: "onboarding");
            migrationBuilder.DropTable(name: "DocumentTemplates", schema: "onboarding");
            migrationBuilder.DropTable(name: "EmailTemplates", schema: "onboarding");
            migrationBuilder.DropTable(name: "OnboardingChecklistSteps", schema: "onboarding");
            migrationBuilder.DropTable(name: "OrganizationSetups", schema: "onboarding");
            migrationBuilder.DropTable(name: "SeededRecords", schema: "onboarding");
            migrationBuilder.DropTable(name: "SetupDefaults", schema: "onboarding");
            migrationBuilder.DropTable(name: "TenantSetupLogs", schema: "onboarding");
            migrationBuilder.DropTable(name: "OnboardingChecklists", schema: "onboarding");
        }
    }
}
