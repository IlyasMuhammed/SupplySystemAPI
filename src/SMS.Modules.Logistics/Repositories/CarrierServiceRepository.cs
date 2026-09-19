using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Repositories;

internal interface ICarrierServiceRepository
{
    Task<Guid> CreateAsync(CreateCarrierServiceRequest req, int userId);
    Task<IReadOnlyList<CarrierServiceModel>> GetForCarrierAsync(Guid carrierUuid);
    Task<CarrierServiceModel?> GetByUuidAsync(Guid uuid);
    Task<bool> PatchAsync(Guid uuid, PatchCarrierServiceRequest req, int userId);
    Task<bool> DeleteAsync(Guid uuid, int userId);

    /// <summary>
    /// The service a consignment's code names, or the carrier's default when it names none.
    /// Null when the code matches nothing — which must stay readable rather than throwing, since
    /// consignments already carry codes from before this table existed.
    /// </summary>
    Task<CarrierService?> ResolveAsync(int carrierId, string? serviceCode, CancellationToken ct = default);
}

/// <summary>
/// A carrier's named products and the terms that price them.
/// </summary>
internal sealed class CarrierServiceRepository : ICarrierServiceRepository
{
    private static readonly string[] Clearable =
    [
        "DIM_DIVISOR", "MINIMUM_CHARGEABLE", "WEIGHT_ROUNDING",
        "MAX_WEIGHT", "MAX_LENGTH", "MAX_GIRTH", "TRANSIT_DAYS"
    ];

    private readonly LogisticsDbContext _db;
    public CarrierServiceRepository(LogisticsDbContext db) => _db = db;

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<Guid> CreateAsync(CreateCarrierServiceRequest req, int userId)
    {
        ArgumentNullException.ThrowIfNull(req);

        var carrier = await _db.Carriers
            .FirstOrDefaultAsync(c => c.UUID == req.CarrierUuid && !c.IsDelete)
            ?? throw new NotFoundException("Carrier", req.CarrierUuid);

        var code = Require(req.ServiceCode, "A service code is required — it is how a consignment names the service.");
        var name = Require(req.ServiceName, "A service name is required.");

        if (await _db.CarrierServices.AnyAsync(s =>
                s.CarrierId == carrier.Id && !s.IsDelete && s.ServiceCode == code))
            throw new ConflictException(
                $"{carrier.Name} already has a service with code '{code}'. Two would make the " +
                "service a consignment names ambiguous, which is what this table exists to prevent.");

        ValidateLimits(req.DimDivisor, req.MinimumChargeableKg, req.WeightRoundingKg,
                       req.MaxWeightKgPerPackage, req.MaxLengthCm, req.MaxLengthPlusGirthCm,
                       req.TransitDays);

        var existing = await _db.CarrierServices
            .Where(s => s.CarrierId == carrier.Id && !s.IsDelete)
            .ToListAsync();

        var now = DateTime.UtcNow;

        var service = new CarrierService
        {
            UUID                  = Guid.NewGuid(),
            OrganizationId        = carrier.OrganizationId,
            CarrierId             = carrier.Id,
            ServiceCode           = code,
            ServiceName           = name,
            Description           = Trim(req.Description),
            DimDivisor            = req.DimDivisor,
            MinimumChargeableKg   = req.MinimumChargeableKg,
            WeightRoundingKg      = req.WeightRoundingKg,
            MaxWeightKgPerPackage = req.MaxWeightKgPerPackage,
            MaxLengthCm           = req.MaxLengthCm,
            MaxLengthPlusGirthCm  = req.MaxLengthPlusGirthCm,
            SupportsCod           = req.SupportsCod,
            SupportsHazardous     = req.SupportsHazardous,
            TransitDays           = req.TransitDays,
            // The first service is the default whether or not anybody said so: a carrier whose
            // only service is not the default has nothing to offer a consignment that names none.
            IsDefault             = req.IsDefault || existing.Count == 0,
            IsActive              = true,
            CreatedBy             = userId,
            CreatedDate           = now
        };

        if (service.IsDefault) ClearOtherDefaults(existing, userId, now);

        _db.CarrierServices.Add(service);
        await _db.SaveChangesAsync();

        return service.UUID;
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<CarrierServiceModel>> GetForCarrierAsync(Guid carrierUuid)
    {
        var carrier = await _db.Carriers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.UUID == carrierUuid && !c.IsDelete);

        if (carrier is null) return [];

        var services = await _db.CarrierServices.AsNoTracking()
            .Where(s => s.CarrierId == carrier.Id && !s.IsDelete)
            .OrderByDescending(s => s.IsDefault)
            .ThenBy(s => s.ServiceName)
            .ToListAsync();

        return [.. services.Select(s => ToModel(s, carrier))];
    }

