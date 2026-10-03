using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Controllers;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A32 PA-03/PA-04/PA-06 — rejection reasons (§7, BR-C5-01..03): the ten seeded codes per organization, the
/// idempotent backfill, and the CRUD rules — unique per org, seeded codes never deleted, own organization only.
/// </summary>
public class RejectionReasonServiceTests
{
    private const int User = 7;
    private static readonly string[] SpecCodes = ["NIP", "OOS", "DIS", "MOQ", "GEO", "REG", "CAP", "CRD", "PRC", "OTH"];

    private sealed record Harness(DemandDbContext Db, RejectionReasonService Service, StaticTenantContext Tenant);

    private static Harness NewHarness(string? dbName = null, Guid? orgId = null, bool superAdmin = false)
    {
        var tenant = new StaticTenantContext { OrganizationId = orgId ?? Guid.NewGuid(), IsSuperAdmin = superAdmin };
        var db = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString()).Options, tenant);
        return new Harness(db, new RejectionReasonService(db, tenant), tenant);
    }

    // ── PA-03 seeding ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Seeding_an_organization_inserts_the_ten_spec_codes_as_active_system_reasons_in_spec_order()
    {
        var h = NewHarness();

        var added = await h.Service.EnsureSeededAsync(h.Tenant.OrganizationId);

        added.Should().Be(10);
        var rows = await h.Db.RejectionReasons.OrderBy(r => r.DisplayOrder).ToListAsync();
        rows.Select(r => r.Code).Should().Equal(SpecCodes);
        rows.Should().OnlyContain(r => r.IsSystem && r.IsActive && r.OrganizationId == h.Tenant.OrganizationId);
        rows.Single(r => r.Code == "OOS").Description.Should().Be("Out of stock — no replenishment planned");
        rows.Single(r => r.Code == "OTH").Description.Should().Be("Other (see notes)");
        rows.Select(r => r.DisplayOrder).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Seeding_twice_adds_nothing_and_never_undoes_a_rename_or_a_deactivation()
    {
        var h = NewHarness();
        await h.Service.EnsureSeededAsync(h.Tenant.OrganizationId);
        var oos = await h.Db.RejectionReasons.SingleAsync(r => r.Code == "OOS");
        oos.Description = "Out of stock (renamed)";
        oos.IsActive = false;
        h.Db.RejectionReasons.Remove(await h.Db.RejectionReasons.SingleAsync(r => r.Code == "GEO")); // simulate a lost row
        await h.Db.SaveChangesAsync();

        var added = await h.Service.EnsureSeededAsync(h.Tenant.OrganizationId);

        added.Should().Be(1, "only the missing GEO comes back");
        (await h.Db.RejectionReasons.CountAsync()).Should().Be(10);
        var reloaded = await h.Db.RejectionReasons.AsNoTracking().SingleAsync(r => r.Code == "OOS");
        reloaded.Description.Should().Be("Out of stock (renamed)");
        reloaded.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Seeding_another_organization_stamps_that_organization_whatever_the_callers_tenant_is()
    {
        var dbName = Guid.NewGuid().ToString();
        var admin = NewHarness(dbName, superAdmin: true);
        var newOrg = Guid.NewGuid();

        await admin.Service.EnsureSeededAsync(admin.Tenant.OrganizationId);
        var added = await admin.Service.EnsureSeededAsync(newOrg);

        added.Should().Be(10, "the caller's own seeded rows must not count as the new organization's");
        var all = await admin.Db.RejectionReasons.IgnoreQueryFilters().ToListAsync();
        all.Count(r => r.OrganizationId == newOrg).Should().Be(10);
        all.Count(r => r.OrganizationId == admin.Tenant.OrganizationId).Should().Be(10);
    }

    [Fact]
    public async Task The_startup_backfill_seeds_every_organization_it_is_given_once()
    {
        var h = NewHarness();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        var first  = await h.Service.EnsureSeededForAllAsync([orgA, orgB]);
        var second = await h.Service.EnsureSeededForAllAsync([orgA, orgB]);

        first.Should().Be(20);
        second.Should().Be(0);
    }

    [Fact]
    public async Task The_provisioning_hook_seeds_the_new_organization()
    {
        var h = NewHarness();
        var newOrg = Guid.NewGuid();
        IOrganizationProvisionedHandler handler = new RejectionReasonProvisioningHandler(h.Service);

        await handler.OnOrganizationProvisionedAsync(newOrg);

        (await h.Db.RejectionReasons.IgnoreQueryFilters().CountAsync(r => r.OrganizationId == newOrg)).Should().Be(10);
    }

    // ── PA-04 reads ───────────────────────────────────────────────────────────

    [Fact]
    public async Task The_list_is_active_reasons_of_the_callers_organization_by_display_order_then_code()
    {
        var dbName = Guid.NewGuid().ToString();
        var h = NewHarness(dbName, superAdmin: true); // super admin: the EF filter is off, the service must still filter
        await h.Service.EnsureSeededAsync(h.Tenant.OrganizationId);
        await h.Service.EnsureSeededAsync(Guid.NewGuid());               // another organization's ten
        var dis = await h.Db.RejectionReasons.SingleAsync(r => r.Code == "DIS" && r.OrganizationId == h.Tenant.OrganizationId);
        dis.IsActive = false;
        var nip = await h.Db.RejectionReasons.SingleAsync(r => r.Code == "NIP" && r.OrganizationId == h.Tenant.OrganizationId);
        nip.DisplayOrder = 999;
        await h.Db.SaveChangesAsync();

        var active = await h.Service.GetListAsync(includeInactive: false);
        var all    = await h.Service.GetListAsync(includeInactive: true);

        active.Select(r => r.Code).Should().Equal("OOS", "MOQ", "GEO", "REG", "CAP", "CRD", "PRC", "OTH", "NIP");
        all.Should().HaveCount(10);
        all.Single(r => r.Code == "DIS").IsActive.Should().BeFalse();
    }

    // ── PA-04 writes ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_new_reason_is_stored_upper_case_active_custom_and_last_by_default()
    {
        var h = NewHarness();
        await h.Service.EnsureSeededAsync(h.Tenant.OrganizationId);

        var created = await h.Service.CreateAsync(new CreateRejectionReasonRequest { Code = " lt_9 ", Description = " Lead time too long " }, User);

        created.Code.Should().Be("LT_9");
        created.Description.Should().Be("Lead time too long");
        created.IsActive.Should().BeTrue();
        created.IsSystem.Should().BeFalse();
        created.DisplayOrder.Should().BeGreaterThan(100);
        var row = await h.Db.RejectionReasons.SingleAsync(r => r.UUID == created.Uuid);
        row.OrganizationId.Should().Be(h.Tenant.OrganizationId);
        row.CreatedBy.Should().Be(User);
    }

    [Theory]
    [InlineData("OOS")]
    [InlineData("oos")]
    public async Task A_code_already_used_in_the_organization_is_a_conflict_whatever_its_case(string code)
    {
        var h = NewHarness();
        await h.Service.EnsureSeededAsync(h.Tenant.OrganizationId);

        var act = () => h.Service.CreateAsync(new CreateRejectionReasonRequest { Code = code, Description = "Dup" }, User);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task The_same_code_in_another_organization_is_no_conflict()
    {
        var dbName = Guid.NewGuid().ToString();
        var other = NewHarness(dbName);
        await other.Service.CreateAsync(new CreateRejectionReasonRequest { Code = "XYZ", Description = "Theirs" }, User);
        var mine = NewHarness(dbName, superAdmin: true);

        var created = await mine.Service.CreateAsync(new CreateRejectionReasonRequest { Code = "XYZ", Description = "Mine" }, User);

        created.Code.Should().Be("XYZ");
    }

    [Theory]
    [InlineData("", "Desc")]
    [InlineData("TOOLONGCODE", "Desc")]
    [InlineData("A B", "Desc")]
    [InlineData("A-B", "Desc")]
    [InlineData("OK", "")]
    public async Task An_empty_or_malformed_code_or_an_empty_description_is_refused(string code, string description)
    {
        var h = NewHarness();

        var act = () => h.Service.CreateAsync(new CreateRejectionReasonRequest { Code = code, Description = description }, User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task A_description_over_200_characters_is_refused()
    {
        var h = NewHarness();

        var act = () => h.Service.CreateAsync(new CreateRejectionReasonRequest { Code = "LONG", Description = new string('x', 201) }, User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task A_seeded_reason_can_be_renamed_and_reordered_but_keeps_its_code()
    {
        var h = NewHarness();
        await h.Service.EnsureSeededAsync(h.Tenant.OrganizationId);
        var oos = await h.Db.RejectionReasons.SingleAsync(r => r.Code == "OOS");

        var updated = await h.Service.UpdateAsync(oos.UUID, new UpdateRejectionReasonRequest { Description = "No stock", DisplayOrder = 5 }, User);

        updated!.Code.Should().Be("OOS");
        updated.Description.Should().Be("No stock");
        updated.DisplayOrder.Should().Be(5);
        updated.IsSystem.Should().BeTrue();
    }

    [Fact]
    public async Task A_deactivated_reason_leaves_the_dropdown_and_comes_back_when_reactivated()
    {
        var h = NewHarness();
        await h.Service.EnsureSeededAsync(h.Tenant.OrganizationId);
        var cap = await h.Db.RejectionReasons.SingleAsync(r => r.Code == "CAP");

        var off = await h.Service.SetActiveAsync(cap.UUID, false, User);
        var listedWhileOff = (await h.Service.GetListAsync(false)).Select(r => r.Code).ToList();
        var on = await h.Service.SetActiveAsync(cap.UUID, true, User);

        off!.IsActive.Should().BeFalse();
        listedWhileOff.Should().NotContain("CAP");
        on!.IsActive.Should().BeTrue();
        (await h.Service.GetListAsync(false)).Select(r => r.Code).Should().Contain("CAP");
    }

    [Fact]
    public async Task A_seeded_reason_can_never_be_deleted()
    {
        var h = NewHarness();
        await h.Service.EnsureSeededAsync(h.Tenant.OrganizationId);
        var oth = await h.Db.RejectionReasons.SingleAsync(r => r.Code == "OTH");

        var act = () => h.Service.DeleteAsync(oth.UUID, User);

        await act.Should().ThrowAsync<ConflictException>();
        (await h.Db.RejectionReasons.CountAsync()).Should().Be(10);
    }

    [Fact]
    public async Task A_custom_reason_a_line_already_uses_cannot_be_deleted()
    {
        var h = NewHarness();
        var custom = await h.Service.CreateAsync(new CreateRejectionReasonRequest { Code = "USED", Description = "Used" }, User);
        var reasonId = (await h.Db.RejectionReasons.SingleAsync(r => r.UUID == custom.Uuid)).Id;
        h.Db.SaleInquiries.Add(new SaleInquiry
        {
            InquiryNumber = "INQ-2026-00001", PartnerId = Guid.NewGuid(), ReceivedDate = DateTime.UtcNow.Date, CreatedBy = User,
            Lines = { new SaleInquiryLine { LineNumber = 1, ProductDescription = "x", RequestedQuantity = 1, LineStatus = "CANNOT_SUPPLY", RejectionReasonId = reasonId } }
        });
        await h.Db.SaveChangesAsync();

        var act = () => h.Service.DeleteAsync(custom.Uuid, User);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task A_custom_reason_used_only_on_a_quotation_line_cannot_be_deleted_either()
    {
        var h = NewHarness();
        var custom = await h.Service.CreateAsync(new CreateRejectionReasonRequest { Code = "QUSED", Description = "Used" }, User);
        var reasonId = (await h.Db.RejectionReasons.SingleAsync(r => r.UUID == custom.Uuid)).Id;
        h.Db.SaleQuotations.Add(new SaleQuotation
        {
            QuotationNumber = "SQ-2026-00001", PartnerId = Guid.NewGuid(), CurrencyId = Guid.NewGuid(),
            ValidFrom = DateTime.UtcNow.Date, ValidTo = DateTime.UtcNow.Date.AddDays(30), CreatedBy = User,
            Lines = { new SaleQuotationLine { LineNumber = 1, ProductDescription = "x", Quantity = 1, LineType = "REJECTED", RejectionReasonId = reasonId } }
        });
        await h.Db.SaveChangesAsync();

        var act = () => h.Service.DeleteAsync(custom.Uuid, User);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task An_unused_custom_reason_is_deleted()
    {
        var h = NewHarness();
        var custom = await h.Service.CreateAsync(new CreateRejectionReasonRequest { Code = "TEMP", Description = "Temp" }, User);

        var deleted = await h.Service.DeleteAsync(custom.Uuid, User);

        deleted.Should().BeTrue();
        (await h.Db.RejectionReasons.AnyAsync(r => r.UUID == custom.Uuid)).Should().BeFalse();
    }

    [Fact]
    public async Task Another_organizations_reason_is_not_found_for_any_write_even_for_a_super_admin()
    {
        var dbName = Guid.NewGuid().ToString();
        var theirs = NewHarness(dbName);
        var reason = await theirs.Service.CreateAsync(new CreateRejectionReasonRequest { Code = "THEIRS", Description = "Theirs" }, User);
        var admin = NewHarness(dbName, superAdmin: true);

        (await admin.Service.GetByIdAsync(reason.Uuid)).Should().BeNull();
        (await admin.Service.UpdateAsync(reason.Uuid, new UpdateRejectionReasonRequest { Description = "Hijack", DisplayOrder = 1 }, User)).Should().BeNull();
        (await admin.Service.SetActiveAsync(reason.Uuid, false, User)).Should().BeNull();
        (await admin.Service.DeleteAsync(reason.Uuid, User)).Should().BeFalse();
        (await theirs.Db.RejectionReasons.AsNoTracking().SingleAsync(r => r.UUID == reason.Uuid)).Description.Should().Be("Theirs");
    }

    // ── Gating (server = frontend guard) ──────────────────────────────────────

    [Theory]
    [InlineData(nameof(RejectionReasonsController.GetList),
        "SALE_INQUIRY_VIEW SALE_INQUIRY_EDIT SALE_QUOTATION_VIEW SALE_QUOTATION_EDIT SALE_REJECTION_REASON_MANAGE")]
    [InlineData(nameof(RejectionReasonsController.Create),     "SALE_REJECTION_REASON_MANAGE")]
    [InlineData(nameof(RejectionReasonsController.Update),     "SALE_REJECTION_REASON_MANAGE")]
    [InlineData(nameof(RejectionReasonsController.Deactivate), "SALE_REJECTION_REASON_MANAGE")]
    [InlineData(nameof(RejectionReasonsController.Activate),   "SALE_REJECTION_REASON_MANAGE")]
    [InlineData(nameof(RejectionReasonsController.Delete),     "SALE_REJECTION_REASON_MANAGE")]
    public void Every_action_asks_for_exactly_the_contracts_permissions(string action, string codes)
    {
        var method = typeof(RejectionReasonsController).GetMethod(action)!;
        var attribute = method.GetCustomAttribute<RequirePermissionAttribute>();

        attribute.Should().NotBeNull();
        attribute!.AnyOf.Should().BeEquivalentTo(codes.Split(' '));
        typeof(RejectionReasonsController).GetCustomAttribute<RouteAttribute>()!.Template.Should().Be("api/rejection-reasons");
    }
}
