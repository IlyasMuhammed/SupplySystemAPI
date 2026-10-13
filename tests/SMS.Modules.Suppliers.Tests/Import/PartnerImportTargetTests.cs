using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Suppliers.Import;
using SMS.Modules.Suppliers.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Suppliers.Tests.Import;

/// <summary>A40 (TSUP) — CUSTOMER / SUPPLIER / SUPPLIER_CONTACT import targets.</summary>
public class PartnerImportTargetTests
{
    private const int User = 42;

    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class Gate(bool creditEnabled) : IModuleGate
    {
        public Task<bool> IsEnabledAsync(Guid organizationId, string featureCode, CancellationToken ct = default) =>
            Task.FromResult(featureCode != ModuleCodes.CreditManagement || creditEnabled);
    }

    private sealed class Currencies(params OrgCurrencyInfo[] items) : IOrgCurrencyLookup
    {
        public Task<OrgCurrencyInfo?> GetAsync(Guid organizationId, Guid currencyId, CancellationToken ct = default) =>
            Task.FromResult(items.FirstOrDefault(i => i.CurrencyId == currencyId));
        public Task<OrgCurrencyInfo?> GetByCodeAsync(Guid organizationId, string code, CancellationToken ct = default) =>
            Task.FromResult(items.FirstOrDefault(i => i.Code == code));
        public Task<IReadOnlyList<OrgCurrencyInfo>> ListAsync(Guid organizationId, bool activeOnly, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OrgCurrencyInfo>>(items.Where(i => !activeOnly || i.IsActive).ToList());
        public Task<int> GetDecimalPlacesAsync(Guid organizationId, Guid currencyId, CancellationToken ct = default) => Task.FromResult(2);
    }

    private sealed class Audit : IAuditService
    {
        public List<(int? User, string Action, Guid? Entity, string? NewValue, string? Notes)> Entries { get; } = [];
        public Task LogAsync(int? userId, string? userName, string module, string action, string entityType, Guid? entityId,
            string ipAddress = "", string? fieldChanged = null, string? oldValue = null, string? newValue = null, string? notes = null)
        {
            Entries.Add((userId, action, entityId, newValue, notes));
            return Task.CompletedTask;
        }
    }

    private sealed class Events : ISupplierEventPublisher
    {
        public List<Guid> Approved { get; } = [];
        public Task PublishSupplierApprovedAsync(Guid supplierUuid, int supplierId) { Approved.Add(supplierUuid); return Task.CompletedTask; }
        public Task PublishSupplierRejectedAsync(Guid supplierUuid, int supplierId, string reason) => Task.CompletedTask;
    }

    private static readonly OrgCurrencyInfo Pkr = new(Guid.NewGuid(), "PKR", "Rupee", "Rs", 2, 0.01m, "BEFORE", true, 1);
    private static readonly OrgCurrencyInfo Usd = new(Guid.NewGuid(), "USD", "Dollar", "$", 2, 0.01m, "BEFORE", true, 2);
    private static readonly OrgCurrencyInfo Eur = new(Guid.NewGuid(), "EUR", "Euro", "€", 2, 0.01m, "BEFORE", false, 3);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (SuppliersDbContext Db, Guid Org) NewDb()
    {
        var (db, tenant, _) = SuppliersTestDb.New();
        return (db, tenant.OrganizationId);
    }

