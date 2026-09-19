using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Domain.StateMachines;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Repositories;

internal interface IConsignmentRepository
{
    Task<Guid> CreateAsync(CreateConsignmentRequest req, int createdBy);
    Task<ConsignmentDetailModel?> GetByUuidAsync(Guid uuid);
    Task<bool> BookManuallyAsync(Guid uuid, ManualBookingRequest req, int userId);
    Task<bool> AttachDeliveryAsync(Guid uuid, Guid deliveryUuid, int userId);
}

/// <summary>
/// Consignments (layer C) and the manual booking path.
/// <para>
/// Manual booking is the one that has to work first. Most local carriers will never have an API,
/// and a module that only functions for the ones that do is a module most of the business cannot
/// use. Here a person keys in the airway bill and the carrier's own tracking page does the rest —
/// which is exactly what the legacy <c>Shipment</c> did, kept working on top of the new model.
/// </para>
/// </summary>
internal sealed class ConsignmentRepository : IConsignmentRepository
{
    /// <summary>The placeholder the existing carrier tracking templates already use.</summary>
    private const string TrackingPlaceholder = "{tracking}";

    private readonly LogisticsDbContext       _db;
    private readonly IDocumentNumberGenerator _numbers;

    public ConsignmentRepository(LogisticsDbContext db, IDocumentNumberGenerator numbers)
    {
        _db      = db;
        _numbers = numbers;
    }

    public async Task<Guid> CreateAsync(CreateConsignmentRequest req, int createdBy)
    {
        ArgumentNullException.ThrowIfNull(req);

        Carrier? carrier = null;
        if (req.CarrierUuid is { } carrierUuid)
        {
            carrier = await _db.Carriers.FirstOrDefaultAsync(c => c.UUID == carrierUuid && !c.IsDelete)
                ?? throw new NotFoundException("Carrier", carrierUuid);
        }

        var now = DateTime.UtcNow;

        var consignment = new Consignment
        {
            UUID              = Guid.NewGuid(),
            ConsignmentNumber = await _numbers.NextAsync(DocumentNumberPrefix.Consignment, now),
            CarrierId         = carrier?.Id,
            CarrierName       = carrier?.Name,
            CarrierServiceCode = Trim(req.CarrierServiceCode),
            Mode              = LogisticsCode.Of(Parse<ShipmentMode>(req.Mode, ShipmentMode.Courier, "mode")),
            FreightTerms      = LogisticsCode.Of(Parse<FreightTerms>(req.FreightTerms, FreightTerms.Prepaid, "freight terms")),
            CodAmount         = req.CodAmount,
            CodCurrency       = Trim(req.CodCurrency)?.ToUpperInvariant(),
            PickupWindowStart = req.PickupWindowStart,
            PickupWindowEnd   = req.PickupWindowEnd,
            Etd               = req.Etd,
            Eta               = req.Eta,
            VehicleNumber     = Trim(req.VehicleNumber),
            DriverName        = Trim(req.DriverName),
            DriverPhone       = Trim(req.DriverPhone),
            Status            = LogisticsCode.Of(ShipmentStatus.Draft),
            Notes             = Trim(req.Notes),
            IsActive          = true,
            CreatedBy         = createdBy,
            CreatedDate       = now
        };

        _db.Consignments.Add(consignment);
        await _db.SaveChangesAsync();

        foreach (var deliveryUuid in req.DeliveryUuids ?? [])
            await AttachDeliveryAsync(consignment.UUID, deliveryUuid, createdBy);

        return consignment.UUID;
    }

