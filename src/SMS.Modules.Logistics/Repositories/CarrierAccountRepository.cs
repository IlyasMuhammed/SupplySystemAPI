using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Couriers;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Repositories;

internal interface ICarrierAccountRepository
{
    Task<Guid> CreateAsync(CreateCarrierAccountRequest req, int userId);
    Task<IReadOnlyList<CarrierAccountModel>> GetForCarrierAsync(Guid carrierUuid);
    Task<CarrierAccountModel?> GetByUuidAsync(Guid uuid);
    Task<bool> PatchAsync(Guid uuid, PatchCarrierAccountRequest req, int userId);
    Task<bool> DeleteAsync(Guid uuid, int userId);
}

/// <summary>
/// The accounts a carrier can be booked on, and what each is allowed to be asked for.
/// </summary>
internal sealed class CarrierAccountRepository : ICarrierAccountRepository
{
    /// <summary>The overridable capabilities, by the name a caller uses to clear one.</summary>
    private static readonly string[] Overridable =
        ["COD", "LABELS", "TRACKING", "CANCELLATION", "PICKUP_BOOKING"];

    private readonly LogisticsDbContext       _db;
    private readonly ICourierProviderRegistry _registry;

    public CarrierAccountRepository(LogisticsDbContext db, ICourierProviderRegistry registry)
    {
        _db       = db;
        _registry = registry;
    }

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<Guid> CreateAsync(CreateCarrierAccountRequest req, int userId)
    {
        ArgumentNullException.ThrowIfNull(req);

        var carrier = await _db.Carriers
            .FirstOrDefaultAsync(c => c.UUID == req.CarrierUuid && !c.IsDelete)
            ?? throw new NotFoundException("Carrier", req.CarrierUuid);

        var name = Require(req.AccountName, "An account name is required.");

        if (await _db.CarrierAccounts.AnyAsync(a =>
                a.CarrierId == carrier.Id && !a.IsDelete && a.AccountName == name))
            throw new ConflictException(
                $"{carrier.Name} already has an account called '{name}'. Two accounts sharing a " +
                "name is how somebody books on the wrong contract.");

        var existing = await _db.CarrierAccounts
            .Where(a => a.CarrierId == carrier.Id && !a.IsDelete)
            .ToListAsync();

        var now = DateTime.UtcNow;

        var account = new CarrierAccount
        {
            UUID               = Guid.NewGuid(),
            OrganizationId     = carrier.OrganizationId,
            CarrierId          = carrier.Id,
            AccountName        = name,
            AccountNumber      = Trim(req.AccountNumber),
            DefaultServiceCode = Trim(req.DefaultServiceCode),
            // The first account is the default whether or not anybody said so: an only account
            // that is not the default is a carrier nothing can book on.
            IsDefault          = req.IsDefault || existing.Count == 0,
            IsSandbox          = req.IsSandbox,
            CodEnabled           = req.CodEnabled,
            LabelsEnabled        = req.LabelsEnabled,
            TrackingEnabled      = req.TrackingEnabled,
            CancellationEnabled  = req.CancellationEnabled,
            PickupBookingEnabled = req.PickupBookingEnabled,
            Notes              = Trim(req.Notes),
            IsActive           = true,
            CreatedBy          = userId,
            CreatedDate        = now
        };

        if (account.IsDefault) ClearOtherDefaults(existing, userId, now);

        _db.CarrierAccounts.Add(account);
        await _db.SaveChangesAsync();

        return account.UUID;
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<CarrierAccountModel>> GetForCarrierAsync(Guid carrierUuid)
    {
        var carrier = await _db.Carriers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.UUID == carrierUuid && !c.IsDelete);

        if (carrier is null) return [];

        var accounts = await _db.CarrierAccounts.AsNoTracking()
            .Where(a => a.CarrierId == carrier.Id && !a.IsDelete)
            .OrderByDescending(a => a.IsDefault)
            .ThenBy(a => a.AccountName)
            .ToListAsync();

        return [.. accounts.Select(a => ToModel(a, carrier))];
    }

