using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Services;

public sealed record BackfillResult(int ShipmentsRead, int Migrated, int AlreadyPresent)
{
    public bool DidWork => Migrated > 0;
}

internal interface ILegacyShipmentBackfillService
{
    Task<BackfillResult> BackfillAsync(CancellationToken ct = default);
}

/// <summary>
/// Moves the legacy <c>logistics.shipments</c> rows into the four-layer model.
/// <para>
/// Each legacy row becomes one delivery plus one consignment plus the junction between them. The
/// legacy table is <b>not</b> dropped or modified — it still backs the four existing Angular
/// screens, and <c>SMS.Modules.Reports</c> queries it directly.
/// </para>
/// <para>
/// Runs at startup and is idempotent: a legacy shipment whose number already exists as a
/// consignment is skipped, so restarting the application does not duplicate anything.
/// </para>
/// </summary>
internal sealed class LegacyShipmentBackfillService : ILegacyShipmentBackfillService
{
    /// <summary>
    /// Legacy status → the delivery and consignment statuses that best describe the same reality.
    /// <para>
    /// Deliberately coarse. A backfilled delivery has no lines, so any status implying picking or
    /// packing would be a claim about work whose record does not exist. Only states that can be
    /// honestly asserted from a header are used.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, (DeliveryStatus Delivery, ShipmentStatus Consignment)> StatusMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Preparing"]  = (DeliveryStatus.Draft,      ShipmentStatus.Draft),
            ["Dispatched"] = (DeliveryStatus.InTransit,  ShipmentStatus.PickedUp),
            ["In Transit"] = (DeliveryStatus.InTransit,  ShipmentStatus.InTransit),
            ["Delivered"]  = (DeliveryStatus.Delivered,  ShipmentStatus.Delivered),
            ["Returned"]   = (DeliveryStatus.Cancelled,  ShipmentStatus.ReturnedToOrigin)
        };

    private readonly LogisticsDbContext _db;
    private readonly IDocumentNumberGenerator _numbers;
    private readonly ILogger<LegacyShipmentBackfillService> _logger;

    public LegacyShipmentBackfillService(
        LogisticsDbContext db,
        IDocumentNumberGenerator numbers,
        ILogger<LegacyShipmentBackfillService> logger)
    {
        _db      = db;
        _numbers = numbers;
        _logger  = logger;
    }

    public async Task<BackfillResult> BackfillAsync(CancellationToken ct = default)
    {
        // Query filters off throughout: this is a system operation that migrates every
        // organization's data, and it must not depend on an ambient tenant that happens to
        // bypass the filter at startup and would scope it at any other time.
        var legacy = await _db.Shipments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .OrderBy(s => s.Id)
            .ToListAsync(ct);

        if (legacy.Count == 0) return new BackfillResult(0, 0, 0);

        // Legacy shipment numbers are already SHP-YYYY-NNNNN and people recognise them, so they
        // are carried across rather than reissued. That makes the number the natural idempotency
        // key — and means the counter has to be taught about them before anything new is issued.
        var existing = await _db.Consignments
            .IgnoreQueryFilters()
            .Select(c => c.ConsignmentNumber)
            .ToListAsync(ct);

        var taken = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var migrated = 0;
        var skipped  = 0;

        foreach (var shipment in legacy)
        {
            if (taken.Contains(shipment.ShipmentNumber))
            {
                skipped++;
                continue;
            }

            await MigrateAsync(shipment, ct);
            taken.Add(shipment.ShipmentNumber);
            migrated++;
        }

        if (migrated > 0)
        {
            await AdvanceConsignmentCountersAsync(legacy, ct);
            _logger.LogInformation(
                "Backfilled {Migrated} legacy shipment(s) into the delivery model; {Skipped} already present.",
                migrated, skipped);
        }

        return new BackfillResult(legacy.Count, migrated, skipped);
    }

    private async Task MigrateAsync(Shipment shipment, CancellationToken ct)
    {
        var (deliveryStatus, consignmentStatus) = StatusMap.TryGetValue(shipment.Status, out var mapped)
            ? mapped
            : (DeliveryStatus.Draft, ShipmentStatus.Draft);

        var address = BuildAddress(shipment);
        if (address is not null) _db.Addresses.Add(address);

        var delivery = new DeliveryOrder
        {
            UUID           = Guid.NewGuid(),
            OrganizationId = shipment.OrganizationId,
            TraceId        = Guid.NewGuid(),
            // Numbered in the year the shipment was created, so backfilled documents sit in
            // chronological order rather than all landing in the current year.
            // The owning organization is passed explicitly: this runs at startup with no request
            // behind it, where the ambient tenant is not the organization being migrated.
            DeliveryNumber = await _numbers.NextAsync(
                DocumentNumberPrefix.Delivery, shipment.CreatedDate, shipment.OrganizationId, ct),
            Direction      = LogisticsCode.Of(DeliveryDirection.Inbound),
            SourceType     = LogisticsCode.Of(DeliverySourceType.Po),
            SourceUuid     = shipment.PoUuid,
            SourceNumber   = shipment.PoNumber,
            ShipToAddress  = address,
            ShipFromWarehouseUuid = shipment.OriginWarehouseUuid,
            RequestedDate  = shipment.EstimatedArrival,
            Priority       = LogisticsCode.Of(DeliveryPriority.Normal),
            Status         = LogisticsCode.Of(deliveryStatus),
            // The flag that stops a header-only record masquerading as a complete delivery. The
            // legacy table recorded a weight and a PO number and never what was in the box.
            LinesUnknown   = true,
            Notes          = shipment.Notes,
            IsActive       = shipment.IsActive,
            IsDelete       = shipment.IsDelete,
            CreatedBy      = shipment.CreatedBy,
            CreatedDate    = shipment.CreatedDate,
            ModifiedBy     = shipment.ModifiedBy,
            ModifiedDate   = shipment.ModifiedDate
        };

        if (deliveryStatus == DeliveryStatus.Cancelled)
        {
            delivery.ClosureReason = "Returned to origin (migrated from the legacy shipment record).";
            delivery.ClosedAt      = shipment.ModifiedDate ?? shipment.CreatedDate;
        }

        var consignment = new Consignment
        {
            UUID              = Guid.NewGuid(),
            OrganizationId    = shipment.OrganizationId,
            ConsignmentNumber = shipment.ShipmentNumber,
            CarrierId         = shipment.CarrierId,
            CarrierName       = shipment.CarrierName,
            Mode              = LogisticsCode.Of(ShipmentMode.Courier),
            MasterAwb         = shipment.TrackingNumber,
            FreightTerms      = LogisticsCode.Of(FreightTerms.Prepaid),
            Etd               = shipment.DispatchDate,
            Eta               = shipment.EstimatedArrival,
            ActualArrivalAt   = shipment.ActualArrival,
            Status            = LogisticsCode.Of(consignmentStatus),
            Notes             = shipment.Notes,
            IsActive          = shipment.IsActive,
            IsDelete          = shipment.IsDelete,
            CreatedBy         = shipment.CreatedBy,
            CreatedDate       = shipment.CreatedDate,
            ModifiedBy        = shipment.ModifiedBy,
            ModifiedDate      = shipment.ModifiedDate
        };

        _db.DeliveryOrders.Add(delivery);
        _db.Consignments.Add(consignment);

        _db.ConsignmentDeliveries.Add(new ConsignmentDelivery
        {
            UUID           = Guid.NewGuid(),
            OrganizationId = shipment.OrganizationId,
            Consignment    = consignment,
            DeliveryOrder  = delivery,
            Sequence       = 1,
            CreatedBy      = shipment.CreatedBy,
            CreatedDate    = shipment.CreatedDate
        });

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Builds a structured address from the legacy free-text destination.
    /// <para>
    /// No attempt is made to parse a city or country out of it — guessing would put wrong data
    /// into fields that courier booking will later rely on. The text is preserved in full and the
    /// row is marked UNVALIDATED, which puts it straight onto the operations queue the
    /// <c>(OrganizationId, ValidationStatus)</c> index exists to serve.
    /// </para>
    /// <para>
    /// The legacy column allows 300 characters and <c>Line1</c> allows 200, so the overflow goes
    /// into <c>Line2</c> rather than being truncated. Losing part of an address during a
    /// migration would be silent and unrecoverable.
    /// </para>
    /// </summary>
    private static Address? BuildAddress(Shipment shipment)
    {
        var raw = shipment.DestinationAddress?.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return null;

        const int lineLength = 200;

        return new Address
        {
            UUID             = Guid.NewGuid(),
            OrganizationId   = shipment.OrganizationId,
            Line1            = raw.Length <= lineLength ? raw : raw[..lineLength],
            Line2            = raw.Length <= lineLength ? null : raw[lineLength..],
            CityName         = "UNKNOWN",
            CountryName      = "UNKNOWN",
            AddressType      = LogisticsCode.Of(AddressType.Other),
            ValidationStatus = LogisticsCode.Of(AddressValidationStatus.Unvalidated),
            ValidationNotes  = "Migrated from a free-text shipment address. City and country need "
                             + "confirming before this address can be used to book a courier.",
            CreatedBy        = shipment.CreatedBy,
            CreatedDate      = shipment.CreatedDate
        };
    }

    /// <summary>
    /// Moves each organization's consignment counter past the highest legacy number it inherited.
    /// <para>
    /// Without this the very next consignment would be issued <c>SHP-YYYY-00001</c> — a number a
    /// legacy row already holds — and fail on the unique index. Carrying the old numbers across
    /// is only safe if the counter is told about them.
    /// </para>
    /// </summary>
    private async Task AdvanceConsignmentCountersAsync(
        IReadOnlyList<Shipment> legacy, CancellationToken ct)
    {
        var highest = legacy
            .Select(s => (s.OrganizationId, Parsed: ParseNumber(s.ShipmentNumber)))
            .Where(x => x.Parsed is not null)
            .GroupBy(x => (x.OrganizationId, x.Parsed!.Value.Year))
            .ToDictionary(g => g.Key, g => g.Max(x => x.Parsed!.Value.Sequence));

        foreach (var ((organizationId, year), maxSequence) in highest)
        {
            var counter = await _db.DocumentNumberSequences
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    s => s.OrganizationId == organizationId
                      && s.Prefix == DocumentNumberPrefix.Consignment
                      && s.Year == year, ct);

            if (counter is null)
            {
                _db.DocumentNumberSequences.Add(new DocumentNumberSequence
                {
                    OrganizationId = organizationId,
                    Prefix         = DocumentNumberPrefix.Consignment,
                    Year           = year,
                    NextValue      = maxSequence + 1
                });
            }
            else if (counter.NextValue <= maxSequence)
            {
                counter.NextValue = maxSequence + 1;
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Splits <c>SHP-2026-00042</c> into its year and sequence. Null when it does not fit that shape.</summary>
    internal static (int Year, int Sequence)? ParseNumber(string? number)
    {
        var parts = number?.Split('-');

        return parts is { Length: 3 }
            && int.TryParse(parts[1], out var year)
            && int.TryParse(parts[2], out var sequence)
                ? (year, sequence)
                : null;
    }
}
