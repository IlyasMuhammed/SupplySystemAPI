using Microsoft.EntityFrameworkCore;
using SMS.Modules.Pos.Data;
using SMS.Modules.Pos.Domain;
using SMS.Modules.Pos.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Pos.Services;

/// <summary>A38 API-CONTRACT §3 — PIN sign-in on a terminal (POS-CSH-07 lockout) and cashier sessions (POS-CSH-04).</summary>
public interface IPosSessionService
{
    Task<PosPinSignInModel> PinSignInAsync(PosPinSignInRequest req, CancellationToken ct = default);
    Task<PosCashierSessionModel> StartAsync(PosSessionStartRequest req, PosCaller caller, CancellationToken ct = default);
    Task<PosCashierSessionModel> EndAsync(Guid sessionUuid, PosCaller caller, CancellationToken ct = default);
}

internal sealed class PosSessionService : IPosSessionService
{
    internal const int MaxPinFailures = 5;
    internal static readonly TimeSpan PinLockout = TimeSpan.FromMinutes(5);

    private readonly PosDbContext    _db;
    private readonly ITenantContext  _tenant;
    private readonly PosReadModel    _read;
    private readonly IPosPinService? _pins;

    public PosSessionService(PosDbContext db, ITenantContext tenant, PosReadModel read, IPosPinService? pins = null)
    {
        _db     = db;
        _tenant = tenant;
        _read   = read;
        _pins   = pins;
    }

    private Guid Org => _tenant.OrganizationId;
    private IPosPinService Pins => _pins ?? throw new BadRequestException("Cashier PINs are not available on this server.");

    public async Task<PosPinSignInModel> PinSignInAsync(PosPinSignInRequest req, CancellationToken ct = default)
    {
        var terminal = await _db.Terminals.Include(t => t.Store).FirstOrDefaultAsync(t => t.Uuid == req.TerminalUuid, ct)
            ?? throw new NotFoundException("Terminal", req.TerminalUuid);
        if (!terminal.IsActive || !terminal.Store.IsActive) throw new BadRequestException("The terminal is inactive.");

        var now = _read.UtcNow;
        if (terminal.PinLockedUntil is { } until && until > now) throw Locked(terminal, until);

        var pin = req.Pin?.Trim();
        if (!PosStoreService.IsValidPin(pin)) throw new BadRequestException("PIN must be 4 to 6 digits.");

        var staff = await _db.StoreStaff.Where(s => s.StoreId == terminal.StoreId && s.IsActive).ToListAsync(ct);
        int? userId;
        if (req.UserId is { } requested)
        {
            if (staff.All(s => s.UserId != requested)) throw new BadRequestException("You are not staff of this store.");
            userId = await Pins.VerifyAsync(Org, requested, pin!, ct) ? requested : null;
        }
        else
        {
            // Each PIN check is a full PBKDF2 hash: only the store's active staff who have a PIN are candidates.
            var withPin = staff.Count == 0 ? [] : (await Pins.GetUsersAsync(Org, staff.Select(s => s.UserId).ToList(), ct))
                .Where(u => u.IsActive && u.HasPin).Select(u => u.UserId).ToList();
            userId = withPin.Count == 0 ? null : await Pins.FindByPinAsync(Org, withPin, pin!, ct);
        }

        if (userId is null)
        {
            // POS-CSH-07 — 5 consecutive failures on this terminal lock it for 5 minutes.
            terminal.PinFailedAttempts++;
            if (terminal.PinFailedAttempts >= MaxPinFailures)
            {
                terminal.PinFailedAttempts = 0;
                terminal.PinLockedUntil    = now + PinLockout;
            }
            await TrySaveAsync(ct);
            if (terminal.PinLockedUntil is { } locked && locked > now) throw Locked(terminal, locked);
            throw new BadRequestException("Invalid PIN.");
        }

        var user = (await Pins.GetUsersAsync(Org, [userId.Value], ct)).FirstOrDefault(u => u.UserId == userId.Value);
        if (user is null || !user.IsActive) throw new BadRequestException("The user is inactive.");
        if (!user.Permissions.Contains(PosPermissions.OrderCreate, StringComparer.OrdinalIgnoreCase))
            throw new BadRequestException("You do not have permission to sell on POS.");

        terminal.PinFailedAttempts = 0;
        terminal.PinLockedUntil    = null;
        await TrySaveAsync(ct);

        var tokens = await Pins.IssueTokensAsync(Org, user.UserId, ct);
        return new PosPinSignInModel
        {
            AccessToken = tokens.AccessToken, RefreshToken = tokens.RefreshToken,
            User = new PosSignedInUser
            {
                UserId = user.UserId, Name = user.Name, PosRole = staff.First(s => s.UserId == user.UserId).PosRole,
                Permissions = user.Permissions.ToList()
            }
        };
    }