    public async Task<CarrierAccountModel?> GetByUuidAsync(Guid uuid)
    {
        var account = await _db.CarrierAccounts.AsNoTracking()
            .Include(a => a.Carrier)
            .FirstOrDefaultAsync(a => a.UUID == uuid && !a.IsDelete);

        return account is null ? null : ToModel(account, account.Carrier);
    }

    /// <summary>
    /// Presents each capability three ways — what the adapter can do, what the account says, and
    /// what actually applies — so a screen can explain why something is off rather than merely
    /// showing it greyed out.
    /// </summary>
    private CarrierAccountModel ToModel(CarrierAccount account, Carrier carrier)
    {
        var provider = ProviderFor(carrier, out var warning);
        var declared = provider?.Capabilities ?? new CourierCapabilities();
        var effective = provider is null
            ? new CourierCapabilities(SupportsBooking: false, SupportsMultiPiece: false)
            : CarrierCapabilityResolver.Resolve(declared, account);

        return new CarrierAccountModel
        {
            UUID                = account.UUID,
            CarrierUuid         = carrier.UUID,
            CarrierName         = carrier.Name,
            AccountName         = account.AccountName,
            AccountNumber       = account.AccountNumber,
            DefaultServiceCode  = account.DefaultServiceCode,
            IsDefault           = account.IsDefault,
            IsSandbox           = account.IsSandbox,
            IsActive            = account.IsActive,
            ProviderKey         = provider?.Key,
            ProviderDisplayName = provider?.DisplayName,
            ProviderWarning     = warning,
            Notes               = account.Notes,
            CreatedDate         = account.CreatedDate,
            Capabilities =
            [
                Capability("COD",            declared.SupportsCod,           account.CodEnabled,           effective.SupportsCod),
                Capability("LABELS",         declared.SupportsLabels,        account.LabelsEnabled,        effective.SupportsLabels),
                Capability("TRACKING",       declared.SupportsTracking,      account.TrackingEnabled,      effective.SupportsTracking),
                Capability("CANCELLATION",   declared.SupportsCancellation,  account.CancellationEnabled,  effective.SupportsCancellation),
                Capability("PICKUP_BOOKING", declared.SupportsPickupBooking, account.PickupBookingEnabled, effective.SupportsPickupBooking)
            ]
        };
    }

    private static CarrierCapabilityModel Capability(
        string name, bool supported, bool? onAccount, bool effective) => new()
    {
        Name                = name,
        SupportedByProvider = supported,
        EnabledOnAccount    = onAccount,
        Effective           = effective
    };

    /// <summary>
    /// The adapter this carrier would book through, or null with an explanation. Reading an account
    /// must not fail because an adapter is missing — that is exactly the configuration a screen is
    /// being opened to fix.
    /// </summary>
    private ICourierProvider? ProviderFor(Carrier carrier, out string? warning)
    {
        warning = null;

        var isManual = string.IsNullOrWhiteSpace(carrier.IntegrationMode)
                    || carrier.IntegrationMode == LogisticsCode.Of(CarrierIntegrationMode.Manual);

        var key = isManual ? Couriers.Manual.ManualCourierProvider.ProviderKey : carrier.ProviderKey;

        if (string.IsNullOrWhiteSpace(key))
        {
            warning = $"{carrier.Name} is set to {carrier.IntegrationMode} integration but names no "
                    + "courier provider, so nothing can book on this account.";
            return null;
        }

        var provider = _registry.Find(key);

        if (provider is null)
            warning = $"No courier provider is registered for '{key}'. This account cannot be "
                    + "booked on until one is deployed.";

        return provider;
    }

    // ── Update ────────────────────────────────────────────────────────────────

