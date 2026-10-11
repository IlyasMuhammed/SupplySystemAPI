using Microsoft.EntityFrameworkCore;
using SMS.Modules.Pos.Data;
using SMS.Modules.Pos.Domain;
using SMS.Modules.Pos.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Pos.Services;

/// <summary>A38 API-CONTRACT §4 — shift lifecycle (D-5) and cash movements.</summary>
public interface IPosShiftService
{
    Task<PosShiftModel> OpenAsync(Guid terminalUuid, PosOpenShiftRequest req, PosCaller caller, CancellationToken ct = default);
    Task<PosShiftModel?> GetCurrentAsync(Guid terminalUuid, CancellationToken ct = default);
    Task<PosShiftDetailModel> GetAsync(Guid uuid, PosCaller caller, CancellationToken ct = default);
    Task<PosShiftModel> StartCloseAsync(Guid uuid, PosCaller caller, CancellationToken ct = default);
    Task<PosShiftModel> ResumeAsync(Guid uuid, PosCaller caller, CancellationToken ct = default);
    Task<PosShiftModel> CloseAsync(Guid uuid, PosCloseShiftRequest req, PosCaller caller, CancellationToken ct = default);
    Task<PosShiftModel> ReconcileAsync(Guid uuid, string? note, PosCaller caller, CancellationToken ct = default);
    Task<PosShiftModel> AddNoteAsync(Guid uuid, string? note, PosCaller caller, CancellationToken ct = default);
    Task<PaginatedResponse<PosShiftModel>> ListAsync(PosShiftListFilter filter, CancellationToken ct = default);
    Task<PosShiftSummaryModel> GetSummaryAsync(Guid uuid, PosCaller caller, CancellationToken ct = default);
    Task<PosCashMovementModel> AddCashMovementAsync(Guid shiftUuid, PosCashMovementRequest req, PosCaller caller, CancellationToken ct = default);
    Task<IReadOnlyList<PosCashMovementModel>> ListCashMovementsAsync(Guid shiftUuid, PosCaller caller, CancellationToken ct = default);
}

internal sealed class PosShiftService : IPosShiftService
{
    internal const int MaxNotesLength = 4000;
    internal static readonly IReadOnlyList<string> SummaryMethods =
    [
        PosPaymentMethod.Cash, PosPaymentMethod.Card, PosPaymentMethod.Mobile, PosPaymentMethod.BankTransfer,
        PosPaymentMethod.Cheque, PosPaymentMethod.OnAccount, PosPaymentMethod.StoreCredit, PosPaymentMethod.Exchange // A39 §7
    ];

    private readonly PosDbContext       _db;
    private readonly ITenantContext     _tenant;
    private readonly PosReadModel       _read;
    private readonly IPosNumberSequence _numbers;
    private readonly IPosShiftTotals    _totals;

    public PosShiftService(PosDbContext db, ITenantContext tenant, PosReadModel read, IPosNumberSequence numbers, IPosShiftTotals totals)
    {
        _db      = db;
        _tenant  = tenant;
        _read    = read;
        _numbers = numbers;
        _totals  = totals;
    }

    private Guid Org => _tenant.OrganizationId;

    public async Task<PosShiftModel> OpenAsync(Guid terminalUuid, PosOpenShiftRequest req, PosCaller caller, CancellationToken ct = default)
    {
        var terminal = await _db.Terminals.Include(t => t.Store).FirstOrDefaultAsync(t => t.Uuid == terminalUuid, ct)
            ?? throw new NotFoundException("Terminal", terminalUuid);
        if (!terminal.IsActive || !terminal.Store.IsActive) throw new BadRequestException("The terminal is inactive.");
        if (!caller.Has(PosPermissions.StoreManage) && !await IsStaffAsync(terminal.StoreId, caller.UserId, ct))
            throw new BadRequestException("You are not staff of this store.");
        if (req.OpeningFloat < 0) throw new BadRequestException("Opening float cannot be negative.");
        if (req.VarianceThreshold < 0) throw new BadRequestException("Variance threshold cannot be negative.");
        if (await _db.Shifts.AnyAsync(s => s.TerminalId == terminal.Id && PosShiftStatus.Active.Contains(s.Status), ct))
            throw new BadRequestException("Terminal already has an active shift.");

        var now  = _read.UtcNow;
        var date = PosClock.BusinessDate(terminal.Store, now);
        var shift = new PosShift
        {
            OrganizationId = Org, StoreId = terminal.StoreId, Store = terminal.Store, TerminalId = terminal.Id, Terminal = terminal,
            BusinessDate = date, Status = PosShiftStatus.Open, OpenedBy = caller.UserId, OpenedAt = now,
            OpeningFloat = req.OpeningFloat, VarianceThreshold = req.VarianceThreshold ?? terminal.Store.VarianceThreshold,
            ShiftNumber = PosNumberSequenceService.Format(PosNumberPrefix.Shift, terminal.Store.Code, date,
                await _numbers.NextValueAsync(terminal.Store, PosNumberPrefix.Shift, date, ct))
        };
        PosShiftTotals.Compute(shift, [], [], []);
        _db.Shifts.Add(shift);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (_db.Shifts.AsNoTracking()
                   .Any(s => s.TerminalId == terminal.Id && s.Id != shift.Id && PosShiftStatus.Active.Contains(s.Status)))
        {
            // UX_shifts_TerminalId_Active — a concurrent open won.
            throw new BadRequestException("Terminal already has an active shift.");
        }
        return await _read.ShiftAsync(shift, ct);
    }

