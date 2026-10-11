using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Pos.Data;
using SMS.Modules.Pos.Domain;
using SMS.Modules.Pos.Hubs;
using SMS.Modules.Pos.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;
using Calc = SMS.Modules.Pos.Services.PosTotalsCalculator;

namespace SMS.Modules.Pos.Services;

/// <summary>A38 API-CONTRACT §5 — order sync (idempotent on localId), reads, void, return, customer credit.</summary>
public interface IPosOrderService
{
    Task<PosSyncResultModel> SyncAsync(PosSyncOrderRequest req, PosCaller caller, CancellationToken ct = default);
    Task<IReadOnlyList<PosSyncResultModel>> SyncBatchAsync(PosSyncBatchRequest req, PosCaller caller, CancellationToken ct = default);
    Task<PosOrderModel> GetAsync(Guid uuid, PosCaller caller, CancellationToken ct = default);
    Task<PosOrderModel> GetByLocalIdAsync(string localId, PosCaller caller, CancellationToken ct = default);
    Task<PaginatedResponse<PosOrderListItemModel>> ListAsync(PosOrderListFilter filter, CancellationToken ct = default);
    Task<PosOrderModel> VoidAsync(Guid uuid, string? reason, PosCaller caller, CancellationToken ct = default);
    Task<PosOrderModel> ReturnAsync(Guid uuid, PosReturnRequest req, PosCaller caller, CancellationToken ct = default);
    Task<PosCustomerCreditModel> GetCreditAsync(Guid customerUuid, CancellationToken ct = default);
    // A39 §2–§3
    Task<PosOrderModel> ExchangeAsync(Guid uuid, PosExchangeRequest req, PosCaller caller, CancellationToken ct = default);
    Task<PosSyncResultModel> SyncReturnAsync(PosReturnSyncRequest req, PosCaller caller, CancellationToken ct = default);
    Task<PosOrderLookupModel> LookupAsync(string? receipt, Guid? storeUuid, CancellationToken ct = default);
    Task<PosOrderLookupModel> ReturnableLinesAsync(Guid uuid, Guid? storeUuid, CancellationToken ct = default);
    Task<PosReturnAuthorizationModel> AuthorizeReturnAsync(PosReturnAuthorizeRequest req, PosCaller caller, CancellationToken ct = default);
}

internal sealed partial class PosOrderService : IPosOrderService
{
    internal const int MaxBatch = 100, MaxLines = 500;
    internal const string CreditOverLimitNotification = "POS_CREDIT_OVER_LIMIT";
    internal const string WalkInCode = "C-WALKIN";

    private readonly PosDbContext            _db;
    private readonly ITenantContext          _tenant;
    private readonly PosReadModel            _read;
    private readonly IPosNumberSequence      _numbers;
    private readonly IPosShiftTotals         _totals;
    private readonly IPosPostingService      _posting;
    private readonly IPosCatalogSource?      _catalog;
    private readonly IPosCustomerSource?     _customers;
    private readonly ICustomerBalanceLookup? _balances;
    private readonly IPosPinService?         _pins;
    private readonly IModuleGate?            _gate;
    private readonly INotificationService?   _notifications;
    private readonly ICounterSaleService?    _finance;
    private readonly IPosChangeListener?     _changes;
    private readonly IPosHubNotifier?        _hub;
    private readonly ILogger                 _log;

