using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;

namespace SMS.Modules.Demand.Services;

// A29-P3-06 §4.1/§4.2/§4.5.
internal sealed partial class SaleOrderService : ISaleOrderService
{
    private readonly DemandDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly IOrganizationCurrencyService _orgCurrency;
    private readonly IDocumentNumberGenerator _numberGenerator;
    private readonly IPricingService _pricing;
    private readonly IStockReservationService _stock;
    private readonly ITimelineService _timeline;
    private readonly IBackgroundJobClient _jobs;
    private readonly IAvailabilityCheckService _availabilityCheck;
    private readonly IPurchaseOrderService _purchaseOrders;
    private readonly ISaleOrderEmailService _emailService;
    private readonly IProductVariantResolver? _variants;
    private readonly IVariantAvailabilityService? _availability;
    private readonly ITaxCodeLookup? _taxCodes;
    private readonly IExchangeRateProvider? _exchangeRates;
    private readonly ICurrencyCodeLookup? _currencyCodes;
    private readonly ISaleOrderInvoiceLookup? _invoices;
    private readonly IAttachmentService? _attachments;
    private readonly ISaleOrderDeliveryQuantities? _deliveries;

    // Both are optional the way InventoryLedgerService's master ledger is: production DI always
    // supplies them (Inventory registers both), and a caller without one gets the same behaviour
    // this codebase already had before either existed — no description, no channel check — rather
    // than failing.
    //
    // SAP alignment (docs/finance/SAP-ALIGNMENT-PLAN.md) — the last four are optional for the same
    // reason. Finance supplies tax codes, exchange rates and the order's invoices; Lookups the currency
    // codes. Without them: a line naming a tax code is refused (it cannot be checked), a price quoted in
    // another currency is taken as it is (the behaviour before rates existed), and an order is cancelled
    // without asking Finance about invoices.
    public SaleOrderService(
        DemandDbContext db, ITenantContext tenantContext, IOrganizationCurrencyService orgCurrency,
        IDocumentNumberGenerator numberGenerator, IPricingService pricing, IStockReservationService stock,
        ITimelineService timeline, IBackgroundJobClient jobs, IAvailabilityCheckService availabilityCheck,
        IPurchaseOrderService purchaseOrders, ISaleOrderEmailService emailService,
        IProductVariantResolver? variants = null, IVariantAvailabilityService? availability = null,
        ITaxCodeLookup? taxCodes = null, IExchangeRateProvider? exchangeRates = null,
        ICurrencyCodeLookup? currencyCodes = null, ISaleOrderInvoiceLookup? invoices = null,
        IAttachmentService? attachments = null, ISaleOrderDeliveryQuantities? deliveries = null,
        IEffectiveRouteResolver? routes = null, ISaleOrderDeliveryCreator? deliveryCreator = null,
        ISaleOrderDeliveryCanceller? deliveryCanceller = null, ILogger<SaleOrderService>? log = null)
    {
        // A33 — fulfillment routes (resolver + gate) and Logistics' delivery creator/canceller. All optional: without
        // them routes are off (D-11) and confirm/cancel behave exactly as before A33.
        _routes             = routes;
        _deliveryCreator    = deliveryCreator;
        _deliveryCanceller  = deliveryCanceller;
        _log                = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SaleOrderService>.Instance;
        // A32 — WorkflowEngine's attachment store, to check a linked customer PO file is this order's; and Logistics'
        // in-flight delivery quantities, for each line's computed held/reservable figures and delivery indicator.
        _attachments        = attachments;
        _deliveries         = deliveries;
        _variants           = variants;
        _availability       = availability;
        _taxCodes           = taxCodes;
        _exchangeRates      = exchangeRates;
        _currencyCodes      = currencyCodes;
        _invoices           = invoices;
        _db                 = db;
        _tenantContext      = tenantContext;
        _orgCurrency        = orgCurrency;
        _numberGenerator    = numberGenerator;
        _pricing            = pricing;
        _stock              = stock;
        _timeline           = timeline;
        _jobs               = jobs;
        _availabilityCheck  = availabilityCheck;
        _purchaseOrders     = purchaseOrders;
        _emailService       = emailService;
    }

    /// <summary>
    /// A32 PF-05 — the caller's own organization's orders. The EF tenant filter is off for a super admin, so every read and
    /// write of an existing order goes through here: another organization's order is "not found", super admin included.
    /// Every caller of this service runs in a request (the controller, SaleQuotationService), never in a bare job.
    /// </summary>
    private IQueryable<SaleOrder> OwnOrders() =>
        _db.SaleOrders.Where(o => o.OrganizationId == _tenantContext.OrganizationId);

    public async Task<Guid> CreateAsync(CreateSaleOrderRequest req, int createdBy)
    {
        if (req.PartnerId == Guid.Empty)
            throw new BadRequestException("A sale order must be for a partner.");

        // A32 BR-C3-01/02 — a direct order is MANUAL. One made from a quotation goes through the quotation's own
        // convert-to-order (which links it back); PORTAL and INTER_TENANT are not built yet (Addendum 33).
        EnsureDirectSourceType(req.SourceType);
        var customerPo = NormalizeCustomerPo(req.CustomerPoReference);

        var config       = await ReadConfigAsync();
        var deliveryMode = ResolveDeliveryMode(req.DeliveryMode, config, existing: null);
        if (deliveryMode == DeliveryMode.Ship && req.ShippingAddressId is null)
            throw new BadRequestException("A shipping address is required when the delivery mode is SHIP.");
        if (req.Lines.Count == 0)
            throw new BadRequestException("A sale order needs at least one line.");
        await ValidateRouteOverridesAsync(req.Lines);

        var currencyId = req.CurrencyId
            ?? await _orgCurrency.GetBaseCurrencyIdAsync(_tenantContext.OrganizationId)
            ?? throw new BadRequestException(
                "Currency is required — this organization has no base currency configured, so it must be supplied explicitly.");

        var orderDate = req.OrderDate?.Date ?? DateTime.UtcNow.Date;
        // §4.1 — "SO-YYYYMMDD-NNNN." The only document numbering this codebase actually has is
        // IDocumentNumberGenerator's PREFIX-YYYY-NNNNN (five digits, reused as-is here rather than
        // built a second time) — the same deviation every other numbered document in this addendum
        // makes from the spec's literal format, for the same reason: match what already exists.
        var soNumber = await _numberGenerator.NextAsync("SO", orderDate);

        var order = new SaleOrder
        {
            SoNumber               = soNumber,
            PartnerId              = req.PartnerId,
            OrderDate              = orderDate,
            ExpectedDeliveryDate   = req.ExpectedDeliveryDate,
            CurrencyId             = currencyId,
            Status                 = EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Draft),
            // An order that is collected needs no shipment (§5.2), so the two always agree.
            RequiresShipment       = deliveryMode == DeliveryMode.Ship,
            DeliveryMode           = EnumCode<DeliveryMode>.Of(deliveryMode),
            ShippingAddressId      = req.ShippingAddressId,
            // The organization's notification department unless the order names another.
            IntimationDepartmentId = req.IntimationDepartmentId ?? config.IntimationDepartmentId,
            Notes                  = req.Notes,
            SourceType             = EnumCode<SaleOrderSourceType>.Of(SaleOrderSourceType.Manual),
            CustomerPoReference    = customerPo,
            CustomerPoDate         = req.CustomerPoDate?.Date,
            CreatedBy              = createdBy,
            CreatedDate            = DateTime.UtcNow
        };

