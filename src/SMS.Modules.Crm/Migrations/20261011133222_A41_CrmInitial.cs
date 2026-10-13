using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Crm.Migrations
{
    /// <inheritdoc />
    public partial class A41_CrmInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "crm");

            migrationBuilder.CreateTable(
                name: "ActivityFiles",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    UploadedBy = table.Column<int>(type: "int", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityFiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LeadApiKeys",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    KeyPrefix = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    KeyHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RequestsPerMinute = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadApiKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LeadSources",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadSources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecordHistory",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EntityId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FromValue = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ToValue = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Campaigns",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CampaignType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    LeadSourceId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StartDate = table.Column<DateTime>(type: "date", nullable: true),
                    EndDate = table.Column<DateTime>(type: "date", nullable: true),
                    BudgetAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    ExpectedLeads = table.Column<int>(type: "int", nullable: true),
                    ActualLeads = table.Column<int>(type: "int", nullable: false),
                    ExternalReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Campaigns", x => x.Id);
                    table.CheckConstraint("CK_Campaigns_Status", "[Status] IN ('DRAFT', 'ACTIVE', 'PAUSED', 'COMPLETED', 'CANCELLED')");
                    table.CheckConstraint("CK_Campaigns_Type", "[CampaignType] IN ('GENERAL', 'ADVERTISEMENT', 'PROMOTION', 'EVENT', 'EMAIL_BLAST', 'SOCIAL_MEDIA', 'REFERRAL_PROGRAM')");
                    table.ForeignKey(
                        name: "FK_Campaigns_LeadSources_LeadSourceId",
                        column: x => x.LeadSourceId,
                        principalSchema: "crm",
                        principalTable: "LeadSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesTeams",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TeamLeaderUserId = table.Column<int>(type: "int", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DefaultCampaignId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesTeams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalesTeams_Campaigns_DefaultCampaignId",
                        column: x => x.DefaultCampaignId,
                        principalSchema: "crm",
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PipelineStages",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    StageType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SalesTeamId = table.Column<int>(type: "int", nullable: true),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    WinProbability = table.Column<int>(type: "int", nullable: true),
                    IsWon = table.Column<bool>(type: "bit", nullable: false),
                    IsLost = table.Column<bool>(type: "bit", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineStages", x => x.Id);
                    table.CheckConstraint("CK_PipelineStages_Probability", "[WinProbability] IS NULL OR [WinProbability] BETWEEN 0 AND 100");
                    table.CheckConstraint("CK_PipelineStages_Type", "[StageType] IN ('LEAD', 'OPPORTUNITY')");
                    table.ForeignKey(
                        name: "FK_PipelineStages_SalesTeams_SalesTeamId",
                        column: x => x.SalesTeamId,
                        principalSchema: "crm",
                        principalTable: "SalesTeams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesTeamMembers",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SalesTeamId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    JoinedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesTeamMembers", x => x.Id);
                    table.CheckConstraint("CK_SalesTeamMembers_Role", "[Role] IN ('LEADER', 'MEMBER')");
                    table.ForeignKey(
                        name: "FK_SalesTeamMembers_SalesTeams_SalesTeamId",
                        column: x => x.SalesTeamId,
                        principalSchema: "crm",
                        principalTable: "SalesTeams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Activities",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeadId = table.Column<int>(type: "int", nullable: true),
                    OpportunityId = table.Column<int>(type: "int", nullable: true),
                    ActivityType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    OutcomeNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ActivityDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DurationMinutes = table.Column<int>(type: "int", nullable: true),
                    ScheduledDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DueDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsCompleted = table.Column<bool>(type: "bit", nullable: false),
                    PerformedByUserId = table.Column<int>(type: "int", nullable: false),
                    ContactPersonId = table.Column<int>(type: "int", nullable: true),
                    FollowUpDate = table.Column<DateTime>(type: "date", nullable: true),
                    FollowUpType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    FollowUpNotes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PipelineStageId = table.Column<int>(type: "int", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Activities", x => x.Id);
                    table.CheckConstraint("CK_Activities_Outcome", "[Outcome] IS NULL OR [Outcome] IN ('SUCCESSFUL', 'UNSUCCESSFUL', 'NO_RESPONSE', 'FOLLOW_UP_NEEDED', 'REFERRED', 'COMPLETED', 'CANCELLED')");
                    table.CheckConstraint("CK_Activities_ParentRef", "[LeadId] IS NOT NULL OR [OpportunityId] IS NOT NULL");
                    table.CheckConstraint("CK_Activities_Type", "[ActivityType] IN ('PHONE_CALL', 'MEETING', 'EMAIL_SENT', 'EMAIL_RECEIVED', 'NOTE', 'TASK', 'FOLLOW_UP', 'SITE_VISIT', 'DEMO', 'PRESENTATION', 'STATUS_CHANGE', 'DOCUMENT_SHARED', 'OTHER')");
                    table.ForeignKey(
                        name: "FK_Activities_PipelineStages_PipelineStageId",
                        column: x => x.PipelineStageId,
                        principalSchema: "crm",
                        principalTable: "PipelineStages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ActivityParticipants",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActivityId = table.Column<int>(type: "int", nullable: false),
                    ParticipantType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    ContactPersonId = table.Column<int>(type: "int", nullable: true),
                    ExternalName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ExternalEmail = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Role = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityParticipants", x => x.Id);
                    table.CheckConstraint("CK_ActivityParticipants_Role", "[Role] IS NULL OR [Role] IN ('ORGANIZER', 'REQUIRED', 'OPTIONAL', 'ATTENDEE')");
                    table.CheckConstraint("CK_ActivityParticipants_Type", "[ParticipantType] IN ('INTERNAL_USER', 'CUSTOMER_CONTACT', 'EXTERNAL')");
                    table.ForeignKey(
                        name: "FK_ActivityParticipants_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalSchema: "crm",
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ActivityDocuments",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActivityId = table.Column<int>(type: "int", nullable: true),
                    LeadId = table.Column<int>(type: "int", nullable: true),
                    OpportunityId = table.Column<int>(type: "int", nullable: true),
                    DocumentType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DocumentName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FileId = table.Column<int>(type: "int", nullable: false),
                    Direction = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    PipelineStageId = table.Column<int>(type: "int", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SharedByUserId = table.Column<int>(type: "int", nullable: false),
                    SharedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityDocuments", x => x.Id);
                    table.CheckConstraint("CK_ActivityDocuments_Direction", "[Direction] IN ('SENT_TO_CUSTOMER', 'RECEIVED_FROM_CUSTOMER', 'INTERNAL')");
                    table.CheckConstraint("CK_ActivityDocuments_Type", "[DocumentType] IN ('BROCHURE', 'PRICE_LIST', 'PROPOSAL', 'SPECIFICATION', 'CONTRACT', 'CUSTOMER_PO', 'QUOTATION_PDF', 'PRESENTATION', 'IMAGE', 'MEETING_MINUTES', 'NDA', 'OTHER')");
                    table.ForeignKey(
                        name: "FK_ActivityDocuments_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalSchema: "crm",
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ActivityDocuments_ActivityFiles_FileId",
                        column: x => x.FileId,
                        principalSchema: "crm",
                        principalTable: "ActivityFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ActivityDocuments_PipelineStages_PipelineStageId",
                        column: x => x.PipelineStageId,
                        principalSchema: "crm",
                        principalTable: "PipelineStages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Leads",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeadNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Topic = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Salutation = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    FirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(201)", maxLength: 201, nullable: false),
                    JobTitle = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    MobilePhone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Website = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AddressLine1 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AddressLine2 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    StateProvince = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PostalCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Country = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AccountUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContactPersonId = table.Column<int>(type: "int", nullable: true),
                    LeadSourceId = table.Column<int>(type: "int", nullable: true),
                    CampaignId = table.Column<int>(type: "int", nullable: true),
                    SourceMedium = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    SourceReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ExternalSourceSystem = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ExternalSourceId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BudgetAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    BudgetCurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    BudgetConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    AuthorityConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    NeedIdentified = table.Column<bool>(type: "bit", nullable: false),
                    PurchaseTimeframe = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Rating = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Priority = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    AssignedToUserId = table.Column<int>(type: "int", nullable: true),
                    AssignedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SalesTeamId = table.Column<int>(type: "int", nullable: true),
                    PreviousOwnerUserId = table.Column<int>(type: "int", nullable: true),
                    PipelineStageId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StatusReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    QualifiedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DisqualifiedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConvertedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConvertedAccountUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConvertedContactId = table.Column<int>(type: "int", nullable: true),
                    ConvertedOpportunityId = table.Column<int>(type: "int", nullable: true),
                    EstimatedRevenue = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    EstimatedCloseDate = table.Column<DateTime>(type: "date", nullable: true),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedByName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    TraceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Leads", x => x.Id);
                    table.CheckConstraint("CK_Leads_Priority", "[Priority] IN ('HIGH', 'NORMAL', 'LOW')");
                    table.CheckConstraint("CK_Leads_Rating", "[Rating] IN ('HOT', 'WARM', 'COLD')");
                    table.CheckConstraint("CK_Leads_Status", "[Status] IN ('NEW', 'CONTACTED', 'QUALIFIED', 'DISQUALIFIED', 'CONVERTED')");
                    table.CheckConstraint("CK_Leads_Timeframe", "[PurchaseTimeframe] IS NULL OR [PurchaseTimeframe] IN ('IMMEDIATE', 'THIS_QUARTER', 'THIS_YEAR', 'NEXT_YEAR', 'UNKNOWN')");
                    table.ForeignKey(
                        name: "FK_Leads_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalSchema: "crm",
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Leads_LeadSources_LeadSourceId",
                        column: x => x.LeadSourceId,
                        principalSchema: "crm",
                        principalTable: "LeadSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Leads_PipelineStages_PipelineStageId",
                        column: x => x.PipelineStageId,
                        principalSchema: "crm",
                        principalTable: "PipelineStages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Leads_SalesTeams_SalesTeamId",
                        column: x => x.SalesTeamId,
                        principalSchema: "crm",
                        principalTable: "SalesTeams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Opportunities",
                schema: "crm",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Uuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OpportunityNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Topic = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    AccountUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContactPersonId = table.Column<int>(type: "int", nullable: true),
                    SourceLeadId = table.Column<int>(type: "int", nullable: false),
                    CampaignId = table.Column<int>(type: "int", nullable: true),
                    PipelineStageId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EstimatedRevenue = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    ActualRevenue = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    EstimatedCloseDate = table.Column<DateTime>(type: "date", nullable: true),
                    ActualCloseDate = table.Column<DateTime>(type: "date", nullable: true),
                    WinLossReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CompetitorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AssignedToUserId = table.Column<int>(type: "int", nullable: true),
                    SalesTeamId = table.Column<int>(type: "int", nullable: true),
                    SourceInquiryUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceQuotationUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceOrderUuid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    TraceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Opportunities", x => x.Id);
                    table.CheckConstraint("CK_Opportunities_Status", "[Status] IN ('OPEN', 'WON', 'LOST')");
                    table.ForeignKey(
                        name: "FK_Opportunities_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalSchema: "crm",
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Opportunities_Leads_SourceLeadId",
                        column: x => x.SourceLeadId,
                        principalSchema: "crm",
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Opportunities_PipelineStages_PipelineStageId",
                        column: x => x.PipelineStageId,
                        principalSchema: "crm",
                        principalTable: "PipelineStages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Opportunities_SalesTeams_SalesTeamId",
                        column: x => x.SalesTeamId,
                        principalSchema: "crm",
                        principalTable: "SalesTeams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_DueDate",
                schema: "crm",
                table: "Activities",
                columns: new[] { "OrganizationId", "DueDate" },
                filter: "[DueDate] IS NOT NULL AND [IsCompleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_FollowUp",
                schema: "crm",
                table: "Activities",
                columns: new[] { "OrganizationId", "FollowUpDate" },
                filter: "[FollowUpDate] IS NOT NULL AND [IsCompleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_Lead",
                schema: "crm",
                table: "Activities",
                columns: new[] { "LeadId", "ActivityDate" },
                descending: new[] { false, true },
                filter: "[LeadId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_Opportunity",
                schema: "crm",
                table: "Activities",
                columns: new[] { "OpportunityId", "ActivityDate" },
                descending: new[] { false, true },
                filter: "[OpportunityId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_PerformedBy",
                schema: "crm",
                table: "Activities",
                columns: new[] { "PerformedByUserId", "ActivityDate" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_PipelineStageId",
                schema: "crm",
                table: "Activities",
                column: "PipelineStageId");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_Scheduled",
                schema: "crm",
                table: "Activities",
                columns: new[] { "OrganizationId", "ScheduledDate" },
                filter: "[ScheduledDate] IS NOT NULL AND [IsCompleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_Type",
                schema: "crm",
                table: "Activities",
                columns: new[] { "OrganizationId", "ActivityType", "ActivityDate" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_Uuid",
                schema: "crm",
                table: "Activities",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivityDocuments_Activity",
                schema: "crm",
                table: "ActivityDocuments",
                column: "ActivityId",
                filter: "[ActivityId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityDocuments_FileId",
                schema: "crm",
                table: "ActivityDocuments",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityDocuments_Lead",
                schema: "crm",
                table: "ActivityDocuments",
                column: "LeadId",
                filter: "[LeadId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityDocuments_Opportunity",
                schema: "crm",
                table: "ActivityDocuments",
                column: "OpportunityId",
                filter: "[OpportunityId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityDocuments_OrganizationId",
                schema: "crm",
                table: "ActivityDocuments",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityDocuments_PipelineStageId",
                schema: "crm",
                table: "ActivityDocuments",
                column: "PipelineStageId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityDocuments_Uuid",
                schema: "crm",
                table: "ActivityDocuments",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivityFiles_OrganizationId",
                schema: "crm",
                table: "ActivityFiles",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityParticipants_Activity",
                schema: "crm",
                table: "ActivityParticipants",
                column: "ActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityParticipants_OrganizationId",
                schema: "crm",
                table: "ActivityParticipants",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_Dates",
                schema: "crm",
                table: "Campaigns",
                columns: new[] { "OrganizationId", "StartDate", "EndDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_LeadSourceId",
                schema: "crm",
                table: "Campaigns",
                column: "LeadSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_Status",
                schema: "crm",
                table: "Campaigns",
                columns: new[] { "OrganizationId", "Status" },
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_Uuid",
                schema: "crm",
                table: "Campaigns",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_Campaigns_OrgCode",
                schema: "crm",
                table: "Campaigns",
                columns: new[] { "OrganizationId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeadApiKeys_Uuid",
                schema: "crm",
                table: "LeadApiKeys",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_LeadApiKeys_Hash",
                schema: "crm",
                table: "LeadApiKeys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_LeadApiKeys_OrgName",
                schema: "crm",
                table: "LeadApiKeys",
                columns: new[] { "OrganizationId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Leads_Account",
                schema: "crm",
                table: "Leads",
                column: "AccountUuid",
                filter: "[AccountUuid] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_AssignedTo",
                schema: "crm",
                table: "Leads",
                column: "AssignedToUserId",
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_Campaign",
                schema: "crm",
                table: "Leads",
                column: "CampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_CompanyName",
                schema: "crm",
                table: "Leads",
                columns: new[] { "OrganizationId", "CompanyName" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_ContactPerson",
                schema: "crm",
                table: "Leads",
                column: "ContactPersonId",
                filter: "[ContactPersonId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_ConvertedOpportunityId",
                schema: "crm",
                table: "Leads",
                column: "ConvertedOpportunityId");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_CreatedAt",
                schema: "crm",
                table: "Leads",
                columns: new[] { "OrganizationId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_Email",
                schema: "crm",
                table: "Leads",
                columns: new[] { "OrganizationId", "Email" },
                filter: "[Email] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_LeadSourceId",
                schema: "crm",
                table: "Leads",
                column: "LeadSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_Phone",
                schema: "crm",
                table: "Leads",
                columns: new[] { "OrganizationId", "MobilePhone" },
                filter: "[MobilePhone] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_PipelineStage",
                schema: "crm",
                table: "Leads",
                columns: new[] { "OrganizationId", "PipelineStageId" },
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_PipelineStageId",
                schema: "crm",
                table: "Leads",
                column: "PipelineStageId");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_SalesTeam",
                schema: "crm",
                table: "Leads",
                columns: new[] { "OrganizationId", "SalesTeamId" },
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_SalesTeamId",
                schema: "crm",
                table: "Leads",
                column: "SalesTeamId");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_Status",
                schema: "crm",
                table: "Leads",
                columns: new[] { "OrganizationId", "Status" },
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_Uuid",
                schema: "crm",
                table: "Leads",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_Leads_OrgNumber",
                schema: "crm",
                table: "Leads",
                columns: new[] { "OrganizationId", "LeadNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeadSources_Uuid",
                schema: "crm",
                table: "LeadSources",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_LeadSources_OrgCode",
                schema: "crm",
                table: "LeadSources",
                columns: new[] { "OrganizationId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_Account",
                schema: "crm",
                table: "Opportunities",
                column: "AccountUuid");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_AssignedTo",
                schema: "crm",
                table: "Opportunities",
                column: "AssignedToUserId",
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_CampaignId",
                schema: "crm",
                table: "Opportunities",
                column: "CampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_CloseDate",
                schema: "crm",
                table: "Opportunities",
                columns: new[] { "OrganizationId", "EstimatedCloseDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_Contact",
                schema: "crm",
                table: "Opportunities",
                column: "ContactPersonId",
                filter: "[ContactPersonId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_PipelineStage",
                schema: "crm",
                table: "Opportunities",
                columns: new[] { "OrganizationId", "PipelineStageId" },
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_PipelineStageId",
                schema: "crm",
                table: "Opportunities",
                column: "PipelineStageId");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_SalesTeamId",
                schema: "crm",
                table: "Opportunities",
                column: "SalesTeamId");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_SourceLead",
                schema: "crm",
                table: "Opportunities",
                column: "SourceLeadId");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_Status",
                schema: "crm",
                table: "Opportunities",
                columns: new[] { "OrganizationId", "Status" },
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_Uuid",
                schema: "crm",
                table: "Opportunities",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_Opportunities_OrgNumber",
                schema: "crm",
                table: "Opportunities",
                columns: new[] { "OrganizationId", "OpportunityNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PipelineStages_SalesTeamId",
                schema: "crm",
                table: "PipelineStages",
                column: "SalesTeamId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineStages_Sequence",
                schema: "crm",
                table: "PipelineStages",
                columns: new[] { "OrganizationId", "SalesTeamId", "StageType", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_PipelineStages_Uuid",
                schema: "crm",
                table: "PipelineStages",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_PipelineStages_OrgTeamCode",
                schema: "crm",
                table: "PipelineStages",
                columns: new[] { "OrganizationId", "SalesTeamId", "Code" },
                unique: true,
                filter: "[SalesTeamId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RecordHistory_Entity",
                schema: "crm",
                table: "RecordHistory",
                columns: new[] { "OrganizationId", "EntityType", "EntityId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesTeamMembers_SalesTeamId",
                schema: "crm",
                table: "SalesTeamMembers",
                column: "SalesTeamId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesTeamMembers_User",
                schema: "crm",
                table: "SalesTeamMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "UQ_SalesTeamMembers_TeamUser",
                schema: "crm",
                table: "SalesTeamMembers",
                columns: new[] { "OrganizationId", "SalesTeamId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesTeams_DefaultCampaignId",
                schema: "crm",
                table: "SalesTeams",
                column: "DefaultCampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesTeams_Uuid",
                schema: "crm",
                table: "SalesTeams",
                column: "Uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_SalesTeams_OrgCode",
                schema: "crm",
                table: "SalesTeams",
                columns: new[] { "OrganizationId", "Code" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Activities_Leads_LeadId",
                schema: "crm",
                table: "Activities",
                column: "LeadId",
                principalSchema: "crm",
                principalTable: "Leads",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Activities_Opportunities_OpportunityId",
                schema: "crm",
                table: "Activities",
                column: "OpportunityId",
                principalSchema: "crm",
                principalTable: "Opportunities",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ActivityDocuments_Leads_LeadId",
                schema: "crm",
                table: "ActivityDocuments",
                column: "LeadId",
                principalSchema: "crm",
                principalTable: "Leads",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ActivityDocuments_Opportunities_OpportunityId",
                schema: "crm",
                table: "ActivityDocuments",
                column: "OpportunityId",
                principalSchema: "crm",
                principalTable: "Opportunities",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Leads_Opportunities_ConvertedOpportunityId",
                schema: "crm",
                table: "Leads",
                column: "ConvertedOpportunityId",
                principalSchema: "crm",
                principalTable: "Opportunities",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Opportunities_Leads_SourceLeadId",
                schema: "crm",
                table: "Opportunities");

            migrationBuilder.DropTable(
                name: "ActivityDocuments",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "ActivityParticipants",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "LeadApiKeys",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "RecordHistory",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "SalesTeamMembers",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "ActivityFiles",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "Activities",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "Leads",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "Opportunities",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "PipelineStages",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "SalesTeams",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "Campaigns",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "LeadSources",
                schema: "crm");
        }
    }
}