    public PosOrderService(
        PosDbContext db, ITenantContext tenant, PosReadModel read, IPosNumberSequence numbers, IPosShiftTotals totals, IPosPostingService posting,
        IPosCatalogSource? catalog = null, IPosCustomerSource? customers = null, ICustomerBalanceLookup? balances = null,
        IPosPinService? pins = null, IModuleGate? gate = null, INotificationService? notifications = null,
        ICounterSaleService? finance = null, IPosChangeListener? changes = null, ILogger<PosOrderService>? log = null,
        IPosHubNotifier? hub = null)
    {
        _db = db; _tenant = tenant; _read = read; _numbers = numbers; _totals = totals; _posting = posting;
        _catalog = catalog; _customers = customers; _balances = balances; _pins = pins; _gate = gate;
        _notifications = notifications; _finance = finance; _changes = changes; _hub = hub;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    private Guid Org => _tenant.OrganizationId;
    private IPosCatalogSource Catalog => _catalog ?? throw new BadRequestException("The product catalog is not available on this server.");
    private IPosCustomerSource Customers => _customers ?? throw new BadRequestException("Customers are not available on this server.");

    // ══ Sync ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    public async Task<IReadOnlyList<PosSyncResultModel>> SyncBatchAsync(PosSyncBatchRequest req, PosCaller caller, CancellationToken ct = default)
    {
        var orders = req?.Orders ?? [];
        if (orders.Count > MaxBatch) throw new BadRequestException($"At most {MaxBatch} orders per batch.");
        var results = new PosSyncResultModel[orders.Count];
        // createdAtLocal order (earlier offline credit sales count in a later one's balance); each one independent.
        foreach (var (order, index) in orders.Select((o, i) => (o, i)).OrderBy(x => x.o?.CreatedAtLocal ?? DateTimeOffset.MaxValue).ThenBy(x => x.i))
        {
            try
            {
                results[index] = await SyncAsync(order, caller, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _db.ChangeTracker.Clear();
                var known = ex is BadRequestException or NotFoundException or ForbiddenException or ConflictException;
                if (!known) _log.LogError(ex, "POS batch sync: order {LocalId} failed.", order?.LocalId);
                results[index] = new PosSyncResultModel
                {
                    LocalId = order?.LocalId, Success = false,
                    Error = known ? ex.Message : "The order could not be saved. Try again."
                };
            }
        }
        return results;
    }

    public async Task<PosSyncResultModel> SyncAsync(PosSyncOrderRequest req, PosCaller caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        var localId = req.LocalId?.Trim();
        if (string.IsNullOrEmpty(localId) || localId.Length > 64) throw new BadRequestException("Every order needs a localId (up to 64 characters).");
        if (await FindByLocalIdAsync(localId, ct) is { } replay) return Replayed(replay);

        var offline  = req.IsOffline;
        var warnings = new List<string>();
        var flags    = new List<string>();
        if (offline) flags.Add(PosOrderFlag.Offline);
        var now = _read.UtcNow;

        var terminal = await _db.Terminals.Include(t => t.Store).FirstOrDefaultAsync(t => t.Uuid == req.TerminalUuid, ct)
            ?? throw new NotFoundException("Terminal", req.TerminalUuid);
        var store = terminal.Store;
        var shift = await _db.Shifts.FirstOrDefaultAsync(s => s.Uuid == req.ShiftUuid, ct) ?? throw new NotFoundException("Shift", req.ShiftUuid);
        if (shift.TerminalId != terminal.Id) throw new BadRequestException("The shift does not belong to this terminal.");
        if (!caller.Has(PosPermissions.StoreManage) && !await IsStaffAsync(store.Id, caller.UserId, ct))
            throw new ForbiddenException("You are not staff of this store.");

        // ── shift state: OPEN; CLOSING for what was rung up before start-close; offline orders whatever the state ──
        var createdAtLocal = req.CreatedAtLocal?.UtcDateTime ?? now;
        var late = false;
        switch (shift.Status)
        {
            case PosShiftStatus.Open:
                break;
            case PosShiftStatus.Closing when offline || createdAtLocal <= shift.ClosingStartedAt:
                break;
            case PosShiftStatus.Closing:
                throw new BadRequestException("The shift is closing: no new sales can be made on it.");
            default:
                if (!offline) throw new BadRequestException("The shift is not open.");
                late = true;
                warnings.Add($"Shift {shift.ShiftNumber} was already {shift.Status}; the order is booked to it late.");
                break;
        }

        // ── cashier: the one the device names (active staff with POS_ORDER_CREATE), else the pushing user ──
        var cashierId = caller.UserId;
        if (req.CashierUserId is { } named && named > 0 && named != caller.UserId)
        {
            if (await IsSellerAsync(store.Id, named, ct)) cashierId = named;
            else warnings.Add($"User {named} cannot sell in this store; the order is booked to the user who synced it.");
        }
        else if (req.CashierUserId is null && req.SessionUuid is { } su0
                 && await _db.CashierSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Uuid == su0 && s.ShiftId == shift.Id, ct) is { } known0)
            cashierId = known0.UserId; // no cashier named: the session's user rang it up

        // ── session: the known one; else (offline) the cashier's session of this shift by session localId, latest, or a new one ──
        PosCashierSession? session = null;
        var newSession = false;
        var sessionLocalId = string.IsNullOrWhiteSpace(req.SessionLocalId) ? null : req.SessionLocalId.Trim();
        if (sessionLocalId is { Length: > 64 }) throw new BadRequestException("A session localId has at most 64 characters.");
        if (req.SessionUuid is { } sessionUuid && sessionUuid != Guid.Empty)
        {
            session = await _db.CashierSessions.FirstOrDefaultAsync(s => s.Uuid == sessionUuid, ct);
            if (session is not null && session.ShiftId != shift.Id) session = null;
            if (session is null && !offline) throw new BadRequestException("The cashier session does not belong to this shift.");
        }
        if (session is null && offline)
        {
            session = sessionLocalId is null ? null
                : await _db.CashierSessions.FirstOrDefaultAsync(s => s.LocalId == sessionLocalId && s.ShiftId == shift.Id, ct);
            session ??= await _db.CashierSessions.Where(s => s.ShiftId == shift.Id && s.UserId == cashierId)
                .OrderByDescending(s => s.StartedAt).FirstOrDefaultAsync(ct);
            if (session is null && (sessionLocalId is null || !await _db.CashierSessions.AnyAsync(s => s.LocalId == sessionLocalId, ct)))
            {
                // An offline-only session: recorded closed (the terminal's live session is never displaced by a sync).
                session = new PosCashierSession
                {
                    OrganizationId = Org, ShiftId = shift.Id, TerminalId = terminal.Id, UserId = cashierId, StartedAt = createdAtLocal,
                    EndedAt = createdAtLocal, IsActive = false, EndReason = PosSessionEndReason.Ended, LocalId = sessionLocalId
                };
                newSession = true; // saved once every rule passed, just before the order
            }
        }

        // ── customer ──
        PosCustomer customer;
        if (req.CustomerUuid is { } customerUuid && customerUuid != Guid.Empty)
        {
            customer = await Customers.GetAsync(Org, customerUuid, ct) ?? throw new BadRequestException("Customer not found.");
            if (!customer.IsActive)
            {
                if (!offline) throw new BadRequestException("The customer is inactive.");
                warnings.Add($"Customer {customer.Code} is inactive.");
            }
        }
        else customer = await Customers.GetWalkInAsync(Org, ct);
        var walkIn = IsWalkIn(customer);

        // ── lines ──
        var lines = req.Lines ?? [];
        if (lines.Count == 0) throw new BadRequestException("An order needs at least one line.");
        if (lines.Count > MaxLines) throw new BadRequestException($"An order can have at most {MaxLines} lines.");
        var lineTypes = lines.Select(l => ValidateLine(l)).ToList();
        var orderDiscountType = DiscountType(req.OrderDiscountType, req.OrderDiscountValue, "order");

        var variants = (await Catalog.GetByVariantUuidsAsync(Org, lines.Select(l => l.VariantUuid).Distinct().ToList(), ct))
            .ToDictionary(v => v.VariantUuid);
        var productIds = variants.Values.Select(v => v.ProductUuid).Distinct().ToList();
        var settings = await _db.ProductSettings.AsNoTracking().Where(p => productIds.Contains(p.ProductUuid)).ToDictionaryAsync(p => p.ProductUuid, ct);
        foreach (var l in lines)
        {
            var v = variants.GetValueOrDefault(l.VariantUuid) ?? throw new BadRequestException($"Product variant {l.VariantUuid} was not found.");
            if (!IsUsable(v, settings.GetValueOrDefault(v.ProductUuid)))
            {
                var message = $"{Name(v)} is not available for sale on POS.";
                if (!offline) throw new BadRequestException(message);
                if (!warnings.Contains(message)) warnings.Add(message);
            }
            if (l.UnitPrice != v.Price && !flags.Contains(PosOrderFlag.PriceChanged)) flags.Add(PosOrderFlag.PriceChanged);
        }

        // ── discount permission of the session user ──
        var discounted = lineTypes.Select((t, i) => t != PosDiscountType.None && lines[i].DiscountValue > 0m).Any(x => x)
                         || (orderDiscountType != PosDiscountType.None && req.OrderDiscountValue > 0m);
        if (discounted && !await CanDiscountAsync(cashierId, caller, ct))
        {
            if (!offline) throw new BadRequestException("You do not have permission to give discounts.");
            flags.Add(PosOrderFlag.DiscountUnauthorized);
        }

        // ── totals (§1) — server totals win ──
        var tax  = await _read.StoreTaxAsync(store, ct);
        var calc = Calc.Calculate(
            lines.Select((l, i) => new Calc.LineInput(l.Quantity, l.UnitPrice, lineTypes[i], l.DiscountValue)).ToList(),
            orderDiscountType, req.OrderDiscountValue, tax.Percent, store.TaxInclusivePricing);
        if (req.ClientTotals is { } client
            && (Differs(client.GrandTotal, calc.GrandTotal) || Differs(client.Subtotal, calc.Subtotal)
                || Differs(client.DiscountAmount, calc.DiscountAmount) || Differs(client.TaxTotal, calc.TaxTotal)))
            warnings.Add($"Totals were recalculated by the server: grand total {calc.GrandTotal:0.00} (device {client.GrandTotal:0.00}).");

        // ── payments ──
        var payments = req.Payments ?? [];
        foreach (var p in payments) p.Method = p.Method?.Trim().ToUpperInvariant();
        if (payments.Count == 0) throw new BadRequestException("At least one payment is required.");
        var malformed = payments.Select(p => Calc.CheckTender(new Calc.Tender(p.Method, p.Amount, p.ChangeGiven))).FirstOrDefault(e => e is not null);
        if (malformed is not null) throw new BadRequestException(malformed);
        var paymentIds = payments.Select(p => p.LocalId?.Trim()).Where(id => !string.IsNullOrEmpty(id)).ToList();
        if (paymentIds.Any(id => id!.Length > 64) || paymentIds.Distinct().Count() != paymentIds.Count)
            throw new BadRequestException("Each payment needs its own localId (up to 64 characters).");
        if (paymentIds.Count > 0 && await _db.Payments.AnyAsync(p => paymentIds.Contains(p.LocalId), ct))
        {
            // POS-SYN-05 — a concurrent push of this very order may have committed after the localId check above: its
            // payments are ours, so answer with it (A38 QA: concurrent duplicates answered 400 here).
            if (await FindByLocalIdAsync(localId, ct) is { } winner) return Replayed(winner);
            throw new BadRequestException("A payment of this order was already synced with another order.");
        }
        // A39 D-2 — store credit tenders: online only (POS-CN-04, also for an order pushed as offline), checked and spent here.
        var creditNotes = await PosStoreCreditService.PrepareRedemptionsAsync(_db, _gate, Org, payments, customer.Uuid, offline, caller, now, ct);
        if (Calc.CheckTenders(calc.GrandTotal, payments.Select(p => new Calc.Tender(p.Method, p.Amount, p.ChangeGiven)).ToList()) is { } tenderError)
        {
            if (!offline) throw new BadRequestException(tenderError);
            warnings.Add(tenderError);
        }

        var onAccount = payments.Where(p => p.Method == PosPaymentMethod.OnAccount).Sum(p => p.Amount);
        var overLimit = onAccount > 0m && await CheckCreditAsync(store, customer, walkIn, onAccount, offline || req.OfflineCredit, flags, warnings, ct);

        // ── the order ──
        if (newSession && session is not null)
        {
            _db.CashierSessions.Add(session);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (sessionLocalId is not null)
            {
                // Another push of the same offline session created it first.
                _db.Entry(session).State = EntityState.Detached;
                session = await _db.CashierSessions.FirstAsync(s => s.LocalId == sessionLocalId, ct);
            }
        }
        var number = await _numbers.NextNumberAsync(store, PosNumberPrefix.Order, now, ct);
        var order = new PosOrder
        {
            OrganizationId = Org, OrderNumber = number, LocalId = localId, OfflineNumber = Clean(req.OfflineNumber, 50),
            OrderType = PosOrderType.Sale, Status = PosOrderStatus.Completed, StoreId = store.Id, TerminalId = terminal.Id, ShiftId = shift.Id,
            SessionId = session?.Id, CashierUserId = cashierId, SyncedByUserId = caller.UserId, CustomerUuid = customer.Uuid, CustomerCode = customer.Code,
            CustomerName = customer.Name, IsWalkIn = walkIn, Subtotal = calc.Subtotal, DiscountType = orderDiscountType,
            DiscountValue = req.OrderDiscountValue, DiscountAmount = calc.DiscountAmount, TaxTotal = calc.TaxTotal, GrandTotal = calc.GrandTotal,
            PaidAmount = payments.Where(p => p.Method != PosPaymentMethod.OnAccount).Sum(p => p.Amount),
            ChangeAmount = payments.Sum(p => p.ChangeGiven), BalanceDue = onAccount,
            PaymentStatus = onAccount <= 0m ? PosPaymentStatus.Paid
                : onAccount >= calc.GrandTotal - 0.005m ? PosPaymentStatus.CreditSale : PosPaymentStatus.PartialCredit,
            TaxInclusive = store.TaxInclusivePricing, TaxCodeUuid = tax.Uuid, TaxCode = tax.Code, TaxPercent = tax.Percent,
            Note = Clean(req.Note, 1000), IsOffline = offline, ClientGrandTotal = req.ClientTotals?.GrandTotal,
            BusinessDate = PosClock.BusinessDate(store, now), CreatedAtLocal = createdAtLocal, CreatedAt = now, SyncedAt = now
        };
        foreach (var f in flags) order.AddFlag(f);
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var v = variants[l.VariantUuid];
            var r = calc.Lines[i];
            order.Lines.Add(new PosOrderLine
            {
                OrganizationId = Org, LineNumber = i + 1, LocalLineId = Clean(l.LocalLineId, 64), VariantUuid = v.VariantUuid,
                ProductUuid = v.ProductUuid, ProductName = Clean(v.ProductName, 300) ?? v.Sku, VariantName = Clean(v.VariantName, 200),
                Sku = v.Sku, Barcode = Clean(l.Barcode, 100) ?? Clean(v.Barcode, 100), Quantity = l.Quantity, UnitPrice = l.UnitPrice,
                CatalogPrice = v.Price, DiscountType = lineTypes[i], DiscountValue = l.DiscountValue, DiscountAmount = r.DiscountAmount,
                NetAmount = r.NetAmount, TaxPercent = tax.Percent, TaxAmount = r.TaxAmount, LineTotal = r.LineTotal, Note = Clean(l.Note, 500)
            });
        }
        foreach (var p in payments)
        {
            var payment = new PosPayment
            {
                OrganizationId = Org, ShiftId = shift.Id, LocalId = Clean(p.LocalId, 64), Method = p.Method!, Amount = p.Amount,
                ChangeGiven = p.ChangeGiven, Reference = Clean(p.Reference, 100), CreatedAt = now
            };
            if (p.Method == PosPaymentMethod.StoreCredit && creditNotes.GetValueOrDefault(p.CreditNoteUuid!.Value) is { } cn)
            {
                payment.CreditNote = cn;
                payment.Reference  = cn.Number; // XFIN — Finance shows the CN number on the STORE_CREDIT payment
                PosStoreCreditService.Redeem(cn, p.Amount, order, store.Id, cashierId, now);
            }
            order.Payments.Add(payment);
        }

        _db.Orders.Add(order);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException) when (creditNotes.Count > 0)
        {
            // Two tills spending the same note: the second save loses on the note's RowVersion — nothing was saved.
            _db.ChangeTracker.Clear();
            throw new ConflictException("The credit note was used at the same time. Check its balance and try again.");
        }
        catch (DbUpdateException)
        {
            // POS-SYN-05 — a concurrent push of the same localId won on the unique index: answer with it.
            _db.ChangeTracker.Clear();
            if (await FindByLocalIdAsync(localId, ct) is { } winner) return Replayed(winner);
            throw;
        }