    public async Task<bool> PatchAsync(Guid uuid, PatchCarrierAccountRequest req, int userId)
    {
        ArgumentNullException.ThrowIfNull(req);

        var account = await _db.CarrierAccounts.FirstOrDefaultAsync(a => a.UUID == uuid && !a.IsDelete);
        if (account is null) return false;

        var now = DateTime.UtcNow;

        if (req.AccountName is not null)
        {
            var name = Require(req.AccountName, "An account name cannot be blank.");

            if (await _db.CarrierAccounts.AnyAsync(a =>
                    a.CarrierId == account.CarrierId && a.Id != account.Id
                 && !a.IsDelete && a.AccountName == name))
                throw new ConflictException($"Another account of this carrier is already called '{name}'.");

            account.AccountName = name;
        }

        if (req.AccountNumber      is not null) account.AccountNumber      = Trim(req.AccountNumber);
        if (req.DefaultServiceCode is not null) account.DefaultServiceCode = Trim(req.DefaultServiceCode);
        if (req.Notes              is not null) account.Notes              = Trim(req.Notes);
        if (req.IsSandbox          is { } sandbox) account.IsSandbox       = sandbox;

        if (req.CodEnabled           is { } cod)     account.CodEnabled           = cod;
        if (req.LabelsEnabled        is { } labels)  account.LabelsEnabled        = labels;
        if (req.TrackingEnabled      is { } track)   account.TrackingEnabled      = track;
        if (req.CancellationEnabled  is { } cancel)  account.CancellationEnabled  = cancel;
        if (req.PickupBookingEnabled is { } pickup)  account.PickupBookingEnabled = pickup;

        ApplyClears(account, req.ClearOverrides);

        if (req.IsActive is { } isActive)
        {
            if (!isActive && account.IsDefault)
                throw new ConflictException(
                    $"'{account.AccountName}' is this carrier's default account. Make another " +
                    "account the default before deactivating it, or bookings will have nowhere to go.");

            account.IsActive = isActive;
        }

        if (req.IsDefault is true)
        {
            if (!account.IsActive)
                throw new ConflictException("An inactive account cannot be the default.");

            var siblings = await _db.CarrierAccounts
                .Where(a => a.CarrierId == account.CarrierId && a.Id != account.Id && !a.IsDelete)
                .ToListAsync();

            ClearOtherDefaults(siblings, userId, now);
            account.IsDefault = true;
        }
        else if (req.IsDefault is false && account.IsDefault)
        {
            // Unsetting the only default would leave a carrier nothing can book on, and the fix
            // is to promote another account rather than to demote this one.
            throw new ConflictException(
                "A carrier needs a default account. Make another account the default instead — " +
                "that clears this one.");
        }

        account.ModifiedBy   = userId;
        account.ModifiedDate = now;

        await _db.SaveChangesAsync();
        return true;
    }

    private static void ApplyClears(CarrierAccount account, List<string>? clears)
    {
        if (clears is null || clears.Count == 0) return;

        foreach (var raw in clears)
        {
            var name = raw?.Trim().ToUpperInvariant();

            switch (name)
            {
                case "COD":            account.CodEnabled           = null; break;
                case "LABELS":         account.LabelsEnabled        = null; break;
                case "TRACKING":       account.TrackingEnabled      = null; break;
                case "CANCELLATION":   account.CancellationEnabled  = null; break;
                case "PICKUP_BOOKING": account.PickupBookingEnabled = null; break;
                default:
                    throw new BadRequestException(
                        $"'{raw}' is not an overridable capability. Valid values: " +
                        $"{string.Join(", ", Overridable)}.");
            }
        }
    }

    public async Task<bool> DeleteAsync(Guid uuid, int userId)
    {
        var account = await _db.CarrierAccounts.FirstOrDefaultAsync(a => a.UUID == uuid && !a.IsDelete);
        if (account is null) return false;

        var siblings = await _db.CarrierAccounts
            .CountAsync(a => a.CarrierId == account.CarrierId && a.Id != account.Id && !a.IsDelete);

        if (account.IsDefault && siblings > 0)
            throw new ConflictException(
                $"'{account.AccountName}' is the default account. Make another account the default " +
                "before removing it.");

        account.IsDelete     = true;
        account.IsActive     = false;
        account.IsDefault    = false;
        account.ModifiedBy   = userId;
        account.ModifiedDate = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void ClearOtherDefaults(IEnumerable<CarrierAccount> accounts, int userId, DateTime now)
    {
        foreach (var other in accounts.Where(a => a.IsDefault))
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
