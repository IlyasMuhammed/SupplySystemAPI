using Hangfire;
using Microsoft.EntityFrameworkCore;
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
internal sealed class SaleOrderService : ISaleOrderService
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

    // The resolver is optional the way InventoryLedgerService's master ledger is: production DI
    // always supplies it (Inventory registers it), and a line read without one simply carries no
    // description rather than failing.
    public SaleOrderService(
        DemandDbContext db, ITenantContext tenantContext, IOrganizationCurrencyService orgCurrency,
        IDocumentNumberGenerator numberGenerator, IPricingService pricing, IStockReservationService stock,
        ITimelineService timeline, IBackgroundJobClient jobs, IAvailabilityCheckService availabilityCheck,
        IPurchaseOrderService purchaseOrders, ISaleOrderEmailService emailService,
        IProductVariantResolver? variants = null)
    {
        _variants           = variants;
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

    public async Task<Guid> CreateAsync(CreateSaleOrderRequest req, int createdBy)
    {
        if (req.PartnerId == Guid.Empty)
            throw new BadRequestException("A sale order must be for a partner.");

        var config       = await ReadConfigAsync();
        var deliveryMode = ResolveDeliveryMode(req.DeliveryMode, config, existing: null);
        if (deliveryMode == DeliveryMode.Ship && req.ShippingAddressId is null)
            throw new BadRequestException("A shipping address is required when the delivery mode is SHIP.");
        if (req.Lines.Count == 0)
            throw new BadRequestException("A sale order needs at least one line.");

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
            CreatedBy              = createdBy,
            CreatedDate            = DateTime.UtcNow
        };

        var lineMode = DefaultLineMode(config, deliveryMode);
        foreach (var lineReq in req.Lines)
            order.Lines.Add(await BuildLineAsync(lineReq, req.PartnerId, orderDate, lineMode));

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
        var order = await _db.SaleOrders.Include(x => x.Lines).FirstOrDefaultAsync(x => x.UUID == uuid);
        if (order is null) return false;

        if (order.Status != EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Draft))
            throw new BadRequestException("Only a DRAFT sale order can be updated.");

        var config       = await ReadConfigAsync();
        var deliveryMode = ResolveDeliveryMode(req.DeliveryMode, config, existing: order.DeliveryMode);
        if (deliveryMode == DeliveryMode.Ship && req.ShippingAddressId is null)
            throw new BadRequestException("A shipping address is required when the delivery mode is SHIP.");
        if (req.Lines.Count == 0)
            throw new BadRequestException("A sale order needs at least one line.");

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
        order.ModifiedBy             = modifiedBy;
        order.ModifiedDate           = DateTime.UtcNow;

        // A DRAFT order's lines are wholesale replaced rather than diffed line-by-line — every
        // price on every line needs re-resolving against the (possibly changed) order date and
        // quantities anyway, so there is nothing an in-place per-line update would save.
        _db.SaleOrderLines.RemoveRange(order.Lines);
        order.Lines.Clear();
        var lineMode = DefaultLineMode(config, deliveryMode);
        foreach (var lineReq in req.Lines)
            order.Lines.Add(await BuildLineAsync(lineReq, order.PartnerId, order.OrderDate, lineMode));

        ApplyTotals(order);

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<SaleOrderModel?> GetByIdAsync(Guid uuid)
    {
        var order = await _db.SaleOrders.AsNoTracking()
            .Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.UUID == uuid);

        if (order is null) return null;

        var model = ToModel(order, includeLines: true);

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

        return model;
    }

    public async Task<PaginatedResponse<SaleOrderModel>> GetListAsync(SaleOrderListFilter filter)
    {
        var query = _db.SaleOrders.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Status))
            query = query.Where(x => x.Status == filter.Status);
        if (filter.PartnerId is { } partnerId)
            query = query.Where(x => x.PartnerId == partnerId);
        if (filter.OrderDateFrom is { } from)
            query = query.Where(x => x.OrderDate >= from.Date);
        if (filter.OrderDateTo is { } to)
            query = query.Where(x => x.OrderDate <= to.Date);
        if (!string.IsNullOrWhiteSpace(filter.Search))
            query = query.Where(x => x.SoNumber.Contains(filter.Search));

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
    public async Task<bool> ConfirmAsync(Guid uuid, int userId)
    {
        var order = await _db.SaleOrders.Include(x => x.Lines).FirstOrDefaultAsync(x => x.UUID == uuid);
        if (order is null) return false;

        if (order.Status != EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Draft))
            throw new BadRequestException("Only a DRAFT sale order can be confirmed.");

        // A draft taken while customer pickup was on must not be confirmed once it is off (§8.1).
        if (order.DeliveryMode == EnumCode<DeliveryMode>.Of(DeliveryMode.SelfPickup) && !(await ReadConfigAsync()).SelfPickupEnabled)
            throw new BadRequestException(
                "Customer pickup is switched off for this organization. Change the order to be shipped before confirming it.");

        var reservations = await _availabilityCheck.CheckAndReserveAsync(uuid, userId);

        order.Status       = EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Confirmed);
        order.ModifiedBy   = userId;
        order.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();

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

        return true;
    }

    // A29-P4-04 §4.5.
    public async Task<bool> CancelAsync(Guid uuid, int userId, string? reason)
    {
        var order = await _db.SaleOrders.Include(x => x.Lines).FirstOrDefaultAsync(x => x.UUID == uuid);
        if (order is null) return false;

        var cancelled = EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Cancelled);
        var closed    = EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Closed);
        var invoiced  = EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Invoiced);
        if (order.Status is var s && (s == cancelled || s == closed || s == invoiced))
            throw new BadRequestException($"A sale order already {order.Status} cannot be cancelled.");

        order.Status       = cancelled;
        order.ModifiedBy   = userId;
        order.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // §4.5 — "cancel (releases reservations)." A no-op for a DRAFT order that was never
        // confirmed: nothing was ever reserved under its UUID, and ReleaseBySourceAsync is
        // idempotent regardless.
        await _stock.ReleaseBySourceAsync(
            ReservationSourceType.SalesOrder, order.UUID,
            reason ?? "Sale order cancelled.", userId);

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

        // §4.5 — "enqueue cancellation email."
        _jobs.Enqueue<ISaleOrderEmailJob>(j => j.SendCancellationEmailAsync(order.UUID, reason, userId));

        return true;
    }

    public async Task<TimelineDetail?> GetTimelineAsync(Guid uuid)
    {
        var traceId = await _db.SaleOrders
            .Where(x => x.UUID == uuid)
            .Select(x => (Guid?)x.TraceId)
            .FirstOrDefaultAsync();

        return traceId is null ? null : await _timeline.GetTimelineDetailAsync(traceId.Value);
    }

    // A preview only — reads IStockReservationService.GetAvailableAsync, reserves nothing. What
    // §4.3's confirm would see if it ran right now.
    public async Task<IReadOnlyList<SaleOrderLineAvailabilityModel>?> GetAvailabilityAsync(Guid uuid)
    {
        var order = await _db.SaleOrders.AsNoTracking().Include(x => x.Lines)
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

    // §4.2 — "unit_price (resolved selling price)": never trust a client-supplied price, always
    // resolve it through the same §2.3 waterfall a real sale would use.
    private async Task<SaleOrderLine> BuildLineAsync(
        CreateSaleOrderLineRequest lineReq, Guid partnerId, DateTime orderDate, string? fulfillmentMode)
    {
        if (lineReq.VariantUuid == Guid.Empty)
            throw new BadRequestException("Every sale order line needs a variant.");
        if (lineReq.Quantity <= 0)
            throw new BadRequestException("Every sale order line's quantity must be greater than zero.");

        var resolution = await _pricing.ResolveSalePriceAsync(lineReq.VariantUuid, partnerId, lineReq.Quantity, orderDate);
        if (!resolution.Found || resolution.UnitPrice is not { } unitPrice)
            throw new BadRequestException(
                $"No sale price could be resolved for variant {lineReq.VariantUuid} — it has no selling price and no pricing rule applies.");

        var line = new SaleOrderLine
        {
            VariantUuid     = lineReq.VariantUuid,
            Quantity        = lineReq.Quantity,
            UnitPrice       = unitPrice,
            DiscountPercent = lineReq.DiscountPercent,
            TaxPercent      = lineReq.TaxPercent,
            FulfillmentMode = fulfillmentMode,
            Status          = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Open)
        };
        line.LineTotal = ComputeLineTotal(line);
        return line;
    }

    // The policy, or the defaults a new organization gets when nobody has opened it yet. Read without
    // creating the row: taking an order should not be what first writes the settings.
    private async Task<SaleOrderConfig> ReadConfigAsync() =>
        await _db.SaleOrderConfigs.AsNoTracking().FirstOrDefaultAsync() ?? new SaleOrderConfig();

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
        Lines = includeLines
            ? x.Lines.Select(l => new SaleOrderLineModel
              {
                  Uuid = l.UUID, VariantUuid = l.VariantUuid, Quantity = l.Quantity, UnitPrice = l.UnitPrice,
                  DiscountPercent = l.DiscountPercent, TaxPercent = l.TaxPercent, LineTotal = l.LineTotal,
                  FulfilledQty = l.FulfilledQty, InvoicedQty = l.InvoicedQty, FulfillmentMode = l.FulfillmentMode,
                  AvailableQtyAtConfirm = l.AvailableQtyAtConfirm, DeficitQty = l.DeficitQty,
                  LinkedPoId = l.LinkedPoId, SelectedSupplierId = l.SelectedSupplierId,
                  Margin = l.Margin, MarginPercent = l.MarginPercent, Status = l.Status, Notes = l.Notes
              }).ToList()
            : []
    };
}