    public async Task<CarrierServiceModel?> GetByUuidAsync(Guid uuid)
    {
        var service = await _db.CarrierServices.AsNoTracking()
            .Include(s => s.Carrier)
            .FirstOrDefaultAsync(s => s.UUID == uuid && !s.IsDelete);

        return service is null ? null : ToModel(service, service.Carrier);
    }

    public async Task<CarrierService?> ResolveAsync(
        int carrierId, string? serviceCode, CancellationToken ct = default)
    {
        var services = await _db.CarrierServices
            .AsNoTracking()
            .Where(s => s.CarrierId == carrierId && !s.IsDelete && s.IsActive)
            .ToListAsync(ct);

        if (services.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(serviceCode))
        {
            // Case-insensitive: the code is configuration somebody types, and on a consignment it
            // may have been typed years before this table existed.
            var code = serviceCode.Trim();
            return services.FirstOrDefault(
                s => string.Equals(s.ServiceCode, code, StringComparison.OrdinalIgnoreCase));
        }

        return services.FirstOrDefault(s => s.IsDefault)
            ?? (services.Count == 1 ? services[0] : null);
    }

    private static CarrierServiceModel ToModel(CarrierService s, Carrier carrier) => new()
    {
        UUID                  = s.UUID,
        CarrierUuid           = carrier.UUID,
        CarrierName           = carrier.Name,
        ServiceCode           = s.ServiceCode,
        ServiceName           = s.ServiceName,
        Description           = s.Description,
        DimDivisor            = s.DimDivisor,
        MinimumChargeableKg   = s.MinimumChargeableKg,
        WeightRoundingKg      = s.WeightRoundingKg,
        MaxWeightKgPerPackage = s.MaxWeightKgPerPackage,
        MaxLengthCm           = s.MaxLengthCm,
        MaxLengthPlusGirthCm  = s.MaxLengthPlusGirthCm,
        SupportsCod           = s.SupportsCod,
        SupportsHazardous     = s.SupportsHazardous,
        TransitDays           = s.TransitDays,
        IsDefault             = s.IsDefault,
        IsActive              = s.IsActive,
        // Said out loud, because a null divisor and an unfilled divisor look identical otherwise.
        ChargesVolumetricWeight = s.DimDivisor is > 0,
        CreatedDate           = s.CreatedDate
    };

    // ── Update ────────────────────────────────────────────────────────────────

