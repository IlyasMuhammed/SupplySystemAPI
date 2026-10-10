using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Suppliers.Controllers;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Suppliers.Models;
using SMS.Modules.Suppliers.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Suppliers.Tests;

/// <summary>A37 D-13 / D-16 — customer master over BusinessPartners: walk-in (CUST-01/04), codes (CUST-02), gate, search, sync.</summary>
public class CustomerMasterTests
{
    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public DateTime Now { get; set; } = utc;
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    private sealed class Gate(bool creditEnabled) : IModuleGate
    {
        public Task<bool> IsEnabledAsync(Guid organizationId, string featureCode, CancellationToken ct = default) =>
            Task.FromResult(featureCode != ModuleCodes.CreditManagement || creditEnabled);
    }

    private sealed class Balances(Dictionary<Guid, CustomerBalanceInfo> data) : ICustomerBalanceLookup
    {
        public Task<IReadOnlyDictionary<Guid, CustomerBalanceInfo>> GetAsync(Guid organizationId, IReadOnlyCollection<Guid> partnerIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, CustomerBalanceInfo>>(data.Where(d => partnerIds.Contains(d.Key)).ToDictionary());
        public Task<string?> GetBaseCurrencyCodeAsync(Guid organizationId, CancellationToken ct = default) => Task.FromResult<string?>("PKR");
    }

    private static (CustomerService Service, SuppliersDbContext Db, Guid Org) New(
        IModuleGate? gate = null, Dictionary<Guid, CustomerBalanceInfo>? balances = null)
    {
        var (db, tenant, _) = SuppliersTestDb.New();
        return (new CustomerService(db, new WalkInCustomerSeeder(db), new Balances(balances ?? []), gate), db, tenant.OrganizationId);
    }

    private static CustomerUpsert Upsert(string name, string? type = null, decimal? credit = null, string? phone = null) =>
        new() { Name = name, CustomerType = type, CreditLimit = credit, Phone = phone };

    private static async Task<BusinessPartner> WalkInAsync(CustomerService service, SuppliersDbContext db)
    {
        await service.SearchAsync(null, 10);
        return await db.BusinessPartners.SingleAsync(p => p.IsSystem);
    }

    // ── CUST-01 walk-in ──────────────────────────────────────────────────────

    [Fact]
    public async Task The_walk_in_customer_is_seeded_once_per_organization()
    {
        var (db, _, _) = SuppliersTestDb.New();
        var org = Guid.NewGuid();
        var seeder = new WalkInCustomerSeeder(db);
        await seeder.EnsureForAllAsync([org, org]);
        WalkInCustomerSeeder.ResetCache();
        await seeder.EnsureAsync(org);

        var rows = await db.BusinessPartners.IgnoreQueryFilters().Where(p => p.OrganizationId == org).ToListAsync();
        rows.Should().ContainSingle();
        var w = rows[0];
        (w.SupplierCode, w.IsCustomer, w.IsVendor, w.IsSystem, w.CustomerType, w.Status, w.IsActive, w.CreditLimit)
            .Should().Be(("C-WALKIN", true, false, true, "WALK_IN", "ACTIVE", true, (decimal?)0m));
        w.PartnerType.Should().Be("CUSTOMER");
    }

    [Fact]
    public async Task The_walk_in_customer_cannot_be_deactivated_retyped_or_given_credit()
    {
        var (service, db, _) = New();
        var walkIn = await WalkInAsync(service, db);

        await service.Invoking(s => s.SetStatusAsync(walkIn.UUID, false, 1)).Should()
            .ThrowAsync<BadRequestException>().WithMessage("The walk-in customer cannot be deactivated.");
        await service.Invoking(s => s.UpdateAsync(walkIn.UUID, Upsert("Counter", "INDIVIDUAL"), 1)).Should()
            .ThrowAsync<BadRequestException>().WithMessage("The walk-in customer cannot be changed to another type.");
        await service.Invoking(s => s.UpdateAsync(walkIn.UUID, Upsert("Counter", credit: 100m), 1)).Should()
            .ThrowAsync<BadRequestException>().WithMessage("Walk-in customers cannot have a credit limit.");

        await service.UpdateAsync(walkIn.UUID, Upsert("Counter sales", phone: "0300"), 1);   // renaming is fine
        (await service.GetAsync(walkIn.UUID))!.Name.Should().Be("Counter sales");
    }

