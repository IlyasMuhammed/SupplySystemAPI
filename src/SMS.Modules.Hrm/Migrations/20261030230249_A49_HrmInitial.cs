using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Hrm.Migrations
{
    /// <summary>A49 (CORE) — the hrm schema, all phases H1..H6E (docs/hrm/ADDENDUM-49-ANALYSIS.md D-3). Generated from the EF script, then every statement guarded: every API start migrates the shared, drifted database, so each table and index is created only when missing. Additive: nothing outside schema hrm. Down is EF's.</summary>
    public partial class A49_HrmInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"IF SCHEMA_ID(N'hrm') IS NULL EXEC(N'CREATE SCHEMA [hrm];');");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.Branches', N'U') IS NULL
CREATE TABLE [hrm].[Branches] (
    [Id] int NOT NULL IDENTITY,
    [Code] nvarchar(20) NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [Address] nvarchar(500) NULL,
    [City] nvarchar(100) NULL,
    [WarehouseUuid] uniqueidentifier NULL,
    [IsActive] bit NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_Branches] PRIMARY KEY ([Id])
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.Candidates', N'U') IS NULL
CREATE TABLE [hrm].[Candidates] (
    [Id] int NOT NULL IDENTITY,
    [FirstName] nvarchar(60) NOT NULL,
    [LastName] nvarchar(60) NOT NULL,
    [Email] nvarchar(200) NULL,
    [Phone] nvarchar(30) NULL,
    [Source] nvarchar(50) NULL,
    [ResumeAttachmentUuid] uniqueidentifier NULL,
    [Notes] nvarchar(2000) NULL,
    [IsDeleted] bit NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_Candidates] PRIMARY KEY ([Id])
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.Courses', N'U') IS NULL
CREATE TABLE [hrm].[Courses] (
    [Id] int NOT NULL IDENTITY,
    [Code] nvarchar(20) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [Description] nvarchar(2000) NULL,
    [Provider] nvarchar(200) NULL,
    [DurationHours] decimal(9,2) NULL,
    [Cost] decimal(18,2) NULL,
    [IsActive] bit NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_Courses] PRIMARY KEY ([Id])
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.Designations', N'U') IS NULL
CREATE TABLE [hrm].[Designations] (
    [Id] int NOT NULL IDENTITY,
    [Code] nvarchar(20) NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [Level] int NOT NULL,
    [IsActive] bit NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_Designations] PRIMARY KEY ([Id])
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.Employees', N'U') IS NULL
CREATE TABLE [hrm].[Employees] (
    [Id] int NOT NULL IDENTITY,
    [EmployeeNumber] nvarchar(30) NOT NULL,
    [FirstName] nvarchar(60) NOT NULL,
    [LastName] nvarchar(60) NOT NULL,
    [FatherOrHusbandName] nvarchar(100) NULL,
    [DateOfBirth] date NULL,
    [Gender] nvarchar(10) NULL,
    [MaritalStatus] nvarchar(20) NULL,
    [Nationality] nvarchar(3) NULL,
    [CnicEncrypted] nvarchar(500) NULL,
    [CnicHash] nvarchar(64) NULL,
    [CnicLastDigit] nvarchar(1) NULL,
    [NtnEncrypted] nvarchar(500) NULL,
    [NtnHash] nvarchar(64) NULL,
    [NtnLast4] nvarchar(4) NULL,
    [PersonalEmail] nvarchar(200) NULL,
    [WorkEmail] nvarchar(200) NULL,
    [Phone] nvarchar(30) NULL,
    [Mobile] nvarchar(30) NULL,
    [CurrentAddressJson] nvarchar(2000) NULL,
    [PermanentAddressJson] nvarchar(2000) NULL,
    [EmergencyContactsJson] nvarchar(2000) NULL,
    [PhotoAttachmentUuid] uniqueidentifier NULL,
    [Status] nvarchar(20) NOT NULL,
    [UserId] int NULL,
    [SourceCandidateUuid] uniqueidentifier NULL,
    [JoinDate] date NULL,
    [LeaveDate] date NULL,
    [DepartureReason] nvarchar(20) NULL,
    [Notes] nvarchar(2000) NULL,
    [IsDeleted] bit NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_Employees] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Employees_Departure] CHECK ([DepartureReason] IS NULL OR [DepartureReason] IN ('Resignation', 'Retirement', 'Termination', 'ContractEnd', 'Death', 'Other')),
    CONSTRAINT [CK_Employees_Gender] CHECK ([Gender] IS NULL OR [Gender] IN ('M', 'F', 'Other')),
    CONSTRAINT [CK_Employees_Marital] CHECK ([MaritalStatus] IS NULL OR [MaritalStatus] IN ('Single', 'Married', 'Widowed', 'Divorced', 'Other')),
    CONSTRAINT [CK_Employees_Status] CHECK ([Status] IN ('Candidate', 'Probation', 'Active', 'OnNotice', 'Suspended', 'Terminated', 'Archived'))
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.ExpenseCategories', N'U') IS NULL
CREATE TABLE [hrm].[ExpenseCategories] (
    [Id] int NOT NULL IDENTITY,
    [Code] nvarchar(20) NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [AccountCode] nvarchar(20) NULL,
    [AccountRole] nvarchar(50) NULL,
    [DefaultTaxCode] nvarchar(20) NULL,
    [RequiresReceipt] bit NOT NULL,
    [ReceiptThreshold] decimal(18,2) NULL,
    [RatePerUnit] decimal(18,2) NULL,
    [UnitName] nvarchar(20) NULL,
    [IsActive] bit NOT NULL,
    [IsSeeded] bit NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_ExpenseCategories] PRIMARY KEY ([Id])
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.Grades', N'U') IS NULL
CREATE TABLE [hrm].[Grades] (
    [Id] int NOT NULL IDENTITY,
    [Code] nvarchar(20) NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [MinSalary] decimal(18,2) NULL,
    [MaxSalary] decimal(18,2) NULL,
    [IsActive] bit NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_Grades] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Grades_MinMax] CHECK ([MinSalary] IS NULL OR [MaxSalary] IS NULL OR [MinSalary] <= [MaxSalary])
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.LeaveTypes', N'U') IS NULL
CREATE TABLE [hrm].[LeaveTypes] (
    [Id] int NOT NULL IDENTITY,
    [Code] nvarchar(20) NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [IsPaid] bit NOT NULL,
    [CarryForwardCap] decimal(9,2) NULL,
    [AttachmentAfterDays] int NULL,
    [GenderRestriction] nvarchar(10) NULL,
    [AllowNegativeBalance] bit NOT NULL,
    [IsActive] bit NOT NULL,
    [IsSeeded] bit NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_LeaveTypes] PRIMARY KEY ([Id])
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.LetterTemplates', N'U') IS NULL
CREATE TABLE [hrm].[LetterTemplates] (
    [Id] int NOT NULL IDENTITY,
    [Code] nvarchar(30) NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [Kind] nvarchar(30) NOT NULL,
    [Subject] nvarchar(200) NOT NULL,
    [Body] nvarchar(max) NOT NULL,
    [IsSystem] bit NOT NULL,
    [IsActive] bit NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_LetterTemplates] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_LetterTemplate_Kind] CHECK ([Kind] IN ('OfferLetter', 'ExperienceCertificate', 'SalaryCertificate', 'Custom'))
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.PayrollRuns', N'U') IS NULL
CREATE TABLE [hrm].[PayrollRuns] (
    [Id] int NOT NULL IDENTITY,
    [RunNumber] nvarchar(30) NOT NULL,
    [PeriodStart] date NOT NULL,
    [PeriodEnd] date NOT NULL,
    [PayDate] date NOT NULL,
    [PayrollGroup] nvarchar(30) NOT NULL,
    [Status] nvarchar(20) NOT NULL,
    [CurrencyCode] nvarchar(3) NOT NULL,
    [TotalEmployees] int NOT NULL,
    [BlockedEmployees] int NOT NULL,
    [TotalGross] decimal(18,2) NOT NULL,
    [TotalDeductions] decimal(18,2) NOT NULL,
    [TotalNet] decimal(18,2) NOT NULL,
    [TotalEmployerCost] decimal(18,2) NOT NULL,
    [CalculatedAt] datetime2 NULL,
    [CalculatedBy] int NULL,
    [ReviewedAt] datetime2 NULL,
    [ReviewedBy] int NULL,
    [ApprovedAt] datetime2 NULL,
    [ApprovedBy] int NULL,
    [FinalizedAt] datetime2 NULL,
    [FinalizedBy] int NULL,
    [PostedAt] datetime2 NULL,
    [PaidAt] datetime2 NULL,
    [PaidBy] int NULL,
    [PaymentReference] nvarchar(100) NULL,
    [PaidFromAccount] nvarchar(10) NOT NULL,
    [PostingDate] date NULL,
    [PostingNote] nvarchar(1000) NULL,
    [CostCenterMode] nvarchar(20) NOT NULL,
    [ReversalOfRunId] int NULL,
    [ReversalReason] nvarchar(500) NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_PayrollRuns] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_PayrollRun_Dates] CHECK ([PeriodEnd] >= [PeriodStart]),
    CONSTRAINT [CK_PayrollRun_Mode] CHECK ([CostCenterMode] IN ('CENTRAL', 'BY_DEPARTMENT')),
    CONSTRAINT [CK_PayrollRun_PaidFrom] CHECK ([PaidFromAccount] IN ('Bank', 'Cash')),
    CONSTRAINT [CK_PayrollRun_Status] CHECK ([Status] IN ('Draft', 'Calculated', 'UnderReview', 'Approved', 'Finalized', 'Posted', 'Paid', 'Cancelled', 'Reversed')),
    CONSTRAINT [FK_PayrollRuns_PayrollRuns_ReversalOfRunId] FOREIGN KEY ([ReversalOfRunId]) REFERENCES [hrm].[PayrollRuns] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.ReviewCycles', N'U') IS NULL
CREATE TABLE [hrm].[ReviewCycles] (
    [Id] int NOT NULL IDENTITY,
    [Name] nvarchar(100) NOT NULL,
    [Period] nvarchar(20) NOT NULL,
    [StartDate] date NOT NULL,
    [EndDate] date NOT NULL,
    [Status] nvarchar(20) NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_ReviewCycles] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_ReviewCycle_Dates] CHECK ([EndDate] >= [StartDate]),
    CONSTRAINT [CK_ReviewCycle_Period] CHECK ([Period] IN ('Annual', 'Quarterly')),
    CONSTRAINT [CK_ReviewCycle_Status] CHECK ([Status] IN ('Draft', 'Open', 'Closed'))
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.SalaryComponents', N'U') IS NULL
CREATE TABLE [hrm].[SalaryComponents] (
    [Id] int NOT NULL IDENTITY,
    [Code] nvarchar(20) NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [Type] nvarchar(30) NOT NULL,
    [IsTaxable] bit NOT NULL,
    [InGross] bit NOT NULL,
    [CalcMethod] nvarchar(10) NOT NULL,
    [DefaultValue] decimal(18,2) NULL,
    [PercentOf] nvarchar(20) NULL,
    [Formula] nvarchar(1000) NULL,
    [PostingRole] nvarchar(50) NULL,
    [ExpenseRole] nvarchar(50) NULL,
    [IsSystem] bit NOT NULL,
    [IsActive] bit NOT NULL,
    [SortOrder] int NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_SalaryComponents] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_SalaryComponent_Calc] CHECK ([CalcMethod] IN ('Fixed', 'Percent', 'Formula')),
    CONSTRAINT [CK_SalaryComponent_Type] CHECK ([Type] IN ('Earning', 'Deduction', 'EmployerContribution', 'Result'))
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.SalaryStructures', N'U') IS NULL
CREATE TABLE [hrm].[SalaryStructures] (
    [Id] int NOT NULL IDENTITY,
    [Code] nvarchar(20) NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [Description] nvarchar(500) NULL,
    [IsDefault] bit NOT NULL,
    [IsActive] bit NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_SalaryStructures] PRIMARY KEY ([Id])
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.SensitiveAccessLogs', N'U') IS NULL
CREATE TABLE [hrm].[SensitiveAccessLogs] (
    [Id] int NOT NULL IDENTITY,
    [EmployeeId] int NOT NULL,
    [Field] nvarchar(30) NOT NULL,
    [Purpose] nvarchar(100) NOT NULL,
    [UserId] int NULL,
    [UserName] nvarchar(200) NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    CONSTRAINT [PK_SensitiveAccessLogs] PRIMARY KEY ([Id])
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.Shifts', N'U') IS NULL
CREATE TABLE [hrm].[Shifts] (
    [Id] int NOT NULL IDENTITY,
    [Code] nvarchar(20) NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [StartTime] time NOT NULL,
    [EndTime] time NOT NULL,
    [BreakMinutes] int NOT NULL,
    [GraceInMinutes] int NOT NULL,
    [GraceOutMinutes] int NOT NULL,
    [HalfDayMinutes] int NOT NULL,
    [OvertimeThresholdMinutes] int NOT NULL,
    [WeeklyOffDays] int NOT NULL,
    [IsDefault] bit NOT NULL,
    [IsActive] bit NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_Shifts] PRIMARY KEY ([Id])
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.StatutoryRates', N'U') IS NULL
CREATE TABLE [hrm].[StatutoryRates] (
    [Id] int NOT NULL IDENTITY,
    [RateType] nvarchar(20) NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [CountryCode] nvarchar(3) NOT NULL,
    [EmployeePercent] decimal(9,4) NOT NULL,
    [EmployerPercent] decimal(9,4) NOT NULL,
    [WageCeiling] decimal(18,2) NULL,
    [FixedBasis] decimal(18,2) NULL,
    [EffectiveFrom] date NOT NULL,
    [EffectiveTo] date NULL,
    [IsVerified] bit NOT NULL,
    [VerifiedBy] int NULL,
    [VerifiedByName] nvarchar(200) NULL,
    [VerifiedAt] datetime2 NULL,
    [SourceNote] nvarchar(500) NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_StatutoryRates] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_StatutoryRate_Type] CHECK ([RateType] IN ('EOBI', 'PF', 'SS', 'GRATUITY'))
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.StatutoryRemittances', N'U') IS NULL
CREATE TABLE [hrm].[StatutoryRemittances] (
    [Id] int NOT NULL IDENTITY,
    [RemittanceNumber] nvarchar(30) NOT NULL,
    [RemittanceType] nvarchar(20) NOT NULL,
    [PeriodStart] date NOT NULL,
    [PeriodEnd] date NOT NULL,
    [Amount] decimal(18,2) NOT NULL,
    [PaidOn] date NOT NULL,
    [PaidFromAccount] nvarchar(10) NOT NULL,
    [Reference] nvarchar(100) NULL,
    [AuthorityName] nvarchar(200) NULL,
    [Notes] nvarchar(500) NULL,
    [PostingState] nvarchar(10) NOT NULL,
    [PostingNote] nvarchar(1000) NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_StatutoryRemittances] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Remittance_Amount] CHECK ([Amount] > 0),
    CONSTRAINT [CK_Remittance_PaidFrom] CHECK ([PaidFromAccount] IN ('Bank', 'Cash')),
    CONSTRAINT [CK_Remittance_Posting] CHECK ([PostingState] IN ('None', 'Pending', 'Posted', 'Failed', 'NoTarget')),
    CONSTRAINT [CK_Remittance_Type] CHECK ([RemittanceType] IN ('IncomeTax', 'EOBI', 'PF', 'SS'))
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.TaxSlabTables', N'U') IS NULL
CREATE TABLE [hrm].[TaxSlabTables] (
    [Id] int NOT NULL IDENTITY,
    [Code] nvarchar(30) NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [CountryCode] nvarchar(3) NOT NULL,
    [EffectiveFrom] date NOT NULL,
    [EffectiveTo] date NULL,
    [IsVerified] bit NOT NULL,
    [VerifiedBy] int NULL,
    [VerifiedByName] nvarchar(200) NULL,
    [VerifiedAt] datetime2 NULL,
    [SourceNote] nvarchar(500) NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_TaxSlabTables] PRIMARY KEY ([Id])
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.CostCenters', N'U') IS NULL
CREATE TABLE [hrm].[CostCenters] (
    [Id] int NOT NULL IDENTITY,
    [Code] nvarchar(20) NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [ParentId] int NULL,
    [BranchId] int NULL,
    [IsActive] bit NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_CostCenters] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_CostCenters_Branches_BranchId] FOREIGN KEY ([BranchId]) REFERENCES [hrm].[Branches] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_CostCenters_CostCenters_ParentId] FOREIGN KEY ([ParentId]) REFERENCES [hrm].[CostCenters] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.Holidays', N'U') IS NULL
