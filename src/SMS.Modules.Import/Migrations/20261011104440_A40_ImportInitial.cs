using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Import.Migrations
{
    /// <summary>
    /// A40 — schema <c>import</c>: profiles (+ fields) mirrored from the registered targets, per-org overrides, sessions,
    /// column mappings, rows (JSON) and external references. Guarded and additive: every table and index is created only
    /// when missing, so a partially applied or hand-created schema on a shared database does not stop the API start.
    /// </summary>
    public partial class A40_ImportInitial : Migration
    {
        private static void Table(MigrationBuilder mb, string name, string body) =>
            mb.Sql($"IF OBJECT_ID(N'[import].[{name}]', N'U') IS NULL\nBEGIN\n    CREATE TABLE [import].[{name}] (\n{body}\n    );\nEND;");

        private static void Index(MigrationBuilder mb, string table, string name, string ddl) =>
            mb.Sql($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{name}' AND object_id = OBJECT_ID(N'[import].[{table}]'))\n    {ddl};");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF SCHEMA_ID(N'import') IS NULL EXEC(N'CREATE SCHEMA [import];');");

            Table(migrationBuilder, "external_references", """
                [Id] int NOT NULL IDENTITY,
                [OrganizationId] uniqueidentifier NOT NULL,
                [EntityType] nvarchar(40) NOT NULL,
                [ExternalId] nvarchar(100) NOT NULL,
                [EntityUuid] uniqueidentifier NOT NULL,
                [LastSessionUuid] uniqueidentifier NULL,
                [CreatedAt] datetime2 NOT NULL,
                [ModifiedAt] datetime2 NOT NULL,
                CONSTRAINT [PK_external_references] PRIMARY KEY ([Id])
                """);

            Table(migrationBuilder, "profile_overrides", """
                [Id] int NOT NULL IDENTITY,
                [OrganizationId] uniqueidentifier NOT NULL,
                [EntityType] nvarchar(40) NOT NULL,
                [DuplicateStrategy] nvarchar(20) NOT NULL,
                [MaxRowsPerFile] int NOT NULL,
                [MaxFileSizeMb] int NOT NULL,
                [ModifiedBy] int NOT NULL,
                [ModifiedAt] datetime2 NOT NULL,
                CONSTRAINT [PK_profile_overrides] PRIMARY KEY ([Id])
                """);

            Table(migrationBuilder, "profiles", """
                [Id] int NOT NULL IDENTITY,
                [EntityType] nvarchar(40) NOT NULL,
                [Name] nvarchar(100) NOT NULL,
                [Description] nvarchar(500) NULL,
                [ImportOrder] int NOT NULL,
                [IsOpeningBalance] bit NOT NULL,
                [AllowUpdate] bit NOT NULL,
                [MatchFieldsJson] nvarchar(1000) NOT NULL,
                [IsActive] bit NOT NULL,
                [SyncedAt] datetime2 NOT NULL,
                CONSTRAINT [PK_profiles] PRIMARY KEY ([Id])
                """);

            Table(migrationBuilder, "sessions", """
                [Id] int NOT NULL IDENTITY,
                [Uuid] uniqueidentifier NOT NULL,
                [OrganizationId] uniqueidentifier NOT NULL,
                [EntityType] nvarchar(40) NOT NULL,
                [FileName] nvarchar(260) NOT NULL,
                [FileType] nvarchar(10) NOT NULL,
                [FileSizeBytes] bigint NOT NULL,
                [SheetName] nvarchar(100) NULL,
                [SheetNamesJson] nvarchar(max) NOT NULL,
                [HeadersJson] nvarchar(max) NOT NULL,
                [Status] nvarchar(30) NOT NULL,
                [DuplicateStrategy] nvarchar(20) NOT NULL,
                [Notes] nvarchar(1000) NULL,
                [SavedMappingName] nvarchar(100) NULL,
                [SavedMappingAt] datetime2 NULL,
                [TotalRows] int NOT NULL,
                [ValidRows] int NOT NULL,
                [InvalidRows] int NOT NULL,
                [ImportedRows] int NOT NULL,
                [UpdatedRows] int NOT NULL,
                [SkippedRows] int NOT NULL,
                [ErrorRows] int NOT NULL,
                [ProcessedRows] int NOT NULL,
                [RowsToProcess] int NOT NULL,
                [ValidationCompletedAt] datetime2 NULL,
                [ImportStartedAt] datetime2 NULL,
                [ImportCompletedAt] datetime2 NULL,
                [ErrorMessage] nvarchar(2000) NULL,
                [JobId] nvarchar(100) NULL,
                [ExecutedBy] int NULL,
                [RowsPurgedAt] datetime2 NULL,
                [CreatedBy] int NOT NULL,
                [CreatedByName] nvarchar(200) NULL,
                [CreatedAt] datetime2 NOT NULL,
                [ModifiedAt] datetime2 NOT NULL,
                CONSTRAINT [PK_sessions] PRIMARY KEY ([Id])
                """);

            Table(migrationBuilder, "profile_fields", """
                [Id] int NOT NULL IDENTITY,
                [ProfileId] int NOT NULL,
                [FieldName] nvarchar(100) NOT NULL,
                [DisplayName] nvarchar(150) NOT NULL,
                [Description] nvarchar(500) NULL,
                [FieldType] nvarchar(20) NOT NULL,
                [IsRequired] bit NOT NULL,
                [IsKey] bit NOT NULL,
                [SortOrder] int NOT NULL,
                [SampleValue] nvarchar(200) NULL,
                [MaxLength] int NULL,
                [MinValue] decimal(19,6) NULL,
                [MaxValue] decimal(19,6) NULL,
                [DecimalPlaces] int NULL,
                [ValidationRegex] nvarchar(500) NULL,
                [DefaultValue] nvarchar(200) NULL,
                [LookupList] nvarchar(100) NULL,
                [AliasesJson] nvarchar(2000) NULL,
                CONSTRAINT [PK_profile_fields] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_profile_fields_profiles_ProfileId] FOREIGN KEY ([ProfileId]) REFERENCES [import].[profiles] ([Id]) ON DELETE CASCADE
                """);

            Table(migrationBuilder, "column_mappings", """
                [Id] int NOT NULL IDENTITY,
                [SessionId] int NOT NULL,
                [OrganizationId] uniqueidentifier NOT NULL,
                [SourceColumnIndex] int NOT NULL,
                [SourceColumnName] nvarchar(300) NOT NULL,
                [TargetFieldName] nvarchar(100) NULL,
                [IsAutoMapped] bit NOT NULL,
                [ConfidenceScore] decimal(3,2) NULL,
                CONSTRAINT [PK_column_mappings] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_column_mappings_sessions_SessionId] FOREIGN KEY ([SessionId]) REFERENCES [import].[sessions] ([Id]) ON DELETE CASCADE
                """);

            Table(migrationBuilder, "rows", """
                [Id] bigint NOT NULL IDENTITY,
                [SessionId] int NOT NULL,
                [OrganizationId] uniqueidentifier NOT NULL,
                [RowNumber] int NOT NULL,
                [RawJson] nvarchar(max) NOT NULL,
                [ValuesJson] nvarchar(max) NULL,
                [ErrorsJson] nvarchar(max) NULL,
                [Status] nvarchar(20) NOT NULL,
                [ExternalId] nvarchar(100) NULL,
                [ExistingEntityUuid] uniqueidentifier NULL,
                [EntityUuid] uniqueidentifier NULL,
                CONSTRAINT [PK_rows] PRIMARY KEY ([Id]),
                CONSTRAINT [FK_rows_sessions_SessionId] FOREIGN KEY ([SessionId]) REFERENCES [import].[sessions] ([Id]) ON DELETE CASCADE
                """);

            Index(migrationBuilder, "column_mappings", "IX_column_mappings_OrganizationId",
                "CREATE INDEX [IX_column_mappings_OrganizationId] ON [import].[column_mappings] ([OrganizationId])");
            Index(migrationBuilder, "column_mappings", "IX_column_mappings_SessionId_SourceColumnIndex",
                "CREATE UNIQUE INDEX [IX_column_mappings_SessionId_SourceColumnIndex] ON [import].[column_mappings] ([SessionId], [SourceColumnIndex])");
            Index(migrationBuilder, "external_references", "IX_external_references_OrganizationId_EntityType_EntityUuid",
                "CREATE UNIQUE INDEX [IX_external_references_OrganizationId_EntityType_EntityUuid] ON [import].[external_references] ([OrganizationId], [EntityType], [EntityUuid])");
            Index(migrationBuilder, "external_references", "IX_external_references_OrganizationId_EntityType_ExternalId",
                "CREATE UNIQUE INDEX [IX_external_references_OrganizationId_EntityType_ExternalId] ON [import].[external_references] ([OrganizationId], [EntityType], [ExternalId])");
            Index(migrationBuilder, "profile_fields", "IX_profile_fields_ProfileId_FieldName",
                "CREATE UNIQUE INDEX [IX_profile_fields_ProfileId_FieldName] ON [import].[profile_fields] ([ProfileId], [FieldName])");
            Index(migrationBuilder, "profile_overrides", "IX_profile_overrides_OrganizationId_EntityType",
                "CREATE UNIQUE INDEX [IX_profile_overrides_OrganizationId_EntityType] ON [import].[profile_overrides] ([OrganizationId], [EntityType])");
            Index(migrationBuilder, "profiles", "IX_profiles_EntityType",
                "CREATE UNIQUE INDEX [IX_profiles_EntityType] ON [import].[profiles] ([EntityType])");
            Index(migrationBuilder, "rows", "IX_rows_OrganizationId",
                "CREATE INDEX [IX_rows_OrganizationId] ON [import].[rows] ([OrganizationId])");
            Index(migrationBuilder, "rows", "IX_rows_SessionId_RowNumber",
                "CREATE UNIQUE INDEX [IX_rows_SessionId_RowNumber] ON [import].[rows] ([SessionId], [RowNumber])");
            Index(migrationBuilder, "rows", "IX_rows_SessionId_Status",
                "CREATE INDEX [IX_rows_SessionId_Status] ON [import].[rows] ([SessionId], [Status])");
            Index(migrationBuilder, "sessions", "IX_sessions_OrganizationId_CreatedAt",
                "CREATE INDEX [IX_sessions_OrganizationId_CreatedAt] ON [import].[sessions] ([OrganizationId], [CreatedAt])");
            Index(migrationBuilder, "sessions", "IX_sessions_OrganizationId_EntityType_Status",
                "CREATE INDEX [IX_sessions_OrganizationId_EntityType_Status] ON [import].[sessions] ([OrganizationId], [EntityType], [Status])");
            Index(migrationBuilder, "sessions", "IX_sessions_Uuid",
                "CREATE UNIQUE INDEX [IX_sessions_Uuid] ON [import].[sessions] ([Uuid])");
            Index(migrationBuilder, "sessions", "UX_sessions_one_importing",
                "CREATE UNIQUE INDEX [UX_sessions_one_importing] ON [import].[sessions] ([OrganizationId], [EntityType]) WHERE [Status] = 'IMPORTING'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "column_mappings", schema: "import");
            migrationBuilder.DropTable(name: "external_references", schema: "import");
            migrationBuilder.DropTable(name: "profile_fields", schema: "import");
            migrationBuilder.DropTable(name: "profile_overrides", schema: "import");
            migrationBuilder.DropTable(name: "rows", schema: "import");
            migrationBuilder.DropTable(name: "profiles", schema: "import");
            migrationBuilder.DropTable(name: "sessions", schema: "import");
        }
    }
}
