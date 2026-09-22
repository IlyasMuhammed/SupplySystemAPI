using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>A29-P3-03 §3.2/§3.5 — GetConfig/UpdateConfig and the per-field audit trail.</summary>
public class SaleOrderConfigServiceTests
{
    private const int User = 42;

    private sealed record Harness(DemandDbContext Db, SaleOrderConfigService Service, Guid OrgId, string DbName);

    private static Harness NewHarness(params int[] departmentIds)
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(dbName).Options, tenant);

        var userQuery = new Mock<IUserQueryService>();
        userQuery.Setup(u => u.GetUsersAsync(It.IsAny<IReadOnlyList<int>>()))
            .ReturnsAsync((IReadOnlyList<int> ids) => ids.Select(id => new UserIdentity(id, $"User {id}")).ToList());

        var orgChart = new Mock<IOrgChartService>();
        orgChart.Setup(o => o.GetDepartmentsAsync()).ReturnsAsync(
            (IReadOnlyList<DepartmentSummary>)departmentIds.Select(id => new DepartmentSummary(id, $"Dept {id}", null, true)).ToList());

        return new Harness(db, new SaleOrderConfigService(db, userQuery.Object, orgChart.Object), tenant.OrganizationId, dbName);
    }

    private static UpdateSaleOrderConfigRequest ValidUpdate() => new()
    {
        AutoPoEnabled = true, SupplierSelectionMode = SupplierSelectionModes.BestMatch,
        AutoPoApprovalMode = AutoPoApprovalModes.RequireWorkflow, DropShipEnabled = false,
        SelfPickupEnabled = true, DefaultFulfillmentMode = FulfillmentModes.InStock,
        ReservationTtlHours = 72, PartialFulfillmentAllowed = true, EmailIntimationEnabled = true,
        IntimationDepartmentId = null, IntimationCcEmails = null, ShipmentRequiredDefault = true
    };

    [Fact]
    public async Task First_read_creates_a_default_row_with_the_specs_defaults()
    {
        var h = NewHarness();

        var config = await h.Service.GetConfigAsync();

        config.AutoPoEnabled.Should().BeTrue();
        config.SupplierSelectionMode.Should().Be(SupplierSelectionModes.BestMatch);
        config.DropShipEnabled.Should().BeFalse();
        config.SelfPickupEnabled.Should().BeTrue();
        config.ReservationTtlHours.Should().Be(72);
        config.PartialFulfillmentAllowed.Should().BeTrue();
        config.EmailIntimationEnabled.Should().BeTrue();
        config.ShipmentRequiredDefault.Should().BeTrue();
    }

    [Fact]
    public async Task Reading_twice_never_creates_a_second_row()
    {
        var h = NewHarness();

        await h.Service.GetConfigAsync();
        await h.Service.GetConfigAsync();

        (await h.Db.SaleOrderConfigs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Update_persists_every_field()
    {
        var h = NewHarness();
        var req = ValidUpdate();
        req.AutoPoEnabled = false;
        req.SupplierSelectionMode = SupplierSelectionModes.Manual;
        req.DropShipEnabled = true;
        req.ReservationTtlHours = 48;
        req.IntimationCcEmails = "supply@example.com";

        var result = await h.Service.UpdateConfigAsync(req, User);

        result.AutoPoEnabled.Should().BeFalse();
        result.SupplierSelectionMode.Should().Be(SupplierSelectionModes.Manual);
        result.DropShipEnabled.Should().BeTrue();
        result.ReservationTtlHours.Should().Be(48);
        result.IntimationCcEmails.Should().Be("supply@example.com");
        result.UpdatedBy.Should().Be(User);
        result.UpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Update_writes_an_audit_row_only_for_fields_that_actually_changed()
    {
        var h = NewHarness();
        await h.Service.GetConfigAsync(); // establishes the default row

        var req = ValidUpdate();
        req.ReservationTtlHours = 96; // the only real change from the seeded defaults

        await h.Service.UpdateConfigAsync(req, User);

        var audit = await h.Db.SaleOrderConfigAudits.ToListAsync();
        audit.Should().ContainSingle();
        audit[0].FieldChanged.Should().Be(nameof(SaleOrderConfig.ReservationTtlHours));
        audit[0].OldValue.Should().Be("72");
        audit[0].NewValue.Should().Be("96");
        audit[0].ChangedBy.Should().Be(User);
    }

    [Fact]
    public async Task Update_writes_one_audit_row_per_changed_field_when_several_change_at_once()
    {
        var h = NewHarness();
        await h.Service.GetConfigAsync();

        var req = ValidUpdate();
        req.DropShipEnabled = true;
        req.SelfPickupEnabled = false;
        req.EmailIntimationEnabled = false;

        await h.Service.UpdateConfigAsync(req, User);

        var fields = (await h.Db.SaleOrderConfigAudits.ToListAsync()).Select(a => a.FieldChanged).ToList();
        fields.Should().BeEquivalentTo(
        [
            nameof(SaleOrderConfig.DropShipEnabled),
            nameof(SaleOrderConfig.SelfPickupEnabled),
            nameof(SaleOrderConfig.EmailIntimationEnabled)
        ]);
    }

    [Fact]
    public async Task Updating_with_no_actual_changes_writes_no_audit_rows()
    {
        var h = NewHarness();
        var current = await h.Service.GetConfigAsync();

        await h.Service.UpdateConfigAsync(new UpdateSaleOrderConfigRequest
        {
            AutoPoEnabled = current.AutoPoEnabled, SupplierSelectionMode = current.SupplierSelectionMode,
            AutoPoApprovalMode = current.AutoPoApprovalMode, DropShipEnabled = current.DropShipEnabled,
            SelfPickupEnabled = current.SelfPickupEnabled, DefaultFulfillmentMode = current.DefaultFulfillmentMode,
            ReservationTtlHours = current.ReservationTtlHours, PartialFulfillmentAllowed = current.PartialFulfillmentAllowed,
            EmailIntimationEnabled = current.EmailIntimationEnabled, IntimationDepartmentId = current.IntimationDepartmentId,
            IntimationCcEmails = current.IntimationCcEmails, ShipmentRequiredDefault = current.ShipmentRequiredDefault
        }, User);

        (await h.Db.SaleOrderConfigAudits.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Audit_list_is_newest_first_and_resolves_the_changers_name()
    {
        var h = NewHarness();
        await h.Service.GetConfigAsync();

        var req1 = ValidUpdate(); req1.ReservationTtlHours = 48;
        await h.Service.UpdateConfigAsync(req1, updatedBy: 1);
        var req2 = ValidUpdate(); req2.ReservationTtlHours = 24;
        await h.Service.UpdateConfigAsync(req2, updatedBy: 2);

        var page = await h.Service.GetAuditAsync(new SaleOrderConfigAuditFilter());

        page.Data.Should().HaveCount(2);
        page.Data[0].ChangedBy.Should().Be(2); // most recent change first
        page.Data[0].NewValue.Should().Be("24");
        page.Data[0].ChangedByName.Should().Be("User 2");
        page.Data[1].ChangedBy.Should().Be(1);
    }

    [Fact]
    public async Task Audit_is_empty_when_the_config_has_never_been_changed()
    {
        var h = NewHarness();
        await h.Service.GetConfigAsync();

        var page = await h.Service.GetAuditAsync(new SaleOrderConfigAuditFilter());

        page.Data.Should().BeEmpty();
        page.TotalRecords.Should().Be(0);
    }

    // ── What a policy has to satisfy ───────────────────────────────────────────

    public static IEnumerable<object[]> InvalidPolicies()
    {
        yield return new object[] { "an unknown supplier mode", (Action<UpdateSaleOrderConfigRequest>)(r => r.SupplierSelectionMode = "CHEAPEST"), "not a supplier selection mode" };
        yield return new object[] { "an unknown approval mode", (Action<UpdateSaleOrderConfigRequest>)(r => r.AutoPoApprovalMode = "SKIP"), "not a purchase order approval mode" };
        yield return new object[] { "an unknown fulfilment mode", (Action<UpdateSaleOrderConfigRequest>)(r => r.DefaultFulfillmentMode = "SPLIT"), "not a fulfilment mode" };
        yield return new object[] { "a lower case mode", (Action<UpdateSaleOrderConfigRequest>)(r => r.SupplierSelectionMode = "best_match"), "not a supplier selection mode" };
        yield return new object[] { "no hold", (Action<UpdateSaleOrderConfigRequest>)(r => r.ReservationTtlHours = 0), "from 1 to 8760" };
        yield return new object[] { "a negative hold", (Action<UpdateSaleOrderConfigRequest>)(r => r.ReservationTtlHours = -3), "from 1 to 8760" };
        yield return new object[] { "a hold over a year", (Action<UpdateSaleOrderConfigRequest>)(r => r.ReservationTtlHours = 8761), "from 1 to 8760" };
        yield return new object[] { "an address that is not one", (Action<UpdateSaleOrderConfigRequest>)(r => r.IntimationCcEmails = "good@x.com, nope"), "nope" };
        yield return new object[] { "a copy list too long", (Action<UpdateSaleOrderConfigRequest>)(r => r.IntimationCcEmails = string.Join(", ", Enumerable.Range(0, 60).Select(i => $"person{i}@example.com"))), "too long" };
        yield return new object[] { "drop ship as the default with drop shipping off", (Action<UpdateSaleOrderConfigRequest>)(r => { r.DefaultFulfillmentMode = FulfillmentModes.DropShip; r.DropShipEnabled = false; }), "drop shipping is switched off" };
        yield return new object[] { "pickup as the default with pickup off", (Action<UpdateSaleOrderConfigRequest>)(r => { r.ShipmentRequiredDefault = false; r.SelfPickupEnabled = false; }), "customer pickup is switched off" };
    }

    [Theory]
    [MemberData(nameof(InvalidPolicies))]
    public async Task A_policy_that_cannot_work_is_refused_saying_why_and_leaves_no_trace(
        string _, Action<UpdateSaleOrderConfigRequest> break_, string reason)
    {
        var h = NewHarness();
        var req = ValidUpdate();
        break_(req);

        var act = () => h.Service.UpdateConfigAsync(req, User);

        (await act.Should().ThrowAsync<SMS.Shared.Exceptions.BadRequestException>()).Which.Message.Should().Contain(reason);
        (await h.Db.SaleOrderConfigs.CountAsync()).Should().Be(0, "a refused request must not even create the row");
        (await h.Db.SaleOrderConfigAudits.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_refused_save_leaves_the_saved_policy_as_it_was()
    {
        var h = NewHarness();
        var good = ValidUpdate();
        good.ReservationTtlHours = 48;
        await h.Service.UpdateConfigAsync(good, User);
        var bad = ValidUpdate();
        bad.ReservationTtlHours = 0;

        var act = () => h.Service.UpdateConfigAsync(bad, User);

        await act.Should().ThrowAsync<SMS.Shared.Exceptions.BadRequestException>();
        (await h.Service.GetConfigAsync()).ReservationTtlHours.Should().Be(48);
    }

    [Fact]
    public async Task The_edges_of_what_is_allowed_are_allowed()
    {
        var h = NewHarness();

        foreach (var hours in new[] { 1, 8760 })
        {
            var req = ValidUpdate();
            req.ReservationTtlHours = hours;
            (await h.Service.UpdateConfigAsync(req, User)).ReservationTtlHours.Should().Be(hours);
        }

        var dropShip = ValidUpdate();
        dropShip.DefaultFulfillmentMode = FulfillmentModes.DropShip;
        dropShip.DropShipEnabled = true;
        (await h.Service.UpdateConfigAsync(dropShip, User)).DefaultFulfillmentMode.Should().Be(FulfillmentModes.DropShip);
    }

    [Fact]
    public async Task The_copy_list_is_stored_as_one_tidy_line_and_recorded_that_way()
    {
        var h = NewHarness();
        var req = ValidUpdate();
        req.IntimationCcEmails = " a@x.com;b@x.com  A@X.com,, c@x.com ";

        var saved = await h.Service.UpdateConfigAsync(req, User);

        saved.IntimationCcEmails.Should().Be("a@x.com, b@x.com, c@x.com");
        var audit = await h.Db.SaleOrderConfigAudits.SingleAsync();
        audit.FieldChanged.Should().Be(nameof(SaleOrderConfig.IntimationCcEmails));
        audit.OldValue.Should().BeNull();
        audit.NewValue.Should().Be("a@x.com, b@x.com, c@x.com");
    }

    [Fact]
    public async Task An_empty_copy_list_is_stored_as_none_and_is_not_a_change_from_none()
    {
        var h = NewHarness();
        var req = ValidUpdate();
        req.IntimationCcEmails = "   ";

        var saved = await h.Service.UpdateConfigAsync(req, User);

        saved.IntimationCcEmails.Should().BeNull();
        (await h.Db.SaleOrderConfigAudits.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_notification_department_has_to_be_one_the_organization_has()
    {
        var h = NewHarness(departmentIds: [3, 4]);
        var req = ValidUpdate();

        req.IntimationDepartmentId = 4;
        (await h.Service.UpdateConfigAsync(req, User)).IntimationDepartmentId.Should().Be(4);

        req.IntimationDepartmentId = 99;
        var act = () => h.Service.UpdateConfigAsync(req, User);

        (await act.Should().ThrowAsync<SMS.Shared.Exceptions.BadRequestException>()).Which.Message.Should().Contain("department does not exist");
        (await h.Service.GetConfigAsync()).IntimationDepartmentId.Should().Be(4);
    }

    [Fact]
    public async Task A_department_saved_earlier_and_since_removed_does_not_stop_other_settings_being_saved()
    {
        // The saved policy names department 3, and the organization has no departments any more.
        var h = NewHarness();
        h.Db.SaleOrderConfigs.Add(new SaleOrderConfig { IntimationDepartmentId = 3, OrganizationId = h.OrgId });
        await h.Db.SaveChangesAsync();
        var req = ValidUpdate();
        req.IntimationDepartmentId = 3;
        req.ReservationTtlHours = 96;

        var saved = await h.Service.UpdateConfigAsync(req, User);

        saved.ReservationTtlHours.Should().Be(96);
        saved.IntimationDepartmentId.Should().Be(3);
    }

    [Fact]
    public async Task Each_org_gets_its_own_config_row()
    {
        var h = NewHarness();
        var firstOrgConfig = await h.Service.GetConfigAsync();

        var otherOrg = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        await using var otherDb = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(h.DbName).Options, otherOrg);
        var otherUserQuery = new Mock<IUserQueryService>();
        otherUserQuery.Setup(u => u.GetUsersAsync(It.IsAny<IReadOnlyList<int>>())).ReturnsAsync([]);
        var otherService = new SaleOrderConfigService(otherDb, otherUserQuery.Object, Mock.Of<IOrgChartService>());

        var otherOrgConfig = await otherService.GetConfigAsync();

        otherOrgConfig.Uuid.Should().NotBe(firstOrgConfig.Uuid);
        (await h.Db.SaleOrderConfigs.IgnoreQueryFilters().CountAsync()).Should().Be(2, "each org's GetOrCreate must produce its own row, not share one");
    }
}