CREATE TABLE [hrm].[Holidays] (
    [Id] int NOT NULL IDENTITY,
    [Date] date NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [CountryCode] nvarchar(3) NOT NULL,
    [BranchId] int NULL,
    [IsEstimated] bit NOT NULL,
    [IsSeeded] bit NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_Holidays] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Holidays_Branches_BranchId] FOREIGN KEY ([BranchId]) REFERENCES [hrm].[Branches] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.AttendanceEvents', N'U') IS NULL
CREATE TABLE [hrm].[AttendanceEvents] (
    [Id] int NOT NULL IDENTITY,
    [EmployeeId] int NOT NULL,
    [EventTimeUtc] datetime2 NOT NULL,
    [EventType] nvarchar(10) NOT NULL,
    [Source] nvarchar(20) NOT NULL,
    [ExternalEventId] nvarchar(100) NULL,
    [Note] nvarchar(500) NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    CONSTRAINT [PK_AttendanceEvents] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_AttendanceEvent_Source] CHECK ([Source] IN ('MANUAL', 'DEVICE', 'IMPORT', 'WEB')),
    CONSTRAINT [CK_AttendanceEvent_Type] CHECK ([EventType] IN ('IN', 'OUT', 'UNKNOWN')),
    CONSTRAINT [FK_AttendanceEvents_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [hrm].[Employees] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.CourseEnrollments', N'U') IS NULL
CREATE TABLE [hrm].[CourseEnrollments] (
    [Id] int NOT NULL IDENTITY,
    [EmployeeId] int NOT NULL,
    [CourseId] int NOT NULL,
    [EnrolledDate] date NOT NULL,
    [CompletedDate] date NULL,
    [Status] nvarchar(20) NOT NULL,
    [CertificateAttachmentUuid] uniqueidentifier NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_CourseEnrollments] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Enrollment_Status] CHECK ([Status] IN ('Enrolled', 'Completed', 'Cancelled')),
    CONSTRAINT [FK_CourseEnrollments_Courses_CourseId] FOREIGN KEY ([CourseId]) REFERENCES [hrm].[Courses] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_CourseEnrollments_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [hrm].[Employees] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.EmployeeBankAccounts', N'U') IS NULL
CREATE TABLE [hrm].[EmployeeBankAccounts] (
    [Id] int NOT NULL IDENTITY,
    [EmployeeId] int NOT NULL,
    [BankName] nvarchar(100) NOT NULL,
    [BranchName] nvarchar(100) NULL,
    [AccountTitle] nvarchar(200) NULL,
    [AccountNumberEncrypted] nvarchar(500) NULL,
    [AccountNumberLast4] nvarchar(4) NULL,
    [IbanEncrypted] nvarchar(500) NULL,
    [IbanLast4] nvarchar(4) NULL,
    [IsPrimary] bit NOT NULL,
    [IsActive] bit NOT NULL,
    [PaymentMethod] nvarchar(10) NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_EmployeeBankAccounts] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_BankAccount_Method] CHECK ([PaymentMethod] IN ('Bank', 'Cash', 'Cheque')),
    CONSTRAINT [FK_EmployeeBankAccounts_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [hrm].[Employees] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.EmployeeDocuments', N'U') IS NULL
CREATE TABLE [hrm].[EmployeeDocuments] (
    [Id] int NOT NULL IDENTITY,
    [EmployeeId] int NOT NULL,
    [DocumentType] nvarchar(20) NOT NULL,
    [Title] nvarchar(200) NOT NULL,
    [AttachmentUuid] uniqueidentifier NOT NULL,
    [ExpiryDate] date NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    CONSTRAINT [PK_EmployeeDocuments] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_EmployeeDoc_Type] CHECK ([DocumentType] IN ('CNIC', 'CONTRACT', 'CERTIFICATE', 'RESUME', 'PHOTO', 'OTHER')),
    CONSTRAINT [FK_EmployeeDocuments_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [hrm].[Employees] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.FinalSettlements', N'U') IS NULL
CREATE TABLE [hrm].[FinalSettlements] (
    [Id] int NOT NULL IDENTITY,
    [SettlementNumber] nvarchar(30) NOT NULL,
    [EmployeeId] int NOT NULL,
    [LastWorkingDay] date NOT NULL,
    [ServiceYears] decimal(9,4) NOT NULL,
    [LastDrawnSalary] decimal(18,2) NOT NULL,
    [TotalEarnings] decimal(18,2) NOT NULL,
    [TotalDeductions] decimal(18,2) NOT NULL,
    [Net] decimal(18,2) NOT NULL,
    [Status] nvarchar(20) NOT NULL,
    [Notes] nvarchar(2000) NULL,
    [ApprovedBy] int NULL,
    [ApprovedAt] datetime2 NULL,
    [PaidOn] date NULL,
    [PaidFromAccount] nvarchar(10) NOT NULL,
    [PaymentReference] nvarchar(100) NULL,
    [PostingState] nvarchar(10) NOT NULL,
    [PostingNote] nvarchar(1000) NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_FinalSettlements] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Settlement_PaidFrom] CHECK ([PaidFromAccount] IN ('Bank', 'Cash')),
    CONSTRAINT [CK_Settlement_Posting] CHECK ([PostingState] IN ('None', 'Pending', 'Posted', 'Failed', 'NoTarget')),
    CONSTRAINT [CK_Settlement_Status] CHECK ([Status] IN ('Draft', 'Approved', 'Posted', 'Paid', 'Cancelled')),
    CONSTRAINT [FK_FinalSettlements_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [hrm].[Employees] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.Loans', N'U') IS NULL
CREATE TABLE [hrm].[Loans] (
    [Id] int NOT NULL IDENTITY,
    [LoanNumber] nvarchar(30) NOT NULL,
    [EmployeeId] int NOT NULL,
    [Type] nvarchar(20) NOT NULL,
    [Principal] decimal(18,2) NOT NULL,
    [InstallmentCount] int NOT NULL,
    [MonthlyAmount] decimal(18,2) NOT NULL,
    [StartMonth] date NOT NULL,
    [Status] nvarchar(20) NOT NULL,
    [Reason] nvarchar(500) NULL,
    [ApprovedBy] int NULL,
    [ApprovedByName] nvarchar(200) NULL,
    [ApprovedAt] datetime2 NULL,
    [DisbursedOn] date NULL,
    [DisbursedBy] int NULL,
    [PaidFromAccount] nvarchar(10) NOT NULL,
    [PaymentReference] nvarchar(100) NULL,
    [PostingState] nvarchar(10) NOT NULL,
    [PostingNote] nvarchar(1000) NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_Loans] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Loan_Amounts] CHECK ([Principal] > 0 AND [InstallmentCount] > 0),
    CONSTRAINT [CK_Loan_PaidFrom] CHECK ([PaidFromAccount] IN ('Bank', 'Cash')),
    CONSTRAINT [CK_Loan_Posting] CHECK ([PostingState] IN ('None', 'Pending', 'Posted', 'Failed', 'NoTarget')),
    CONSTRAINT [CK_Loan_Status] CHECK ([Status] IN ('Draft', 'Approved', 'Disbursed', 'Closed', 'Rejected', 'Cancelled')),
    CONSTRAINT [CK_Loan_Type] CHECK ([Type] IN ('Advance', 'Personal', 'Housing')),
    CONSTRAINT [FK_Loans_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [hrm].[Employees] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.TaxCertificates', N'U') IS NULL
CREATE TABLE [hrm].[TaxCertificates] (
    [Id] int NOT NULL IDENTITY,
    [EmployeeId] int NOT NULL,
    [TaxYear] int NOT NULL,
    [TotalIncome] decimal(18,2) NOT NULL,
    [TotalTax] decimal(18,2) NOT NULL,
    [AttachmentUuid] uniqueidentifier NULL,
    [GeneratedAt] datetime2 NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    CONSTRAINT [PK_TaxCertificates] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_TaxCertificates_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [hrm].[Employees] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.LeaveLedgerEntries', N'U') IS NULL
CREATE TABLE [hrm].[LeaveLedgerEntries] (
    [Id] int NOT NULL IDENTITY,
    [EmployeeId] int NOT NULL,
    [LeaveTypeId] int NOT NULL,
    [Delta] decimal(9,2) NOT NULL,
    [Reason] nvarchar(30) NOT NULL,
    [Date] date NOT NULL,
    [Year] int NOT NULL,
    [ReferenceUuid] uniqueidentifier NULL,
    [Note] nvarchar(500) NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    CONSTRAINT [PK_LeaveLedgerEntries] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_LeaveLedger_Reason] CHECK ([Reason] IN ('Allocation', 'Accrual', 'CarryForward', 'Expiry', 'Consumption', 'CancellationRestore', 'Adjustment', 'Opening', 'Encashment')),
    CONSTRAINT [FK_LeaveLedgerEntries_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [hrm].[Employees] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_LeaveLedgerEntries_LeaveTypes_LeaveTypeId] FOREIGN KEY ([LeaveTypeId]) REFERENCES [hrm].[LeaveTypes] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.LeavePolicies', N'U') IS NULL
CREATE TABLE [hrm].[LeavePolicies] (
    [Id] int NOT NULL IDENTITY,
    [LeaveTypeId] int NOT NULL,
    [GradeId] int NULL,
    [Entitlement] decimal(9,2) NOT NULL,
    [AccrualMethod] nvarchar(20) NOT NULL,
    [EffectiveFrom] date NOT NULL,
    [EffectiveTo] date NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_LeavePolicies] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_LeavePolicy_Accrual] CHECK ([AccrualMethod] IN ('Annual', 'Monthly', 'None')),
    CONSTRAINT [FK_LeavePolicies_Grades_GradeId] FOREIGN KEY ([GradeId]) REFERENCES [hrm].[Grades] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_LeavePolicies_LeaveTypes_LeaveTypeId] FOREIGN KEY ([LeaveTypeId]) REFERENCES [hrm].[LeaveTypes] ([Id]) ON DELETE CASCADE
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.LeaveRequests', N'U') IS NULL
CREATE TABLE [hrm].[LeaveRequests] (
    [Id] int NOT NULL IDENTITY,
    [RequestNumber] nvarchar(30) NOT NULL,
    [EmployeeId] int NOT NULL,
    [LeaveTypeId] int NOT NULL,
    [FromDate] date NOT NULL,
    [ToDate] date NOT NULL,
    [HalfDay] bit NOT NULL,
    [Days] decimal(9,2) NOT NULL,
    [Reason] nvarchar(500) NULL,
    [Status] nvarchar(20) NOT NULL,
    [AttachmentUuid] uniqueidentifier NULL,
    [ApprovalUuid] uniqueidentifier NULL,
    [DecidedBy] int NULL,
    [DecidedByName] nvarchar(200) NULL,
    [DecidedAt] datetime2 NULL,
    [DecisionNote] nvarchar(500) NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_LeaveRequests] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_LeaveRequest_Dates] CHECK ([ToDate] >= [FromDate]),
    CONSTRAINT [CK_LeaveRequest_Status] CHECK ([Status] IN ('Pending', 'Approved', 'Rejected', 'Cancelled')),
    CONSTRAINT [FK_LeaveRequests_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [hrm].[Employees] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_LeaveRequests_LeaveTypes_LeaveTypeId] FOREIGN KEY ([LeaveTypeId]) REFERENCES [hrm].[LeaveTypes] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.IssuedLetters', N'U') IS NULL
CREATE TABLE [hrm].[IssuedLetters] (
    [Id] int NOT NULL IDENTITY,
    [LetterNumber] nvarchar(30) NOT NULL,
    [EmployeeId] int NOT NULL,
    [TemplateId] int NULL,
    [Kind] nvarchar(30) NOT NULL,
    [AttachmentUuid] uniqueidentifier NOT NULL,
    [IssuedAt] datetime2 NOT NULL,
    [IssuedBy] int NULL,
    [IssuedByName] nvarchar(200) NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    CONSTRAINT [PK_IssuedLetters] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_IssuedLetter_Kind] CHECK ([Kind] IN ('OfferLetter', 'ExperienceCertificate', 'SalaryCertificate', 'Custom')),
    CONSTRAINT [FK_IssuedLetters_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [hrm].[Employees] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_IssuedLetters_LetterTemplates_TemplateId] FOREIGN KEY ([TemplateId]) REFERENCES [hrm].[LetterTemplates] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.ExpenseClaims', N'U') IS NULL
