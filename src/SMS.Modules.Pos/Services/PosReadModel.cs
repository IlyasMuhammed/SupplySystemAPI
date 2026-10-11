using Microsoft.EntityFrameworkCore;
using SMS.Modules.Pos.Data;
using SMS.Modules.Pos.Domain;
using SMS.Modules.Pos.Models;
using SMS.Shared.Common;

namespace SMS.Modules.Pos.Services;

/// <summary>
/// A38 — entity → contract DTO mapping shared by the POS services (stores, terminals, shifts, sessions, movements), with
/// per-scope caches for user names (through <see cref="IPosPinService.GetUsersAsync"/>), the store currency (org sale
/// base, A35) and tax codes (Finance, D-11). Every external seam is optional: without it the DTO falls back to stored data.
/// POS-B reuses it for order DTOs' store/terminal/shift parts.
/// </summary>
internal sealed class PosReadModel
{
    private readonly PosDbContext                  _db;
    private readonly ITenantContext                _tenant;
    private readonly IPosPinService?               _pins;
    private readonly ITaxCodeLookup?               _taxCodes;
    private readonly IOrganizationCurrencyService? _orgCurrency;
    private readonly IOrgCurrencyLookup?           _currencies;
    private readonly TimeProvider                  _clock;
    private readonly Dictionary<int, PosUserInfo?> _users = [];
    private readonly Dictionary<Guid, TaxCodeInfo?> _tax = [];
    private string? _currencyCode;
    private bool _currencyResolved;

    public PosReadModel(
        PosDbContext db, ITenantContext tenant, IPosPinService? pins = null, ITaxCodeLookup? taxCodes = null,
        IOrganizationCurrencyService? orgCurrency = null, IOrgCurrencyLookup? currencies = null, TimeProvider? clock = null)
    {
        _db          = db;
        _tenant      = tenant;
        _pins        = pins;
        _taxCodes    = taxCodes;
        _orgCurrency = orgCurrency;
        _currencies  = currencies;
        _clock       = clock ?? TimeProvider.System;
    }

    public DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    // ── users ──

    public async Task<IReadOnlyDictionary<int, PosUserInfo?>> UsersAsync(IEnumerable<int> userIds, CancellationToken ct = default)
    {
        var missing = userIds.Where(id => id > 0 && !_users.ContainsKey(id)).Distinct().ToList();
        if (missing.Count > 0)
        {
            var found = _pins is null ? [] : await _pins.GetUsersAsync(_tenant.OrganizationId, missing, ct);
            foreach (var id in missing) _users[id] = found.FirstOrDefault(u => u.UserId == id);
        }
        return _users;
    }

    public string NameOf(int? userId) =>
        userId is not > 0 ? string.Empty
        : _users.GetValueOrDefault(userId.Value) is { } u ? u.Name : $"User {userId}";

    // ── store ──

    public async Task<string?> CurrencyCodeAsync(CancellationToken ct = default)
    {
        if (_currencyResolved) return _currencyCode;
        _currencyResolved = true;
        if (_orgCurrency is null || _currencies is null) return null;
        var org = _tenant.OrganizationId;
        var id  = await _orgCurrency.GetBaseCurrencyIdAsync(org, TransactionDomain.Sale, ct);
        if (id is { } currencyId && currencyId != Guid.Empty)
            _currencyCode = (await _currencies.GetAsync(org, currencyId, ct))?.Code;
        return _currencyCode;
    }

    public async Task<TaxCodeInfo?> TaxCodeAsync(Guid? uuid, CancellationToken ct = default)
    {
        if (uuid is not { } id || _taxCodes is null) return null;
        if (!_tax.TryGetValue(id, out var info)) _tax[id] = info = await _taxCodes.GetAsync(id, ct);
        return info;
    }

    /// <summary>D-11 — the store's sales tax: the live Finance code when readable, else the snapshot from the last save (POSB).</summary>
    public async Task<(Guid? Uuid, string? Code, decimal Percent)> StoreTaxAsync(PosStore s, CancellationToken ct = default)
    {
        var tax = await TaxCodeAsync(s.DefaultTaxCodeUuid, ct);
        return tax is null ? (s.DefaultTaxCodeUuid, s.DefaultTaxCode, s.DefaultTaxPercent) : (tax.Uuid, tax.Code, tax.RatePercent);
    }

