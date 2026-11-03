using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.ArAp;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests.ArApCore;

/// <summary>
/// A48 CORE — tasks 001–030 (profiles), D-4/D-CORE-3 (no duplicate permanent limit; effective limit), API-CONTRACT §1
/// (get-or-create, update, list, settings) and D-16 (the defaults seeder) on the in-memory provider. The database constraints
/// themselves (unique, CHECKs) are proven on LocalDB in A48MigrationTests.
/// </summary>
public sealed class A48PartnerProfileTests
{
    private static readonly Guid Org   = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 10, 25);

    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly FakePartners _partners = new();

    private FinanceDbContext Db(Guid? org = null) => new(
        new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(_dbName).Options,
        new StaticTenantContext { OrganizationId = org ?? Org });

    private PartnerFinancialProfileService Service(FinanceDbContext db) =>
        new(db, _partners, new FixedClock(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero)));

    private static ArApCaller Caller(params string[] codes) => new(Org, 7, codes, "Asha Finance");
    private static ArApCaller Admin => Caller([.. PermissionCodes.ArApCodes]);

    private Guid Customer(decimal? limit = 10_000m, int terms = 30) =>
        _partners.Add(new PartnerInfo(Guid.NewGuid(), "C-1", "Acme Retail", true, false, true, "ar@acme.test", null, null,
            null, null, limit, terms, "COMPANY"));

    private Guid Vendor() =>
        _partners.Add(new PartnerInfo(Guid.NewGuid(), "V-1", "Steel Co", false, true, true, null, null, null, null, null, null, 45, null));

    // ── 025–027: get-or-create ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task T026_first_read_creates_a_customer_profile_with_defaults_and_the_partner_master_limit()
    {
        var customer = Customer(limit: 50_000m);
        await using var db = Db();

        var m = await Service(db).GetModelAsync(Caller(PermissionCodes.AR_VIEW), customer, "customer");

        m.Role.Should().Be("Customer");
        (m.RiskTier, m.PaymentPriority, m.CurrentDunningLevel, m.CreditHold).Should().Be(("Medium", "Normal", 0, false));
        (m.PartnerName, m.PartnerCode, m.PartnerEmail, m.PaymentTermsDays).Should().Be(("Acme Retail", "C-1", "ar@acme.test", 30));
        m.CreditLimit.Should().Be(50_000m, "the permanent limit is read from BusinessPartner.CreditLimit (D-4), never stored twice");
        m.EffectiveCreditLimit.Should().Be(50_000m);
        (await db.PartnerFinancialProfiles.CountAsync()).Should().Be(1);
        typeof(PartnerFinancialProfile).GetProperty("CreditLimit").Should().BeNull("no duplicate permanent credit limit (D-4)");
    }

    [Fact]
    public async Task T027_vendor_profile_and_customer_profile_of_the_same_partner_are_separate_rows()
    {
        var both = _partners.Add(new PartnerInfo(Guid.NewGuid(), "B-1", "Both Ways", true, true, true, null, null, null, null, null, null, 0, null));
        await using var db = Db();
        var svc = Service(db);

        var c = await svc.GetModelAsync(Admin, both, "Customer");
        var v = await svc.GetModelAsync(Admin, both, "Vendor");
        var again = await svc.GetOrCreateAsync(Org, both, "Customer");

        c.Uuid.Should().NotBe(v.Uuid);
        again.Uuid.Should().Be(c.Uuid, "get-or-create returns the existing row");
        (await db.PartnerFinancialProfiles.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Get_or_create_does_not_look_the_partner_up_so_a_documents_deleted_partner_still_gets_a_profile()
    {
        await using var db = Db();
        var p = await Service(db).GetOrCreateAsync(Org, Guid.NewGuid(), "Customer");
        p.Id.Should().BeGreaterThan(0);
        p.OrganizationId.Should().Be(Org);
    }

    [Theory]
    [InlineData(null, PartnerFinancialProfileService.MsgRoleRequired)]
    [InlineData("Supplier", "Unknown role 'Supplier'. Allowed: Customer, Vendor.")]
    public async Task Role_is_required_and_must_be_known(string? role, string message)
    {
        await using var db = Db();
        var act = () => Service(db).GetModelAsync(Admin, Customer(), role);
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage(message);
    }

    [Fact]
    public async Task Unknown_partner_is_404_and_the_wrong_flag_is_400()
    {
        await using var db = Db();
        var svc = Service(db);
        var missing = Guid.NewGuid();

        (await FluentActions.Awaiting(() => svc.GetModelAsync(Admin, missing, "Customer")).Should().ThrowAsync<NotFoundException>())
            .WithMessage($"Business partner with key '{missing}' was not found.");
        (await FluentActions.Awaiting(() => svc.GetModelAsync(Admin, Vendor(), "Customer")).Should().ThrowAsync<BadRequestException>())
            .WithMessage("Steel Co is not a customer.");
        (await FluentActions.Awaiting(() => svc.GetModelAsync(Admin, Customer(), "Vendor")).Should().ThrowAsync<BadRequestException>())
            .WithMessage("Acme Retail is not a vendor.");
    }

    [Fact]
    public async Task Reading_needs_the_roles_own_codes()
    {
        await using var db = Db();
        var svc = Service(db);
        await FluentActions.Awaiting(() => svc.GetModelAsync(Caller(PermissionCodes.AP_VIEW), Customer(), "Customer"))
            .Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Awaiting(() => svc.GetModelAsync(Caller(PermissionCodes.AR_VIEW), Vendor(), "Vendor"))
            .Should().ThrowAsync<ForbiddenException>();
        (await svc.GetModelAsync(Caller(PermissionCodes.CREDIT_MANAGE), Customer(), "Customer")).Role.Should().Be("Customer");
        (await svc.GetModelAsync(Caller(PermissionCodes.AP_EXECUTE), Vendor(), "Vendor")).Role.Should().Be("Vendor");
    }

    [Fact]
    public async Task Another_organizations_profile_is_never_returned()
    {
        var customer = Customer();
        await using (var other = Db(Other))
            await Service(other).GetOrCreateAsync(Other, customer, "Customer");

        await using var db = Db();
        var mine = await Service(db).GetOrCreateAsync(Org, customer, "Customer");
        mine.OrganizationId.Should().Be(Org);
        (await db.PartnerFinancialProfiles.IgnoreQueryFilters().CountAsync()).Should().Be(2);
    }

    // ── §1.2 update ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_sets_the_editable_fields_canonicalizes_enums_and_audits_a_tier_change()
    {
        var customer = Customer();
        await using var db = Db();
        var proc = new DunningProcedure { OrganizationId = Org, Code = "SOFT", Name = "Soft", IsActive = true };
        var term = new EarlyPaymentTerm { OrganizationId = Org, Code = "2-10-N30", Name = "2/10 Net 30", DiscountPercent = 2m, DiscountDays = 10, NetDays = 30 };
        db.AddRange(proc, term);
        await db.SaveChangesAsync();

        var m = await Service(db).UpdateAsync(Admin, customer, "Customer", new UpdatePartnerFinancialProfileRequest
        {
            RiskTier = "high", DunningProcedureUuid = proc.Uuid, BlockSalesAtDunning = true, ExcludeFromDunning = true,
            DefaultPaymentMethod = "bank_transfer", PaymentPriority = "LOW", PreferredPaymentMethod = "cheque",
            WithholdingTaxApplicable = true, EarlyPaymentTermUuid = term.Uuid, Notes = "  pays late in Q4  ",
        });

        (m.RiskTier, m.DunningProcedureCode, m.BlockSalesAtDunning, m.ExcludeFromDunning).Should().Be(("High", "SOFT", true, true));
        (m.DefaultPaymentMethod, m.PaymentPriority, m.PreferredPaymentMethod, m.WithholdingTaxApplicable)
            .Should().Be(("BANK_TRANSFER", "Low", "CHEQUE", true));
        (m.EarlyPaymentTermCode, m.Notes, m.ModifiedByName).Should().Be(("2-10-N30", "pays late in Q4", "Asha Finance"));

        var audit = await db.CreditHoldRequests.SingleAsync();
        (audit.Action, audit.PreviousValue, audit.NewValue, audit.RequestedBy, audit.PartnerName)
            .Should().Be(("RiskTier", "Medium", "High", (int?)7, "Acme Retail"));

        var cleared = await Service(db).UpdateAsync(Admin, customer, "Customer",
            new UpdatePartnerFinancialProfileRequest { ClearDunningProcedure = true, EarlyPaymentTermUuid = Guid.Empty, DefaultPaymentMethod = "" });
        (cleared.DunningProcedureUuid, cleared.EarlyPaymentTermUuid, cleared.DefaultPaymentMethod).Should().Be(((Guid?)null, (Guid?)null, (string?)null));
        (await db.CreditHoldRequests.CountAsync()).Should().Be(1, "no tier change, no audit row");
    }

    [Fact]
    public async Task Update_refuses_bad_input_with_the_contract_texts()
    {
        var customer = Customer();
        await using var db = Db();
        var inactive = new DunningProcedure { OrganizationId = Org, Code = "OLD", Name = "Old", IsActive = false };
        db.Add(inactive);
        await db.SaveChangesAsync();
        var svc = Service(db);
        var missing = Guid.NewGuid();

        async Task Refused(UpdatePartnerFinancialProfileRequest r, string text) =>
            (await FluentActions.Awaiting(() => svc.UpdateAsync(Admin, customer, "Customer", r)).Should().ThrowAsync<BadRequestException>())
                .WithMessage(text);

        await Refused(new() { RiskTier = "Extreme" }, "Unknown riskTier 'Extreme'. Allowed: Low, Medium, High.");
        await Refused(new() { PaymentPriority = "Urgent" }, "Unknown paymentPriority 'Urgent'. Allowed: High, Normal, Low.");
        await Refused(new() { PreferredPaymentMethod = "CARD" }, "Unknown payment method 'CARD'. Allowed: BANK_TRANSFER, ONLINE_WIRE, CHEQUE, CASH.");
        await Refused(new() { DunningProcedureUuid = missing }, $"Dunning procedure with key '{missing}' was not found.");
        await Refused(new() { DunningProcedureUuid = inactive.Uuid }, "Dunning procedure OLD is inactive.");
        await Refused(new() { EarlyPaymentTermUuid = missing }, $"Early payment term with key '{missing}' was not found.");
        await Refused(new() { Notes = new string('x', 1001) }, "Notes cannot be longer than 1000 characters.");
        await Refused(new() { RowVersion = "not base64!" }, "rowVersion is not valid.");

        await FluentActions.Awaiting(() => svc.UpdateAsync(Caller(PermissionCodes.AR_VIEW, PermissionCodes.CREDIT_MANAGE), customer, "Customer", new()))
            .Should().ThrowAsync<ForbiddenException>("writing a profile is FINANCE_AR_AP_ADMIN");
    }

    // ── §1.3 list ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_filters_by_role_hold_tier_and_level_sorts_by_name_and_pages()
    {
        await using var db = Db();
        var svc = Service(db);
        var zed = _partners.Add(new PartnerInfo(Guid.NewGuid(), "Z", "Zed Traders", true, false, true, null, null, null, null, null, null, 0, null));
        var abc = _partners.Add(new PartnerInfo(Guid.NewGuid(), "A", "Abc Stores", true, false, true, null, null, null, null, null, null, 0, null));
        var vendor = Vendor();
        (await svc.GetOrCreateAsync(Org, zed, "Customer")).CreditHold = true;
        var a = await svc.GetOrCreateAsync(Org, abc, "Customer");
        a.CurrentDunningLevel = 2;
        a.RiskTier = "High";
        // D-AR-13 (QA-3): a dunning level reads as stored only while the customer still owes something.
        db.SalesInvoices.Add(new SMS.Modules.Finance.Domain.SalesInvoice
        {
            UUID = Guid.NewGuid(), OrganizationId = Org, InvoiceNumber = "SINV-TEST-0001", PartnerId = abc, PartnerName = "Abc Stores",
            GrandTotal = 100m, BalanceDue = 100m, Status = "OVERDUE", CurrencyCode = "PKR"
        });
        await svc.GetOrCreateAsync(Org, vendor, "Vendor");
        await db.SaveChangesAsync();

        var all = await svc.ListAsync(Admin, new PartnerProfileListQuery { Role = "Customer" });
        all.Data.Select(d => d.PartnerName).Should().Equal("Abc Stores", "Zed Traders");
        all.TotalRecords.Should().Be(2);

        (await svc.ListAsync(Admin, new PartnerProfileListQuery { Role = "Customer", OnHold = true })).Data.Single().PartnerName.Should().Be("Zed Traders");
        (await svc.ListAsync(Admin, new PartnerProfileListQuery { Role = "Customer", RiskTier = "high" })).Data.Single().PartnerName.Should().Be("Abc Stores");
        (await svc.ListAsync(Admin, new PartnerProfileListQuery { Role = "Customer", MinDunningLevel = 1 })).Data.Single().PartnerName.Should().Be("Abc Stores");
        (await svc.ListAsync(Admin, new PartnerProfileListQuery { Role = "Customer", Search = "zed" })).Data.Single().PartnerName.Should().Be("Zed Traders");
        (await svc.ListAsync(Admin, new PartnerProfileListQuery { Role = "Vendor" })).Data.Single().PartnerName.Should().Be("Steel Co");

        var page2 = await svc.ListAsync(Admin, new PartnerProfileListQuery { Role = "Customer", Page = 2, PageSize = 1 });
        (page2.Data.Single().PartnerName, page2.TotalPages, page2.HasPrevious).Should().Be(("Zed Traders", 2, true));
    }

    // ── D-CORE-3 effective limit ──────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, null, null, null)]          // no permanent limit = no limit
    [InlineData(0, 5000, null, null)]             // a temporary limit means nothing without a permanent one
    [InlineData(10000, null, null, 10000)]
    [InlineData(10000, 15000, "2026-10-25", 15000)] // expiry day itself still counts
    [InlineData(10000, 15000, "2026-10-24", 10000)] // expired
    [InlineData(10000, 8000, null, 10000)]          // MAX(permanent, temporary)
    public void Effective_limit_follows_BR_CR_002(int? permanent, int? temporary, string? expiry, int? expected)
    {
        CreditLimits.Effective(permanent, temporary, expiry is null ? null : DateOnly.Parse(expiry), Today)
            .Should().Be(expected is null ? null : (decimal)expected.Value);
    }

    [Fact]
    public async Task Profile_shows_an_active_temporary_limit()
    {
        var customer = Customer(limit: 10_000m);
        await using var db = Db();
        var svc = Service(db);
        var p = await svc.GetOrCreateAsync(Org, customer, "Customer");
        p.TemporaryCreditLimit = 25_000m;
        p.TemporaryCreditExpiry = Today.AddDays(5);
        await db.SaveChangesAsync();

        var m = await svc.GetModelAsync(Admin, customer, "Customer");
        (m.TemporaryLimitActive, m.EffectiveCreditLimit, m.TemporaryCreditExpiry).Should().Be((true, (decimal?)25_000m, "2026-10-30"));
    }

    // ── §1.4 settings ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Credit_control_mode_is_Off_without_a_row_and_can_be_set_by_the_admin()
    {
        await using var db = Db();
        var svc = Service(db);

        (await svc.GetCreditControlModeAsync(Org)).Should().Be("Off", "D-5 — existing organizations are not suddenly blocked");
        (await svc.GetSettingsAsync(Caller(PermissionCodes.AP_VIEW))).CreditControlMode.Should().Be("Off");

        (await svc.UpdateSettingsAsync(Admin, new ArApSettingsModel { CreditControlMode = "block" })).CreditControlMode.Should().Be("Block");
        (await svc.GetCreditControlModeAsync(Org)).Should().Be("Block");
        (await svc.GetCreditControlModeAsync(Other)).Should().Be("Off");

        db.OrgSettings.Single().OpeningBalanceDate = new DateOnly(2026, 1, 1);
        await db.SaveChangesAsync();
        await svc.UpdateSettingsAsync(Admin, new ArApSettingsModel { CreditControlMode = "Warn" });
        var row = await db.OrgSettings.SingleAsync();
        (row.CreditControlMode, row.OpeningBalanceDate).Should().Be(("Warn", (DateOnly?)new DateOnly(2026, 1, 1)), "one settings row per organization");

        (await FluentActions.Awaiting(() => svc.UpdateSettingsAsync(Admin, new ArApSettingsModel { CreditControlMode = "Strict" }))
            .Should().ThrowAsync<BadRequestException>()).WithMessage("Unknown creditControlMode 'Strict'. Allowed: Off, Warn, Block.");
        await FluentActions.Awaiting(() => svc.UpdateSettingsAsync(Caller(PermissionCodes.CREDIT_MANAGE), new ArApSettingsModel { CreditControlMode = "Warn" }))
            .Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Awaiting(() => svc.GetSettingsAsync(Caller(PermissionCodes.INVOICE_VIEW)))
            .Should().ThrowAsync<ForbiddenException>();
    }

    // ── D-16 seeder ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Defaults_seeder_adds_the_standard_procedure_and_default_tolerance_once()
    {
        await using var db = Db();
        var seeder = new ArApDefaultsSeeder(db);

        await seeder.EnsureAsync(Org);
        await seeder.EnsureAsync(Org);
        await seeder.OnOrganizationProvisionedAsync(Org);

        var proc = await db.DunningProcedures.Include(p => p.Levels).SingleAsync();
        (proc.Code, proc.IsDefault, proc.BlockSalesAtLevel, proc.GraceDays, proc.MinimumAmount).Should().Be(("STANDARD", true, (int?)4, 0, 0m));
        proc.Levels.OrderBy(l => l.LevelNumber).Select(l => (l.LevelNumber, l.DaysAfterPrevious, l.ActionType, l.ChargeAmount))
            .Should().Equal((1, 0, "Email", (decimal?)null), (2, 14, "Email", null), (3, 14, "Email", null), (4, 14, "Email", null));
        proc.Levels.Should().OnlyContain(l => l.SubjectTemplate.Length > 0 && l.BodyTemplate.Contains("{CustomerName}"));

        var rule = await db.MatchToleranceRules.SingleAsync();
        (rule.Name, rule.IsDefault, rule.PriceTolerancePercent, rule.PriceToleranceAmount, rule.QuantityTolerancePercent, rule.AutoApproveWithin)
            .Should().Be(("DEFAULT", true, 2m, 0m, 0m, false));
    }

    [Fact]
    public async Task Defaults_seeder_leaves_an_organization_that_already_has_its_own_defaults_alone()
    {
        await using var db = Db();
        db.Add(new DunningProcedure { OrganizationId = Org, Code = "MINE", Name = "Mine", IsDefault = true });
        db.Add(new MatchToleranceRule { OrganizationId = Org, Name = "Strict", IsDefault = true });
        await db.SaveChangesAsync();

        await new ArApDefaultsSeeder(db).EnsureAsync(Org);

        (await db.DunningProcedures.Select(p => p.Code).ToListAsync()).Should().Equal("MINE");
        (await db.MatchToleranceRules.Select(r => r.Name).ToListAsync()).Should().Equal("Strict");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class FakePartners : IPartnerLookupService
    {
        private readonly Dictionary<Guid, PartnerInfo> _all = [];

        public Guid Add(PartnerInfo p) { _all[p.Uuid] = p; return p.Uuid; }

        public Task<PartnerInfo?> GetAsync(Guid organizationId, Guid partnerUuid, string? role = null, CancellationToken ct = default) =>
            Task.FromResult(organizationId == Org && _all.TryGetValue(partnerUuid, out var p) ? p : null);

        public Task<IReadOnlyDictionary<Guid, PartnerInfo>> GetManyAsync(Guid organizationId, IEnumerable<Guid> partnerUuids,
            string? role = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, PartnerInfo>>(
                partnerUuids.Where(_all.ContainsKey).Distinct().ToDictionary(u => u, u => _all[u]));

        public Task<IReadOnlyList<PartnerInfo>> SearchAsync(Guid organizationId, string search, string? role = null, int limit = 200,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PartnerInfo>>(_all.Values.Where(p => p.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList());

        public Task<PartnerBankInfo?> GetBankDetailsAsync(Guid organizationId, Guid partnerUuid, CancellationToken ct = default) =>
            Task.FromResult<PartnerBankInfo?>(null);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
