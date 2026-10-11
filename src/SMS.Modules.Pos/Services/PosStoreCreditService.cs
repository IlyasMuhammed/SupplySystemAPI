using Microsoft.EntityFrameworkCore;
using SMS.Modules.Pos.Data;
using SMS.Modules.Pos.Domain;
using SMS.Modules.Pos.Hubs;
using SMS.Modules.Pos.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;
using Calc = SMS.Modules.Pos.Services.PosTotalsCalculator;

namespace SMS.Modules.Pos.Services;

/// <summary>
/// A39 API-CONTRACT §4 / D-2 — store credit notes (CN-…): standalone issue, reads, cancel. Issue as a refund and redemption
/// as a sale tender happen inside the order/return flows through the static helpers below (same SaveChanges as the order,
/// RowVersion on the note against double spend). One transaction row per balance change.
/// </summary>
public interface IPosStoreCreditService
{
    Task<PosStoreCreditModel> IssueAsync(PosStoreCreditIssueRequest req, PosCaller caller, CancellationToken ct = default);
    Task<PaginatedResponse<PosStoreCreditModel>> ListAsync(PosStoreCreditListFilter filter, CancellationToken ct = default);
    Task<PosStoreCreditDetailModel> GetAsync(Guid uuid, CancellationToken ct = default);
    Task<PosStoreCreditModel> LookupAsync(string? number, CancellationToken ct = default);
    Task<IReadOnlyList<PosStoreCreditModel>> ListForCustomerAsync(Guid customerUuid, string? status, CancellationToken ct = default);
    Task<PosStoreCreditModel> CancelAsync(Guid uuid, string? note, PosCaller caller, CancellationToken ct = default);
    Task<IReadOnlyList<PosStoreCreditTxnModel>> TransactionsAsync(Guid uuid, CancellationToken ct = default);
}

internal sealed class PosStoreCreditService : IPosStoreCreditService
{
    internal const string ExpiredMessage = "This credit note has expired.";
    internal const string OfflineMessage = "Credit note redemption requires internet connectivity.";
    internal const string OtherCustomerMessage = "This credit note belongs to another customer.";

    private readonly PosDbContext         _db;
    private readonly ITenantContext       _tenant;
    private readonly PosReadModel         _read;
    private readonly IPosNumberSequence   _numbers;
    private readonly IPosHubNotifier      _hub;
    private readonly IPosCustomerSource?  _customers;
    private readonly IModuleGate?         _gate;

    public PosStoreCreditService(PosDbContext db, ITenantContext tenant, PosReadModel read, IPosNumberSequence numbers, IPosHubNotifier hub,
        IPosCustomerSource? customers = null, IModuleGate? gate = null)
    {
        _db = db; _tenant = tenant; _read = read; _numbers = numbers; _hub = hub; _customers = customers; _gate = gate;
    }

    private Guid Org => _tenant.OrganizationId;

    // ══ Standalone issue / cancel ══════════════════════════════════════════════════════════════════════════════════