    internal static BadRequestException Locked(PosTerminal terminal, DateTime until) =>
        new($"Terminal locked until {PosClock.ToLocal(terminal.Store, until):HH:mm}.");

    public async Task<PosCashierSessionModel> StartAsync(PosSessionStartRequest req, PosCaller caller, CancellationToken ct = default)
    {
        var localId = string.IsNullOrWhiteSpace(req.LocalId) ? null : req.LocalId.Trim();
        if (localId is not null && await _db.CashierSessions.FirstOrDefaultAsync(s => s.LocalId == localId, ct) is { } replay)
            return await ModelAsync(replay, ct);

        var shift = await _db.Shifts.FirstOrDefaultAsync(s => s.Uuid == req.ShiftUuid, ct) ?? throw new NotFoundException("Shift", req.ShiftUuid);
        if (shift.Status != PosShiftStatus.Open) throw new BadRequestException("The shift is not open.");
        if (!await _db.StoreStaff.AnyAsync(s => s.StoreId == shift.StoreId && s.UserId == caller.UserId && s.IsActive, ct))
            throw new BadRequestException("You are not staff of this store.");

        var active = await _db.CashierSessions.Where(s => s.TerminalId == shift.TerminalId && s.IsActive).ToListAsync(ct);
        // Resume: the same user's active session on this shift.
        if (active.FirstOrDefault(s => s.UserId == caller.UserId && s.ShiftId == shift.Id) is { } own)
            return await ModelAsync(own, ct);

        // POS-CSH-04 — one active session per terminal: the previous cashier's ends.
        var now = _read.UtcNow;
        foreach (var s in active)
        {
            s.IsActive  = false;
            s.EndedAt   = now;
            s.EndReason = s.UserId == caller.UserId ? PosSessionEndReason.Ended : PosSessionEndReason.Switched;
        }
        var session = new PosCashierSession
        {
            OrganizationId = Org, ShiftId = shift.Id, TerminalId = shift.TerminalId, UserId = caller.UserId, StartedAt = now, LocalId = localId
        };
        _db.CashierSessions.Add(session);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (localId is not null)
        {
            // POSB — a concurrent start with the same localId won on the unique (org, LocalId) index: answer with it.
            _db.ChangeTracker.Clear();
            var winner = await _db.CashierSessions.AsNoTracking().FirstOrDefaultAsync(s => s.LocalId == localId, ct);
            if (winner is null) throw;
            return await ModelAsync(winner, ct);
        }
        return await ModelAsync(session, ct);
    }

    public async Task<PosCashierSessionModel> EndAsync(Guid sessionUuid, PosCaller caller, CancellationToken ct = default)
    {
        var session = await _db.CashierSessions.FirstOrDefaultAsync(s => s.Uuid == sessionUuid, ct)
            ?? throw new NotFoundException("Session", sessionUuid);
        if (session.UserId != caller.UserId && !caller.Has(PosPermissions.ShiftClose))
            throw new ForbiddenException("You can only end your own session.");
        if (session.IsActive)
        {
            session.IsActive  = false;
            session.EndedAt   = _read.UtcNow;
            session.EndReason = PosSessionEndReason.Ended;
            await _db.SaveChangesAsync(ct);
        }
        return await ModelAsync(session, ct);
    }

    private async Task<PosCashierSessionModel> ModelAsync(PosCashierSession s, CancellationToken ct) => (await _read.SessionsAsync([s], ct))[0];

    /// <summary>Lockout bookkeeping never fails a sign-in on a concurrent edit of the terminal.</summary>
    private async Task TrySaveAsync(CancellationToken ct)
    {
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { _db.ChangeTracker.Clear(); }
    }
}
