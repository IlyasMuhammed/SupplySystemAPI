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

    private static Harness NewHarness()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(dbName).Options, tenant);

        var userQuery = new Mock<IUserQueryService>();
        userQuery.Setup(u => u.GetUsersAsync(It.IsAny<IReadOnlyList<int>>()))
            .ReturnsAsync((IReadOnlyList<int> ids) => ids.Select(id => new UserIdentity(id, $"User {id}")).ToList());

        return new Harness(db, new SaleOrderConfigService(db, userQuery.Object), tenant.OrganizationId, dbName);
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
        var otherService = new SaleOrderConfigService(otherDb, otherUserQuery.Object);

        var otherOrgConfig = await otherService.GetConfigAsync();

        otherOrgConfig.Uuid.Should().NotBe(firstOrgConfig.Uuid);
        (await h.Db.SaleOrderConfigs.IgnoreQueryFilters().CountAsync()).Should().Be(2, "each org's GetOrCreate must produce its own row, not share one");
    }
}
