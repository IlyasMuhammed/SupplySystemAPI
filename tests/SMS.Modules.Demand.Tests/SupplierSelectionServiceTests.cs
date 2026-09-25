using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>A29-P5-02 §3.3 — the three supplier-selection modes, read from SaleOrderConfig rather
/// than passed by the caller.</summary>
public class SupplierSelectionServiceTests
{
    private const int User = 9;
    private const int DeptId = 5;

    private sealed record Harness(
        SupplierSelectionService Service, Mock<ISaleOrderConfigService> Config,
        Mock<IVariantSupplierService> Rates, Mock<IOrgChartService> OrgChart, Mock<INotificationService> Notifications,
        Mock<IProductVariantResolver> Variants);

    private static Harness NewHarness(string mode, int? intimationDepartmentId = DeptId)
    {
        var config = new Mock<ISaleOrderConfigService>();
        config.Setup(c => c.GetConfigAsync()).ReturnsAsync(new SaleOrderConfigModel
        {
            SupplierSelectionMode = mode, IntimationDepartmentId = intimationDepartmentId
        });
        var rates = new Mock<IVariantSupplierService>();
        var orgChart = new Mock<IOrgChartService>();
        var notifications = new Mock<INotificationService>();
        var variants = new Mock<IProductVariantResolver>();
        // No name set up by default — the fallback-to-id path, exercised on its own below.
        variants.Setup(v => v.DescribeVariantsAsync(It.IsAny<IReadOnlyList<Guid>>()))
            .ReturnsAsync(new Dictionary<Guid, VariantDescription>());

        var service = new SupplierSelectionService(
            config.Object, rates.Object, orgChart.Object, notifications.Object, variants.Object,
            NullLogger<SupplierSelectionService>.Instance);

        return new Harness(service, config, rates, orgChart, notifications, variants);
    }

    // "Product (SKU)" — IProductVariantResolver's own DisplayName for a default variant, so a test
    // that names a product this way exercises the real computed property, not a stand-in for it.
    private static string DisplayNameOf(string productName, string sku) => $"{productName} ({sku})";

