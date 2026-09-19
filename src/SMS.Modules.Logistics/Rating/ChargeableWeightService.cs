using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;

namespace SMS.Modules.Logistics.Rating;

/// <summary>
/// Chargeable weight for a whole consignment — the bridge between the pure calculator and the
/// carrier service a consignment happens to name.
/// </summary>
public interface IChargeableWeightService
{
    /// <summary>
    /// Works the figures out and returns them without writing anything. This is what a screen asks
    /// for, and a screen must not change what it is showing.
    /// </summary>
    Task<ConsignmentWeightModel?> GetAsync(Guid consignmentUuid, CancellationToken ct = default);

    /// <summary>
    /// Works the figures out and writes them onto the packages, so a later quote can be explained
    /// with the numbers that actually produced it rather than with today's service terms.
    /// </summary>
    Task<ConsignmentWeightModel?> RecalculateAsync(Guid consignmentUuid, int userId, CancellationToken ct = default);
}

internal sealed class ChargeableWeightService : IChargeableWeightService
{
    private readonly LogisticsDbContext        _db;
    private readonly ICarrierServiceRepository _services;

    public ChargeableWeightService(LogisticsDbContext db, ICarrierServiceRepository services)
    {
        _db       = db;
        _services = services;
    }

    public Task<ConsignmentWeightModel?> GetAsync(Guid consignmentUuid, CancellationToken ct = default) =>
        BuildAsync(consignmentUuid, persistAs: null, ct);

    public Task<ConsignmentWeightModel?> RecalculateAsync(
        Guid consignmentUuid, int userId, CancellationToken ct = default) =>
        BuildAsync(consignmentUuid, persistAs: userId, ct);

