using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Rating;
using SMS.Modules.Logistics.Repositories;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Rating;

// T-44 — chargeable weight for a whole consignment: the calculator joined to the service its
// carrier actually sells.
public class ConsignmentWeightTests
{
    private const int User = 42;
    private static readonly DateTime T0 = new(2026, 9, 18, 9, 0, 0, DateTimeKind.Utc);

    private sealed record Harness(
        LogisticsDbContext Db, CarrierServiceRepository Services, ChargeableWeightService Weights, string DbName);

    private static Harness NewHarness()
    {
        var (db, _, dbName) = LogisticsTestDb.New();
        var services = new CarrierServiceRepository(db);
        return new Harness(db, services, new ChargeableWeightService(db, services), dbName);
    }

    private sealed record Seeded(Guid Consignment, Guid Carrier, int CarrierId, int DeliveryId);

    /// <summary>A carrier, one service on it, one delivery, and the packages given.</summary>
    private static async Task<Seeded> Seed(
        Harness h,
        string? consignmentServiceCode = "ECON",
        string? configuredServiceCode  = "ECON",
        int?    divisor                = 5000,
        decimal? minimum               = null,
        decimal? rounding              = null,
        bool    withCarrier            = true,
        params ShipmentPackage[] packages)
    {
        var carrier = new Carrier
        {
            UUID = Guid.NewGuid(), Name = "Simcourier", Code = $"C{Guid.NewGuid():N}"[..6], IsActive = true
        };
        h.Db.Carriers.Add(carrier);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        if (configuredServiceCode is not null)
            await h.Services.CreateAsync(new CreateCarrierServiceRequest
            {
                CarrierUuid         = carrier.UUID,
                ServiceCode         = configuredServiceCode,
                ServiceName         = "Economy",
                DimDivisor          = divisor,
                MinimumChargeableKg = minimum,
                WeightRoundingKg    = rounding
            }, User);

        var delivery = new DeliveryOrder
        {
            UUID = Guid.NewGuid(), DeliveryNumber = $"DLV-2026-{Random.Shared.Next(1, 99_999):D5}",
            Direction  = LogisticsCode.Of(DeliveryDirection.Outbound),
            SourceType = LogisticsCode.Of(DeliverySourceType.Manual),
            CreatedBy  = User, CreatedDate = T0
        };

        foreach (var package in packages.Length > 0 ? packages : [Package("PKG-001")])
            delivery.Packages.Add(package);

        var consignment = new Consignment
        {
            UUID = Guid.NewGuid(), ConsignmentNumber = $"SHP-2026-{Random.Shared.Next(1, 99_999):D5}",
            CarrierId = withCarrier ? carrier.Id : null,
            CarrierName = withCarrier ? carrier.Name : null,
            CarrierServiceCode = consignmentServiceCode,
            CreatedBy = User, CreatedDate = T0
        };
        consignment.Deliveries.Add(new ConsignmentDelivery
        {
            UUID = Guid.NewGuid(), DeliveryOrder = delivery, Sequence = 1, CreatedBy = User, CreatedDate = T0
        });

        h.Db.Consignments.Add(consignment);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return new Seeded(consignment.UUID, carrier.UUID, carrier.Id, delivery.Id);
    }

    /// <summary>40 × 30 × 20 — 4.8 kg volumetric at a 5000 divisor.</summary>
    private static ShipmentPackage Package(
        string barcode, decimal? weight = 5m, decimal? l = 40m, decimal? w = 30m, decimal? h = 20m,
        bool voided = false) =>
        new()
        {
            UUID = Guid.NewGuid(), PackageBarcode = barcode,
            GrossWeightKg = weight, LengthCm = l, WidthCm = w, HeightCm = h,
            IsVoided = voided, VoidReason = voided ? "Crushed on the dock" : null,
            CreatedBy = User, CreatedDate = T0
        };

    private static Task<ShipmentPackage> Reload(Harness h, string barcode) =>
        h.Db.ShipmentPackages.AsNoTracking().SingleAsync(p => p.PackageBarcode == barcode);