    [Fact]
    public async Task Older_endpoints_cannot_delete_or_deactivate_the_walk_in_either()
    {
        var (service, db, _) = New();
        var walkIn = await WalkInAsync(service, db);

        walkIn.IsDelete = true;
        await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowAsync<BadRequestException>().WithMessage("The walk-in customer cannot be deleted.");
        walkIn.IsDelete = false;
        walkIn.IsActive = false;
        await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowAsync<BadRequestException>().WithMessage("The walk-in customer cannot be deactivated.");
        walkIn.IsActive = true;
        walkIn.IsCustomer = false;
        await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowAsync<BadRequestException>().WithMessage("The walk-in customer cannot be changed to another type.");
    }

    // ── CUST-04 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_walk_in_type_customer_must_have_zero_credit()
    {
        var (service, db, _) = New();
        await service.Invoking(s => s.CreateAsync(Upsert("Cash buyer", "WALK_IN", 50m), 1)).Should()
            .ThrowAsync<BadRequestException>().WithMessage("Walk-in customers cannot have a credit limit.");

        var id = await service.CreateAsync(Upsert("Firm", "COMPANY", 500m), 1);
        await service.UpdateAsync(id, Upsert("Firm", "WALK_IN"), 1);   // retyping clears the limit
        (await db.BusinessPartners.SingleAsync(p => p.UUID == id)).CreditLimit.Should().Be(0m);
    }

    // ── CUST-02 codes ────────────────────────────────────────────────────────

    [Fact]
    public async Task New_customers_get_sequential_codes_per_organization()
    {
        var (service, db, org) = New();
        db.BusinessPartners.Add(new BusinessPartner { UUID = Guid.NewGuid(), OrganizationId = org, SupplierCode = "C-00007",
            SupplierName = "Old", IsCustomer = true, IsVendor = false, PartnerType = "CUSTOMER", IsDelete = true });
        await db.SaveChangesAsync();

        var a = await service.CreateAsync(Upsert("A"), 1);
        var b = await service.CreateAsync(Upsert("B"), 1);
        var other = New();
        var c = await other.Service.CreateAsync(Upsert("C"), 1);

        (await service.GetAsync(a))!.Code.Should().Be("C-00008");
        (await service.GetAsync(b))!.Code.Should().Be("C-00009");
        (await other.Service.GetAsync(c))!.Code.Should().Be("C-00001");
        var created = await service.GetAsync(a);
        (created!.CustomerType, created.IsActive, created.IsVendor, created.IsSystem).Should().Be(("COMPANY", true, false, false));
    }

    [Fact]
    public async Task Name_and_type_are_validated()
    {
        var (service, _, _) = New();
        await service.Invoking(s => s.CreateAsync(Upsert(" "), 1)).Should().ThrowAsync<BadRequestException>().WithMessage("Name is required.");
        await service.Invoking(s => s.CreateAsync(Upsert("X", "VIP"), 1)).Should().ThrowAsync<BadRequestException>();
    }

    // ── Credit management gate ───────────────────────────────────────────────

    [Fact]
    public async Task Credit_limit_is_written_only_with_credit_management()
    {
        var (off, offDb, _) = New(new Gate(false));
        var id = await off.CreateAsync(Upsert("Off", credit: 900m), 1);
        (await offDb.BusinessPartners.SingleAsync(p => p.UUID == id)).CreditLimit.Should().BeNull();
        var row = await offDb.BusinessPartners.SingleAsync(p => p.UUID == id);
        row.CreditLimit = 300m; await offDb.SaveChangesAsync();
        await off.UpdateAsync(id, Upsert("Off", credit: 5m), 1);
        row.CreditLimit.Should().Be(300m, "ignored on write, the existing limit stays");

        var (on, onDb, _) = New(new Gate(true));
        var id2 = await on.CreateAsync(Upsert("On", credit: 900m), 1);
        (await onDb.BusinessPartners.SingleAsync(p => p.UUID == id2)).CreditLimit.Should().Be(900m);

        var (noGate, ngDb, _) = New();
        var id3 = await noGate.CreateAsync(Upsert("NoRegistry", credit: 10m), 1);
        (await ngDb.BusinessPartners.SingleAsync(p => p.UUID == id3)).CreditLimit.Should().Be(10m, "no registry ⇒ enabled");
    }

    // ── Status ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_toggles_a_customer_and_leaves_a_vendors_workflow_status_alone()
    {
        var (service, db, org) = New();
        var id = await service.CreateAsync(Upsert("Pure"), 1);
        await service.SetStatusAsync(id, false, 7);
        var pure = await db.BusinessPartners.SingleAsync(p => p.UUID == id);
        (pure.IsActive, pure.Status, pure.StatusChangedBy).Should().Be((false, "INACTIVE", (int?)7));
        (await service.ListAsync(new CustomerListFilter { Status = "INACTIVE" })).Data.Should().ContainSingle(c => c.Uuid == id);
        await service.SetStatusAsync(id, true, 7);
        (pure.IsActive, pure.Status).Should().Be((true, "ACTIVE"));

        var both = new BusinessPartner { UUID = Guid.NewGuid(), OrganizationId = org, SupplierCode = "V1", SupplierName = "Both",
            IsVendor = true, IsCustomer = true, PartnerType = "BOTH", Status = "SUSPENDED", IsActive = true };
        db.BusinessPartners.Add(both); await db.SaveChangesAsync();
        await service.SetStatusAsync(both.UUID, false, 7);
        (both.IsActive, both.Status).Should().Be((false, "SUSPENDED"));
        both.CustomerType.Should().Be("COMPANY", "a customer saved through any path gets the default type");
    }

