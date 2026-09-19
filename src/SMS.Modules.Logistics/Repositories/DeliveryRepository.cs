using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Domain.StateMachines;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Repositories;

internal interface IDeliveryRepository
{
    Task<Guid> CreateAsync(CreateDeliveryRequest req, int createdBy);
    Task<PaginatedResponse<DeliveryListItemModel>> GetListAsync(DeliveryFilter filter);
    /// <summary>Every delivery raised for one sale order, oldest first.</summary>
    Task<IReadOnlyList<DeliveryListItemModel>> GetForSaleOrderAsync(Guid saleOrderUuid);
    Task<DeliveryDetailModel?> GetByUuidAsync(Guid uuid);
    Task<bool> PatchAsync(Guid uuid, PatchDeliveryRequest req, int modifiedBy);
    Task<bool> DeleteAsync(Guid uuid);
}

internal sealed class DeliveryRepository : IDeliveryRepository
{
    /// <summary>
    /// Header edits are allowed while the delivery is still being planned. Once picking starts,
    /// warehouse staff are working to a printed pick list, and changing the document underneath
    /// them is how the paper and the system stop agreeing.
    /// </summary>
    private static readonly DeliveryStatus[] EditableStatuses =
        [DeliveryStatus.Draft, DeliveryStatus.Released];

    /// <summary>
    /// Only a draft can be deleted outright. Once released, stock is reserved against these exact
    /// lines, so withdrawing the delivery is a cancellation — a state transition with cleanup —
    /// not a delete. A cancelled delivery may then be tidied away.
    /// </summary>
    private static readonly DeliveryStatus[] DeletableStatuses =
        [DeliveryStatus.Draft, DeliveryStatus.Cancelled];

    private readonly LogisticsDbContext        _db;
    private readonly IDocumentNumberGenerator  _numbers;
    private readonly IAddressNormalizer        _addresses;

    public DeliveryRepository(
        LogisticsDbContext db,
        IDocumentNumberGenerator numbers,
        IAddressNormalizer addresses)
    {
        _db        = db;
        _numbers   = numbers;
        _addresses = addresses;
    }

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<Guid> CreateAsync(CreateDeliveryRequest req, int createdBy)
    {
        ArgumentNullException.ThrowIfNull(req);

        var sourceType = ParseSourceType(req.SourceType);
        var direction  = ResolveDirection(sourceType, req.Direction);

        // A warehouse transfer is the one source type with no document behind it, so this
        // endpoint is where it is created — and it is also the one that posts its own stock
        // movement, which makes getting both ends right load-bearing rather than cosmetic.
        if (sourceType == DeliverySourceType.Transfer)
        {
            if (req.ShipFromWarehouseUuid is null || req.ShipToWarehouseUuid is null)
                throw new BadRequestException(
                    "A transfer needs both a source and a destination warehouse — it posts a stock " +
                    "movement out of one and into the other.");

            if (req.ShipFromWarehouseUuid == req.ShipToWarehouseUuid)
                throw new BadRequestException(
                    "A transfer's source and destination warehouses must differ. Moving stock to " +
                    "where it already is would post two movements that cancel out.");
        }

        // A delivery whose lines are unknown is exactly the defect this module exists to fix —
        // the legacy Shipment recorded a weight and a PO number and could never say what was in
        // the box. The only rows allowed to have no lines are the backfilled ones (T-16).
        if (req.Lines.Count == 0)
            throw new BadRequestException(
                "A delivery must have at least one line. Without lines it cannot be picked, " +
                "packed, rated or received.");

        var now = DateTime.UtcNow;

        var delivery = new DeliveryOrder
        {
            UUID           = Guid.NewGuid(),
            // A manual delivery starts its own lineage; deliveries created from a source
            // document inherit that document's TraceId (T-11/T-12).
            TraceId        = Guid.NewGuid(),
            DeliveryNumber = await _numbers.NextAsync(DocumentNumberPrefix.Delivery, now),
            Direction      = LogisticsCode.Of(direction),
            SourceType     = LogisticsCode.Of(sourceType),
            SourceUuid     = req.SourceUuid,
            SourceNumber   = Trim(req.SourceNumber),
            ShipFromWarehouseUuid = req.ShipFromWarehouseUuid,
            ShipToWarehouseUuid   = req.ShipToWarehouseUuid,
            RequestedDate  = req.RequestedDate,
            PromisedDate   = req.PromisedDate,
            Priority       = LogisticsCode.Of(ParsePriority(req.Priority)),
            Incoterm       = Trim(req.Incoterm)?.ToUpperInvariant(),
            Status         = LogisticsCode.Of(DeliveryStatus.Draft),
            Notes          = Trim(req.Notes),
            IsActive       = true,
            CreatedBy      = createdBy,
            CreatedDate    = now
        };

        delivery.ShipFromAddress = await BuildAddressAsync(req.ShipFromAddress, createdBy, now);
        delivery.ShipToAddress   = await BuildAddressAsync(req.ShipToAddress, createdBy, now);

        var lineNo = 1;
        foreach (var line in req.Lines)
            delivery.Lines.Add(BuildLine(line, lineNo++, createdBy, now));

        _db.DeliveryOrders.Add(delivery);
        await _db.SaveChangesAsync();

        return delivery.UUID;
    }

