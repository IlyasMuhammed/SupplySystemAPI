using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Pos.Domain;
using SMS.Modules.Pos.Hubs;
using SMS.Modules.Pos.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Calc = SMS.Modules.Pos.Services.PosTotalsCalculator;

namespace SMS.Modules.Pos.Services;

/// <summary>
/// A39 API-CONTRACT §2–§3 (D-1, D-5..D-8) — receipt lookup, manager authorization, and the one return path shared by
/// <c>POST orders/{uuid}/return</c>, <c>POST orders/{uuid}/exchange</c> and <c>POST returns/sync</c>: reason, policy (window
/// of the ORIGINAL store, acceptance by the PROCESSING store), manager authorization, restocking fee, refund-method rules,
/// store credit issue / redemption, cross-store. The order is saved first; stock/Finance postings follow (A38 model).
/// Offline (sync, isOffline): what the cashier already did is flagged instead of refused — except cross-store and
/// store-credit redemption, which are never accepted offline.
/// </summary>
internal sealed partial class PosOrderService
{
    internal const string ManagerRequiredMessage = "Manager authorization required.";
    internal const string RefundMethodNotAllowed = "Refund method not allowed by the store's return policy.";
    internal static readonly TimeSpan AuthorizationLifetime = TimeSpan.FromMinutes(5);

    private IPosPinService Pins => _pins ?? throw new BadRequestException("Cashier PINs are not available on this server.");

    private sealed record ReturnInput(
        string Kind, PosReturnRequest Request, bool Offline, bool IsSync, DateTime? CreatedAtLocal = null, int? AuthorizedByUserId = null,
        string? CreditNoteOfflineNumber = null, string? OfflineNumber = null)
    {
        public PosExchangeRequest? Exchange => Request as PosExchangeRequest;
    }

    // ══ Entry points ═══════════════════════════════════════════════════════════════════════════════════════════════

    public async Task<PosOrderModel> ReturnAsync(Guid uuid, PosReturnRequest req, PosCaller caller, CancellationToken ct = default)
    {
        var (order, _) = await ProcessReturnAsync(uuid, new ReturnInput(PosOrderType.Return, req, false, false), caller, ct);
        return await GetModelAsync(order.Id, ct);
    }

    public async Task<PosOrderModel> ExchangeAsync(Guid uuid, PosExchangeRequest req, PosCaller caller, CancellationToken ct = default)
    {
        var (order, _) = await ProcessReturnAsync(uuid, new ReturnInput(PosOrderType.Exchange, req, false, false), caller, ct);
        return await GetModelAsync(order.Id, ct);
    }

    public async Task<PosSyncResultModel> SyncReturnAsync(PosReturnSyncRequest req, PosCaller caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        var kind = string.IsNullOrWhiteSpace(req.Kind) ? PosOrderType.Return : req.Kind.Trim().ToUpperInvariant();
        if (kind is not (PosOrderType.Return or PosOrderType.Exchange)) throw new BadRequestException("kind must be RETURN or EXCHANGE.");
        var (order, warnings) = await ProcessReturnAsync(req.OriginalOrderUuid,
            new ReturnInput(kind, req, req.IsOffline, true, req.CreatedAtLocal?.UtcDateTime, req.AuthorizedByUserId,
                Clean(req.StoreCreditOfflineNumber, 50)?.ToUpperInvariant(), Clean(req.OfflineNumber, 50)), caller, ct);
        return new PosSyncResultModel
        {
            LocalId = order.LocalId, Success = true, OrderUuid = order.Uuid, OrderNumber = order.OrderNumber, Status = order.Status,
            Flags = order.FlagList.ToList(), Warnings = warnings
        };
    }

    // ══ The return path ════════════════════════════════════════════════════════════════════════════════════════════