    // ── Search & list ────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_puts_exact_matches_first_then_starts_with_then_contains_and_skips_inactive()
    {
        var (service, _, _) = New();
        var contains = await service.CreateAsync(Upsert("Shop 12"), 1);
        var starts = await service.CreateAsync(Upsert("12 Stores"), 1);
        var exact = await service.CreateAsync(Upsert("Zulu", phone: "12"), 1);
        var inactive = await service.CreateAsync(Upsert("12 Gone"), 1);
        await service.SetStatusAsync(inactive, false, 1);

        var hits = await service.SearchAsync("12", 10);
        hits.Select(h => h.Uuid).Should().Equal(exact, starts, contains);
        (await service.SearchAsync("c-0000", 2)).Should().HaveCount(2);
    }

    [Fact]
    public async Task List_includes_the_walk_in_and_sorts_by_balance()
    {
        var ids = new Dictionary<Guid, CustomerBalanceInfo>();
        var (service, _, _) = New(balances: ids);
        var a = await service.CreateAsync(Upsert("A"), 1);
        var b = await service.CreateAsync(Upsert("B"), 1);
        ids[a] = new CustomerBalanceInfo(10m, 0m, "PKR");
        ids[b] = new CustomerBalanceInfo(99m, 5m, "PKR");

        var page = await service.ListAsync(new CustomerListFilter { SortField = "balance", SortOrder = "desc" });
        page.TotalRecords.Should().Be(3);
        page.Data.Select(d => d.Name).Should().Equal("B", "A", "Walk-in Customer");
        page.Data[0].Balance.Should().Be(99m);
        page.Data[2].IsSystem.Should().BeTrue();

        var bal = await service.GetBalanceAsync(b);
        bal.Should().Be(new CustomerBalanceModel(99m, "PKR", 5m));
        (await service.GetBalanceAsync(Guid.NewGuid())).Should().BeNull();
    }

    // ── Sync (D-16) ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Sync_returns_only_changes_strictly_after_since_and_never_splits_a_tie()
    {
        var (service, db, _) = New();
        var t0 = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        var clock = new FixedClock(t0);
        db.Clock = clock;
        await service.SyncAsync(null, 10);                      // seeds the walk-in at t0
        clock.Now = t0.AddMinutes(1);
        var a = await service.CreateAsync(Upsert("A"), 1);
        var b = await service.CreateAsync(Upsert("B"), 1);      // a and b tie at t0+1
        clock.Now = t0.AddMinutes(2);
        var c = await service.CreateAsync(Upsert("C"), 1);
        await service.SetStatusAsync(c, false, 1);

        var after = await service.SyncAsync(t0, 10);
        after.Customers.Select(x => x.Uuid).Should().Equal(a, b, c);
        after.HasMore.Should().BeFalse();
        after.Customers[2].IsActive.Should().BeFalse("deactivations travel too");

        var paged = await service.SyncAsync(t0, 1);
        paged.HasMore.Should().BeTrue();
        paged.Customers.Select(x => x.Uuid).Should().Equal(a, b);
        (await service.SyncAsync(paged.Customers[^1].ModifiedAt, 1)).Customers.Select(x => x.Uuid).Should().Equal(c);
        (await service.SyncAsync(null, 10)).Customers.Should().HaveCount(4);
    }

    [Fact]
    public async Task ModifiedAt_moves_on_every_save()
    {
        var (service, db, _) = New();
        var clock = new FixedClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        db.Clock = clock;
        var id = await service.CreateAsync(Upsert("A"), 1);
        clock.Now = clock.Now.AddHours(3);
        await service.UpdateAsync(id, Upsert("A2"), 1);
        (await service.GetAsync(id))!.ModifiedAt.Should().Be(clock.Now);
    }

    // ── Balance over Finance's sales invoices ────────────────────────────────