    // ── The figures ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_consignment_reports_what_each_package_is_charged_on()
    {
        var h = NewHarness();
        var s = await Seed(h, packages: [Package("PKG-001", weight: 1m), Package("PKG-002", weight: 20m)]);

        var weight = await h.Weights.GetAsync(s.Consignment);

        weight!.PackageCount.Should().Be(2);
        weight.ResolvedServiceName.Should().Be("Economy");
        weight.DimDivisor.Should().Be(5000);
        weight.ChargesVolumetricWeight.Should().BeTrue();

        weight.Packages[0].ChargeableKg.Should().Be(4.8m);
        weight.Packages[0].Basis.Should().Be("VOLUMETRIC");
        weight.Packages[1].ChargeableKg.Should().Be(20m);
        weight.Packages[1].Basis.Should().Be("ACTUAL");

        weight.TotalActualKg.Should().Be(21m);
        weight.TotalVolumetricKg.Should().Be(9.6m);
        weight.TotalChargeableKg.Should().Be(24.8m);
        weight.IsComplete.Should().BeTrue();
        weight.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task The_total_is_the_sum_of_the_pieces_not_the_weight_of_the_sum()
    {
        // Carriers bill piece by piece. Summing the dimensions first and rating once would let two
        // bulky parcels subsidise each other and quote below what is invoiced.
        var h = NewHarness();
        var s = await Seed(h, minimum: 10m,
            packages: [Package("PKG-001", weight: 1m, l: 10m, w: 10m, h: 10m),
                       Package("PKG-002", weight: 1m, l: 10m, w: 10m, h: 10m)]);

        var weight = await h.Weights.GetAsync(s.Consignment);

        weight!.TotalChargeableKg.Should().Be(20m, "each piece is floored at 10, not the pair together");
        weight.Packages.Should().OnlyContain(p => p.Basis == "MINIMUM");
    }

    [Fact]
    public async Task A_carton_inside_a_pallet_is_not_charged_twice()
    {
        // It travels inside the pallet, and the pallet's own dimensions already cover it — the same
        // rule the booking request factory applies when declaring pieces to a carrier.
        var h = NewHarness();
        var s = await Seed(h, packages: [Package("PLT-001", weight: 50m, l: 120m, w: 100m, h: 150m)]);

        var pallet = await h.Db.ShipmentPackages.SingleAsync(p => p.PackageBarcode == "PLT-001");
        h.Db.ShipmentPackages.Add(new ShipmentPackage
        {
            UUID = Guid.NewGuid(), PackageBarcode = "PKG-009", DeliveryOrderId = pallet.DeliveryOrderId,
            ParentPackageId = pallet.Id, GrossWeightKg = 10m, LengthCm = 40m, WidthCm = 30m, HeightCm = 20m,
            CreatedBy = User, CreatedDate = T0
        });
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var weight = await h.Weights.GetAsync(s.Consignment);

        weight!.PackageCount.Should().Be(1);
        weight.Packages.Single().PackageBarcode.Should().Be("PLT-001");
    }

    [Fact]
    public async Task A_voided_package_is_not_charged_for()
    {
        var h = NewHarness();
        var s = await Seed(h, packages: [Package("PKG-001", weight: 5m), Package("PKG-002", weight: 9m, voided: true)]);

        var weight = await h.Weights.GetAsync(s.Consignment);

        weight!.PackageCount.Should().Be(1);
        weight.TotalActualKg.Should().Be(5m);
    }

    [Fact]
    public async Task An_unpacked_consignment_says_so_rather_than_quoting_nothing()
    {
        var h = NewHarness();
        var s = await Seed(h, packages: []);

        // Strip the package the default seed added.
        h.Db.ShipmentPackages.RemoveRange(await h.Db.ShipmentPackages.ToListAsync());
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var weight = await h.Weights.GetAsync(s.Consignment);

        weight!.PackageCount.Should().Be(0);
        weight.TotalChargeableKg.Should().Be(0m);
        weight.IsComplete.Should().BeFalse("nothing packed is not a complete answer");
        weight.Warnings.Should().ContainMatch("*has been packed*");
    }

    [Fact]
    public async Task One_unrateable_package_makes_the_whole_total_a_floor()
    {
        var h = NewHarness();
        var s = await Seed(h, packages:
        [
            Package("PKG-001", weight: 5m),
            Package("PKG-002", weight: null, l: null, w: null, h: null)
        ]);

        var weight = await h.Weights.GetAsync(s.Consignment);

        weight!.IsComplete.Should().BeFalse();
        weight.TotalChargeableKg.Should().Be(5m, "the honest total of what is known");
        weight.Packages[1].ChargeableKg.Should().BeNull();
        weight.Packages[1].Warnings.Should().NotBeEmpty();
    }

    // ── Which service ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_consignment_naming_no_service_is_rated_on_the_carriers_default()
    {
        var h = NewHarness();
        var s = await Seed(h, consignmentServiceCode: null);

        var weight = await h.Weights.GetAsync(s.Consignment);

        weight!.ResolvedServiceCode.Should().Be("ECON");
        weight.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task A_service_code_matching_nothing_falls_back_to_actual_weight_and_says_why()
    {
        // The case T-43 chose not to make impossible: consignments carry codes typed before the
        // service table existed, and they must still be readable.
        var h = NewHarness();
        var s = await Seed(h, consignmentServiceCode: "GONE", configuredServiceCode: "ECON",
                           packages: [Package("PKG-001", weight: 1m)]);

        var weight = await h.Weights.GetAsync(s.Consignment);

        weight!.ResolvedServiceName.Should().BeNull();
        weight.DimDivisor.Should().BeNull();
        weight.ChargesVolumetricWeight.Should().BeFalse();
        weight.TotalChargeableKg.Should().Be(1m, "actual weight, because no divisor could be found");
        weight.Warnings.Should().ContainMatch("*No service matching 'GONE'*rated on actual weight only*");
    }

    [Fact]
    public async Task A_carrier_with_no_services_configured_falls_back_and_says_why()
    {
        var h = NewHarness();
        var s = await Seed(h, consignmentServiceCode: null, configuredServiceCode: null,
                           packages: [Package("PKG-001", weight: 1m)]);

        var weight = await h.Weights.GetAsync(s.Consignment);

        weight!.TotalChargeableKg.Should().Be(1m);
        weight.Warnings.Should().ContainMatch("*no services configured*");
    }

    [Fact]
    public async Task A_consignment_with_no_carrier_yet_is_rated_on_actual_weight()
    {
        var h = NewHarness();
        var s = await Seed(h, withCarrier: false, packages: [Package("PKG-001", weight: 1m)]);

        var weight = await h.Weights.GetAsync(s.Consignment);

        weight!.TotalChargeableKg.Should().Be(1m);
        weight.Warnings.Should().ContainMatch("*no carrier yet*");
    }

    [Fact]
    public async Task The_service_terms_are_returned_beside_the_figures()
    {
        // A total with no divisor next to it is a number nobody can check, and checking it is the
        // whole point once a carrier invoice turns up.
        var h = NewHarness();
        var s = await Seed(h, divisor: 6000, minimum: 0.5m, rounding: 0.5m);

        var weight = await h.Weights.GetAsync(s.Consignment);

        weight!.DimDivisor.Should().Be(6000);
        weight.MinimumChargeableKg.Should().Be(0.5m);
        weight.WeightRoundingKg.Should().Be(0.5m);
    }

    // ── Reading versus writing ────────────────────────────────────────────────

    [Fact]
    public async Task Reading_the_figures_writes_nothing()
    {
        var h = NewHarness();
        var s = await Seed(h, packages: [Package("PKG-001", weight: 1m)]);

        await h.Weights.GetAsync(s.Consignment);

        var package = await Reload(h, "PKG-001");
        package.DimWeightKg.Should().BeNull();
        package.ChargeableWeightKg.Should().BeNull();
        package.WeightRatedAt.Should().BeNull();
    }

    [Fact]
    public async Task Recalculating_writes_the_figures_and_the_divisor_that_produced_them()
    {
        var h = NewHarness();
        var s = await Seed(h, packages: [Package("PKG-001", weight: 1m)]);

        var weight = await h.Weights.RecalculateAsync(s.Consignment, User);

        var package = await Reload(h, "PKG-001");
        package.DimWeightKg.Should().Be(4.8m);
        package.DimWeightDivisor.Should().Be(5000, "the figure is only checkable with the term that made it");
        package.ChargeableWeightKg.Should().Be(4.8m);
        package.ChargeableWeightBasis.Should().Be("VOLUMETRIC");
        package.WeightRatedAt.Should().NotBeNull();
        package.ModifiedBy.Should().Be(User);

        weight!.LastRatedAt.Should().Be(package.WeightRatedAt);
    }

    [Fact]
    public async Task A_divisor_change_shows_up_on_the_next_recalculation_and_not_before()
    {
        // T-26 left DimWeightKg null because nothing could fill it. Stored means stored: it does not
        // silently follow a service somebody re-negotiated last week.
        var h = NewHarness();
        var s = await Seed(h, packages: [Package("PKG-001", weight: 1m)]);

        await h.Weights.RecalculateAsync(s.Consignment, User);

        var service = await h.Db.CarrierServices.SingleAsync();
        await h.Services.PatchAsync(service.UUID, new PatchCarrierServiceRequest { DimDivisor = 6000 }, User);
        h.Db.ChangeTracker.Clear();

        (await Reload(h, "PKG-001")).DimWeightKg.Should().Be(4.8m, "nothing has been recalculated yet");

        await h.Weights.RecalculateAsync(s.Consignment, User);

        var package = await Reload(h, "PKG-001");
        package.DimWeightKg.Should().Be(4m);
        package.DimWeightDivisor.Should().Be(6000);
    }

    [Fact]
    public async Task An_unrateable_package_is_recorded_as_unrateable_rather_than_as_zero()
    {
        var h = NewHarness();
        var s = await Seed(h, packages: [Package("PKG-001", weight: null, l: null, w: null, h: null)]);

        await h.Weights.RecalculateAsync(s.Consignment, User);

        var package = await Reload(h, "PKG-001");
        package.ChargeableWeightKg.Should().BeNull();
        package.ChargeableWeightBasis.Should().Be("UNKNOWN");
    }

    [Fact]
    public async Task Rating_a_consignment_whose_deliveries_travel_elsewhere_warns_that_the_figures_are_shared()
    {
        // A delivery may sit on more than one consignment — that is what shipping in waves means —
        // and its packages hold one set of stored weights between them.
        var h = NewHarness();
        var s = await Seed(h);

        var second = new Consignment
        {
            UUID = Guid.NewGuid(), ConsignmentNumber = "SHP-2026-99999",
            CarrierId = s.CarrierId, CarrierName = "Simcourier", CarrierServiceCode = "ECON",
            CreatedBy = User, CreatedDate = T0
        };
        second.Deliveries.Add(new ConsignmentDelivery
        {
            UUID = Guid.NewGuid(), DeliveryOrderId = s.DeliveryId, Sequence = 1,
            CreatedBy = User, CreatedDate = T0
        });
        h.Db.Consignments.Add(second);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var weight = await h.Weights.GetAsync(s.Consignment);

        weight!.Warnings.Should().ContainMatch("*also travel on SHP-2026-99999*");
    }

    // ── Not found, and not yours ──────────────────────────────────────────────

    [Fact]
    public async Task An_unknown_consignment_is_reported_as_not_found()
    {
        var h = NewHarness();

        (await h.Weights.GetAsync(Guid.NewGuid())).Should().BeNull();
        (await h.Weights.RecalculateAsync(Guid.NewGuid(), User)).Should().BeNull();
    }

    [Fact]
    public async Task Another_organizations_consignment_does_not_exist_here()
    {
        var h = NewHarness();
        var s = await Seed(h);

        var otherDb = LogisticsTestDb.OpenAs(h.DbName, Guid.NewGuid());
        var other   = new ChargeableWeightService(otherDb, new CarrierServiceRepository(otherDb));

        (await other.GetAsync(s.Consignment)).Should().BeNull();
    }
}