CREATE TABLE [hrm].[ExpenseClaims] (
    [Id] int NOT NULL IDENTITY,
    [ClaimNumber] nvarchar(30) NOT NULL,
    [EmployeeId] int NOT NULL,
    [ClaimDate] date NOT NULL,
    [Status] nvarchar(20) NOT NULL,
    [PaymentMode] nvarchar(20) NOT NULL,
    [SettlementMethod] nvarchar(20) NOT NULL,
    [CurrencyCode] nvarchar(3) NOT NULL,
    [TotalAmount] decimal(18,2) NOT NULL,
    [TotalTax] decimal(18,2) NOT NULL,
    [Description] nvarchar(500) NULL,
    [ApprovalUuid] uniqueidentifier NULL,
    [SubmittedAt] datetime2 NULL,
    [DecidedBy] int NULL,
    [DecidedByName] nvarchar(200) NULL,
    [DecidedAt] datetime2 NULL,
    [DecisionNote] nvarchar(500) NULL,
    [PaidOn] date NULL,
    [PaidInRunId] int NULL,
    [PaidFromAccount] nvarchar(10) NOT NULL,
    [PaymentReference] nvarchar(100) NULL,
    [PostingState] nvarchar(10) NOT NULL,
    [PaymentPostingState] nvarchar(10) NOT NULL,
    [PostingNote] nvarchar(1000) NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_ExpenseClaims] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_ExpenseClaim_Mode] CHECK ([PaymentMode] IN ('OwnAccount', 'CompanyAccount')),
    CONSTRAINT [CK_ExpenseClaim_PaidFrom] CHECK ([PaidFromAccount] IN ('Bank', 'Cash')),
    CONSTRAINT [CK_ExpenseClaim_PayPosting] CHECK ([PaymentPostingState] IN ('None', 'Pending', 'Posted', 'Failed', 'NoTarget')),
    CONSTRAINT [CK_ExpenseClaim_Posting] CHECK ([PostingState] IN ('None', 'Pending', 'Posted', 'Failed', 'NoTarget')),
    CONSTRAINT [CK_ExpenseClaim_Settlement] CHECK ([SettlementMethod] IN ('WithPayroll', 'SeparatePayment')),
    CONSTRAINT [CK_ExpenseClaim_Status] CHECK ([Status] IN ('Draft', 'Submitted', 'Approved', 'Paid', 'Rejected')),
    CONSTRAINT [FK_ExpenseClaims_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [hrm].[Employees] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_ExpenseClaims_PayrollRuns_PaidInRunId] FOREIGN KEY ([PaidInRunId]) REFERENCES [hrm].[PayrollRuns] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.OvertimeRecords', N'U') IS NULL
CREATE TABLE [hrm].[OvertimeRecords] (
    [Id] int NOT NULL IDENTITY,
    [EmployeeId] int NOT NULL,
    [Date] date NOT NULL,
    [Minutes] int NOT NULL,
    [Multiplier] decimal(9,4) NOT NULL,
    [Status] nvarchar(20) NOT NULL,
    [Reason] nvarchar(500) NULL,
    [DecidedBy] int NULL,
    [DecidedByName] nvarchar(200) NULL,
    [DecidedAt] datetime2 NULL,
    [DecisionNote] nvarchar(500) NULL,
    [PaidInRunId] int NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_OvertimeRecords] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Overtime_Minutes] CHECK ([Minutes] > 0),
    CONSTRAINT [CK_Overtime_Status] CHECK ([Status] IN ('Pending', 'Approved', 'Rejected')),
    CONSTRAINT [FK_OvertimeRecords_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [hrm].[Employees] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_OvertimeRecords_PayrollRuns_PaidInRunId] FOREIGN KEY ([PaidInRunId]) REFERENCES [hrm].[PayrollRuns] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.PayrollPostings', N'U') IS NULL
CREATE TABLE [hrm].[PayrollPostings] (
    [Id] int NOT NULL IDENTITY,
    [RunId] int NOT NULL,
    [Kind] nvarchar(20) NOT NULL,
    [IdempotencyKey] nvarchar(200) NOT NULL,
    [Status] nvarchar(20) NOT NULL,
    [PostingDate] date NULL,
    [TotalDebit] decimal(18,2) NOT NULL,
    [TotalCredit] decimal(18,2) NOT NULL,
    [TargetResultsJson] nvarchar(max) NULL,
    [LastError] nvarchar(2000) NULL,
    [LastAttemptAt] datetime2 NULL,
    [Attempts] int NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    CONSTRAINT [PK_PayrollPostings] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_PayrollPosting_Kind] CHECK ([Kind] IN ('PayrollRun', 'PayrollPayment', 'Void')),
    CONSTRAINT [CK_PayrollPosting_Status] CHECK ([Status] IN ('Pending', 'Posted', 'Failed', 'Held', 'NoTarget', 'Reversed')),
    CONSTRAINT [FK_PayrollPostings_PayrollRuns_RunId] FOREIGN KEY ([RunId]) REFERENCES [hrm].[PayrollRuns] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.PayrollRunLines', N'U') IS NULL
CREATE TABLE [hrm].[PayrollRunLines] (
    [Id] int NOT NULL IDENTITY,
    [RunId] int NOT NULL,
    [EmployeeId] int NOT NULL,
    [WorkingDays] decimal(9,2) NOT NULL,
    [PaidDays] decimal(9,2) NOT NULL,
    [WorkedDays] decimal(9,2) NOT NULL,
    [Gross] decimal(18,2) NOT NULL,
    [TotalDeductions] decimal(18,2) NOT NULL,
    [Net] decimal(18,2) NOT NULL,
    [TotalEmployerCost] decimal(18,2) NOT NULL,
    [IsBlocked] bit NOT NULL,
    [BlockReason] nvarchar(500) NULL,
    [DepartmentName] nvarchar(100) NULL,
    [DesignationName] nvarchar(100) NULL,
    [CostCenterCode] nvarchar(20) NULL,
    [PaymentMethod] nvarchar(10) NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    CONSTRAINT [PK_PayrollRunLines] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_PayrollLine_Method] CHECK ([PaymentMethod] IN ('Bank', 'Cash', 'Cheque')),
    CONSTRAINT [FK_PayrollRunLines_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [hrm].[Employees] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PayrollRunLines_PayrollRuns_RunId] FOREIGN KEY ([RunId]) REFERENCES [hrm].[PayrollRuns] ([Id]) ON DELETE CASCADE
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.Goals', N'U') IS NULL
CREATE TABLE [hrm].[Goals] (
    [Id] int NOT NULL IDENTITY,
    [EmployeeId] int NOT NULL,
    [ReviewCycleId] int NOT NULL,
    [Title] nvarchar(200) NOT NULL,
    [Description] nvarchar(2000) NULL,
    [Weight] decimal(9,4) NOT NULL,
    [SelfRating] decimal(9,4) NULL,
    [ManagerRating] decimal(9,4) NULL,
    [Status] nvarchar(20) NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_Goals] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Goal_Status] CHECK ([Status] IN ('Open', 'Achieved', 'Missed', 'Dropped')),
    CONSTRAINT [CK_Goal_Weight] CHECK ([Weight] >= 0),
    CONSTRAINT [FK_Goals_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [hrm].[Employees] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_Goals_ReviewCycles_ReviewCycleId] FOREIGN KEY ([ReviewCycleId]) REFERENCES [hrm].[ReviewCycles] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.Reviews', N'U') IS NULL
CREATE TABLE [hrm].[Reviews] (
    [Id] int NOT NULL IDENTITY,
    [EmployeeId] int NOT NULL,
    [ReviewCycleId] int NOT NULL,
    [ReviewerEmployeeId] int NULL,
    [OverallRating] decimal(9,4) NULL,
    [Status] nvarchar(20) NOT NULL,
    [ManagerComments] nvarchar(4000) NULL,
    [EmployeeComments] nvarchar(4000) NULL,
    [SubmittedAt] datetime2 NULL,
    [AcknowledgedAt] datetime2 NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_Reviews] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Review_Status] CHECK ([Status] IN ('Draft', 'Submitted', 'Acknowledged')),
    CONSTRAINT [FK_Reviews_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [hrm].[Employees] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_Reviews_Employees_ReviewerEmployeeId] FOREIGN KEY ([ReviewerEmployeeId]) REFERENCES [hrm].[Employees] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_Reviews_ReviewCycles_ReviewCycleId] FOREIGN KEY ([ReviewCycleId]) REFERENCES [hrm].[ReviewCycles] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.SalaryAssignments', N'U') IS NULL
CREATE TABLE [hrm].[SalaryAssignments] (
    [Id] int NOT NULL IDENTITY,
    [EmployeeId] int NOT NULL,
    [StructureId] int NOT NULL,
    [EffectiveFrom] date NOT NULL,
    [EffectiveTo] date NULL,
    [ApprovalStatus] nvarchar(20) NOT NULL,
    [ChangeReason] nvarchar(500) NULL,
    [ApprovalUuid] uniqueidentifier NULL,
    [ApprovedBy] int NULL,
    [ApprovedByName] nvarchar(200) NULL,
    [ApprovedAt] datetime2 NULL,
    [IncrementProposalId] int NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_SalaryAssignments] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_SalaryAssignment_Dates] CHECK ([EffectiveTo] IS NULL OR [EffectiveTo] >= [EffectiveFrom]),
    CONSTRAINT [CK_SalaryAssignment_Status] CHECK ([ApprovalStatus] IN ('Draft', 'PendingApproval', 'Approved', 'Rejected', 'Superseded')),
    CONSTRAINT [FK_SalaryAssignments_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [hrm].[Employees] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_SalaryAssignments_SalaryStructures_StructureId] FOREIGN KEY ([StructureId]) REFERENCES [hrm].[SalaryStructures] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.SalaryStructureLines', N'U') IS NULL
CREATE TABLE [hrm].[SalaryStructureLines] (
    [Id] int NOT NULL IDENTITY,
    [StructureId] int NOT NULL,
    [ComponentId] int NOT NULL,
    [Sequence] int NOT NULL,
    [FormulaOverride] nvarchar(1000) NULL,
    [ValueOverride] decimal(18,2) NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    CONSTRAINT [PK_SalaryStructureLines] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_SalaryStructureLines_SalaryComponents_ComponentId] FOREIGN KEY ([ComponentId]) REFERENCES [hrm].[SalaryComponents] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_SalaryStructureLines_SalaryStructures_StructureId] FOREIGN KEY ([StructureId]) REFERENCES [hrm].[SalaryStructures] ([Id]) ON DELETE CASCADE
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.AttendanceDays', N'U') IS NULL
CREATE TABLE [hrm].[AttendanceDays] (
    [Id] int NOT NULL IDENTITY,
    [EmployeeId] int NOT NULL,
    [Date] date NOT NULL,
    [ShiftId] int NULL,
    [FirstInUtc] datetime2 NULL,
    [LastOutUtc] datetime2 NULL,
    [WorkedMinutes] int NOT NULL,
    [LateMinutes] int NOT NULL,
    [EarlyMinutes] int NOT NULL,
    [OvertimeMinutes] int NOT NULL,
    [Status] nvarchar(20) NOT NULL,
    [IsManualOverride] bit NOT NULL,
    [OverrideNote] nvarchar(500) NULL,
    [ComputedAt] datetime2 NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    CONSTRAINT [PK_AttendanceDays] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_AttendanceDay_Status] CHECK ([Status] IN ('Present', 'Late', 'HalfDay', 'Absent', 'OnLeave', 'Holiday', 'WeeklyOff')),
    CONSTRAINT [FK_AttendanceDays_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [hrm].[Employees] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_AttendanceDays_Shifts_ShiftId] FOREIGN KEY ([ShiftId]) REFERENCES [hrm].[Shifts] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.TaxSlabs', N'U') IS NULL
CREATE TABLE [hrm].[TaxSlabs] (
    [Id] int NOT NULL IDENTITY,
    [TableId] int NOT NULL,
    [FromAmount] decimal(18,2) NOT NULL,
    [ToAmount] decimal(18,2) NULL,
    [FixedAmount] decimal(18,2) NOT NULL,
    [RatePercent] decimal(9,4) NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    CONSTRAINT [PK_TaxSlabs] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_TaxSlab_Range] CHECK ([ToAmount] IS NULL OR [ToAmount] > [FromAmount]),
    CONSTRAINT [FK_TaxSlabs_TaxSlabTables_TableId] FOREIGN KEY ([TableId]) REFERENCES [hrm].[TaxSlabTables] ([Id]) ON DELETE CASCADE
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.Departments', N'U') IS NULL
CREATE TABLE [hrm].[Departments] (
    [Id] int NOT NULL IDENTITY,
    [Code] nvarchar(20) NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [ParentId] int NULL,
    [HeadEmployeeId] int NULL,
    [BranchId] int NULL,
    [CostCenterId] int NULL,
    [CostCenterCode] nvarchar(20) NULL,
    [IsActive] bit NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_Departments] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Departments_NotOwnParent] CHECK ([ParentId] IS NULL OR [ParentId] <> [Id]),
    CONSTRAINT [FK_Departments_Branches_BranchId] FOREIGN KEY ([BranchId]) REFERENCES [hrm].[Branches] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_Departments_CostCenters_CostCenterId] FOREIGN KEY ([CostCenterId]) REFERENCES [hrm].[CostCenters] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_Departments_Departments_ParentId] FOREIGN KEY ([ParentId]) REFERENCES [hrm].[Departments] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_Departments_Employees_HeadEmployeeId] FOREIGN KEY ([HeadEmployeeId]) REFERENCES [hrm].[Employees] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.HrmSettings', N'U') IS NULL