        if (late) await NoteLateOrderAsync(shift.Id, order, ct);
        await _posting.PostAsync(order, ct);
        await _totals.RecalculateAsync(shift.Id, ct);

        if (overLimit)
            await PosSupervisors.NotifyAsync(_db, _notifications, Org, store.Id, CreditOverLimitNotification, "POS credit over limit",
                $"Offline credit sale {order.OrderNumber} ({store.Name}) for {customer.Name} put {onAccount:0.00} on account beyond the customer's limit.",
                "PosOrder", order.Uuid, $"/portal/pages/pos/orders/{order.Uuid}", ct);
        if (onAccount > 0m) await BalanceChangedAsync(customer.Uuid, ct);
        await RedeemedEventsAsync(order, store.Uuid);

        return new PosSyncResultModel
        {
            LocalId = localId, Success = true, OrderUuid = order.Uuid, OrderNumber = order.OrderNumber, Status = order.Status,
            Flags = order.FlagList.ToList(), Warnings = warnings
        };
    }

    private static PosSyncResultModel Replayed(PosOrder o)
    {
        if (o.OrderType != PosOrderType.Sale) throw new ConflictException("This localId already belongs to a return.");
        return new PosSyncResultModel
        {
            LocalId = o.LocalId, Success = true, OrderUuid = o.Uuid, OrderNumber = o.OrderNumber, Status = o.Status,
            Flags = o.FlagList.ToList(), Warnings = []
        };
    }

    private Task<PosOrder?> FindByLocalIdAsync(string localId, CancellationToken ct) =>
        _db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.LocalId == localId, ct);

    /// <summary>
    /// D-12 — ON_ACCOUNT rules. Online: refusal (400). Offline: accepted, OFFLINE_CREDIT always, CREDIT_OVER_LIMIT +
    /// notification on any breach; the offline limit is the override (else credit limit) × (1 + store tolerance %).
    /// The balance is the server's at sync time (earlier offline orders of a batch are already in it).
    /// </summary>
    private async Task<bool> CheckCreditAsync(
        PosStore store, PosCustomer customer, bool walkIn, decimal onAccount, bool offline, List<string> flags, List<string> warnings, CancellationToken ct)
    {
        if (offline) flags.Add(PosOrderFlag.OfflineCredit);
        string? breach = null;
        if (!await _gate.IsOnAsync(Org, PosFeatureCodes.CreditSales, ct)) breach = "Credit sales are not enabled.";
        else if (walkIn) breach = "Walk-In customers cannot use credit.";
        else
        {
            var settings = await _db.CustomerSettings.AsNoTracking().FirstOrDefaultAsync(c => c.CustomerUuid == customer.Uuid, ct);
            if (settings is not { AllowPosCredit: true }) breach = "This customer is not allowed to buy on credit at the POS.";
            else if (settings.MaxSingleCreditOrder is { } max && onAccount > max) breach = $"Credit for one order cannot exceed {max:0.00}.";
            else
            {
                var balance = await BalanceAsync(customer.Uuid, ct);
                var limit = offline
                    ? Calc.Round((settings.OfflineCreditLimitOverride ?? customer.CreditLimit) * (1m + store.OfflineCreditTolerancePct / 100m))
                    : customer.CreditLimit;
                if (balance + onAccount > limit)
                    breach = $"Credit limit exceeded. Limit: {limit:0.00}. Balance: {balance:0.00}. This order: {onAccount:0.00}.";
            }
        }
        if (breach is null) return false;
        if (!offline) throw new BadRequestException(breach);
        flags.Add(PosOrderFlag.CreditOverLimit);
        warnings.Add(breach);
        return true;
    }

    private async Task<decimal> BalanceAsync(Guid customerUuid, CancellationToken ct) =>
        _balances is null ? 0m : (await _balances.GetAsync(Org, [customerUuid], ct)).GetValueOrDefault(customerUuid)?.Balance ?? 0m;

    private async Task BalanceChangedAsync(Guid customerUuid, CancellationToken ct)
    {
        if (_changes is null) return;
        try { await _changes.OnCustomerBalanceChangedAsync(Org, customerUuid, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "POS: balance push failed."); }
    }

    /// <summary>Active staff of the store whose account is active and holds POS_ORDER_CREATE.</summary>
    private async Task<bool> IsSellerAsync(int storeId, int userId, CancellationToken ct, string permission = PosPermissions.OrderCreate)
    {
        if (!await IsStaffAsync(storeId, userId, ct) || _pins is null) return false;
        var user = (await _pins.GetUsersAsync(Org, [userId], ct)).FirstOrDefault(u => u.UserId == userId);
        return user is { IsActive: true } && user.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<bool> CanDiscountAsync(int userId, PosCaller caller, CancellationToken ct)
    {
        if (userId == caller.UserId) return caller.Has(PosPermissions.OrderDiscount);
        if (_pins is null) return false;
        var user = (await _pins.GetUsersAsync(Org, [userId], ct)).FirstOrDefault(u => u.UserId == userId);
        return user?.Permissions.Contains(PosPermissions.OrderDiscount, StringComparer.OrdinalIgnoreCase) == true;
    }

    private async Task NoteLateOrderAsync(int shiftId, PosOrder order, CancellationToken ct)
    {
        var shift = await _db.Shifts.FirstAsync(s => s.Id == shiftId, ct);
        var line  = $"[{_read.UtcNow:yyyy-MM-dd HH:mm} UTC] System: late offline order {order.OrderNumber} ({order.GrandTotal:0.00}) booked after the shift was {shift.Status}.";
        var notes = string.IsNullOrEmpty(shift.ReconcileNotes) ? line : shift.ReconcileNotes + "\n" + line;
        if (notes.Length > PosShiftService.MaxNotesLength) return;
        shift.ReconcileNotes = notes;
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { await _db.Entry(shift).ReloadAsync(ct); }
    }

    private static string ValidateLine(PosSyncLineRequest l)
    {
        if (l.VariantUuid == Guid.Empty) throw new BadRequestException("Every line needs a product variant.");
        if (l.Quantity <= 0m || decimal.Round(l.Quantity, 4) != l.Quantity) throw new BadRequestException("Quantities must be above zero with at most 4 decimals.");
        if (l.UnitPrice < 0m || Calc.Round(l.UnitPrice) != l.UnitPrice) throw new BadRequestException("Prices cannot be negative and have at most 2 decimals.");
        return DiscountType(l.DiscountType, l.DiscountValue, "line");
    }

    private static string DiscountType(string? type, decimal value, string what)
    {
        var t = string.IsNullOrWhiteSpace(type) ? PosDiscountType.None : type.Trim().ToUpperInvariant();
        if (!PosDiscountType.IsValid(t)) throw new BadRequestException($"Unknown {what} discount type '{type}'.");
        if (value < 0m) throw new BadRequestException("A discount cannot be negative.");
        if (t == PosDiscountType.Percent && value > 100m) throw new BadRequestException("A percentage discount cannot exceed 100%.");
        if (t == PosDiscountType.Amount && Calc.Round(value) != value) throw new BadRequestException("A discount amount has at most 2 decimals.");
        return t;
    }

    private static bool Differs(decimal a, decimal b) => Math.Abs(a - b) > Calc.Tolerance;

    /// <summary>§7 — usable on POS now: variant + product active, product saleable, IsAvailableForPos, POS settings active.</summary>
    internal static bool IsUsable(PosCatalogVariant v, PosProductSettings? s) =>
        v.IsActive && v.IsSaleable && v.IsAvailableForPos && (s?.IsPosActive ?? true);

    internal static bool IsWalkIn(PosCustomer c) =>
        c.IsSystem || string.Equals(c.Code, WalkInCode, StringComparison.OrdinalIgnoreCase) || string.Equals(c.Code, "WALK_IN", StringComparison.OrdinalIgnoreCase);

    private static string Name(PosCatalogVariant v) => string.IsNullOrWhiteSpace(v.VariantName) ? v.ProductName : $"{v.ProductName} ({v.VariantName})";

    // ══ Reads ══════════════════════════════════════════════════════════════════════════════════════════════════════

    public async Task<PosOrderModel> GetAsync(Guid uuid, PosCaller caller, CancellationToken ct = default)
    {
        var order = await Full().AsNoTracking().FirstOrDefaultAsync(o => o.Uuid == uuid, ct) ?? throw new NotFoundException("Order", uuid);
        await EnsureReadableAsync(order, caller, ct);
        return (await ModelsAsync([order], ct))[0];
    }

    public async Task<PosOrderModel> GetByLocalIdAsync(string localId, PosCaller caller, CancellationToken ct = default)
    {
        var id = localId?.Trim() ?? string.Empty;
        var order = await Full().AsNoTracking().FirstOrDefaultAsync(o => o.LocalId == id, ct) ?? throw new NotFoundException("Order", id);
        await EnsureReadableAsync(order, caller, ct);
        return (await ModelsAsync([order], ct))[0];
    }

    /// <summary>POS_ORDER_VIEW, or POS_ORDER_CREATE for an order the caller rang up or of a store they are staff of.</summary>
    private async Task EnsureReadableAsync(PosOrder order, PosCaller caller, CancellationToken ct)
    {
        if (caller.Has(PosPermissions.OrderView)) return;
        if (caller.Has(PosPermissions.OrderCreate)
            && (order.CashierUserId == caller.UserId || await IsStaffAsync(order.StoreId, caller.UserId, ct))) return;
        throw new ForbiddenException();
    }

    public async Task<PaginatedResponse<PosOrderListItemModel>> ListAsync(PosOrderListFilter f, CancellationToken ct = default)
    {
        var q = _db.Orders.AsNoTracking();
        if (f.StoreUuid is { } store)       q = q.Where(o => o.Shift.Store.Uuid == store);
        if (f.TerminalUuid is { } terminal) q = q.Where(o => o.Shift.Terminal.Uuid == terminal);
        if (f.ShiftUuid is { } shift)       q = q.Where(o => o.Shift.Uuid == shift);
        if (f.CustomerUuid is { } customer) q = q.Where(o => o.CustomerUuid == customer);
        if (Codes(f.Status) is { Count: > 0 } statuses) q = q.Where(o => statuses.Contains(o.Status));
        if (Codes(f.OrderType) is { Count: > 0 } types)  q = q.Where(o => types.Contains(o.OrderType));
        if (f.From is { } from) q = q.Where(o => o.CreatedAt >= from);
        if (f.To is { } to)     q = q.Where(o => o.CreatedAt < (to.TimeOfDay == TimeSpan.Zero ? to.AddDays(1) : to));
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var s = f.Search.Trim();
            q = q.Where(o => o.OrderNumber.Contains(s) || o.CustomerName.Contains(s) || o.CustomerCode.Contains(s)
                             || (o.OfflineNumber != null && o.OfflineNumber.Contains(s)) || o.LocalId == s);
        }
        var (page, size) = PosStoreService.Paging(f.Page, f.PageSize);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id).Skip((page - 1) * size).Take(size)
            .Select(o => new
            {
                o.Uuid, o.OrderNumber, o.OrderType, o.Status, StoreName = o.Shift.Store.Name, TerminalName = o.Shift.Terminal.Name,
                o.CashierUserId, o.CustomerName, o.GrandTotal, o.PaymentStatus, o.Flags, o.CreatedAt, o.IsOffline, o.FinancePosted, o.StockPosted
            }).ToListAsync(ct);
        await _read.UsersAsync(rows.Select(r => r.CashierUserId), ct);
        var data = rows.Select(r => new PosOrderListItemModel
        {
            Uuid = r.Uuid, OrderNumber = r.OrderNumber, OrderType = r.OrderType, Status = r.Status, StoreName = r.StoreName,
            TerminalName = r.TerminalName, CashierName = _read.NameOf(r.CashierUserId), CustomerName = r.CustomerName,
            GrandTotal = r.GrandTotal, PaymentStatus = r.PaymentStatus, Flags = new PosOrder { Flags = r.Flags }.FlagList.ToList(),
            CreatedAt = r.CreatedAt, IsOffline = r.IsOffline, FinancePosted = r.FinancePosted, StockPosted = r.StockPosted
        }).ToList();
        return PosStoreService.Page(data, total, page, size);
    }

    private static List<string>? Codes(string? csv) => string.IsNullOrWhiteSpace(csv) ? null
        : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToUpperInvariant()).ToList();

    private IQueryable<PosOrder> Full() => _db.Orders.Include(o => o.Lines).Include(o => o.Payments);

    internal async Task<List<PosOrderModel>> ModelsAsync(IReadOnlyList<PosOrder> orders, CancellationToken ct = default)
    {
        var shiftIds = orders.Select(o => o.ShiftId).Distinct().ToList();
        var shifts = await _db.Shifts.AsNoTracking().Include(s => s.Store).Include(s => s.Terminal)
            .Where(s => shiftIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var sessionIds = orders.Where(o => o.SessionId != null).Select(o => o.SessionId!.Value).Distinct().ToList();
        var sessions = await _db.CashierSessions.AsNoTracking().Where(s => sessionIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Uuid, ct);
        var originalIds = orders.Where(o => o.OriginalOrderId != null).Select(o => o.OriginalOrderId!.Value).Distinct().ToList();
        var originals = await _db.Orders.AsNoTracking().Where(o => originalIds.Contains(o.Id))
            .Select(o => new { o.Id, o.Uuid, o.OrderNumber }).ToDictionaryAsync(o => o.Id, ct);
        var originalLineIds = orders.SelectMany(o => o.Lines).Where(l => l.OriginalLineId != null).Select(l => l.OriginalLineId!.Value).Distinct().ToList();
        var originalLines = await _db.OrderLines.AsNoTracking().Where(l => originalLineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Uuid, ct);
        var ids = orders.Select(o => o.Id).ToList();
        var returns = await _db.Orders.AsNoTracking().Where(o => o.OriginalOrderId != null && ids.Contains(o.OriginalOrderId.Value))
            .OrderBy(o => o.Id).Select(o => new { OriginalId = o.OriginalOrderId!.Value, o.Uuid, o.OrderNumber, o.GrandTotal }).ToListAsync(ct);
        await _read.UsersAsync(orders.Select(o => o.CashierUserId).Concat(orders.Select(o => o.SyncedByUserId ?? 0))
            .Concat(orders.Select(o => o.AuthorizedByUserId ?? 0)), ct);
        // A39
        var reasonIds = orders.Where(o => o.ReturnReasonId != null).Select(o => o.ReturnReasonId!.Value).Distinct().ToList();
        var reasons = await _db.ReturnReasons.AsNoTracking().Where(r => reasonIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var originalStoreIds = orders.Where(o => o.OriginalStoreId != null).Select(o => o.OriginalStoreId!.Value).Distinct().ToList();
        var originalStores = await _db.Stores.AsNoTracking().Where(s => originalStoreIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var issued = await _db.StoreCreditNotes.AsNoTracking().Where(n => n.ReturnOrderId != null && ids.Contains(n.ReturnOrderId.Value))
            .ToDictionaryAsync(n => n.ReturnOrderId!.Value, ct);
        var paymentNoteIds = orders.SelectMany(o => o.Payments).Where(p => p.CreditNoteId != null).Select(p => p.CreditNoteId!.Value).Distinct().ToList();
        var paymentNotes = await _db.StoreCreditNotes.AsNoTracking().Where(n => paymentNoteIds.Contains(n.Id)).ToDictionaryAsync(n => n.Id, n => n.Uuid, ct);

        return orders.Select(o =>
        {
            var shift = shifts[o.ShiftId];
            var original = o.OriginalOrderId is { } oid ? originals.GetValueOrDefault(oid) : null;
            var reason = o.ReturnReasonId is { } rid ? reasons.GetValueOrDefault(rid) : null;
            var originalStore = o.OriginalStoreId is { } osid ? originalStores.GetValueOrDefault(osid) : null;
            var note = issued.GetValueOrDefault(o.Id);
            decimal? net = o.OrderType == PosOrderType.Exchange ? o.GrandTotal + o.RestockingFeeAmount : null;
            return new PosOrderModel
            {
                ReturnReasonUuid = reason?.Uuid, ReturnReasonName = reason?.Name, AuthorizedByUserId = o.AuthorizedByUserId,
                AuthorizedByName = o.AuthorizedByUserId is null ? null : _read.NameOf(o.AuthorizedByUserId), AuthorizedAt = o.AuthorizedAt,
                RestockingFeeAmount = o.RestockingFeeAmount, IsCrossStore = o.IsCrossStore, OriginalStoreUuid = originalStore?.Uuid,
                OriginalStoreName = originalStore?.Name,
                StoreCredit = note is null ? null : new PosOrderStoreCreditModel
                {
                    Uuid = note.Uuid, Number = note.Number, Amount = note.OriginalAmount, Balance = note.RemainingBalance, ExpiresAt = note.ExpiresAt
                },
                NetAmount = net, Settlement = net is null ? null : net > 0m ? "PAY" : net < 0m ? "REFUND" : "EVEN",
                Uuid = o.Uuid, OrderNumber = o.OrderNumber, LocalId = o.LocalId, OfflineNumber = o.OfflineNumber, OrderType = o.OrderType,
                Status = o.Status, StoreUuid = shift.Store.Uuid, StoreName = shift.Store.Name, TerminalUuid = shift.Terminal.Uuid,
                TerminalName = shift.Terminal.Name, ShiftUuid = shift.Uuid, ShiftNumber = shift.ShiftNumber,
                SessionUuid = o.SessionId is { } sid ? sessions.GetValueOrDefault(sid) : null, CashierUserId = o.CashierUserId,
                CashierName = _read.NameOf(o.CashierUserId), SyncedByUserId = o.SyncedByUserId,
                SyncedByName = o.SyncedByUserId is null ? null : _read.NameOf(o.SyncedByUserId), CustomerUuid = o.CustomerUuid, CustomerCode = o.CustomerCode,
                CustomerName = o.CustomerName, IsWalkIn = o.IsWalkIn,
                Lines = o.Lines.OrderBy(l => l.LineNumber).Select(l => new PosOrderLineModel
                {
                    Uuid = l.Uuid, LineNumber = l.LineNumber, VariantUuid = l.VariantUuid, ProductName = l.ProductName, VariantName = l.VariantName,
                    Sku = l.Sku, Barcode = l.Barcode, Quantity = l.Quantity, UnitPrice = l.UnitPrice, DiscountType = l.DiscountType,
                    DiscountValue = l.DiscountValue, DiscountAmount = l.DiscountAmount, NetAmount = l.NetAmount, TaxPercent = l.TaxPercent,
                    TaxAmount = l.TaxAmount, LineTotal = l.LineTotal,
                    OriginalLineUuid = l.OriginalLineId is { } lid ? originalLines.GetValueOrDefault(lid) : null,
                    ReturnedQuantity = l.ReturnedQuantity,
                    ReturnableQuantity = o.OrderType == PosOrderType.Sale && o.Status != PosOrderStatus.Voided ? Math.Max(0m, l.Quantity - l.ReturnedQuantity) : 0m,
                    Note = l.Note
                }).ToList(),
                Payments = o.Payments.OrderBy(p => p.Id).Select(p => new PosPaymentModel
                {
                    Uuid = p.Uuid, Method = p.Method, Amount = p.Amount, ChangeGiven = p.ChangeGiven, Reference = p.Reference,
                    IsRefund = p.IsRefund, CustomerPaymentUuid = p.CustomerPaymentUuid, CreatedAt = p.CreatedAt,
                    CreditNoteUuid = p.CreditNoteId is { } cnid ? paymentNotes.GetValueOrDefault(cnid) : null
                }).ToList(),
                Subtotal = o.Subtotal, DiscountType = o.DiscountType, DiscountValue = o.DiscountValue, DiscountAmount = o.DiscountAmount,
                TaxTotal = o.TaxTotal, GrandTotal = o.GrandTotal, PaidAmount = o.PaidAmount, ChangeAmount = o.ChangeAmount,
                BalanceDue = o.BalanceDue, PaymentStatus = o.PaymentStatus, TaxInclusive = o.TaxInclusive, TaxCode = o.TaxCode,
                TaxPercent = o.TaxPercent, OriginalOrderUuid = original?.Uuid, OriginalOrderNumber = original?.OrderNumber,
                ReturnReason = o.ReturnReason, VoidReason = o.VoidReason, Note = o.Note, IsOffline = o.IsOffline, Flags = o.FlagList.ToList(),
                StockPosted = o.StockPosted, FinancePosted = o.FinancePosted, PostingError = o.PostingError,
                SalesInvoiceUuid = o.SalesInvoiceUuid, SalesInvoiceNumber = o.SalesInvoiceNumber, CreditNoteUuid = o.CreditNoteUuid,
                CreditNoteNumber = o.CreditNoteNumber, CreatedAt = o.CreatedAt, SyncedAt = o.SyncedAt, CreatedAtLocal = o.CreatedAtLocal,
                Returns = returns.Where(r => r.OriginalId == o.Id)
                    .Select(r => new PosOrderReturnRefModel { Uuid = r.Uuid, OrderNumber = r.OrderNumber, GrandTotal = r.GrandTotal }).ToList()
            };
        }).ToList();
    }

    // ══ Void ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    public async Task<PosOrderModel> VoidAsync(Guid uuid, string? reason, PosCaller caller, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new BadRequestException("A reason is required to void an order.");
        var order = await Full().FirstOrDefaultAsync(o => o.Uuid == uuid, ct) ?? throw new NotFoundException("Order", uuid);
        if (order.OrderType != PosOrderType.Sale) throw new BadRequestException("Only a sale can be voided.");
        if (order.Status == PosOrderStatus.Voided) throw new BadRequestException("The order is already voided.");
        if (order.Status is PosOrderStatus.Returned or PosOrderStatus.PartiallyReturned)
            throw new BadRequestException("A returned order cannot be voided.");
        var shift = await _db.Shifts.FirstAsync(s => s.Id == order.ShiftId, ct);
        if (!PosShiftStatus.IsActive(shift.Status)) throw new BadRequestException("Orders can only be voided within the same shift.");
        if (!caller.Has(PosPermissions.StoreManage) && !await IsStaffAsync(order.StoreId, caller.UserId, ct))
            throw new ForbiddenException("You are not staff of this store.");

        var text = reason.Trim();
        if (text.Length > 500) text = text[..500];
        var now = _read.UtcNow;

        // A sale already in Finance is credited first, so Finance's refusals (a return or a back-office payment on the
        // invoice) answer the cashier as they are, before anything changes here.
        if (order.FinancePosted && order.SalesInvoiceUuid is { } invoice && _finance is not null)
        {
            var credit = await _finance.VoidSaleAsync(Org, invoice, PosPostingService.VoidSourceUuid(order.Uuid), text, caller.UserId, ct);
            order.CreditNoteUuid   = credit.CreditNoteUuid;
            order.CreditNoteNumber = credit.CreditNoteNumber;
        }
        else order.FinancePosted = order.GrandTotal <= 0m && order.SalesInvoiceUuid is null && order.FinancePosted;

        // A39 (XFIN) — Finance reversed the STORE_CREDIT payment; the value goes back on the note itself.
        foreach (var p in order.Payments.Where(p => !p.IsRefund && p.CreditNoteId != null))
        {
            var cn = await _db.StoreCreditNotes.FirstAsync(n => n.Id == p.CreditNoteId, ct);
            PosStoreCreditService.Restore(cn, p.Amount, order, caller.UserId, now);
        }

        order.Status     = PosOrderStatus.Voided;
        order.VoidReason = text;
        order.VoidedBy   = caller.UserId;
        order.VoidedAt   = now;
        order.StockPosted = false; // POS_VOID (and POS_SALE first if it never went through) — both idempotent
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("The order was changed at the same time. Reload and try again."); }

        await _posting.PostAsync(order, ct);
        await _totals.RecalculateAsync(shift.Id, ct);
        if (order.Payments.Any(p => p.Method == PosPaymentMethod.OnAccount)) await BalanceChangedAsync(order.CustomerUuid, ct);
        return (await ModelsAsync([order], ct))[0];
    }

    // ══ Return ═════════════════════════════════════════════════════════════════════════════════════════════════════

    // ReturnAsync / ExchangeAsync / SyncReturnAsync: PosOrderService.Returns.cs (A39 D-1).

    private async Task<PosOrderModel> GetModelAsync(int id, CancellationToken ct) =>
        (await ModelsAsync([await Full().AsNoTracking().FirstAsync(o => o.Id == id, ct)], ct))[0];

    // ══ Credit ═════════════════════════════════════════════════════════════════════════════════════════════════════

    public async Task<PosCustomerCreditModel> GetCreditAsync(Guid customerUuid, CancellationToken ct = default)
    {
        var customer = await Customers.GetAsync(Org, customerUuid, ct) ?? throw new NotFoundException("Customer", customerUuid);
        var settings = await _db.CustomerSettings.AsNoTracking().FirstOrDefaultAsync(c => c.CustomerUuid == customerUuid, ct);
        var balance  = await BalanceAsync(customerUuid, ct);
        var walkIn   = IsWalkIn(customer);
        return new PosCustomerCreditModel
        {
            Balance = balance, CreditLimit = customer.CreditLimit, Available = Math.Max(0m, customer.CreditLimit - balance),
            AllowPosCredit = !walkIn && settings?.AllowPosCredit == true, MaxSingleCreditOrder = settings?.MaxSingleCreditOrder,
            RequiresIdForCredit = settings?.RequiresIdForCredit ?? false, IsWalkIn = walkIn
        };
    }

    // ── helpers ──

    private Task<bool> IsStaffAsync(int storeId, int userId, CancellationToken ct) =>
        _db.StoreStaff.AnyAsync(s => s.StoreId == storeId && s.UserId == userId && s.IsActive, ct);

    private static string? Clean(string? v, int max)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = v.Trim();
        return v.Length <= max ? v : v[..max];
    }
}
