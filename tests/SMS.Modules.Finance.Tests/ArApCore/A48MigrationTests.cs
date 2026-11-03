using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SMS.Modules.Finance.ArAp;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests.ArApCore;

/// <summary>
/// A48 CORE — tasks 019–020, 028–030, 050–058, 063–069, 077–082, 476–487 and D-8/D-16/D-17 on a throwaway LocalDB database:
/// <c>A48_ArApSubledgers</c> down (tables and columns gone, existing data kept), up (schema back, seeds for every organization,
/// payment schedules backfilled for approved open invoices only), down + up again, a re-run with the history row lost (the
/// drifted shared database), and the constraints the schema enforces.
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class A48MigrationTests : IAsyncLifetime
{
    private const string Server   = @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True;";
    private const string Name     = "A48_ArApSubledgers";
    private const string Previous = "20261011103422_A40_FinanceOrgSettings";
    private readonly string _connection = $"{Server}Database=A48Mig_{Guid.NewGuid():N};";
    private readonly Guid _org  = Guid.NewGuid();
    private readonly Guid _org2 = Guid.NewGuid();

    private FinanceDbContext NewContext(Guid? org = null) => new(
        new DbContextOptionsBuilder<FinanceDbContext>().UseSqlServer(_connection).Options,
        new StaticTenantContext { OrganizationId = org ?? _org });

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new SqlConnection(_connection);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        var v = await cmd.ExecuteScalarAsync();
        return v is DBNull ? null : v;
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = new SqlConnection(_connection);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> CountAsync(string sql) => Convert.ToInt32(await ScalarAsync(sql));

    private static readonly string[] Tables =
    [
        "partner_financial_profiles", "credit_hold_requests", "statement_log", "dunning_procedures", "dunning_levels",
        "dunning_runs", "dunning_run_items", "match_tolerance_rules", "invoice_match_lines", "early_payment_terms",
        "payment_schedules", "payment_runs", "payment_run_batches", "payment_run_items", "customer_credit_applications",
        "supplier_note_applications",
    ];

    private Task<int> TableCountAsync() => CountAsync(
        $"SELECT COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID('finance') AND name IN ({string.Join(",", Tables.Select(t => $"'{t}'"))})");

    private Task<int> NewColumnCountAsync() => CountAsync(
        "SELECT COUNT(*) FROM sys.columns WHERE (object_id = OBJECT_ID('finance.invoices') AND name IN ('ToleranceRuleId','MatchType','LineMatchResult'," +
        "'MatchVariancePercent','WithinTolerance','AutoApproved','MatchedAt','MatchedBy','MatchedByName','MatchOverrideReason','MatchOverriddenAt'," +
        "'MatchOverriddenBy','MatchOverriddenByName')) OR (object_id = OBJECT_ID('finance.debit_notes') AND name = 'Origin') " +
        "OR (object_id = OBJECT_ID('finance.supplier_payment_lines') AND name = 'DiscountAmount') " +
        "OR (object_id = OBJECT_ID('finance.org_settings') AND name = 'CreditControlMode')");

    private const int ExpectedColumns = 13 + 1 + 1 + 1;

    private static Task UpAsync(FinanceDbContext db) =>
        db.GetService<IMigrator>().MigrateAsync(db.Database.GetMigrations().Single(m => m.EndsWith("_" + Name)));

    private static Invoice Bill(Guid org, string number, decimal total, string match = "Approved", string payment = "Unpaid",
        decimal paid = 0m, bool deleted = false, bool reversed = false)
    {
        var day = new DateTime(2026, 9, 1);
        return new Invoice
        {
            UUID = Guid.NewGuid(), OrganizationId = org, InvoiceNumber = number, SupplierInvoiceNo = "S-" + number, SupplierId = Guid.NewGuid(),
            SupplierName = "Steel Co", Currency = "PKR", TotalAmount = total, MatchStatus = match, PaymentStatus = payment, PaidAmount = paid,
            IsDelete = deleted, ReversedAt = reversed ? day : null, InvoiceDate = day, ReceivedDate = day, DueDate = day.AddDays(30), CreatedDate = day,
        };
    }

    [Fact]
    public void The_model_snapshot_matches_the_model_so_startup_Migrate_has_nothing_pending()
    {
        using var db = NewContext();
        db.Database.HasPendingModelChanges().Should().BeFalse("EF 9 refuses Migrate() on pending model changes — every A48 map change needs the snapshot");
    }

    /// <summary>RPT-1 / RPT-2 — the optimizer picks the new indexes for the AR open-item lookup and the AP aging read.</summary>
    [Fact]
    public async Task The_aging_indexes_are_used_by_the_ar_and_ap_queries()
    {
        await using (var db = NewContext()) await db.Database.EnsureCreatedAsync();
        // 20 organizations × 500 ledger entries and 500 bills, so a scan is clearly worse than a seek.
        await ExecAsync(@"
;WITH n AS (SELECT TOP (10000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b),
      o AS (SELECT i, CAST(CONVERT(binary(16), i % 20) AS uniqueidentifier) AS org FROM n)
INSERT INTO finance.customer_ledger (UUID, OrganizationId, PartnerId, SequenceNo, EntryDate, EntryType, ReferenceType, ReferenceId, ReferenceNumber,
    DebitAmount, CreditAmount, RunningBalance, CurrencyCode, CreatedBy, CreatedDate)
SELECT NEWID(), org, NEWID(), i, '2026-09-01', 'INVOICE', 'SalesInvoice', NEWID(), 'R' + CAST(i AS varchar(10)), 10, 0, 10, 'PKR', 1, SYSUTCDATETIME() FROM o;
;WITH n AS (SELECT TOP (10000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
INSERT INTO finance.invoices (UUID, TraceId, OrganizationId, InvoiceNumber, SupplierId, SupplierName, InvoiceDate, ReceivedDate, DueDate, Currency, Subtotal,
    TaxAmount, TotalAmount, MatchedPoValue, MatchedGrnValue, VarianceAmount, MatchStatus, PaymentStatus, PaidAmount, IsActive, IsDelete, CreatedBy, CreatedDate, AutoApproved)
SELECT NEWID(), NEWID(), CAST(CONVERT(binary(16), i % 20) AS uniqueidentifier), 'INV-' + CAST(i AS varchar(10)), NEWID(), 'S', '2026-09-01', '2026-09-01', '2026-10-01',
    'PKR', 100, 0, 100, 0, 0, 0, CASE WHEN i % 5 = 0 THEN 'Approved' ELSE 'Pending' END, 'Unpaid', 0, 1, 0, 1, SYSUTCDATETIME(), 0 FROM n;
UPDATE STATISTICS finance.customer_ledger; UPDATE STATISTICS finance.invoices;");

        var org = "CAST(CONVERT(binary(16), 7) AS uniqueidentifier)";
        var ref1 = await ScalarAsync($"SELECT TOP 1 ReferenceId FROM finance.customer_ledger WHERE OrganizationId = {org}");
        (await PlanAsync($"SELECT ReferenceId, EntryType, ReferenceType, EntryDate FROM finance.customer_ledger WHERE OrganizationId = {org} AND ReferenceId = '{ref1}'"))
            .Should().Contain("IX_customer_ledger_OrganizationId_ReferenceId");
        (await PlanAsync($"SELECT SupplierId, TotalAmount, PaidAmount, DueDate, InvoiceDate, Currency, ExchangeRate FROM finance.invoices " +
                         $"WHERE OrganizationId = {org} AND MatchStatus = 'Approved' AND IsDelete = 0 AND ReversedAt IS NULL"))
            .Should().Contain("IX_invoices_ApAging");
    }

    /// <summary>The estimated plan (SHOWPLAN_XML) of one query.</summary>
    private async Task<string> PlanAsync(string sql)
    {
        await using var conn = new SqlConnection(_connection);
        await conn.OpenAsync();
        await using (var on = new SqlCommand("SET SHOWPLAN_XML ON", conn)) await on.ExecuteNonQueryAsync();
        await using var cmd = new SqlCommand(sql, conn);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Down_up_seeds_backfill_and_a_rerun_on_a_drifted_database_all_work()
    {
        await using (var db = NewContext())
        {
            await db.Database.EnsureCreatedAsync();
            await ExecAsync("CREATE TABLE [dbo].[__EFMigrationsHistory] ([MigrationId] nvarchar(150) NOT NULL PRIMARY KEY, [ProductVersion] nvarchar(32) NOT NULL);");
            foreach (var id in db.Database.GetMigrations().Where(m => string.CompareOrdinal(m, Previous) <= 0 || m.EndsWith("_" + Name)))
                await ExecAsync($"INSERT INTO [dbo].[__EFMigrationsHistory] VALUES (N'{id}', N'9.0.0');");
        }
        await ExecAsync("CREATE SCHEMA tenant;");
        await ExecAsync("CREATE TABLE tenant.Organizations (Id uniqueidentifier NOT NULL PRIMARY KEY);");
        await ExecAsync($"INSERT INTO tenant.Organizations VALUES ('{_org}'), ('{_org2}');");

        // Payables as they stood before A48.
        var open      = Bill(_org, "INV-OPEN", 1000m);
        var partial   = Bill(_org, "INV-PART", 1000m, payment: "PARTIALLY_PAID", paid: 300m);
        var legacy    = Bill(_org, "INV-LEG", 500m, match: " approved ", payment: "Partial");
        var paid      = Bill(_org, "INV-PAID", 400m, payment: "FULLY_PAID", paid: 400m);
        var pending   = Bill(_org, "INV-PEND", 700m, match: "Pending");
        var reversed  = Bill(_org, "INV-REV", 800m, reversed: true, match: "Reversed");
        var deleted   = Bill(_org, "INV-DEL", 900m, deleted: true);
        var otherOrg  = Bill(_org2, "INV-O2", 250m);
        await using (var db = NewContext())
        {
            db.AddRange(open, partial, legacy, paid, pending, reversed, deleted, otherOrg);
            await db.SaveChangesAsync();
            db.Payments.Add(new Payment
            {
                UUID = Guid.NewGuid(), OrganizationId = _org, PaymentNumber = "PAY-1", InvoiceId = legacy.Id, InvoiceUuid = legacy.UUID,
                SupplierName = "Steel Co", PaymentDate = new DateTime(2026, 9, 5), AmountPaid = 200m, PaymentMethod = "CASH", Status = "Processed",
                CreatedDate = new DateTime(2026, 9, 5), ProcessedAt = new DateTime(2026, 9, 5),
            });
            db.Payments.Add(new Payment
            {
                UUID = Guid.NewGuid(), OrganizationId = _org, PaymentNumber = "PAY-2", InvoiceId = legacy.Id, InvoiceUuid = legacy.UUID,
                SupplierName = "Steel Co", PaymentDate = new DateTime(2026, 9, 6), AmountPaid = 100m, PaymentMethod = "CASH", Status = "Reversed",
                CreatedDate = new DateTime(2026, 9, 6), ProcessedAt = new DateTime(2026, 9, 6),
            });
            db.DebitNotes.Add(new DebitNote
            {
                UUID = Guid.NewGuid(), OrganizationId = _org, DebitNoteNumber = "DN-2026-00001", SroUuid = Guid.NewGuid(), SroNumber = "SRO-1",
                SupplierName = "Steel Co", DebitReason = "DAMAGED", DebitAmount = 50m, CreatedDate = new DateTime(2026, 9, 7),
            });
            await db.SaveChangesAsync();
        }

        // Down: the A48 schema goes, the documents stay.
        await using (var db = NewContext()) await db.GetService<IMigrator>().MigrateAsync(Previous);
        (await TableCountAsync()).Should().Be(0);
        (await NewColumnCountAsync()).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM finance.invoices")).Should().Be(8);

        // Up: schema back, seeds, backfill.
        await using (var db = NewContext()) await UpAsync(db);
        (await TableCountAsync()).Should().Be(Tables.Length);
        (await NewColumnCountAsync()).Should().Be(ExpectedColumns);
        await AssertSeededAndBackfilledAsync();
        (await ScalarAsync("SELECT Origin FROM finance.debit_notes")).Should().Be("SRO", "existing debit notes came from supplier returns");
        (await ScalarAsync("SELECT TOP 1 CreditControlMode FROM finance.org_settings")).Should().BeNull("no settings rows existed");

        // Down + up again, then the history row lost and the migration re-run on the schema it already made (SMSGlobal).
        await using (var db = NewContext()) await db.GetService<IMigrator>().MigrateAsync(Previous);
        await using (var db = NewContext()) await UpAsync(db);
        await ExecAsync($"DELETE FROM [dbo].[__EFMigrationsHistory] WHERE MigrationId LIKE '%{Name}'");
        await using (var db = NewContext()) await UpAsync(db);
        (await TableCountAsync()).Should().Be(Tables.Length);
        (await NewColumnCountAsync()).Should().Be(ExpectedColumns);
        await AssertSeededAndBackfilledAsync();

        // A third, idempotent re-run after the data moved on: an invoice approved since gets its schedule, nothing doubles.
        await ExecAsync($"UPDATE finance.invoices SET MatchStatus = 'Approved' WHERE InvoiceNumber = 'INV-PEND'");
        await ExecAsync($"DELETE FROM [dbo].[__EFMigrationsHistory] WHERE MigrationId LIKE '%{Name}'");
        await using (var db = NewContext()) await UpAsync(db);
        (await CountAsync("SELECT COUNT(*) FROM finance.payment_schedules")).Should().Be(5);
        (await CountAsync("SELECT COUNT(*) FROM finance.dunning_procedures")).Should().Be(2);

        (await CountAsync("SELECT COUNT(*) FROM sys.indexes WHERE name IN ('IX_customer_ledger_OrganizationId_ReferenceId', 'IX_invoices_ApAging')"))
            .Should().Be(2, "RPT-1 / RPT-2 survive down, up and the re-runs");

        await AssertConstraintsAsync(open.Id);
        await AssertServicesOnSqlServerAsync();
    }

    private async Task AssertSeededAndBackfilledAsync()
    {
        await using var db = NewContext();

        foreach (var org in new[] { _org, _org2 })
        {
            var proc = await db.DunningProcedures.IgnoreQueryFilters().Include(p => p.Levels).SingleAsync(p => p.OrganizationId == org);
            (proc.Code, proc.IsDefault, proc.IsActive, proc.BlockSalesAtLevel, proc.CreatedByName).Should().Be(("STANDARD", true, true, (int?)4, "System"));
            proc.Levels.OrderBy(l => l.LevelNumber).Select(l => (l.LevelNumber, l.DaysAfterPrevious, l.IncludeStatement, l.ChargeAmount, l.OrganizationId))
                .Should().Equal((1, 0, false, (decimal?)null, org), (2, 14, false, null, org), (3, 14, true, null, org), (4, 14, true, null, org));
            proc.Levels.Single(l => l.LevelNumber == 1).BodyTemplate.Should().Be(ArApSeedData.Levels[0].Body, "the seed SQL keeps the text exactly");
            proc.Levels.Single(l => l.LevelNumber == 4).EscalationNote.Should().Be("Escalate to the collections team.");

            var rule = await db.MatchToleranceRules.IgnoreQueryFilters().SingleAsync(r => r.OrganizationId == org);
            (rule.Name, rule.IsDefault, rule.PriceTolerancePercent, rule.PriceToleranceAmount, rule.AutoApproveWithin).Should().Be(("DEFAULT", true, 2m, 0m, false));
        }

        var schedules = await db.PaymentSchedules.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(s => s.InvoiceNumber);
        schedules.Keys.Should().BeEquivalentTo(["INV-OPEN", "INV-PART", "INV-LEG", "INV-O2"],
            "only approved, live, not reversed invoices still owing get a schedule (pending, paid, reversed and deleted do not)");
        (schedules["INV-OPEN"].AmountRemaining, schedules["INV-OPEN"].Status, schedules["INV-OPEN"].OriginalAmount).Should().Be((1000m, "Open", 1000m));
        (schedules["INV-PART"].AmountRemaining, schedules["INV-PART"].Status).Should().Be((700m, "PartiallyPaid"));
        (schedules["INV-LEG"].AmountRemaining, schedules["INV-LEG"].Status).Should().Be((300m, "PartiallyPaid"), "the reversed legacy payment does not count");
        var o = schedules["INV-OPEN"];
        (o.DueDate, o.InvoiceDate, o.CurrencyCode, o.PaymentPriority, o.VendorName, o.SupplierInvoiceNo, o.PaymentBlock)
            .Should().Be((new DateOnly(2026, 10, 1), new DateOnly(2026, 9, 1), "PKR", "Normal", "Steel Co", "S-INV-OPEN", false));
        schedules["INV-O2"].OrganizationId.Should().Be(_org2);
    }

    private async Task AssertConstraintsAsync(int invoiceId)
    {
        async Task Fails(string sql, string because) =>
            await FluentActions.Awaiting(() => ExecAsync(sql)).Should().ThrowAsync<SqlException>(because);

        string Profile(string role, string tier = "Medium", string priority = "Normal", Guid? partner = null) =>
            "INSERT INTO finance.partner_financial_profiles (Uuid, OrganizationId, PartnerUuid, PartnerRole, CreditHold, RiskTier, CurrentDunningLevel, " +
            "BlockSalesAtDunning, ExcludeFromDunning, PaymentPriority, WithholdingTaxApplicable, CreatedAt) VALUES " +
            $"(NEWID(), '{_org}', '{partner ?? Guid.NewGuid()}', '{role}', 0, '{tier}', 0, 0, 0, '{priority}', 0, SYSUTCDATETIME())";

        // 028–030 — profile uniqueness and CHECKs.
        var partner = Guid.NewGuid();
        await ExecAsync(Profile("Customer", partner: partner));
        await ExecAsync(Profile("Vendor", partner: partner));
        await Fails(Profile("Customer", partner: partner), "UQ (organization, partner, role)");
        await Fails(Profile("Supplier"), "CK_partner_financial_profiles_PartnerRole");
        await Fails(Profile("Customer", tier: "Extreme"), "CK_partner_financial_profiles_RiskTier");
        await Fails(Profile("Customer", priority: "Urgent"), "CK_partner_financial_profiles_PaymentPriority");

        // 056 / 058 — tolerance names, early-payment terms.
        await Fails($"INSERT INTO finance.match_tolerance_rules (Uuid, OrganizationId, Name, PriceTolerancePercent, PriceToleranceAmount, QuantityTolerancePercent, " +
            $"TotalTolerancePercent, TotalToleranceAmount, AutoApproveWithin, IsDefault, IsActive, CreatedAt) VALUES (NEWID(), '{_org}', 'DEFAULT', 1, 0, 0, 0, 0, 0, 0, 1, SYSUTCDATETIME())",
            "UQ (organization, name)");
        await Fails($"INSERT INTO finance.match_tolerance_rules (Uuid, OrganizationId, Name, PriceTolerancePercent, PriceToleranceAmount, QuantityTolerancePercent, " +
            $"TotalTolerancePercent, TotalToleranceAmount, AutoApproveWithin, IsDefault, IsActive, CreatedAt) VALUES (NEWID(), '{_org}', 'Second', 1, 0, 0, 0, 0, 0, 1, 1, SYSUTCDATETIME())",
            "one default rule per organization");
        string Term(string code, int discountDays, int netDays, decimal pct = 2m) =>
            "INSERT INTO finance.early_payment_terms (Uuid, OrganizationId, Code, Name, DiscountPercent, DiscountDays, NetDays, IsActive, CreatedAt) VALUES " +
            $"(NEWID(), '{_org}', '{code}', 'Term', {pct}, {discountDays}, {netDays}, 1, SYSUTCDATETIME())";
        await ExecAsync(Term("2-10-N30", 10, 30));
        await Fails(Term("BAD", 30, 30), "CK_early_payment_terms_DiscountDays: discount days < net days");
        await Fails(Term("BAD2", 10, 30, 0m), "CK_early_payment_terms_DiscountPercent");
        await Fails(Term("2-10-N30", 5, 20), "UQ (organization, code)");

        // 063 / 069 — credit actions.
        var profileId = Convert.ToInt32(await ScalarAsync($"SELECT TOP 1 Id FROM finance.partner_financial_profiles WHERE PartnerUuid = '{partner}'"));
        string Hold(string action) =>
            "INSERT INTO finance.credit_hold_requests (Uuid, OrganizationId, ProfileId, PartnerUuid, Action, Reason, RequestedAt, Status) VALUES " +
            $"(NEWID(), '{_org}', {profileId}, '{partner}', '{action}', 'r', SYSUTCDATETIME(), 'Completed')";
        await ExecAsync(Hold("TempIncrease"));
        await Fails(Hold("Freeze"), "CK_credit_hold_requests_Action");

        // Dunning — a second default procedure, a level skipped twice, one live run per day.
        await Fails($"INSERT INTO finance.dunning_procedures (Uuid, OrganizationId, Code, Name, IsDefault, IsActive, GraceDays, MinimumAmount, CreatedAt) " +
            $"VALUES (NEWID(), '{_org}', 'OTHER', 'Other', 1, 1, 0, 0, SYSUTCDATETIME())", "one default procedure per organization");
        string Run(string number, string status) =>
            "INSERT INTO finance.dunning_runs (Uuid, OrganizationId, RunNumber, RunDate, AsOfDate, Status, TotalCustomers, TotalAmount, ItemsProcessed, " +
            $"ItemsSent, ItemsSkipped, ItemsFailed, CreatedAt) VALUES (NEWID(), '{_org}', '{number}', '2026-10-25', '2026-10-25', '{status}', 0, 0, 0, 0, 0, 0, SYSUTCDATETIME())";
        await ExecAsync(Run("DUN-1", "Cancelled"));
        await ExecAsync(Run("DUN-2", "Draft"));
        await Fails(Run("DUN-3", "Completed"), "BR-DN-010 — one live run per organization per day");
        await Fails(Run("DUN-4", "Paused"), "CK_dunning_runs_Status");

        // Payment schedules — one per invoice.
        await Fails("INSERT INTO finance.payment_schedules (Uuid, OrganizationId, InvoiceId, InvoiceUuid, InvoiceNumber, VendorUuid, VendorName, CurrencyCode, " +
            $"InvoiceDate, DueDate, OriginalAmount, AmountRemaining, PaymentPriority, PaymentBlock, Status, CreatedAt) VALUES (NEWID(), '{_org}', {invoiceId}, NEWID(), 'X', " +
            "NEWID(), 'V', 'PKR', '2026-09-01', '2026-10-01', 1, 1, 'Normal', 0, 'Open', SYSUTCDATETIME())", "D-8 — one schedule per invoice");

        // Settings and debit-note origin CHECKs.
        await Fails($"INSERT INTO finance.org_settings (OrganizationId, CreatedBy, CreatedDate, CreditControlMode) VALUES ('{Guid.NewGuid()}', 1, SYSUTCDATETIME(), 'Strict')",
            "CK_org_settings_CreditControlMode");
        await Fails("UPDATE finance.debit_notes SET Origin = 'OTHER'", "CK_debit_notes_Origin");

        // 081 — no A48 foreign key leaves the finance schema.
        (await CountAsync("SELECT COUNT(*) FROM sys.foreign_keys fk WHERE OBJECT_SCHEMA_NAME(fk.parent_object_id) = 'finance' " +
            "AND OBJECT_SCHEMA_NAME(fk.referenced_object_id) <> 'finance'")).Should().Be(0);
    }

    /// <summary>The profile service on SQL Server: get-or-create twice in two contexts → one row; a stale rowVersion → 409.</summary>
    private async Task AssertServicesOnSqlServerAsync()
    {
        var customer = Guid.NewGuid();
        var admin = new ArApCaller(_org, 1, PermissionCodes.ArApCodes, "Admin");
        await using (var a = NewContext())
        await using (var b = NewContext())
        {
            var pa = await new PartnerFinancialProfileService(a).GetOrCreateAsync(_org, customer, "Customer");
            var pb = await new PartnerFinancialProfileService(b).GetOrCreateAsync(_org, customer, "Customer");
            pb.Id.Should().Be(pa.Id);
            pa.RowVersion.Should().NotBeEmpty();
        }

        string stale;
        await using (var db = NewContext())
        {
            var m = await new PartnerFinancialProfileService(db).GetModelAsync(admin, customer, "Customer");
            stale = m.RowVersion!;
            await new PartnerFinancialProfileService(db).UpdateAsync(admin, customer, "Customer", new UpdatePartnerFinancialProfileRequest { RiskTier = "High", RowVersion = stale });
        }
        await using (var db = NewContext())
        {
            var act = () => new PartnerFinancialProfileService(db).UpdateAsync(admin, customer, "Customer",
                new UpdatePartnerFinancialProfileRequest { RiskTier = "Low", RowVersion = stale });
            (await act.Should().ThrowAsync<ConflictException>()).WithMessage("The partner's financial profile was changed by someone else; reload and try again.");
        }

        // Settings row created on first write; defaults seeder idempotent against the migration's seed.
        await using (var db = NewContext())
        {
            await new PartnerFinancialProfileService(db).UpdateSettingsAsync(admin, new ArApSettingsModel { CreditControlMode = "Warn" });
            await new ArApDefaultsSeeder(db).EnsureAsync(_org);
            await new ArApDefaultsSeeder(db).EnsureAsync(Guid.NewGuid());
        }
        (await ScalarAsync($"SELECT CreditControlMode FROM finance.org_settings WHERE OrganizationId = '{_org}'")).Should().Be("Warn");
        (await CountAsync($"SELECT COUNT(*) FROM finance.dunning_procedures WHERE OrganizationId = '{_org}'")).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM finance.dunning_levels")).Should().Be(12, "3 organizations × 4 levels");
    }
}