        var lineMode = DefaultLineMode(config, deliveryMode);
        var context  = new LineBuildContext(currencyId, orderDate);
        foreach (var lineReq in req.Lines)
            order.Lines.Add(await BuildLineAsync(lineReq, req.PartnerId, lineMode, context));

        ApplyTotals(order);

        _db.SaleOrders.Add(order);
        await _db.SaveChangesAsync();

        _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
            order.TraceId,
            new TimelineEvent(SaleOrderTimelineEventTypes.SoCreated, "SO", order.UUID, order.SoNumber, DateTime.UtcNow, createdBy, null),
            "SO", order.SoNumber));

        return order.UUID;
    }

    public async Task<bool> UpdateAsync(Guid uuid, UpdateSaleOrderRequest req, int modifiedBy)
    {
        var order = await OwnOrders().Include(x => x.Lines).FirstOrDefaultAsync(x => x.UUID == uuid);
        if (order is null) return false;

        if (order.Status != EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Draft))
            throw new BadRequestException("Only a DRAFT sale order can be updated.");

        var config       = await ReadConfigAsync();
        var deliveryMode = ResolveDeliveryMode(req.DeliveryMode, config, existing: order.DeliveryMode);
        if (deliveryMode == DeliveryMode.Ship && req.ShippingAddressId is null)
            throw new BadRequestException("A shipping address is required when the delivery mode is SHIP.");
        if (req.Lines.Count == 0)
            throw new BadRequestException("A sale order needs at least one line.");
        await ValidateRouteOverridesAsync(req.Lines);

        var currencyId = req.CurrencyId
            ?? await _orgCurrency.GetBaseCurrencyIdAsync(_tenantContext.OrganizationId)
            ?? throw new BadRequestException(
                "Currency is required — this organization has no base currency configured, so it must be supplied explicitly.");

        order.ExpectedDeliveryDate   = req.ExpectedDeliveryDate;
        order.CurrencyId             = currencyId;
        order.RequiresShipment       = deliveryMode == DeliveryMode.Ship;
        order.DeliveryMode           = EnumCode<DeliveryMode>.Of(deliveryMode);
        order.ShippingAddressId      = req.ShippingAddressId;
        order.IntimationDepartmentId = req.IntimationDepartmentId ?? config.IntimationDepartmentId;
        order.Notes                  = req.Notes;
        order.CustomerPoReference    = NormalizeCustomerPo(req.CustomerPoReference);
        order.CustomerPoDate         = req.CustomerPoDate?.Date;
        order.ModifiedBy             = modifiedBy;
        order.ModifiedDate           = DateTime.UtcNow;

        // A DRAFT order's lines are wholesale replaced rather than diffed line-by-line — every
        // price on every line needs re-resolving against the (possibly changed) order date and
        // quantities anyway, so there is nothing an in-place per-line update would save.
        // Every new line is built before the old ones are dropped, so a refused line (an inactive tax
        // code, a price with no exchange rate) fails the update before any line is touched.
        var lineMode = DefaultLineMode(config, deliveryMode);
        var context  = new LineBuildContext(currencyId, order.OrderDate);
        var rebuilt  = new List<SaleOrderLine>(req.Lines.Count);
        foreach (var lineReq in req.Lines)
            rebuilt.Add(await BuildLineAsync(lineReq, order.PartnerId, lineMode, context));

        _db.SaleOrderLines.RemoveRange(order.Lines);
        order.Lines.Clear();
        foreach (var line in rebuilt)
            order.Lines.Add(line);

        ApplyTotals(order);

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<SaleOrderModel?> GetByIdAsync(Guid uuid)
    {
        var order = await OwnOrders().AsNoTracking()
            .Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.UUID == uuid);

        if (order is null) return null;

        var model = ToModel(order, includeLines: true);

        // A32 C3 — the chain this order came from, as links.
        if (order.SourceQuotationId is { } quotationId)
            model.SourceQuotation = await _db.SaleQuotations.IgnoreQueryFilters().AsNoTracking()
                .Where(q => q.Id == quotationId)
                .Select(q => new SalesDocumentLinkModel { Uuid = q.UUID, Number = q.QuotationNumber, Status = q.Status })
                .FirstOrDefaultAsync();
        if (order.SourceInquiryId is { } inquiryId)
            model.SourceInquiry = await _db.SaleInquiries.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.Id == inquiryId)
                .Select(i => new SalesDocumentLinkModel { Uuid = i.UUID, Number = i.InquiryNumber, Status = i.Status })
                .FirstOrDefaultAsync();

        // A32 C4 — what each line holds, read from the ledger (never stored), and the §6.3 indicator from it.
        if (model.Lines.Count > 0)
        {
            var holds = await SaleOrderHolds.ReadAsync(_stock, _deliveries, order.UUID);
            foreach (var line in order.Lines)
            {
                var lineModel = model.Lines.Single(l => l.Uuid == line.UUID);
                lineModel.ReservedQty       = holds.ReservedFor(line.UUID);
                lineModel.ReservableQty     = holds.ReservableFor(line);
                lineModel.DeliveryIndicator = holds.IndicatorFor(order, line);
            }
        }

        if (_variants is not null && model.Lines.Count > 0)
        {
            var described = await _variants.DescribeVariantsAsync(
                model.Lines.Select(l => l.VariantUuid).Distinct().ToList());

            foreach (var line in model.Lines)
            {
                if (described is null || !described.TryGetValue(line.VariantUuid, out var v)) continue;
                line.VariantSku      = v.Sku;
                line.VariantName     = v.VariantName;
                line.ItemDescription = v.DisplayName;
                line.UnitOfMeasure   = v.UomCode;
            }
        }

        // A33 C3 — each line's route (live while DRAFT, the snapshot afterwards) and what blocks confirming.
        await ApplyRoutesAsync(order, model);

        return model;
    }

    public async Task<PaginatedResponse<SaleOrderModel>> GetListAsync(SaleOrderListFilter filter)
    {
        var query = OwnOrders().AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Status))
            query = query.Where(x => x.Status == filter.Status);
        if (filter.PartnerId is { } partnerId)
            query = query.Where(x => x.PartnerId == partnerId);
        if (filter.OrderDateFrom is { } from)
            query = query.Where(x => x.OrderDate >= from.Date);
        if (filter.OrderDateTo is { } to)
            query = query.Where(x => x.OrderDate <= to.Date);
        if (!string.IsNullOrWhiteSpace(filter.Search))
            query = query.Where(x => x.SoNumber.Contains(filter.Search)
                                  || (x.CustomerPoReference != null && x.CustomerPoReference.Contains(filter.Search)));
        if (!string.IsNullOrWhiteSpace(filter.SourceType))
            query = query.Where(x => x.SourceType == filter.SourceType);

        query = query.OrderByDescending(x => x.OrderDate).ThenByDescending(x => x.Id);

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        return new PaginatedResponse<SaleOrderModel>
        {
            // List rows omit Lines — GetByIdAsync is where a caller reads a single order's detail.
            Data         = rows.Select(x => ToModel(x, includeLines: false)).ToList(),
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    // A29-P4-03 §4.3/§4.5 — the confirmation orchestrator. CheckAndReserveAsync scores every line
    // and reserves what it can (mutating them in memory, per its own remarks on why it does not
    // save); this method makes those line changes and the header's DRAFT -> CONFIRMED transition
    // commit together in the one SaveChangesAsync below, so a failure here can never leave the
    // order confirmed without its lines matching what was actually reserved. PO creation for any
    // deficit and the confirmation email are explicitly outside that transaction, as Hangfire jobs
    // — see SaleOrderConfirmationJobs.cs for why both are safe, genuine delegations rather than
    // built-out business logic.
    public async Task<bool> ConfirmAsync(Guid uuid, int userId) => await ConfirmWithResultAsync(uuid, userId) is not null;

    public async Task<SaleOrderConfirmResultModel?> ConfirmWithResultAsync(Guid uuid, int userId)
    {
        if (!await OwnOrders().AnyAsync(x => x.UUID == uuid)) return null;

        // Under the order's hold lock, like reserve, release and cancel: confirming reserves the order's stock, and the
        // ledger is Inventory's and commits on its own, so two confirmations of the same draft at the same moment both
        // passed the DRAFT check and both reserved it. The second now waits, reads CONFIRMED and is refused.
        var confirmed = await SaleOrderHolds.OneChangeAtATimeAsync<ConfirmOutcome?>(
            _db, uuid, () => ConfirmHeldAsync(uuid, userId));
        if (confirmed is null) return null;

        var (order, reservations, createDeliveries) = confirmed;

        // §13.4 — SO_CONFIRMED carries the outcome ("60 reserved, 40 deficit"), and each reservation
        // it made gets its own SO_STOCK_RESERVED event, in that order.
        var reservedQty = reservations.Sum(r => r.ReservedQty);
        var deficitQty  = order.Lines.Sum(l => l.DeficitQty ?? 0m);
        _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
            order.TraceId,
            new TimelineEvent(
                SaleOrderTimelineEventTypes.SoConfirmed, "SO", order.UUID, order.SoNumber, DateTime.UtcNow, userId,
                $"{reservedQty:0.####} reserved, {deficitQty:0.####} deficit"),
            "SO", order.SoNumber));

        foreach (var reservation in reservations)
        {
            // Built outside the expression: a Hangfire call can't contain an 'is' pattern.
            var reservedNote = string.IsNullOrEmpty(reservation.WarehouseName)
                ? $"{reservation.ReservedQty:0.####} reserved"
                : $"{reservation.ReservedQty:0.####} reserved ({reservation.WarehouseName})";
            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                order.TraceId,
                new TimelineEvent(
                    SaleOrderTimelineEventTypes.SoStockReserved, "SO", order.UUID, order.SoNumber, DateTime.UtcNow, userId,
                    reservedNote),
                "SO", order.SoNumber));
        }

        // §4.3 — "create auto-POs for deficit (delegates to Phase 5)."
        foreach (var line in order.Lines.Where(l => l.DeficitQty is > 0))
            _jobs.Enqueue<IAutoPoCreationJob>(j => j.CreateForDeficitAsync(order.UUID, line.UUID, userId));

        // §4.5/A29-P4-07 — the real confirmation email: renders, logs a SaleOrderIntimations row,
        // and enqueues its own retried send. Called directly rather than via Hangfire, unlike the
        // two lines above — it does its own enqueueing internally (see ISaleOrderEmailService) and
        // never throws outward, so awaiting it here adds real work but no new failure mode to
        // ConfirmAsync's own transaction.
        await _emailService.SendConfirmationAsync(order.UUID);

        var result = new SaleOrderConfirmResultModel { Status = order.Status };

        // A33 D-1 — the deliveries, one per route × ship-from warehouse. Only now, after the lock's transaction has
        // committed: Logistics' creator takes the same per-order lock on its own connection (REV-02), and inside ours it
        // would wait out the timeout. Best effort — a failure leaves the order confirmed and pending for the D-12 sweep.
        if (createDeliveries)
            await CreateDeliveriesAfterConfirmAsync(order, userId, result);

        return result;
    }

    private sealed record ConfirmOutcome(SaleOrder Order, IReadOnlyList<LineReservation> Reservations, bool CreateDeliveries);

    /// <summary>The DRAFT → CONFIRMED change itself, run under the order's hold lock: read afresh, check, reserve, save.</summary>
    private async Task<ConfirmOutcome?> ConfirmHeldAsync(Guid uuid, int userId)
    {
        var order = await OwnOrders().Include(x => x.Lines).FirstOrDefaultAsync(x => x.UUID == uuid);
        if (order is null) return null;

        if (order.Status != EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Draft))
            throw new BadRequestException("Only a DRAFT sale order can be confirmed.");

        // A draft taken while customer pickup was on must not be confirmed once it is off (§8.1).
        var config = await ReadConfigAsync();
        if (order.DeliveryMode == EnumCode<DeliveryMode>.Of(DeliveryMode.SelfPickup) && !config.SelfPickupEnabled)
            throw new BadRequestException(
                "Customer pickup is switched off for this organization. Change the order to be shipped before confirming it.");

        // A33 PC-04 — the route gate (BR-C3-02, D-4), before anything is reserved, and re-checked here rather than trusted
        // from the form: a route may have been deactivated since the line was saved (R-8). Every blocker in one 400.
        var routing = await ResolveRoutesAsync(order, config);
        if (routing.Blockers.Count > 0)
            throw new BadRequestException(routing.Message);

        var reservations = await _availabilityCheck.CheckAndReserveAsync(uuid, userId);

        // A33 D-16 — each line keeps the route it resolved to; its deliveries, and any later remainder, follow it.
        var createDeliveries = false;
        if (routing.RoutesEnabled)
        {
            SnapshotRoutes(order, routing);
            createDeliveries = config.AutoCreateDeliveriesOnConfirm && _deliveryCreator is not null
                            && order.Lines.Any(l => l.RouteSource is not null);
            // REV-01 — stamped in this same commit, cleared once the creator has run; the sweep retries only these.
            if (createDeliveries) order.DeliveryCreationPendingSince = DateTime.UtcNow;
        }

        order.Status       = EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Confirmed);
        order.ModifiedBy   = userId;
        order.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return new ConfirmOutcome(order, reservations, createDeliveries);
    }

    // A29-P4-04 §4.5.
    public async Task<bool> CancelAsync(Guid uuid, int userId, string? reason) =>
        await CancelWithResultAsync(uuid, userId, reason) is not null;

    public async Task<SaleOrderCancelResultModel?> CancelWithResultAsync(Guid uuid, int userId, string? reason)
    {
        if (!await OwnOrders().AnyAsync(x => x.UUID == uuid)) return null;

        // A32 PE-04 (BR-C4-05) — under the order's hold lock, so no manual reserve can slip in between the release and
        // the status change; and the holds are released BEFORE the order is saved as cancelled. The ledger is Inventory's
        // and commits on its own, so the two cannot share one transaction: releasing first means a failed save leaves a
        // still-open order with nothing held (cancel again — the release is idempotent), never a cancelled order whose
        // stock stays locked.
        var outcome = await SaleOrderHolds.OneChangeAtATimeAsync<(SaleOrder Order, SaleOrderDeliveryCancellationResult? Deliveries)?>(
            _db, uuid, () => CancelHeldAsync(uuid, userId, reason));
        if (outcome is not { } held) return null;
        var (order, deliveries) = held;

        // §4.5 — "cancel linked DRAFT POs." Checked here, not left to PurchaseOrderService.CancelAsync's
        // own guard, so one line's PO having already moved past DRAFT (approved, sent — outside
        // this task's scope to touch) never aborts cancelling the rest of the order.
        // Found through both links: a line's back-pointer names only its own PO, but the PO's own
        // LinkedSoId also reaches the ones a split (A29-P5-11) put alongside it for other suppliers.
        var linkedPoIds = order.Lines.Where(l => l.LinkedPoId is not null).Select(l => l.LinkedPoId!.Value).Distinct().ToList();
        var draftPoUuids = await _db.PurchaseOrders
            .Where(p => (linkedPoIds.Contains(p.Id) || p.LinkedSoId == order.Id) && p.Status == "DRAFT" && !p.IsDelete)
            .Select(p => p.UUID)
            .ToListAsync();

        foreach (var poUuid in draftPoUuids)
            await _purchaseOrders.CancelAsync(poUuid, userId, $"Linked sale order {order.SoNumber} was cancelled.");

        _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
            order.TraceId,
            new TimelineEvent("SO_CANCELLED", "SO", order.UUID, order.SoNumber, DateTime.UtcNow, userId, reason),
            "SO", order.SoNumber));

        // A33 D-15 — what happened to its deliveries, on the timeline too: the issued ones need a manual reversal.
        var result = ToCancelResult(deliveries);
        if (deliveries is not null && (deliveries.Cancelled.Count > 0 || deliveries.AlreadyIssued.Count > 0))
        {
            var note = DeliveriesCancelledNote(deliveries);
            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                order.TraceId,
                new TimelineEvent(SaleOrderTimelineEventTypes.SoDeliveriesCancelled, "SO", order.UUID, order.SoNumber, DateTime.UtcNow, userId, note),
                "SO", order.SoNumber));
        }

        // §4.5 — "enqueue cancellation email."
        _jobs.Enqueue<ISaleOrderEmailJob>(j => j.SendCancellationEmailAsync(order.UUID, reason, userId));

        return result;
    }

    private async Task<(SaleOrder Order, SaleOrderDeliveryCancellationResult? Deliveries)?> CancelHeldAsync(Guid uuid, int userId, string? reason)
    {
        var order = await OwnOrders().Include(x => x.Lines).FirstOrDefaultAsync(x => x.UUID == uuid);
        if (order is null) return null;

        var cancelled = EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Cancelled);
        var closed    = EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Closed);
        var invoiced  = EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Invoiced);
        if (order.Status is var s && (s == cancelled || s == closed || s == invoiced))
            throw new BadRequestException($"A sale order already {order.Status} cannot be cancelled.");

        // SAP alignment (S-7) — reverse, don't edit: an order whose goods have been billed is not cancelled
        // out from under its invoices, which would leave a receivable (and a cost of sales) for an order
        // that no longer exists. Finance owns the invoices; each is cancelled (or, a draft, deleted) there.
        if (_invoices is not null)
        {
            var live = await _invoices.GetLiveInvoicesAsync(order.UUID);
            if (live.Count > 0)
            {
                var listed = string.Join(", ", live.Select(i => $"{i.InvoiceNumber} ({i.Status})"));
                throw new ConflictException(
                    $"Sale order {order.SoNumber} has sales invoices that still stand: {listed}. Cancel its invoices first " +
                    "(Finance → Sales Invoices: cancel an issued one, delete a draft), then cancel the order.");
            }
        }

        // A33 D-15 (BR-C4-06/07, R-7) — the order's deliveries that have not been goods-issued are cancelled first, which
        // hands their holds back to the order, so the release below frees those too. Called inside this lock but before
        // anything here is written, so this transaction holds no row the canceller's writes could wait on (REV-02); the
        // canceller never takes this lock itself. If it fails, nothing of the cancel has happened; both are idempotent.
        // A DRAFT order never had deliveries.
        SaleOrderDeliveryCancellationResult? deliveries = null;
        if (_deliveryCanceller is not null && order.Status != EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Draft))
            deliveries = await _deliveryCanceller.CancelOpenAsync(
                _tenantContext.OrganizationId, order.UUID, reason ?? "Sale order cancelled.", userId);

        // §4.5 — "cancel (releases reservations)", BR-C4-05 — every ACTIVE SALES_ORDER hold of the order, manual or
        // not. A no-op for a DRAFT order that was never confirmed, and idempotent regardless.
        await _stock.ReleaseBySourceAsync(
            ReservationSourceType.SalesOrder, order.UUID,
            reason ?? "Sale order cancelled.", userId);

        order.Status       = cancelled;
        order.ModifiedBy   = userId;
        order.ModifiedDate = DateTime.UtcNow;
        // REV-01 — a cancelled order is never swept for deliveries.
        order.DeliveryCreationPendingSince = null;

        // A32 — the lines that were only waiting (OPEN) or holding stock (RESERVED, now released) are cancelled with the
        // order; a line that has shipped anything keeps its fulfilment status, which is history.
        var open     = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Open);
        var reserved = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Reserved);
        foreach (var line in order.Lines.Where(l => l.Status == open || l.Status == reserved))
            line.Status = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Cancelled);

        await _db.SaveChangesAsync();
        return (order, deliveries);
    }

    public async Task<TimelineDetail?> GetTimelineAsync(Guid uuid)
    {
        var traceId = await OwnOrders()
            .Where(x => x.UUID == uuid)
            .Select(x => (Guid?)x.TraceId)
            .FirstOrDefaultAsync();

        return traceId is null ? null : await _timeline.GetTimelineDetailAsync(traceId.Value);
    }

    // A preview only — reads IStockReservationService.GetAvailableAsync, reserves nothing. What
    // §4.3's confirm would see if it ran right now.
    public async Task<IReadOnlyList<SaleOrderLineAvailabilityModel>?> GetAvailabilityAsync(Guid uuid)
    {
        var order = await OwnOrders().AsNoTracking().Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.UUID == uuid);
        if (order is null) return null;

        var variantUuids = order.Lines.Select(l => l.VariantUuid).Distinct().ToList();
        var available = await _stock.GetAvailableAsync(variantUuids, warehouseUuid: null);
        var byVariant = available.ToDictionary(a => a.VariantUuid);

        return order.Lines.Select(l =>
        {
            byVariant.TryGetValue(l.VariantUuid, out var a);
            var availableQty = a?.Available ?? 0;
            return new SaleOrderLineAvailabilityModel
            {
                VariantUuid   = l.VariantUuid,
                OrderedQty    = l.Quantity,
                AvailableQty  = availableQty,
                DeficitQty    = Math.Max(0, l.Quantity - availableQty),
                WarehouseUuid = a?.WarehouseUuid,
                WarehouseName = a?.WarehouseName
            };
        }).ToList();
    }

    /// <summary>
    /// What every line of one order is built against — its currency and date — and what was already
    /// looked up for an earlier line of it, so ten lines on one tax code ask Finance once.
    /// </summary>
    private sealed class LineBuildContext(Guid orderCurrencyId, DateTime orderDate)
    {
        public Guid     OrderCurrencyId { get; } = orderCurrencyId;
        public DateTime OrderDate       { get; } = orderDate;
        public Dictionary<Guid, TaxCodeInfo?> TaxCodes      { get; } = [];
        public Dictionary<Guid, string?>      CurrencyCodes { get; } = [];

        /// <summary>The organization's base currency, once a line has needed it; null inside when it has none.</summary>
        public (Guid? Id, bool Known) BaseCurrency { get; set; }
    }

    // §4.2 — "unit_price (resolved selling price)": never trust a client-supplied price, always
    // resolve it through the same §2.3 waterfall a real sale would use.
    // A32 PD-04 — a quotation's accepted line keeps the price the customer accepted (quotedUnitPrice, already in the
    // order's currency, which is the quotation's): only the price resolution is skipped; every other rule still applies.
    private async Task<SaleOrderLine> BuildLineAsync(
        CreateSaleOrderLineRequest lineReq, Guid partnerId, string? fulfillmentMode, LineBuildContext context,
        decimal? quotedUnitPrice = null)
    {
        if (lineReq.VariantUuid == Guid.Empty)
            throw new BadRequestException("Every sale order line needs a variant.");
        if (lineReq.Quantity <= 0)
            throw new BadRequestException("Every sale order line's quantity must be greater than zero.");
        // Held to what its decimal(5,2) column can keep, as the tax percentage is: over 100% is a negative line,
        // below 0 a surcharge no one entered as one, and a third decimal is rounded away after the total was worked.
        if (lineReq.DiscountPercent is < 0m or > 100m || decimal.Round(lineReq.DiscountPercent, 2) != lineReq.DiscountPercent)
            throw new BadRequestException(
                $"A line's discount must be a percentage from 0 to 100 with at most two decimal places; {lineReq.DiscountPercent} is not.");

        var (taxCodeUuid, taxCode, taxPercent) = await ResolveTaxAsync(lineReq, context);

        string? displayName = null;
        if (_availability is not null)
        {
            var availability = await _availability.GetAvailabilityAsync(lineReq.VariantUuid);
            displayName = availability?.DisplayName;
            if (availability is null || !availability.IsAvailableForRetail)
                throw new BadRequestException(
                    $"{availability?.DisplayName ?? lineReq.VariantUuid.ToString()} is not available for retail sale.");

            // A31-C1 — conditional: a NULL or 0 limit on either side means that side is unconstrained.
            if (availability.SaleOrderMinQty is > 0 && lineReq.Quantity < availability.SaleOrderMinQty)
                throw new BadRequestException(
                    $"Quantity {lineReq.Quantity} is below the minimum order quantity of {availability.SaleOrderMinQty} for {availability.DisplayName}.");
            if (availability.SaleOrderMaxQty is > 0 && lineReq.Quantity > availability.SaleOrderMaxQty)
                throw new BadRequestException(
                    $"Quantity {lineReq.Quantity} exceeds the maximum order quantity of {availability.SaleOrderMaxQty} for {availability.DisplayName}.");
        }

        decimal unitPrice;
        if (quotedUnitPrice is { } quoted)
        {
            if (quoted < 0m)
                throw new BadRequestException($"A quoted price cannot be negative; {quoted} is.");
            unitPrice = quoted;
        }
        else
        {
            var resolution = await _pricing.ResolveSalePriceAsync(lineReq.VariantUuid, partnerId, lineReq.Quantity, context.OrderDate);
            if (!resolution.Found || resolution.UnitPrice is not { } resolvedPrice)
                throw new BadRequestException(
                    $"No sale price could be resolved for variant {lineReq.VariantUuid} — it has no selling price and no pricing rule applies.");

            unitPrice = await ToOrderCurrencyAsync(
                resolvedPrice, resolution.CurrencyId, context, displayName ?? lineReq.VariantUuid.ToString());
        }

        var line = new SaleOrderLine
        {
            VariantUuid     = lineReq.VariantUuid,
            Quantity        = lineReq.Quantity,
            UnitPrice       = unitPrice,
            DiscountPercent = lineReq.DiscountPercent,
            TaxPercent      = taxPercent,
            TaxCodeUuid     = taxCodeUuid,
            TaxCode         = taxCode,
            FulfillmentMode = fulfillmentMode,
            Status          = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Open),
            // A33 — the override, already checked by ValidateRouteOverridesAsync (BR-C3-01); null = inherit.
            FulfillmentRouteUuid = lineReq.FulfillmentRouteUuid is { } route && route != Guid.Empty ? route : null
        };
        line.LineTotal = ComputeLineTotal(line);
        return line;
    }

    /// <summary>
    /// SAP alignment (S-3) — what the line is taxed at. With a tax code, the code decides: it must be this
    /// organization's, active, and usable on a sale, and its rate is copied onto the line as a snapshot
    /// (whatever percentage the caller also sent is ignored — the code is the source of truth, as the
    /// resolved price is for the unit price). Without one, the percentage as entered, as before tax codes
    /// existed, held to 0–100 (the column is decimal(5,2)).
    /// </summary>
    private async Task<(Guid? Uuid, string? Code, decimal Percent)> ResolveTaxAsync(
        CreateSaleOrderLineRequest lineReq, LineBuildContext context)
    {
        if (lineReq.TaxCodeUuid is not { } codeUuid || codeUuid == Guid.Empty)
        {
            if (lineReq.TaxPercent is < 0m or > 100m)
                throw new BadRequestException(
                    $"A line's tax percentage must be between 0 and 100; {lineReq.TaxPercent:0.##} is not.");
            // The column keeps two places: a third would be rounded away on save, after the line's total had
            // already been worked from it, and every invoice of the order (worked from what was saved) would
            // then disagree with the order by however much that rounding was worth.
            if (decimal.Round(lineReq.TaxPercent, 2) != lineReq.TaxPercent)
                throw new BadRequestException(
                    $"A line's tax percentage can have at most two decimal places, like 17.25; {lineReq.TaxPercent} has more.");
            return (null, null, lineReq.TaxPercent);
        }

        if (_taxCodes is null)
            throw new BadRequestException(
                "Tax codes cannot be checked here, so a sale order line cannot name one. Enter the tax percentage instead.");

        if (!context.TaxCodes.TryGetValue(codeUuid, out var code))
            context.TaxCodes[codeUuid] = code = await _taxCodes.GetAsync(codeUuid);

        if (code is null)
            throw new BadRequestException(
                $"Tax code {codeUuid} does not exist in this organization. Pick one of the codes under Settings → Tax Codes.");
        if (!code.IsActive)
            throw new BadRequestException(
                $"Tax code {code.Code} is inactive and cannot be used on a new or edited line. " +
                "Pick another code, or reactivate it under Settings → Tax Codes.");
        if (!TaxCodeUsage.Allows(code.Usage, TaxCodeUsage.Sales))
            throw new BadRequestException(
                $"Tax code {code.Code} is for {code.Usage.ToLowerInvariant()} only and cannot be used on a sale order line. " +
                "Pick a code whose usage is SALES or BOTH.");

        return (code.Uuid, code.Code, code.RatePercent);
    }

    /// <summary>
    /// A price rule may be quoted in another currency than the order (a USD contract on a PKR order); the
    /// line is priced in the order's currency, converted at the rate on file for the order date. A price
    /// with no currency of its own — the variant's own selling price — is in the organization's base
    /// currency (<see cref="SalePriceResolution.CurrencyId"/>: "a null here means the org's base currency,
    /// for the caller to resolve"), so on an order in another currency it is converted from the base
    /// currency like any other; an organization with no base currency set gives it no currency to convert
    /// from, and it is taken as it is. A price already in the order's currency is taken as it is. Rounded to
    /// the cent, the way every Finance amount is — the sales invoice line that copies it keeps two places.
    /// </summary>
    private async Task<decimal> ToOrderCurrencyAsync(
        decimal price, Guid? priceCurrencyId, LineBuildContext context, string variantName)
    {
        // Without either service there is no rate to convert at: the price is taken as quoted, as it
        // always was before exchange rates existed. Production supplies both.
        if (_exchangeRates is null || _currencyCodes is null)
            return price;

        var fromId = priceCurrencyId ?? await BaseCurrencyAsync(context);
        if (fromId is not { } from || from == context.OrderCurrencyId)
            return price;

        var fromCode = await CurrencyCodeAsync(from, context);
        var toCode   = await CurrencyCodeAsync(context.OrderCurrencyId, context);

        if (fromCode is null || toCode is null)
            throw new BadRequestException(
                $"The price of {variantName} is quoted in a different currency from this order, and " +
                $"{(fromCode is null ? "the price's" : "the order's")} currency has no ISO code in Settings → Currencies, " +
                "so it cannot be converted. Give the currency its code and try again.");

        if (string.Equals(fromCode, toCode, StringComparison.OrdinalIgnoreCase))
            return price;

        var quote = await _exchangeRates.GetRateAsync(fromCode, toCode, context.OrderDate);
        if (quote is null)
            throw new BadRequestException(
                $"The price of {variantName} is quoted in {fromCode}, but this order is in {toCode} and there is no " +
                $"{fromCode} → {toCode} exchange rate on or before {context.OrderDate:dd MMM yyyy}. " +
                "Add one under Settings → Exchange Rates and try again.");

        return ExchangeRateMath.Convert(price, quote.Rate);
    }

    /// <summary>The organization's base currency, asked once per order however many lines carry a list price.</summary>
    private async Task<Guid?> BaseCurrencyAsync(LineBuildContext context)
    {
        if (!context.BaseCurrency.Known)
            context.BaseCurrency = (await _orgCurrency.GetBaseCurrencyIdAsync(_tenantContext.OrganizationId), true);
        return context.BaseCurrency.Id;
    }

    private async Task<string?> CurrencyCodeAsync(Guid currencyId, LineBuildContext context)
    {
        if (!context.CurrencyCodes.TryGetValue(currencyId, out var code))
        {
            code = await _currencyCodes!.GetCodeAsync(currencyId);
            code = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
            context.CurrencyCodes[currencyId] = code;
        }
        return code;
    }

    // The policy, or the defaults a new organization gets when nobody has opened it yet. Read without
    // creating the row: taking an order should not be what first writes the settings.
    // Own organization explicitly (A33): the tenant filter is off for a super admin, who would otherwise read whichever
    // organization's settings came first.
    private async Task<SaleOrderConfig> ReadConfigAsync() =>
        await _db.SaleOrderConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.OrganizationId == _tenantContext.OrganizationId)
        ?? new SaleOrderConfig();

    // §8.1 — "shipment_required_default=0 makes new SOs default to SELF_PICKUP (overridable)" and
    // "self_pickup_enabled=0 forces SHIP". A mode the request leaves out is the default; an order
    // being edited keeps the mode it has.
    private static DeliveryMode DefaultDeliveryMode(SaleOrderConfig config) =>
        config.ShipmentRequiredDefault || !config.SelfPickupEnabled ? DeliveryMode.Ship : DeliveryMode.SelfPickup;

    private static DeliveryMode ResolveDeliveryMode(string? requested, SaleOrderConfig config, string? existing)
    {
        DeliveryMode mode;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (!EnumCode<DeliveryMode>.TryParse(requested, out mode))
                throw new BadRequestException($"'{requested}' is not a valid delivery mode.");
        }
        else if (existing is null || !EnumCode<DeliveryMode>.TryParse(existing, out mode))
        {
            mode = DefaultDeliveryMode(config);
        }

        if (mode == DeliveryMode.SelfPickup && !config.SelfPickupEnabled)
            throw new BadRequestException(
                "Customer pickup is switched off for this organization, so this order has to be shipped.");

        return mode;
    }

    // What a new line is taken to be, until confirming the order works out what it really is. IN_STOCK
    // is "let the stock decide" and so leaves the mode empty. Drop ship needs somewhere to ship to,
    // which only a shipped order has, and an organization that has drop shipping off cannot have it.
    private static string? DefaultLineMode(SaleOrderConfig config, DeliveryMode orderMode) => config.DefaultFulfillmentMode switch
    {
        FulfillmentModes.BackToBack => EnumCode<SaleOrderLineFulfillmentMode>.Of(SaleOrderLineFulfillmentMode.BackToBack),
        FulfillmentModes.DropShip when config.DropShipEnabled && orderMode == DeliveryMode.Ship
            => EnumCode<SaleOrderLineFulfillmentMode>.Of(SaleOrderLineFulfillmentMode.DropShip),
        _ => null
    };

    public async Task<SaleOrderDefaultsModel> GetDefaultsAsync()
    {
        var config = await ReadConfigAsync();
        return new SaleOrderDefaultsModel
        {
            DeliveryMode      = EnumCode<DeliveryMode>.Of(DefaultDeliveryMode(config)),
            SelfPickupEnabled = config.SelfPickupEnabled
        };
    }

    // ── A32 C3 — source linking and the customer's PO ───────────────────────

    /// <summary>
    /// PD-04 — see <see cref="ISaleOrderService.CreateFromQuotationAsync"/>. The quotation is read from this same scoped
    /// context, so a status change the caller (SaleQuotationService) already made on it is saved by the one
    /// SaveChangesAsync below, together with the order. Its status is not checked here: by now the caller has moved it
    /// to CONVERTED. What stops a second order is the existing-order check plus the unique index on SourceQuotationId.
    /// </summary>
    public async Task<Guid> CreateFromQuotationAsync(CreateSaleOrderFromQuotationCommand cmd, int createdBy)
    {
        var quotation = await _db.SaleQuotations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(q => q.UUID == cmd.SourceQuotationUuid && q.OrganizationId == _tenantContext.OrganizationId)
            ?? throw new NotFoundException("SaleQuotation", cmd.SourceQuotationUuid);

        if (cmd.Lines.Count == 0)
            throw new BadRequestException("No accepted lines to convert — a sale order needs at least one line.");
        if (await _db.SaleOrders.IgnoreQueryFilters().AnyAsync(o => o.SourceQuotationId == quotation.Id))
            throw new ConflictException($"Quotation {quotation.QuotationNumber} has already been converted to a sale order.");

        var customerPo   = NormalizeCustomerPo(cmd.CustomerPoReference);
        var config       = await ReadConfigAsync();
        var deliveryMode = ResolveDeliveryMode(cmd.DeliveryMode, config, existing: null);
        if (deliveryMode == DeliveryMode.Ship && cmd.ShippingAddressId is null)
            throw new BadRequestException("A shipping address is required when the delivery mode is SHIP.");

        var orderDate = cmd.OrderDate?.Date ?? DateTime.UtcNow.Date;
        var soNumber  = await _numberGenerator.NextAsync("SO", orderDate);

        var order = new SaleOrder
        {
            SoNumber               = soNumber,
            PartnerId              = quotation.PartnerId,
            OrderDate              = orderDate,
            ExpectedDeliveryDate   = cmd.ExpectedDeliveryDate,
            CurrencyId             = quotation.CurrencyId,
            Status                 = EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Draft),
            RequiresShipment       = deliveryMode == DeliveryMode.Ship,
            DeliveryMode           = EnumCode<DeliveryMode>.Of(deliveryMode),
            ShippingAddressId      = cmd.ShippingAddressId,
            IntimationDepartmentId = cmd.IntimationDepartmentId ?? config.IntimationDepartmentId,
            Notes                  = cmd.Notes,
            SourceType             = EnumCode<SaleOrderSourceType>.Of(SaleOrderSourceType.FromQuotation),
            SourceQuotationId      = quotation.Id,
            SourceInquiryId        = quotation.SourceInquiryId, // BR-C3-03 — chained from the quotation
            CustomerPoReference    = customerPo,
            CustomerPoDate         = cmd.CustomerPoDate?.Date,
            CreatedBy              = createdBy,
            CreatedDate            = DateTime.UtcNow
        };

        var lineMode = DefaultLineMode(config, deliveryMode);
        var context  = new LineBuildContext(quotation.CurrencyId, orderDate);
        foreach (var quoted in cmd.Lines)
        {
            var lineReq = new CreateSaleOrderLineRequest
            {
                VariantUuid = quoted.VariantUuid, Quantity = quoted.Quantity, DiscountPercent = quoted.DiscountPercent,
                TaxPercent = quoted.TaxPercent, TaxCodeUuid = quoted.TaxCodeUuid
            };
            order.Lines.Add(await BuildLineAsync(lineReq, quotation.PartnerId, lineMode, context, quoted.UnitPrice));
        }

        ApplyTotals(order);

        _db.SaleOrders.Add(order);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            // The unique index on SourceQuotationId: another conversion of the same quotation won the race.
            _db.Entry(order).State = EntityState.Detached;
            throw new ConflictException($"Quotation {quotation.QuotationNumber} has already been converted to a sale order.");
        }

        _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
            order.TraceId,
            new TimelineEvent(SaleOrderTimelineEventTypes.SoCreated, "SO", order.UUID, order.SoNumber, DateTime.UtcNow, createdBy,
                $"From quotation {quotation.QuotationNumber}"),
            "SO", order.SoNumber));

        return order.UUID;
    }

    /// <summary>PD-05 — any order but a cancelled or closed one; the linked file must be this order's CUSTOMER_PO upload.</summary>
    public async Task<bool> UpdateCustomerPoAsync(Guid uuid, UpdateSaleOrderCustomerPoRequest req, int modifiedBy)
    {
        var order = await _db.SaleOrders.IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.UUID == uuid && o.OrganizationId == _tenantContext.OrganizationId && !o.IsDeleted);
        if (order is null) return false;

        if (order.Status == EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Cancelled)
            || order.Status == EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Closed))
            throw new BadRequestException($"The customer PO of a {order.Status} sale order cannot be changed.");

        var reference = NormalizeCustomerPo(req.CustomerPoReference);

        if (req.CustomerPoAttachmentUuid is { } attachmentUuid)
        {
            var file = _attachments is null ? null : await _attachments.FindAsync(attachmentUuid);
            if (file is null)
                throw new BadRequestException("That customer PO file was not found. Upload it to this order first, then link it.");
            if (file.InterfaceCode != CustomerPoInterfaceCode || file.DocumentId != order.UUID)
                throw new BadRequestException(
                    $"Only a file uploaded as this order's customer PO ({CustomerPoInterfaceCode}) can be linked as its PO document.");
        }

        order.CustomerPoReference      = reference;
        order.CustomerPoDate           = req.CustomerPoDate?.Date;
        order.CustomerPoAttachmentUuid = req.CustomerPoAttachmentUuid;
        order.ModifiedBy               = modifiedBy;
        order.ModifiedDate             = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>BR-C3-05 — the warning, never a refusal: same reference (trimmed, any case) on another of the org's orders.</summary>
    public async Task<IReadOnlyList<CustomerPoDuplicateModel>> FindCustomerPoDuplicatesAsync(string reference, Guid? excludeUuid)
    {
        var normalized = reference?.Trim();
        if (string.IsNullOrEmpty(normalized)) return [];

        var upper = normalized.ToUpperInvariant();
        return await _db.SaleOrders.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.OrganizationId == _tenantContext.OrganizationId && !o.IsDeleted
                     && o.CustomerPoReference != null && o.CustomerPoReference.ToUpper() == upper
                     && (excludeUuid == null || o.UUID != excludeUuid))
            .OrderBy(o => o.OrderDate).ThenBy(o => o.Id)
            .Select(o => new CustomerPoDuplicateModel
            {
                Uuid = o.UUID, SoNumber = o.SoNumber, PartnerId = o.PartnerId, Status = o.Status, OrderDate = o.OrderDate
            })
            .ToListAsync();
    }

    /// <summary>The generic attachment interface code a customer PO document is uploaded under (documentId = the order).</summary>
    internal const string CustomerPoInterfaceCode = "CUSTOMER_PO";
    internal const int CustomerPoReferenceMaxLength = 50;

    private static void EnsureDirectSourceType(string? sourceType)
    {
        if (string.IsNullOrWhiteSpace(sourceType)) return;
        if (!EnumCode<SaleOrderSourceType>.TryParse(sourceType.Trim().ToUpperInvariant(), out var parsed))
            throw new BadRequestException($"'{sourceType}' is not a sale order source. Use MANUAL.");
        switch (parsed)
        {
            case SaleOrderSourceType.Manual:
                return;
            case SaleOrderSourceType.FromQuotation:
                throw new BadRequestException(
                    "An order from a quotation is made by converting the accepted quotation (POST /api/sale-quotations/{id}/convert-to-order).");
            default:
                throw new BadRequestException($"Sale orders from {sourceType.Trim().ToUpperInvariant()} are not available yet.");
        }
    }

    // BR-C3-04 — free text, trimmed, at most 50; blank is none.
    private static string? NormalizeCustomerPo(string? reference)
    {
        var trimmed = reference?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Length > CustomerPoReferenceMaxLength)
            throw new BadRequestException(
                $"A customer PO reference can be at most {CustomerPoReferenceMaxLength} characters; this one has {trimmed.Length}.");
        return trimmed;
    }

    // §4.2 — "line_total = qty * price * (1 - disc%) * (1 + tax%)."
    private static decimal ComputeLineTotal(SaleOrderLine line) =>
        Math.Round(
            line.Quantity * line.UnitPrice * (1 - line.DiscountPercent / 100m) * (1 + line.TaxPercent / 100m),
            2, MidpointRounding.AwayFromZero);

    // §4.1 — "grand_total (= subtotal + tax - discount)." Subtotal/DiscountAmount/TaxAmount are
    // each the sum of the same three quantities every line's own total was built from, so the
    // header and the lines can never silently disagree with one another.
    private static void ApplyTotals(SaleOrder order)
    {
        decimal subtotal = 0, discount = 0, tax = 0;
        foreach (var line in order.Lines)
        {
            var base_ = line.Quantity * line.UnitPrice;
            var lineDiscount = base_ * line.DiscountPercent / 100m;
            var afterDiscount = base_ - lineDiscount;
            var lineTax = afterDiscount * line.TaxPercent / 100m;

            subtotal += base_;
            discount += lineDiscount;
            tax      += lineTax;
        }

        order.Subtotal       = Math.Round(subtotal, 2, MidpointRounding.AwayFromZero);
        order.DiscountAmount = Math.Round(discount, 2, MidpointRounding.AwayFromZero);
        order.TaxAmount      = Math.Round(tax, 2, MidpointRounding.AwayFromZero);
        order.GrandTotal     = order.Subtotal - order.DiscountAmount + order.TaxAmount;
    }

    private static SaleOrderModel ToModel(SaleOrder x, bool includeLines) => new()
    {
        Uuid                   = x.UUID,
        TraceId                = x.TraceId,
        SoNumber               = x.SoNumber,
        PartnerId              = x.PartnerId,
        OrderDate              = x.OrderDate,
        ExpectedDeliveryDate   = x.ExpectedDeliveryDate,
        CurrencyId             = x.CurrencyId,
        Subtotal               = x.Subtotal,
        TaxAmount              = x.TaxAmount,
        DiscountAmount         = x.DiscountAmount,
        GrandTotal             = x.GrandTotal,
        Status                 = x.Status,
        RequiresShipment       = x.RequiresShipment,
        DeliveryMode           = x.DeliveryMode,
        ShippingAddressId      = x.ShippingAddressId,
        IntimationDepartmentId = x.IntimationDepartmentId,
        Notes                  = x.Notes,
        CreatedDate             = x.CreatedDate,
        ModifiedDate            = x.ModifiedDate,
        SourceType               = x.SourceType,
        CustomerPoReference      = x.CustomerPoReference,
        CustomerPoDate           = x.CustomerPoDate,
        CustomerPoAttachmentUuid = x.CustomerPoAttachmentUuid,
        Lines = includeLines
            ? x.Lines.Select(l => new SaleOrderLineModel
              {
                  Uuid = l.UUID, VariantUuid = l.VariantUuid, Quantity = l.Quantity, UnitPrice = l.UnitPrice,
                  DiscountPercent = l.DiscountPercent, TaxPercent = l.TaxPercent,
                  TaxCodeUuid = l.TaxCodeUuid, TaxCode = l.TaxCode, LineTotal = l.LineTotal,
                  FulfilledQty = l.FulfilledQty, InvoicedQty = l.InvoicedQty, FulfillmentMode = l.FulfillmentMode,
                  AvailableQtyAtConfirm = l.AvailableQtyAtConfirm, DeficitQty = l.DeficitQty,
                  LinkedPoId = l.LinkedPoId, SelectedSupplierId = l.SelectedSupplierId,
                  Margin = l.Margin, MarginPercent = l.MarginPercent, Status = l.Status, Notes = l.Notes
              }).ToList()
            : []
    };
}