    private async Task<(PosOrder Order, List<string> Warnings)> ProcessReturnAsync(Guid originalUuid, ReturnInput input, PosCaller caller, CancellationToken ct)
    {
        var req = input.Request ?? throw new BadRequestException("A return request is required.");
        var exchange = input.Kind == PosOrderType.Exchange;
        var offline = input.Offline;
        var warnings = new List<string>();
        var flags = new List<string>();
        if (offline) flags.Add(PosOrderFlag.Offline);

        if (!await _gate.IsOnAsync(Org, PosFeatureCodes.Returns, ct))
        {
            // A39 QA — an offline return already happened at the till (and MODULE_POS in grace reads its sub-features as off,
            // which refused every offline return despite [AllowDuringGrace]): accept it with a warning, as other offline breaches.
            if (!offline) throw new BadRequestException("Returns are not enabled for your organization.");
            warnings.Add("Returns are not enabled for your organization.");
        }
        var localId = req.LocalId?.Trim();
        if (string.IsNullOrEmpty(localId) || localId.Length > 64) throw new BadRequestException("Every return needs a localId (up to 64 characters).");

        var original = await Full().FirstOrDefaultAsync(o => o.Uuid == originalUuid, ct) ?? throw new NotFoundException("Order", originalUuid);
        if (await FindByLocalIdAsync(localId, ct) is { } replay)
        {
            if (replay.OrderType != input.Kind || replay.OriginalOrderId != original.Id)
                throw new ConflictException("This localId already belongs to another order.");
            return (replay, []);
        }
        if (original.OrderType != PosOrderType.Sale) throw new BadRequestException("Only sales can be returned.");
        if (original.Status == PosOrderStatus.Voided) throw new BadRequestException("Cannot return a voided order.");
        if (original.Status == PosOrderStatus.Returned) throw new BadRequestException("All items in this order have already been returned.");

        var now = _read.UtcNow;
        var at = input.CreatedAtLocal is { } local && local < now ? local : now;

        // ── processing shift / store: the shift named, else the terminal's open one, else the original terminal's ──
        PosTerminal? terminal = null;
        if (req.TerminalUuid is { } tu && tu != Guid.Empty)
            terminal = await _db.Terminals.FirstOrDefaultAsync(t => t.Uuid == tu, ct) ?? throw new NotFoundException("Terminal", tu);
        PosShift shift;
        if (req.ShiftUuid is { } shiftUuid && shiftUuid != Guid.Empty)
        {
            shift = await _db.Shifts.FirstOrDefaultAsync(s => s.Uuid == shiftUuid, ct) ?? throw new NotFoundException("Shift", shiftUuid);
            if (terminal is not null && shift.TerminalId != terminal.Id) throw new BadRequestException("The shift does not belong to this terminal.");
        }
        else
        {
            var terminalId = terminal?.Id ?? original.TerminalId;
            shift = await _db.Shifts.FirstOrDefaultAsync(s => s.TerminalId == terminalId && s.Status == PosShiftStatus.Open, ct)
                    ?? throw new BadRequestException("Open a shift on the terminal to refund this return.");
        }
        var late = false;
        if (shift.Status != PosShiftStatus.Open)
        {
            if (!offline) throw new BadRequestException("The shift is not open.");
            late = !PosShiftStatus.IsActive(shift.Status);
            warnings.Add($"Shift {shift.ShiftNumber} was already {shift.Status}; the {input.Kind.ToLowerInvariant()} is booked to it late.");
        }
        var store = await _db.Stores.FirstAsync(s => s.Id == shift.StoreId, ct);
        var cross = store.Id != original.StoreId;
        var originalStore = cross ? await _db.Stores.AsNoTracking().FirstAsync(s => s.Id == original.StoreId, ct) : store;
        if (!caller.Has(PosPermissions.StoreManage) && !await IsStaffAsync(store.Id, caller.UserId, ct))
            throw new ForbiddenException("You are not staff of this store.");

        // ── D-7 cross-store ──
        if (cross)
        {
            if (offline) throw new BadRequestException("Cross-store returns require internet connectivity.");
            if (!await _gate.IsOnAsync(Org, PosFeatureCodes.CrossStoreReturns, ct))
                throw new BadRequestException("Cross-store returns are not enabled for your organization.");
            if (!caller.Has(PosPermissions.ReturnCrossStore)) throw new ForbiddenException("You do not have permission to process returns from other stores.");
            flags.Add(PosOrderFlag.CrossStore);
        }

        // ── D-4 policies: acceptance/fees/refunds from the processing store, the window from the original store ──
        var (policy, _) = await PosReturnPolicyService.ResolveAsync(_db, Org, store.Id, now, ct);
        if (policy is null) throw new BadRequestException(PosReturnPolicyService.NoPolicyMessage);
        var originalPolicy = cross ? (await PosReturnPolicyService.ResolveAsync(_db, Org, original.StoreId, now, ct)).Policy ?? policy : policy;
        if (cross && !policy.AllowCrossStoreReturn) throw new BadRequestException("This store does not accept returns from other locations.");

        string? Breach(string message)
        {
            // Online: refuse. Offline: the cashier already did it — flag it.
            if (!offline) throw new BadRequestException(message);
            if (!flags.Contains(PosOrderFlag.PolicyOverriddenOffline)) flags.Add(PosOrderFlag.PolicyOverriddenOffline);
            warnings.Add(message);
            return message;
        }

        // ── reason ──
        PosReturnReason? reason = null;
        string reasonText;
        if (req.ReasonUuid is { } ru && ru != Guid.Empty)
        {
            reason = await _db.ReturnReasons.AsNoTracking().FirstOrDefaultAsync(r => r.Uuid == ru, ct) ?? throw new BadRequestException("Unknown return reason.");
            if (!reason.IsActive) throw new BadRequestException("This return reason is no longer in use.");
            if (!PosReasonAppliesTo.Allows(reason.AppliesTo, input.Kind))
                throw new BadRequestException($"The reason '{reason.Name}' cannot be used for {(exchange ? "an exchange" : "a return")}.");
            var note = Clean(req.Note, 400);
            if (reason.RequiresNote && note is null) throw new BadRequestException("A note is required for this reason.");
            reasonText = note is null ? reason.Name : $"{reason.Name}: {note}";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(req.Reason)) throw new BadRequestException("A reason is required for a return.");
            reasonText = req.Reason.Trim();
        }

        // ── returned lines (A38 remainder rule, earlier returns and exchanges counted by absolute value) ──
        var requested = (req.Lines ?? []).Where(l => l.Quantity != 0m).ToList();
        if (requested.Count == 0) throw new BadRequestException("Select at least one item to return.");
        if (requested.Any(l => l.Quantity < 0m || decimal.Round(l.Quantity, 4) != l.Quantity))
            throw new BadRequestException("Quantities must be above zero with at most 4 decimals.");
        var earlier = await EarlierReturnsAsync(original, ct);
        var sign = exchange ? -1m : 1m;
        var returnLines = new List<PosOrderLine>();
        var returned = new List<(PosOrderLine Line, decimal Qty)>();
        foreach (var group in requested.GroupBy(l => l.OriginalLineUuid))
        {
            var line = original.Lines.FirstOrDefault(l => l.Uuid == group.Key) ?? throw new BadRequestException("A returned line is not a line of this order.");
            var qty = group.Sum(l => l.Quantity);
            var left = line.Quantity - line.ReturnedQuantity;
            if (qty > left) throw new BadRequestException($"Cannot return {qty:0.####} of '{line.ProductName}'. Only {left:0.####} returnable.");
            var before =earlier.GetValueOrDefault(line.Id) ?? new Earlier(0m, 0m, 0m);
            var (shareNet, shareTax, total) = Calc.ReturnShare(line.Quantity, line.TaxAmount, line.LineTotal, before.Qty, before.Tax, before.Total, qty);
            returnLines.Add(new PosOrderLine
            {
                OrganizationId = Org, VariantUuid = line.VariantUuid, ProductUuid = line.ProductUuid, ProductName = line.ProductName,
                VariantName = line.VariantName, Sku = line.Sku, Barcode = line.Barcode, Quantity = sign * qty, UnitPrice = line.UnitPrice,
                DiscountType = PosDiscountType.None, DiscountAmount = sign * Calc.Round(line.DiscountAmount * qty / line.Quantity),
                NetAmount = sign * shareNet, TaxPercent = line.TaxPercent, TaxAmount = sign * shareTax, LineTotal = sign * total, OriginalLineId = line.Id
            });
            returned.Add((line, qty));
        }
        var returnTotal = returnLines.Sum(l => Math.Abs(l.LineTotal));
        var returnTax   = returnLines.Sum(l => Math.Abs(l.TaxAmount));