    public async Task<PosShiftModel?> GetCurrentAsync(Guid terminalUuid, CancellationToken ct = default)
    {
        var terminal = await _db.Terminals.FirstOrDefaultAsync(t => t.Uuid == terminalUuid, ct) ?? throw new NotFoundException("Terminal", terminalUuid);
        var shift = await Shifts().FirstOrDefaultAsync(s => s.TerminalId == terminal.Id && PosShiftStatus.Active.Contains(s.Status), ct);
        return shift is null ? null : await _read.ShiftAsync(shift, ct);
    }

    public async Task<PosShiftDetailModel> GetAsync(Guid uuid, PosCaller caller, CancellationToken ct = default)
    {
        var shift = await ReadableAsync(uuid, caller, ct);
        var sessions  = await _db.CashierSessions.AsNoTracking().Where(s => s.ShiftId == shift.Id).OrderBy(s => s.StartedAt).ToListAsync(ct);
        var movements = await _db.CashMovements.AsNoTracking().Where(m => m.ShiftId == shift.Id).OrderBy(m => m.PerformedAt).ToListAsync(ct);
        await _read.UsersAsync([shift.OpenedBy, shift.ClosedBy ?? 0, shift.ReconciledBy ?? 0], ct);
        var detail = _read.Fill(new PosShiftDetailModel(), shift);
        detail.Sessions      = await _read.SessionsAsync(sessions, ct);
        detail.CashMovements = await _read.MovementsAsync(movements, ct);
        return detail;
    }

    public async Task<PosShiftModel> StartCloseAsync(Guid uuid, PosCaller caller, CancellationToken ct = default)
    {
        var shift = await ShiftAsync(uuid, ct);
        if (shift.Status != PosShiftStatus.Open) throw new BadRequestException($"Only an open shift can start closing (it is {shift.Status}).");
        shift.Status           = PosShiftStatus.Closing;
        shift.ClosingStartedAt = _read.UtcNow;
        shift.ClosingStartedBy = caller.UserId;
        await _totals.ApplyAsync(shift, ct);
        await SaveAsync(ct);
        return await _read.ShiftAsync(shift, ct);
    }

    public async Task<PosShiftModel> ResumeAsync(Guid uuid, PosCaller caller, CancellationToken ct = default)
    {
        var shift = await ShiftAsync(uuid, ct);
        if (shift.Status != PosShiftStatus.Closing) throw new BadRequestException($"Only a closing shift can be resumed (it is {shift.Status}).");
        shift.Status           = PosShiftStatus.Open;
        shift.ClosingStartedAt = null;
        shift.ClosingStartedBy = null;
        await SaveAsync(ct);
        return await _read.ShiftAsync(shift, ct);
    }

    /// <summary>Counted drawer → CLOSED, then RECONCILED when |variance| ≤ threshold, else DISPUTED (D-5). Ends the shift's sessions.</summary>
    public async Task<PosShiftModel> CloseAsync(Guid uuid, PosCloseShiftRequest req, PosCaller caller, CancellationToken ct = default)
    {
        var shift = await ShiftAsync(uuid, ct);
        if (!PosShiftStatus.IsActive(shift.Status)) throw new BadRequestException($"The shift is already {shift.Status}.");
        if (req.ActualCashClosing is not { } actual) throw new BadRequestException("Counted cash is required.");
        if (actual < 0) throw new BadRequestException("Counted cash cannot be negative.");

        var now = _read.UtcNow;
        if (shift.Status == PosShiftStatus.Open) { shift.ClosingStartedAt = now; shift.ClosingStartedBy = caller.UserId; }
        shift.ActualCashClosing = actual;
        await _totals.ApplyAsync(shift, ct); // sets CashVariance = actual − expected
        shift.VarianceNote = string.IsNullOrWhiteSpace(req.VarianceNote) ? null : req.VarianceNote.Trim();
        shift.ClosedAt     = now;
        shift.ClosedBy     = caller.UserId;
        if (Math.Abs(shift.CashVariance ?? 0m) <= shift.VarianceThreshold)
        {
            shift.Status       = PosShiftStatus.Reconciled;
            shift.ReconciledAt = now;
            shift.ReconciledBy = caller.UserId;
        }
        else shift.Status = PosShiftStatus.Disputed;

        foreach (var s in await _db.CashierSessions.Where(s => s.ShiftId == shift.Id && s.IsActive).ToListAsync(ct))
        {
            s.IsActive = false; s.EndedAt = now; s.EndReason = PosSessionEndReason.ShiftClosed;
        }
        await SaveAsync(ct);
        return await _read.ShiftAsync(shift, ct);
    }

