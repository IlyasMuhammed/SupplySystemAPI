using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Finance.ArAp;
using SMS.Modules.Finance.Domain;

namespace SMS.Modules.Finance.Data.Maps;

// Addendum 48 (CORE) — finance.* tables of the AR & AP sub-ledgers (API-CONTRACT §11). Created by the guarded migration
// A48_ArApSubledgers. Money decimal(18,2) like the Finance documents (D-CORE-2), percents decimal(9,4), quantities and unit
// prices decimal(18,4). CHECK constraints are named CK_{table}_{Column} and list the ArApEnums values.

internal static class ArApColumn
{
    internal const string Money    = "decimal(18,2)";
    internal const string Percent  = "decimal(9,4)";
    internal const string Quantity = "decimal(18,4)";
    internal const string Name     = "nvarchar(200)";

    /// <summary>"[Column] IN ('A','B')".</summary>
    internal static string In(string column, IEnumerable<string> values) =>
        $"[{column}] IN ({string.Join(",", values.Select(v => $"'{v}'"))})";
}

internal sealed class PartnerFinancialProfileMap : IEntityTypeConfiguration<PartnerFinancialProfile>
{
    public void Configure(EntityTypeBuilder<PartnerFinancialProfile> b)
    {
        b.ToTable("partner_financial_profiles", t =>
        {
            t.HasCheckConstraint("CK_partner_financial_profiles_PartnerRole", ArApColumn.In("PartnerRole", ArApEnums.PartnerRole.All));
            t.HasCheckConstraint("CK_partner_financial_profiles_RiskTier", ArApColumn.In("RiskTier", ArApEnums.RiskTier.All));
            t.HasCheckConstraint("CK_partner_financial_profiles_PaymentPriority", ArApColumn.In("PaymentPriority", ArApEnums.PaymentPriority.All));
            t.HasCheckConstraint("CK_partner_financial_profiles_DunningLevel", "[CurrentDunningLevel] >= 0");
            t.HasCheckConstraint("CK_partner_financial_profiles_TemporaryCreditLimit", "[TemporaryCreditLimit] IS NULL OR [TemporaryCreditLimit] > 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.PartnerUuid, x.PartnerRole }).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.PartnerRole }).IncludeProperties(x => new { x.CreditHold, x.RiskTier });
        b.HasIndex(x => x.OrganizationId, "IX_partner_financial_profiles_CreditHold").HasFilter("[CreditHold] = 1");

        b.Property(x => x.PartnerRole).HasMaxLength(10).IsRequired();
        b.Property(x => x.CreditHoldReason).HasMaxLength(500);
        b.Property(x => x.CreditHoldByName).HasMaxLength(200);
        b.Property(x => x.TemporaryCreditLimit).HasColumnType(ArApColumn.Money);
        b.Property(x => x.TemporaryCreditExpiry).HasColumnType("date");
        b.Property(x => x.RiskTier).HasMaxLength(10).IsRequired().HasDefaultValue(ArApEnums.RiskTier.Medium);
        b.Property(x => x.LastDunningDate).HasColumnType("date");
        b.Property(x => x.DefaultPaymentMethod).HasMaxLength(30);
        b.Property(x => x.PaymentPriority).HasMaxLength(10).IsRequired().HasDefaultValue(ArApEnums.PaymentPriority.Normal);
        b.Property(x => x.PreferredPaymentMethod).HasMaxLength(30);
        b.Property(x => x.LastPaymentDate).HasColumnType("date");
        b.Property(x => x.TotalOverdueAmount).HasColumnType(ArApColumn.Money);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.CreatedByName).HasMaxLength(200);
        b.Property(x => x.ModifiedByName).HasMaxLength(200);
        b.Property(x => x.RowVersion).IsRowVersion();

        b.HasOne(x => x.DunningProcedure).WithMany().HasForeignKey(x => x.DunningProcedureId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.DunningProcedureId).HasFilter("[DunningProcedureId] IS NOT NULL");
        b.HasOne(x => x.EarlyPaymentTerm).WithMany().HasForeignKey(x => x.EarlyPaymentTermId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.EarlyPaymentTermId).HasFilter("[EarlyPaymentTermId] IS NOT NULL");
    }
}

internal sealed class CreditHoldRequestMap : IEntityTypeConfiguration<CreditHoldRequest>
{
    public void Configure(EntityTypeBuilder<CreditHoldRequest> b)
    {
        b.ToTable("credit_hold_requests", t =>
        {
            t.HasCheckConstraint("CK_credit_hold_requests_Action", ArApColumn.In("Action", ArApEnums.CreditHoldAction.All));
            t.HasCheckConstraint("CK_credit_hold_requests_Status", ArApColumn.In("Status", ArApEnums.CreditHoldRequestStatus.All));
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.PartnerUuid, x.RequestedAt });

        b.Property(x => x.PartnerName).HasMaxLength(200);
        b.Property(x => x.Action).HasMaxLength(20).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        b.Property(x => x.PreviousValue).HasMaxLength(200);
        b.Property(x => x.NewValue).HasMaxLength(200);
        b.Property(x => x.Amount).HasColumnType(ArApColumn.Money);
        b.Property(x => x.ExpiryDate).HasColumnType("date");
        b.Property(x => x.SourceDocumentType).HasMaxLength(30);
        b.Property(x => x.SourceDocumentNumber).HasMaxLength(50);
        b.Property(x => x.RequestedByName).HasMaxLength(200);
        b.Property(x => x.ApprovedByName).HasMaxLength(200);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired().HasDefaultValue(ArApEnums.CreditHoldRequestStatus.Completed);

        b.HasOne(x => x.Profile).WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StatementLogMap : IEntityTypeConfiguration<StatementLog>
{
    public void Configure(EntityTypeBuilder<StatementLog> b)
    {
        b.ToTable("statement_log", t =>
        {
            t.HasCheckConstraint("CK_statement_log_PartnerRole", ArApColumn.In("PartnerRole", ArApEnums.PartnerRole.All));
            t.HasCheckConstraint("CK_statement_log_StatementType", ArApColumn.In("StatementType", ArApEnums.StatementType.All));
            t.HasCheckConstraint("CK_statement_log_EmailStatus", ArApColumn.In("EmailStatus", ArApEnums.StatementEmailStatus.All));
            t.HasCheckConstraint("CK_statement_log_Dates", "[ToDate] >= [FromDate]");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.PartnerUuid, x.PartnerRole, x.GeneratedAt });

        b.Property(x => x.PartnerName).HasMaxLength(200);
        b.Property(x => x.PartnerRole).HasMaxLength(10).IsRequired();
        b.Property(x => x.StatementType).HasMaxLength(20).IsRequired();
        b.Property(x => x.FromDate).HasColumnType("date");
        b.Property(x => x.ToDate).HasColumnType("date");
        b.Property(x => x.CurrencyCode).HasMaxLength(10);
        b.Property(x => x.OpeningBalance).HasColumnType(ArApColumn.Money);
        b.Property(x => x.ClosingBalance).HasColumnType(ArApColumn.Money);
        b.Property(x => x.Format).HasMaxLength(10).IsRequired().HasDefaultValue("PDF");
        b.Property(x => x.EmailStatus).HasMaxLength(10).IsRequired().HasDefaultValue(ArApEnums.StatementEmailStatus.NotSent);
        b.Property(x => x.EmailedTo).HasMaxLength(500);
        b.Property(x => x.EmailError).HasMaxLength(500);
        b.Property(x => x.GeneratedByName).HasMaxLength(200);
    }
}

internal sealed class DunningProcedureMap : IEntityTypeConfiguration<DunningProcedure>
{
    public void Configure(EntityTypeBuilder<DunningProcedure> b)
    {
        b.ToTable("dunning_procedures", t =>
        {
            t.HasCheckConstraint("CK_dunning_procedures_GraceDays", "[GraceDays] >= 0");
            t.HasCheckConstraint("CK_dunning_procedures_MinimumAmount", "[MinimumAmount] >= 0");
            t.HasCheckConstraint("CK_dunning_procedures_BlockSalesAtLevel", "[BlockSalesAtLevel] IS NULL OR [BlockSalesAtLevel] >= 1");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique();
        // One default per organization.
        b.HasIndex(x => x.OrganizationId, "UX_dunning_procedures_Default").IsUnique().HasFilter("[IsDefault] = 1");

        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.IsActive).HasDefaultValue(true);
        b.Property(x => x.MinimumAmount).HasColumnType(ArApColumn.Money);
        b.Property(x => x.CreatedByName).HasMaxLength(200);
        b.Property(x => x.ModifiedByName).HasMaxLength(200);

        b.HasMany(x => x.Levels).WithOne(x => x.DunningProcedure).HasForeignKey(x => x.DunningProcedureId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DunningLevelMap : IEntityTypeConfiguration<DunningLevel>
{
    public void Configure(EntityTypeBuilder<DunningLevel> b)
    {
        b.ToTable("dunning_levels", t =>
        {
            t.HasCheckConstraint("CK_dunning_levels_LevelNumber", "[LevelNumber] >= 1");
            t.HasCheckConstraint("CK_dunning_levels_DaysAfterPrevious", "[DaysAfterPrevious] >= 0");
            t.HasCheckConstraint("CK_dunning_levels_ChargeAmount", "[ChargeAmount] IS NULL OR [ChargeAmount] >= 0");
            t.HasCheckConstraint("CK_dunning_levels_ActionType", ArApColumn.In("ActionType", ArApEnums.DunningActionType.All));
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
        b.HasIndex(x => new { x.DunningProcedureId, x.LevelNumber }).IsUnique();

        b.Property(x => x.SubjectTemplate).HasMaxLength(200).IsRequired();
        b.Property(x => x.BodyTemplate).IsRequired();
        b.Property(x => x.IncludeInvoiceList).HasDefaultValue(true);
        b.Property(x => x.ChargeAmount).HasColumnType(ArApColumn.Money);
        b.Property(x => x.ActionType).HasMaxLength(20).IsRequired().HasDefaultValue(ArApEnums.DunningActionType.Email);
        b.Property(x => x.EscalationNote).HasMaxLength(500);
    }
}

internal sealed class DunningRunMap : IEntityTypeConfiguration<DunningRun>
{
    public void Configure(EntityTypeBuilder<DunningRun> b)
    {
        b.ToTable("dunning_runs", t =>
            t.HasCheckConstraint("CK_dunning_runs_Status", ArApColumn.In("Status", ArApEnums.DunningRunStatus.All)));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.RunNumber }).IsUnique();
        // BR-DN-010 — one live run per organization per day, under a race too.
        b.HasIndex(x => new { x.OrganizationId, x.RunDate }, "UX_dunning_runs_OrgDay").IsUnique()
         .HasFilter("[Status] <> 'Cancelled'");

        b.Property(x => x.RunNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.RunDate).HasColumnType("date");
        b.Property(x => x.AsOfDate).HasColumnType("date");
        b.Property(x => x.Status).HasMaxLength(20).IsRequired().HasDefaultValue(ArApEnums.DunningRunStatus.Draft);
        b.Property(x => x.CurrencyCode).HasMaxLength(10);
        b.Property(x => x.TotalAmount).HasColumnType(ArApColumn.Money);
        b.Property(x => x.CreatedByName).HasMaxLength(200);
        b.Property(x => x.ExecutedByName).HasMaxLength(200);
        b.Property(x => x.CancelledByName).HasMaxLength(200);
        b.Property(x => x.CancelReason).HasMaxLength(500);

        b.HasMany(x => x.Items).WithOne(x => x.DunningRun).HasForeignKey(x => x.DunningRunId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DunningRunItemMap : IEntityTypeConfiguration<DunningRunItem>
{
    public void Configure(EntityTypeBuilder<DunningRunItem> b)
    {
        b.ToTable("dunning_run_items", t =>
        {
            t.HasCheckConstraint("CK_dunning_run_items_Status", ArApColumn.In("Status", ArApEnums.DunningRunItemStatus.All));
            t.HasCheckConstraint("CK_dunning_run_items_ActionType", ArApColumn.In("ActionType", ArApEnums.DunningActionType.All));
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.PartnerUuid });
        b.HasIndex(x => x.DunningRunId);

        b.Property(x => x.PartnerName).HasMaxLength(200).IsRequired();
        b.Property(x => x.PartnerCode).HasMaxLength(50);
        b.Property(x => x.OverdueAmount).HasColumnType(ArApColumn.Money);
        b.Property(x => x.CurrencySummary).HasMaxLength(500);
        b.Property(x => x.OldestDueDate).HasColumnType("date");
        b.Property(x => x.ActionType).HasMaxLength(20).IsRequired().HasDefaultValue(ArApEnums.DunningActionType.Email);
        b.Property(x => x.ChargeAmount).HasColumnType(ArApColumn.Money);
        b.Property(x => x.ChargeDocumentNumber).HasMaxLength(30);
        b.Property(x => x.SentTo).HasMaxLength(500);
        b.Property(x => x.Subject).HasMaxLength(200);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired().HasDefaultValue(ArApEnums.DunningRunItemStatus.Pending);
        b.Property(x => x.SkipReason).HasMaxLength(200);
        b.Property(x => x.FailureReason).HasMaxLength(1000);
    }
}

internal sealed class MatchToleranceRuleMap : IEntityTypeConfiguration<MatchToleranceRule>
{
    public void Configure(EntityTypeBuilder<MatchToleranceRule> b)
    {
        b.ToTable("match_tolerance_rules", t =>
        {
            t.HasCheckConstraint("CK_match_tolerance_rules_NonNegative",
                "[PriceTolerancePercent] >= 0 AND [PriceToleranceAmount] >= 0 AND [QuantityTolerancePercent] >= 0 " +
                "AND [TotalTolerancePercent] >= 0 AND [TotalToleranceAmount] >= 0");
            t.HasCheckConstraint("CK_match_tolerance_rules_Percent",
                "[PriceTolerancePercent] <= 100 AND [QuantityTolerancePercent] <= 100 AND [TotalTolerancePercent] <= 100");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique();
        b.HasIndex(x => x.OrganizationId, "UX_match_tolerance_rules_Default").IsUnique().HasFilter("[IsDefault] = 1");
        b.HasIndex(x => new { x.OrganizationId, x.VendorUuid }, "UX_match_tolerance_rules_Vendor").IsUnique()
         .HasFilter("[VendorUuid] IS NOT NULL AND [IsActive] = 1");

        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.VendorName).HasMaxLength(200);
        b.Property(x => x.PriceTolerancePercent).HasColumnType(ArApColumn.Percent);
        b.Property(x => x.PriceToleranceAmount).HasColumnType(ArApColumn.Money);
        b.Property(x => x.QuantityTolerancePercent).HasColumnType(ArApColumn.Percent);
        b.Property(x => x.TotalTolerancePercent).HasColumnType(ArApColumn.Percent);
        b.Property(x => x.TotalToleranceAmount).HasColumnType(ArApColumn.Money);
        b.Property(x => x.IsActive).HasDefaultValue(true);
        b.Property(x => x.CreatedByName).HasMaxLength(200);
        b.Property(x => x.ModifiedByName).HasMaxLength(200);
    }
}

internal sealed class InvoiceMatchLineMap : IEntityTypeConfiguration<InvoiceMatchLine>
{
    public void Configure(EntityTypeBuilder<InvoiceMatchLine> b)
    {
        b.ToTable("invoice_match_lines", t =>
            t.HasCheckConstraint("CK_invoice_match_lines_LineStatus", ArApColumn.In("LineStatus", ArApEnums.MatchLineStatus.All)));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
        b.HasIndex(x => x.InvoiceId);

        b.Property(x => x.ItemDescription).HasMaxLength(300).IsRequired();
        b.Property(x => x.PoQuantity).HasColumnType(ArApColumn.Quantity);
        b.Property(x => x.PoUnitPrice).HasColumnType(ArApColumn.Quantity);
        b.Property(x => x.PoLineTotal).HasColumnType(ArApColumn.Money);
        b.Property(x => x.GrnQuantity).HasColumnType(ArApColumn.Quantity);
        b.Property(x => x.InvoiceQuantity).HasColumnType(ArApColumn.Quantity);
        b.Property(x => x.InvoiceUnitPrice).HasColumnType(ArApColumn.Quantity);
        b.Property(x => x.InvoiceLineTotal).HasColumnType(ArApColumn.Money);
        b.Property(x => x.QuantityVariance).HasColumnType(ArApColumn.Quantity);
        b.Property(x => x.PriceVariance).HasColumnType(ArApColumn.Quantity);
        b.Property(x => x.PriceVariancePercent).HasColumnType(ArApColumn.Percent);
        b.Property(x => x.TotalVariance).HasColumnType(ArApColumn.Money);
        b.Property(x => x.LineStatus).HasMaxLength(20).IsRequired().HasDefaultValue(ArApEnums.MatchLineStatus.Pending);
        b.Property(x => x.Note).HasMaxLength(300);

        b.HasOne(x => x.Invoice).WithMany(x => x.MatchLines).HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class EarlyPaymentTermMap : IEntityTypeConfiguration<EarlyPaymentTerm>
{
    public void Configure(EntityTypeBuilder<EarlyPaymentTerm> b)
    {
        b.ToTable("early_payment_terms", t =>
        {
            t.HasCheckConstraint("CK_early_payment_terms_DiscountDays", "[DiscountDays] >= 0 AND [DiscountDays] < [NetDays]");
            t.HasCheckConstraint("CK_early_payment_terms_DiscountPercent", "[DiscountPercent] > 0 AND [DiscountPercent] < 100");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique();

        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.DiscountPercent).HasColumnType(ArApColumn.Percent);
        b.Property(x => x.IsActive).HasDefaultValue(true);
        b.Property(x => x.CreatedByName).HasMaxLength(200);
        b.Property(x => x.ModifiedByName).HasMaxLength(200);
    }
}

internal sealed class PaymentScheduleMap : IEntityTypeConfiguration<PaymentSchedule>
{
    public void Configure(EntityTypeBuilder<PaymentSchedule> b)
    {
        b.ToTable("payment_schedules", t =>
        {
            t.HasCheckConstraint("CK_payment_schedules_Status", ArApColumn.In("Status", ArApEnums.ScheduleStatus.All));
            t.HasCheckConstraint("CK_payment_schedules_PaymentPriority", ArApColumn.In("PaymentPriority", ArApEnums.PaymentPriority.All));
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();
        // D-8 — one schedule per invoice.
        b.HasIndex(x => x.InvoiceId).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.Status, x.DueDate });
        b.HasIndex(x => new { x.OrganizationId, x.VendorUuid, x.Status });
        b.HasIndex(x => new { x.OrganizationId, x.DiscountDate }).HasFilter("[DiscountDate] IS NOT NULL");
        b.HasIndex(x => x.ReservedByRunId).HasFilter("[ReservedByRunId] IS NOT NULL");

        b.Property(x => x.InvoiceNumber).HasMaxLength(20).IsRequired();
        b.Property(x => x.SupplierInvoiceNo).HasMaxLength(50);
        b.Property(x => x.VendorName).HasMaxLength(200).IsRequired();
        b.Property(x => x.CurrencyCode).HasMaxLength(10).IsRequired();
        b.Property(x => x.InvoiceDate).HasColumnType("date");
        b.Property(x => x.DueDate).HasColumnType("date");
        b.Property(x => x.OriginalAmount).HasColumnType(ArApColumn.Money);
        b.Property(x => x.AmountRemaining).HasColumnType(ArApColumn.Money);
        b.Property(x => x.PaymentPriority).HasMaxLength(10).IsRequired().HasDefaultValue(ArApEnums.PaymentPriority.Normal);
        b.Property(x => x.PaymentMethod).HasMaxLength(30);
        b.Property(x => x.BlockReason).HasMaxLength(200);
        b.Property(x => x.BlockedByName).HasMaxLength(200);
        b.Property(x => x.DiscountDate).HasColumnType("date");
        b.Property(x => x.DiscountPercent).HasColumnType(ArApColumn.Percent);
        b.Property(x => x.DiscountAmount).HasColumnType(ArApColumn.Money);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired().HasDefaultValue(ArApEnums.ScheduleStatus.Open);
        b.Property(x => x.RowVersion).IsRowVersion();

        b.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.EarlyPaymentTerm).WithMany().HasForeignKey(x => x.EarlyPaymentTermId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.ReservedByRun).WithMany().HasForeignKey(x => x.ReservedByRunId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.PaidInRun).WithMany().HasForeignKey(x => x.PaidInRunId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PaymentRunMap : IEntityTypeConfiguration<PaymentRun>
{
    public void Configure(EntityTypeBuilder<PaymentRun> b)
    {
        b.ToTable("payment_runs", t =>
            t.HasCheckConstraint("CK_payment_runs_Status", ArApColumn.In("Status", ArApEnums.PaymentRunStatus.All)));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.RunNumber }).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.Status });
        b.HasIndex(x => new { x.OrganizationId, x.RunDate });

        b.Property(x => x.RunNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.RunDate).HasColumnType("date");
        b.Property(x => x.PaymentDate).HasColumnType("date");
        b.Property(x => x.DueDateCutoff).HasColumnType("date");
        b.Property(x => x.Status).HasMaxLength(20).IsRequired().HasDefaultValue(ArApEnums.PaymentRunStatus.Draft);
        b.Property(x => x.PaymentMethod).HasMaxLength(30);
        b.Property(x => x.BankAccount).HasMaxLength(100);
        b.Property(x => x.IncludeDiscount).HasDefaultValue(true);
        b.Property(x => x.BaseCurrencyCode).HasMaxLength(10);
        b.Property(x => x.TotalAmount).HasColumnType(ArApColumn.Money);
        b.Property(x => x.TotalDiscount).HasColumnType(ArApColumn.Money);
        b.Property(x => x.NetPaymentAmount).HasColumnType(ArApColumn.Money);
        b.Property(x => x.ProposedByName).HasMaxLength(200);
        b.Property(x => x.ApprovedByName).HasMaxLength(200);
        b.Property(x => x.ExecutedByName).HasMaxLength(200);
        b.Property(x => x.CancelledByName).HasMaxLength(200);
        b.Property(x => x.CancelReason).HasMaxLength(500);
        b.Property(x => x.PaymentFileName).HasMaxLength(200);
        b.Property(x => x.RowVersion).IsRowVersion();

        b.HasMany(x => x.Batches).WithOne(x => x.PaymentRun).HasForeignKey(x => x.PaymentRunId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Items).WithOne(x => x.PaymentRun).HasForeignKey(x => x.PaymentRunId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PaymentRunBatchMap : IEntityTypeConfiguration<PaymentRunBatch>
{
    public void Configure(EntityTypeBuilder<PaymentRunBatch> b)
    {
        b.ToTable("payment_run_batches", t =>
            t.HasCheckConstraint("CK_payment_run_batches_Status", ArApColumn.In("Status", ArApEnums.PaymentRunBatchStatus.All)));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
        b.HasIndex(x => new { x.PaymentRunId, x.VendorUuid, x.CurrencyCode, x.PaymentMethod }).IsUnique();

        b.Property(x => x.VendorName).HasMaxLength(200).IsRequired();
        b.Property(x => x.CurrencyCode).HasMaxLength(10).IsRequired();
        b.Property(x => x.PaymentMethod).HasMaxLength(30).IsRequired();
        b.Property(x => x.BankDetailsMasked).HasMaxLength(100);
        b.Property(x => x.TotalAmount).HasColumnType(ArApColumn.Money);
        b.Property(x => x.TotalDiscount).HasColumnType(ArApColumn.Money);
        b.Property(x => x.NetAmount).HasColumnType(ArApColumn.Money);
        b.Property(x => x.PaymentReference).HasMaxLength(100);
        b.Property(x => x.SupplierPaymentNumber).HasMaxLength(20);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired().HasDefaultValue(ArApEnums.PaymentRunBatchStatus.Pending);
        b.Property(x => x.FailureReason).HasMaxLength(1000);

        // NoAction: the run already cascades to both batches and items (one cascade path per table).
        b.HasMany(x => x.Items).WithOne(x => x.Batch).HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class PaymentRunItemMap : IEntityTypeConfiguration<PaymentRunItem>
{
    public void Configure(EntityTypeBuilder<PaymentRunItem> b)
    {
        b.ToTable("payment_run_items", t =>
            t.HasCheckConstraint("CK_payment_run_items_Status", ArApColumn.In("Status", ArApEnums.PaymentRunItemStatus.All)));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.VendorUuid });
        b.HasIndex(x => new { x.PaymentRunId, x.PaymentScheduleId }).IsUnique();
        b.HasIndex(x => x.PaymentScheduleId);
        b.HasIndex(x => x.BatchId);

        b.Property(x => x.InvoiceNumber).HasMaxLength(20).IsRequired();
        b.Property(x => x.VendorName).HasMaxLength(200).IsRequired();
        b.Property(x => x.CurrencyCode).HasMaxLength(10).IsRequired();
        b.Property(x => x.DueDate).HasColumnType("date");
        b.Property(x => x.OriginalAmount).HasColumnType(ArApColumn.Money);
        b.Property(x => x.DiscountAmount).HasColumnType(ArApColumn.Money);
        b.Property(x => x.NetPaymentAmount).HasColumnType(ArApColumn.Money);
        b.Property(x => x.PaymentMethod).HasMaxLength(30);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired().HasDefaultValue(ArApEnums.PaymentRunItemStatus.Proposed);
        b.Property(x => x.RemovedReason).HasMaxLength(200);
        b.Property(x => x.RemovedByName).HasMaxLength(200);

        b.HasOne(x => x.PaymentSchedule).WithMany().HasForeignKey(x => x.PaymentScheduleId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CustomerCreditApplicationMap : IEntityTypeConfiguration<CustomerCreditApplication>
{
    public void Configure(EntityTypeBuilder<CustomerCreditApplication> b)
    {
        b.ToTable("customer_credit_applications", t =>
        {
            t.HasCheckConstraint("CK_customer_credit_applications_Amount", "[Amount] > 0");
            t.HasCheckConstraint("CK_customer_credit_applications_Distinct", "[CreditNoteId] <> [InvoiceId]");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.InvoiceUuid });
        b.HasIndex(x => new { x.OrganizationId, x.CreditNoteUuid });
        b.HasIndex(x => x.CreditNoteId);
        b.HasIndex(x => x.InvoiceId);

        b.Property(x => x.CreditNoteNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.InvoiceNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.Amount).HasColumnType(ArApColumn.Money);
        b.Property(x => x.Notes).HasMaxLength(300);
        b.Property(x => x.AppliedByName).HasMaxLength(200);
        b.Property(x => x.ReversalReason).HasMaxLength(500);

        b.HasOne(x => x.CreditNote).WithMany().HasForeignKey(x => x.CreditNoteId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class SupplierNoteApplicationMap : IEntityTypeConfiguration<SupplierNoteApplication>
{
    public void Configure(EntityTypeBuilder<SupplierNoteApplication> b)
    {
        b.ToTable("supplier_note_applications", t =>
        {
            t.HasCheckConstraint("CK_supplier_note_applications_NoteType", ArApColumn.In("NoteType", ArApEnums.SupplierNoteType.All));
            t.HasCheckConstraint("CK_supplier_note_applications_Amount", "[Amount] > 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.NoteType, x.NoteUuid });
        b.HasIndex(x => new { x.OrganizationId, x.InvoiceUuid });
        b.HasIndex(x => x.InvoiceId);

        b.Property(x => x.NoteType).HasMaxLength(20).IsRequired();
        b.Property(x => x.NoteNumber).HasMaxLength(20).IsRequired();
        b.Property(x => x.InvoiceNumber).HasMaxLength(20).IsRequired();
        b.Property(x => x.Amount).HasColumnType(ArApColumn.Money);
        b.Property(x => x.Notes).HasMaxLength(300);
        b.Property(x => x.AppliedByName).HasMaxLength(200);
        b.Property(x => x.ReversalReason).HasMaxLength(500);

        b.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
    }
}