    public async Task<List<PosStoreModel>> StoresAsync(IReadOnlyList<PosStore> stores, CancellationToken ct = default)
    {
        var ids = stores.Select(s => s.Id).ToList();
        var terminals = await _db.Terminals.AsNoTracking().Where(t => ids.Contains(t.StoreId) && t.IsActive)
            .GroupBy(t => t.StoreId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var shifts = await _db.Shifts.AsNoTracking().Where(s => ids.Contains(s.StoreId) && PosShiftStatus.Active.Contains(s.Status))
            .GroupBy(s => s.StoreId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var currency = await CurrencyCodeAsync(ct);

        var result = new List<PosStoreModel>(stores.Count);
        foreach (var s in stores)
        {
            // D-11 — the live Finance code when it can be read, else the snapshot taken on the last save.
            var tax = await TaxCodeAsync(s.DefaultTaxCodeUuid, ct);
            result.Add(new PosStoreModel
            {
                Uuid = s.Uuid, Code = s.Code, Name = s.Name, WarehouseUuid = s.WarehouseUuid, WarehouseName = s.WarehouseName,
                AddressLine1 = s.AddressLine1, AddressLine2 = s.AddressLine2, City = s.City, StateProvince = s.StateProvince,
                PostalCode = s.PostalCode, Country = s.Country, Phone = s.Phone, Email = s.Email, Timezone = s.Timezone,
                CurrencyCode = currency, TaxInclusivePricing = s.TaxInclusivePricing, DefaultTaxCodeUuid = s.DefaultTaxCodeUuid,
                DefaultTaxCode = tax?.Code ?? s.DefaultTaxCode, DefaultTaxPercent = tax?.RatePercent ?? s.DefaultTaxPercent,
                ReceiptHeader = s.ReceiptHeader, ReceiptFooter = s.ReceiptFooter, OfflineCreditTolerancePct = s.OfflineCreditTolerancePct,
                AutoCloseShiftHours = s.AutoCloseShiftHours, VarianceThreshold = s.VarianceThreshold, IsActive = s.IsActive,
                TerminalCount = terminals.GetValueOrDefault(s.Id), ActiveShiftCount = shifts.GetValueOrDefault(s.Id),
                CreatedAt = s.CreatedAt, ModifiedAt = s.ModifiedAt, RowVersion = s.RowVersion
            });
        }
        return result;
    }

    public async Task<PosStoreModel> StoreAsync(PosStore store, CancellationToken ct = default) => (await StoresAsync([store], ct))[0];

    // ── terminals ──

    /// <summary>Terminals with their Store loaded.</summary>
    public async Task<List<PosTerminalModel>> TerminalsAsync(IReadOnlyList<PosTerminal> terminals, CancellationToken ct = default)
    {
        var ids = terminals.Select(t => t.Id).ToList();
        var active = await _db.Shifts.AsNoTracking().Where(s => ids.Contains(s.TerminalId) && PosShiftStatus.Active.Contains(s.Status))
            .Select(s => new { s.TerminalId, s.Uuid, s.ShiftNumber }).ToListAsync(ct);
        var now = UtcNow;
        return terminals.Select(t =>
        {
            var shift = active.FirstOrDefault(s => s.TerminalId == t.Id);
            return new PosTerminalModel
            {
                Uuid = t.Uuid, StoreUuid = t.Store.Uuid, StoreName = t.Store.Name, Code = t.Code, Name = t.Name,
                DeviceBound = t.DeviceUuid is not null, DeviceUuid = t.DeviceUuid, LastSeenAt = t.LastSeenAt, LastSyncAt = t.LastSyncAt,
                IsOnline = PosClock.IsOnline(t.LastSeenAt, now), IsActive = t.IsActive,
                ActiveShiftUuid = shift?.Uuid, ActiveShiftNumber = shift?.ShiftNumber,
                PinLockedUntil = t.PinLockedUntil > now ? t.PinLockedUntil : null, RowVersion = t.RowVersion
            };
        }).ToList();
    }

    public async Task<PosTerminalModel> TerminalAsync(PosTerminal terminal, CancellationToken ct = default) => (await TerminalsAsync([terminal], ct))[0];

    public async Task<PosTerminalConfigModel> ConfigAsync(PosTerminal terminal, CancellationToken ct = default) => new()
    {
        Terminal = await TerminalAsync(terminal, ct), Store = await StoreAsync(terminal.Store, ct), ServerTime = UtcNow
    };

    // ── shifts ──

    /// <summary>Shifts with Store and Terminal loaded.</summary>
    public async Task<List<PosShiftModel>> ShiftsAsync(IReadOnlyList<PosShift> shifts, CancellationToken ct = default)
    {
        await UsersAsync(shifts.SelectMany(s => new[] { s.OpenedBy, s.ClosedBy ?? 0, s.ReconciledBy ?? 0 }), ct);
        return shifts.Select(s => Fill(new PosShiftModel(), s)).ToList();
    }

    public async Task<PosShiftModel> ShiftAsync(PosShift shift, CancellationToken ct = default) => (await ShiftsAsync([shift], ct))[0];

    public T Fill<T>(T m, PosShift s) where T : PosShiftModel
    {
        m.Uuid = s.Uuid; m.ShiftNumber = s.ShiftNumber; m.StoreUuid = s.Store.Uuid; m.StoreName = s.Store.Name;
        m.TerminalUuid = s.Terminal.Uuid; m.TerminalName = s.Terminal.Name; m.Status = s.Status;
        m.OpenedByName = NameOf(s.OpenedBy); m.OpenedAt = s.OpenedAt; m.ClosingStartedAt = s.ClosingStartedAt;
        m.ClosedByName = s.ClosedBy is null ? null : NameOf(s.ClosedBy); m.ClosedAt = s.ClosedAt;
        m.ReconciledByName = s.ReconciledBy is null ? null : NameOf(s.ReconciledBy); m.ReconciledAt = s.ReconciledAt;
        m.OpeningFloat = s.OpeningFloat; m.TotalCashSales = s.TotalCashSales; m.TotalCardSales = s.TotalCardSales;
        m.TotalMobileSales = s.TotalMobileSales; m.TotalOtherSales = s.TotalOtherSales; m.TotalCreditSales = s.TotalCreditSales;
        m.TotalCashRefunds = s.TotalCashRefunds; m.TotalOtherRefunds = s.TotalOtherRefunds; m.TotalCashIn = s.TotalCashIn;
        m.TotalCashOut = s.TotalCashOut; m.TotalStoreCreditIssued = s.TotalStoreCreditIssued;
        m.TotalStoreCreditRedeemed = s.TotalStoreCreditRedeemed; m.ExpectedCashClosing = s.ExpectedCashClosing; m.ActualCashClosing = s.ActualCashClosing;
        m.CashVariance = s.CashVariance; m.VarianceThreshold = s.VarianceThreshold; m.VarianceNote = s.VarianceNote;
        m.ReconcileNotes = s.ReconcileNotes; m.OrderCount = s.OrderCount; m.RefundCount = s.RefundCount;
        m.GrossTotal = s.GrossTotal; m.NetTotal = s.NetTotal; m.RowVersion = s.RowVersion;
        return m;
    }

    // ── sessions, cash movements ──

    /// <summary>Sessions with their totals from the session's orders (voided sales excluded, like the shift totals).</summary>
    public async Task<List<PosCashierSessionModel>> SessionsAsync(IReadOnlyList<PosCashierSession> sessions, CancellationToken ct = default)
    {
        var ids = sessions.Select(s => s.Id).ToList();
        var payments = await _db.Payments.AsNoTracking()
            .Where(p => p.Order.SessionId != null && ids.Contains(p.Order.SessionId.Value) && p.Order.Status != PosOrderStatus.Voided)
            .Select(p => new { SessionId = p.Order.SessionId!.Value, p.OrderId, p.Order.OrderType, p.Method, p.Amount, p.ChangeGiven, p.IsRefund })
            .ToListAsync(ct);
        var shiftIds    = sessions.Select(s => s.ShiftId).Distinct().ToList();
        var terminalIds = sessions.Select(s => s.TerminalId).Distinct().ToList();
        var shiftUuids    = await _db.Shifts.AsNoTracking().Where(s => shiftIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Uuid, ct);
        var terminalUuids = await _db.Terminals.AsNoTracking().Where(t => terminalIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Uuid, ct);
        await UsersAsync(sessions.Select(s => s.UserId), ct);

        return sessions.Select(s =>
        {
            var mine  = payments.Where(p => p.SessionId == s.Id).ToList();
            var sales = mine.Where(p => !p.IsRefund && PosOrderType.Sells(p.OrderType)).ToList();
            decimal Sum(string method) => sales.Where(p => p.Method == method).Sum(p => p.Amount - p.ChangeGiven);
            return new PosCashierSessionModel
            {
                Uuid = s.Uuid, ShiftUuid = shiftUuids.GetValueOrDefault(s.ShiftId), TerminalUuid = terminalUuids.GetValueOrDefault(s.TerminalId),
                UserId = s.UserId, UserName = NameOf(s.UserId), StartedAt = s.StartedAt, EndedAt = s.EndedAt, IsActive = s.IsActive,
                CashSales = Sum(PosPaymentMethod.Cash), CardSales = Sum(PosPaymentMethod.Card), MobileSales = Sum(PosPaymentMethod.Mobile),
                CreditSales = Sum(PosPaymentMethod.OnAccount),
                RefundTotal = mine.Where(p => p.IsRefund).Sum(p => p.Amount),
                OrderCount = sales.Select(p => p.OrderId).Distinct().Count()
            };
        }).ToList();
    }

    public async Task<List<PosCashMovementModel>> MovementsAsync(IReadOnlyList<PosCashMovement> movements, CancellationToken ct = default)
    {
        await UsersAsync(movements.Select(m => m.PerformedBy), ct);
        return movements.Select(m => new PosCashMovementModel
        {
            Uuid = m.Uuid, Direction = m.Direction, Amount = m.Amount, Reason = m.Reason, Note = m.Note,
            PerformedByName = NameOf(m.PerformedBy), PerformedAt = m.PerformedAt, LocalId = m.LocalId
        }).ToList();
    }
}
