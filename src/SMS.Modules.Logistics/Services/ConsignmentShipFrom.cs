using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Services;

/// <summary>
/// Tells a consignment where it is collected from.
/// <para>
/// A carrier cannot book a parcel it has no collection address for, and nothing in the app lets a
/// person type one onto a delivery or a consignment. What every delivery does have is the warehouse it
/// leaves from — and a warehouse has an address. So when a consignment has no ship-from address of its
/// own, one is built from that warehouse.
/// </para>
/// <para>
/// <b>The delivery's own <c>ShipFromWarehouseUuid</c> is not always populated</b> — most deliveries
/// raised from a sale order predate this being load-bearing, and the field that was meant to carry it
/// can come back blank even though real stock moved for a real warehouse. Where that happens, the
/// delivery's own reservation says where the stock actually came from, active or already consumed —
/// consumed is the normal state by the time a consignment exists, since goods issue closes the hold —
/// and that is the truth a carrier needs, not a second guess.
/// </para>
/// </summary>
internal interface IConsignmentShipFrom
{
    /// <summary>
    /// Gives the consignment a ship-from address if it has none and its deliveries name a warehouse that
    /// can supply one. True when the consignment ends up with somewhere to be collected from — its own,
    /// or one its deliveries carry. Does not save: the caller owns the unit of work.
    /// <para>
    /// <b>Never throws for a data problem.</b> A warehouse with no address, or two deliveries leaving
    /// from different warehouses, is answered with false, and it is the booking that explains what is
    /// missing. Creating a consignment must not fail because a warehouse record is incomplete.
    /// </para>
    /// </summary>
    Task<bool> EnsureAsync(Consignment consignment, int userId, CancellationToken ct = default);
}

internal sealed class ConsignmentShipFrom : IConsignmentShipFrom
{
    private readonly LogisticsDbContext               _db;
    private readonly IWarehouseDirectory              _warehouses;
    private readonly IStockReservationService         _reservations;
    private readonly IAddressNormalizer               _addresses;
    private readonly ILogger<ConsignmentShipFrom>     _log;

    public ConsignmentShipFrom(
        LogisticsDbContext db, IWarehouseDirectory warehouses, IStockReservationService reservations,
        IAddressNormalizer addresses, ILogger<ConsignmentShipFrom> log)
    {
        _db           = db;
        _warehouses   = warehouses;
        _reservations = reservations;
        _addresses    = addresses;
        _log          = log;
    }

    public async Task<bool> EnsureAsync(Consignment consignment, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(consignment);

        if (consignment.ShipFromAddressId is not null || consignment.ShipFromAddress is not null)
            return true;

        try
        {
            var leaving = await _db.ConsignmentDeliveries
                .AsNoTracking()
                .Where(l => l.ConsignmentId == consignment.Id && !l.DeliveryOrder.IsDelete)
                .Select(l => new
                {
                    l.DeliveryOrder.UUID,
                    l.DeliveryOrder.ShipFromAddressId,
                    l.DeliveryOrder.ShipFromWarehouseUuid
                })
                .ToListAsync(ct);

            // A delivery with its own address is the better answer: it is where this delivery was
            // said to leave from, and the booking takes it from there.
            if (leaving.Any(l => l.ShipFromAddressId is not null)) return true;

            var warehouseUuids = new HashSet<Guid>();

            foreach (var delivery in leaving)
            {
                var warehouseUuid = delivery.ShipFromWarehouseUuid
                    ?? await WarehouseFromReservationAsync(delivery.UUID, ct);

                if (warehouseUuid is { } found) warehouseUuids.Add(found);
            }

            // One consignment has one collection point. Two warehouses would mean guessing which —
            // and a delivery that supplied neither a header value nor a resolvable reservation
            // simply contributes nothing, which is what makes this a HashSet rather than a list.
            if (warehouseUuids.Count != 1) return false;

            var warehouse = await _warehouses.FindAsync(warehouseUuids.Single(), ct);

            if (warehouse is null
             || string.IsNullOrWhiteSpace(warehouse.Address)
             || string.IsNullOrWhiteSpace(warehouse.City)
             || string.IsNullOrWhiteSpace(warehouse.Country))
                return false;

            var address = new Address
            {
                UUID          = Guid.NewGuid(),
                Line1         = warehouse.Address.Trim(),
                CityName      = warehouse.City.Trim(),
                CountryName   = warehouse.Country.Trim(),
                CountryIsoCode = IsoCodeOf(warehouse.Country),
                ContactName   = Trim(warehouse.ContactName),
                ContactPhone  = Trim(warehouse.ContactPhone),
                AddressType   = LogisticsCode.Of(AddressType.Warehouse),
                ConsigneeUuid = warehouse.Uuid,
                CreatedBy     = userId,
                CreatedDate   = DateTime.UtcNow
            };

            // A snapshot, like every other address here: the warehouse can be edited tomorrow, but
            // this consignment keeps the address it was actually collected from.
            await _addresses.NormalizeAsync(address);

            consignment.ShipFromAddress = address;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex,
                "Consignment {Consignment} has no ship-from address and one could not be built from its warehouse.",
                consignment.ConsignmentNumber);
            return false;
        }
    }

    /// <summary>
    /// The warehouse a delivery's own reservation names, when there is exactly one. Every status
    /// counts, not only active — by the time a consignment exists the hold is usually CONSUMED,
    /// goods issue having already closed it, and that is still the truest record of where the
    /// stock came from. Two warehouses on one delivery's holds (which release does not allow, but
    /// nothing enforces it can never happen) is treated the same as none: silence, not a guess.
    /// </summary>
    private async Task<Guid?> WarehouseFromReservationAsync(Guid deliveryUuid, CancellationToken ct)
    {
        var held = await _reservations.GetBySourceAsync(ReservationSourceType.Delivery, deliveryUuid, ct);

        var warehouses = held.Select(r => r.WarehouseUuid).Distinct().ToList();

        return warehouses.Count == 1 ? warehouses[0] : null;
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ── The country's code ────────────────────────────────────────────────────

    /// <summary>English country name → ISO-3166 alpha-2, from the runtime's own region data.</summary>
    private static readonly Lazy<IReadOnlyDictionary<string, string>> IsoByName = new(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);

                if (region.TwoLetterISORegionName.Length == 2)
                    map.TryAdd(region.EnglishName, region.TwoLetterISORegionName);
            }
            catch (ArgumentException)
            {
                // A culture with no region of its own. Nothing to learn from it.
            }
        }

        return map;
    });

    /// <summary>
    /// The warehouse master keeps the country as free text, and the city catalogue that could resolve
    /// it needs a city id the warehouse does not have — so without this the address would never carry the
    /// two-letter code a carrier books against. A name the runtime does not know ("South Afriqa") gives
    /// null, and the address is honestly left unconfirmed rather than given a guess.
    /// </summary>
    private static string? IsoCodeOf(string country) =>
        IsoByName.Value.TryGetValue(country.Trim(), out var iso) ? iso : null;
}