    public async Task<PosShiftModel> ReconcileAsync(Guid uuid, string? note, PosCaller caller, CancellationToken ct = default)
    {
        var shift = await ShiftAsync(uuid, ct);
        if (shift.Status != PosShiftStatus.Disputed) throw new BadRequestException("Only a disputed shift can be reconciled.");
        if (string.IsNullOrWhiteSpace(note)) throw new BadRequestException("A reconciliation note is required.");
        await AppendNoteAsync(shift, note, caller.UserId, ct);
        shift.Status       = PosShiftStatus.Reconciled;
        shift.ReconciledAt = _read.UtcNow;
        shift.ReconciledBy = caller.UserId;
        await SaveAsync(ct);
        return await _read.ShiftAsync(shift, ct);
    }

    /// <summary>POS-SHF-10 — a closed shift is immutable except for appended notes.</summary>
    public async Task<PosShiftModel> AddNoteAsync(Guid uuid, string? note, PosCaller caller, CancellationToken ct = default)
    {
        var shift = await ShiftAsync(uuid, ct);
        if (shift.Status is not (PosShiftStatus.Reconciled or PosShiftStatus.Disputed))
            throw new BadRequestException("Notes can only be added to a reconciled or disputed shift.");
        if (string.IsNullOrWhiteSpace(note)) throw new BadRequestException("Note is required.");
        await AppendNoteAsync(shift, note, caller.UserId, ct);
        await SaveAsync(ct);
        return await _read.ShiftAsync(shift, ct);
    }

    private async Task AppendNoteAsync(PosShift shift, string note, int userId, CancellationToken ct)
    {
        await _read.UsersAsync([userId], ct);
        var line  = $"[{_read.UtcNow:yyyy-MM-dd HH:mm} UTC] {_read.NameOf(userId)}: {note.Trim()}";
        var notes = string.IsNullOrEmpty(shift.ReconcileNotes) ? line : shift.ReconcileNotes + "\n" + line;
        if (notes.Length > MaxNotesLength) throw new BadRequestException("The shift's notes are full.");
        shift.ReconcileNotes = notes;
    }

    public async Task<PaginatedResponse<PosShiftModel>> ListAsync(PosShiftListFilter f, CancellationToken ct = default)
    {
        var q = Shifts().AsNoTracking();
        if (f.StoreUuid is { } store)       q = q.Where(s => s.Store.Uuid == store);
        if (f.TerminalUuid is { } terminal) q = q.Where(s => s.Terminal.Uuid == terminal);
        if (!string.IsNullOrWhiteSpace(f.Status))
        {
            var statuses = f.Status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToUpperInvariant()).ToList();
            q = q.Where(s => statuses.Contains(s.Status));
        }
        if (f.From is { } from) q = q.Where(s => s.OpenedAt >= from);
        if (f.To is { } to)     q = q.Where(s => s.OpenedAt < (to.TimeOfDay == TimeSpan.Zero ? to.AddDays(1) : to));
        var (page, size) = PosStoreService.Paging(f.Page, f.PageSize);
        var total  = await q.CountAsync(ct);
        var shifts = await q.OrderByDescending(s => s.OpenedAt).ThenByDescending(s => s.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return PosStoreService.Page(await _read.ShiftsAsync(shifts, ct), total, page, size);
    }

    public async Task<PosShiftSummaryModel> GetSummaryAsync(Guid uuid, PosCaller caller, CancellationToken ct = default)
    {
        var shift = await ReadableAsync(uuid, caller, ct);
        var payments = await _db.Payments.AsNoTracking().Where(p => p.Order.ShiftId == shift.Id)
            .Select(p => new { p.Method, p.Amount, p.ChangeGiven, p.IsRefund, p.Order.OrderType, p.Order.Status }).ToListAsync(ct);
        var sessions = await _db.CashierSessions.AsNoTracking().Where(s => s.ShiftId == shift.Id).OrderBy(s => s.StartedAt).ToListAsync(ct);
        return new PosShiftSummaryModel
        {
            Shift = await _read.ShiftAsync(shift, ct),
            ByMethod = SummaryMethods.Select(m =>
            {
                var sales   = payments.Where(p => p.Method == m && !p.IsRefund && PosOrderType.Sells(p.OrderType) && p.Status != PosOrderStatus.Voided)
                                      .Sum(p => p.Amount - p.ChangeGiven);
                var refunds = payments.Where(p => p.Method == m && p.IsRefund).Sum(p => p.Amount);
                return new PosMethodTotalModel { Method = m, Sales = sales, Refunds = refunds, Net = sales - refunds };
            }).ToList(),
            ByCashier = await _read.SessionsAsync(sessions, ct)
        };
    }

