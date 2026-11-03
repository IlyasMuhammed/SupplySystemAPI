using System;
using Microsoft.EntityFrameworkCore.Migrations;
using SMS.Modules.Finance.ArAp;

#nullable disable

namespace SMS.Modules.Finance.Migrations
{
    /// <summary>
    /// A48 (CORE) — AR & AP sub-ledgers (docs/ar-ap-subledgers/API-CONTRACT.md §11, D-CORE-1): the new finance.* tables
    /// (partner financial profiles, credit audit, statement log, dunning, tolerance rules, per-line match, early-payment terms,
    /// payment schedules and runs, credit/debit note applications), the new columns on invoices / debit_notes /
    /// supplier_payment_lines / org_settings, the D-16 seeds for every existing organization and the D-8 payment-schedule
    /// backfill.
    /// <para>
    /// <b>Guarded and idempotent</b> (every API start migrates the shared, drifted database): every column, table, index and
    /// constraint is created only when missing (one statement per batch, so a column added above is visible below); seeds and
    /// the backfill insert only what is missing. Additive only — no existing column changes. Down drops it all again.
    /// </para>
    /// </summary>
    public partial class A48_ArApSubledgers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {            migrationBuilder.Sql(@"IF COL_LENGTH('finance.supplier_payment_lines', 'DiscountAmount') IS NULL
ALTER TABLE [finance].[supplier_payment_lines] ADD [DiscountAmount] decimal(18,2) NOT NULL DEFAULT 0.0;");

            migrationBuilder.Sql(@"IF COL_LENGTH('finance.org_settings', 'CreditControlMode') IS NULL
ALTER TABLE [finance].[org_settings] ADD [CreditControlMode] nvarchar(10) NOT NULL DEFAULT N'Off';");

            migrationBuilder.Sql(@"IF COL_LENGTH('finance.invoices', 'AutoApproved') IS NULL
ALTER TABLE [finance].[invoices] ADD [AutoApproved] bit NOT NULL DEFAULT CAST(0 AS bit);");

            migrationBuilder.Sql(@"IF COL_LENGTH('finance.invoices', 'LineMatchResult') IS NULL
ALTER TABLE [finance].[invoices] ADD [LineMatchResult] nvarchar(20) NULL;");

            migrationBuilder.Sql(@"IF COL_LENGTH('finance.invoices', 'MatchOverriddenAt') IS NULL
ALTER TABLE [finance].[invoices] ADD [MatchOverriddenAt] datetime2 NULL;");

            migrationBuilder.Sql(@"IF COL_LENGTH('finance.invoices', 'MatchOverriddenBy') IS NULL
ALTER TABLE [finance].[invoices] ADD [MatchOverriddenBy] int NULL;");

            migrationBuilder.Sql(@"IF COL_LENGTH('finance.invoices', 'MatchOverriddenByName') IS NULL
ALTER TABLE [finance].[invoices] ADD [MatchOverriddenByName] nvarchar(200) NULL;");

            migrationBuilder.Sql(@"IF COL_LENGTH('finance.invoices', 'MatchOverrideReason') IS NULL
ALTER TABLE [finance].[invoices] ADD [MatchOverrideReason] nvarchar(500) NULL;");

            migrationBuilder.Sql(@"IF COL_LENGTH('finance.invoices', 'MatchType') IS NULL
ALTER TABLE [finance].[invoices] ADD [MatchType] nvarchar(10) NULL;");

            migrationBuilder.Sql(@"IF COL_LENGTH('finance.invoices', 'MatchVariancePercent') IS NULL
ALTER TABLE [finance].[invoices] ADD [MatchVariancePercent] decimal(9,4) NULL;");

            migrationBuilder.Sql(@"IF COL_LENGTH('finance.invoices', 'MatchedAt') IS NULL
ALTER TABLE [finance].[invoices] ADD [MatchedAt] datetime2 NULL;");

            migrationBuilder.Sql(@"IF COL_LENGTH('finance.invoices', 'MatchedBy') IS NULL
ALTER TABLE [finance].[invoices] ADD [MatchedBy] int NULL;");

            migrationBuilder.Sql(@"IF COL_LENGTH('finance.invoices', 'MatchedByName') IS NULL
ALTER TABLE [finance].[invoices] ADD [MatchedByName] nvarchar(200) NULL;");

            migrationBuilder.Sql(@"IF COL_LENGTH('finance.invoices', 'ToleranceRuleId') IS NULL
ALTER TABLE [finance].[invoices] ADD [ToleranceRuleId] int NULL;");

            migrationBuilder.Sql(@"IF COL_LENGTH('finance.invoices', 'WithinTolerance') IS NULL
ALTER TABLE [finance].[invoices] ADD [WithinTolerance] bit NULL;");

            migrationBuilder.Sql(@"IF COL_LENGTH('finance.debit_notes', 'Origin') IS NULL
ALTER TABLE [finance].[debit_notes] ADD [Origin] nvarchar(10) NOT NULL DEFAULT N'SRO';");

            migrationBuilder.Sql(@"IF OBJECT_ID('finance.customer_credit_applications') IS NULL
CREATE TABLE [finance].[customer_credit_applications] (
    [Id] int NOT NULL IDENTITY,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [CreditNoteId] int NOT NULL,
    [CreditNoteUuid] uniqueidentifier NOT NULL,
    [CreditNoteNumber] nvarchar(30) NOT NULL,
    [InvoiceId] int NOT NULL,
    [InvoiceUuid] uniqueidentifier NOT NULL,
    [InvoiceNumber] nvarchar(30) NOT NULL,
    [PartnerUuid] uniqueidentifier NOT NULL,
    [Amount] decimal(18,2) NOT NULL,
    [Notes] nvarchar(300) NULL,
    [AppliedAt] datetime2 NOT NULL,
    [AppliedBy] int NULL,
    [AppliedByName] nvarchar(200) NULL,
    [ReversedAt] datetime2 NULL,
    [ReversedBy] int NULL,
    [ReversalReason] nvarchar(500) NULL,
    CONSTRAINT [PK_customer_credit_applications] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_customer_credit_applications_Amount] CHECK ([Amount] > 0),
    CONSTRAINT [CK_customer_credit_applications_Distinct] CHECK ([CreditNoteId] <> [InvoiceId]),
    CONSTRAINT [FK_customer_credit_applications_sales_invoices_CreditNoteId] FOREIGN KEY ([CreditNoteId]) REFERENCES [finance].[sales_invoices] ([Id]),
    CONSTRAINT [FK_customer_credit_applications_sales_invoices_InvoiceId] FOREIGN KEY ([InvoiceId]) REFERENCES [finance].[sales_invoices] ([Id])
);");

            migrationBuilder.Sql(@"IF OBJECT_ID('finance.dunning_procedures') IS NULL
CREATE TABLE [finance].[dunning_procedures] (
    [Id] int NOT NULL IDENTITY,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [Code] nvarchar(20) NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [Description] nvarchar(500) NULL,
    [IsDefault] bit NOT NULL,
    [IsActive] bit NOT NULL DEFAULT CAST(1 AS bit),
    [GraceDays] int NOT NULL,
    [MinimumAmount] decimal(18,2) NOT NULL,
    [BlockSalesAtLevel] int NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    CONSTRAINT [PK_dunning_procedures] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_dunning_procedures_BlockSalesAtLevel] CHECK ([BlockSalesAtLevel] IS NULL OR [BlockSalesAtLevel] >= 1),
    CONSTRAINT [CK_dunning_procedures_GraceDays] CHECK ([GraceDays] >= 0),
    CONSTRAINT [CK_dunning_procedures_MinimumAmount] CHECK ([MinimumAmount] >= 0)
);");

            migrationBuilder.Sql(@"IF OBJECT_ID('finance.dunning_runs') IS NULL
CREATE TABLE [finance].[dunning_runs] (
    [Id] int NOT NULL IDENTITY,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [RunNumber] nvarchar(30) NOT NULL,
    [RunDate] date NOT NULL,
    [AsOfDate] date NOT NULL,
    [Status] nvarchar(20) NOT NULL DEFAULT N'Draft',
    [CurrencyCode] nvarchar(10) NULL,
    [TotalCustomers] int NOT NULL,
    [TotalAmount] decimal(18,2) NOT NULL,
    [ItemsProcessed] int NOT NULL,
    [ItemsSent] int NOT NULL,
    [ItemsSkipped] int NOT NULL,
    [ItemsFailed] int NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ExecutedAt] datetime2 NULL,
    [ExecutedBy] int NULL,
    [ExecutedByName] nvarchar(200) NULL,
    [CompletedAt] datetime2 NULL,
    [CancelledAt] datetime2 NULL,
    [CancelledBy] int NULL,
    [CancelledByName] nvarchar(200) NULL,
    [CancelReason] nvarchar(500) NULL,
    CONSTRAINT [PK_dunning_runs] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_dunning_runs_Status] CHECK ([Status] IN ('Draft','Processing','Completed','Cancelled'))
);");

            migrationBuilder.Sql(@"IF OBJECT_ID('finance.early_payment_terms') IS NULL
CREATE TABLE [finance].[early_payment_terms] (
    [Id] int NOT NULL IDENTITY,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [Code] nvarchar(20) NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [DiscountPercent] decimal(9,4) NOT NULL,
    [DiscountDays] int NOT NULL,
    [NetDays] int NOT NULL,
    [IsActive] bit NOT NULL DEFAULT CAST(1 AS bit),
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    CONSTRAINT [PK_early_payment_terms] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_early_payment_terms_DiscountDays] CHECK ([DiscountDays] >= 0 AND [DiscountDays] < [NetDays]),
    CONSTRAINT [CK_early_payment_terms_DiscountPercent] CHECK ([DiscountPercent] > 0 AND [DiscountPercent] < 100)
);");

            migrationBuilder.Sql(@"IF OBJECT_ID('finance.invoice_match_lines') IS NULL
CREATE TABLE [finance].[invoice_match_lines] (
    [Id] int NOT NULL IDENTITY,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [InvoiceId] int NOT NULL,
    [InvoiceLineUuid] uniqueidentifier NULL,
    [PoLineUuid] uniqueidentifier NULL,
    [GrnLineUuid] uniqueidentifier NULL,
    [LineNo] int NOT NULL,
    [ItemDescription] nvarchar(300) NOT NULL,
    [PoQuantity] decimal(18,4) NULL,
    [PoUnitPrice] decimal(18,4) NULL,
    [PoLineTotal] decimal(18,2) NULL,
    [GrnQuantity] decimal(18,4) NULL,
    [InvoiceQuantity] decimal(18,4) NOT NULL,
    [InvoiceUnitPrice] decimal(18,4) NOT NULL,
    [InvoiceLineTotal] decimal(18,2) NOT NULL,
    [QuantityVariance] decimal(18,4) NULL,
    [PriceVariance] decimal(18,4) NULL,
    [PriceVariancePercent] decimal(9,4) NULL,
    [TotalVariance] decimal(18,2) NULL,
    [LineStatus] nvarchar(20) NOT NULL DEFAULT N'Pending',
    [Note] nvarchar(300) NULL,
    [CreatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_invoice_match_lines] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_invoice_match_lines_LineStatus] CHECK ([LineStatus] IN ('Pending','Matched','QuantityMismatch','PriceMismatch','Unmatched','Overridden')),
    CONSTRAINT [FK_invoice_match_lines_invoices_InvoiceId] FOREIGN KEY ([InvoiceId]) REFERENCES [finance].[invoices] ([Id]) ON DELETE CASCADE
);");

            migrationBuilder.Sql(@"IF OBJECT_ID('finance.match_tolerance_rules') IS NULL
CREATE TABLE [finance].[match_tolerance_rules] (
    [Id] int NOT NULL IDENTITY,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [VendorUuid] uniqueidentifier NULL,
    [VendorName] nvarchar(200) NULL,
    [PriceTolerancePercent] decimal(9,4) NOT NULL,
    [PriceToleranceAmount] decimal(18,2) NOT NULL,
    [QuantityTolerancePercent] decimal(9,4) NOT NULL,
    [TotalTolerancePercent] decimal(9,4) NOT NULL,
    [TotalToleranceAmount] decimal(18,2) NOT NULL,
    [AutoApproveWithin] bit NOT NULL,
    [IsDefault] bit NOT NULL,
    [IsActive] bit NOT NULL DEFAULT CAST(1 AS bit),
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    CONSTRAINT [PK_match_tolerance_rules] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_match_tolerance_rules_NonNegative] CHECK ([PriceTolerancePercent] >= 0 AND [PriceToleranceAmount] >= 0 AND [QuantityTolerancePercent] >= 0 AND [TotalTolerancePercent] >= 0 AND [TotalToleranceAmount] >= 0),
    CONSTRAINT [CK_match_tolerance_rules_Percent] CHECK ([PriceTolerancePercent] <= 100 AND [QuantityTolerancePercent] <= 100 AND [TotalTolerancePercent] <= 100)
);");

            migrationBuilder.Sql(@"IF OBJECT_ID('finance.payment_runs') IS NULL
CREATE TABLE [finance].[payment_runs] (
    [Id] int NOT NULL IDENTITY,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [RunNumber] nvarchar(30) NOT NULL,
    [RunDate] date NOT NULL,
    [PaymentDate] date NOT NULL,
    [DueDateCutoff] date NOT NULL,
    [Status] nvarchar(20) NOT NULL DEFAULT N'Draft',
    [SelectionCriteria] nvarchar(max) NULL,
    [PaymentMethod] nvarchar(30) NULL,
    [BankAccount] nvarchar(100) NULL,
    [IncludeDiscount] bit NOT NULL DEFAULT CAST(1 AS bit),
    [BaseCurrencyCode] nvarchar(10) NULL,
    [TotalVendors] int NOT NULL,
    [TotalItems] int NOT NULL,
    [TotalAmount] decimal(18,2) NOT NULL,
    [TotalDiscount] decimal(18,2) NOT NULL,
    [NetPaymentAmount] decimal(18,2) NOT NULL,
    [FailedBatchCount] int NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [ProposedBy] int NULL,
    [ProposedByName] nvarchar(200) NULL,
    [ProposedAt] datetime2 NULL,
    [ApprovedBy] int NULL,
    [ApprovedByName] nvarchar(200) NULL,
    [ApprovedAt] datetime2 NULL,
    [ExecutedBy] int NULL,
    [ExecutedByName] nvarchar(200) NULL,
    [ExecutedAt] datetime2 NULL,
    [CompletedAt] datetime2 NULL,
    [CancelledBy] int NULL,
    [CancelledByName] nvarchar(200) NULL,
    [CancelledAt] datetime2 NULL,
    [CancelReason] nvarchar(500) NULL,
    [PaymentFileName] nvarchar(200) NULL,
    [ExportedAt] datetime2 NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_payment_runs] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_payment_runs_Status] CHECK ([Status] IN ('Draft','Proposed','Reviewing','Approved','Executing','Completed','Cancelled'))
);");

            migrationBuilder.Sql(@"IF OBJECT_ID('finance.statement_log') IS NULL
CREATE TABLE [finance].[statement_log] (
    [Id] int NOT NULL IDENTITY,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [PartnerUuid] uniqueidentifier NOT NULL,
    [PartnerName] nvarchar(200) NULL,
    [PartnerRole] nvarchar(10) NOT NULL,
    [StatementType] nvarchar(20) NOT NULL,
    [FromDate] date NOT NULL,
    [ToDate] date NOT NULL,
    [CurrencyCode] nvarchar(10) NULL,
    [OpeningBalance] decimal(18,2) NOT NULL,
    [ClosingBalance] decimal(18,2) NOT NULL,
    [LineCount] int NOT NULL,
    [Format] nvarchar(10) NOT NULL DEFAULT N'PDF',
    [EmailStatus] nvarchar(10) NOT NULL DEFAULT N'NotSent',
    [EmailedTo] nvarchar(500) NULL,
    [EmailedAt] datetime2 NULL,
    [EmailError] nvarchar(500) NULL,
    [BatchUuid] uniqueidentifier NULL,
    [GeneratedAt] datetime2 NOT NULL,
    [GeneratedBy] int NULL,
    [GeneratedByName] nvarchar(200) NULL,
    CONSTRAINT [PK_statement_log] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_statement_log_Dates] CHECK ([ToDate] >= [FromDate]),
    CONSTRAINT [CK_statement_log_EmailStatus] CHECK ([EmailStatus] IN ('NotSent','Sent','Failed')),
    CONSTRAINT [CK_statement_log_PartnerRole] CHECK ([PartnerRole] IN ('Customer','Vendor')),
    CONSTRAINT [CK_statement_log_StatementType] CHECK ([StatementType] IN ('Activity','OpenItems'))
);");

            migrationBuilder.Sql(@"IF OBJECT_ID('finance.supplier_note_applications') IS NULL
CREATE TABLE [finance].[supplier_note_applications] (
    [Id] int NOT NULL IDENTITY,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [NoteType] nvarchar(20) NOT NULL,
    [NoteUuid] uniqueidentifier NOT NULL,
    [NoteNumber] nvarchar(20) NOT NULL,
    [InvoiceId] int NOT NULL,
    [InvoiceUuid] uniqueidentifier NOT NULL,
    [InvoiceNumber] nvarchar(20) NOT NULL,
    [VendorUuid] uniqueidentifier NOT NULL,
    [Amount] decimal(18,2) NOT NULL,
    [Notes] nvarchar(300) NULL,
    [AppliedAt] datetime2 NOT NULL,
    [AppliedBy] int NULL,
    [AppliedByName] nvarchar(200) NULL,
    [ReversedAt] datetime2 NULL,
    [ReversedBy] int NULL,
    [ReversalReason] nvarchar(500) NULL,
    CONSTRAINT [PK_supplier_note_applications] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_supplier_note_applications_Amount] CHECK ([Amount] > 0),
    CONSTRAINT [CK_supplier_note_applications_NoteType] CHECK ([NoteType] IN ('DebitNote','CreditNote')),
    CONSTRAINT [FK_supplier_note_applications_invoices_InvoiceId] FOREIGN KEY ([InvoiceId]) REFERENCES [finance].[invoices] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID('finance.dunning_levels') IS NULL
CREATE TABLE [finance].[dunning_levels] (
    [Id] int NOT NULL IDENTITY,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [DunningProcedureId] int NOT NULL,
    [LevelNumber] int NOT NULL,
    [DaysAfterPrevious] int NOT NULL,
    [SubjectTemplate] nvarchar(200) NOT NULL,
    [BodyTemplate] nvarchar(max) NOT NULL,
    [IncludeInvoiceList] bit NOT NULL DEFAULT CAST(1 AS bit),
    [IncludeStatement] bit NOT NULL,
    [ChargeAmount] decimal(18,2) NULL,
    [ActionType] nvarchar(20) NOT NULL DEFAULT N'Email',
    [EscalationNote] nvarchar(500) NULL,
    CONSTRAINT [PK_dunning_levels] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_dunning_levels_ActionType] CHECK ([ActionType] IN ('Email','Letter','Manual')),
    CONSTRAINT [CK_dunning_levels_ChargeAmount] CHECK ([ChargeAmount] IS NULL OR [ChargeAmount] >= 0),
    CONSTRAINT [CK_dunning_levels_DaysAfterPrevious] CHECK ([DaysAfterPrevious] >= 0),
    CONSTRAINT [CK_dunning_levels_LevelNumber] CHECK ([LevelNumber] >= 1),
    CONSTRAINT [FK_dunning_levels_dunning_procedures_DunningProcedureId] FOREIGN KEY ([DunningProcedureId]) REFERENCES [finance].[dunning_procedures] ([Id]) ON DELETE CASCADE
);");

            migrationBuilder.Sql(@"IF OBJECT_ID('finance.dunning_run_items') IS NULL
CREATE TABLE [finance].[dunning_run_items] (
    [Id] int NOT NULL IDENTITY,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [DunningRunId] int NOT NULL,
    [PartnerUuid] uniqueidentifier NOT NULL,
    [PartnerName] nvarchar(200) NOT NULL,
    [PartnerCode] nvarchar(50) NULL,
    [DunningProcedureId] int NULL,
    [DunningLevel] int NOT NULL,
    [PreviousLevel] int NOT NULL,
    [OverdueAmount] decimal(18,2) NOT NULL,
    [CurrencySummary] nvarchar(500) NULL,
    [OpenItemCount] int NOT NULL,
    [OldestDueDate] date NOT NULL,
    [DaysOverdue] int NOT NULL,
    [ActionType] nvarchar(20) NOT NULL DEFAULT N'Email',
    [ChargeAmount] decimal(18,2) NULL,
    [ChargeDocumentUuid] uniqueidentifier NULL,
    [ChargeDocumentNumber] nvarchar(30) NULL,
    [SentTo] nvarchar(500) NULL,
    [SentAt] datetime2 NULL,
    [Subject] nvarchar(200) NULL,
    [Body] nvarchar(max) NULL,
    [InvoiceUuids] nvarchar(max) NULL,
    [Status] nvarchar(20) NOT NULL DEFAULT N'Pending',
    [SkipReason] nvarchar(200) NULL,
    [FailureReason] nvarchar(1000) NULL,
    CONSTRAINT [PK_dunning_run_items] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_dunning_run_items_ActionType] CHECK ([ActionType] IN ('Email','Letter','Manual')),
    CONSTRAINT [CK_dunning_run_items_Status] CHECK ([Status] IN ('Pending','Sent','Skipped','Failed','Manual')),
    CONSTRAINT [FK_dunning_run_items_dunning_runs_DunningRunId] FOREIGN KEY ([DunningRunId]) REFERENCES [finance].[dunning_runs] ([Id]) ON DELETE CASCADE
);");

            migrationBuilder.Sql(@"IF OBJECT_ID('finance.partner_financial_profiles') IS NULL
CREATE TABLE [finance].[partner_financial_profiles] (
    [Id] int NOT NULL IDENTITY,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [PartnerUuid] uniqueidentifier NOT NULL,
    [PartnerRole] nvarchar(10) NOT NULL,
    [CreditHold] bit NOT NULL,
    [CreditHoldReason] nvarchar(500) NULL,
    [CreditHoldAt] datetime2 NULL,
    [CreditHoldBy] int NULL,
    [CreditHoldByName] nvarchar(200) NULL,
    [TemporaryCreditLimit] decimal(18,2) NULL,
    [TemporaryCreditExpiry] date NULL,
    [RiskTier] nvarchar(10) NOT NULL DEFAULT N'Medium',
    [DunningProcedureId] int NULL,
    [CurrentDunningLevel] int NOT NULL,
    [LastDunningDate] date NULL,
    [BlockSalesAtDunning] bit NOT NULL,
    [ExcludeFromDunning] bit NOT NULL,
    [DefaultPaymentMethod] nvarchar(30) NULL,
    [PaymentPriority] nvarchar(10) NOT NULL DEFAULT N'Normal',
    [PreferredPaymentMethod] nvarchar(30) NULL,
    [WithholdingTaxApplicable] bit NOT NULL,
    [EarlyPaymentTermId] int NULL,
    [LastPaymentDate] date NULL,
    [AverageDaysToPay] int NULL,
    [TotalOverdueAmount] decimal(18,2) NULL,
    [StatisticsUpdatedAt] datetime2 NULL,
    [Notes] nvarchar(1000) NULL,
    [CreatedAt] datetime2 NOT NULL,
    [CreatedBy] int NULL,
    [CreatedByName] nvarchar(200) NULL,
    [ModifiedAt] datetime2 NULL,
    [ModifiedBy] int NULL,
    [ModifiedByName] nvarchar(200) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_partner_financial_profiles] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_partner_financial_profiles_DunningLevel] CHECK ([CurrentDunningLevel] >= 0),
    CONSTRAINT [CK_partner_financial_profiles_PartnerRole] CHECK ([PartnerRole] IN ('Customer','Vendor')),
    CONSTRAINT [CK_partner_financial_profiles_PaymentPriority] CHECK ([PaymentPriority] IN ('High','Normal','Low')),
    CONSTRAINT [CK_partner_financial_profiles_RiskTier] CHECK ([RiskTier] IN ('Low','Medium','High')),
    CONSTRAINT [CK_partner_financial_profiles_TemporaryCreditLimit] CHECK ([TemporaryCreditLimit] IS NULL OR [TemporaryCreditLimit] > 0),
    CONSTRAINT [FK_partner_financial_profiles_dunning_procedures_DunningProcedureId] FOREIGN KEY ([DunningProcedureId]) REFERENCES [finance].[dunning_procedures] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_partner_financial_profiles_early_payment_terms_EarlyPaymentTermId] FOREIGN KEY ([EarlyPaymentTermId]) REFERENCES [finance].[early_payment_terms] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID('finance.payment_run_batches') IS NULL
CREATE TABLE [finance].[payment_run_batches] (
    [Id] int NOT NULL IDENTITY,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [PaymentRunId] int NOT NULL,
    [VendorUuid] uniqueidentifier NOT NULL,
    [VendorName] nvarchar(200) NOT NULL,
    [CurrencyCode] nvarchar(10) NOT NULL,
    [PaymentMethod] nvarchar(30) NOT NULL,
    [BankDetailsMasked] nvarchar(100) NULL,
    [ItemCount] int NOT NULL,
    [TotalAmount] decimal(18,2) NOT NULL,
    [TotalDiscount] decimal(18,2) NOT NULL,
    [NetAmount] decimal(18,2) NOT NULL,
    [PaymentReference] nvarchar(100) NULL,
    [SupplierPaymentUuid] uniqueidentifier NULL,
    [SupplierPaymentNumber] nvarchar(20) NULL,
    [Status] nvarchar(20) NOT NULL DEFAULT N'Pending',
    [FailureReason] nvarchar(1000) NULL,
    [ExecutedAt] datetime2 NULL,
    CONSTRAINT [PK_payment_run_batches] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_payment_run_batches_Status] CHECK ([Status] IN ('Pending','Paid','Failed')),
    CONSTRAINT [FK_payment_run_batches_payment_runs_PaymentRunId] FOREIGN KEY ([PaymentRunId]) REFERENCES [finance].[payment_runs] ([Id]) ON DELETE CASCADE
);");

            migrationBuilder.Sql(@"IF OBJECT_ID('finance.payment_schedules') IS NULL
CREATE TABLE [finance].[payment_schedules] (
    [Id] int NOT NULL IDENTITY,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [InvoiceId] int NOT NULL,
    [InvoiceUuid] uniqueidentifier NOT NULL,
    [InvoiceNumber] nvarchar(20) NOT NULL,
    [SupplierInvoiceNo] nvarchar(50) NULL,
    [VendorUuid] uniqueidentifier NOT NULL,
    [VendorName] nvarchar(200) NOT NULL,
    [CurrencyCode] nvarchar(10) NOT NULL,
    [InvoiceDate] date NOT NULL,
    [DueDate] date NOT NULL,
    [OriginalAmount] decimal(18,2) NOT NULL,
    [AmountRemaining] decimal(18,2) NOT NULL,
    [PaymentPriority] nvarchar(10) NOT NULL DEFAULT N'Normal',
    [PaymentMethod] nvarchar(30) NULL,
    [PaymentBlock] bit NOT NULL,
    [BlockReason] nvarchar(200) NULL,
    [BlockedBy] int NULL,
    [BlockedByName] nvarchar(200) NULL,
    [BlockedAt] datetime2 NULL,
    [EarlyPaymentTermId] int NULL,
    [DiscountDate] date NULL,
    [DiscountPercent] decimal(9,4) NULL,
    [DiscountAmount] decimal(18,2) NULL,
    [ReservedByRunId] int NULL,
    [PaidInRunId] int NULL,
    [Status] nvarchar(20) NOT NULL DEFAULT N'Open',
    [CreatedAt] datetime2 NOT NULL,
    [ModifiedAt] datetime2 NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_payment_schedules] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_payment_schedules_PaymentPriority] CHECK ([PaymentPriority] IN ('High','Normal','Low')),
    CONSTRAINT [CK_payment_schedules_Status] CHECK ([Status] IN ('Open','Blocked','Scheduled','PartiallyPaid','Paid','Cancelled')),
    CONSTRAINT [FK_payment_schedules_early_payment_terms_EarlyPaymentTermId] FOREIGN KEY ([EarlyPaymentTermId]) REFERENCES [finance].[early_payment_terms] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_payment_schedules_invoices_InvoiceId] FOREIGN KEY ([InvoiceId]) REFERENCES [finance].[invoices] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_payment_schedules_payment_runs_PaidInRunId] FOREIGN KEY ([PaidInRunId]) REFERENCES [finance].[payment_runs] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_payment_schedules_payment_runs_ReservedByRunId] FOREIGN KEY ([ReservedByRunId]) REFERENCES [finance].[payment_runs] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID('finance.credit_hold_requests') IS NULL
CREATE TABLE [finance].[credit_hold_requests] (
    [Id] int NOT NULL IDENTITY,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [ProfileId] int NOT NULL,
    [PartnerUuid] uniqueidentifier NOT NULL,
    [PartnerName] nvarchar(200) NULL,
    [Action] nvarchar(20) NOT NULL,
    [Reason] nvarchar(500) NOT NULL,
    [PreviousValue] nvarchar(200) NULL,
    [NewValue] nvarchar(200) NULL,
    [Amount] decimal(18,2) NULL,
    [ExpiryDate] date NULL,
    [SourceDocumentType] nvarchar(30) NULL,
    [SourceDocumentUuid] uniqueidentifier NULL,
    [SourceDocumentNumber] nvarchar(50) NULL,
    [RequestedBy] int NULL,
    [RequestedByName] nvarchar(200) NULL,
    [RequestedAt] datetime2 NOT NULL,
    [ApprovedBy] int NULL,
    [ApprovedByName] nvarchar(200) NULL,
    [ApprovedAt] datetime2 NULL,
    [Status] nvarchar(20) NOT NULL DEFAULT N'Completed',
    CONSTRAINT [PK_credit_hold_requests] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_credit_hold_requests_Action] CHECK ([Action] IN ('Hold','Release','TempIncrease','TempExpired','RiskTier','Override','DunningHold','DunningReset')),
    CONSTRAINT [CK_credit_hold_requests_Status] CHECK ([Status] IN ('Pending','Completed','Rejected')),
    CONSTRAINT [FK_credit_hold_requests_partner_financial_profiles_ProfileId] FOREIGN KEY ([ProfileId]) REFERENCES [finance].[partner_financial_profiles] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF OBJECT_ID('finance.payment_run_items') IS NULL
CREATE TABLE [finance].[payment_run_items] (
    [Id] int NOT NULL IDENTITY,
    [Uuid] uniqueidentifier NOT NULL,
    [OrganizationId] uniqueidentifier NOT NULL,
    [PaymentRunId] int NOT NULL,
    [BatchId] int NULL,
    [PaymentScheduleId] int NOT NULL,
    [InvoiceUuid] uniqueidentifier NOT NULL,
    [InvoiceNumber] nvarchar(20) NOT NULL,
    [VendorUuid] uniqueidentifier NOT NULL,
    [VendorName] nvarchar(200) NOT NULL,
    [CurrencyCode] nvarchar(10) NOT NULL,
    [DueDate] date NOT NULL,
    [OriginalAmount] decimal(18,2) NOT NULL,
    [DiscountAmount] decimal(18,2) NOT NULL,
    [NetPaymentAmount] decimal(18,2) NOT NULL,
    [PaymentMethod] nvarchar(30) NULL,
    [Status] nvarchar(20) NOT NULL DEFAULT N'Proposed',
    [RemovedReason] nvarchar(200) NULL,
    [RemovedBy] int NULL,
    [RemovedByName] nvarchar(200) NULL,
    [RemovedAt] datetime2 NULL,
    [PaidAt] datetime2 NULL,
    CONSTRAINT [PK_payment_run_items] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_payment_run_items_Status] CHECK ([Status] IN ('Proposed','Approved','Removed','Paid','Failed')),
    CONSTRAINT [FK_payment_run_items_payment_run_batches_BatchId] FOREIGN KEY ([BatchId]) REFERENCES [finance].[payment_run_batches] ([Id]),
    CONSTRAINT [FK_payment_run_items_payment_runs_PaymentRunId] FOREIGN KEY ([PaymentRunId]) REFERENCES [finance].[payment_runs] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_payment_run_items_payment_schedules_PaymentScheduleId] FOREIGN KEY ([PaymentScheduleId]) REFERENCES [finance].[payment_schedules] ([Id]) ON DELETE NO ACTION
);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_org_settings_CreditControlMode' AND parent_object_id = OBJECT_ID('finance.org_settings'))
ALTER TABLE [finance].[org_settings] ADD CONSTRAINT [CK_org_settings_CreditControlMode] CHECK ([CreditControlMode] IN ('Off','Warn','Block'));");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_invoices_ToleranceRuleId' AND object_id = OBJECT_ID('finance.invoices'))
CREATE INDEX [IX_invoices_ToleranceRuleId] ON [finance].[invoices] ([ToleranceRuleId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_invoices_VendorInvoiceNo' AND object_id = OBJECT_ID('finance.invoices'))
CREATE INDEX [IX_invoices_VendorInvoiceNo] ON [finance].[invoices] ([OrganizationId], [SupplierId], [SupplierInvoiceNo]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_invoices_LineMatchResult' AND parent_object_id = OBJECT_ID('finance.invoices'))
ALTER TABLE [finance].[invoices] ADD CONSTRAINT [CK_invoices_LineMatchResult] CHECK ([LineMatchResult] IS NULL OR [LineMatchResult] IN ('Matched','PartialMatch','Mismatch'));");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_invoices_MatchType' AND parent_object_id = OBJECT_ID('finance.invoices'))
ALTER TABLE [finance].[invoices] ADD CONSTRAINT [CK_invoices_MatchType] CHECK ([MatchType] IS NULL OR [MatchType] IN ('ThreeWay','TwoWay'));");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_debit_notes_Origin' AND parent_object_id = OBJECT_ID('finance.debit_notes'))
ALTER TABLE [finance].[debit_notes] ADD CONSTRAINT [CK_debit_notes_Origin] CHECK ([Origin] IN ('SRO','MEMO'));");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_credit_hold_requests_OrganizationId_PartnerUuid_RequestedAt' AND object_id = OBJECT_ID('finance.credit_hold_requests'))
CREATE INDEX [IX_credit_hold_requests_OrganizationId_PartnerUuid_RequestedAt] ON [finance].[credit_hold_requests] ([OrganizationId], [PartnerUuid], [RequestedAt]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_credit_hold_requests_ProfileId' AND object_id = OBJECT_ID('finance.credit_hold_requests'))
CREATE INDEX [IX_credit_hold_requests_ProfileId] ON [finance].[credit_hold_requests] ([ProfileId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_credit_hold_requests_Uuid' AND object_id = OBJECT_ID('finance.credit_hold_requests'))
CREATE UNIQUE INDEX [IX_credit_hold_requests_Uuid] ON [finance].[credit_hold_requests] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_customer_credit_applications_CreditNoteId' AND object_id = OBJECT_ID('finance.customer_credit_applications'))
CREATE INDEX [IX_customer_credit_applications_CreditNoteId] ON [finance].[customer_credit_applications] ([CreditNoteId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_customer_credit_applications_InvoiceId' AND object_id = OBJECT_ID('finance.customer_credit_applications'))
CREATE INDEX [IX_customer_credit_applications_InvoiceId] ON [finance].[customer_credit_applications] ([InvoiceId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_customer_credit_applications_OrganizationId_CreditNoteUuid' AND object_id = OBJECT_ID('finance.customer_credit_applications'))
CREATE INDEX [IX_customer_credit_applications_OrganizationId_CreditNoteUuid] ON [finance].[customer_credit_applications] ([OrganizationId], [CreditNoteUuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_customer_credit_applications_OrganizationId_InvoiceUuid' AND object_id = OBJECT_ID('finance.customer_credit_applications'))
CREATE INDEX [IX_customer_credit_applications_OrganizationId_InvoiceUuid] ON [finance].[customer_credit_applications] ([OrganizationId], [InvoiceUuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_customer_credit_applications_Uuid' AND object_id = OBJECT_ID('finance.customer_credit_applications'))
CREATE UNIQUE INDEX [IX_customer_credit_applications_Uuid] ON [finance].[customer_credit_applications] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_dunning_levels_DunningProcedureId_LevelNumber' AND object_id = OBJECT_ID('finance.dunning_levels'))
CREATE UNIQUE INDEX [IX_dunning_levels_DunningProcedureId_LevelNumber] ON [finance].[dunning_levels] ([DunningProcedureId], [LevelNumber]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_dunning_levels_OrganizationId' AND object_id = OBJECT_ID('finance.dunning_levels'))
CREATE INDEX [IX_dunning_levels_OrganizationId] ON [finance].[dunning_levels] ([OrganizationId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_dunning_levels_Uuid' AND object_id = OBJECT_ID('finance.dunning_levels'))
CREATE UNIQUE INDEX [IX_dunning_levels_Uuid] ON [finance].[dunning_levels] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_dunning_procedures_OrganizationId_Code' AND object_id = OBJECT_ID('finance.dunning_procedures'))
CREATE UNIQUE INDEX [IX_dunning_procedures_OrganizationId_Code] ON [finance].[dunning_procedures] ([OrganizationId], [Code]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_dunning_procedures_Uuid' AND object_id = OBJECT_ID('finance.dunning_procedures'))
CREATE UNIQUE INDEX [IX_dunning_procedures_Uuid] ON [finance].[dunning_procedures] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_dunning_procedures_Default' AND object_id = OBJECT_ID('finance.dunning_procedures'))
CREATE UNIQUE INDEX [UX_dunning_procedures_Default] ON [finance].[dunning_procedures] ([OrganizationId]) WHERE [IsDefault] = 1;");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_dunning_run_items_DunningRunId' AND object_id = OBJECT_ID('finance.dunning_run_items'))
CREATE INDEX [IX_dunning_run_items_DunningRunId] ON [finance].[dunning_run_items] ([DunningRunId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_dunning_run_items_OrganizationId_PartnerUuid' AND object_id = OBJECT_ID('finance.dunning_run_items'))
CREATE INDEX [IX_dunning_run_items_OrganizationId_PartnerUuid] ON [finance].[dunning_run_items] ([OrganizationId], [PartnerUuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_dunning_run_items_Uuid' AND object_id = OBJECT_ID('finance.dunning_run_items'))
CREATE UNIQUE INDEX [IX_dunning_run_items_Uuid] ON [finance].[dunning_run_items] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_dunning_runs_OrganizationId_RunNumber' AND object_id = OBJECT_ID('finance.dunning_runs'))
CREATE UNIQUE INDEX [IX_dunning_runs_OrganizationId_RunNumber] ON [finance].[dunning_runs] ([OrganizationId], [RunNumber]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_dunning_runs_Uuid' AND object_id = OBJECT_ID('finance.dunning_runs'))
CREATE UNIQUE INDEX [IX_dunning_runs_Uuid] ON [finance].[dunning_runs] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_dunning_runs_OrgDay' AND object_id = OBJECT_ID('finance.dunning_runs'))
CREATE UNIQUE INDEX [UX_dunning_runs_OrgDay] ON [finance].[dunning_runs] ([OrganizationId], [RunDate]) WHERE [Status] <> 'Cancelled';");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_early_payment_terms_OrganizationId_Code' AND object_id = OBJECT_ID('finance.early_payment_terms'))
CREATE UNIQUE INDEX [IX_early_payment_terms_OrganizationId_Code] ON [finance].[early_payment_terms] ([OrganizationId], [Code]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_early_payment_terms_Uuid' AND object_id = OBJECT_ID('finance.early_payment_terms'))
CREATE UNIQUE INDEX [IX_early_payment_terms_Uuid] ON [finance].[early_payment_terms] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_invoice_match_lines_InvoiceId' AND object_id = OBJECT_ID('finance.invoice_match_lines'))
CREATE INDEX [IX_invoice_match_lines_InvoiceId] ON [finance].[invoice_match_lines] ([InvoiceId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_invoice_match_lines_OrganizationId' AND object_id = OBJECT_ID('finance.invoice_match_lines'))
CREATE INDEX [IX_invoice_match_lines_OrganizationId] ON [finance].[invoice_match_lines] ([OrganizationId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_invoice_match_lines_Uuid' AND object_id = OBJECT_ID('finance.invoice_match_lines'))
CREATE UNIQUE INDEX [IX_invoice_match_lines_Uuid] ON [finance].[invoice_match_lines] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_match_tolerance_rules_OrganizationId_Name' AND object_id = OBJECT_ID('finance.match_tolerance_rules'))
CREATE UNIQUE INDEX [IX_match_tolerance_rules_OrganizationId_Name] ON [finance].[match_tolerance_rules] ([OrganizationId], [Name]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_match_tolerance_rules_Uuid' AND object_id = OBJECT_ID('finance.match_tolerance_rules'))
CREATE UNIQUE INDEX [IX_match_tolerance_rules_Uuid] ON [finance].[match_tolerance_rules] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_match_tolerance_rules_Default' AND object_id = OBJECT_ID('finance.match_tolerance_rules'))
CREATE UNIQUE INDEX [UX_match_tolerance_rules_Default] ON [finance].[match_tolerance_rules] ([OrganizationId]) WHERE [IsDefault] = 1;");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_match_tolerance_rules_Vendor' AND object_id = OBJECT_ID('finance.match_tolerance_rules'))
CREATE UNIQUE INDEX [UX_match_tolerance_rules_Vendor] ON [finance].[match_tolerance_rules] ([OrganizationId], [VendorUuid]) WHERE [VendorUuid] IS NOT NULL AND [IsActive] = 1;");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_partner_financial_profiles_CreditHold' AND object_id = OBJECT_ID('finance.partner_financial_profiles'))
CREATE INDEX [IX_partner_financial_profiles_CreditHold] ON [finance].[partner_financial_profiles] ([OrganizationId]) WHERE [CreditHold] = 1;");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_partner_financial_profiles_DunningProcedureId' AND object_id = OBJECT_ID('finance.partner_financial_profiles'))
CREATE INDEX [IX_partner_financial_profiles_DunningProcedureId] ON [finance].[partner_financial_profiles] ([DunningProcedureId]) WHERE [DunningProcedureId] IS NOT NULL;");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_partner_financial_profiles_EarlyPaymentTermId' AND object_id = OBJECT_ID('finance.partner_financial_profiles'))
CREATE INDEX [IX_partner_financial_profiles_EarlyPaymentTermId] ON [finance].[partner_financial_profiles] ([EarlyPaymentTermId]) WHERE [EarlyPaymentTermId] IS NOT NULL;");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_partner_financial_profiles_OrganizationId_PartnerRole' AND object_id = OBJECT_ID('finance.partner_financial_profiles'))
CREATE INDEX [IX_partner_financial_profiles_OrganizationId_PartnerRole] ON [finance].[partner_financial_profiles] ([OrganizationId], [PartnerRole]) INCLUDE ([CreditHold], [RiskTier]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_partner_financial_profiles_OrganizationId_PartnerUuid_PartnerRole' AND object_id = OBJECT_ID('finance.partner_financial_profiles'))
CREATE UNIQUE INDEX [IX_partner_financial_profiles_OrganizationId_PartnerUuid_PartnerRole] ON [finance].[partner_financial_profiles] ([OrganizationId], [PartnerUuid], [PartnerRole]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_partner_financial_profiles_Uuid' AND object_id = OBJECT_ID('finance.partner_financial_profiles'))
CREATE UNIQUE INDEX [IX_partner_financial_profiles_Uuid] ON [finance].[partner_financial_profiles] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_payment_run_batches_OrganizationId' AND object_id = OBJECT_ID('finance.payment_run_batches'))
CREATE INDEX [IX_payment_run_batches_OrganizationId] ON [finance].[payment_run_batches] ([OrganizationId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_payment_run_batches_PaymentRunId_VendorUuid_CurrencyCode_PaymentMethod' AND object_id = OBJECT_ID('finance.payment_run_batches'))
CREATE UNIQUE INDEX [IX_payment_run_batches_PaymentRunId_VendorUuid_CurrencyCode_PaymentMethod] ON [finance].[payment_run_batches] ([PaymentRunId], [VendorUuid], [CurrencyCode], [PaymentMethod]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_payment_run_batches_Uuid' AND object_id = OBJECT_ID('finance.payment_run_batches'))
CREATE UNIQUE INDEX [IX_payment_run_batches_Uuid] ON [finance].[payment_run_batches] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_payment_run_items_BatchId' AND object_id = OBJECT_ID('finance.payment_run_items'))
CREATE INDEX [IX_payment_run_items_BatchId] ON [finance].[payment_run_items] ([BatchId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_payment_run_items_OrganizationId_VendorUuid' AND object_id = OBJECT_ID('finance.payment_run_items'))
CREATE INDEX [IX_payment_run_items_OrganizationId_VendorUuid] ON [finance].[payment_run_items] ([OrganizationId], [VendorUuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_payment_run_items_PaymentRunId_PaymentScheduleId' AND object_id = OBJECT_ID('finance.payment_run_items'))
CREATE UNIQUE INDEX [IX_payment_run_items_PaymentRunId_PaymentScheduleId] ON [finance].[payment_run_items] ([PaymentRunId], [PaymentScheduleId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_payment_run_items_PaymentScheduleId' AND object_id = OBJECT_ID('finance.payment_run_items'))
CREATE INDEX [IX_payment_run_items_PaymentScheduleId] ON [finance].[payment_run_items] ([PaymentScheduleId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_payment_run_items_Uuid' AND object_id = OBJECT_ID('finance.payment_run_items'))
CREATE UNIQUE INDEX [IX_payment_run_items_Uuid] ON [finance].[payment_run_items] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_payment_runs_OrganizationId_RunDate' AND object_id = OBJECT_ID('finance.payment_runs'))
CREATE INDEX [IX_payment_runs_OrganizationId_RunDate] ON [finance].[payment_runs] ([OrganizationId], [RunDate]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_payment_runs_OrganizationId_RunNumber' AND object_id = OBJECT_ID('finance.payment_runs'))
CREATE UNIQUE INDEX [IX_payment_runs_OrganizationId_RunNumber] ON [finance].[payment_runs] ([OrganizationId], [RunNumber]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_payment_runs_OrganizationId_Status' AND object_id = OBJECT_ID('finance.payment_runs'))
CREATE INDEX [IX_payment_runs_OrganizationId_Status] ON [finance].[payment_runs] ([OrganizationId], [Status]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_payment_runs_Uuid' AND object_id = OBJECT_ID('finance.payment_runs'))
CREATE UNIQUE INDEX [IX_payment_runs_Uuid] ON [finance].[payment_runs] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_payment_schedules_EarlyPaymentTermId' AND object_id = OBJECT_ID('finance.payment_schedules'))
CREATE INDEX [IX_payment_schedules_EarlyPaymentTermId] ON [finance].[payment_schedules] ([EarlyPaymentTermId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_payment_schedules_InvoiceId' AND object_id = OBJECT_ID('finance.payment_schedules'))
CREATE UNIQUE INDEX [IX_payment_schedules_InvoiceId] ON [finance].[payment_schedules] ([InvoiceId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_payment_schedules_OrganizationId_DiscountDate' AND object_id = OBJECT_ID('finance.payment_schedules'))
CREATE INDEX [IX_payment_schedules_OrganizationId_DiscountDate] ON [finance].[payment_schedules] ([OrganizationId], [DiscountDate]) WHERE [DiscountDate] IS NOT NULL;");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_payment_schedules_OrganizationId_Status_DueDate' AND object_id = OBJECT_ID('finance.payment_schedules'))
CREATE INDEX [IX_payment_schedules_OrganizationId_Status_DueDate] ON [finance].[payment_schedules] ([OrganizationId], [Status], [DueDate]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_payment_schedules_OrganizationId_VendorUuid_Status' AND object_id = OBJECT_ID('finance.payment_schedules'))
CREATE INDEX [IX_payment_schedules_OrganizationId_VendorUuid_Status] ON [finance].[payment_schedules] ([OrganizationId], [VendorUuid], [Status]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_payment_schedules_PaidInRunId' AND object_id = OBJECT_ID('finance.payment_schedules'))
CREATE INDEX [IX_payment_schedules_PaidInRunId] ON [finance].[payment_schedules] ([PaidInRunId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_payment_schedules_ReservedByRunId' AND object_id = OBJECT_ID('finance.payment_schedules'))
CREATE INDEX [IX_payment_schedules_ReservedByRunId] ON [finance].[payment_schedules] ([ReservedByRunId]) WHERE [ReservedByRunId] IS NOT NULL;");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_payment_schedules_Uuid' AND object_id = OBJECT_ID('finance.payment_schedules'))
CREATE UNIQUE INDEX [IX_payment_schedules_Uuid] ON [finance].[payment_schedules] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_statement_log_OrganizationId_PartnerUuid_PartnerRole_GeneratedAt' AND object_id = OBJECT_ID('finance.statement_log'))
CREATE INDEX [IX_statement_log_OrganizationId_PartnerUuid_PartnerRole_GeneratedAt] ON [finance].[statement_log] ([OrganizationId], [PartnerUuid], [PartnerRole], [GeneratedAt]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_statement_log_Uuid' AND object_id = OBJECT_ID('finance.statement_log'))
CREATE UNIQUE INDEX [IX_statement_log_Uuid] ON [finance].[statement_log] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_supplier_note_applications_InvoiceId' AND object_id = OBJECT_ID('finance.supplier_note_applications'))
CREATE INDEX [IX_supplier_note_applications_InvoiceId] ON [finance].[supplier_note_applications] ([InvoiceId]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_supplier_note_applications_OrganizationId_InvoiceUuid' AND object_id = OBJECT_ID('finance.supplier_note_applications'))
CREATE INDEX [IX_supplier_note_applications_OrganizationId_InvoiceUuid] ON [finance].[supplier_note_applications] ([OrganizationId], [InvoiceUuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_supplier_note_applications_OrganizationId_NoteType_NoteUuid' AND object_id = OBJECT_ID('finance.supplier_note_applications'))
CREATE INDEX [IX_supplier_note_applications_OrganizationId_NoteType_NoteUuid] ON [finance].[supplier_note_applications] ([OrganizationId], [NoteType], [NoteUuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_supplier_note_applications_Uuid' AND object_id = OBJECT_ID('finance.supplier_note_applications'))
CREATE UNIQUE INDEX [IX_supplier_note_applications_Uuid] ON [finance].[supplier_note_applications] ([Uuid]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_invoices_match_tolerance_rules_ToleranceRuleId' AND parent_object_id = OBJECT_ID('finance.invoices'))
ALTER TABLE [finance].[invoices] ADD CONSTRAINT [FK_invoices_match_tolerance_rules_ToleranceRuleId] FOREIGN KEY ([ToleranceRuleId]) REFERENCES [finance].[match_tolerance_rules] ([Id]) ON DELETE NO ACTION;");

            // ── RPT-1 / RPT-2: aging and open-item lookups over the existing documents (plain guarded CREATE INDEX) ────────
            // These two indexes sit on EXISTING tables that older migrations alter. Only columns no migration alters or drops are in them
            // (invoices.ExchangeRate — ALTERed by A35 — and ReversedAt — dropped by SAP's Down — are left out). A FUTURE migration that
            // alters or drops any key/INCLUDE column of IX_customer_ledger_OrganizationId_ReferenceId or IX_invoices_ApAging must drop the
            // index (guarded) before the ALTER and recreate it after, or the ALTER fails with 'The index … is dependent on column …'.
            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_customer_ledger_OrganizationId_ReferenceId' AND object_id = OBJECT_ID('finance.customer_ledger'))
CREATE INDEX [IX_customer_ledger_OrganizationId_ReferenceId] ON [finance].[customer_ledger] ([OrganizationId], [ReferenceId]) INCLUDE ([EntryType], [ReferenceType], [EntryDate]);");

            migrationBuilder.Sql(@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_invoices_ApAging' AND object_id = OBJECT_ID('finance.invoices'))
CREATE INDEX [IX_invoices_ApAging] ON [finance].[invoices] ([OrganizationId], [MatchStatus], [SupplierId]) INCLUDE ([TotalAmount], [PaidAmount], [DueDate], [InvoiceDate], [ApprovedAt], [Currency], [IsDelete]);");

            // ── D-16 seeds: every organization gets the STANDARD dunning procedure and the DEFAULT tolerance rule ──────────
            migrationBuilder.Sql(SeedProceduresSql());
            migrationBuilder.Sql(SeedLevelsSql());
            migrationBuilder.Sql(SeedToleranceSql());

            // ── D-8 backfill: a schedule for every approved, live supplier invoice still owing ─────────────────────────────
            migrationBuilder.Sql(BackfillSchedulesSql);
        }

        /// <summary>An N'…' literal on one line: EF re-joins a Sql() batch's lines with CRLF, so a newline is spelled NCHAR(10).</summary>
        private static string N(string s) => "N'" + s.Replace("'", "''").Replace("\r", "").Replace("\n", "' + NCHAR(10) + N'") + "'";

        /// <summary>Organizations come from tenant.Organizations (Tenancy migrates first); absent table = nothing to seed.</summary>
        internal static string SeedProceduresSql() => $@"IF OBJECT_ID('tenant.Organizations') IS NOT NULL
INSERT INTO [finance].[dunning_procedures] ([Uuid], [OrganizationId], [Code], [Name], [Description], [IsDefault], [IsActive], [GraceDays], [MinimumAmount], [BlockSalesAtLevel], [CreatedAt], [CreatedByName])
SELECT NEWID(), o.[Id], {N(ArApSeedData.ProcedureCode)}, {N(ArApSeedData.ProcedureName)}, {N(ArApSeedData.ProcedureDescription)}, 1, 1, 0, 0, {ArApSeedData.BlockSalesAtLevel}, SYSUTCDATETIME(), N'System'
FROM [tenant].[Organizations] o
WHERE NOT EXISTS (SELECT 1 FROM [finance].[dunning_procedures] p WHERE p.[OrganizationId] = o.[Id] AND (p.[IsDefault] = 1 OR p.[Code] = {N(ArApSeedData.ProcedureCode)}));";

        /// <summary>The four levels, for every STANDARD procedure that has none yet.</summary>
        internal static string SeedLevelsSql()
        {
            var values = string.Join(",\r\n    ", ArApSeedData.Levels.Select(l =>
                $"({l.Number}, {l.DaysAfterPrevious}, {N(l.Subject)}, {N(l.Body)}, {(l.IncludeStatement ? 1 : 0)}, {(l.EscalationNote is null ? "NULL" : N(l.EscalationNote))})"));
            return $@"INSERT INTO [finance].[dunning_levels] ([Uuid], [OrganizationId], [DunningProcedureId], [LevelNumber], [DaysAfterPrevious], [SubjectTemplate], [BodyTemplate], [IncludeInvoiceList], [IncludeStatement], [ChargeAmount], [ActionType], [EscalationNote])
SELECT NEWID(), p.[OrganizationId], p.[Id], v.[LevelNumber], v.[Days], v.[Subject], v.[Body], 1, v.[IncludeStatement], NULL, N'Email', v.[Note]
FROM [finance].[dunning_procedures] p
CROSS JOIN (VALUES
    {values}
) v ([LevelNumber], [Days], [Subject], [Body], [IncludeStatement], [Note])
WHERE p.[Code] = {N(ArApSeedData.ProcedureCode)}
  AND NOT EXISTS (SELECT 1 FROM [finance].[dunning_levels] l WHERE l.[DunningProcedureId] = p.[Id]);";
        }

        internal static string SeedToleranceSql() => $@"IF OBJECT_ID('tenant.Organizations') IS NOT NULL
INSERT INTO [finance].[match_tolerance_rules] ([Uuid], [OrganizationId], [Name], [VendorUuid], [VendorName], [PriceTolerancePercent], [PriceToleranceAmount], [QuantityTolerancePercent], [TotalTolerancePercent], [TotalToleranceAmount], [AutoApproveWithin], [IsDefault], [IsActive], [CreatedAt], [CreatedByName])
SELECT NEWID(), o.[Id], {N(ArApSeedData.ToleranceName)}, NULL, NULL, {ArApSeedData.PriceTolerancePercent.ToString(System.Globalization.CultureInfo.InvariantCulture)}, 0, 0, 0, 0, 0, 1, 1, SYSUTCDATETIME(), N'System'
FROM [tenant].[Organizations] o
WHERE NOT EXISTS (SELECT 1 FROM [finance].[match_tolerance_rules] r WHERE r.[OrganizationId] = o.[Id] AND (r.[IsDefault] = 1 OR r.[Name] = {N(ArApSeedData.ToleranceName)}));";

        /// <summary>
        /// D-8 — remaining = TotalAmount − PaidAmount (posted supplier payments) − legacy single-invoice payments not reversed
        /// (InvoiceSettlement without the unposted lines, which are promises, not payments; a NULL status counts, as EF's != does). REV-32: NULL-safe on SupplierName / InvoiceDate / DueDate / PaymentStatus so a drifted row never fails the startup migration. Approved (S-7 statuses trimmed),
        /// not deleted, not reversed, not fully paid, still owing; one row per invoice.
        /// </summary>
        internal const string BackfillSchedulesSql = @"INSERT INTO [finance].[payment_schedules] ([Uuid], [OrganizationId], [InvoiceId], [InvoiceUuid], [InvoiceNumber], [SupplierInvoiceNo], [VendorUuid], [VendorName], [CurrencyCode], [InvoiceDate], [DueDate], [OriginalAmount], [AmountRemaining], [PaymentPriority], [PaymentBlock], [Status], [CreatedAt])
SELECT NEWID(), i.[OrganizationId], i.[Id], i.[UUID], i.[InvoiceNumber], i.[SupplierInvoiceNo], i.[SupplierId], ISNULL(i.[SupplierName], N''),
       ISNULL(NULLIF(LTRIM(RTRIM(i.[Currency])), N''), N'PKR'), CAST(ISNULL(i.[InvoiceDate], i.[CreatedDate]) AS date), CAST(ISNULL(i.[DueDate], ISNULL(i.[InvoiceDate], i.[CreatedDate])) AS date),
       i.[TotalAmount], r.[Remaining], N'Normal', 0,
       CASE WHEN r.[Remaining] < i.[TotalAmount] THEN N'PartiallyPaid' ELSE N'Open' END, SYSUTCDATETIME()
FROM [finance].[invoices] i
CROSS APPLY (SELECT i.[TotalAmount] - i.[PaidAmount]
                    - ISNULL((SELECT SUM(p.[AmountPaid]) FROM [finance].[payments] p
                              WHERE p.[InvoiceId] = i.[Id] AND p.[IsDelete] = 0 AND (p.[Status] IS NULL OR LTRIM(RTRIM(p.[Status])) <> N'Reversed')), 0) AS [Remaining]) r
WHERE LTRIM(RTRIM(i.[MatchStatus])) = N'Approved'
  AND i.[IsDelete] = 0
  AND i.[ReversedAt] IS NULL
  AND ISNULL(LTRIM(RTRIM(i.[PaymentStatus])), N'') NOT IN (N'Paid', N'FULLY_PAID')
  AND r.[Remaining] > 0
  AND NOT EXISTS (SELECT 1 FROM [finance].[payment_schedules] s WHERE s.[InvoiceId] = i.[Id]);";
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_customer_ledger_OrganizationId_ReferenceId' AND object_id = OBJECT_ID('finance.customer_ledger'))
DROP INDEX [IX_customer_ledger_OrganizationId_ReferenceId] ON [finance].[customer_ledger];");
            migrationBuilder.Sql(@"IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_invoices_ApAging' AND object_id = OBJECT_ID('finance.invoices'))
DROP INDEX [IX_invoices_ApAging] ON [finance].[invoices];");
            migrationBuilder.DropForeignKey(
                name: "FK_invoices_match_tolerance_rules_ToleranceRuleId",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropTable(
                name: "credit_hold_requests",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "customer_credit_applications",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "dunning_levels",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "dunning_run_items",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "invoice_match_lines",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "match_tolerance_rules",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "payment_run_items",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "statement_log",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "supplier_note_applications",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "partner_financial_profiles",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "dunning_runs",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "payment_run_batches",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "payment_schedules",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "dunning_procedures",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "early_payment_terms",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "payment_runs",
                schema: "finance");

            migrationBuilder.DropCheckConstraint(
                name: "CK_org_settings_CreditControlMode",
                schema: "finance",
                table: "org_settings");

            migrationBuilder.DropIndex(
                name: "IX_invoices_ToleranceRuleId",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "IX_invoices_VendorInvoiceNo",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropCheckConstraint(
                name: "CK_invoices_LineMatchResult",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropCheckConstraint(
                name: "CK_invoices_MatchType",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropCheckConstraint(
                name: "CK_debit_notes_Origin",
                schema: "finance",
                table: "debit_notes");

            migrationBuilder.DropColumn(
                name: "DiscountAmount",
                schema: "finance",
                table: "supplier_payment_lines");

            migrationBuilder.DropColumn(
                name: "CreditControlMode",
                schema: "finance",
                table: "org_settings");

            migrationBuilder.DropColumn(
                name: "AutoApproved",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "LineMatchResult",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "MatchOverriddenAt",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "MatchOverriddenBy",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "MatchOverriddenByName",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "MatchOverrideReason",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "MatchType",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "MatchVariancePercent",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "MatchedAt",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "MatchedBy",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "MatchedByName",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "ToleranceRuleId",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "WithinTolerance",
                schema: "finance",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "Origin",
                schema: "finance",
                table: "debit_notes");
        }
    }
}