    public async Task<bool> AttachDeliveryAsync(Guid uuid, Guid deliveryUuid, int userId)
    {
        var consignment = await _db.Consignments
            .Include(c => c.Deliveries)
            .FirstOrDefaultAsync(c => c.UUID == uuid && !c.IsDelete);

        if (consignment is null) return false;

        var delivery = await _db.DeliveryOrders
            .FirstOrDefaultAsync(d => d.UUID == deliveryUuid && !d.IsDelete)
            ?? throw new NotFoundException("Delivery", deliveryUuid);

        if (consignment.Deliveries.Any(d => d.DeliveryOrderId == delivery.Id))
            throw new ConflictException(
                $"Delivery {delivery.DeliveryNumber} is already on consignment " +
                $"{consignment.ConsignmentNumber}. Counting it twice would double the load.");

        _db.ConsignmentDeliveries.Add(new ConsignmentDelivery
        {
            UUID          = Guid.NewGuid(),
            ConsignmentId = consignment.Id,
            DeliveryOrderId = delivery.Id,
            Sequence      = consignment.Deliveries.Count + 1,
            CreatedBy     = userId,
            CreatedDate   = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Records an airway bill that a person obtained from the carrier by other means — a phone
    /// call, a portal, a paper consignment note — and moves the consignment to BOOKED.
    /// </summary>
    public async Task<bool> BookManuallyAsync(Guid uuid, ManualBookingRequest req, int userId)
    {
        ArgumentNullException.ThrowIfNull(req);

        var consignment = await _db.Consignments
            .Include(c => c.Carrier)
            .FirstOrDefaultAsync(c => c.UUID == uuid && !c.IsDelete);

        if (consignment is null) return false;

        if (consignment.CarrierId is null)
            throw new BadRequestException(
                "This consignment has no carrier, so there is nobody to have issued an airway bill. " +
                "Assign a carrier first.");

        // The whole point of a manual booking is that the number came from outside the system.
        // Without it the consignment is BOOKED with nothing to track and nothing to prove it.
        if (string.IsNullOrWhiteSpace(req.Awb))
            throw new BadRequestException(
                "A manual booking needs the airway bill number the carrier issued — it is the only " +
                "way to track or prove the consignment.");

        var mode = IntegrationModeOf(consignment.Carrier);

        if (mode != CarrierIntegrationMode.Manual)
            throw new ConflictException(
                $"{consignment.Carrier!.Name} is configured for {LogisticsCode.Of(mode)} integration. " +
                "Book it through the carrier adapter rather than by hand, so the booking is idempotent.");

        // A manual booking skips RATED and BOOKING — there is no rate call and no in-flight
        // carrier request to guard. The state machine still has to allow DRAFT → BOOKED.
        var current = LogisticsCode.Parse<ShipmentStatus>(consignment.Status);
        ShipmentStateMachine.Instance.EnsureCanTransition(current, ShipmentStatus.Booked);

        consignment.MasterAwb        = req.Awb.Trim();
        consignment.CarrierReference = Trim(req.CarrierReference);
        consignment.Status           = LogisticsCode.Of(ShipmentStatus.Booked);
        consignment.ModifiedBy       = userId;
        consignment.ModifiedDate     = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<ConsignmentDetailModel?> GetByUuidAsync(Guid uuid)
    {
        var consignment = await _db.Consignments
            .Include(c => c.Carrier)
            .Include(c => c.CarrierAccount)
            .Include(c => c.Deliveries).ThenInclude(cd => cd.DeliveryOrder)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.UUID == uuid && !c.IsDelete);

        if (consignment is null) return null;

        var status = LogisticsCode.Parse<ShipmentStatus>(consignment.Status);

        return new ConsignmentDetailModel
        {
            UUID               = consignment.UUID,
            ConsignmentNumber  = consignment.ConsignmentNumber,
            CarrierUuid        = consignment.Carrier?.UUID,
            CarrierName        = consignment.CarrierName,
            CarrierServiceCode = consignment.CarrierServiceCode,
            IntegrationMode    = LogisticsCode.Of(IntegrationModeOf(consignment.Carrier)),
            Mode               = consignment.Mode,
            MasterAwb          = consignment.MasterAwb,
            CarrierReference   = consignment.CarrierReference,
            TrackingUrl        = BuildTrackingUrl(consignment.Carrier, consignment.MasterAwb),
            FreightTerms       = consignment.FreightTerms,
            CodAmount          = consignment.CodAmount,
            CodCurrency        = consignment.CodCurrency,
            PickupWindowStart  = consignment.PickupWindowStart,
            PickupWindowEnd    = consignment.PickupWindowEnd,
            Etd                = consignment.Etd,
            Eta                = consignment.Eta,
            VehicleNumber      = consignment.VehicleNumber,
            DriverName         = consignment.DriverName,
            DriverPhone        = consignment.DriverPhone,
            Status             = consignment.Status,
            Notes              = consignment.Notes,
            CarrierAccountUuid   = consignment.CarrierAccount?.UUID,
            CarrierAccountName   = consignment.CarrierAccount?.AccountName,
            BookingFailureReason = consignment.BookingFailureReason,
            AllowedNextStatuses = [.. ShipmentStateMachine.Instance.From(status).Select(LogisticsCode.Of)],
            CreatedDate        = consignment.CreatedDate,
            Deliveries         = [.. consignment.Deliveries
                .OrderBy(d => d.Sequence)
                .Select(d => new ConsignmentDeliveryModel
                {
                    DeliveryUuid   = d.DeliveryOrder.UUID,
                    DeliveryNumber = d.DeliveryOrder.DeliveryNumber,
                    Status         = d.DeliveryOrder.Status,
                    Sequence       = d.Sequence
                })]
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A carrier's integration mode, defaulting to MANUAL.
    /// <para>
    /// Null and unrecognised values both resolve to MANUAL rather than throwing. Every carrier
    /// that existed before this column did has null here, and the safe reading of "we do not know
    /// how to talk to this carrier" is "a person does it" — never "call an adapter that may not
    /// exist".
    /// </para>
    /// </summary>
    internal static CarrierIntegrationMode IntegrationModeOf(Carrier? carrier) =>
        carrier is not null
        && LogisticsCode.TryParse<CarrierIntegrationMode>(carrier.IntegrationMode, out var mode)
            ? mode
            : CarrierIntegrationMode.Manual;

    /// <summary>
    /// Substitutes the AWB into the carrier's tracking template, using the same
    /// <c>{tracking}</c> placeholder the existing carrier rows already contain.
    /// </summary>
    internal static string? BuildTrackingUrl(Carrier? carrier, string? awb)
    {
        var template = carrier?.TrackingUrlTemplate;

        return string.IsNullOrWhiteSpace(template) || string.IsNullOrWhiteSpace(awb)
            ? null
            : template.Replace(TrackingPlaceholder, awb.Trim());
    }

    private static TEnum Parse<TEnum>(string? code, TEnum fallback, string what)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(code)) return fallback;

        return LogisticsCode.TryParse<TEnum>(code, out var value)
            ? value
            : throw new BadRequestException(
                $"'{code}' is not a valid {what}. Valid values: " +
                $"{string.Join(", ", LogisticsCode.Codes<TEnum>())}.");
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