    public async Task<PosStoreCreditModel> IssueAsync(PosStoreCreditIssueRequest req, PosCaller caller, CancellationToken ct = default)
    {
        if (!await _gate.IsOnAsync(Org, PosFeatureCodes.CreditNotes, ct)) throw new BadRequestException("Store credit is not enabled for your organization.");
        var localId = string.IsNullOrWhiteSpace(req.LocalId) ? null : req.LocalId.Trim();
        if (localId is { Length: > 64 }) throw new BadRequestException("A localId has at most 64 characters.");
        if (localId is not null && await _db.StoreCreditNotes.AsNoTracking().FirstOrDefaultAsync(n => n.LocalId == localId, ct) is { } replay)
            return (await ModelsAsync([replay], ct))[0];

        var store = await _db.Stores.FirstOrDefaultAsync(s => s.Uuid == req.StoreUuid, ct) ?? throw new NotFoundException("Store", req.StoreUuid);
        if (req.Amount <= 0m || Calc.Round(req.Amount) != req.Amount) throw new BadRequestException("The amount must be above zero with at most 2 decimals.");
        var now = _read.UtcNow;
        if (req.ExpiresAt is { } exp && exp <= now) throw new BadRequestException("The expiry date must be in the future.");
        string? customerName = null;
        if (req.CustomerUuid is { } cu && cu != Guid.Empty)
        {
            var customer = await (_customers ?? throw new BadRequestException("Customers are not available on this server.")).GetAsync(Org, cu, ct)
                ?? throw new BadRequestException("Customer not found.");
            customerName = customer.Name;
        }
        var expires = req.ExpiresAt;
        if (expires is null)
        {
            var (policy, _) = await PosReturnPolicyService.ResolveAsync(_db, Org, store.Id, now, ct);
            if (policy?.CreditNoteExpiryDays is { } days) expires = now.AddDays(days);
        }
        var note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
        if (note is { Length: > 500 }) note = note[..500];

        var number = await _numbers.NextNumberAsync(store, PosNumberPrefix.CreditNote, now, ct);
        var cn = NewNote(Org, number, req.CustomerUuid is { } c && c != Guid.Empty ? c : null, customerName, store.Id, null, req.Amount, expires,
            caller.UserId, now, note);
        cn.LocalId = localId;
        _db.StoreCreditNotes.Add(cn);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (localId is not null)
        {
            _db.ChangeTracker.Clear();
            var winner = await _db.StoreCreditNotes.AsNoTracking().FirstOrDefaultAsync(n => n.LocalId == localId, ct);
            if (winner is null) throw;
            return (await ModelsAsync([winner], ct))[0];
        }
        await IssuedEventAsync(_hub, Org, store.Uuid, cn);
        return (await ModelsAsync([cn], ct))[0];
    }