    private static void NameAs(Harness h, Guid variantUuid, string productName, string sku = "SKU-1")
    {
        h.Variants.Setup(v => v.DescribeVariantsAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(variantUuid))))
            .ReturnsAsync(new Dictionary<Guid, VariantDescription>
            {
                [variantUuid] = new(variantUuid, Guid.NewGuid(), sku, "Default", productName, true, "Piece")
            });
    }

    private static RateComparisonRowModel Candidate(
        Guid supplierId, decimal price, string? grade, int? leadTimeDays, string name = "Vendor") => new()
    {
        Uuid = Guid.NewGuid(), SupplierId = supplierId, SupplierName = name, VendorUnitCost = price,
        CurrencyId = Guid.NewGuid(), LeadTimeDays = leadTimeDays, ScorecardGrade = grade
    };

    // ── DEFAULT_SUPPLIER ─────────────────────────────────────────────────────

    [Fact]
    public async Task DefaultSupplier_mode_selects_the_variants_configured_default_supplier()
    {
        var h = NewHarness(SupplierSelectionModes.DefaultSupplier);
        var variant = Guid.NewGuid();
        var supplier = Guid.NewGuid();
        h.Rates.Setup(r => r.GetDefaultSupplierIdAsync(variant)).ReturnsAsync(supplier);
        h.Rates.Setup(r => r.GetComparisonAsync(variant))
            .ReturnsAsync([Candidate(supplier, 42.50m, "B", 10, "Acme")]);

        var result = await h.Service.SelectAsync(variant, 10m, User);

        result.RequiresManualSelection.Should().BeFalse();
        result.SupplierId.Should().Be(supplier);
        result.SupplierName.Should().Be("Acme");
        result.UnitPrice.Should().Be(42.50m);
        result.Mode.Should().Be("DEFAULT_SUPPLIER");
        h.Notifications.Verify(n => n.TryCreateAsync(It.IsAny<NotificationRequest>()), Times.Never);
    }

    [Fact]
    public async Task DefaultSupplier_mode_falls_back_to_manual_when_none_configured()
    {
        var h = NewHarness(SupplierSelectionModes.DefaultSupplier);
        var variant = Guid.NewGuid();
        h.Rates.Setup(r => r.GetDefaultSupplierIdAsync(variant)).ReturnsAsync((Guid?)null);
        h.OrgChart.Setup(o => o.GetDepartmentHeadAsync(DeptId)).ReturnsAsync(new UserIdentity(77, "Head"));

        var result = await h.Service.SelectAsync(variant, 10m, User);

        result.RequiresManualSelection.Should().BeTrue();
        result.Mode.Should().Be("MANUAL");
        result.SupplierId.Should().BeNull();
        h.Notifications.Verify(n => n.TryCreateAsync(It.Is<NotificationRequest>(r => r.UserId == 77)), Times.Once);
    }

    [Fact]
    public async Task DefaultSupplier_mode_falls_back_to_manual_when_the_default_has_no_active_rate()
    {
        var h = NewHarness(SupplierSelectionModes.DefaultSupplier);
        var variant = Guid.NewGuid();
        var supplier = Guid.NewGuid();
        h.Rates.Setup(r => r.GetDefaultSupplierIdAsync(variant)).ReturnsAsync(supplier);
        // The variant has OTHER active vendors, just not its configured default.
        h.Rates.Setup(r => r.GetComparisonAsync(variant)).ReturnsAsync([Candidate(Guid.NewGuid(), 10m, "A", 5)]);

        var result = await h.Service.SelectAsync(variant, 10m, User);

        result.RequiresManualSelection.Should().BeTrue();
        result.Reason.Should().Contain("no active rate");
    }

    // ── BEST_MATCH ───────────────────────────────────────────────────────────

    [Fact]
    public async Task BestMatch_mode_picks_the_highest_composite_score_not_just_the_cheapest_or_best_graded()
    {
        var h = NewHarness(SupplierSelectionModes.BestMatch);
        var variant = Guid.NewGuid();
        var cheapButPoor  = Guid.NewGuid(); // price 10 (rank 1), grade D (score 2) -> 2*0.6 + 1*0.4     = 1.6
        var midGoodGrade  = Guid.NewGuid(); // price 20 (rank 2), grade A (score 5) -> 5*0.6 + 0.5*0.4   = 3.2  <- winner
        var pricyOkGrade  = Guid.NewGuid(); // price 30 (rank 3), grade B (score 4) -> 4*0.6 + 0.333*0.4 = 2.53
        h.Rates.Setup(r => r.GetComparisonAsync(variant)).ReturnsAsync(
        [
            Candidate(cheapButPoor, 10m, "D", 5, "Cheap"),
            Candidate(midGoodGrade, 20m, "A", 5, "GoodGrade"),
            Candidate(pricyOkGrade, 30m, "B", 5, "Pricy")
        ]);

        var result = await h.Service.SelectAsync(variant, 5m, User);

        result.RequiresManualSelection.Should().BeFalse();
        result.SupplierId.Should().Be(midGoodGrade);
        result.SupplierName.Should().Be("GoodGrade");
    }

    [Fact]
    public async Task BestMatch_mode_breaks_a_composite_tie_by_shortest_lead_time()
    {
        var h = NewHarness(SupplierSelectionModes.BestMatch);
        var variant = Guid.NewGuid();
        var slower = Guid.NewGuid();
        var faster = Guid.NewGuid();
        // Same grade and same price -> identical rank -> identical composite. Only lead time differs.
        h.Rates.Setup(r => r.GetComparisonAsync(variant)).ReturnsAsync(
        [
            Candidate(slower, 10m, "B", 20, "Slower"),
            Candidate(faster, 10m, "B", 3, "Faster")
        ]);

        var result = await h.Service.SelectAsync(variant, 5m, User);

        result.SupplierId.Should().Be(faster);
    }

    [Fact]
    public async Task BestMatch_treats_an_unscored_supplier_the_same_as_an_f_grade()
    {
        var h = NewHarness(SupplierSelectionModes.BestMatch);
        var variant = Guid.NewGuid();
        var unscored = Guid.NewGuid();
        var fGraded  = Guid.NewGuid();
        // Same price (same rank) so the two must be judged purely on grade score -> both score 1 -> tie -> lead time.
        h.Rates.Setup(r => r.GetComparisonAsync(variant)).ReturnsAsync(
        [
            Candidate(unscored, 10m, null, 5, "Unscored"),
            Candidate(fGraded, 10m, "F", 9, "FGraded")
        ]);

        var result = await h.Service.SelectAsync(variant, 5m, User);

        result.SupplierId.Should().Be(unscored, "shorter lead time (5 < 9) once grade score ties at 1 for both");
    }

    [Fact]
    public async Task BestMatch_mode_falls_back_to_manual_when_no_active_vendor_supplies_the_variant()
    {
        var h = NewHarness(SupplierSelectionModes.BestMatch);
        var variant = Guid.NewGuid();
        h.Rates.Setup(r => r.GetComparisonAsync(variant)).ReturnsAsync([]);
        h.OrgChart.Setup(o => o.GetDepartmentHeadAsync(DeptId)).ReturnsAsync(new UserIdentity(77, "Head"));

        var result = await h.Service.SelectAsync(variant, 5m, User);

        result.RequiresManualSelection.Should().BeTrue();
        result.Reason.Should().Contain("No active vendor");
        h.Notifications.Verify(n => n.TryCreateAsync(It.IsAny<NotificationRequest>()), Times.Once);
    }

    // ── MANUAL ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Manual_mode_never_looks_up_rates_and_always_requires_manual_selection()
    {
        var h = NewHarness(SupplierSelectionModes.Manual);
        h.OrgChart.Setup(o => o.GetDepartmentHeadAsync(DeptId)).ReturnsAsync(new UserIdentity(77, "Head"));

        var result = await h.Service.SelectAsync(Guid.NewGuid(), 5m, User);

        result.RequiresManualSelection.Should().BeTrue();
        result.Mode.Should().Be("MANUAL");
        h.Rates.Verify(r => r.GetComparisonAsync(It.IsAny<Guid>()), Times.Never);
        h.Rates.Verify(r => r.GetDefaultSupplierIdAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Manual_selection_notifies_the_intimation_departments_head()
    {
        var h = NewHarness(SupplierSelectionModes.Manual);
        h.OrgChart.Setup(o => o.GetDepartmentHeadAsync(DeptId)).ReturnsAsync(new UserIdentity(77, "Head"));
        var variant = Guid.NewGuid();

        await h.Service.SelectAsync(variant, 5m, User);

        h.Notifications.Verify(n => n.TryCreateAsync(It.Is<NotificationRequest>(r =>
            r.UserId == 77 && r.Type == "SUPPLIER_SELECTION_MANUAL" && r.SendEmail == true &&
            r.CreatedBy == User && r.Message.Contains(variant.ToString()))), Times.Once);
    }

    [Fact]
    public async Task The_notification_names_the_product_it_is_about_rather_than_its_id()
    {
        var h = NewHarness(SupplierSelectionModes.Manual);
        h.OrgChart.Setup(o => o.GetDepartmentHeadAsync(DeptId)).ReturnsAsync(new UserIdentity(77, "Head"));
        var variant = Guid.NewGuid();
        NameAs(h, variant, "4mm Cable", "CAB-4MM");

        await h.Service.SelectAsync(variant, 5m, User);

        h.Notifications.Verify(n => n.TryCreateAsync(It.Is<NotificationRequest>(r =>
            r.Message.Contains(DisplayNameOf("4mm Cable", "CAB-4MM")) && !r.Message.Contains(variant.ToString()))), Times.Once);
    }

    [Fact]
    public async Task A_variant_the_resolver_does_not_know_falls_back_to_its_id_rather_than_dropping_the_notification()
    {
        var h = NewHarness(SupplierSelectionModes.Manual);
        h.OrgChart.Setup(o => o.GetDepartmentHeadAsync(DeptId)).ReturnsAsync(new UserIdentity(77, "Head"));
        var variant = Guid.NewGuid();
        // No NameAs call: the default harness resolver already returns nothing for it.

        await h.Service.SelectAsync(variant, 5m, User);

        h.Notifications.Verify(n => n.TryCreateAsync(It.Is<NotificationRequest>(
            r => r.Message.Contains(variant.ToString()))), Times.Once);
    }

    [Fact]
    public async Task With_no_intimation_department_the_person_who_confirmed_the_order_is_told_instead()
    {
        var h = NewHarness(SupplierSelectionModes.Manual, intimationDepartmentId: null);
        var variant = Guid.NewGuid();

        await h.Service.SelectAsync(variant, 5m, User);

        h.OrgChart.Verify(o => o.GetDepartmentHeadAsync(It.IsAny<int>()), Times.Never);
        h.Notifications.Verify(n => n.TryCreateAsync(It.Is<NotificationRequest>(r =>
            r.UserId == User && r.Type == "SUPPLIER_SELECTION_MANUAL" && r.SendEmail == true &&
            r.CreatedBy == User && r.Message.Contains(variant.ToString()))), Times.Once,
            "nobody must be left with no idea a line needs a supplier chosen by hand");
    }

    [Fact]
    public async Task With_a_department_that_has_no_head_the_person_who_confirmed_the_order_is_told_instead()
    {
        var h = NewHarness(SupplierSelectionModes.Manual);
        h.OrgChart.Setup(o => o.GetDepartmentHeadAsync(DeptId)).ReturnsAsync((UserIdentity?)null);

        var act = () => h.Service.SelectAsync(Guid.NewGuid(), 5m, User);

        await act.Should().NotThrowAsync();
        h.Notifications.Verify(n => n.TryCreateAsync(It.Is<NotificationRequest>(r => r.UserId == User)), Times.Once);
    }

    [Fact]
    public async Task With_a_department_head_the_head_is_told_not_the_person_who_confirmed_the_order()
    {
        var h = NewHarness(SupplierSelectionModes.Manual);
        h.OrgChart.Setup(o => o.GetDepartmentHeadAsync(DeptId)).ReturnsAsync(new UserIdentity(77, "Head"));

        await h.Service.SelectAsync(Guid.NewGuid(), 5m, User);

        h.Notifications.Verify(n => n.TryCreateAsync(It.Is<NotificationRequest>(r => r.UserId == 77)), Times.Once);
        h.Notifications.Verify(n => n.TryCreateAsync(It.Is<NotificationRequest>(r => r.UserId == User)), Times.Never);
    }

    [Fact]
    public async Task An_unrecognized_mode_falls_back_to_manual_rather_than_throwing()
    {
        var h = NewHarness("SOMETHING_ELSE");

        var result = await h.Service.SelectAsync(Guid.NewGuid(), 5m, User);

        result.RequiresManualSelection.Should().BeTrue();
    }
}