    [Fact]
    public async Task Balance_sums_open_invoices_at_their_locked_rate_and_splits_out_the_overdue_part()
    {
        var org = Guid.NewGuid();
        var finance = new FinanceDbContext(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = org });
        var customer = Guid.NewGuid();
        var today = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
        void Add(string status, decimal due, decimal? rate, int dueInDays, Guid? orgId = null) => finance.SalesInvoices.Add(new SalesInvoice
        {
            UUID = Guid.NewGuid(), OrganizationId = orgId ?? org, PartnerId = customer, Status = status, BalanceDue = due,
            ExchangeRate = rate, DueDate = today.AddDays(dueInDays), InvoiceNumber = "SINV", BaseCurrencyCode = "PKR"
        });
        Add("ISSUED", 100m, 1m, 5);
        Add("OVERDUE", 10m, 280m, -3);           // 10 USD at 280 = 2800 base, overdue
        Add("PARTIALLY_PAID", 50m, null, -1);    // overdue
        Add("DRAFT", 1000m, 1m, -9);
        Add("PAID", 0m, 1m, -9);
        Add("ISSUED", 777m, 1m, -9, Guid.NewGuid());   // another organization
        await finance.SaveChangesAsync();

        var lookup = new SalesInvoiceCustomerBalanceLookup(finance, clock: new FixedClock(today));
        var result = await lookup.GetAsync(org, [customer]);

        result[customer].Should().Be(new CustomerBalanceInfo(2950m, 2850m, "PKR"));
        (await lookup.GetAsync(org, [Guid.NewGuid()])).Should().BeEmpty();
    }

    // ── Permissions & module gate metadata ───────────────────────────────────

    [Theory]
    [InlineData(nameof(CustomersController.GetCustomers), PermissionCodes.CUSTOMER_VIEW)]
    [InlineData(nameof(CustomersController.Search), PermissionCodes.CUSTOMER_VIEW)]
    [InlineData(nameof(CustomersController.GetCustomer), PermissionCodes.CUSTOMER_VIEW)]
    [InlineData(nameof(CustomersController.GetBalance), PermissionCodes.CUSTOMER_VIEW)]
    [InlineData(nameof(CustomersController.CreateCustomer), PermissionCodes.CUSTOMER_CREATE)]
    [InlineData(nameof(CustomersController.UpdateCustomer), PermissionCodes.CUSTOMER_EDIT)]
    [InlineData(nameof(CustomersController.SetStatus), PermissionCodes.CUSTOMER_DEACTIVATE)]
    public void Every_customer_action_requires_its_permission(string action, string code)
    {
        var attribute = typeof(CustomersController).GetMethod(action)!.GetCustomAttributes<RequirePermissionAttribute>().Single();
        attribute.AnyOf.Should().Equal(code);
    }

    [Fact]
    public void Customer_endpoints_sit_behind_the_customers_module()
    {
        foreach (var controller in new[] { typeof(CustomersController), typeof(CustomerSyncController) })
            controller.GetCustomAttribute<RequiresFeatureAttribute>()!.AnyOfFeatureCodes.Should().Equal(ModuleCodes.Customers);
        var sync = typeof(CustomerSyncController).GetMethod(nameof(CustomerSyncController.GetChanges))!
            .GetCustomAttributes<RequirePermissionAttribute>().Single();
        sync.AnyOf.Should().Equal(PermissionCodes.CUSTOMER_VIEW);
    }

    // ── Migration ────────────────────────────────────────────────────────────

    [Fact]
    public void The_A37_migration_is_guarded_additive_sql()
    {
        using var db = new SuppliersDbContext(
            new DbContextOptionsBuilder<SuppliersDbContext>().UseSqlServer("Server=none;Database=none;Trusted_Connection=True;").Options,
            new StaticTenantContext());
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.Migrations.Select(m => assembly.CreateMigration(m.Value, "Microsoft.EntityFrameworkCore.SqlServer"))
            .Single(m => m.GetType().Name == "A37_CustomerMasterColumns");

        migration.UpOperations.Should().OnlyContain(op => op is SqlOperation);
        var sql = string.Join("\n", migration.UpOperations.OfType<SqlOperation>().Select(o => o.Sql));
        foreach (var column in new[] { "CustomerType", "Mobile", "PaymentTermsDays", "IsSystem", "ModifiedAt" })
            sql.Should().Contain($"COL_LENGTH(N'suppliers.BusinessPartners', N'{column}') IS NULL");
        sql.Should().Contain("SET [CustomerType] = N''COMPANY'' WHERE [IsCustomer] = 1");
        sql.Should().Contain("SET [ModifiedAt] = COALESCE([ModifiedDate], [CreatedDate])");
        sql.Should().Contain("IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_BusinessPartners_OrganizationId_ModifiedAt'");
        sql.Should().NotContainAny("DROP ", "ALTER COLUMN");
    }
}