        // ── D-5 restocking fee: reason override wins (0 waives) ──
        var feePct = reason?.RestockingFeeOverride ?? policy.RestockingFeePct;
        var fee = Calc.Round(returnTotal * feePct / 100m);
        var refundValue = returnTotal - fee;

        // ── window (original store's policy), exchange allowed (processing store's policy) ──
        var days = DaysSince(originalStore, original.CreatedAtLocal == default ? original.CreatedAt : original.CreatedAtLocal, at);
        var window = exchange ? originalPolicy.ExchangeWindowDays ?? originalPolicy.ReturnWindowDays : originalPolicy.ReturnWindowDays;
        if (exchange && (policy.ExchangeWindowDays ?? policy.ReturnWindowDays) == 0) Breach("Exchanges are not allowed under this store's policy.");
        var windowExpired = window is { } w && days > w;

        // ── exchange: the new part ──
        PosTotalsCalculator.Result? calc = null;
        List<PosSyncLineRequest> newLines = [];
        List<string> newLineTypes = [];
        Dictionary<Guid, PosCatalogVariant> variants = [];
        string orderDiscountType = PosDiscountType.None;
        (Guid? Uuid, string? Code, decimal Percent) tax = (original.TaxCodeUuid, original.TaxCode, original.TaxPercent);
        if (exchange)
        {
            var ex = input.Exchange ?? throw new BadRequestException("An exchange needs its new items.");
            newLines = ex.NewLines ?? [];
            if (newLines.Count == 0) throw new BadRequestException("An exchange needs at least one new item.");
            if (newLines.Count > MaxLines) throw new BadRequestException($"An order can have at most {MaxLines} lines.");
            newLineTypes = newLines.Select(ValidateLine).ToList();
            orderDiscountType = DiscountType(ex.OrderDiscountType, ex.OrderDiscountValue, "order");
            variants = (await Catalog.GetByVariantUuidsAsync(Org, newLines.Select(l => l.VariantUuid).Distinct().ToList(), ct)).ToDictionary(v => v.VariantUuid);
            var productIds = variants.Values.Select(v => v.ProductUuid).Distinct().ToList();
            var settings = await _db.ProductSettings.AsNoTracking().Where(p => productIds.Contains(p.ProductUuid)).ToDictionaryAsync(p => p.ProductUuid, ct);
            foreach (var l in newLines)
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
            var discounted = newLineTypes.Select((t, i) => t != PosDiscountType.None && newLines[i].DiscountValue > 0m).Any(x => x)
                             || (orderDiscountType != PosDiscountType.None && ex.OrderDiscountValue > 0m);
            if (discounted && !caller.Has(PosPermissions.OrderDiscount))
            {
                if (!offline) throw new BadRequestException("You do not have permission to give discounts.");
                flags.Add(PosOrderFlag.DiscountUnauthorized);
            }
            tax = await _read.StoreTaxAsync(store, ct);
            calc = Calc.Calculate(newLines.Select((l, i) => new Calc.LineInput(l.Quantity, l.UnitPrice, newLineTypes[i], l.DiscountValue)).ToList(),
                orderDiscountType, ex.OrderDiscountValue, tax.Percent, store.TaxInclusivePricing);
        }
        var newTotal = calc?.GrandTotal ?? 0m;
        var net = exchange ? newTotal - refundValue : -refundValue;       // > 0 the customer pays, < 0 refunded
        var carried = exchange ? Math.Min(refundValue, newTotal) : 0m;    // D-6 — value moved from the return to the new sale
        var refundDue = exchange ? Math.Max(0m, -net) : refundValue;      // paid out through the refund method(s)

        // ── D-8 manager authorization ──
        var needs = new List<string>();
        if (reason?.RequiresManager == true) needs.Add(PosManagerReason.Reason);
        if (policy.MaxReturnNoAuth is { } max && refundValue > max) needs.Add(PosManagerReason.Amount);
        if (windowExpired) needs.Add(PosManagerReason.Window);
        if (cross && policy.CrossStoreRequiresManager) needs.Add(PosManagerReason.CrossStore);
        PosReturnAuthorization? authorization = null;
        int? authorizedBy = null;
        if (needs.Count > 0)
        {
            if (offline && string.IsNullOrWhiteSpace(req.AuthorizationToken))
            {
                if (input.AuthorizedByUserId is { } manager && manager > 0)
                {
                    flags.Add(PosOrderFlag.AuthorizedOffline);
                    authorizedBy = manager;
                    if (!await QualifiesAsManagerAsync(store.Id, manager, needs, ct))
                    {
                        flags.Add(PosOrderFlag.AuthorizationInvalid);
                        warnings.Add($"User {manager} cannot authorize this return ({string.Join(", ", needs)}).");
                    }
                }
                else Breach(WindowOrManagerMessage(needs, window));
            }
            else
            {
                authorization = await UseAuthorizationAsync(req.AuthorizationToken, original, needs, window, refundValue, now, ct);
                authorizedBy = authorization.ManagerUserId;
            }
        }