    private static DeliveryOrderLine BuildLine(
        CreateDeliveryLineRequest req, int lineNo, int createdBy, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(req.ItemDescription))
            throw new BadRequestException($"Line {lineNo}: an item description is required.");

        if (req.QtyOrdered <= 0)
            throw new BadRequestException(
                $"Line {lineNo}: quantity must be greater than zero. A line for nothing cannot be picked.");

        return new DeliveryOrderLine
        {
            UUID                    = Guid.NewGuid(),
            LineNo                  = lineNo,
            VariantUuid             = req.VariantUuid,
            ProductUuid             = req.ProductUuid,
            ItemDescription         = req.ItemDescription.Trim(),
            UnitOfMeasure           = Trim(req.UnitOfMeasure),
            QtyOrdered              = req.QtyOrdered,
            BatchNumber             = Trim(req.BatchNumber),
            SerialNumber            = Trim(req.SerialNumber),
            SourceLineUuid          = req.SourceLineUuid,
            BinUuid                 = req.BinUuid,
            UnitValue               = req.UnitValue,
            IsHazardous             = req.IsHazardous,
            IsFragile               = req.IsFragile,
            IsTemperatureControlled = req.IsTemperatureControlled,
            CreatedBy               = createdBy,
            CreatedDate             = now
        };
    }

    private async Task<Address?> BuildAddressAsync(AddressRequest? req, int createdBy, DateTime now)
    {
        if (req is null) return null;

        var address = new Address
        {
            UUID           = Guid.NewGuid(),
            Line1          = req.Line1,
            Line2          = req.Line2,
            CityId         = req.CityId,
            CityName       = req.CityName,
            State          = req.State,
            PostalCode     = req.PostalCode,
            CountryName    = req.CountryName,
            CountryIsoCode = req.CountryIsoCode,
            ContactName    = req.ContactName,
            ContactPhone   = req.ContactPhone,
            ContactEmail   = req.ContactEmail,
            Latitude       = req.Latitude,
            Longitude      = req.Longitude,
            ConsigneeUuid  = req.ConsigneeUuid,
            CreatedBy      = createdBy,
            CreatedDate    = now
        };

        if (!string.IsNullOrWhiteSpace(req.AddressType))
            address.AddressType = LogisticsCode.Of(
                LogisticsCode.TryParse<AddressType>(req.AddressType, out var type)
                    ? type
                    : throw new BadRequestException(
                        $"'{req.AddressType}' is not a valid address type. Valid values: " +
                        $"{string.Join(", ", LogisticsCode.Codes<AddressType>())}."));

        await _addresses.NormalizeAsync(address);
        return address;
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<DeliveryListItemModel>> GetListAsync(DeliveryFilter filter)
    {
        var q = _db.DeliveryOrders.Where(x => !x.IsDelete);

        if (!string.IsNullOrWhiteSpace(filter.Status))
            q = q.Where(x => x.Status == filter.Status);

        if (!string.IsNullOrWhiteSpace(filter.Direction))
            q = q.Where(x => x.Direction == filter.Direction);

        if (!string.IsNullOrWhiteSpace(filter.SourceType))
            q = q.Where(x => x.SourceType == filter.SourceType);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim().ToLower();
            q = q.Where(x => x.DeliveryNumber.ToLower().Contains(s)
                          || (x.SourceNumber != null && x.SourceNumber.ToLower().Contains(s)));
        }

        if (filter.FromDate.HasValue) q = q.Where(x => x.CreatedDate >= filter.FromDate.Value);
        if (filter.ToDate.HasValue)   q = q.Where(x => x.CreatedDate <= filter.ToDate.Value);

        var page     = filter.Page     < 1 ? 1  : filter.Page;
        var pageSize = filter.PageSize < 1 ? 20 : filter.PageSize;

        var total = await q.CountAsync();

        var data = await q
            .OrderByDescending(x => x.CreatedDate)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(ToListItem)
            .ToListAsync();

        return new PaginatedResponse<DeliveryListItemModel>
        {
            Data         = data,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    public async Task<IReadOnlyList<DeliveryListItemModel>> GetForSaleOrderAsync(Guid saleOrderUuid) =>
        await _db.DeliveryOrders
            .Where(x => x.SaleOrderUuid == saleOrderUuid && !x.IsDelete)
            // Oldest first: the order's fulfilment reads as a history — first shipment, second,
            // the collection — rather than as a cockpit of what is newest.
            .OrderBy(x => x.CreatedDate)
            .ThenBy(x => x.Id)
            .Select(ToListItem)
            .ToListAsync();

    private static readonly System.Linq.Expressions.Expression<Func<DeliveryOrder, DeliveryListItemModel>> ToListItem =
        x => new DeliveryListItemModel
        {
            UUID           = x.UUID,
            DeliveryNumber = x.DeliveryNumber,
            Direction      = x.Direction,
            SourceType     = x.SourceType,
            SourceNumber   = x.SourceNumber,
            DeliveryMode   = x.DeliveryMode,
            Status         = x.Status,
            Priority       = x.Priority,
            RequestedDate  = x.RequestedDate,
            PromisedDate   = x.PromisedDate,
            ShipToCity     = x.ShipToAddress != null ? x.ShipToAddress.CityName : null,
            LineCount      = x.Lines.Count,
            LinesUnknown   = x.LinesUnknown,
            CreatedDate    = x.CreatedDate
        };

    public async Task<DeliveryDetailModel?> GetByUuidAsync(Guid uuid)
    {
        var delivery = await _db.DeliveryOrders
            .Include(x => x.Lines)
            .Include(x => x.ShipFromAddress)
            .Include(x => x.ShipToAddress)
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.UUID == uuid && !x.IsDelete);

        if (delivery is null) return null;

        var status = LogisticsCode.Parse<DeliveryStatus>(delivery.Status);

        return new DeliveryDetailModel
        {
            UUID             = delivery.UUID,
            DeliveryNumber   = delivery.DeliveryNumber,
            TraceId          = delivery.TraceId,
            Direction        = delivery.Direction,
            SourceType       = delivery.SourceType,
            SourceUuid       = delivery.SourceUuid,
            SourceNumber     = delivery.SourceNumber,
            PostsGoodsIssue  = delivery.PostsGoodsIssue,
            SaleOrderUuid    = delivery.SaleOrderUuid,
            DeliveryMode     = delivery.DeliveryMode,
            PickupPersonName     = delivery.PickupPersonName,
            PickupPersonIdType   = delivery.PickupPersonIdType,
            PickupPersonIdNumber = delivery.PickupPersonIdNumber,
            PickupAuthorization  = delivery.PickupAuthorization,
            PickedUpAt           = delivery.PickedUpAt,
            ShipFromAddress  = ToAddressModel(delivery.ShipFromAddress),
            ShipToAddress    = ToAddressModel(delivery.ShipToAddress),
            RequestedDate    = delivery.RequestedDate,
            PromisedDate     = delivery.PromisedDate,
            Priority         = delivery.Priority,
            Incoterm         = delivery.Incoterm,
            Status           = delivery.Status,
            StatusBeforeHold = delivery.StatusBeforeHold,
            HoldReason       = delivery.HoldReason,
            LinesUnknown     = delivery.LinesUnknown,
            Notes            = delivery.Notes,
            // Straight from the state machine, so the UI never has to hardcode which buttons are
            // enabled and cannot drift from what the server will actually accept.
            AllowedNextStatuses = [.. DeliveryStateMachine.Instance.From(status).Select(LogisticsCode.Of)],
            CreatedDate      = delivery.CreatedDate,
            ModifiedDate     = delivery.ModifiedDate,
            Lines            = [.. delivery.Lines.OrderBy(l => l.LineNo).Select(ToLineModel)]
        };
    }

    // ── Update ────────────────────────────────────────────────────────────────

    public async Task<bool> PatchAsync(Guid uuid, PatchDeliveryRequest req, int modifiedBy)
    {
        ArgumentNullException.ThrowIfNull(req);

        var delivery = await _db.DeliveryOrders
            .Include(x => x.ShipToAddress)
            .FirstOrDefaultAsync(x => x.UUID == uuid && !x.IsDelete);

        if (delivery is null) return false;

        EnsureEditable(delivery);

        if (req.RequestedDate.HasValue) delivery.RequestedDate = req.RequestedDate;
        if (req.PromisedDate.HasValue)  delivery.PromisedDate  = req.PromisedDate;
        if (req.Notes is not null)      delivery.Notes         = Trim(req.Notes);
        if (req.Incoterm is not null)   delivery.Incoterm      = Trim(req.Incoterm)?.ToUpperInvariant();

        if (req.Priority is not null)
            delivery.Priority = LogisticsCode.Of(ParsePriority(req.Priority));

        if (req.ShipToAddress is not null)
        {
            // A new snapshot rather than an edit in place: the old address may already be
            // printed on a document, and rewriting it would rewrite history.
            delivery.ShipToAddress = await BuildAddressAsync(req.ShipToAddress, modifiedBy, DateTime.UtcNow);
        }

        delivery.ModifiedBy   = modifiedBy;
        delivery.ModifiedDate = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(Guid uuid)
    {
        var delivery = await _db.DeliveryOrders.FirstOrDefaultAsync(x => x.UUID == uuid && !x.IsDelete);
        if (delivery is null) return false;

        var status = LogisticsCode.Parse<DeliveryStatus>(delivery.Status);

        if (!DeletableStatuses.Contains(status))
            throw new ConflictException(
                $"A delivery in {delivery.Status} cannot be deleted. Cancel it instead — stock may " +
                "be reserved against its lines, and that reservation has to be released.");

        delivery.IsDelete = true;
        delivery.IsActive = false;
        await _db.SaveChangesAsync();
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void EnsureEditable(DeliveryOrder delivery)
    {
        var status = LogisticsCode.Parse<DeliveryStatus>(delivery.Status);

        if (!EditableStatuses.Contains(status))
            throw new ConflictException(
                $"A delivery in {delivery.Status} can no longer be edited. Editable statuses are: " +
                $"{string.Join(", ", EditableStatuses.Select(LogisticsCode.Of))}.");
    }

    private static DeliverySourceType ParseSourceType(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return DeliverySourceType.Manual;

        return LogisticsCode.TryParse<DeliverySourceType>(code, out var sourceType)
            ? sourceType
            : throw new BadRequestException(
                $"'{code}' is not a valid source type. Valid values: " +
                $"{string.Join(", ", LogisticsCode.Codes<DeliverySourceType>())}.");
    }

    private static DeliveryPriority ParsePriority(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return DeliveryPriority.Normal;

        return LogisticsCode.TryParse<DeliveryPriority>(code, out var priority)
            ? priority
            : throw new BadRequestException(
                $"'{code}' is not a valid priority. Valid values: " +
                $"{string.Join(", ", LogisticsCode.Codes<DeliveryPriority>())}.");
    }

    private static DeliveryDirection ResolveDirection(DeliverySourceType sourceType, string? requested)
    {
        var hasDefault = DeliverySourceTypeInfo.TryGetDefaultDirection(sourceType, out var implied);

        if (string.IsNullOrWhiteSpace(requested))
            return hasDefault
                ? implied
                : throw new BadRequestException(
                    $"A {LogisticsCode.Of(sourceType)} delivery does not imply a direction, so one " +
                    $"must be given. Valid values: {string.Join(", ", LogisticsCode.Codes<DeliveryDirection>())}.");

        if (!LogisticsCode.TryParse<DeliveryDirection>(requested, out var direction))
            throw new BadRequestException(
                $"'{requested}' is not a valid direction. Valid values: " +
                $"{string.Join(", ", LogisticsCode.Codes<DeliveryDirection>())}.");

        // Contradicting the source document is a mistake, not an override — a PO delivery that
        // claims to be outbound would point the stock movement the wrong way.
        if (hasDefault && direction != implied)
            throw new BadRequestException(
                $"A {LogisticsCode.Of(sourceType)} delivery is always {LogisticsCode.Of(implied)}, " +
                $"but {LogisticsCode.Of(direction)} was given.");

        return direction;
    }

    private static DeliveryLineModel ToLineModel(DeliveryOrderLine l) => new()
    {
        UUID                    = l.UUID,
        LineNo                  = l.LineNo,
        VariantUuid             = l.VariantUuid,
        ProductUuid             = l.ProductUuid,
        ItemDescription         = l.ItemDescription,
        UnitOfMeasure           = l.UnitOfMeasure,
        QtyOrdered              = l.QtyOrdered,
        QtyPicked               = l.QtyPicked,
        QtyPacked               = l.QtyPacked,
        QtyShipped              = l.QtyShipped,
        QtyDelivered            = l.QtyDelivered,
        QtyShort                = l.QtyShort,
        ShortReason             = l.ShortReason,
        BatchNumber             = l.BatchNumber,
        SerialNumber            = l.SerialNumber,
        SourceLineUuid          = l.SourceLineUuid,
        SoLineUuid              = l.SoLineUuid,
        UnitValue               = l.UnitValue,
        IsHazardous             = l.IsHazardous,
        IsFragile               = l.IsFragile,
        IsTemperatureControlled = l.IsTemperatureControlled
    };

    private static AddressModel? ToAddressModel(Address? a) => a is null ? null : new AddressModel
    {
        UUID             = a.UUID,
        Line1            = a.Line1,
        Line2            = a.Line2,
        CityId           = a.CityId,
        CityName         = a.CityName,
        State            = a.State,
        PostalCode       = a.PostalCode,
        CountryName      = a.CountryName,
        CountryIsoCode   = a.CountryIsoCode,
        ContactName      = a.ContactName,
        ContactPhone     = a.ContactPhone,
        ContactPhoneE164 = a.ContactPhoneE164,
        ContactEmail     = a.ContactEmail,
        AddressType      = a.AddressType,
        ValidationStatus = a.ValidationStatus,
        ValidationNotes  = a.ValidationNotes
    };

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