    private static Dictionary<string, string?> V(params (string Key, string? Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static ImportRowInput In(int row, Dictionary<string, string?> values, Guid? existing = null) => new(row, values, existing);
    private static ImportApplyRow Create(int row, Dictionary<string, string?> values) => new(row, values, "CREATE", null);
    private static ImportApplyRow Update(int row, Dictionary<string, string?> values, Guid id) => new(row, values, "UPDATE", id);

    private static BusinessPartner Partner(Guid org, string code, string name, bool vendor = false, bool customer = true,
        string? email = null, string status = "ACTIVE", bool system = false, bool deleted = false, bool active = true) => new()
    {
        UUID = Guid.NewGuid(), OrganizationId = org, SupplierCode = code, SupplierName = name, Email = email,
        IsVendor = vendor, IsCustomer = customer, PartnerType = PartnerCode.Of(PartnerCode.FromFlags(vendor, customer, false, false)),
        CustomerType = customer ? (system ? CustomerTypes.WalkIn : CustomerTypes.Company) : null, IsSystem = system,
        CreditLimit = system ? 0m : null, Status = status, IsActive = active, IsDelete = deleted, CreatedBy = 1, CreatedDate = DateTime.UtcNow
    };

    private static async Task<BusinessPartner> SeedAsync(SuppliersDbContext db, BusinessPartner p)
    {
        db.BusinessPartners.Add(p);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return p;
    }

    private static Task<BusinessPartner> ReloadAsync(SuppliersDbContext db, Guid uuid) =>
        db.BusinessPartners.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.UUID == uuid);

    private static IEnumerable<string> Codes(ImportRowCheck c) => c.Errors.Select(e => e.Code);

    // ── Describe ─────────────────────────────────────────────────────────────

    [Fact]
    public void Describe_gives_the_three_profiles_in_wizard_order()
    {
        var (db, _) = NewDb();
        var customer = new CustomerImportTarget(db).Describe();
        var supplier = new SupplierImportTarget(db).Describe();
        var contact = new SupplierContactImportTarget(db).Describe();

        (customer.EntityType, customer.ImportOrder, customer.IsOpeningBalance, customer.AllowUpdate).Should().Be(("CUSTOMER", 7, false, true));
        (supplier.EntityType, supplier.ImportOrder).Should().Be(("SUPPLIER", 8));
        (contact.EntityType, contact.ImportOrder).Should().Be(("SUPPLIER_CONTACT", 9));
        customer.MatchFields.Should().Equal("code", "email");
        supplier.MatchFields.Should().Equal("code", "email");
        contact.MatchFields.Should().Equal("supplier_code", "email");

        customer.Fields.Select(f => f.FieldName).Should().Contain(["code", "name", "email", "phone", "address_line1", "address_line2", "city",
            "state_province", "postal_code", "country_code", "tax_number", "credit_limit", "payment_terms_days", "is_active"])
            .And.NotContain("external_id");
        supplier.Fields.Select(f => f.FieldName).Should().Equal("code", "name", "email", "phone", "address_line1", "city", "country_code",
            "tax_number", "payment_terms_days", "currency_code", "is_active");
        contact.Fields.Select(f => f.FieldName).Should().Equal("supplier_code", "name", "title", "email", "phone", "is_primary");

        customer.Fields.Single(f => f.FieldName == "name").IsRequired.Should().BeTrue();
        customer.Fields.Single(f => f.FieldName == "code").IsRequired.Should().BeFalse();
        supplier.Fields.Single(f => f.FieldName == "code").Should().Match<ImportFieldDefinition>(f => f.IsRequired && f.IsKey && f.MaxLength == 10);
        supplier.Fields.Single(f => f.FieldName == "currency_code").LookupList.Should().Be("currencies");
        contact.Fields.Single(f => f.FieldName == "supplier_code").LookupList.Should().Be("suppliers");
        foreach (var f in customer.Fields.Concat(supplier.Fields).Concat(contact.Fields))
            f.SortOrder.Should().BePositive();
    }

    [Fact]
    public async Task Template_lists_hold_countries_customer_types_active_currencies_and_active_supplier_codes()
    {
        var (db, org) = NewDb();
        await SeedAsync(db, Partner(org, "SUP-1", "Vendor A", vendor: true, customer: false));
        await SeedAsync(db, Partner(org, "SUP-0", "Gone", vendor: true, customer: false, deleted: true));
        await SeedAsync(db, Partner(org, "C-00001", "Buyer"));
        await SeedAsync(db, Partner(Guid.NewGuid(), "SUP-X", "Other org", vendor: true, customer: false));

        var customerLists = await new CustomerImportTarget(db).GetTemplateListsAsync(org);
        customerLists.Single(l => l.Key == "countries").Values.Should().Contain(["PK", "US", "GB"]).And.OnlyContain(c => c.Length == 2);
        customerLists.Single(l => l.Key == "customer_types").Values.Should().Contain(["COMPANY", "INDIVIDUAL"]);

        var supplierLists = await new SupplierImportTarget(db, new Currencies(Pkr, Usd, Eur)).GetTemplateListsAsync(org);
        supplierLists.Single(l => l.Key == "currencies").Values.Should().Equal("PKR", "USD");

        var contactLists = await new SupplierContactImportTarget(db).GetTemplateListsAsync(org);
        contactLists.Single(l => l.Key == "suppliers").Values.Should().Equal("SUP-1");
    }

    // ── CUSTOMER ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Customer_validate_matches_by_code_then_by_email_and_flags_rule_breaks()
    {
        var (db, org) = NewDb();
        var byCode = await SeedAsync(db, Partner(org, "C-00007", "Coded", email: "coded@x.com"));
        var byEmail = await SeedAsync(db, Partner(org, "C-00008", "Mailed", email: "Mail@X.com"));
        await SeedAsync(db, Partner(org, "C-00009", "Twin 1", email: "twin@x.com"));
        await SeedAsync(db, Partner(org, "C-00010", "Twin 2", email: "twin@x.com"));
        await SeedAsync(db, Partner(org, "C-WALKIN", "Walk-in Customer", system: true));
        await SeedAsync(db, Partner(org, "C-00011", "Deleted", deleted: true, active: false));
        var vendor = await SeedAsync(db, Partner(org, "V-1", "Vendor", vendor: true, customer: false));
        await SeedAsync(db, Partner(Guid.NewGuid(), "C-00012", "Other org", email: "other@x.com"));

        var checks = await new CustomerImportTarget(db).ValidateAsync(org,
        [
            In(2, V(("code", "c-00007"), ("name", "Coded"))),
            In(3, V(("email", "mail@x.com"), ("name", "Mailed"))),
            In(4, V(("email", "twin@x.com"), ("name", "Twin"))),
            In(5, V(("code", "C-WALKIN"), ("name", "Counter"))),
            In(6, V(("code", "C-00011"), ("name", "Back"))),
            In(7, V(("code", "V-1"), ("name", "Vendor"))),
            In(8, V(("email", "other@x.com"), ("name", "New"))),
            In(9, V(("name", "Bad"), ("country_code", "Atlantis"), ("customer_type", "VIP"), ("credit_limit", "-1"))),
            In(10, V(("name", "Cash"), ("customer_type", "WALK_IN"), ("credit_limit", "100"))),
            In(11, V(("code", "NEW-1"), ("name", "First"))),
            In(12, V(("code", "new-1"), ("name", "Again"))),
            In(13, V(("name", null)))
        ]);

        checks.Select(c => c.RowNumber).Should().Equal(2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13);
        checks[0].ExistingEntityUuid.Should().Be(byCode.UUID);
        checks[0].Errors.Should().BeEmpty();
        checks[1].ExistingEntityUuid.Should().Be(byEmail.UUID);
        Codes(checks[2]).Should().Equal("AMBIGUOUS_MATCH");
        Codes(checks[3]).Should().Equal("WALK_IN_PROTECTED");
        Codes(checks[4]).Should().Equal("CODE_IN_USE");
        checks[5].ExistingEntityUuid.Should().Be(vendor.UUID);      // the vendor gains the customer role
        checks[5].Errors.Should().BeEmpty();
        checks[6].ExistingEntityUuid.Should().BeNull();              // another organization's customer is never matched
        checks[6].Errors.Should().BeEmpty();
        Codes(checks[7]).Should().BeEquivalentTo(["LOOKUP_NOT_FOUND", "LOOKUP_NOT_FOUND", "OUT_OF_RANGE"]);
        Codes(checks[8]).Should().Equal("CREDIT_NOT_ALLOWED");
        checks[9].Errors.Should().BeEmpty();
        Codes(checks[10]).Should().Equal("DUPLICATE_IN_FILE");
        Codes(checks[11]).Should().Equal("REQUIRED");
    }

    [Fact]
    public async Task Customer_create_generates_codes_defaults_company_and_normalizes_countries()
    {
        var (db, org) = NewDb();
        await SeedAsync(db, Partner(org, "C-00003", "Old", deleted: true, active: false));

        var outcomes = await new CustomerImportTarget(db).ApplyAsync(org, User,
        [
            Create(2, V(("name", "Alpha"), ("country_code", "pak"), ("payment_terms_days", "30"), ("email", "a@x.com"))),
            Create(3, V(("name", "Beta"), ("country_code", "United Kingdom"), ("customer_type", "individual"), ("is_active", "false"))),
            Create(4, V(("code", "KEY-1"), ("name", "Gamma"), ("country_code", "US")))
        ]);

        outcomes.Select(o => (o.RowNumber, o.Status)).Should().Equal((2, "IMPORTED"), (3, "IMPORTED"), (4, "IMPORTED"));
        var rows = await db.BusinessPartners.IgnoreQueryFilters().AsNoTracking().Where(p => !p.IsDelete).OrderBy(p => p.SupplierName).ToListAsync();
        rows.Select(p => p.SupplierCode).Should().Equal("C-00004", "C-00005", "KEY-1");
        rows.Select(p => p.Country).Should().Equal("PK", "GB", "US");
        rows.Select(p => p.CustomerType).Should().Equal("COMPANY", "INDIVIDUAL", "COMPANY");
        rows.Should().OnlyContain(p => p.IsCustomer && !p.IsVendor && p.PartnerType == "CUSTOMER" && p.CreatedBy == User && p.OrganizationId == org);
        rows[0].PaymentTermsDays.Should().Be(30);
        (rows[1].IsActive, rows[1].Status).Should().Be((false, "INACTIVE"));
        outcomes.Select(o => o.EntityUuid).Should().Equal(rows.Select(p => p.UUID));
    }

    [Fact]
    public async Task Customer_update_leaves_blank_fields_unchanged_and_never_changes_the_code()
    {
        var (db, org) = NewDb();
        var seeded = Partner(org, "C-00001", "Old name", email: "old@x.com");
        seeded.Phone = "0300"; seeded.City = "Lahore"; seeded.PaymentTermsDays = 15;
        await SeedAsync(db, seeded);

        var outcomes = await new CustomerImportTarget(db).ApplyAsync(org, User,
            [Update(5, V(("code", "C-00001"), ("name", "New name"), ("phone", null), ("city", ""), ("tax_number", "NTN-9")), seeded.UUID)]);

        outcomes.Single().Should().Be(new ImportRowOutcome(5, "UPDATED", seeded.UUID));
        var p = await ReloadAsync(db, seeded.UUID);
        (p.SupplierName, p.Phone, p.City, p.Email, p.TaxId, p.PaymentTermsDays, p.SupplierCode, p.ModifiedBy)
            .Should().Be(("New name", "0300", "Lahore", "old@x.com", "NTN-9", 15, "C-00001", (int?)User));
    }

    [Fact]
    public async Task Customer_credit_limit_follows_credit_management()
    {
        var (db, org) = NewDb();
        var existing = await SeedAsync(db, Partner(org, "C-00001", "Has credit"));

        await new CustomerImportTarget(db, new Gate(creditEnabled: false)).ApplyAsync(org, User,
        [
            Create(2, V(("name", "Off"), ("credit_limit", "5000"))),
            Update(3, V(("name", "Has credit"), ("credit_limit", "7000")), existing.UUID)
        ]);
        (await db.BusinessPartners.AsNoTracking().SingleAsync(p => p.SupplierName == "Off")).CreditLimit.Should().BeNull();
        (await ReloadAsync(db, existing.UUID)).CreditLimit.Should().BeNull();

        db.ChangeTracker.Clear();
        await new CustomerImportTarget(db, new Gate(creditEnabled: true)).ApplyAsync(org, User,
        [
            Create(2, V(("name", "On"), ("credit_limit", "5000"))),
            Update(3, V(("credit_limit", "7000")), existing.UUID)
        ]);
        (await db.BusinessPartners.AsNoTracking().SingleAsync(p => p.SupplierName == "On")).CreditLimit.Should().Be(5000m);
        (await ReloadAsync(db, existing.UUID)).CreditLimit.Should().Be(7000m);
    }

    [Fact]
    public async Task The_walk_in_customer_is_never_updated_by_import()
    {
        var (db, org) = NewDb();
        var walkIn = await SeedAsync(db, Partner(org, "C-WALKIN", "Walk-in Customer", system: true));

        await new CustomerImportTarget(db).Invoking(t => t.ApplyAsync(org, User, [Update(2, V(("name", "Hijacked")), walkIn.UUID)]))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Row 2:*walk-in*");
        await new CustomerImportTarget(db).Invoking(t => t.ApplyAsync(org, User, [Create(3, V(("code", "c-walkin"), ("name", "Second")))]))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Row 3:*");

        (await ReloadAsync(db, walkIn.UUID)).SupplierName.Should().Be("Walk-in Customer");
        (await db.BusinessPartners.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Customer_apply_is_all_or_nothing()
    {
        var (db, org) = NewDb();
        var existing = await SeedAsync(db, Partner(org, "C-00001", "Keep me"));
        var target = new CustomerImportTarget(db);

        await target.Invoking(t => t.ApplyAsync(org, User,
        [
            Create(2, V(("name", "Fine"))),
            Update(3, V(("name", "Changed")), existing.UUID),
            Create(4, V(("name", "Broken"), ("country_code", "Narnia")))
        ])).Should().ThrowAsync<BadRequestException>().WithMessage("Row 4:*LOOKUP_NOT_FOUND*");

        (await db.BusinessPartners.IgnoreQueryFilters().CountAsync()).Should().Be(1);
        (await ReloadAsync(db, existing.UUID)).SupplierName.Should().Be("Keep me");

        // The context is clean afterwards: the next batch works.
        (await target.ApplyAsync(org, User, [Create(2, V(("name", "Fine")))])).Should().ContainSingle();
        (await db.BusinessPartners.IgnoreQueryFilters().CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Customer_import_onto_a_vendor_code_makes_the_partner_both()
    {
        var (db, org) = NewDb();
        var vendor = await SeedAsync(db, Partner(org, "V-1", "Vendor", vendor: true, customer: false));

        await new CustomerImportTarget(db).ApplyAsync(org, User, [Update(2, V(("code", "V-1")), vendor.UUID)]);

        var p = await ReloadAsync(db, vendor.UUID);
        (p.IsVendor, p.IsCustomer, p.PartnerType, p.CustomerType, p.SupplierName).Should().Be((true, true, "BOTH", "COMPANY", "Vendor"));
    }

    // ── SUPPLIER ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Imported_suppliers_are_created_approved_with_an_Imported_history_entry()
    {
        var (db, org) = NewDb();
        var audit = new Audit();
        var events = new Events();
        var target = new SupplierImportTarget(db, new Currencies(Pkr, Usd), audit: audit, events: events);

        var outcomes = await target.ApplyAsync(org, User,
        [
            Create(2, V(("code", "SUP-1"), ("name", "Global Parts"), ("currency_code", "usd"), ("payment_terms_days", "45"),
                ("country_code", "PK"), ("email", "s@gp.com"), ("tax_number", "T-1")))
        ]);

        var s = await ReloadAsync(db, outcomes.Single().EntityUuid);
        (s.IsVendor, s.IsCustomer, s.PartnerType, s.Status, s.IsActive).Should().Be((true, false, "VENDOR", "ACTIVE", true));
        (s.ApprovedBy, s.StatusChangedBy, s.CreatedBy).Should().Be(((int?)User, (int?)User, User));
        s.OnboardingDate.Should().NotBeNull();
        (s.PreferredCurrency, s.PaymentTermsDays, s.Country).Should().Be(((Guid?)Usd.CurrencyId, 45, "PK"));
        audit.Entries.Should().ContainSingle().Which.Should().Be(((int?)User, "STATUS_CHANGE", (Guid?)s.UUID, "ACTIVE", "Imported"));
        events.Approved.Should().Equal(s.UUID);
    }

    [Fact]
    public async Task Supplier_validate_checks_currencies_codes_and_status_changes()
    {
        var (db, org) = NewDb();
        var active = await SeedAsync(db, Partner(org, "SUP-1", "Active", vendor: true, customer: false));
        var pending = await SeedAsync(db, Partner(org, "SUP-2", "Pending", vendor: true, customer: false, status: "PENDING"));
        var customer = await SeedAsync(db, Partner(org, "C-00001", "Customer"));

        var checks = await new SupplierImportTarget(db, new Currencies(Pkr, Eur)).ValidateAsync(org,
        [
            In(2, V(("code", "sup-1"), ("is_active", "false"))),
            In(3, V(("code", "SUP-2"), ("is_active", "false"))),
            In(4, V(("code", "C-00001"))),
            In(5, V(("code", "SUP-9"), ("name", "New"), ("currency_code", "EUR"))),
            In(6, V(("code", "SUP-10"), ("name", "New"), ("currency_code", "XYZ"))),
            In(7, V(("code", "C-WALKIN"), ("name", "Walk"))),
            In(8, V(("code", "TOO-LONG-CODE"), ("name", "Long"))),
            In(9, V(("code", null), ("name", "No code"))),
            In(10, V(("code", "SUP-11")))
        ]);

        checks[0].ExistingEntityUuid.Should().Be(active.UUID);
        checks[0].Errors.Should().BeEmpty();
        checks[1].ExistingEntityUuid.Should().Be(pending.UUID);
        Codes(checks[1]).Should().Equal("STATUS_CHANGE_NOT_ALLOWED");
        checks[2].ExistingEntityUuid.Should().Be(customer.UUID);
        checks[2].Errors.Should().BeEmpty();
        Codes(checks[3]).Should().Equal("LOOKUP_NOT_FOUND");      // EUR is configured but inactive
        Codes(checks[4]).Should().Equal("LOOKUP_NOT_FOUND");
        Codes(checks[5]).Should().Equal("WALK_IN_PROTECTED");
        Codes(checks[6]).Should().Equal("MAX_LENGTH");
        Codes(checks[7]).Should().Equal("REQUIRED");
        Codes(checks[8]).Should().Equal("REQUIRED");               // a new supplier needs a name
    }

    [Fact]
    public async Task Supplier_update_keeps_blank_fields_deactivates_and_adds_the_vendor_role()
    {
        var (db, org) = NewDb();
        var audit = new Audit();
        var seeded = Partner(org, "SUP-1", "Old", vendor: true, customer: false, email: "old@s.com");
        seeded.City = "Karachi"; seeded.PreferredCurrency = Pkr.CurrencyId;
        await SeedAsync(db, seeded);
        var customer = await SeedAsync(db, Partner(org, "C-00001", "Customer"));

        var outcomes = await new SupplierImportTarget(db, new Currencies(Pkr), audit: audit).ApplyAsync(org, User,
        [
            Update(2, V(("code", "SUP-1"), ("name", "New"), ("city", null), ("currency_code", null), ("is_active", "false")), seeded.UUID),
            Update(3, V(("code", "C-00001")), customer.UUID)
        ]);

        outcomes.Select(o => o.Status).Should().Equal("UPDATED", "UPDATED");
        var s = await ReloadAsync(db, seeded.UUID);
        (s.SupplierName, s.City, s.Email, s.PreferredCurrency, s.Status, s.IsActive, s.ModifiedBy)
            .Should().Be(("New", "Karachi", "old@s.com", (Guid?)Pkr.CurrencyId, "INACTIVE", false, (int?)User));
        var both = await ReloadAsync(db, customer.UUID);
        (both.IsVendor, both.IsCustomer, both.PartnerType, both.ApprovedBy).Should().Be((true, true, "BOTH", (int?)User));
        audit.Entries.Should().ContainSingle(e => e.Entity == customer.UUID && e.Notes == "Imported");
    }

    [Fact]
    public async Task Supplier_apply_rolls_back_the_whole_batch_on_a_bad_row()
    {
        var (db, org) = NewDb();
        var audit = new Audit();
        var target = new SupplierImportTarget(db, new Currencies(Pkr), audit: audit);

        await target.Invoking(t => t.ApplyAsync(org, User,
        [
            Create(2, V(("code", "SUP-1"), ("name", "One"))),
            Create(3, V(("code", "SUP-1"), ("name", "Duplicate")))
        ])).Should().ThrowAsync<BadRequestException>().WithMessage("Row 3:*CODE_IN_USE*");

        (await db.BusinessPartners.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        audit.Entries.Should().BeEmpty();
    }

    // ── SUPPLIER_CONTACT ─────────────────────────────────────────────────────

    [Fact]
    public async Task Contacts_need_an_existing_supplier_and_match_on_supplier_plus_email()
    {
        var (db, org) = NewDb();
        var supplier = await SeedAsync(db, Partner(org, "SUP-1", "Vendor", vendor: true, customer: false));
        await SeedAsync(db, Partner(org, "C-00001", "Customer only"));
        db.SupplierContacts.Add(new SupplierContact { SupplierId = supplier.Id, OrganizationId = org, ContactName = "Sara", Email = "Sara@v.com", IsActive = true });
        await db.SaveChangesAsync();
        var contactId = (await db.SupplierContacts.AsNoTracking().SingleAsync()).Id;
        db.ChangeTracker.Clear();

        var checks = await new SupplierContactImportTarget(db).ValidateAsync(org,
        [
            In(2, V(("supplier_code", "sup-1"), ("name", "Sara K"), ("email", "sara@v.com"))),
            In(3, V(("supplier_code", "SUP-1"), ("name", "Ali"), ("email", "ali@v.com"))),
            In(4, V(("supplier_code", "SUP-404"), ("name", "Ghost"))),
            In(5, V(("supplier_code", "C-00001"), ("name", "Not a vendor")))
        ]);

        checks[0].ExistingEntityUuid.Should().Be(SupplierContactImportTarget.ToUuid(contactId));
        checks[0].Errors.Should().BeEmpty();
        checks[1].ExistingEntityUuid.Should().BeNull();
        checks[1].Errors.Should().BeEmpty();
        Codes(checks[2]).Should().Equal("LOOKUP_NOT_FOUND");
        Codes(checks[3]).Should().Equal("LOOKUP_NOT_FOUND");

        SupplierContactImportTarget.TryFromUuid(checks[0].ExistingEntityUuid!.Value, out var decoded).Should().BeTrue();
        decoded.Should().Be(contactId);
        SupplierContactImportTarget.TryFromUuid(Guid.NewGuid(), out _).Should().BeFalse();
    }

    [Fact]
    public async Task Contacts_are_created_and_updated_and_a_new_primary_demotes_the_old_one()
    {
        var (db, org) = NewDb();
        var supplier = await SeedAsync(db, Partner(org, "SUP-1", "Vendor", vendor: true, customer: false));
        db.SupplierContacts.Add(new SupplierContact { SupplierId = supplier.Id, OrganizationId = org, ContactName = "Sara", Email = "sara@v.com",
            Phone = "0300", Title = "Sales", IsPrimary = true, IsActive = true });
        await db.SaveChangesAsync();
        var sara = (await db.SupplierContacts.AsNoTracking().SingleAsync()).Id;
        db.ChangeTracker.Clear();

        var outcomes = await new SupplierContactImportTarget(db).ApplyAsync(org, User,
        [
            Update(2, V(("supplier_code", "SUP-1"), ("name", "Sara Khan"), ("phone", null), ("email", "sara@v.com")), SupplierContactImportTarget.ToUuid(sara)),
            Create(3, V(("supplier_code", "SUP-1"), ("name", "Ali"), ("email", "ali@v.com"), ("is_primary", "true")))
        ]);

        var contacts = await db.SupplierContacts.AsNoTracking().OrderBy(c => c.Id).ToListAsync();
        contacts.Should().HaveCount(2);
        (contacts[0].ContactName, contacts[0].Phone, contacts[0].Title, contacts[0].IsPrimary).Should().Be(("Sara Khan", "0300", "Sales", false));
        (contacts[1].ContactName, contacts[1].SupplierId, contacts[1].OrganizationId, contacts[1].IsPrimary).Should().Be(("Ali", supplier.Id, org, true));
        outcomes.Should().Equal(
            new ImportRowOutcome(2, "UPDATED", SupplierContactImportTarget.ToUuid(sara)),
            new ImportRowOutcome(3, "IMPORTED", SupplierContactImportTarget.ToUuid(contacts[1].Id)));
    }

    [Fact]
    public async Task Contact_apply_is_all_or_nothing()
    {
        var (db, org) = NewDb();
        await SeedAsync(db, Partner(org, "SUP-1", "Vendor", vendor: true, customer: false));

        await new SupplierContactImportTarget(db).Invoking(t => t.ApplyAsync(org, User,
        [
            Create(2, V(("supplier_code", "SUP-1"), ("name", "Ali"))),
            Create(3, V(("supplier_code", "SUP-404"), ("name", "Ghost")))
        ])).Should().ThrowAsync<BadRequestException>().WithMessage("Row 3:*LOOKUP_NOT_FOUND*");

        (await db.SupplierContacts.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }
}