        // ── refund tenders ──
        var originalMethod = OriginalRefundMethod(original);
        var creditNotesOn = await _gate.IsOnAsync(Org, PosFeatureCodes.CreditNotes, ct);
        var refunds = new List<(string Method, decimal Amount, string? Reference)>();
        var method = req.RefundMethod?.Trim().ToUpperInvariant();
        if (!string.IsNullOrEmpty(method))
        {
            if (!Calc.IsMethod(method)) throw new BadRequestException($"Unknown payment method '{method}'.");
            if (refundDue > 0m) refunds.Add((method, refundDue, Clean(req.RefundReference, 100)));
        }
        else if (!exchange && (req.Refunds?.Count ?? 0) > 0)
        {
            foreach (var r in req.Refunds!)
            {
                var m = r.Method?.Trim().ToUpperInvariant();
                if (!Calc.IsMethod(m)) throw new BadRequestException($"Unknown payment method '{r.Method}'.");
                if (r.Amount <= 0m || Calc.Round(r.Amount) != r.Amount) throw new BadRequestException("Refund amounts must be above zero with at most 2 decimals.");
                refunds.Add((m!, r.Amount, Clean(r.Reference, 100)));
            }
            var sum = refunds.Sum(r => r.Amount);
            if (sum != refundDue) throw new BadRequestException($"Refunds ({sum:0.00}) must equal the return total ({refundDue:0.00}).");
        }
        else if (refundDue > 0m) throw new BadRequestException(exchange ? "Choose how to refund the difference." : "Choose a refund method.");
        foreach (var m in refunds.Select(r => r.Method).Distinct())
        {
            var notAllowed = (policy.RefundToOriginalMethod && m != originalMethod)
                             || (m == PosPaymentMethod.Cash && !policy.AllowCashRefund)
                             || (m == PosPaymentMethod.StoreCredit && (!policy.AllowCreditNoteRefund || !creditNotesOn));
            if (notAllowed) Breach(RefundMethodNotAllowed);
        }
        var onAccountRefund = refunds.Where(r => r.Method == PosPaymentMethod.OnAccount).Sum(r => r.Amount);
        if (onAccountRefund > 0m)
        {
            var credit = original.Payments.Where(p => !p.IsRefund && p.Method == PosPaymentMethod.OnAccount).Sum(p => p.Amount);
            if (credit <= 0m) throw new BadRequestException("On-account refunds are only possible for credit sales.");
            var refundedBefore = await _db.Payments.AsNoTracking()
                .Where(p => p.IsRefund && p.Method == PosPaymentMethod.OnAccount && p.Order.OriginalOrderId == original.Id).SumAsync(p => p.Amount, ct);
            if (onAccountRefund > credit - refundedBefore)
                throw new BadRequestException($"On-account refunds cannot exceed the credit still open on this sale ({credit - refundedBefore:0.00}).");
        }

        // ── exchange settlement payments (net > 0) ──
        var payments = input.Exchange?.Payments ?? [];
        foreach (var p in payments) p.Method = p.Method?.Trim().ToUpperInvariant();
        Dictionary<Guid, PosStoreCreditNote> redeemNotes = [];
        decimal onAccountTender = 0m;
        if (exchange)
        {
            if (net > 0m)
            {
                if (payments.Count == 0) throw new BadRequestException($"Payments must cover the difference ({net:0.00}).");
                if (payments.Select(p => Calc.CheckTender(new Calc.Tender(p.Method, p.Amount, p.ChangeGiven))).FirstOrDefault(e => e is not null) is { } bad)
                    throw new BadRequestException(bad);
                var ids = payments.Select(p => p.LocalId?.Trim()).Where(id => !string.IsNullOrEmpty(id)).ToList();
                if (ids.Any(id => id!.Length > 64) || ids.Distinct().Count() != ids.Count)
                    throw new BadRequestException("Each payment needs its own localId (up to 64 characters).");
                if (ids.Count > 0 && await _db.Payments.AnyAsync(p => ids.Contains(p.LocalId), ct))
                    throw new BadRequestException("A payment of this order was already synced with another order.");
                redeemNotes = await PosStoreCreditService.PrepareRedemptionsAsync(_db, _gate, Org, payments, original.CustomerUuid, offline, caller, now, ct);
                if (Calc.CheckTenders(net, payments.Select(p => new Calc.Tender(p.Method, p.Amount, p.ChangeGiven)).ToList()) is { } tenderError)
                {
                    if (!offline) throw new BadRequestException(tenderError);
                    warnings.Add(tenderError);
                }
                onAccountTender = payments.Where(p => p.Method == PosPaymentMethod.OnAccount).Sum(p => p.Amount);
                if (onAccountTender > 0m)
                {
                    var customer = await Customers.GetAsync(Org, original.CustomerUuid, ct) ?? throw new BadRequestException("Customer not found.");
                    await CheckCreditAsync(store, customer, original.IsWalkIn, onAccountTender, offline, flags, warnings, ct);
                }
            }
            else if (payments.Count > 0) throw new BadRequestException("No payment is taken when the new items do not cost more.");
        }

        // ── store credit issued as the refund (D-2): the note's customer, expiry from the processing store's policy ──
        var storeCreditAmount = refunds.Where(r => r.Method == PosPaymentMethod.StoreCredit).Sum(r => r.Amount);
        Guid? creditCustomer = null;
        string? creditCustomerName = null;
        if (storeCreditAmount > 0m)
        {
            if (req.StoreCreditCustomerUuid is { } sc && sc != Guid.Empty)
            {
                var c = await Customers.GetAsync(Org, sc, ct) ?? throw new BadRequestException("Customer not found.");
                (creditCustomer, creditCustomerName) = (c.Uuid, c.Name);
            }
            else if (!original.IsWalkIn) (creditCustomer, creditCustomerName) = (original.CustomerUuid, original.CustomerName);
        }

        // ── numbers, then every change at once ──
        var orderNumber = await _numbers.NextNumberAsync(store, exchange ? PosNumberPrefix.Exchange : PosNumberPrefix.Return, now, ct);
        var creditNumber = storeCreditAmount > 0m ? await _numbers.NextNumberAsync(store, PosNumberPrefix.CreditNote, now, ct) : null;

