using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Pos.Data;
using SMS.Modules.Pos.Domain;
using SMS.Modules.Pos.Hubs;
using SMS.Modules.Pos.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Pos.Services;

/// <summary>A38 API-CONTRACT §2 — stores, terminals (device binding, PIN lockout), store staff and their PINs.</summary>
public interface IPosStoreService
{
    Task<PaginatedResponse<PosStoreModel>> ListStoresAsync(PosStoreListFilter filter, CancellationToken ct = default);
    Task<PosStoreModel> GetStoreAsync(Guid uuid, CancellationToken ct = default);
    Task<PosStoreModel> CreateStoreAsync(PosStoreUpsertRequest req, int userId, CancellationToken ct = default);
    Task<PosStoreModel> UpdateStoreAsync(Guid uuid, PosStoreUpsertRequest req, int userId, CancellationToken ct = default);
    Task<PosStoreModel> SetStoreStatusAsync(Guid uuid, bool isActive, int userId, CancellationToken ct = default);

    Task<IReadOnlyList<PosTerminalModel>> ListTerminalsAsync(Guid storeUuid, CancellationToken ct = default);
    Task<PosTerminalModel> CreateTerminalAsync(Guid storeUuid, PosTerminalCreateRequest req, int userId, CancellationToken ct = default);
    Task<PosTerminalModel> UpdateTerminalAsync(Guid uuid, PosTerminalUpdateRequest req, CancellationToken ct = default);
    Task<PosTerminalConfigModel> BindAsync(Guid uuid, Guid deviceUuid, PosCaller caller, CancellationToken ct = default);
    Task<PosTerminalModel> UnbindAsync(Guid uuid, CancellationToken ct = default);
    Task<PosTerminalModel> UnlockAsync(Guid uuid, CancellationToken ct = default);
    Task<PosTerminalConfigModel?> GetByDeviceAsync(Guid deviceUuid, CancellationToken ct = default);
    Task<PosTerminalStatusModel> GetStatusAsync(Guid uuid, CancellationToken ct = default);
    Task<IReadOnlyList<PosTerminalModel>> ListAvailableAsync(PosCaller caller, CancellationToken ct = default);
    Task ForceSyncAsync(Guid uuid, string? syncType, CancellationToken ct = default);
    Task ForceLogoutAsync(Guid uuid, string? reason, CancellationToken ct = default);

    Task<IReadOnlyList<PosStaffMemberModel>> ListStaffAsync(Guid storeUuid, CancellationToken ct = default);
    Task<IReadOnlyList<PosStaffCandidateModel>> ListCandidatesAsync(Guid storeUuid, string? search, CancellationToken ct = default);
    Task<PosStaffMemberModel> UpsertStaffAsync(Guid storeUuid, int userId, PosStaffUpsertRequest req, int actorUserId, CancellationToken ct = default);
    Task RemoveStaffAsync(Guid storeUuid, int userId, CancellationToken ct = default);
}

internal sealed partial class PosStoreService : IPosStoreService
{
    internal const int CandidateLimit = 50;

    private readonly PosDbContext         _db;
    private readonly ITenantContext       _tenant;
    private readonly PosReadModel         _read;
    private readonly IPosHubNotifier      _hub;
    private readonly IPosPinService?      _pins;
    private readonly IWarehouseDirectory? _warehouses;
    private readonly ITaxCodeLookup?      _taxCodes;
    private readonly IModuleGate?         _gate;

    public PosStoreService(
        PosDbContext db, ITenantContext tenant, PosReadModel read, IPosHubNotifier hub, IPosPinService? pins = null,
        IWarehouseDirectory? warehouses = null, ITaxCodeLookup? taxCodes = null, IModuleGate? gate = null)
    {
        _db         = db;
        _tenant     = tenant;
        _read       = read;
        _hub        = hub;
        _pins       = pins;
        _warehouses = warehouses;
        _taxCodes   = taxCodes;
        _gate       = gate;
    }

    private Guid Org => _tenant.OrganizationId;
    private IPosPinService Pins => _pins ?? throw new BadRequestException("Cashier PINs are not available on this server.");

