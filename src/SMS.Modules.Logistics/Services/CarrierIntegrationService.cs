using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Couriers;
using SMS.Modules.Logistics.Couriers.Manual;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Services;

/// <summary>
/// Which adapter a carrier books through — the setting nothing could write until now, so pointing a
/// carrier at an integration meant a hand-written SQL update.
/// </summary>
public interface ICarrierIntegrationService
{
    /// <summary>The adapters this deployment has, for the screen to offer. Manual is a mode, not an entry here.</summary>
    IReadOnlyList<CourierProviderModel> GetProviders();

    Task<CarrierIntegrationModel?> GetAsync(Guid carrierUuid);

    Task<CarrierIntegrationModel?> SetAsync(Guid carrierUuid, SetCarrierIntegrationRequest req, int userId);
}

internal sealed class CarrierIntegrationService : ICarrierIntegrationService
{
    /// <summary>
    /// Consignments with no carrier commitment left to lose: nothing booked yet, or finished. Everything
    /// else has an airway bill somewhere, or a booking under way.
    /// </summary>
    private static readonly string[] Settled =
    [
        LogisticsCode.Of(ShipmentStatus.Draft),     LogisticsCode.Of(ShipmentStatus.Rated),
        LogisticsCode.Of(ShipmentStatus.BookingFailed),
        LogisticsCode.Of(ShipmentStatus.Delivered), LogisticsCode.Of(ShipmentStatus.Cancelled),
        LogisticsCode.Of(ShipmentStatus.ReturnedToOrigin), LogisticsCode.Of(ShipmentStatus.Lost)
    ];

    private readonly LogisticsDbContext       _db;
    private readonly ICourierProviderRegistry _registry;

    public CarrierIntegrationService(LogisticsDbContext db, ICourierProviderRegistry registry)
    {
        _db       = db;
        _registry = registry;
    }

    public IReadOnlyList<CourierProviderModel> GetProviders() =>
    [
        .. _registry.All
            .Where(p => !string.Equals(p.Key, ManualCourierProvider.ProviderKey, StringComparison.OrdinalIgnoreCase))
            .Select(p => new CourierProviderModel
            {
                Key                  = p.Key,
                DisplayName          = p.DisplayName,
                SupportsBooking      = p.Capabilities.SupportsBooking,
                SupportsRating       = p.Capabilities.SupportsRating,
                SupportsTracking     = p.Capabilities.SupportsTracking,
                SupportsLabels       = p.Capabilities.SupportsLabels,
                SupportsCancellation = p.Capabilities.SupportsCancellation,
                SupportsCod          = p.Capabilities.SupportsCod,
                SupportsMultiPiece   = p.Capabilities.SupportsMultiPiece,
                Credentials          = p is ICourierCredentialSpec spec
                    ? [.. spec.Credentials.Select(c => new CourierCredentialModel
                      {
                          Key = c.Key, Description = c.Description, Required = c.Required, IsSecret = c.IsSecret
                      })]
                    : []
            })
    ];

    public async Task<CarrierIntegrationModel?> GetAsync(Guid carrierUuid)
    {
        var carrier = await _db.Carriers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.UUID == carrierUuid && !c.IsDelete);

        return carrier is null ? null : ToModel(carrier);
    }

    public async Task<CarrierIntegrationModel?> SetAsync(Guid carrierUuid, SetCarrierIntegrationRequest req, int userId)
    {
        ArgumentNullException.ThrowIfNull(req);

        var carrier = await _db.Carriers.FirstOrDefaultAsync(c => c.UUID == carrierUuid && !c.IsDelete);
        if (carrier is null) return null;

        if (!LogisticsCode.TryParse<CarrierIntegrationMode>(req.IntegrationMode?.Trim().ToUpperInvariant(), out var mode))
            throw new BadRequestException(
                $"'{req.IntegrationMode}' is not an integration mode. Choose MANUAL or API.");

        var key = KeyFor(mode, req.ProviderKey);

        var (currentMode, currentKey) = Current(carrier);

        if (currentMode == mode && string.Equals(currentKey, key, StringComparison.OrdinalIgnoreCase))
            return ToModel(carrier);

        // A parcel booked through one adapter is tracked through the same one. Move the carrier while
        // consignments are on the road and their tracking is asked of a system that never heard of them.
        var inFlight = await _db.Consignments.CountAsync(c =>
            c.CarrierId == carrier.Id && !c.IsDelete && !Settled.Contains(c.Status));

        if (inFlight > 0)
            throw new ConflictException(
                $"{carrier.Name} has {inFlight} consignment(s) booked or being booked through " +
                $"{DisplayNameOf(currentKey)}. Their tracking would be asked of {DisplayNameOf(key)}, which " +
                "does not know them. Let them finish, or cancel them, then change the integration.");

        carrier.IntegrationMode = LogisticsCode.Of(mode);
        carrier.ProviderKey     = key;
        carrier.ModifiedBy      = userId;
        carrier.ModifiedDate    = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return ToModel(carrier);
    }

    /// <summary>The adapter key a mode implies, or a refusal saying why it cannot have one.</summary>
    private string? KeyFor(CarrierIntegrationMode mode, string? requested)
    {
        switch (mode)
        {
            case CarrierIntegrationMode.Manual:
                // The convention every existing carrier already follows.
                return ManualCourierProvider.ProviderKey;

            case CarrierIntegrationMode.File:
                throw new BadRequestException(
                    "FILE integration has no adapter yet, so a carrier set to it could not be booked at all. " +
                    "Choose MANUAL or API.");
        }

        var key = requested?.Trim();

        if (string.IsNullOrEmpty(key))
            throw new BadRequestException("API integration needs a provider — say which adapter books this carrier.");

        var providers = GetProviders();
        var match = providers.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

        if (match is null)
            throw new BadRequestException(
                $"No courier adapter is registered as '{key}'. This deployment has: " +
                $"{(providers.Count == 0 ? "none" : string.Join(", ", providers.Select(p => p.Key)))}.");

        // The registry's own spelling, so a carrier row never holds "dhl_express" beside "DHL_EXPRESS".
        return match.Key;
    }

    private static (CarrierIntegrationMode Mode, string? Key) Current(Carrier carrier)
    {
        var mode = LogisticsCode.TryParse<CarrierIntegrationMode>(carrier.IntegrationMode, out var parsed)
            ? parsed
            : CarrierIntegrationMode.Manual;

        return (mode, mode == CarrierIntegrationMode.Manual ? ManualCourierProvider.ProviderKey : carrier.ProviderKey);
    }

    private string DisplayNameOf(string? key) =>
        _registry.Find(key)?.DisplayName ?? key ?? "no adapter";

    private CarrierIntegrationModel ToModel(Carrier carrier)
    {
        var (mode, key) = Current(carrier);
        var provider    = _registry.Find(key);

        return new CarrierIntegrationModel
        {
            CarrierUuid         = carrier.UUID,
            CarrierName         = carrier.Name,
            IntegrationMode     = LogisticsCode.Of(mode),
            ProviderKey         = key,
            ProviderDisplayName = provider?.DisplayName,
            Warning             = provider is null
                ? $"No courier adapter is registered as '{key}', so nothing can be booked through this carrier."
                : null
        };
    }
}