    private async Task<ConsignmentWeightModel?> BuildAsync(Guid consignmentUuid, int? persistAs, CancellationToken ct)
    {
        // Tracked when it is going to write, no-tracking when it is only going to read — a read
        // that hands back tracked entities is one stray SaveChanges away from writing.
        var query = _db.Consignments
            .Include(c => c.Carrier)
            .Include(c => c.Deliveries).ThenInclude(cd => cd.DeliveryOrder).ThenInclude(d => d.Packages)
            .AsSplitQuery();

        if (persistAs is null) query = query.AsNoTracking();

        var consignment = await query.FirstOrDefaultAsync(c => c.UUID == consignmentUuid && !c.IsDelete, ct);
        if (consignment is null) return null;

        var warnings = new List<string>();

        // ── Which service, and on what terms ──────────────────────────────────

        CarrierService? service = null;

        if (consignment.CarrierId is { } carrierId)
        {
            service = await _services.ResolveAsync(carrierId, consignment.CarrierServiceCode, ct);

            if (service is null)
                warnings.Add(string.IsNullOrWhiteSpace(consignment.CarrierServiceCode)
                    ? $"{consignment.CarrierName ?? "This carrier"} has no services configured, so these " +
                      "packages are rated on actual weight only. Volume is not being charged for."
                    : $"No service matching '{consignment.CarrierServiceCode}' is configured for " +
                      $"{consignment.CarrierName ?? "this carrier"}, so these packages are rated on actual " +
                      "weight only. Volume is not being charged for.");
        }
        else
        {
            warnings.Add("This consignment has no carrier yet, so there are no service terms to rate it on. " +
                         "The figures below are actual weight.");
        }

        var terms = ServiceWeightTerms.From(service);

        // ── The packages ──────────────────────────────────────────────────────
        //
        // Top-level handling units only, matching what is declared to a carrier at booking: a carton
        // inside a pallet is carried by the pallet, and charging for both bills the same goods twice.

        var packages = consignment.Deliveries
            .Select(cd => cd.DeliveryOrder)
            .Where(d => d is not null && !d.IsDelete)
            .SelectMany(d => d.Packages)
            .Where(p => !p.IsVoided && !p.IsDelete && p.ParentPackageId == null)
            .DistinctBy(p => p.Id)
            .OrderBy(p => p.PackageBarcode, StringComparer.Ordinal)
            .ToList();

        if (packages.Count == 0)
            warnings.Add("Nothing on this consignment has been packed, so there is no weight to charge. " +
                         "Pack the deliveries first.");

        await WarnIfDeliveriesAreSharedAsync(consignment, warnings, ct);

        var now      = DateTime.UtcNow;
        var results  = new List<PackageWeightModel>(packages.Count);
        var complete = packages.Count > 0;

        decimal totalActual = 0, totalVolumetric = 0, totalChargeable = 0;

        foreach (var package in packages)
        {
            var result = ChargeableWeight.ForParcel(
                new ParcelMeasurements(package.LengthCm, package.WidthCm, package.HeightCm, package.GrossWeightKg),
                terms);

            totalActual     += result.ActualKg     ?? 0m;
            totalVolumetric += result.VolumetricKg ?? 0m;
            totalChargeable += result.ChargeableKg ?? 0m;

            if (result.ChargeableKg is null) complete = false;

            if (persistAs is { } userId)
            {
                package.DimWeightKg           = result.VolumetricKg;
                package.DimWeightDivisor      = result.DivisorUsed;
                package.ChargeableWeightKg    = result.ChargeableKg;
                package.ChargeableWeightBasis = LogisticsCode.Of(result.Basis);
                package.WeightRatedAt         = now;
                package.ModifiedBy            = userId;
                package.ModifiedDate          = now;
            }

            results.Add(new PackageWeightModel
            {
                PackageUuid       = package.UUID,
                PackageBarcode    = package.PackageBarcode,
                PackageType       = package.PackageType,
                LengthCm          = package.LengthCm,
                WidthCm           = package.WidthCm,
                HeightCm          = package.HeightCm,
                ActualKg          = result.ActualKg,
                VolumetricKg      = result.VolumetricKg,
                ChargeableKg      = result.ChargeableKg,
                Basis             = LogisticsCode.Of(result.Basis),
                DivisorUsed       = result.DivisorUsed,
                LongestSideCm     = result.LongestSideCm,
                LengthPlusGirthCm = result.LengthPlusGirthCm,
                Warnings          = [.. result.Warnings]
            });
        }

        if (persistAs is not null && packages.Count > 0)
            await _db.SaveChangesAsync(ct);

        return new ConsignmentWeightModel
        {
            ConsignmentUuid     = consignment.UUID,
            ConsignmentNumber   = consignment.ConsignmentNumber,
            CarrierName         = consignment.CarrierName ?? consignment.Carrier?.Name,
            CarrierServiceCode  = consignment.CarrierServiceCode,
            ResolvedServiceCode = service?.ServiceCode,
            ResolvedServiceName = service?.ServiceName,

            DimDivisor              = terms.DimDivisor,
            MinimumChargeableKg     = terms.MinimumChargeableKg,
            WeightRoundingKg        = terms.WeightRoundingKg,
            ChargesVolumetricWeight = terms.DimDivisor is > 0,

            PackageCount      = packages.Count,
            TotalActualKg     = totalActual,
            TotalVolumetricKg = totalVolumetric,
            TotalChargeableKg = totalChargeable,
            IsComplete        = complete,
            LastRatedAt       = persistAs is not null && packages.Count > 0
                ? now
                : packages.Select(p => p.WeightRatedAt).Where(d => d is not null).Max(),

            Warnings = warnings,
            Packages = results
        };
    }

    /// <summary>
    /// A delivery can sit on more than one consignment — that is what makes shipping in waves
    /// possible — and its packages then have one set of stored weights between them. Whichever
    /// consignment was rated last wins, so the reader is told rather than left to wonder why a
    /// figure changed on a consignment nobody touched.
    /// </summary>
    private async Task WarnIfDeliveriesAreSharedAsync(
        Consignment consignment, List<string> warnings, CancellationToken ct)
    {
        var deliveryIds = consignment.Deliveries.Select(cd => cd.DeliveryOrderId).Distinct().ToList();
        if (deliveryIds.Count == 0) return;

        var others = await _db.ConsignmentDeliveries
            .AsNoTracking()
            .Where(cd => deliveryIds.Contains(cd.DeliveryOrderId) && cd.ConsignmentId != consignment.Id)
            .Select(cd => cd.Consignment.ConsignmentNumber)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync(ct);

        if (others.Count == 0) return;

        warnings.Add(
            $"Deliveries on this consignment also travel on {string.Join(", ", others)}. The packages hold " +
            "one set of stored weights, so rating either consignment overwrites the other's — the terms " +
            "used are stored beside each figure so it stays checkable.");
    }
}