CREATE TABLE [hrm].[HrmSettings] (
    [Id] int NOT NULL IDENTITY,
    [PayrollCostCenterId] int NULL,
    [PayrollCostCenterMode] nvarchar(20) NOT NULL,
    [PostDatePolicy] nvarchar(30) NOT NULL,
    [EmployeeNumberPrefix] nvarchar(10) NOT NULL,
    [NextEmployeeNumber] int NOT NULL,
    [CountryCode] nvarchar(3) NOT NULL,
    [WorkingDaysDivisor] int NULL,
    [DefaultExpenseSettlement] nvarchar(20) NOT NULL,
    [DefaultReceiptThreshold] decimal(18,2) NULL,
    [SeedVersion] int NOT NULL,
    [EnablementCompletedAt] datetime2 NULL,
    [EnablementCompletedBy] int NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_HrmSettings] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_HrmSettings_Mode] CHECK ([PayrollCostCenterMode] IN ('CENTRAL', 'BY_DEPARTMENT')),
    CONSTRAINT [CK_HrmSettings_PostDate] CHECK ([PostDatePolicy] IN ('PayDate', 'PeriodEnd', 'PayDateWithFallback')),
    CONSTRAINT [CK_HrmSettings_Settlement] CHECK ([DefaultExpenseSettlement] IN ('WithPayroll', 'SeparatePayment')),
    CONSTRAINT [FK_HrmSettings_CostCenters_PayrollCostCenterId] FOREIGN KEY ([PayrollCostCenterId]) REFERENCES [hrm].[CostCenters] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.FinalSettlementLines', N'U') IS NULL
CREATE TABLE [hrm].[FinalSettlementLines] (
    [Id] int NOT NULL IDENTITY,
    [SettlementId] int NOT NULL,
    [Code] nvarchar(20) NOT NULL,
    [Description] nvarchar(200) NOT NULL,
    [Type] nvarchar(30) NOT NULL,
    [Amount] decimal(18,2) NOT NULL,
    [Basis] decimal(18,2) NULL,
    [PostingRole] nvarchar(50) NULL,
    [Sequence] int NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    CONSTRAINT [PK_FinalSettlementLines] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_SettlementLine_Type] CHECK ([Type] IN ('Earning', 'Deduction', 'EmployerContribution', 'Result')),
    CONSTRAINT [FK_FinalSettlementLines_FinalSettlements_SettlementId] FOREIGN KEY ([SettlementId]) REFERENCES [hrm].[FinalSettlements] ([Id]) ON DELETE CASCADE
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.LoanInstallments', N'U') IS NULL
CREATE TABLE [hrm].[LoanInstallments] (
    [Id] int NOT NULL IDENTITY,
    [LoanId] int NOT NULL,
    [Sequence] int NOT NULL,
    [DueMonth] date NOT NULL,
    [Amount] decimal(18,2) NOT NULL,
    [Status] nvarchar(20) NOT NULL,
    [RunId] int NULL,
    [Note] nvarchar(500) NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    CONSTRAINT [PK_LoanInstallments] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_LoanInstallment_Status] CHECK ([Status] IN ('Pending', 'Deducted', 'Waived')),
    CONSTRAINT [FK_LoanInstallments_Loans_LoanId] FOREIGN KEY ([LoanId]) REFERENCES [hrm].[Loans] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_LoanInstallments_PayrollRuns_RunId] FOREIGN KEY ([RunId]) REFERENCES [hrm].[PayrollRuns] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.ExpenseLines', N'U') IS NULL
CREATE TABLE [hrm].[ExpenseLines] (
    [Id] int NOT NULL IDENTITY,
    [ClaimId] int NOT NULL,
    [CategoryId] int NOT NULL,
    [ExpenseDate] date NOT NULL,
    [Description] nvarchar(500) NULL,
    [Amount] decimal(18,2) NOT NULL,
    [Quantity] decimal(9,2) NULL,
    [TaxCode] nvarchar(20) NULL,
    [TaxAmount] decimal(18,2) NOT NULL,
    [CostCenterCode] nvarchar(20) NULL,
    [AttachmentUuid] uniqueidentifier NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    CONSTRAINT [PK_ExpenseLines] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_ExpenseLine_Amount] CHECK ([Amount] >= 0 AND [TaxAmount] >= 0),
    CONSTRAINT [FK_ExpenseLines_ExpenseCategories_CategoryId] FOREIGN KEY ([CategoryId]) REFERENCES [hrm].[ExpenseCategories] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_ExpenseLines_ExpenseClaims_ClaimId] FOREIGN KEY ([ClaimId]) REFERENCES [hrm].[ExpenseClaims] ([Id]) ON DELETE CASCADE
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.PayrollAdjustments', N'U') IS NULL
CREATE TABLE [hrm].[PayrollAdjustments] (
    [Id] int NOT NULL IDENTITY,
    [EmployeeId] int NOT NULL,
    [RunId] int NULL,
    [ComponentCode] nvarchar(20) NOT NULL,
    [Amount] decimal(18,2) NOT NULL,
    [Reason] nvarchar(500) NOT NULL,
    [AppliedInRunId] int NULL,
    [ExpenseClaimId] int NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_PayrollAdjustments] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_PayrollAdjustments_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [hrm].[Employees] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PayrollAdjustments_ExpenseClaims_ExpenseClaimId] FOREIGN KEY ([ExpenseClaimId]) REFERENCES [hrm].[ExpenseClaims] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PayrollAdjustments_PayrollRuns_AppliedInRunId] FOREIGN KEY ([AppliedInRunId]) REFERENCES [hrm].[PayrollRuns] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PayrollAdjustments_PayrollRuns_RunId] FOREIGN KEY ([RunId]) REFERENCES [hrm].[PayrollRuns] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.PayrollRunLineComponents', N'U') IS NULL
CREATE TABLE [hrm].[PayrollRunLineComponents] (
    [Id] int NOT NULL IDENTITY,
    [LineId] int NOT NULL,
    [Code] nvarchar(20) NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [Type] nvarchar(30) NOT NULL,
    [Amount] decimal(18,2) NOT NULL,
    [Basis] decimal(18,2) NULL,
    [PostingRole] nvarchar(50) NULL,
    [ExpenseRole] nvarchar(50) NULL,
    [IsTaxable] bit NOT NULL,
    [InGross] bit NOT NULL,
    [Sequence] int NOT NULL,
    [CostCenterCode] nvarchar(20) NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    CONSTRAINT [PK_PayrollRunLineComponents] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_PayrollComponent_Type] CHECK ([Type] IN ('Earning', 'Deduction', 'EmployerContribution', 'Result')),
    CONSTRAINT [FK_PayrollRunLineComponents_PayrollRunLines_LineId] FOREIGN KEY ([LineId]) REFERENCES [hrm].[PayrollRunLines] ([Id]) ON DELETE CASCADE
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.Payslips', N'U') IS NULL
CREATE TABLE [hrm].[Payslips] (
    [Id] int NOT NULL IDENTITY,
    [RunLineId] int NOT NULL,
    [AttachmentUuid] uniqueidentifier NOT NULL,
    [GeneratedAt] datetime2 NOT NULL,
    [FileName] nvarchar(200) NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    CONSTRAINT [PK_Payslips] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Payslips_PayrollRunLines_RunLineId] FOREIGN KEY ([RunLineId]) REFERENCES [hrm].[PayrollRunLines] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.IncrementProposals', N'U') IS NULL
CREATE TABLE [hrm].[IncrementProposals] (
    [Id] int NOT NULL IDENTITY,
    [EmployeeId] int NOT NULL,
    [NewBasic] decimal(18,2) NULL,
    [Percent] decimal(9,4) NULL,
    [EffectiveDate] date NOT NULL,
    [ReviewId] int NULL,
    [Status] nvarchar(20) NOT NULL,
    [Reason] nvarchar(500) NULL,
    [DecidedBy] int NULL,
    [DecidedByName] nvarchar(200) NULL,
    [DecidedAt] datetime2 NULL,
    [AppliedAssignmentId] int NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_IncrementProposals] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Increment_Status] CHECK ([Status] IN ('Draft', 'Approved', 'Applied', 'Rejected')),
    CONSTRAINT [CK_Increment_Value] CHECK ([NewBasic] IS NOT NULL OR [Percent] IS NOT NULL),
    CONSTRAINT [FK_IncrementProposals_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [hrm].[Employees] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_IncrementProposals_Reviews_ReviewId] FOREIGN KEY ([ReviewId]) REFERENCES [hrm].[Reviews] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_IncrementProposals_SalaryAssignments_AppliedAssignmentId] FOREIGN KEY ([AppliedAssignmentId]) REFERENCES [hrm].[SalaryAssignments] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.SalaryAssignmentLines', N'U') IS NULL
CREATE TABLE [hrm].[SalaryAssignmentLines] (
    [Id] int NOT NULL IDENTITY,
    [AssignmentId] int NOT NULL,
    [ComponentId] int NOT NULL,
    [Amount] decimal(18,2) NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    CONSTRAINT [PK_SalaryAssignmentLines] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_SalaryAssignmentLines_SalaryAssignments_AssignmentId] FOREIGN KEY ([AssignmentId]) REFERENCES [hrm].[SalaryAssignments] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_SalaryAssignmentLines_SalaryComponents_ComponentId] FOREIGN KEY ([ComponentId]) REFERENCES [hrm].[SalaryComponents] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.EmploymentRecords', N'U') IS NULL
CREATE TABLE [hrm].[EmploymentRecords] (
    [Id] int NOT NULL IDENTITY,
    [EmployeeId] int NOT NULL,
    [EffectiveFrom] date NOT NULL,
    [EffectiveTo] date NULL,
    [EmploymentType] nvarchar(20) NOT NULL,
    [DepartmentId] int NOT NULL,
    [DesignationId] int NOT NULL,
    [BranchId] int NOT NULL,
    [GradeId] int NULL,
    [ManagerEmployeeId] int NULL,
    [ShiftId] int NULL,
    [ProbationEndDate] date NULL,
    [ConfirmationDate] date NULL,
    [ContractEndDate] date NULL,
    [NoticePeriodDays] int NOT NULL,
    [CostCenterId] int NULL,
    [CostCenterCode] nvarchar(20) NULL,
    [PayrollGroup] nvarchar(30) NULL,
    [ChangeReason] nvarchar(20) NOT NULL,
    [Notes] nvarchar(500) NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_EmploymentRecords] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Employment_Dates] CHECK ([EffectiveTo] IS NULL OR [EffectiveTo] >= [EffectiveFrom]),
    CONSTRAINT [CK_Employment_Notice] CHECK ([NoticePeriodDays] >= 0),
    CONSTRAINT [CK_Employment_Reason] CHECK ([ChangeReason] IN ('Hire', 'Transfer', 'Promotion', 'Confirmation', 'Demotion', 'ContractRenewal', 'Correction', 'Other')),
    CONSTRAINT [CK_Employment_Type] CHECK ([EmploymentType] IN ('Permanent', 'Contract', 'Probation', 'Intern', 'DailyWage')),
    CONSTRAINT [FK_EmploymentRecords_Branches_BranchId] FOREIGN KEY ([BranchId]) REFERENCES [hrm].[Branches] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_EmploymentRecords_CostCenters_CostCenterId] FOREIGN KEY ([CostCenterId]) REFERENCES [hrm].[CostCenters] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_EmploymentRecords_Departments_DepartmentId] FOREIGN KEY ([DepartmentId]) REFERENCES [hrm].[Departments] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_EmploymentRecords_Designations_DesignationId] FOREIGN KEY ([DesignationId]) REFERENCES [hrm].[Designations] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_EmploymentRecords_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [hrm].[Employees] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_EmploymentRecords_Employees_ManagerEmployeeId] FOREIGN KEY ([ManagerEmployeeId]) REFERENCES [hrm].[Employees] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_EmploymentRecords_Grades_GradeId] FOREIGN KEY ([GradeId]) REFERENCES [hrm].[Grades] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_EmploymentRecords_Shifts_ShiftId] FOREIGN KEY ([ShiftId]) REFERENCES [hrm].[Shifts] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.HrUserScopes', N'U') IS NULL
CREATE TABLE [hrm].[HrUserScopes] (
    [Id] int NOT NULL IDENTITY,
    [UserId] int NOT NULL,
    [ScopeType] nvarchar(20) NOT NULL,
    [BranchId] int NULL,
    [DepartmentId] int NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    CONSTRAINT [PK_HrUserScopes] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_HrUserScopes_Type] CHECK (([ScopeType] = 'Branch' AND [BranchId] IS NOT NULL AND [DepartmentId] IS NULL) OR ([ScopeType] = 'Department' AND [DepartmentId] IS NOT NULL AND [BranchId] IS NULL)),
    CONSTRAINT [FK_HrUserScopes_Branches_BranchId] FOREIGN KEY ([BranchId]) REFERENCES [hrm].[Branches] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_HrUserScopes_Departments_DepartmentId] FOREIGN KEY ([DepartmentId]) REFERENCES [hrm].[Departments] ([Id]) ON DELETE CASCADE
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.Vacancies', N'U') IS NULL
CREATE TABLE [hrm].[Vacancies] (
    [Id] int NOT NULL IDENTITY,
    [VacancyNumber] nvarchar(30) NOT NULL,
    [Title] nvarchar(200) NOT NULL,
    [DesignationId] int NOT NULL,
    [DepartmentId] int NOT NULL,
    [BranchId] int NULL,
    [Positions] int NOT NULL,
    [Status] nvarchar(20) NOT NULL,
    [Description] nvarchar(4000) NULL,
    [ClosingDate] date NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_Vacancies] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Vacancy_Positions] CHECK ([Positions] > 0),
    CONSTRAINT [CK_Vacancy_Status] CHECK ([Status] IN ('Open', 'OnHold', 'Closed')),
    CONSTRAINT [FK_Vacancies_Branches_BranchId] FOREIGN KEY ([BranchId]) REFERENCES [hrm].[Branches] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_Vacancies_Departments_DepartmentId] FOREIGN KEY ([DepartmentId]) REFERENCES [hrm].[Departments] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_Vacancies_Designations_DesignationId] FOREIGN KEY ([DesignationId]) REFERENCES [hrm].[Designations] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.EmploymentCostSplits', N'U') IS NULL