    // [0-9], not \d (which accepts other-script digits) — same rule as Auth (D-17).
    [GeneratedRegex(@"^[0-9]{4,6}$")]
    private static partial Regex PinPattern();

    internal static bool IsValidPin(string? pin) => pin is not null && PinPattern().IsMatch(pin);

    // ── Stores ──────────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<PosStoreModel>> ListStoresAsync(PosStoreListFilter f, CancellationToken ct = default)
    {
        var q = _db.Stores.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var s = f.Search.Trim();
            q = q.Where(x => x.Code.Contains(s) || x.Name.Contains(s) || (x.City != null && x.City.Contains(s)));
        }
        if (f.IsActive is { } active) q = q.Where(x => x.IsActive == active);
        var (page, size) = Paging(f.Page, f.PageSize);
        var total  = await q.CountAsync(ct);
        var stores = await q.OrderBy(x => x.Code).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return Page(await _read.StoresAsync(stores, ct), total, page, size);
    }

    public async Task<PosStoreModel> GetStoreAsync(Guid uuid, CancellationToken ct = default) =>
        await _read.StoreAsync(await StoreAsync(uuid, ct), ct);

    public async Task<PosStoreModel> CreateStoreAsync(PosStoreUpsertRequest req, int userId, CancellationToken ct = default)
    {
        var store = new PosStore { OrganizationId = Org, CreatedBy = userId, IsActive = req.IsActive ?? true };
        await ApplyAsync(store, req, ct);
        if (store.IsActive) await EnsureCanActivateAsync(store, ct);
        _db.Stores.Add(store);
        await SaveAsync(ct);
        return await _read.StoreAsync(store, ct);
    }

    public async Task<PosStoreModel> UpdateStoreAsync(Guid uuid, PosStoreUpsertRequest req, int userId, CancellationToken ct = default)
    {
        var store = await StoreAsync(uuid, ct);
        CheckRowVersion(store, req.RowVersion);
        if (req.WarehouseUuid != store.WarehouseUuid && await _db.Shifts.AnyAsync(s => s.StoreId == store.Id, ct))
            throw new BadRequestException("The warehouse cannot be changed once the store has shifts.");
        await ApplyAsync(store, req, ct);
        if (req.IsActive is { } active && active != store.IsActive) await ChangeStatusAsync(store, active, ct);
        await SaveAsync(ct);
        await _hub.ConfigChangedAsync(Org, store.Uuid, "STORE");
        return await _read.StoreAsync(store, ct);
    }

    public async Task<PosStoreModel> SetStoreStatusAsync(Guid uuid, bool isActive, int userId, CancellationToken ct = default)
    {
        var store = await StoreAsync(uuid, ct);
        if (store.IsActive != isActive)
        {
            await ChangeStatusAsync(store, isActive, ct);
            await SaveAsync(ct);
            await _hub.ConfigChangedAsync(Org, store.Uuid, "STORE");
        }
        return await _read.StoreAsync(store, ct);
    }

    /// <summary>Deactivate: refused while a shift is OPEN/CLOSING; deactivates the terminals. Activate: warehouse + multi-store rules.</summary>
    private async Task ChangeStatusAsync(PosStore store, bool active, CancellationToken ct)
    {
        if (!active)
        {
            if (await _db.Shifts.AnyAsync(s => s.StoreId == store.Id && PosShiftStatus.Active.Contains(s.Status), ct))
                throw new BadRequestException("Close all shifts before deactivating the store.");
            foreach (var t in await _db.Terminals.Where(t => t.StoreId == store.Id && t.IsActive).ToListAsync(ct))
            {
                t.IsActive   = false;
                t.RowVersion = PosTerminal.NewStamp();
            }
            store.IsActive = false;
            return;
        }
        await EnsureCanActivateAsync(store, ct);
        store.IsActive = true;
    }

    /// <summary>POS-STR-02 (warehouse backs one active store) and FEATURE_POS_MULTI_STORE off ⇒ at most one active store.</summary>
    private async Task EnsureCanActivateAsync(PosStore store, CancellationToken ct)
    {
        var other = await _db.Stores.Where(s => s.IsActive && s.Id != store.Id && s.WarehouseUuid == store.WarehouseUuid)
            .Select(s => s.Code).FirstOrDefaultAsync(ct);
        if (other is not null) throw new BadRequestException($"Warehouse is already linked to store {other}.");

        if (!await _gate.IsOnAsync(Org, PosFeatureCodes.MultiStore, ct)
            && await _db.Stores.AnyAsync(s => s.IsActive && s.Id != store.Id, ct))
            throw new BadRequestException("Multi-store is not enabled for your organization: only one active store is allowed.");
    }

    private async Task ApplyAsync(PosStore s, PosStoreUpsertRequest r, CancellationToken ct)
    {
        var code = r.Code?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code) || code.Length > 20 || !code.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
            throw new BadRequestException("Store code is required: up to 20 letters, digits, '-' or '_'.");
        if (string.IsNullOrWhiteSpace(r.Name)) throw new BadRequestException("Store name is required.");
        if (await _db.Stores.AnyAsync(x => x.Code == code && x.Id != s.Id, ct))
            throw new BadRequestException($"Store code {code} already exists.");
        if (r.WarehouseUuid == Guid.Empty) throw new BadRequestException("Warehouse is required.");

        var warehouseName = s.WarehouseUuid == r.WarehouseUuid ? s.WarehouseName : string.Empty;
        if (_warehouses is not null)
        {
            var warehouse = await _warehouses.FindAsync(r.WarehouseUuid, ct)
                ?? throw new BadRequestException("Warehouse not found.");
            if (!warehouse.IsActive && s.WarehouseUuid != r.WarehouseUuid) throw new BadRequestException("Warehouse is inactive.");
            warehouseName = warehouse.Name;
        }
        if (s.IsActive || r.IsActive == true)
        {
            var other = await _db.Stores.Where(x => x.IsActive && x.Id != s.Id && x.WarehouseUuid == r.WarehouseUuid)
                .Select(x => x.Code).FirstOrDefaultAsync(ct);
            if (other is not null) throw new BadRequestException($"Warehouse is already linked to store {other}.");
        }

        var timezone = string.IsNullOrWhiteSpace(r.Timezone) ? "UTC" : r.Timezone.Trim();
        if (!PosClock.TryFindZone(timezone, out _)) throw new BadRequestException($"Unknown time zone '{timezone}'.");
        if (r.OfflineCreditTolerancePct is < 0 or > 100) throw new BadRequestException("Offline credit tolerance must be between 0 and 100%.");
        if (r.AutoCloseShiftHours is < 1 or > 168) throw new BadRequestException("Stale shift hours must be between 1 and 168.");
        if (r.VarianceThreshold < 0) throw new BadRequestException("Variance threshold cannot be negative.");

        // D-11 — the store's default sales tax code (Finance); none given ⇒ Finance's default sales code, if any.
        TaxCodeInfo? tax = null;
        if (_taxCodes is not null)
        {
            if (r.DefaultTaxCodeUuid is { } taxUuid)
            {
                tax = await _taxCodes.GetAsync(taxUuid, ct);
                if (tax is null || (!tax.IsActive && taxUuid != s.DefaultTaxCodeUuid) || !TaxCodeUsage.Allows(tax.Usage, TaxCodeUsage.Sales))
                    throw new BadRequestException("Default tax code must be an active sales tax code.");
            }
            else tax = await _taxCodes.GetDefaultAsync(TaxCodeUsage.Sales, ct);
        }
        else if (r.DefaultTaxCodeUuid is not null && r.DefaultTaxCodeUuid != s.DefaultTaxCodeUuid)
            throw new BadRequestException("Tax codes are not available on this server.");

        s.Code = code; s.Name = r.Name.Trim(); s.WarehouseUuid = r.WarehouseUuid; s.WarehouseName = warehouseName;
        s.AddressLine1 = Clean(r.AddressLine1); s.AddressLine2 = Clean(r.AddressLine2); s.City = Clean(r.City);
        s.StateProvince = Clean(r.StateProvince); s.PostalCode = Clean(r.PostalCode); s.Country = Clean(r.Country);
        s.Phone = Clean(r.Phone); s.Email = Clean(r.Email); s.Timezone = timezone; s.TaxInclusivePricing = r.TaxInclusivePricing;
        if (tax is not null) { s.DefaultTaxCodeUuid = tax.Uuid; s.DefaultTaxCode = tax.Code; s.DefaultTaxPercent = tax.RatePercent; }
        else if (_taxCodes is not null) { s.DefaultTaxCodeUuid = null; s.DefaultTaxCode = null; s.DefaultTaxPercent = 0m; }
        s.ReceiptHeader = Clean(r.ReceiptHeader); s.ReceiptFooter = Clean(r.ReceiptFooter);
        s.OfflineCreditTolerancePct = r.OfflineCreditTolerancePct ?? s.OfflineCreditTolerancePct;
        s.AutoCloseShiftHours = r.AutoCloseShiftHours ?? s.AutoCloseShiftHours;
        s.VarianceThreshold = r.VarianceThreshold ?? s.VarianceThreshold;
    }

    // ── Terminals ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<PosTerminalModel>> ListTerminalsAsync(Guid storeUuid, CancellationToken ct = default)
    {
        var store = await StoreAsync(storeUuid, ct);
        var terminals = await _db.Terminals.Include(t => t.Store).Where(t => t.StoreId == store.Id).OrderBy(t => t.Code).ToListAsync(ct);
        return await _read.TerminalsAsync(terminals, ct);
    }

    public async Task<PosTerminalModel> CreateTerminalAsync(Guid storeUuid, PosTerminalCreateRequest req, int userId, CancellationToken ct = default)
    {
        var store = await StoreAsync(storeUuid, ct);
        if (!store.IsActive) throw new BadRequestException("The store is inactive.");
        var terminal = new PosTerminal { OrganizationId = Org, Store = store, StoreId = store.Id, CreatedBy = userId };
        await ApplyAsync(terminal, req.Code, req.Name, ct);
        _db.Terminals.Add(terminal);
        await SaveAsync(ct);
        await _hub.ConfigChangedAsync(Org, store.Uuid, "TERMINAL");
        return await _read.TerminalAsync(terminal, ct);
    }

    public async Task<PosTerminalModel> UpdateTerminalAsync(Guid uuid, PosTerminalUpdateRequest req, CancellationToken ct = default)
    {
        var terminal = await TerminalAsync(uuid, ct);
        if (req.RowVersion is { Length: > 0 } rv && !rv.SequenceEqual(terminal.RowVersion))
            throw new ConflictException("The terminal was changed by someone else. Reload and try again.");
        await ApplyAsync(terminal, req.Code, req.Name, ct);
        if (req.IsActive != terminal.IsActive)
        {
            if (req.IsActive && !terminal.Store.IsActive) throw new BadRequestException("The store is inactive.");
            if (!req.IsActive && await ActiveShiftAsync(terminal.Id, ct) is not null)
                throw new BadRequestException("Close the terminal's shift before deactivating it.");
            terminal.IsActive = req.IsActive;
        }
        terminal.RowVersion = PosTerminal.NewStamp();
        await SaveAsync(ct);
        await _hub.ConfigChangedAsync(Org, terminal.Store.Uuid, "TERMINAL");
        return await _read.TerminalAsync(terminal, ct);
    }

    private async Task ApplyAsync(PosTerminal t, string? code, string? name, CancellationToken ct)
    {
        code = code?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code) || code.Length > 20) throw new BadRequestException("Terminal code is required (up to 20 characters).");
        if (string.IsNullOrWhiteSpace(name)) throw new BadRequestException("Terminal name is required.");
        if (await _db.Terminals.AnyAsync(x => x.StoreId == t.StoreId && x.Code == code && x.Id != t.Id, ct))
            throw new BadRequestException("Terminal code already exists in this store.");
        t.Code = code;
        t.Name = name.Trim();
    }

    public async Task<PosTerminalConfigModel> BindAsync(Guid uuid, Guid deviceUuid, PosCaller caller, CancellationToken ct = default)
    {
        if (deviceUuid == Guid.Empty) throw new BadRequestException("Device id is required.");
        var terminal = await TerminalAsync(uuid, ct);
        if (!caller.Has(PosPermissions.TerminalManage) && !await IsStaffAsync(terminal.StoreId, caller.UserId, ct))
            throw new ForbiddenException("You are not staff of this store.");
        if (!terminal.IsActive || !terminal.Store.IsActive) throw new BadRequestException("The terminal is inactive.");
        if (terminal.DeviceUuid == deviceUuid) return await _read.ConfigAsync(terminal, ct);
        if (terminal.DeviceUuid is not null)
            throw new BadRequestException("The terminal is bound to another device. Unbind first.");
        var other = await _db.Terminals.Where(t => t.DeviceUuid == deviceUuid && t.Id != terminal.Id).Select(t => t.Code).FirstOrDefaultAsync(ct);
        if (other is not null) throw new BadRequestException($"This device is already bound to terminal {other}. Unbind it first.");

        terminal.DeviceUuid    = deviceUuid;
        terminal.DeviceBoundAt = _read.UtcNow;
        terminal.RowVersion    = PosTerminal.NewStamp();
        await SaveAsync(ct);
        return await _read.ConfigAsync(terminal, ct);
    }

    /// <summary>D-4 — the device is told to log out and wipe its cached PIN hashes.</summary>
    public async Task<PosTerminalModel> UnbindAsync(Guid uuid, CancellationToken ct = default)
    {
        var terminal = await TerminalAsync(uuid, ct);
        if (terminal.DeviceUuid is not null)
        {
            terminal.DeviceUuid    = null;
            terminal.DeviceBoundAt = null;
            terminal.RowVersion    = PosTerminal.NewStamp();
            await SaveAsync(ct);
            await _hub.ForceLogoutAsync(Org, terminal.Uuid, "UNBOUND");
        }
        return await _read.TerminalAsync(terminal, ct);
    }

    public async Task<PosTerminalModel> UnlockAsync(Guid uuid, CancellationToken ct = default)
    {
        var terminal = await TerminalAsync(uuid, ct);
        terminal.PinFailedAttempts = 0;
        terminal.PinLockedUntil    = null;
        await SaveAsync(ct);
        return await _read.TerminalAsync(terminal, ct);
    }

    public async Task<PosTerminalConfigModel?> GetByDeviceAsync(Guid deviceUuid, CancellationToken ct = default)
    {
        var terminal = await _db.Terminals.Include(t => t.Store).FirstOrDefaultAsync(t => t.DeviceUuid == deviceUuid, ct);
        return terminal is null ? null : await _read.ConfigAsync(terminal, ct);
    }

    public async Task<PosTerminalStatusModel> GetStatusAsync(Guid uuid, CancellationToken ct = default)
    {
        var t = await _read.TerminalAsync(await TerminalAsync(uuid, ct), ct);
        return new PosTerminalStatusModel
        {
            IsOnline = t.IsOnline, LastSeenAt = t.LastSeenAt, LastSyncAt = t.LastSyncAt, DeviceBound = t.DeviceBound,
            ActiveShiftUuid = t.ActiveShiftUuid, PinLockedUntil = t.PinLockedUntil
        };
    }

    /// <summary>Active terminals of active stores where the caller is active staff — every store with POS_STORE_VIEW.</summary>
    public async Task<IReadOnlyList<PosTerminalModel>> ListAvailableAsync(PosCaller caller, CancellationToken ct = default)
    {
        var q = _db.Terminals.Include(t => t.Store).Where(t => t.IsActive && t.Store.IsActive);
        if (!caller.Has(PosPermissions.StoreView))
        {
            var stores = _db.StoreStaff.Where(s => s.UserId == caller.UserId && s.IsActive).Select(s => s.StoreId);
            q = q.Where(t => stores.Contains(t.StoreId));
        }
        return await _read.TerminalsAsync(await q.OrderBy(t => t.Store.Code).ThenBy(t => t.Code).ToListAsync(ct), ct);
    }

    public async Task ForceSyncAsync(Guid uuid, string? syncType, CancellationToken ct = default)
    {
        var type = (syncType ?? "FULL").Trim().ToUpperInvariant();
        if (type is not ("FULL" or "DELTA")) throw new BadRequestException("Sync type must be FULL or DELTA.");
        var terminal = await TerminalAsync(uuid, ct);
        await _hub.ForceSyncAsync(Org, terminal.Uuid, type);
    }

    /// <summary>Ends the terminal's active cashier session and tells the device to return to the PIN screen.</summary>
    public async Task ForceLogoutAsync(Guid uuid, string? reason, CancellationToken ct = default)
    {
        var terminal = await TerminalAsync(uuid, ct);
        var now = _read.UtcNow;
        foreach (var s in await _db.CashierSessions.Where(s => s.TerminalId == terminal.Id && s.IsActive).ToListAsync(ct))
        {
            s.IsActive  = false;
            s.EndedAt   = now;
            s.EndReason = PosSessionEndReason.ForcedLogout;
        }
        await SaveAsync(ct);
        await _hub.ForceLogoutAsync(Org, terminal.Uuid, string.IsNullOrWhiteSpace(reason) ? "Signed out by a supervisor." : reason.Trim());
    }

    // ── Staff ───────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<PosStaffMemberModel>> ListStaffAsync(Guid storeUuid, CancellationToken ct = default)
    {
        var store = await StoreAsync(storeUuid, ct);
        var staff = await _db.StoreStaff.AsNoTracking().Where(s => s.StoreId == store.Id).ToListAsync(ct);
        var users = await _read.UsersAsync(staff.Select(s => s.UserId), ct);
        return staff.Select(s => Member(s, users.GetValueOrDefault(s.UserId)))
            .OrderByDescending(m => m.IsActive).ThenBy(m => m.Name).ToList();
    }

    public async Task<IReadOnlyList<PosStaffCandidateModel>> ListCandidatesAsync(Guid storeUuid, string? search, CancellationToken ct = default)
    {
        var store  = await StoreAsync(storeUuid, ct);
        var staff  = await _db.StoreStaff.Where(s => s.StoreId == store.Id && s.IsActive).Select(s => s.UserId).ToListAsync(ct);
        var users  = await Pins.SearchUsersAsync(Org, search?.Trim(), CandidateLimit + staff.Count, ct);
        return users.Where(u => u.IsActive && !staff.Contains(u.UserId)).Take(CandidateLimit)
            .Select(u => new PosStaffCandidateModel { UserId = u.UserId, Name = u.Name, Email = u.Email, RoleName = u.RoleName }).ToList();
    }

    public async Task<PosStaffMemberModel> UpsertStaffAsync(Guid storeUuid, int userId, PosStaffUpsertRequest req, int actorUserId, CancellationToken ct = default)
    {
        var store = await StoreAsync(storeUuid, ct);
        var role  = req.PosRole?.Trim().ToUpperInvariant();
        if (!PosRole.IsValid(role)) throw new BadRequestException("POS role must be CASHIER or SUPERVISOR.");
        var user = (await Pins.GetUsersAsync(Org, [userId], ct)).FirstOrDefault(u => u.UserId == userId)
            ?? throw new NotFoundException("User", userId);
        if (!user.IsActive) throw new BadRequestException("The user is inactive.");

        var pin = string.IsNullOrEmpty(req.Pin) ? null : req.Pin.Trim();
        if (pin is not null)
        {
            if (!IsValidPin(pin)) throw new BadRequestException("PIN must be 4 to 6 digits.");
            // One PIN per user (auth.UserAccounts): unique among the staff of every store this user works in (D-4).
            var stores = await _db.StoreStaff.Where(s => s.UserId == userId && s.IsActive).Select(s => s.StoreId).ToListAsync(ct);
            stores.Add(store.Id);
            var colleagues = await _db.StoreStaff.Where(s => stores.Contains(s.StoreId) && s.IsActive && s.UserId != userId)
                .Select(s => s.UserId).Distinct().ToListAsync(ct);
            // Each check is a full PBKDF2 hash: only colleagues who have a PIN.
            if (colleagues.Count > 0)
                colleagues = (await Pins.GetUsersAsync(Org, colleagues, ct)).Where(u => u.HasPin).Select(u => u.UserId).ToList();
            if (colleagues.Count > 0 && await Pins.FindByPinAsync(Org, colleagues, pin, ct) is not null)
                throw new BadRequestException("This PIN is already used by another staff member of this store.");
        }

        var member = await _db.StoreStaff.FirstOrDefaultAsync(s => s.StoreId == store.Id && s.UserId == userId, ct);
        if (member is null)
            _db.StoreStaff.Add(member = new PosStoreStaff { OrganizationId = Org, StoreId = store.Id, UserId = userId, CreatedBy = actorUserId });
        member.PosRole  = role!;
        member.IsActive = true;
        await SaveAsync(ct);
        if (pin is not null) await Pins.SetPinAsync(Org, userId, pin, ct);
        await _hub.ConfigChangedAsync(Org, store.Uuid, "STAFF");

        var info = (await Pins.GetUsersAsync(Org, [userId], ct)).FirstOrDefault() ?? user;
        return Member(member, info with { HasPin = info.HasPin || pin is not null });
    }

    /// <summary>Soft: the row stays (history, re-adding) and the user's active sessions on the store's terminals end.</summary>
    public async Task RemoveStaffAsync(Guid storeUuid, int userId, CancellationToken ct = default)
    {
        var store  = await StoreAsync(storeUuid, ct);
        var member = await _db.StoreStaff.FirstOrDefaultAsync(s => s.StoreId == store.Id && s.UserId == userId, ct)
            ?? throw new NotFoundException("Staff member", userId);
        member.IsActive = false;
        var now = _read.UtcNow;
        var terminals = _db.Terminals.Where(t => t.StoreId == store.Id).Select(t => t.Id);
        foreach (var s in await _db.CashierSessions.Where(s => s.UserId == userId && s.IsActive && terminals.Contains(s.TerminalId)).ToListAsync(ct))
        {
            s.IsActive = false; s.EndedAt = now; s.EndReason = PosSessionEndReason.Ended;
        }
        await SaveAsync(ct);
        await _hub.ConfigChangedAsync(Org, store.Uuid, "STAFF");
    }

    private static PosStaffMemberModel Member(PosStoreStaff s, PosUserInfo? u) => new()
    {
        UserId = s.UserId, Name = u?.Name ?? $"User {s.UserId}", Email = u?.Email ?? string.Empty, RoleName = u?.RoleName ?? string.Empty,
        PosRole = s.PosRole, HasPin = u?.HasPin ?? false, IsActive = s.IsActive && (u?.IsActive ?? true)
    };

    // ── helpers ─────────────────────────────────────────────────────────────

    private async Task<PosStore> StoreAsync(Guid uuid, CancellationToken ct) =>
        await _db.Stores.FirstOrDefaultAsync(s => s.Uuid == uuid, ct) ?? throw new NotFoundException("Store", uuid);

    private async Task<PosTerminal> TerminalAsync(Guid uuid, CancellationToken ct) =>
        await _db.Terminals.Include(t => t.Store).FirstOrDefaultAsync(t => t.Uuid == uuid, ct) ?? throw new NotFoundException("Terminal", uuid);

    private Task<PosShift?> ActiveShiftAsync(int terminalId, CancellationToken ct) =>
        _db.Shifts.FirstOrDefaultAsync(s => s.TerminalId == terminalId && PosShiftStatus.Active.Contains(s.Status), ct);

    private Task<bool> IsStaffAsync(int storeId, int userId, CancellationToken ct) =>
        _db.StoreStaff.AnyAsync(s => s.StoreId == storeId && s.UserId == userId && s.IsActive, ct);

    private void CheckRowVersion(PosStore store, byte[]? rowVersion)
    {
        if (rowVersion is not { Length: > 0 }) return;
        if (store.RowVersion is { Length: > 0 } current && !current.SequenceEqual(rowVersion))
            throw new ConflictException("The store was changed by someone else. Reload and try again.");
        _db.Entry(store).Property(x => x.RowVersion).OriginalValue = rowVersion;
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("The record was changed by someone else. Reload and try again."); }
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    internal static (int Page, int Size) Paging(int page, int size) => (Math.Max(1, page), Math.Clamp(size, 1, 200));

    internal static PaginatedResponse<T> Page<T>(List<T> data, int total, int page, int size) => new()
    {
        Data = data, TotalRecords = total, Page = page, PageSize = size, TotalPages = (int)Math.Ceiling(total / (double)size)
    };
}