        // A39 QA — the cashier the device names (as A38 sale sync: active staff, here with POS_ORDER_RETURN), else the caller.
        var cashierId = caller.UserId;
        if (req.CashierUserId is { } named && named > 0 && named != caller.UserId)
        {
            if (await IsSellerAsync(store.Id, named, ct, PosPermissions.OrderReturn)) cashierId = named;
            else warnings.Add($"User {named} cannot process returns in this store; the {input.Kind.ToLowerInvariant()} is booked to the user who synced it.");
        }
        // … and its session: by uuid, else the device's session localId (pushed before the return), else (offline) the cashier's latest.
        PosCashierSession? session = null;
        if (req.SessionUuid is { } su && su != Guid.Empty)
            session = await _db.CashierSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Uuid == su && s.ShiftId == shift.Id, ct);
        var sessionLocalId = Clean(req.SessionLocalId, 64);
        if (session is null && sessionLocalId is not null)
            session = await _db.CashierSessions.AsNoTracking().FirstOrDefaultAsync(s => s.LocalId == sessionLocalId && s.ShiftId == shift.Id, ct);
        if (session is null && offline)
            session = await _db.CashierSessions.AsNoTracking().Where(s => s.ShiftId == shift.Id && s.UserId == cashierId)
                .OrderByDescending(s => s.StartedAt).FirstOrDefaultAsync(ct);

        var ret = new PosOrder
        {
            OrganizationId = Org, LocalId = localId, OrderNumber = orderNumber, OfflineNumber = input.OfflineNumber, OrderType = input.Kind,
            Status = PosOrderStatus.Completed, StoreId = store.Id, TerminalId = shift.TerminalId, ShiftId = shift.Id, SessionId = session?.Id,
            CashierUserId = cashierId, SyncedByUserId = input.IsSync ? caller.UserId : null, CustomerUuid = original.CustomerUuid,
            CustomerCode = original.CustomerCode, CustomerName = original.CustomerName, IsWalkIn = original.IsWalkIn,
            DiscountType = exchange ? orderDiscountType : PosDiscountType.None, DiscountValue = exchange ? input.Exchange!.OrderDiscountValue : 0m,
            TaxInclusive = exchange ? store.TaxInclusivePricing : original.TaxInclusive, TaxCodeUuid = tax.Uuid, TaxCode = tax.Code, TaxPercent = tax.Percent,
            OriginalOrderId = original.Id, ReturnReason = Clean(reasonText, 500), ReturnReasonId = reason?.Id, AuthorizedByUserId = authorizedBy,
            AuthorizedAt = authorizedBy is null ? null : authorization?.CreatedAt ?? at, RestockingFeeAmount = fee, IsCrossStore = cross,
            OriginalStoreId = cross ? original.StoreId : null, IsOffline = offline, BusinessDate = PosClock.BusinessDate(store, now),
            CreatedAtLocal = at, CreatedAt = now, SyncedAt = now
        };
        foreach (var f in flags) ret.AddFlag(f);
        var number = 0;
        foreach (var l in returnLines) { l.LineNumber = ++number; ret.Lines.Add(l); }
        if (exchange)
        {
            for (var i = 0; i < newLines.Count; i++)
            {
                var l = newLines[i];
                var v = variants[l.VariantUuid];
                var r = calc!.Lines[i];
                ret.Lines.Add(new PosOrderLine
                {
                    OrganizationId = Org, LineNumber = ++number, LocalLineId = Clean(l.LocalLineId, 64), VariantUuid = v.VariantUuid,
                    ProductUuid = v.ProductUuid, ProductName = Clean(v.ProductName, 300) ?? v.Sku, VariantName = Clean(v.VariantName, 200), Sku = v.Sku,
                    Barcode = Clean(l.Barcode, 100) ?? Clean(v.Barcode, 100), Quantity = l.Quantity, UnitPrice = l.UnitPrice, CatalogPrice = v.Price,
                    DiscountType = newLineTypes[i], DiscountValue = l.DiscountValue, DiscountAmount = r.DiscountAmount, NetAmount = r.NetAmount,
                    TaxPercent = tax.Percent, TaxAmount = r.TaxAmount, LineTotal = r.LineTotal, Note = Clean(l.Note, 500)
                });
            }
            var returnNet = ret.TaxInclusive ? returnTotal : returnTotal - returnTax;
            ret.Subtotal       = calc!.Subtotal - returnNet;
            ret.DiscountAmount = calc.DiscountAmount;
            ret.TaxTotal       = calc.TaxTotal - returnTax;
            ret.GrandTotal     = newTotal - returnTotal;
            ret.PaidAmount     = payments.Where(p => p.Method != PosPaymentMethod.OnAccount).Sum(p => p.Amount);
            ret.ChangeAmount   = payments.Sum(p => p.ChangeGiven);
            ret.BalanceDue     = onAccountTender;
            ret.PaymentStatus  = net < 0m ? PosPaymentStatus.Refunded
                : onAccountTender <= 0m ? PosPaymentStatus.Paid
                : onAccountTender >= net - 0.005m ? PosPaymentStatus.CreditSale : PosPaymentStatus.PartialCredit;
        }
        else
        {
            ret.TaxTotal      = returnTax;
            ret.Subtotal      = ret.TaxInclusive ? returnTotal : returnTotal - returnTax;
            ret.GrandTotal    = refundValue; // D-5 — the fee stays with the store
            ret.PaidAmount    = refunds.Sum(r => r.Amount);
            ret.PaymentStatus = PosPaymentStatus.Refunded;
        }

        // Payments: the carried value both ways (EXCHANGE), the refund tender(s), the new part's tenders.
        if (carried > 0m)
        {
            ret.Payments.Add(new PosPayment { OrganizationId = Org, ShiftId = shift.Id, Method = PosPaymentMethod.Exchange, Amount = carried,
                Reference = orderNumber, IsRefund = true, CreatedAt = now });
            ret.Payments.Add(new PosPayment { OrganizationId = Org, ShiftId = shift.Id, Method = PosPaymentMethod.Exchange, Amount = carried,
                Reference = orderNumber, IsRefund = false, CreatedAt = now });
        }
        PosStoreCreditNote? issued = null;
        if (creditNumber is not null)
        {
            issued = PosStoreCreditService.NewNote(Org, creditNumber, creditCustomer, creditCustomerName, store.Id, shift.Id, storeCreditAmount,
                policy.CreditNoteExpiryDays is { } d ? at.AddDays(d) : null, caller.UserId, now, null);
            issued.OfflineNumber = input.CreditNoteOfflineNumber;
            issued.ReturnOrder = ret;
            issued.Transactions[0].Order = ret;
            _db.StoreCreditNotes.Add(issued);
        }
        foreach (var r in refunds)
            ret.Payments.Add(new PosPayment
            {
                OrganizationId = Org, ShiftId = shift.Id, Method = r.Method, Amount = r.Amount, IsRefund = true, CreatedAt = now,
                Reference = r.Method == PosPaymentMethod.StoreCredit && issued is not null ? issued.Number : r.Reference,
                CreditNote = r.Method == PosPaymentMethod.StoreCredit ? issued : null
            });
        foreach (var p in payments)
        {
            var payment = new PosPayment
            {
                OrganizationId = Org, ShiftId = shift.Id, LocalId = Clean(p.LocalId, 64), Method = p.Method!, Amount = p.Amount,
                ChangeGiven = p.ChangeGiven, Reference = Clean(p.Reference, 100), CreatedAt = now
            };
            if (p.Method == PosPaymentMethod.StoreCredit && redeemNotes.GetValueOrDefault(p.CreditNoteUuid!.Value) is { } cn)
            {
                payment.CreditNote = cn;
                payment.Reference  = cn.Number;
                PosStoreCreditService.Redeem(cn, p.Amount, ret, store.Id, caller.UserId, now);
            }
            ret.Payments.Add(payment);
        }

        foreach (var (line, qty) in returned) line.ReturnedQuantity += qty;
        original.Status = original.Lines.All(l => l.ReturnedQuantity >= l.Quantity) ? PosOrderStatus.Returned : PosOrderStatus.PartiallyReturned;
        if (authorization is not null) authorization.UsedAt = now;

        _db.Orders.Add(ret);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The original, a store credit note or the authorization token changed meanwhile (double use): nothing saved.
            _db.ChangeTracker.Clear();
            throw new ConflictException("The order was changed at the same time. Reload and try again.");
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            if (await FindByLocalIdAsync(localId, ct) is { } winner && winner.OriginalOrderId == original.Id && winner.OrderType == input.Kind)
                return (winner, warnings);
            throw;
        }

        if (authorization is not null) authorization.UsedByOrderId = ret.Id;
        if (issued is not null) ret.StoreCreditNoteId = issued.Id;
        ret.OriginalOrder = original;
        if (late) await NoteLateOrderAsync(shift.Id, ret, ct);
        await _posting.PostAsync(ret, ct);
        await _totals.RecalculateAsync(shift.Id, ct);

        if (onAccountRefund > 0m || onAccountTender > 0m) await BalanceChangedAsync(ret.CustomerUuid, ct);
        if (_hub is not null)
        {
            var refundMethod = refunds.Count == 0 ? PosPaymentMethod.Exchange : refunds.Count == 1 ? refunds[0].Method : "MIXED";
            await _hub.StoreEventAsync(Org, store.Uuid, PosHubEvents.ReturnProcessed, new
            {
                returnOrderUuid = ret.Uuid, originalOrderUuid = original.Uuid, originalStoreUuid = originalStore.Uuid,
                refundAmount = refunds.Sum(r => r.Amount), refundMethod
            });
            if (issued is not null) await PosStoreCreditService.IssuedEventAsync(_hub, Org, store.Uuid, issued);
        }
        await RedeemedEventsAsync(ret, store.Uuid);
        return (ret, warnings);
    }

    private static string WindowOrManagerMessage(IReadOnlyCollection<string> needs, int? window) =>
        needs.Contains(PosManagerReason.Window) && window is { } w ? $"Return window expired ({w} days)." : ManagerRequiredMessage;

    private static PosRuleException ManagerRequired(string message, IReadOnlyCollection<string> needs) =>
        new(message, new PosManagerRequiredResult { RequiresManager = true, Reasons = needs.ToList() });

    /// <summary>D-8 — the one-time token: this order, unexpired, unused, amount within its ceiling, a manager allowed for every need.</summary>
    private async Task<PosReturnAuthorization> UseAuthorizationAsync(
        string? token, PosOrder original, IReadOnlyCollection<string> needs, int? window, decimal amount, DateTime now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) throw ManagerRequired(WindowOrManagerMessage(needs, window), needs);
        var hash = HashToken(token.Trim());
        var auth = await _db.ReturnAuthorizations.FirstOrDefaultAsync(a => a.TokenHash == hash, ct);
        if (auth is null || auth.UsedAt is not null || auth.ExpiresAt <= now || auth.OriginalOrderId != original.Id)
            throw ManagerRequired("The manager authorization is invalid or has expired.", needs);
        if (amount > auth.AmountCeiling) throw ManagerRequired("The refund exceeds the amount the manager authorized.", needs);
        var needsOverride = needs.Any(n => n != PosManagerReason.CrossStore);
        if (needsOverride ? !auth.CanOverride : !(auth.CanOverride || auth.CanCrossStore))
            throw ManagerRequired("This manager cannot authorize this return.", needs);
        return auth;
    }

    /// <summary>Offline authorization check at sync: active SUPERVISOR staff of the store holding the permission the needs require.</summary>
    private async Task<bool> QualifiesAsManagerAsync(int storeId, int userId, IReadOnlyCollection<string> needs, CancellationToken ct)
    {
        if (!await _db.StoreStaff.AnyAsync(s => s.StoreId == storeId && s.UserId == userId && s.IsActive && s.PosRole == PosRole.Supervisor, ct)
            || _pins is null) return false;
        var user = (await _pins.GetUsersAsync(Org, [userId], ct)).FirstOrDefault(u => u.UserId == userId);
        if (user is not { IsActive: true }) return false;
        bool Has(string p) => user.Permissions.Contains(p, StringComparer.OrdinalIgnoreCase);
        return needs.Any(n => n != PosManagerReason.CrossStore)
            ? Has(PosPermissions.ReturnOverride)
            : Has(PosPermissions.ReturnOverride) || Has(PosPermissions.ReturnCrossStore);
    }

    /// <summary>refundToOriginalMethod — the original's largest non-cash tender method, else CASH.</summary>
    internal static string OriginalRefundMethod(PosOrder original) =>
        original.Payments.Where(p => !p.IsRefund && p.Method != PosPaymentMethod.Cash)
            .GroupBy(p => p.Method).Select(g => (Method: g.Key, Amount: g.Sum(p => p.Amount - p.ChangeGiven)))
            .OrderByDescending(x => x.Amount).Select(x => x.Method).FirstOrDefault() ?? PosPaymentMethod.Cash;

    private sealed record Earlier(decimal Qty, decimal Tax, decimal Total);

    /// <summary>What earlier returns and exchanges took of each original line (exchange lines are stored negative).</summary>
    private async Task<Dictionary<int, Earlier>> EarlierReturnsAsync(PosOrder original, CancellationToken ct)
    {
        var lineIds = original.Lines.Select(l => l.Id).ToList();
        var rows = await _db.OrderLines.AsNoTracking().Where(l => l.OriginalLineId != null && lineIds.Contains(l.OriginalLineId.Value))
            .Select(l => new { Id = l.OriginalLineId!.Value, l.Quantity, l.TaxAmount, l.LineTotal }).ToListAsync(ct);
        return rows.GroupBy(r => r.Id).ToDictionary(g => g.Key,
            g => new Earlier(g.Sum(r => Math.Abs(r.Quantity)), g.Sum(r => Math.Abs(r.TaxAmount)), g.Sum(r => Math.Abs(r.LineTotal))));
    }

    internal static int DaysSince(PosStore store, DateTime saleUtc, DateTime atUtc) =>
        (PosClock.BusinessDate(store, atUtc) - PosClock.BusinessDate(store, saleUtc)).Days;

    internal static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private async Task RedeemedEventsAsync(PosOrder order, Guid storeUuid)
    {
        if (_hub is null) return;
        foreach (var p in order.Payments.Where(p => !p.IsRefund && p.CreditNote is not null))
            await _hub.StoreEventAsync(Org, storeUuid, PosHubEvents.CreditNoteRedeemed, new
            {
                creditNoteUuid = p.CreditNote!.Uuid, redeemedAmount = p.Amount, remainingBalance = p.CreditNote.RemainingBalance, orderUuid = order.Uuid
            });
    }

    // ══ §3 Manager authorization ═══════════════════════════════════════════════════════════════════════════════════

    public async Task<PosReturnAuthorizationModel> AuthorizeReturnAsync(PosReturnAuthorizeRequest req, PosCaller caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        var terminal = await _db.Terminals.Include(t => t.Store).FirstOrDefaultAsync(t => t.Uuid == req.TerminalUuid, ct)
            ?? throw new NotFoundException("Terminal", req.TerminalUuid);
        if (!terminal.IsActive || !terminal.Store.IsActive) throw new BadRequestException("The terminal is inactive.");
        var now = _read.UtcNow;
        if (terminal.PinLockedUntil is { } until && until > now) throw PosSessionService.Locked(terminal, until);
        var pin = req.Pin?.Trim();
        if (!PosStoreService.IsValidPin(pin)) throw new BadRequestException("PIN must be 4 to 6 digits.");
        if (req.Amount <= 0m || Calc.Round(req.Amount) != req.Amount) throw new BadRequestException("The amount must be above zero with at most 2 decimals.");
        var original = await _db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Uuid == req.OriginalOrderUuid, ct)
            ?? throw new NotFoundException("Order", req.OriginalOrderUuid);
        if (!caller.Has(PosPermissions.StoreManage) && !await IsStaffAsync(terminal.StoreId, caller.UserId, ct))
            throw new ForbiddenException("You are not staff of this store.");

        var supervisors = await _db.StoreStaff.Where(s => s.StoreId == terminal.StoreId && s.IsActive && s.PosRole == PosRole.Supervisor)
            .Select(s => s.UserId).ToListAsync(ct);
        int? managerId;
        if (req.ManagerUserId is { } requested && requested > 0)
        {
            if (!supervisors.Contains(requested)) throw new BadRequestException("This manager is not a supervisor of this store.");
            managerId = await Pins.VerifyAsync(Org, requested, pin!, ct) ? requested : null;
        }
        else
        {
            var withPin = supervisors.Count == 0 ? [] : (await Pins.GetUsersAsync(Org, supervisors, ct)).Where(u => u.IsActive && u.HasPin).Select(u => u.UserId).ToList();
            managerId = withPin.Count == 0 ? null : await Pins.FindByPinAsync(Org, withPin, pin!, ct);
        }
        if (managerId is null)
        {
            // Shares the terminal's PIN lockout with the cashier sign-in (POS-CSH-07).
            terminal.PinFailedAttempts++;
            if (terminal.PinFailedAttempts >= PosSessionService.MaxPinFailures)
            {
                terminal.PinFailedAttempts = 0;
                terminal.PinLockedUntil    = now + PosSessionService.PinLockout;
            }
            await TrySaveTerminalAsync(ct);
            if (terminal.PinLockedUntil is { } locked && locked > now) throw PosSessionService.Locked(terminal, locked);
            throw new BadRequestException("Invalid PIN.");
        }

        var manager = (await Pins.GetUsersAsync(Org, [managerId.Value], ct)).FirstOrDefault(u => u.UserId == managerId.Value);
        if (manager is not { IsActive: true }) throw new BadRequestException("The user is inactive.");
        bool Has(string p) => manager.Permissions.Contains(p, StringComparer.OrdinalIgnoreCase);
        var canOverride = Has(PosPermissions.ReturnOverride);
        var canCross    = Has(PosPermissions.ReturnCrossStore);
        var reasons = (req.Reasons ?? []).Select(r => r?.Trim().ToUpperInvariant()).Where(r => !string.IsNullOrEmpty(r)).ToList();
        var onlyCross = reasons.Count > 0 && reasons.All(r => r == PosManagerReason.CrossStore);
        if (!(canOverride || (onlyCross && canCross))) throw new BadRequestException("This manager cannot authorize returns.");

        terminal.PinFailedAttempts = 0;
        terminal.PinLockedUntil    = null;
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var auth = new PosReturnAuthorization
        {
            OrganizationId = Org, TokenHash = HashToken(token), TerminalId = terminal.Id, OriginalOrderId = original.Id, ManagerUserId = manager.UserId,
            RequestedBy = caller.UserId, AmountCeiling = req.Amount, CanOverride = canOverride, CanCrossStore = canCross, CreatedAt = now,
            ExpiresAt = now + AuthorizationLifetime
        };
        _db.ReturnAuthorizations.Add(auth);
        await _db.SaveChangesAsync(ct);
        return new PosReturnAuthorizationModel
        {
            AuthorizationToken = token, ManagerUserId = manager.UserId, ManagerName = manager.Name, ExpiresAt = auth.ExpiresAt
        };
    }

    private async Task TrySaveTerminalAsync(CancellationToken ct)
    {
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { _db.ChangeTracker.Clear(); }
    }

    // ══ §2 Lookup ══════════════════════════════════════════════════════════════════════════════════════════════════

    public async Task<PosOrderLookupModel> LookupAsync(string? receipt, Guid? storeUuid, CancellationToken ct = default)
    {
        var r = receipt?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(r)) throw new BadRequestException("Enter a receipt number.");
        var order = await Full().AsNoTracking().Where(o => o.OrderNumber == r || o.OfflineNumber == r).OrderByDescending(o => o.Id).FirstOrDefaultAsync(ct);
        return order is null ? new PosOrderLookupModel { Found = false } : await LookupModelAsync(order, storeUuid, ct);
    }

    public async Task<PosOrderLookupModel> ReturnableLinesAsync(Guid uuid, Guid? storeUuid, CancellationToken ct = default)
    {
        var order = await Full().AsNoTracking().FirstOrDefaultAsync(o => o.Uuid == uuid, ct) ?? throw new NotFoundException("Order", uuid);
        return await LookupModelAsync(order, storeUuid, ct);
    }

    private async Task<PosOrderLookupModel> LookupModelAsync(PosOrder order, Guid? storeUuid, CancellationToken ct)
    {
        var originalStore = await _db.Stores.AsNoTracking().FirstAsync(s => s.Id == order.StoreId, ct);
        var processing = storeUuid is { } su && su != Guid.Empty
            ? await _db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Uuid == su, ct) ?? throw new NotFoundException("Store", su)
            : originalStore;
        var now = _read.UtcNow;
        var earlier = await EarlierReturnsAsync(order, ct);
        var days = DaysSince(originalStore, order.CreatedAtLocal == default ? order.CreatedAt : order.CreatedAtLocal, now);
        var returnable = order.OrderType == PosOrderType.Sale && order.Status != PosOrderStatus.Voided;

        var model = new PosOrderLookupModel
        {
            Found = true,
            Order = new PosLookupOrderModel
            {
                Uuid = order.Uuid, OrderNumber = order.OrderNumber, StoreUuid = originalStore.Uuid, StoreName = originalStore.Name,
                StoreCode = originalStore.Code, CreatedAt = order.CreatedAtLocal == default ? order.CreatedAt : order.CreatedAtLocal,
                GrandTotal = order.GrandTotal, CustomerUuid = order.CustomerUuid, CustomerName = order.CustomerName, IsWalkIn = order.IsWalkIn,
                Status = order.Status, OrderType = order.OrderType, IsSameStore = processing.Id == originalStore.Id, DaysSincePurchase = days,
                Payments = order.Payments.Where(p => !p.IsRefund).OrderBy(p => p.Id)
                    .Select(p => new PosLookupPaymentModel { Method = p.Method, Amount = p.Amount - p.ChangeGiven, Reference = p.Reference }).ToList(),
                Lines = order.Lines.OrderBy(l => l.LineNumber).Select(l => new PosLookupLineModel
                {
                    LineUuid = l.Uuid, VariantUuid = l.VariantUuid, ProductName = l.ProductName, Sku = l.Sku, Quantity = l.Quantity,
                    ReturnedQuantity = l.ReturnedQuantity, ReturnableQuantity = returnable ? Math.Max(0m, l.Quantity - l.ReturnedQuantity) : 0m,
                    UnitPrice = l.UnitPrice, TaxPercent = l.TaxPercent, LineTotal = l.LineTotal,
                    RefundIfAllReturned = returnable ? l.LineTotal - (earlier.TryGetValue(l.Id, out var e) ? e.Total : 0m) : 0m
                }).ToList()
            }
        };

        var (policy, source) = await PosReturnPolicyService.ResolveAsync(_db, Org, processing.Id, now, ct);
        if (policy is null) return model;
        var originalPolicy = processing.Id == originalStore.Id ? policy
            : (await PosReturnPolicyService.ResolveAsync(_db, Org, originalStore.Id, now, ct)).Policy ?? policy;
        var names = await _db.Stores.AsNoTracking().Select(s => new { s.Id, s.Uuid, s.Name }).ToDictionaryAsync(s => s.Id, s => (s.Uuid, s.Name), ct);
        var p = PosReturnPolicyService.Fill(new PosLookupPolicyModel { Source = source }, policy, names);
        // POS-XST-06 — the window is the original store's.
        p.ReturnWindowDays   = originalPolicy.ReturnWindowDays;
        p.ExchangeWindowDays = originalPolicy.ExchangeWindowDays;
        var rw = originalPolicy.ReturnWindowDays;
        var ew = originalPolicy.ExchangeWindowDays ?? originalPolicy.ReturnWindowDays;
        p.WithinReturnWindow   = rw is null || days <= rw;
        p.WithinExchangeWindow = (policy.ExchangeWindowDays ?? policy.ReturnWindowDays) != 0 && (ew is null || days <= ew);
        p.WindowDaysRemaining  = rw is null ? null : Math.Max(0, rw.Value - days);
        p.CrossStoreAllowed    = policy.AllowCrossStoreReturn && await _gate.IsOnAsync(Org, PosFeatureCodes.CrossStoreReturns, ct);
        p.RequiresManagerForCrossStore = policy.CrossStoreRequiresManager;
        model.Policy = p;
        return model;
    }
}