CREATE TABLE [hrm].[EmploymentCostSplits] (
    [Id] int NOT NULL IDENTITY,
    [EmploymentRecordId] int NOT NULL,
    [CostCenterId] int NOT NULL,
    [Percent] decimal(9,4) NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    CONSTRAINT [PK_EmploymentCostSplits] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_CostSplit_Percent] CHECK ([Percent] > 0 AND [Percent] <= 100),
    CONSTRAINT [FK_EmploymentCostSplits_CostCenters_CostCenterId] FOREIGN KEY ([CostCenterId]) REFERENCES [hrm].[CostCenters] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_EmploymentCostSplits_EmploymentRecords_EmploymentRecordId] FOREIGN KEY ([EmploymentRecordId]) REFERENCES [hrm].[EmploymentRecords] ([Id]) ON DELETE CASCADE
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.JobApplications', N'U') IS NULL
CREATE TABLE [hrm].[JobApplications] (
    [Id] int NOT NULL IDENTITY,
    [CandidateId] int NOT NULL,
    [VacancyId] int NOT NULL,
    [Stage] nvarchar(20) NOT NULL,
    [Rating] int NULL,
    [RejectionReason] nvarchar(500) NULL,
    [HiredEmployeeId] int NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_JobApplications] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_JobApplication_Stage] CHECK ([Stage] IN ('New', 'Screening', 'Interview', 'Offer', 'Hired', 'Rejected')),
    CONSTRAINT [FK_JobApplications_Candidates_CandidateId] FOREIGN KEY ([CandidateId]) REFERENCES [hrm].[Candidates] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_JobApplications_Employees_HiredEmployeeId] FOREIGN KEY ([HiredEmployeeId]) REFERENCES [hrm].[Employees] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_JobApplications_Vacancies_VacancyId] FOREIGN KEY ([VacancyId]) REFERENCES [hrm].[Vacancies] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.Interviews', N'U') IS NULL
