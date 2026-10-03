using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Integration.Migrations
{
    /// <inheritdoc />
    public partial class QBI_InitialIntegrationSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "integration");

            migrationBuilder.CreateTable(
                name: "ApiClients",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiClients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CommandClaims",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityMapId = table.Column<int>(type: "int", nullable: false),
                    CommandKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    FirstAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LeaseExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RemoteId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ErrorCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommandClaims", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Connections",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RealmId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Environment = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EncryptedAccessToken = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    EncryptedRefreshToken = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    AccessTokenExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RefreshTokenExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    HomeCurrencyCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    MultiCurrencyEnabled = table.Column<bool>(type: "bit", nullable: true),
                    Country = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    ConnectedByUserId = table.Column<int>(type: "int", nullable: true),
                    ConnectedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastRefreshAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReconnectWarnedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Connections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EntityMaps",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionId = table.Column<int>(type: "int", nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DisplayLabel = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PayloadFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LastPushedFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PayloadReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RemoteId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RemoteSyncToken = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    RemoteDocNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RemoteName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LinkOrigin = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    State = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastErrorCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Warning = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LinkedByUserId = table.Column<int>(type: "int", nullable: true),
                    LinkedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntityMaps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MatchCandidates",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionId = table.Column<int>(type: "int", nullable: false),
                    EntityMapId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RemoteId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RemoteName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Confidence = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Decision = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DecidedBy = table.Column<int>(type: "int", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchCandidates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OAuthStateTokens",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OAuthStateTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentTermMappings",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionId = table.Column<int>(type: "int", nullable: false),
                    PaymentTermExternalId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PaymentTermName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    QboTermId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentTermMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReferenceSnapshots",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferenceSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionId = table.Column<int>(type: "int", nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AutoPushCustomers = table.Column<bool>(type: "bit", nullable: false),
                    AutoPushVendors = table.Column<bool>(type: "bit", nullable: false),
                    AutoPushItems = table.Column<bool>(type: "bit", nullable: false),
                    AutoPushSalesInvoices = table.Column<bool>(type: "bit", nullable: false),
                    AutoPushBills = table.Column<bool>(type: "bit", nullable: false),
                    ItemTypeDefault = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PartnerScope = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DefaultIncomeAccountId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DefaultExpenseAccountId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    FreightExpenseAccountId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DiscountAccountId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DefaultPurchaseTaxCodeId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DocumentStartDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MatchingConfirmedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastReconciledAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SettingsAudit",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionId = table.Column<int>(type: "int", nullable: false),
                    Area = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    BeforeJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AfterJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SettingsAudit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncLog",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionId = table.Column<int>(type: "int", nullable: false),
                    EntityMapId = table.Column<int>(type: "int", nullable: true),
                    Operation = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ErrorCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RequestJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResponseJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    IntuitTid = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaxCodeMappings",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionId = table.Column<int>(type: "int", nullable: false),
                    TaxPercent = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    QboTaxCodeId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxCodeMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiClientKeys",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApiClientId = table.Column<int>(type: "int", nullable: false),
                    KeyPrefix = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    KeyHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiClientKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiClientKeys_ApiClients_ApiClientId",
                        column: x => x.ApiClientId,
                        principalSchema: "integration",
                        principalTable: "ApiClients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Outbox",
                schema: "integration",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConnectionId = table.Column<int>(type: "int", nullable: false),
                    EntityMapId = table.Column<int>(type: "int", nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    BlockedReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DependsOnJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    WaitingSince = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Outbox", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Outbox_EntityMaps_EntityMapId",
                        column: x => x.EntityMapId,
                        principalSchema: "integration",
                        principalTable: "EntityMaps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiClientKeys_ApiClientId",
                schema: "integration",
                table: "ApiClientKeys",
                column: "ApiClientId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiClientKeys_KeyPrefix",
                schema: "integration",
                table: "ApiClientKeys",
                column: "KeyPrefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiClientKeys_OrganizationId",
                schema: "integration",
                table: "ApiClientKeys",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiClientKeys_Uuid",
                schema: "integration",
                table: "ApiClientKeys",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiClients_OrganizationId_Name",
                schema: "integration",
                table: "ApiClients",
                columns: new[] { "OrganizationId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiClients_Uuid",
                schema: "integration",
                table: "ApiClients",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommandClaims_OrganizationId_CommandKey",
                schema: "integration",
                table: "CommandClaims",
                columns: new[] { "OrganizationId", "CommandKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommandClaims_OrganizationId_EntityMapId",
                schema: "integration",
                table: "CommandClaims",
                columns: new[] { "OrganizationId", "EntityMapId" });

            migrationBuilder.CreateIndex(
                name: "IX_CommandClaims_Status_LeaseExpiresAt",
                schema: "integration",
                table: "CommandClaims",
                columns: new[] { "Status", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Connections_OrganizationId_ProviderKey",
                schema: "integration",
                table: "Connections",
                columns: new[] { "OrganizationId", "ProviderKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Connections_Uuid",
                schema: "integration",
                table: "Connections",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EntityMaps_ConnectionId_Kind_RemoteId",
                schema: "integration",
                table: "EntityMaps",
                columns: new[] { "ConnectionId", "Kind", "RemoteId" });

            migrationBuilder.CreateIndex(
                name: "IX_EntityMaps_ConnectionId_Kind_State",
                schema: "integration",
                table: "EntityMaps",
                columns: new[] { "ConnectionId", "Kind", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_EntityMaps_ConnectionId_SourceSystem_Kind_ExternalId",
                schema: "integration",
                table: "EntityMaps",
                columns: new[] { "ConnectionId", "SourceSystem", "Kind", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EntityMaps_OrganizationId",
                schema: "integration",
                table: "EntityMaps",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_EntityMaps_Uuid",
                schema: "integration",
                table: "EntityMaps",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MatchCandidates_ConnectionId_Kind_Decision",
                schema: "integration",
                table: "MatchCandidates",
                columns: new[] { "ConnectionId", "Kind", "Decision" });

            migrationBuilder.CreateIndex(
                name: "IX_MatchCandidates_EntityMapId",
                schema: "integration",
                table: "MatchCandidates",
                column: "EntityMapId");

            migrationBuilder.CreateIndex(
                name: "IX_MatchCandidates_OrganizationId",
                schema: "integration",
                table: "MatchCandidates",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_MatchCandidates_Uuid",
                schema: "integration",
                table: "MatchCandidates",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OAuthStateTokens_OrganizationId",
                schema: "integration",
                table: "OAuthStateTokens",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_OAuthStateTokens_TokenHash",
                schema: "integration",
                table: "OAuthStateTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_ConnectionId_Status_NextAttemptAt",
                schema: "integration",
                table: "Outbox",
                columns: new[] { "ConnectionId", "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_EntityMapId",
                schema: "integration",
                table: "Outbox",
                column: "EntityMapId");

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_OrganizationId",
                schema: "integration",
                table: "Outbox",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_Uuid",
                schema: "integration",
                table: "Outbox",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTermMappings_ConnectionId_PaymentTermExternalId",
                schema: "integration",
                table: "PaymentTermMappings",
                columns: new[] { "ConnectionId", "PaymentTermExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTermMappings_OrganizationId",
                schema: "integration",
                table: "PaymentTermMappings",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_ReferenceSnapshots_ConnectionId_Kind",
                schema: "integration",
                table: "ReferenceSnapshots",
                columns: new[] { "ConnectionId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReferenceSnapshots_OrganizationId",
                schema: "integration",
                table: "ReferenceSnapshots",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Settings_ConnectionId",
                schema: "integration",
                table: "Settings",
                column: "ConnectionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Settings_OrganizationId",
                schema: "integration",
                table: "Settings",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_SettingsAudit_ConnectionId_CreatedAt",
                schema: "integration",
                table: "SettingsAudit",
                columns: new[] { "ConnectionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SettingsAudit_OrganizationId",
                schema: "integration",
                table: "SettingsAudit",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncLog_ConnectionId_CreatedAt",
                schema: "integration",
                table: "SyncLog",
                columns: new[] { "ConnectionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncLog_CreatedAt",
                schema: "integration",
                table: "SyncLog",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SyncLog_EntityMapId_CreatedAt",
                schema: "integration",
                table: "SyncLog",
                columns: new[] { "EntityMapId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncLog_OrganizationId",
                schema: "integration",
                table: "SyncLog",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncLog_Uuid",
                schema: "integration",
                table: "SyncLog",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxCodeMappings_ConnectionId_TaxPercent",
                schema: "integration",
                table: "TaxCodeMappings",
                columns: new[] { "ConnectionId", "TaxPercent" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxCodeMappings_OrganizationId",
                schema: "integration",
                table: "TaxCodeMappings",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiClientKeys",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "CommandClaims",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "Connections",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "MatchCandidates",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "OAuthStateTokens",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "Outbox",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "PaymentTermMappings",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "ReferenceSnapshots",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "Settings",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "SettingsAudit",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "SyncLog",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "TaxCodeMappings",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "ApiClients",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "EntityMaps",
                schema: "integration");
        }
    }
}