    public async Task<PosCashMovementModel> AddCashMovementAsync(Guid shiftUuid, PosCashMovementRequest req, PosCaller caller, CancellationToken ct = default)
    {
        var localId = string.IsNullOrWhiteSpace(req.LocalId) ? null : req.LocalId.Trim();
        var shift   = await ShiftAsync(shiftUuid, ct);
        if (localId is not null && await _db.CashMovements.AsNoTracking().FirstOrDefaultAsync(m => m.LocalId == localId, ct) is { } replay)
        {
            if (replay.ShiftId != shift.Id) throw new ConflictException("This movement id was already used on another shift.");
            return (await _read.MovementsAsync([replay], ct))[0];
        }
        if (shift.Status != PosShiftStatus.Open) throw new BadRequestException("Cash can only be moved on an open shift.");
        var direction = req.Direction?.Trim().ToUpperInvariant();
        var reason    = req.Reason?.Trim().ToUpperInvariant();
        if (!PosCashDirection.IsValid(direction)) throw new BadRequestException("Direction must be IN or OUT.");
        if (req.Amount <= 0) throw new BadRequestException("Amount must be greater than zero.");
        if (reason is null || !PosCashReason.All.Contains(reason)) throw new BadRequestException("Unknown cash movement reason.");
        if (decimal.Round(req.Amount, 2) != req.Amount) throw new BadRequestException("Amount can have at most 2 decimals.");
        if (!caller.Has(PosPermissions.StoreManage) && !await IsStaffAsync(shift.StoreId, caller.UserId, ct))
            throw new BadRequestException("You are not staff of this store.");

        var movement = new PosCashMovement
        {
            OrganizationId = Org, ShiftId = shift.Id, Direction = direction!, Amount = req.Amount, Reason = reason,
            Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(), PerformedBy = caller.UserId, PerformedAt = _read.UtcNow,
            LocalId = localId
        };
        _db.CashMovements.Add(movement);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (localId is not null)
        {
            // A concurrent push of the same movement won (unique (org, LocalId)): answer with it.
            _db.ChangeTracker.Clear();
            var winner = await _db.CashMovements.AsNoTracking().FirstOrDefaultAsync(m => m.LocalId == localId, ct);
            if (winner is null) throw;
            return (await _read.MovementsAsync([winner], ct))[0];
        }
        await _totals.RecalculateAsync(shift.Id, ct);
        return (await _read.MovementsAsync([movement], ct))[0];
    }

    public async Task<IReadOnlyList<PosCashMovementModel>> ListCashMovementsAsync(Guid shiftUuid, PosCaller caller, CancellationToken ct = default)
    {
        var shift = await ReadableAsync(shiftUuid, caller, ct);
        var movements = await _db.CashMovements.AsNoTracking().Where(m => m.ShiftId == shift.Id).OrderBy(m => m.PerformedAt).ToListAsync(ct);
        return await _read.MovementsAsync(movements, ct);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private IQueryable<PosShift> Shifts() => _db.Shifts.Include(s => s.Store).Include(s => s.Terminal);

    private async Task<PosShift> ShiftAsync(Guid uuid, CancellationToken ct) =>
        await Shifts().FirstOrDefaultAsync(s => s.Uuid == uuid, ct) ?? throw new NotFoundException("Shift", uuid);

    /// <summary>POS_SHIFT_VIEW, or POS_ORDER_CREATE as active staff of the shift's store ("own terminal").</summary>
    private async Task<PosShift> ReadableAsync(Guid uuid, PosCaller caller, CancellationToken ct)
    {
        var shift = await ShiftAsync(uuid, ct);
        if (caller.Has(PosPermissions.ShiftView)) return shift;
        if (caller.Has(PosPermissions.OrderCreate) && await IsStaffAsync(shift.StoreId, caller.UserId, ct)) return shift;
        throw new ForbiddenException();
    }

    private Task<bool> IsStaffAsync(int storeId, int userId, CancellationToken ct) =>
        _db.StoreStaff.AnyAsync(s => s.StoreId == storeId && s.UserId == userId && s.IsActive, ct);

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("The shift was changed at the same time. Reload and try again."); }
    }
}