CREATE TABLE [hrm].[Interviews] (
    [Id] int NOT NULL IDENTITY,
    [ApplicationId] int NOT NULL,
    [InterviewerEmployeeId] int NULL,
    [ScheduledAt] datetime2 NOT NULL,
    [Feedback] nvarchar(4000) NULL,
    [Score] decimal(9,4) NULL,
    [Result] nvarchar(20) NOT NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_Interviews] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Interview_Result] CHECK ([Result] IN ('Pending', 'Passed', 'Failed', 'NoShow')),
    CONSTRAINT [FK_Interviews_Employees_InterviewerEmployeeId] FOREIGN KEY ([InterviewerEmployeeId]) REFERENCES [hrm].[Employees] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_Interviews_JobApplications_ApplicationId] FOREIGN KEY ([ApplicationId]) REFERENCES [hrm].[JobApplications] ([Id]) ON DELETE CASCADE
);");

            migrationBuilder.Sql(@"IF OBJECT_ID(N'hrm.JobOffers', N'U') IS NULL
CREATE TABLE [hrm].[JobOffers] (
    [Id] int NOT NULL IDENTITY,
    [ApplicationId] int NOT NULL,
    [OfferedSalary] decimal(18,2) NOT NULL,
    [ProposedJoinDate] date NULL,
    [Status] nvarchar(20) NOT NULL,
    [LetterAttachmentUuid] uniqueidentifier NULL,
    [Notes] nvarchar(2000) NULL,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_JobOffers] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_JobOffer_Status] CHECK ([Status] IN ('Draft', 'Sent', 'Accepted', 'Declined', 'Withdrawn')),
    CONSTRAINT [FK_JobOffers_JobApplications_ApplicationId] FOREIGN KEY ([ApplicationId]) REFERENCES [hrm].[JobApplications] ([Id]) ON DELETE CASCADE
);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AttendanceDay_OrgDate' AND object_id = OBJECT_ID(N'hrm.AttendanceDays'))
CREATE INDEX [IX_AttendanceDay_OrgDate] ON [hrm].[AttendanceDays] ([OrganizationId], [Date]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AttendanceDays_EmployeeId' AND object_id = OBJECT_ID(N'hrm.AttendanceDays'))
CREATE INDEX [IX_AttendanceDays_EmployeeId] ON [hrm].[AttendanceDays] ([EmployeeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AttendanceDays_ShiftId' AND object_id = OBJECT_ID(N'hrm.AttendanceDays'))
CREATE INDEX [IX_AttendanceDays_ShiftId] ON [hrm].[AttendanceDays] ([ShiftId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AttendanceDays_Uuid' AND object_id = OBJECT_ID(N'hrm.AttendanceDays'))
CREATE UNIQUE INDEX [IX_AttendanceDays_Uuid] ON [hrm].[AttendanceDays] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_AttendanceDay_EmployeeDate' AND object_id = OBJECT_ID(N'hrm.AttendanceDays'))
CREATE UNIQUE INDEX [UQ_AttendanceDay_EmployeeDate] ON [hrm].[AttendanceDays] ([OrganizationId], [EmployeeId], [Date]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AttendanceEvent_EmployeeTime' AND object_id = OBJECT_ID(N'hrm.AttendanceEvents'))
CREATE INDEX [IX_AttendanceEvent_EmployeeTime] ON [hrm].[AttendanceEvents] ([OrganizationId], [EmployeeId], [EventTimeUtc]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AttendanceEvents_EmployeeId' AND object_id = OBJECT_ID(N'hrm.AttendanceEvents'))
CREATE INDEX [IX_AttendanceEvents_EmployeeId] ON [hrm].[AttendanceEvents] ([EmployeeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AttendanceEvents_Uuid' AND object_id = OBJECT_ID(N'hrm.AttendanceEvents'))
CREATE UNIQUE INDEX [IX_AttendanceEvents_Uuid] ON [hrm].[AttendanceEvents] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_AttendanceEvent_External' AND object_id = OBJECT_ID(N'hrm.AttendanceEvents'))
CREATE UNIQUE INDEX [UQ_AttendanceEvent_External] ON [hrm].[AttendanceEvents] ([OrganizationId], [Source], [ExternalEventId]) WHERE [ExternalEventId] IS NOT NULL;");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Branches_Uuid' AND object_id = OBJECT_ID(N'hrm.Branches'))
CREATE UNIQUE INDEX [IX_Branches_Uuid] ON [hrm].[Branches] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_Branch_OrgCode' AND object_id = OBJECT_ID(N'hrm.Branches'))
CREATE UNIQUE INDEX [UQ_Branch_OrgCode] ON [hrm].[Branches] ([OrganizationId], [Code]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Candidate_Email' AND object_id = OBJECT_ID(N'hrm.Candidates'))
CREATE INDEX [IX_Candidate_Email] ON [hrm].[Candidates] ([OrganizationId], [Email]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Candidate_Phone' AND object_id = OBJECT_ID(N'hrm.Candidates'))
CREATE INDEX [IX_Candidate_Phone] ON [hrm].[Candidates] ([OrganizationId], [Phone]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Candidates_Uuid' AND object_id = OBJECT_ID(N'hrm.Candidates'))
CREATE UNIQUE INDEX [IX_Candidates_Uuid] ON [hrm].[Candidates] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CostCenters_BranchId' AND object_id = OBJECT_ID(N'hrm.CostCenters'))
CREATE INDEX [IX_CostCenters_BranchId] ON [hrm].[CostCenters] ([BranchId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CostCenters_ParentId' AND object_id = OBJECT_ID(N'hrm.CostCenters'))
CREATE INDEX [IX_CostCenters_ParentId] ON [hrm].[CostCenters] ([ParentId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CostCenters_Uuid' AND object_id = OBJECT_ID(N'hrm.CostCenters'))
CREATE UNIQUE INDEX [IX_CostCenters_Uuid] ON [hrm].[CostCenters] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_CostCenter_OrgCode' AND object_id = OBJECT_ID(N'hrm.CostCenters'))
CREATE UNIQUE INDEX [UQ_CostCenter_OrgCode] ON [hrm].[CostCenters] ([OrganizationId], [Code]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CourseEnrollments_CourseId' AND object_id = OBJECT_ID(N'hrm.CourseEnrollments'))
CREATE INDEX [IX_CourseEnrollments_CourseId] ON [hrm].[CourseEnrollments] ([CourseId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CourseEnrollments_OrganizationId' AND object_id = OBJECT_ID(N'hrm.CourseEnrollments'))
CREATE INDEX [IX_CourseEnrollments_OrganizationId] ON [hrm].[CourseEnrollments] ([OrganizationId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CourseEnrollments_Uuid' AND object_id = OBJECT_ID(N'hrm.CourseEnrollments'))
CREATE UNIQUE INDEX [IX_CourseEnrollments_Uuid] ON [hrm].[CourseEnrollments] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_Enrollment_EmployeeCourseDate' AND object_id = OBJECT_ID(N'hrm.CourseEnrollments'))
CREATE UNIQUE INDEX [UQ_Enrollment_EmployeeCourseDate] ON [hrm].[CourseEnrollments] ([EmployeeId], [CourseId], [EnrolledDate]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Courses_Uuid' AND object_id = OBJECT_ID(N'hrm.Courses'))
CREATE UNIQUE INDEX [IX_Courses_Uuid] ON [hrm].[Courses] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_Course_OrgCode' AND object_id = OBJECT_ID(N'hrm.Courses'))
CREATE UNIQUE INDEX [UQ_Course_OrgCode] ON [hrm].[Courses] ([OrganizationId], [Code]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Departments_BranchId' AND object_id = OBJECT_ID(N'hrm.Departments'))
CREATE INDEX [IX_Departments_BranchId] ON [hrm].[Departments] ([BranchId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Departments_CostCenterId' AND object_id = OBJECT_ID(N'hrm.Departments'))
CREATE INDEX [IX_Departments_CostCenterId] ON [hrm].[Departments] ([CostCenterId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Departments_HeadEmployeeId' AND object_id = OBJECT_ID(N'hrm.Departments'))
CREATE INDEX [IX_Departments_HeadEmployeeId] ON [hrm].[Departments] ([HeadEmployeeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Departments_ParentId' AND object_id = OBJECT_ID(N'hrm.Departments'))
CREATE INDEX [IX_Departments_ParentId] ON [hrm].[Departments] ([ParentId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Departments_Uuid' AND object_id = OBJECT_ID(N'hrm.Departments'))
CREATE UNIQUE INDEX [IX_Departments_Uuid] ON [hrm].[Departments] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_Dept_OrgCode' AND object_id = OBJECT_ID(N'hrm.Departments'))
CREATE UNIQUE INDEX [UQ_Dept_OrgCode] ON [hrm].[Departments] ([OrganizationId], [Code]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Designations_Uuid' AND object_id = OBJECT_ID(N'hrm.Designations'))
CREATE UNIQUE INDEX [IX_Designations_Uuid] ON [hrm].[Designations] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_Designation_OrgCode' AND object_id = OBJECT_ID(N'hrm.Designations'))
CREATE UNIQUE INDEX [UQ_Designation_OrgCode] ON [hrm].[Designations] ([OrganizationId], [Code]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EmployeeBankAccounts_OrganizationId' AND object_id = OBJECT_ID(N'hrm.EmployeeBankAccounts'))
CREATE INDEX [IX_EmployeeBankAccounts_OrganizationId] ON [hrm].[EmployeeBankAccounts] ([OrganizationId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EmployeeBankAccounts_Uuid' AND object_id = OBJECT_ID(N'hrm.EmployeeBankAccounts'))
CREATE UNIQUE INDEX [IX_EmployeeBankAccounts_Uuid] ON [hrm].[EmployeeBankAccounts] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_BankAccount_OnePrimary' AND object_id = OBJECT_ID(N'hrm.EmployeeBankAccounts'))
CREATE UNIQUE INDEX [UQ_BankAccount_OnePrimary] ON [hrm].[EmployeeBankAccounts] ([EmployeeId]) WHERE [IsPrimary] = 1 AND [IsActive] = 1;");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EmployeeDocuments_EmployeeId' AND object_id = OBJECT_ID(N'hrm.EmployeeDocuments'))
CREATE INDEX [IX_EmployeeDocuments_EmployeeId] ON [hrm].[EmployeeDocuments] ([EmployeeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EmployeeDocuments_OrganizationId' AND object_id = OBJECT_ID(N'hrm.EmployeeDocuments'))
CREATE INDEX [IX_EmployeeDocuments_OrganizationId] ON [hrm].[EmployeeDocuments] ([OrganizationId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EmployeeDocuments_Uuid' AND object_id = OBJECT_ID(N'hrm.EmployeeDocuments'))
CREATE UNIQUE INDEX [IX_EmployeeDocuments_Uuid] ON [hrm].[EmployeeDocuments] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Emp_OrgStatus' AND object_id = OBJECT_ID(N'hrm.Employees'))
CREATE INDEX [IX_Emp_OrgStatus] ON [hrm].[Employees] ([OrganizationId], [Status]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Emp_UserId' AND object_id = OBJECT_ID(N'hrm.Employees'))
CREATE UNIQUE INDEX [IX_Emp_UserId] ON [hrm].[Employees] ([OrganizationId], [UserId]) WHERE [UserId] IS NOT NULL AND [IsDeleted] = 0;");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Employees_Uuid' AND object_id = OBJECT_ID(N'hrm.Employees'))
CREATE UNIQUE INDEX [IX_Employees_Uuid] ON [hrm].[Employees] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_Emp_OrgCnicHash' AND object_id = OBJECT_ID(N'hrm.Employees'))
CREATE UNIQUE INDEX [UQ_Emp_OrgCnicHash] ON [hrm].[Employees] ([OrganizationId], [CnicHash]) WHERE [CnicHash] IS NOT NULL AND [IsDeleted] = 0;");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_Emp_OrgNumber' AND object_id = OBJECT_ID(N'hrm.Employees'))
CREATE UNIQUE INDEX [UQ_Emp_OrgNumber] ON [hrm].[Employees] ([OrganizationId], [EmployeeNumber]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EmploymentCostSplits_CostCenterId' AND object_id = OBJECT_ID(N'hrm.EmploymentCostSplits'))
CREATE INDEX [IX_EmploymentCostSplits_CostCenterId] ON [hrm].[EmploymentCostSplits] ([CostCenterId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EmploymentCostSplits_OrganizationId' AND object_id = OBJECT_ID(N'hrm.EmploymentCostSplits'))
CREATE INDEX [IX_EmploymentCostSplits_OrganizationId] ON [hrm].[EmploymentCostSplits] ([OrganizationId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EmploymentCostSplits_Uuid' AND object_id = OBJECT_ID(N'hrm.EmploymentCostSplits'))
CREATE UNIQUE INDEX [IX_EmploymentCostSplits_Uuid] ON [hrm].[EmploymentCostSplits] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_CostSplit_RecordCenter' AND object_id = OBJECT_ID(N'hrm.EmploymentCostSplits'))
CREATE UNIQUE INDEX [UQ_CostSplit_RecordCenter] ON [hrm].[EmploymentCostSplits] ([EmploymentRecordId], [CostCenterId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Emp_Branch' AND object_id = OBJECT_ID(N'hrm.EmploymentRecords'))
CREATE INDEX [IX_Emp_Branch] ON [hrm].[EmploymentRecords] ([OrganizationId], [BranchId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Emp_Department' AND object_id = OBJECT_ID(N'hrm.EmploymentRecords'))
CREATE INDEX [IX_Emp_Department] ON [hrm].[EmploymentRecords] ([OrganizationId], [DepartmentId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Emp_Manager' AND object_id = OBJECT_ID(N'hrm.EmploymentRecords'))
CREATE INDEX [IX_Emp_Manager] ON [hrm].[EmploymentRecords] ([OrganizationId], [ManagerEmployeeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EmploymentRecords_BranchId' AND object_id = OBJECT_ID(N'hrm.EmploymentRecords'))
CREATE INDEX [IX_EmploymentRecords_BranchId] ON [hrm].[EmploymentRecords] ([BranchId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EmploymentRecords_CostCenterId' AND object_id = OBJECT_ID(N'hrm.EmploymentRecords'))
CREATE INDEX [IX_EmploymentRecords_CostCenterId] ON [hrm].[EmploymentRecords] ([CostCenterId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EmploymentRecords_DepartmentId' AND object_id = OBJECT_ID(N'hrm.EmploymentRecords'))
CREATE INDEX [IX_EmploymentRecords_DepartmentId] ON [hrm].[EmploymentRecords] ([DepartmentId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EmploymentRecords_DesignationId' AND object_id = OBJECT_ID(N'hrm.EmploymentRecords'))
CREATE INDEX [IX_EmploymentRecords_DesignationId] ON [hrm].[EmploymentRecords] ([DesignationId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EmploymentRecords_GradeId' AND object_id = OBJECT_ID(N'hrm.EmploymentRecords'))
CREATE INDEX [IX_EmploymentRecords_GradeId] ON [hrm].[EmploymentRecords] ([GradeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EmploymentRecords_ManagerEmployeeId' AND object_id = OBJECT_ID(N'hrm.EmploymentRecords'))
CREATE INDEX [IX_EmploymentRecords_ManagerEmployeeId] ON [hrm].[EmploymentRecords] ([ManagerEmployeeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EmploymentRecords_ShiftId' AND object_id = OBJECT_ID(N'hrm.EmploymentRecords'))
CREATE INDEX [IX_EmploymentRecords_ShiftId] ON [hrm].[EmploymentRecords] ([ShiftId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EmploymentRecords_Uuid' AND object_id = OBJECT_ID(N'hrm.EmploymentRecords'))
CREATE UNIQUE INDEX [IX_EmploymentRecords_Uuid] ON [hrm].[EmploymentRecords] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_Employment_EmployeeFrom' AND object_id = OBJECT_ID(N'hrm.EmploymentRecords'))
CREATE UNIQUE INDEX [UQ_Employment_EmployeeFrom] ON [hrm].[EmploymentRecords] ([EmployeeId], [EffectiveFrom]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_Employment_OneOpen' AND object_id = OBJECT_ID(N'hrm.EmploymentRecords'))
CREATE UNIQUE INDEX [UQ_Employment_OneOpen] ON [hrm].[EmploymentRecords] ([EmployeeId]) WHERE [EffectiveTo] IS NULL;");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExpenseCategories_Uuid' AND object_id = OBJECT_ID(N'hrm.ExpenseCategories'))
CREATE UNIQUE INDEX [IX_ExpenseCategories_Uuid] ON [hrm].[ExpenseCategories] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_EC_OrgCode' AND object_id = OBJECT_ID(N'hrm.ExpenseCategories'))
CREATE UNIQUE INDEX [UQ_EC_OrgCode] ON [hrm].[ExpenseCategories] ([OrganizationId], [Code]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExpenseClaim_EmployeeStatus' AND object_id = OBJECT_ID(N'hrm.ExpenseClaims'))
CREATE INDEX [IX_ExpenseClaim_EmployeeStatus] ON [hrm].[ExpenseClaims] ([OrganizationId], [EmployeeId], [Status]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExpenseClaims_EmployeeId' AND object_id = OBJECT_ID(N'hrm.ExpenseClaims'))
CREATE INDEX [IX_ExpenseClaims_EmployeeId] ON [hrm].[ExpenseClaims] ([EmployeeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExpenseClaims_PaidInRunId' AND object_id = OBJECT_ID(N'hrm.ExpenseClaims'))
CREATE INDEX [IX_ExpenseClaims_PaidInRunId] ON [hrm].[ExpenseClaims] ([PaidInRunId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExpenseClaims_Uuid' AND object_id = OBJECT_ID(N'hrm.ExpenseClaims'))
CREATE UNIQUE INDEX [IX_ExpenseClaims_Uuid] ON [hrm].[ExpenseClaims] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_ExpenseClaim_OrgNumber' AND object_id = OBJECT_ID(N'hrm.ExpenseClaims'))
CREATE UNIQUE INDEX [UQ_ExpenseClaim_OrgNumber] ON [hrm].[ExpenseClaims] ([OrganizationId], [ClaimNumber]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExpenseLines_CategoryId' AND object_id = OBJECT_ID(N'hrm.ExpenseLines'))
CREATE INDEX [IX_ExpenseLines_CategoryId] ON [hrm].[ExpenseLines] ([CategoryId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExpenseLines_ClaimId' AND object_id = OBJECT_ID(N'hrm.ExpenseLines'))
CREATE INDEX [IX_ExpenseLines_ClaimId] ON [hrm].[ExpenseLines] ([ClaimId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExpenseLines_OrganizationId' AND object_id = OBJECT_ID(N'hrm.ExpenseLines'))
CREATE INDEX [IX_ExpenseLines_OrganizationId] ON [hrm].[ExpenseLines] ([OrganizationId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExpenseLines_Uuid' AND object_id = OBJECT_ID(N'hrm.ExpenseLines'))
CREATE UNIQUE INDEX [IX_ExpenseLines_Uuid] ON [hrm].[ExpenseLines] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FinalSettlementLines_OrganizationId' AND object_id = OBJECT_ID(N'hrm.FinalSettlementLines'))
CREATE INDEX [IX_FinalSettlementLines_OrganizationId] ON [hrm].[FinalSettlementLines] ([OrganizationId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FinalSettlementLines_SettlementId' AND object_id = OBJECT_ID(N'hrm.FinalSettlementLines'))
CREATE INDEX [IX_FinalSettlementLines_SettlementId] ON [hrm].[FinalSettlementLines] ([SettlementId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FinalSettlementLines_Uuid' AND object_id = OBJECT_ID(N'hrm.FinalSettlementLines'))
CREATE UNIQUE INDEX [IX_FinalSettlementLines_Uuid] ON [hrm].[FinalSettlementLines] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FinalSettlements_Uuid' AND object_id = OBJECT_ID(N'hrm.FinalSettlements'))
CREATE UNIQUE INDEX [IX_FinalSettlements_Uuid] ON [hrm].[FinalSettlements] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_Settlement_OnePerEmployee' AND object_id = OBJECT_ID(N'hrm.FinalSettlements'))
CREATE UNIQUE INDEX [UQ_Settlement_OnePerEmployee] ON [hrm].[FinalSettlements] ([EmployeeId]) WHERE [Status] <> 'Cancelled';");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_Settlement_OrgNumber' AND object_id = OBJECT_ID(N'hrm.FinalSettlements'))
CREATE UNIQUE INDEX [UQ_Settlement_OrgNumber] ON [hrm].[FinalSettlements] ([OrganizationId], [SettlementNumber]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Goal_EmployeeCycle' AND object_id = OBJECT_ID(N'hrm.Goals'))
CREATE INDEX [IX_Goal_EmployeeCycle] ON [hrm].[Goals] ([OrganizationId], [EmployeeId], [ReviewCycleId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Goals_EmployeeId' AND object_id = OBJECT_ID(N'hrm.Goals'))
CREATE INDEX [IX_Goals_EmployeeId] ON [hrm].[Goals] ([EmployeeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Goals_ReviewCycleId' AND object_id = OBJECT_ID(N'hrm.Goals'))
CREATE INDEX [IX_Goals_ReviewCycleId] ON [hrm].[Goals] ([ReviewCycleId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Goals_Uuid' AND object_id = OBJECT_ID(N'hrm.Goals'))
CREATE UNIQUE INDEX [IX_Goals_Uuid] ON [hrm].[Goals] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Grades_Uuid' AND object_id = OBJECT_ID(N'hrm.Grades'))
CREATE UNIQUE INDEX [IX_Grades_Uuid] ON [hrm].[Grades] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_Grade_OrgCode' AND object_id = OBJECT_ID(N'hrm.Grades'))
CREATE UNIQUE INDEX [UQ_Grade_OrgCode] ON [hrm].[Grades] ([OrganizationId], [Code]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Holidays_BranchId' AND object_id = OBJECT_ID(N'hrm.Holidays'))
CREATE INDEX [IX_Holidays_BranchId] ON [hrm].[Holidays] ([BranchId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Holidays_Uuid' AND object_id = OBJECT_ID(N'hrm.Holidays'))
CREATE UNIQUE INDEX [IX_Holidays_Uuid] ON [hrm].[Holidays] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_Holiday_OrgDateName' AND object_id = OBJECT_ID(N'hrm.Holidays'))
CREATE UNIQUE INDEX [UQ_Holiday_OrgDateName] ON [hrm].[Holidays] ([OrganizationId], [Date], [Name], [BranchId]) WHERE [BranchId] IS NOT NULL;");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_HrmSettings_PayrollCostCenterId' AND object_id = OBJECT_ID(N'hrm.HrmSettings'))
CREATE INDEX [IX_HrmSettings_PayrollCostCenterId] ON [hrm].[HrmSettings] ([PayrollCostCenterId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_HrmSettings_Uuid' AND object_id = OBJECT_ID(N'hrm.HrmSettings'))
CREATE UNIQUE INDEX [IX_HrmSettings_Uuid] ON [hrm].[HrmSettings] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_HrmSettings_Org' AND object_id = OBJECT_ID(N'hrm.HrmSettings'))
CREATE UNIQUE INDEX [UQ_HrmSettings_Org] ON [hrm].[HrmSettings] ([OrganizationId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_HrUserScopes_BranchId' AND object_id = OBJECT_ID(N'hrm.HrUserScopes'))
CREATE INDEX [IX_HrUserScopes_BranchId] ON [hrm].[HrUserScopes] ([BranchId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_HrUserScopes_DepartmentId' AND object_id = OBJECT_ID(N'hrm.HrUserScopes'))
CREATE INDEX [IX_HrUserScopes_DepartmentId] ON [hrm].[HrUserScopes] ([DepartmentId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_HrUserScopes_User' AND object_id = OBJECT_ID(N'hrm.HrUserScopes'))
CREATE INDEX [IX_HrUserScopes_User] ON [hrm].[HrUserScopes] ([OrganizationId], [UserId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_HrUserScopes_Uuid' AND object_id = OBJECT_ID(N'hrm.HrUserScopes'))
CREATE UNIQUE INDEX [IX_HrUserScopes_Uuid] ON [hrm].[HrUserScopes] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Increment_EmployeeStatus' AND object_id = OBJECT_ID(N'hrm.IncrementProposals'))
CREATE INDEX [IX_Increment_EmployeeStatus] ON [hrm].[IncrementProposals] ([OrganizationId], [EmployeeId], [Status]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_IncrementProposals_AppliedAssignmentId' AND object_id = OBJECT_ID(N'hrm.IncrementProposals'))
CREATE INDEX [IX_IncrementProposals_AppliedAssignmentId] ON [hrm].[IncrementProposals] ([AppliedAssignmentId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_IncrementProposals_EmployeeId' AND object_id = OBJECT_ID(N'hrm.IncrementProposals'))
CREATE INDEX [IX_IncrementProposals_EmployeeId] ON [hrm].[IncrementProposals] ([EmployeeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_IncrementProposals_ReviewId' AND object_id = OBJECT_ID(N'hrm.IncrementProposals'))
CREATE INDEX [IX_IncrementProposals_ReviewId] ON [hrm].[IncrementProposals] ([ReviewId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_IncrementProposals_Uuid' AND object_id = OBJECT_ID(N'hrm.IncrementProposals'))
CREATE UNIQUE INDEX [IX_IncrementProposals_Uuid] ON [hrm].[IncrementProposals] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Interviews_ApplicationId' AND object_id = OBJECT_ID(N'hrm.Interviews'))
CREATE INDEX [IX_Interviews_ApplicationId] ON [hrm].[Interviews] ([ApplicationId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Interviews_InterviewerEmployeeId' AND object_id = OBJECT_ID(N'hrm.Interviews'))
CREATE INDEX [IX_Interviews_InterviewerEmployeeId] ON [hrm].[Interviews] ([InterviewerEmployeeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Interviews_OrganizationId' AND object_id = OBJECT_ID(N'hrm.Interviews'))
CREATE INDEX [IX_Interviews_OrganizationId] ON [hrm].[Interviews] ([OrganizationId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Interviews_Uuid' AND object_id = OBJECT_ID(N'hrm.Interviews'))
CREATE UNIQUE INDEX [IX_Interviews_Uuid] ON [hrm].[Interviews] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_IssuedLetters_EmployeeId' AND object_id = OBJECT_ID(N'hrm.IssuedLetters'))
CREATE INDEX [IX_IssuedLetters_EmployeeId] ON [hrm].[IssuedLetters] ([EmployeeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_IssuedLetters_TemplateId' AND object_id = OBJECT_ID(N'hrm.IssuedLetters'))
CREATE INDEX [IX_IssuedLetters_TemplateId] ON [hrm].[IssuedLetters] ([TemplateId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_IssuedLetters_Uuid' AND object_id = OBJECT_ID(N'hrm.IssuedLetters'))
CREATE UNIQUE INDEX [IX_IssuedLetters_Uuid] ON [hrm].[IssuedLetters] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_IssuedLetter_OrgNumber' AND object_id = OBJECT_ID(N'hrm.IssuedLetters'))
CREATE UNIQUE INDEX [UQ_IssuedLetter_OrgNumber] ON [hrm].[IssuedLetters] ([OrganizationId], [LetterNumber]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_JobApplication_Board' AND object_id = OBJECT_ID(N'hrm.JobApplications'))
CREATE INDEX [IX_JobApplication_Board] ON [hrm].[JobApplications] ([OrganizationId], [VacancyId], [Stage]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_JobApplications_HiredEmployeeId' AND object_id = OBJECT_ID(N'hrm.JobApplications'))
CREATE INDEX [IX_JobApplications_HiredEmployeeId] ON [hrm].[JobApplications] ([HiredEmployeeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_JobApplications_Uuid' AND object_id = OBJECT_ID(N'hrm.JobApplications'))
CREATE UNIQUE INDEX [IX_JobApplications_Uuid] ON [hrm].[JobApplications] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_JobApplications_VacancyId' AND object_id = OBJECT_ID(N'hrm.JobApplications'))
CREATE INDEX [IX_JobApplications_VacancyId] ON [hrm].[JobApplications] ([VacancyId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_JobApplication_CandidateVacancy' AND object_id = OBJECT_ID(N'hrm.JobApplications'))
CREATE UNIQUE INDEX [UQ_JobApplication_CandidateVacancy] ON [hrm].[JobApplications] ([CandidateId], [VacancyId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_JobOffers_ApplicationId' AND object_id = OBJECT_ID(N'hrm.JobOffers'))
CREATE INDEX [IX_JobOffers_ApplicationId] ON [hrm].[JobOffers] ([ApplicationId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_JobOffers_OrganizationId' AND object_id = OBJECT_ID(N'hrm.JobOffers'))
CREATE INDEX [IX_JobOffers_OrganizationId] ON [hrm].[JobOffers] ([OrganizationId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_JobOffers_Uuid' AND object_id = OBJECT_ID(N'hrm.JobOffers'))
CREATE UNIQUE INDEX [IX_JobOffers_Uuid] ON [hrm].[JobOffers] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LeaveLedger_Balance' AND object_id = OBJECT_ID(N'hrm.LeaveLedgerEntries'))
CREATE INDEX [IX_LeaveLedger_Balance] ON [hrm].[LeaveLedgerEntries] ([OrganizationId], [EmployeeId], [LeaveTypeId], [Year]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LeaveLedgerEntries_EmployeeId' AND object_id = OBJECT_ID(N'hrm.LeaveLedgerEntries'))
CREATE INDEX [IX_LeaveLedgerEntries_EmployeeId] ON [hrm].[LeaveLedgerEntries] ([EmployeeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LeaveLedgerEntries_LeaveTypeId' AND object_id = OBJECT_ID(N'hrm.LeaveLedgerEntries'))
CREATE INDEX [IX_LeaveLedgerEntries_LeaveTypeId] ON [hrm].[LeaveLedgerEntries] ([LeaveTypeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LeaveLedgerEntries_Uuid' AND object_id = OBJECT_ID(N'hrm.LeaveLedgerEntries'))
CREATE UNIQUE INDEX [IX_LeaveLedgerEntries_Uuid] ON [hrm].[LeaveLedgerEntries] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LeavePolicies_GradeId' AND object_id = OBJECT_ID(N'hrm.LeavePolicies'))
CREATE INDEX [IX_LeavePolicies_GradeId] ON [hrm].[LeavePolicies] ([GradeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LeavePolicies_LeaveTypeId' AND object_id = OBJECT_ID(N'hrm.LeavePolicies'))
CREATE INDEX [IX_LeavePolicies_LeaveTypeId] ON [hrm].[LeavePolicies] ([LeaveTypeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LeavePolicies_OrganizationId' AND object_id = OBJECT_ID(N'hrm.LeavePolicies'))
CREATE INDEX [IX_LeavePolicies_OrganizationId] ON [hrm].[LeavePolicies] ([OrganizationId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LeavePolicies_Uuid' AND object_id = OBJECT_ID(N'hrm.LeavePolicies'))
CREATE UNIQUE INDEX [IX_LeavePolicies_Uuid] ON [hrm].[LeavePolicies] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LeaveRequest_EmployeeFrom' AND object_id = OBJECT_ID(N'hrm.LeaveRequests'))
CREATE INDEX [IX_LeaveRequest_EmployeeFrom] ON [hrm].[LeaveRequests] ([OrganizationId], [EmployeeId], [FromDate]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LeaveRequest_Status' AND object_id = OBJECT_ID(N'hrm.LeaveRequests'))
CREATE INDEX [IX_LeaveRequest_Status] ON [hrm].[LeaveRequests] ([OrganizationId], [Status]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LeaveRequests_EmployeeId' AND object_id = OBJECT_ID(N'hrm.LeaveRequests'))
CREATE INDEX [IX_LeaveRequests_EmployeeId] ON [hrm].[LeaveRequests] ([EmployeeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LeaveRequests_LeaveTypeId' AND object_id = OBJECT_ID(N'hrm.LeaveRequests'))
CREATE INDEX [IX_LeaveRequests_LeaveTypeId] ON [hrm].[LeaveRequests] ([LeaveTypeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LeaveRequests_Uuid' AND object_id = OBJECT_ID(N'hrm.LeaveRequests'))
CREATE UNIQUE INDEX [IX_LeaveRequests_Uuid] ON [hrm].[LeaveRequests] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_LeaveRequest_OrgNumber' AND object_id = OBJECT_ID(N'hrm.LeaveRequests'))
CREATE UNIQUE INDEX [UQ_LeaveRequest_OrgNumber] ON [hrm].[LeaveRequests] ([OrganizationId], [RequestNumber]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LeaveTypes_Uuid' AND object_id = OBJECT_ID(N'hrm.LeaveTypes'))
CREATE UNIQUE INDEX [IX_LeaveTypes_Uuid] ON [hrm].[LeaveTypes] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_LeaveType_OrgCode' AND object_id = OBJECT_ID(N'hrm.LeaveTypes'))
CREATE UNIQUE INDEX [UQ_LeaveType_OrgCode] ON [hrm].[LeaveTypes] ([OrganizationId], [Code]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LetterTemplates_Uuid' AND object_id = OBJECT_ID(N'hrm.LetterTemplates'))
CREATE UNIQUE INDEX [IX_LetterTemplates_Uuid] ON [hrm].[LetterTemplates] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_LetterTemplate_OrgCode' AND object_id = OBJECT_ID(N'hrm.LetterTemplates'))
CREATE UNIQUE INDEX [UQ_LetterTemplate_OrgCode] ON [hrm].[LetterTemplates] ([OrganizationId], [Code]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LoanInstallment_Due' AND object_id = OBJECT_ID(N'hrm.LoanInstallments'))
CREATE INDEX [IX_LoanInstallment_Due] ON [hrm].[LoanInstallments] ([OrganizationId], [DueMonth], [Status]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LoanInstallments_RunId' AND object_id = OBJECT_ID(N'hrm.LoanInstallments'))
CREATE INDEX [IX_LoanInstallments_RunId] ON [hrm].[LoanInstallments] ([RunId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LoanInstallments_Uuid' AND object_id = OBJECT_ID(N'hrm.LoanInstallments'))
CREATE UNIQUE INDEX [IX_LoanInstallments_Uuid] ON [hrm].[LoanInstallments] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_LoanInstallment_Seq' AND object_id = OBJECT_ID(N'hrm.LoanInstallments'))
CREATE UNIQUE INDEX [UQ_LoanInstallment_Seq] ON [hrm].[LoanInstallments] ([LoanId], [Sequence]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Loan_EmployeeStatus' AND object_id = OBJECT_ID(N'hrm.Loans'))
CREATE INDEX [IX_Loan_EmployeeStatus] ON [hrm].[Loans] ([OrganizationId], [EmployeeId], [Status]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Loans_EmployeeId' AND object_id = OBJECT_ID(N'hrm.Loans'))
CREATE INDEX [IX_Loans_EmployeeId] ON [hrm].[Loans] ([EmployeeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Loans_Uuid' AND object_id = OBJECT_ID(N'hrm.Loans'))
CREATE UNIQUE INDEX [IX_Loans_Uuid] ON [hrm].[Loans] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_Loan_OrgNumber' AND object_id = OBJECT_ID(N'hrm.Loans'))
CREATE UNIQUE INDEX [UQ_Loan_OrgNumber] ON [hrm].[Loans] ([OrganizationId], [LoanNumber]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_OvertimeRecords_EmployeeId' AND object_id = OBJECT_ID(N'hrm.OvertimeRecords'))
CREATE INDEX [IX_OvertimeRecords_EmployeeId] ON [hrm].[OvertimeRecords] ([EmployeeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_OvertimeRecords_PaidInRunId' AND object_id = OBJECT_ID(N'hrm.OvertimeRecords'))
CREATE INDEX [IX_OvertimeRecords_PaidInRunId] ON [hrm].[OvertimeRecords] ([PaidInRunId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_OvertimeRecords_Uuid' AND object_id = OBJECT_ID(N'hrm.OvertimeRecords'))
CREATE UNIQUE INDEX [IX_OvertimeRecords_Uuid] ON [hrm].[OvertimeRecords] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_Overtime_EmployeeDate' AND object_id = OBJECT_ID(N'hrm.OvertimeRecords'))
CREATE UNIQUE INDEX [UQ_Overtime_EmployeeDate] ON [hrm].[OvertimeRecords] ([OrganizationId], [EmployeeId], [Date]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PayrollAdjustment_Pending' AND object_id = OBJECT_ID(N'hrm.PayrollAdjustments'))
CREATE INDEX [IX_PayrollAdjustment_Pending] ON [hrm].[PayrollAdjustments] ([OrganizationId], [EmployeeId], [AppliedInRunId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PayrollAdjustments_AppliedInRunId' AND object_id = OBJECT_ID(N'hrm.PayrollAdjustments'))
CREATE INDEX [IX_PayrollAdjustments_AppliedInRunId] ON [hrm].[PayrollAdjustments] ([AppliedInRunId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PayrollAdjustments_EmployeeId' AND object_id = OBJECT_ID(N'hrm.PayrollAdjustments'))
CREATE INDEX [IX_PayrollAdjustments_EmployeeId] ON [hrm].[PayrollAdjustments] ([EmployeeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PayrollAdjustments_ExpenseClaimId' AND object_id = OBJECT_ID(N'hrm.PayrollAdjustments'))
CREATE INDEX [IX_PayrollAdjustments_ExpenseClaimId] ON [hrm].[PayrollAdjustments] ([ExpenseClaimId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PayrollAdjustments_RunId' AND object_id = OBJECT_ID(N'hrm.PayrollAdjustments'))
CREATE INDEX [IX_PayrollAdjustments_RunId] ON [hrm].[PayrollAdjustments] ([RunId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PayrollAdjustments_Uuid' AND object_id = OBJECT_ID(N'hrm.PayrollAdjustments'))
CREATE UNIQUE INDEX [IX_PayrollAdjustments_Uuid] ON [hrm].[PayrollAdjustments] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PayrollPostings_RunId' AND object_id = OBJECT_ID(N'hrm.PayrollPostings'))
CREATE INDEX [IX_PayrollPostings_RunId] ON [hrm].[PayrollPostings] ([RunId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PayrollPostings_Uuid' AND object_id = OBJECT_ID(N'hrm.PayrollPostings'))
CREATE UNIQUE INDEX [IX_PayrollPostings_Uuid] ON [hrm].[PayrollPostings] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_PayrollPosting_Key' AND object_id = OBJECT_ID(N'hrm.PayrollPostings'))
CREATE UNIQUE INDEX [UQ_PayrollPosting_Key] ON [hrm].[PayrollPostings] ([OrganizationId], [IdempotencyKey]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PayrollComponent_LineCode' AND object_id = OBJECT_ID(N'hrm.PayrollRunLineComponents'))
CREATE INDEX [IX_PayrollComponent_LineCode] ON [hrm].[PayrollRunLineComponents] ([LineId], [Code], [CostCenterCode]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PayrollRunLineComponents_OrganizationId' AND object_id = OBJECT_ID(N'hrm.PayrollRunLineComponents'))
CREATE INDEX [IX_PayrollRunLineComponents_OrganizationId] ON [hrm].[PayrollRunLineComponents] ([OrganizationId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PayrollRunLineComponents_Uuid' AND object_id = OBJECT_ID(N'hrm.PayrollRunLineComponents'))
CREATE UNIQUE INDEX [IX_PayrollRunLineComponents_Uuid] ON [hrm].[PayrollRunLineComponents] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PayrollLine_Employee' AND object_id = OBJECT_ID(N'hrm.PayrollRunLines'))
CREATE INDEX [IX_PayrollLine_Employee] ON [hrm].[PayrollRunLines] ([OrganizationId], [EmployeeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PayrollRunLines_EmployeeId' AND object_id = OBJECT_ID(N'hrm.PayrollRunLines'))
CREATE INDEX [IX_PayrollRunLines_EmployeeId] ON [hrm].[PayrollRunLines] ([EmployeeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PayrollRunLines_Uuid' AND object_id = OBJECT_ID(N'hrm.PayrollRunLines'))
CREATE UNIQUE INDEX [IX_PayrollRunLines_Uuid] ON [hrm].[PayrollRunLines] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_PayrollLine_RunEmployee' AND object_id = OBJECT_ID(N'hrm.PayrollRunLines'))
CREATE UNIQUE INDEX [UQ_PayrollLine_RunEmployee] ON [hrm].[PayrollRunLines] ([RunId], [EmployeeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PayrollRun_Status' AND object_id = OBJECT_ID(N'hrm.PayrollRuns'))
CREATE INDEX [IX_PayrollRun_Status] ON [hrm].[PayrollRuns] ([OrganizationId], [Status]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PayrollRuns_ReversalOfRunId' AND object_id = OBJECT_ID(N'hrm.PayrollRuns'))
CREATE INDEX [IX_PayrollRuns_ReversalOfRunId] ON [hrm].[PayrollRuns] ([ReversalOfRunId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PayrollRuns_Uuid' AND object_id = OBJECT_ID(N'hrm.PayrollRuns'))
CREATE UNIQUE INDEX [IX_PayrollRuns_Uuid] ON [hrm].[PayrollRuns] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_PayrollRun_OrgNumber' AND object_id = OBJECT_ID(N'hrm.PayrollRuns'))
CREATE UNIQUE INDEX [UQ_PayrollRun_OrgNumber] ON [hrm].[PayrollRuns] ([OrganizationId], [RunNumber]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_PayrollRun_PeriodGroup' AND object_id = OBJECT_ID(N'hrm.PayrollRuns'))
CREATE UNIQUE INDEX [UQ_PayrollRun_PeriodGroup] ON [hrm].[PayrollRuns] ([OrganizationId], [PeriodStart], [PeriodEnd], [PayrollGroup]) WHERE [Status] <> 'Cancelled' AND [Status] <> 'Reversed';");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Payslips_OrganizationId' AND object_id = OBJECT_ID(N'hrm.Payslips'))
CREATE INDEX [IX_Payslips_OrganizationId] ON [hrm].[Payslips] ([OrganizationId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Payslips_Uuid' AND object_id = OBJECT_ID(N'hrm.Payslips'))
CREATE UNIQUE INDEX [IX_Payslips_Uuid] ON [hrm].[Payslips] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_Payslip_RunLine' AND object_id = OBJECT_ID(N'hrm.Payslips'))
CREATE UNIQUE INDEX [UQ_Payslip_RunLine] ON [hrm].[Payslips] ([RunLineId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ReviewCycles_OrganizationId' AND object_id = OBJECT_ID(N'hrm.ReviewCycles'))
CREATE INDEX [IX_ReviewCycles_OrganizationId] ON [hrm].[ReviewCycles] ([OrganizationId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ReviewCycles_Uuid' AND object_id = OBJECT_ID(N'hrm.ReviewCycles'))
CREATE UNIQUE INDEX [IX_ReviewCycles_Uuid] ON [hrm].[ReviewCycles] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Reviews_OrganizationId' AND object_id = OBJECT_ID(N'hrm.Reviews'))
CREATE INDEX [IX_Reviews_OrganizationId] ON [hrm].[Reviews] ([OrganizationId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Reviews_ReviewCycleId' AND object_id = OBJECT_ID(N'hrm.Reviews'))
CREATE INDEX [IX_Reviews_ReviewCycleId] ON [hrm].[Reviews] ([ReviewCycleId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Reviews_ReviewerEmployeeId' AND object_id = OBJECT_ID(N'hrm.Reviews'))
CREATE INDEX [IX_Reviews_ReviewerEmployeeId] ON [hrm].[Reviews] ([ReviewerEmployeeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Reviews_Uuid' AND object_id = OBJECT_ID(N'hrm.Reviews'))
CREATE UNIQUE INDEX [IX_Reviews_Uuid] ON [hrm].[Reviews] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_Review_EmployeeCycle' AND object_id = OBJECT_ID(N'hrm.Reviews'))
CREATE UNIQUE INDEX [UQ_Review_EmployeeCycle] ON [hrm].[Reviews] ([EmployeeId], [ReviewCycleId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SalaryAssignmentLines_ComponentId' AND object_id = OBJECT_ID(N'hrm.SalaryAssignmentLines'))
CREATE INDEX [IX_SalaryAssignmentLines_ComponentId] ON [hrm].[SalaryAssignmentLines] ([ComponentId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SalaryAssignmentLines_OrganizationId' AND object_id = OBJECT_ID(N'hrm.SalaryAssignmentLines'))
CREATE INDEX [IX_SalaryAssignmentLines_OrganizationId] ON [hrm].[SalaryAssignmentLines] ([OrganizationId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SalaryAssignmentLines_Uuid' AND object_id = OBJECT_ID(N'hrm.SalaryAssignmentLines'))
CREATE UNIQUE INDEX [IX_SalaryAssignmentLines_Uuid] ON [hrm].[SalaryAssignmentLines] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_AssignmentLine_Component' AND object_id = OBJECT_ID(N'hrm.SalaryAssignmentLines'))
CREATE UNIQUE INDEX [UQ_AssignmentLine_Component] ON [hrm].[SalaryAssignmentLines] ([AssignmentId], [ComponentId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SalaryAssignment_EmployeeFrom' AND object_id = OBJECT_ID(N'hrm.SalaryAssignments'))
CREATE INDEX [IX_SalaryAssignment_EmployeeFrom] ON [hrm].[SalaryAssignments] ([OrganizationId], [EmployeeId], [EffectiveFrom]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SalaryAssignments_EmployeeId' AND object_id = OBJECT_ID(N'hrm.SalaryAssignments'))
CREATE INDEX [IX_SalaryAssignments_EmployeeId] ON [hrm].[SalaryAssignments] ([EmployeeId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SalaryAssignments_StructureId' AND object_id = OBJECT_ID(N'hrm.SalaryAssignments'))
CREATE INDEX [IX_SalaryAssignments_StructureId] ON [hrm].[SalaryAssignments] ([StructureId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SalaryAssignments_Uuid' AND object_id = OBJECT_ID(N'hrm.SalaryAssignments'))
CREATE UNIQUE INDEX [IX_SalaryAssignments_Uuid] ON [hrm].[SalaryAssignments] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SalaryComponents_Uuid' AND object_id = OBJECT_ID(N'hrm.SalaryComponents'))
CREATE UNIQUE INDEX [IX_SalaryComponents_Uuid] ON [hrm].[SalaryComponents] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_SalaryComponent_OrgCode' AND object_id = OBJECT_ID(N'hrm.SalaryComponents'))
CREATE UNIQUE INDEX [UQ_SalaryComponent_OrgCode] ON [hrm].[SalaryComponents] ([OrganizationId], [Code]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SalaryStructureLines_ComponentId' AND object_id = OBJECT_ID(N'hrm.SalaryStructureLines'))
CREATE INDEX [IX_SalaryStructureLines_ComponentId] ON [hrm].[SalaryStructureLines] ([ComponentId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SalaryStructureLines_OrganizationId' AND object_id = OBJECT_ID(N'hrm.SalaryStructureLines'))
CREATE INDEX [IX_SalaryStructureLines_OrganizationId] ON [hrm].[SalaryStructureLines] ([OrganizationId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SalaryStructureLines_Uuid' AND object_id = OBJECT_ID(N'hrm.SalaryStructureLines'))
CREATE UNIQUE INDEX [IX_SalaryStructureLines_Uuid] ON [hrm].[SalaryStructureLines] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_StructureLine_Component' AND object_id = OBJECT_ID(N'hrm.SalaryStructureLines'))
CREATE UNIQUE INDEX [UQ_StructureLine_Component] ON [hrm].[SalaryStructureLines] ([StructureId], [ComponentId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SalaryStructures_Uuid' AND object_id = OBJECT_ID(N'hrm.SalaryStructures'))
CREATE UNIQUE INDEX [IX_SalaryStructures_Uuid] ON [hrm].[SalaryStructures] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_SalaryStructure_OrgCode' AND object_id = OBJECT_ID(N'hrm.SalaryStructures'))
CREATE UNIQUE INDEX [UQ_SalaryStructure_OrgCode] ON [hrm].[SalaryStructures] ([OrganizationId], [Code]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SensitiveAccess_Employee' AND object_id = OBJECT_ID(N'hrm.SensitiveAccessLogs'))
CREATE INDEX [IX_SensitiveAccess_Employee] ON [hrm].[SensitiveAccessLogs] ([OrganizationId], [EmployeeId], [CreatedAt]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SensitiveAccessLogs_Uuid' AND object_id = OBJECT_ID(N'hrm.SensitiveAccessLogs'))
CREATE UNIQUE INDEX [IX_SensitiveAccessLogs_Uuid] ON [hrm].[SensitiveAccessLogs] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Shifts_Uuid' AND object_id = OBJECT_ID(N'hrm.Shifts'))
CREATE UNIQUE INDEX [IX_Shifts_Uuid] ON [hrm].[Shifts] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_Shift_OrgCode' AND object_id = OBJECT_ID(N'hrm.Shifts'))
CREATE UNIQUE INDEX [UQ_Shift_OrgCode] ON [hrm].[Shifts] ([OrganizationId], [Code]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StatutoryRates_Uuid' AND object_id = OBJECT_ID(N'hrm.StatutoryRates'))
CREATE UNIQUE INDEX [IX_StatutoryRates_Uuid] ON [hrm].[StatutoryRates] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_StatutoryRate_TypeFrom' AND object_id = OBJECT_ID(N'hrm.StatutoryRates'))
CREATE UNIQUE INDEX [UQ_StatutoryRate_TypeFrom] ON [hrm].[StatutoryRates] ([OrganizationId], [RateType], [EffectiveFrom]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Remittance_TypePeriod' AND object_id = OBJECT_ID(N'hrm.StatutoryRemittances'))
CREATE INDEX [IX_Remittance_TypePeriod] ON [hrm].[StatutoryRemittances] ([OrganizationId], [RemittanceType], [PeriodStart]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StatutoryRemittances_Uuid' AND object_id = OBJECT_ID(N'hrm.StatutoryRemittances'))
CREATE UNIQUE INDEX [IX_StatutoryRemittances_Uuid] ON [hrm].[StatutoryRemittances] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_Remittance_OrgNumber' AND object_id = OBJECT_ID(N'hrm.StatutoryRemittances'))
CREATE UNIQUE INDEX [UQ_Remittance_OrgNumber] ON [hrm].[StatutoryRemittances] ([OrganizationId], [RemittanceNumber]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TaxCertificates_OrganizationId' AND object_id = OBJECT_ID(N'hrm.TaxCertificates'))
CREATE INDEX [IX_TaxCertificates_OrganizationId] ON [hrm].[TaxCertificates] ([OrganizationId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TaxCertificates_Uuid' AND object_id = OBJECT_ID(N'hrm.TaxCertificates'))
CREATE UNIQUE INDEX [IX_TaxCertificates_Uuid] ON [hrm].[TaxCertificates] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_TaxCertificate_EmployeeYear' AND object_id = OBJECT_ID(N'hrm.TaxCertificates'))
CREATE UNIQUE INDEX [UQ_TaxCertificate_EmployeeYear] ON [hrm].[TaxCertificates] ([EmployeeId], [TaxYear]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TaxSlabs_OrganizationId' AND object_id = OBJECT_ID(N'hrm.TaxSlabs'))
CREATE INDEX [IX_TaxSlabs_OrganizationId] ON [hrm].[TaxSlabs] ([OrganizationId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TaxSlabs_Uuid' AND object_id = OBJECT_ID(N'hrm.TaxSlabs'))
CREATE UNIQUE INDEX [IX_TaxSlabs_Uuid] ON [hrm].[TaxSlabs] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_TaxSlab_TableFrom' AND object_id = OBJECT_ID(N'hrm.TaxSlabs'))
CREATE UNIQUE INDEX [UQ_TaxSlab_TableFrom] ON [hrm].[TaxSlabs] ([TableId], [FromAmount]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TaxSlabTables_Uuid' AND object_id = OBJECT_ID(N'hrm.TaxSlabTables'))
CREATE UNIQUE INDEX [IX_TaxSlabTables_Uuid] ON [hrm].[TaxSlabTables] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_TaxSlabTable_OrgCode' AND object_id = OBJECT_ID(N'hrm.TaxSlabTables'))
CREATE UNIQUE INDEX [UQ_TaxSlabTable_OrgCode] ON [hrm].[TaxSlabTables] ([OrganizationId], [Code]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Vacancies_BranchId' AND object_id = OBJECT_ID(N'hrm.Vacancies'))
CREATE INDEX [IX_Vacancies_BranchId] ON [hrm].[Vacancies] ([BranchId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Vacancies_DepartmentId' AND object_id = OBJECT_ID(N'hrm.Vacancies'))
CREATE INDEX [IX_Vacancies_DepartmentId] ON [hrm].[Vacancies] ([DepartmentId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Vacancies_DesignationId' AND object_id = OBJECT_ID(N'hrm.Vacancies'))
CREATE INDEX [IX_Vacancies_DesignationId] ON [hrm].[Vacancies] ([DesignationId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Vacancies_Uuid' AND object_id = OBJECT_ID(N'hrm.Vacancies'))
CREATE UNIQUE INDEX [IX_Vacancies_Uuid] ON [hrm].[Vacancies] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Vacancy_Status' AND object_id = OBJECT_ID(N'hrm.Vacancies'))
CREATE INDEX [IX_Vacancy_Status] ON [hrm].[Vacancies] ([OrganizationId], [Status]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_Vacancy_OrgNumber' AND object_id = OBJECT_ID(N'hrm.Vacancies'))
CREATE UNIQUE INDEX [UQ_Vacancy_OrgNumber] ON [hrm].[Vacancies] ([OrganizationId], [VacancyNumber]);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttendanceDays",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "AttendanceEvents",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "CourseEnrollments",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "EmployeeBankAccounts",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "EmployeeDocuments",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "EmploymentCostSplits",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "ExpenseLines",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "FinalSettlementLines",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "Goals",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "Holidays",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "HrmSettings",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "HrUserScopes",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "IncrementProposals",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "Interviews",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "IssuedLetters",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "JobOffers",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "LeaveLedgerEntries",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "LeavePolicies",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "LeaveRequests",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "LoanInstallments",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "OvertimeRecords",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "PayrollAdjustments",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "PayrollPostings",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "PayrollRunLineComponents",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "Payslips",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "SalaryAssignmentLines",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "SalaryStructureLines",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "SensitiveAccessLogs",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "StatutoryRates",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "StatutoryRemittances",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "TaxCertificates",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "TaxSlabs",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "Courses",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "EmploymentRecords",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "ExpenseCategories",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "FinalSettlements",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "Reviews",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "LetterTemplates",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "JobApplications",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "LeaveTypes",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "Loans",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "ExpenseClaims",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "PayrollRunLines",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "SalaryAssignments",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "SalaryComponents",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "TaxSlabTables",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "Grades",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "Shifts",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "ReviewCycles",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "Candidates",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "Vacancies",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "PayrollRuns",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "SalaryStructures",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "Departments",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "Designations",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "CostCenters",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "Employees",
                schema: "hrm");

            migrationBuilder.DropTable(
                name: "Branches",
                schema: "hrm");
        }
    }
}