    public async Task<bool> PatchAsync(Guid uuid, PatchCarrierServiceRequest req, int userId)
    {
        ArgumentNullException.ThrowIfNull(req);

        var service = await _db.CarrierServices.FirstOrDefaultAsync(s => s.UUID == uuid && !s.IsDelete);
        if (service is null) return false;

        if (req.ServiceName is not null) service.ServiceName = Require(req.ServiceName, "A service name cannot be blank.");
        if (req.Description is not null) service.Description = Trim(req.Description);

        if (req.DimDivisor            is { } divisor) service.DimDivisor            = divisor;
        if (req.MinimumChargeableKg   is { } minimum) service.MinimumChargeableKg   = minimum;
        if (req.WeightRoundingKg      is { } step)    service.WeightRoundingKg      = step;
        if (req.MaxWeightKgPerPackage is { } weight)  service.MaxWeightKgPerPackage = weight;
        if (req.MaxLengthCm           is { } length)  service.MaxLengthCm           = length;
        if (req.MaxLengthPlusGirthCm  is { } girth)   service.MaxLengthPlusGirthCm  = girth;
        if (req.TransitDays           is { } transit) service.TransitDays           = transit;

        if (req.SupportsCod       is { } cod)  service.SupportsCod       = cod;
        if (req.SupportsHazardous is { } haz)  service.SupportsHazardous = haz;

        ApplyClears(service, req.ClearLimits);

        ValidateLimits(service.DimDivisor, service.MinimumChargeableKg, service.WeightRoundingKg,
                       service.MaxWeightKgPerPackage, service.MaxLengthCm, service.MaxLengthPlusGirthCm,
                       service.TransitDays);

        var now = DateTime.UtcNow;

        if (req.IsActive is { } isActive)
        {
            if (!isActive && service.IsDefault)
                throw new ConflictException(
                    $"'{service.ServiceName}' is this carrier's default service. Make another the " +
                    "default before deactivating it, or a consignment naming no service has nothing to use.");

            service.IsActive = isActive;
        }

        if (req.IsDefault is true)
        {
            if (!service.IsActive)
                throw new ConflictException("An inactive service cannot be the default.");

            var siblings = await _db.CarrierServices
                .Where(s => s.CarrierId == service.CarrierId && s.Id != service.Id && !s.IsDelete)
                .ToListAsync();

            ClearOtherDefaults(siblings, userId, now);
            service.IsDefault = true;
        }
        else if (req.IsDefault is false && service.IsDefault)
        {
            throw new ConflictException(
                "A carrier needs a default service. Make another the default instead — that clears this one.");
        }

        service.ModifiedBy   = userId;
        service.ModifiedDate = now;

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(Guid uuid, int userId)
    {
        var service = await _db.CarrierServices.FirstOrDefaultAsync(s => s.UUID == uuid && !s.IsDelete);
        if (service is null) return false;

        var siblings = await _db.CarrierServices
            .CountAsync(s => s.CarrierId == service.CarrierId && s.Id != service.Id && !s.IsDelete);

        if (service.IsDefault && siblings > 0)
            throw new ConflictException(
                $"'{service.ServiceName}' is the default service. Make another the default before removing it.");

        // Soft: consignments booked on this service still name its code, and the row is what lets
        // them still be explained.
        service.IsDelete     = true;
        service.IsActive     = false;
        service.IsDefault    = false;
        service.ModifiedBy   = userId;
        service.ModifiedDate = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Every limit is a positive number or absent. A zero or negative divisor is the dangerous one:
    /// it would make <c>L×W×H ÷ divisor</c> throw or return a negative weight, and T-44 divides by
    /// this. Refusing it here is cheaper than defending against it at every use.
    /// </summary>
    private static void ValidateLimits(
        int? dimDivisor, decimal? minimumChargeable, decimal? weightRounding, decimal? maxWeight,
        decimal? maxLength, decimal? maxGirth, int? transitDays)
    {
        if (dimDivisor is <= 0)
            throw new BadRequestException(
                "A dim divisor must be greater than zero — it is the denominator in L×W×H ÷ divisor. " +
                "Leave it empty for a service that does not charge on volume.");

        Positive(minimumChargeable, "Minimum chargeable weight");
        // Zero would make Ceiling(weight ÷ step) divide by zero, the same trap as the divisor.
        Positive(weightRounding,    "Weight rounding step");
        Positive(maxWeight,         "Maximum weight");
        Positive(maxLength,         "Maximum length");
        Positive(maxGirth,          "Maximum length plus girth");

        if (transitDays is < 0)
            throw new BadRequestException("Transit days cannot be negative.");
    }

    private static void Positive(decimal? value, string what)
    {
        if (value is <= 0)
            throw new BadRequestException($"{what} must be greater than zero when it is given.");
    }

    private static void ApplyClears(CarrierService service, List<string>? clears)
    {
        if (clears is null || clears.Count == 0) return;

        foreach (var raw in clears)
        {
            switch (raw?.Trim().ToUpperInvariant())
            {
                case "DIM_DIVISOR":         service.DimDivisor            = null; break;
                case "MINIMUM_CHARGEABLE":  service.MinimumChargeableKg   = null; break;
                case "WEIGHT_ROUNDING":     service.WeightRoundingKg      = null; break;
                case "MAX_WEIGHT":          service.MaxWeightKgPerPackage = null; break;
                case "MAX_LENGTH":          service.MaxLengthCm           = null; break;
                case "MAX_GIRTH":           service.MaxLengthPlusGirthCm  = null; break;
                case "TRANSIT_DAYS":        service.TransitDays           = null; break;
                default:
                    throw new BadRequestException(
                        $"'{raw}' is not a clearable limit. Valid values: {string.Join(", ", Clearable)}.");
            }
        }
    }

    private static void ClearOtherDefaults(IEnumerable<CarrierService> services, int userId, DateTime now)
    {
        foreach (var other in services.Where(s => s.IsDefault))
        {
            other.IsDefault    = false;
            other.ModifiedBy   = userId;
            other.ModifiedDate = now;
        }
    }

    private static string Require(string? value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new BadRequestException(message) : value.Trim();

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