    public async Task<PosStoreCreditModel> CancelAsync(Guid uuid, string? note, PosCaller caller, CancellationToken ct = default)
    {
        var cn = await _db.StoreCreditNotes.FirstOrDefaultAsync(n => n.Uuid == uuid, ct) ?? throw new NotFoundException("Credit note", uuid);
        if (cn.Status != PosStoreCreditStatus.Active) throw new BadRequestException("Only an active credit note can be cancelled.");
        var now = _read.UtcNow;
        var text = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (text is { Length: > 500 }) text = text[..500];
        cn.Transactions.Add(Txn(cn, PosStoreCreditTxnType.Cancel, -cn.RemainingBalance, 0m, null, cn.IssuedStoreId, caller.UserId, now, text));
        cn.RemainingBalance = 0m;
        cn.Status      = PosStoreCreditStatus.Cancelled;
        cn.CancelledBy = caller.UserId;
        cn.CancelledAt = now;
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("The credit note was used at the same time. Reload and try again."); }
        return (await ModelsAsync([cn], ct))[0];
    }

    // ══ Reads ══════════════════════════════════════════════════════════════════════════════════════════════════════

    public async Task<PaginatedResponse<PosStoreCreditModel>> ListAsync(PosStoreCreditListFilter f, CancellationToken ct = default)
    {
        var q = _db.StoreCreditNotes.AsNoTracking();
        if (Codes(f.Status) is { Count: > 0 } statuses) q = q.Where(n => statuses.Contains(n.Status));
        if (f.CustomerUuid is { } customer) q = q.Where(n => n.CustomerUuid == customer);
        if (f.StoreUuid is { } storeUuid)
        {
            var storeId = await _db.Stores.AsNoTracking().Where(s => s.Uuid == storeUuid).Select(s => (int?)s.Id).FirstOrDefaultAsync(ct) ?? -1;
            q = q.Where(n => n.IssuedStoreId == storeId);
        }
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var s = f.Search.Trim();
            q = q.Where(n => n.Number.Contains(s) || (n.OfflineNumber != null && n.OfflineNumber.Contains(s))
                             || (n.CustomerName != null && n.CustomerName.Contains(s)));
        }
        var (page, size) = PosStoreService.Paging(f.Page, f.PageSize);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(n => n.IssuedAt).ThenByDescending(n => n.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return PosStoreService.Page(await ModelsAsync(rows, ct), total, page, size);
    }

    public async Task<PosStoreCreditDetailModel> GetAsync(Guid uuid, CancellationToken ct = default)
    {
        var cn = await _db.StoreCreditNotes.AsNoTracking().FirstOrDefaultAsync(n => n.Uuid == uuid, ct) ?? throw new NotFoundException("Credit note", uuid);
        var m = (await ModelsAsync([cn], ct))[0];
        var detail = new PosStoreCreditDetailModel();
        foreach (var p in typeof(PosStoreCreditModel).GetProperties()) p.SetValue(detail, p.GetValue(m));
        detail.Transactions = await TxnModelsAsync(cn.Id, ct);
        return detail;
    }

    public async Task<PosStoreCreditModel> LookupAsync(string? number, CancellationToken ct = default)
    {
        var n = number?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(n)) throw new BadRequestException("Enter a credit note number.");
        var cn = await _db.StoreCreditNotes.AsNoTracking().FirstOrDefaultAsync(x => x.Number == n || x.OfflineNumber == n, ct)
            ?? throw new NotFoundException($"Credit note {n} was not found.");
        return (await ModelsAsync([cn], ct))[0];
    }

    public async Task<IReadOnlyList<PosStoreCreditModel>> ListForCustomerAsync(Guid customerUuid, string? status, CancellationToken ct = default)
    {
        var q = _db.StoreCreditNotes.AsNoTracking().Where(n => n.CustomerUuid == customerUuid);
        if (Codes(status) is { Count: > 0 } statuses) q = q.Where(n => statuses.Contains(n.Status));
        return await ModelsAsync(await q.OrderByDescending(n => n.IssuedAt).Take(200).ToListAsync(ct), ct);
    }

    public async Task<IReadOnlyList<PosStoreCreditTxnModel>> TransactionsAsync(Guid uuid, CancellationToken ct = default)
    {
        var id = await _db.StoreCreditNotes.AsNoTracking().Where(n => n.Uuid == uuid).Select(n => (int?)n.Id).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Credit note", uuid);
        return await TxnModelsAsync(id, ct);
    }

    private async Task<List<PosStoreCreditTxnModel>> TxnModelsAsync(int creditNoteId, CancellationToken ct)
    {
        var rows = await _db.StoreCreditTransactions.AsNoTracking().Where(t => t.CreditNoteId == creditNoteId).OrderBy(t => t.Id)
            .Select(t => new { t.Uuid, t.Type, t.Amount, t.BalanceAfter, OrderUuid = (Guid?)t.Order!.Uuid, OrderNumber = t.Order!.OrderNumber,
                t.StoreId, t.PerformedBy, t.PerformedAt, t.Note })
            .ToListAsync(ct);
        var stores = await _db.Stores.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        await _read.UsersAsync(rows.Select(r => r.PerformedBy), ct);
        return rows.Select(r => new PosStoreCreditTxnModel
        {
            Uuid = r.Uuid, Type = r.Type, Amount = r.Amount, BalanceAfter = r.BalanceAfter, OrderUuid = r.OrderUuid, OrderNumber = r.OrderNumber,
            StoreName = stores.GetValueOrDefault(r.StoreId) ?? string.Empty, PerformedByName = _read.NameOf(r.PerformedBy),
            PerformedAt = r.PerformedAt, Note = r.Note
        }).ToList();
    }

    internal async Task<List<PosStoreCreditModel>> ModelsAsync(IReadOnlyList<PosStoreCreditNote> notes, CancellationToken ct = default) =>
        await ModelsAsync(_db, _read, notes, ct);

    internal static async Task<List<PosStoreCreditModel>> ModelsAsync(PosDbContext db, PosReadModel read, IReadOnlyList<PosStoreCreditNote> notes,
        CancellationToken ct = default)
    {
        var storeIds = notes.Select(n => n.IssuedStoreId).Concat(notes.Where(n => n.LastUsedStoreId != null).Select(n => n.LastUsedStoreId!.Value)).Distinct().ToList();
        var stores = await db.Stores.AsNoTracking().Where(s => storeIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var orderIds = notes.Where(n => n.ReturnOrderId != null).Select(n => n.ReturnOrderId!.Value).Distinct().ToList();
        var orders = await db.Orders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => new { o.Uuid, o.OrderNumber }, ct);
        await read.UsersAsync(notes.Select(n => n.IssuedBy), ct);
        var currency = await read.CurrencyCodeAsync(ct);
        // Redeemed = −Σ REDEEM − Σ ADJUST (a void's restore gives a redemption back); expiry/cancel are not redemptions.
        var noteIds = notes.Select(n => n.Id).ToList();
        var redeemed = await db.StoreCreditTransactions.AsNoTracking()
            .Where(t => noteIds.Contains(t.CreditNoteId) && (t.Type == PosStoreCreditTxnType.Redeem || t.Type == PosStoreCreditTxnType.Adjust))
            .GroupBy(t => t.CreditNoteId).Select(g => new { g.Key, Sum = g.Sum(t => t.Amount) }).ToDictionaryAsync(x => x.Key, x => -x.Sum, ct);
        var now = read.UtcNow;
        return notes.Select(n =>
        {
            var order = n.ReturnOrderId is { } oid ? orders.GetValueOrDefault(oid) : null;
            return new PosStoreCreditModel
            {
                Uuid = n.Uuid, Number = n.Number, OfflineNumber = n.OfflineNumber, CustomerUuid = n.CustomerUuid, CustomerName = n.CustomerName,
                ReturnOrderUuid = order?.Uuid, ReturnOrderNumber = order?.OrderNumber, OriginalAmount = n.OriginalAmount,
                RemainingBalance = n.RemainingBalance, RedeemedAmount = redeemed.GetValueOrDefault(n.Id), CurrencyCode = currency,
                // An ACTIVE note past its expiry reads EXPIRED before the nightly job books it.
                Status = n.Status == PosStoreCreditStatus.Active && n.IsExpiredAt(now) ? PosStoreCreditStatus.Expired : n.Status,
                IssuedAtStoreName = stores.GetValueOrDefault(n.IssuedStoreId) ?? string.Empty, IssuedByName = read.NameOf(n.IssuedBy),
                IssuedAt = n.IssuedAt, ExpiresAt = n.ExpiresAt, LastUsedAt = n.LastUsedAt,
                LastUsedAtStoreName = n.LastUsedStoreId is { } ls ? stores.GetValueOrDefault(ls) : null, Note = n.Note, ModifiedAt = n.ModifiedAt
            };
        }).ToList();
    }

    private static List<string>? Codes(string? csv) => string.IsNullOrWhiteSpace(csv) ? null
        : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToUpperInvariant()).ToList();

    // ══ Helpers for the order flows ════════════════════════════════════════════════════════════════════════════════

    internal static PosStoreCreditNote NewNote(Guid org, string number, Guid? customerUuid, string? customerName, int storeId, int? shiftId,
        decimal amount, DateTime? expiresAt, int userId, DateTime now, string? note)
    {
        var cn = new PosStoreCreditNote
        {
            OrganizationId = org, Number = number, CustomerUuid = customerUuid, CustomerName = customerName, IssuedStoreId = storeId,
            IssuedShiftId = shiftId, OriginalAmount = amount, RemainingBalance = amount, Status = PosStoreCreditStatus.Active, IssuedBy = userId,
            IssuedAt = now, ExpiresAt = expiresAt, Note = note, ModifiedAt = now
        };
        cn.Transactions.Add(Txn(cn, PosStoreCreditTxnType.Issue, amount, amount, null, storeId, userId, now, null));
        return cn;
    }

    internal static PosStoreCreditTransaction Txn(PosStoreCreditNote cn, string type, decimal amount, decimal balanceAfter, PosOrder? order,
        int storeId, int userId, DateTime now, string? note) => new()
    {
        OrganizationId = cn.OrganizationId, CreditNote = cn, Type = type, Amount = amount, BalanceAfter = balanceAfter, Order = order,
        StoreId = storeId, PerformedBy = userId, PerformedAt = now, Note = note
    };

    /// <summary>
    /// D-2 / POS-CN-04 — validates every STORE_CREDIT tender of a sale (or exchange) and returns the tracked notes by uuid:
    /// online only, feature + POS_CREDIT_NOTE_REDEEM, an ACTIVE unexpired note, bound to the order's customer when it has
    /// one, and Σ per note ≤ its balance. (≤ the order's remaining balance and no change: the tender check.)
    /// </summary>
    internal static async Task<Dictionary<Guid, PosStoreCreditNote>> PrepareRedemptionsAsync(
        PosDbContext db, IModuleGate? gate, Guid org, IReadOnlyList<PosSyncPaymentRequest> payments, Guid customerUuid, bool offline,
        PosCaller caller, DateTime now, CancellationToken ct)
    {
        var tenders = payments.Where(p => p.Method == PosPaymentMethod.StoreCredit).ToList();
        if (tenders.Count == 0) return [];
        // POS-CN-04 — never offline: the balance can only be checked (and spent once) against the server.
        if (offline) throw new BadRequestException(OfflineMessage);
        if (!await gate.IsOnAsync(org, PosFeatureCodes.CreditNotes, ct)) throw new BadRequestException("Store credit is not enabled for your organization.");
        if (!caller.Has(PosPermissions.CreditNoteRedeem)) throw new ForbiddenException("You do not have permission to redeem store credit.");
        if (tenders.Any(t => t.CreditNoteUuid is null || t.CreditNoteUuid == Guid.Empty))
            throw new BadRequestException("A store credit payment needs the credit note.");
        var ids = tenders.Select(t => t.CreditNoteUuid!.Value).Distinct().ToList();
        var notes = await db.StoreCreditNotes.Where(n => ids.Contains(n.Uuid)).ToDictionaryAsync(n => n.Uuid, ct);
        foreach (var group in tenders.GroupBy(t => t.CreditNoteUuid!.Value))
        {
            var cn = notes.GetValueOrDefault(group.Key) ?? throw new BadRequestException("Credit note not found.");
            if (cn.Status == PosStoreCreditStatus.Expired || (cn.Status == PosStoreCreditStatus.Active && cn.IsExpiredAt(now)))
                throw new BadRequestException(ExpiredMessage);
            if (cn.Status != PosStoreCreditStatus.Active)
                throw new BadRequestException(cn.Status == PosStoreCreditStatus.Cancelled ? "This credit note was cancelled." : "This credit note has no balance left.");
            if (cn.CustomerUuid is { } owner && owner != customerUuid) throw new BadRequestException(OtherCustomerMessage);
            var amount = group.Sum(t => t.Amount);
            if (amount > cn.RemainingBalance)
                throw new BadRequestException($"The credit note {cn.Number} has only {cn.RemainingBalance:0.00} left.");
        }
        return notes;
    }

    /// <summary>Spends <paramref name="amount"/> of a tracked note for <paramref name="order"/> (REDEEM transaction, status, last use).</summary>
    internal static void Redeem(PosStoreCreditNote cn, decimal amount, PosOrder order, int storeId, int userId, DateTime now)
    {
        cn.RemainingBalance -= amount;
        cn.LastUsedAt      = now;
        cn.LastUsedStoreId = storeId;
        if (cn.RemainingBalance <= 0m) cn.Status = PosStoreCreditStatus.FullyRedeemed;
        cn.Transactions.Add(Txn(cn, PosStoreCreditTxnType.Redeem, -amount, cn.RemainingBalance, order, storeId, userId, now, null));
    }

    /// <summary>XFIN request — a voided sale's store credit tender goes back on the note (ADJUST +amount; FULLY_REDEEMED → ACTIVE).</summary>
    internal static void Restore(PosStoreCreditNote cn, decimal amount, PosOrder order, int userId, DateTime now)
    {
        cn.RemainingBalance += amount;
        if (cn.Status == PosStoreCreditStatus.FullyRedeemed) cn.Status = PosStoreCreditStatus.Active;
        cn.Transactions.Add(Txn(cn, PosStoreCreditTxnType.Adjust, amount, cn.RemainingBalance, order, order.StoreId, userId, now,
            $"Order {order.OrderNumber} voided."));
    }

    internal static Task IssuedEventAsync(IPosHubNotifier hub, Guid org, Guid storeUuid, PosStoreCreditNote cn) =>
        hub.StoreEventAsync(org, storeUuid, PosHubEvents.CreditNoteIssued,
            new { creditNoteUuid = cn.Uuid, number = cn.Number, customerUuid = cn.CustomerUuid, amount = cn.OriginalAmount, expiresAt = cn.ExpiresAt });
}
