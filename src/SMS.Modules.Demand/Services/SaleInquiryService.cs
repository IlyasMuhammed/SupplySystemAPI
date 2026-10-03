using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Demand.Services;

/// <summary>
/// A32 C1 — Sale Inquiry (PB-03 numbering, PB-04 state machine, PB-05 line evaluation, PB-06 CRUD).
/// See docs/sales-preorder/API-CONTRACT.md §4.
/// <para>
/// <b>Tenancy:</b> every lookup filters explicitly on the caller's own organization — the EF tenant filter is
/// bypassed for super admins, so relying on it would let one organization read or change another's inquiry.
/// The list is own-organization too.
/// </para>
/// <para>
/// <b>Concurrency:</b> <c>SaleInquiry.Status</c> is a concurrency token, and every write here — line writes
/// included — touches the header (<c>ModifiedBy/ModifiedDate</c>), so each save carries
/// <c>WHERE Status = @original</c>. A line edit that races a status change (decline, create-quotation) loses
/// with a 409 instead of landing on an inquiry that is no longer editable.
/// </para>
/// <para>
/// <b>REVIEW_COMPLETE means every line is decided</b> (CAN_SUPPLY, PARTIAL or CANNOT_SUPPLY — BR-C1-03 read
/// with the §3.4 diagram's "all lines evaluated"): an UNDER_REVIEW line blocks it like a PENDING one, and an edit
/// that leaves an undecided line, or no line at all, moves a REVIEW_COMPLETE inquiry back to UNDER_REVIEW.
/// The quotation built from a REVIEW_COMPLETE inquiry can therefore rely on it.
/// </para>
/// </summary>
internal sealed class SaleInquiryService : ISaleInquiryService
{
    /// <summary>BR-C1-02 — INQ-YYYY-NNNNN via the shared per-organization, per-year counter.</summary>
    internal const string NumberPrefix = "INQ";

    private static readonly string Received       = EnumCode<SaleInquiryStatus>.Of(SaleInquiryStatus.Received);
    private static readonly string UnderReview    = EnumCode<SaleInquiryStatus>.Of(SaleInquiryStatus.UnderReview);
    private static readonly string ReviewComplete = EnumCode<SaleInquiryStatus>.Of(SaleInquiryStatus.ReviewComplete);
    private static readonly string Quoted         = EnumCode<SaleInquiryStatus>.Of(SaleInquiryStatus.Quoted);
    private static readonly string Declined       = EnumCode<SaleInquiryStatus>.Of(SaleInquiryStatus.Declined);

    private static readonly string LinePending     = EnumCode<SaleInquiryLineStatus>.Of(SaleInquiryLineStatus.Pending);
    private static readonly string LineUnderReview = EnumCode<SaleInquiryLineStatus>.Of(SaleInquiryLineStatus.UnderReview);

    // Column widths from SalesPreOrderMaps — an over-long value is a 400 here, not a truncation error from SQL.
    private const int ReferenceMax        = 50;
    private const int NotesMax            = 2000;
    private const int DeclineReasonMax    = 500;
    private const int DescriptionMax      = 500;
    private const int UomMax              = 20;
    private const int LineNotesMax        = 1000;
    private const int RejectionNotesMax   = 500;
    private const int AlternativeNotesMax = 500;

    private readonly DemandDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IDocumentNumberGenerator _numbers;
    private readonly IPartnerRoleLookup _partners;
    private readonly IProductVariantResolver? _variants;
    private readonly IUserQueryService? _users;
    private readonly ISupplierNameLookupService? _partnerNames;

    // The last three are optional like SaleOrderService's catalog resolver: production DI always supplies them
    // (Inventory, Auth and Suppliers register them); without one, variants go unchecked and names stay empty.
    public SaleInquiryService(
        DemandDbContext db, ITenantContext tenant, IDocumentNumberGenerator numbers, IPartnerRoleLookup partners,
        IProductVariantResolver? variants = null, IUserQueryService? users = null,
        ISupplierNameLookupService? partnerNames = null)
    {
        _db           = db;
        _tenant       = tenant;
        _numbers      = numbers;
        _partners     = partners;
        _variants     = variants;
        _users        = users;
        _partnerNames = partnerNames;
    }

    private Guid Org => _tenant.OrganizationId;

    // ── Create / update / read ──────────────────────────────────────────────────

    public async Task<Guid> CreateAsync(CreateSaleInquiryRequest req, int userId)
    {
        ArgumentNullException.ThrowIfNull(req);
        await EnsureCustomerAsync(req.PartnerId);
        await EnsureAssigneeAsync(req.AssignedToUserId);

        var receivedDate = req.ReceivedDate?.Date ?? DateTime.UtcNow.Date;
        var inquiry = new SaleInquiry
        {
            OrganizationId = Org,
            PartnerId      = req.PartnerId,
            Status         = Received,
            CreatedBy      = userId,
            CreatedDate    = DateTime.UtcNow
        };
        ApplyHeader(inquiry, req.CustomerReference, req.CustomerReferenceDate, receivedDate, req.ResponseDeadline,
            req.AssignedToUserId, req.Notes);

        var lineNumber = 0;
        foreach (var lineReq in req.Lines ?? [])
        {
            var line = new SaleInquiryLine { OrganizationId = Org, LineNumber = ++lineNumber };
            await ApplyLineRequestAsync(line, lineReq);
            inquiry.Lines.Add(line);
        }

        // Drawn last: a number is consumed even when nothing is saved, so every check comes first.
        inquiry.InquiryNumber = await _numbers.NextAsync(NumberPrefix, receivedDate, Org);

        _db.SaleInquiries.Add(inquiry);
        await _db.SaveChangesAsync();
        return inquiry.UUID;
    }

    public async Task<bool> UpdateAsync(Guid uuid, UpdateSaleInquiryRequest req, int userId)
    {
        ArgumentNullException.ThrowIfNull(req);
        var inquiry = await LoadOwnAsync(uuid);
        if (inquiry is null) return false;
        EnsureEditable(inquiry);

        if (req.ReceivedDate == default)
            throw new BadRequestException("The received date is required.");
        // Only a newly picked assignee is checked — one assigned earlier and since deactivated stays as it is.
        if (req.AssignedToUserId != inquiry.AssignedToUserId)
            await EnsureAssigneeAsync(req.AssignedToUserId);

        ApplyHeader(inquiry, req.CustomerReference, req.CustomerReferenceDate, req.ReceivedDate, req.ResponseDeadline,
            req.AssignedToUserId, req.Notes);
        Touch(inquiry, userId);

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<SaleInquiryModel?> GetByIdAsync(Guid uuid)
    {
        var org = Org;
        var inquiry = await _db.SaleInquiries.AsNoTracking()
            .Include(x => x.Lines).ThenInclude(l => l.RejectionReason)
            .FirstOrDefaultAsync(x => x.UUID == uuid && x.OrganizationId == org);
        if (inquiry is null) return null;

        var lines = inquiry.Lines.OrderBy(l => l.LineNumber).ToList();

        var variantIds = lines.SelectMany(l => new[] { l.VariantUuid, l.AlternativeVariantUuid })
            .OfType<Guid>().Distinct().ToList();
        var variants = _variants is not null && variantIds.Count > 0
            ? await _variants.DescribeVariantsAsync(variantIds)
            : new Dictionary<Guid, VariantDescription>();

        var userIds = lines.Select(l => l.ReviewedByUserId).Append(inquiry.AssignedToUserId)
            .OfType<int>().Distinct().ToList();
        var users = await UserNamesAsync(userIds);

        var partner = await _partners.GetAsync(inquiry.PartnerId);

        var quotations = await _db.SaleQuotations.AsNoTracking()
            .Where(q => q.SourceInquiryId == inquiry.Id && q.OrganizationId == org)
            .OrderBy(q => q.Id)
            .Select(q => new SalesDocumentLinkModel { Uuid = q.UUID, Number = q.QuotationNumber, Status = q.Status })
            .ToListAsync();

        return new SaleInquiryModel
        {
            Uuid                  = inquiry.UUID,
            TraceId               = inquiry.TraceId,
            InquiryNumber         = inquiry.InquiryNumber,
            PartnerId             = inquiry.PartnerId,
            PartnerName           = partner?.Name,
            CustomerReference     = inquiry.CustomerReference,
            CustomerReferenceDate = inquiry.CustomerReferenceDate,
            Status                = inquiry.Status,
            ReceivedDate          = inquiry.ReceivedDate,
            ResponseDeadline      = inquiry.ResponseDeadline,
            AssignedToUserId      = inquiry.AssignedToUserId,
            AssignedToUserName    = NameOf(users, inquiry.AssignedToUserId),
            Notes                 = inquiry.Notes,
            DeclineReason         = inquiry.DeclineReason,
            CreatedBy             = inquiry.CreatedBy,
            CreatedDate           = inquiry.CreatedDate,
            ModifiedDate          = inquiry.ModifiedDate,
            AllowedNextStatuses   = AllowedNextStatuses(inquiry.Status, lines),
            IsEditable            = IsEditable(inquiry.Status),
            Lines                 = lines.Select(l => ToLineModel(l, variants, users)).ToList(),
            Quotations            = quotations
        };
    }

    public async Task<PaginatedResponse<SaleInquiryListItemModel>> GetListAsync(SaleInquiryListFilter filter)
    {
        filter ??= new SaleInquiryListFilter();
        var org = Org;
        var query = _db.SaleInquiries.AsNoTracking().Where(x => x.OrganizationId == org);

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            var status = filter.Status.Trim().ToUpperInvariant();
            query = query.Where(x => x.Status == status);
        }
        if (filter.PartnerId is { } partnerId)
            query = query.Where(x => x.PartnerId == partnerId);
        if (filter.ReceivedFrom is { } from)
        {
            var fromDate = from.Date;
            query = query.Where(x => x.ReceivedDate >= fromDate);
        }
        if (filter.ReceivedTo is { } to)
        {
            var toDate = to.Date;
            query = query.Where(x => x.ReceivedDate <= toDate);
        }
        if (filter.AssignedToUserId is { } assignee)
            query = query.Where(x => x.AssignedToUserId == assignee);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            query = query.Where(x => x.InquiryNumber.Contains(term)
                                  || (x.CustomerReference != null && x.CustomerReference.Contains(term)));
        }

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var pending     = LinePending;
        var underReview = LineUnderReview;
        var rows = await query
            .OrderByDescending(x => x.ReceivedDate).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new SaleInquiryListItemModel
            {
                Uuid              = x.UUID,
                InquiryNumber     = x.InquiryNumber,
                PartnerId         = x.PartnerId,
                CustomerReference = x.CustomerReference,
                ReceivedDate      = x.ReceivedDate,
                ResponseDeadline  = x.ResponseDeadline,
                Status            = x.Status,
                AssignedToUserId  = x.AssignedToUserId,
                LineCount         = x.Lines.Count(),
                // What stands between UNDER_REVIEW and REVIEW_COMPLETE: PENDING and UNDER_REVIEW lines.
                PendingLineCount  = x.Lines.Count(l => l.LineStatus == pending || l.LineStatus == underReview),
                CreatedDate       = x.CreatedDate
            })
            .ToListAsync();

        if (rows.Count > 0)
        {
            var partnerIds = rows.Select(r => r.PartnerId).Distinct().ToList();
            var partnerNames = _partnerNames is not null
                ? await _partnerNames.GetNamesAsync(partnerIds)
                : new Dictionary<Guid, string>();
            var users = await UserNamesAsync(rows.Select(r => r.AssignedToUserId).OfType<int>().Distinct().ToList());

            foreach (var row in rows)
            {
                row.PartnerName        = partnerNames.TryGetValue(row.PartnerId, out var name) ? name : null;
                row.AssignedToUserName = NameOf(users, row.AssignedToUserId);
            }
        }

        return new PaginatedResponse<SaleInquiryListItemModel>
        {
            Data         = rows,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    // ── Lines ───────────────────────────────────────────────────────────────────

    public async Task<Guid?> AddLineAsync(Guid uuid, SaleInquiryLineRequest req, int userId)
    {
        ArgumentNullException.ThrowIfNull(req);
        var inquiry = await LoadOwnAsync(uuid);
        if (inquiry is null) return null;
        EnsureEditable(inquiry);

        var line = new SaleInquiryLine
        {
            OrganizationId = Org,
            // Max + 1, never a reused number: numbers of deleted lines stay retired.
            LineNumber     = (inquiry.Lines.Count == 0 ? 0 : inquiry.Lines.Max(l => l.LineNumber)) + 1
        };
        await ApplyLineRequestAsync(line, req);
        inquiry.Lines.Add(line);

        Touch(inquiry, userId);
        ReopenReviewIfUndecided(inquiry);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException
                                           && ex.InnerException?.Message.Contains(nameof(SaleInquiryLine.LineNumber)) == true)
        {
            // Two lines added at the same moment computed the same number; the unique (SaleInquiryId,
            // LineNumber) index let only one in.
            throw new ConflictException("Another line was added to this inquiry at the same moment. Reload it and try again.");
        }
        return line.UUID;
    }

    public async Task<bool> UpdateLineAsync(Guid uuid, Guid lineUuid, UpdateSaleInquiryLineRequest req, int userId)
    {
        ArgumentNullException.ThrowIfNull(req);
        var inquiry = await LoadOwnAsync(uuid);
        var line = inquiry?.Lines.FirstOrDefault(l => l.UUID == lineUuid);
        if (inquiry is null || line is null) return false;
        EnsureEditable(inquiry);

        // The request fields first — PARTIAL is checked against the requested quantity this same call sets.
        await ApplyLineRequestAsync(line, req);
        await ApplyEvaluationAsync(line, req, userId);
        line.ModifiedDate = DateTime.UtcNow;

        Touch(inquiry, userId);
        ReopenReviewIfUndecided(inquiry);

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteLineAsync(Guid uuid, Guid lineUuid, int userId)
    {
        var inquiry = await LoadOwnAsync(uuid);
        var line = inquiry?.Lines.FirstOrDefault(l => l.UUID == lineUuid);
        if (inquiry is null || line is null) return false;
        EnsureEditable(inquiry);

        inquiry.Lines.Remove(line);
        _db.SaleInquiryLines.Remove(line);

        Touch(inquiry, userId);
        ReopenReviewIfUndecided(inquiry);

        await _db.SaveChangesAsync();
        return true;
    }

    // ── State machine ───────────────────────────────────────────────────────────

    public async Task<SaleInquiryModel?> ChangeStatusAsync(Guid uuid, ChangeSaleInquiryStatusRequest req, int userId)
    {
        ArgumentNullException.ThrowIfNull(req);
        var inquiry = await LoadOwnAsync(uuid);
        if (inquiry is null) return null;

        var code = req.Status?.Trim().ToUpperInvariant();
        if (!EnumCode<SaleInquiryStatus>.TryParse(code, out var target))
            throw new BadRequestException(
                $"'{req.Status}' is not an inquiry status. Use UNDER_REVIEW, REVIEW_COMPLETE or DECLINED.");
        if (target == SaleInquiryStatus.Quoted)
            throw new BadRequestException(
                "An inquiry becomes QUOTED when a quotation is created from it (create-quotation), not by a status change.");
        EnsureEditable(inquiry);

        var from = inquiry.Status;
        var to   = EnumCode<SaleInquiryStatus>.Of(target);

        switch (target)
        {
            case SaleInquiryStatus.UnderReview when from == Received:
                if (inquiry.Lines.Count == 0)
                    throw new BadRequestException("Add at least one line before starting the review.");
                break;

            case SaleInquiryStatus.ReviewComplete when from == UnderReview:
                if (inquiry.Lines.Count == 0)
                    throw new BadRequestException("An inquiry with no lines cannot complete its review.");
                var undecided = inquiry.Lines.Count(l => IsUndecided(l.LineStatus));
                if (undecided > 0)
                    throw new BadRequestException(
                        $"Every line must be evaluated (CAN_SUPPLY, PARTIAL or CANNOT_SUPPLY) before the review is " +
                        $"complete — {undecided} line(s) are still PENDING or UNDER_REVIEW.");
                break;

            case SaleInquiryStatus.Declined when from == UnderReview || from == ReviewComplete:
                var reason = Text(req.Reason, DeclineReasonMax, "The decline reason");
                if (reason is null)
                    throw new BadRequestException("Declining an inquiry needs a reason.");
                inquiry.DeclineReason = reason;
                break;

            default:
                throw new BadRequestException($"An inquiry cannot move from {from} to {to}.");
        }

        inquiry.Status = to;
        Touch(inquiry, userId);
        await _db.SaveChangesAsync();

        return await GetByIdAsync(uuid);
    }

    public async Task MarkQuotedAsync(Guid inquiryUuid, int userId)
    {
        var org = Org;
        // Tracked, so the caller's own tracked copy (if it loaded one) is the instance changed here.
        var inquiry = await _db.SaleInquiries.FirstOrDefaultAsync(x => x.UUID == inquiryUuid && x.OrganizationId == org)
            ?? throw new NotFoundException("Sale inquiry", inquiryUuid);

        if (inquiry.Status != ReviewComplete)
            throw new ConflictException(
                $"Inquiry {inquiry.InquiryNumber} is {inquiry.Status} — only a REVIEW_COMPLETE inquiry can be quoted.");

        inquiry.Status = Quoted;
        Touch(inquiry, userId);
        // No save: the caller's one SaveChangesAsync commits the quotation and this transition together.
    }

    // ── Rules ───────────────────────────────────────────────────────────────────

    private Task<SaleInquiry?> LoadOwnAsync(Guid uuid)
    {
        var org = Org;
        return _db.SaleInquiries.Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.UUID == uuid && x.OrganizationId == org);
    }

    private static bool IsEditable(string status) =>
        status == Received || status == UnderReview || status == ReviewComplete;

    private static bool IsUndecided(string lineStatus) =>
        lineStatus == LinePending || lineStatus == LineUnderReview;

    /// <summary>BR-C1-06 — QUOTED and DECLINED inquiries are read-only.</summary>
    private static void EnsureEditable(SaleInquiry inquiry)
    {
        if (!IsEditable(inquiry.Status))
            throw new BadRequestException(
                $"Inquiry {inquiry.InquiryNumber} is {inquiry.Status} and read-only — its header and lines can no longer change.");
    }

    private static void ReopenReviewIfUndecided(SaleInquiry inquiry)
    {
        if (inquiry.Status == ReviewComplete
            && (inquiry.Lines.Count == 0 || inquiry.Lines.Any(l => IsUndecided(l.LineStatus))))
            inquiry.Status = UnderReview;
    }

    private static List<string> AllowedNextStatuses(string status, IReadOnlyCollection<SaleInquiryLine> lines)
    {
        var next = new List<string>();
        if (status == Received)
        {
            if (lines.Count > 0) next.Add(UnderReview);
        }
        else if (status == UnderReview)
        {
            if (lines.Count > 0 && !lines.Any(l => IsUndecided(l.LineStatus))) next.Add(ReviewComplete);
            next.Add(Declined);
        }
        else if (status == ReviewComplete)
        {
            next.Add(Declined);
        }
        return next;
    }

    private static void Touch(SaleInquiry inquiry, int userId)
    {
        inquiry.ModifiedBy   = userId;
        inquiry.ModifiedDate = DateTime.UtcNow;
    }

    /// <summary>BR-C1-01 — an active business partner flagged as a customer, in the caller's organization.</summary>
    private async Task EnsureCustomerAsync(Guid partnerId)
    {
        if (partnerId == Guid.Empty)
            throw new BadRequestException("An inquiry must name the customer it came from.");

        var partner = await _partners.GetAsync(partnerId)
            ?? throw new BadRequestException("The customer was not found in this organization.");
        if (!partner.IsCustomer)
            throw new BadRequestException(
                $"{partner.Name} is not a customer — an inquiry can only be raised for a business partner flagged as a customer.");
        if (!partner.IsActive)
            throw new BadRequestException($"The customer {partner.Name} is inactive.");
    }

    private async Task EnsureAssigneeAsync(int? userId)
    {
        if (userId is null || _users is null) return;
        var found = await _users.GetUsersAsync([userId.Value]);
        if (found.Count == 0)
            throw new BadRequestException("The assigned user was not found or is inactive.");
    }

    private static void ApplyHeader(SaleInquiry inquiry, string? customerReference, DateTime? customerReferenceDate,
        DateTime receivedDate, DateTime? responseDeadline, int? assignedToUserId, string? notes)
    {
        var reference = Text(customerReference, ReferenceMax, "The customer reference");
        var text      = Text(notes, NotesMax, "Notes");

        inquiry.CustomerReference     = reference;
        inquiry.CustomerReferenceDate = customerReferenceDate?.Date;
        inquiry.ReceivedDate          = receivedDate.Date;
        inquiry.ResponseDeadline      = responseDeadline?.Date;
        inquiry.AssignedToUserId      = assignedToUserId;
        inquiry.Notes                 = text;
    }

    /// <summary>What the customer asked for. Validates everything before changing the line.</summary>
    private async Task ApplyLineRequestAsync(SaleInquiryLine line, SaleInquiryLineRequest req)
    {
        var description = Text(req.ProductDescription, DescriptionMax, "The product description")
            ?? throw new BadRequestException("Every inquiry line needs a description of what the customer asked for.");
        if (req.RequestedQuantity <= 0)
            throw new BadRequestException("The requested quantity must be greater than zero.");
        EnsureQuantityScale(req.RequestedQuantity, "The requested quantity");
        var uom   = Text(req.RequestedUomCode, UomMax, "The unit of measure");
        var notes = Text(req.Notes, LineNotesMax, "Line notes");

        var (product, variant) = await ResolveCatalogAsync(
            req.ProductUuid, req.VariantUuid, line.ProductUuid, line.VariantUuid, "variant");

        line.ProductUuid           = product;
        line.VariantUuid           = variant;
        line.ProductDescription    = description;
        line.RequestedQuantity     = req.RequestedQuantity;
        line.RequestedUomCode      = uom;
        line.RequestedDeliveryDate = req.RequestedDeliveryDate?.Date;
        line.Notes                 = notes;
    }

    /// <summary>
    /// PB-05 / §3.5. Each status keeps only the fields that apply to it — the rest are cleared:
    /// CAN_SUPPLY keeps the delivery date (required); PARTIAL the can-supply quantity and date (both required,
    /// BR-C1-05); CANNOT_SUPPLY the reason (required, active, own organization — BR-C1-04), its notes and the
    /// optional alternative (with a delivery date only when an alternative is named); UNDER_REVIEW an optional
    /// date; PENDING nothing. Procurement fields are kept for every status. Leaving or changing an evaluation
    /// stamps the reviewer; going back to PENDING clears it.
    /// </summary>
    private async Task ApplyEvaluationAsync(SaleInquiryLine line, UpdateSaleInquiryLineRequest req, int userId)
    {
        if (!EnumCode<SaleInquiryLineStatus>.TryParse(req.LineStatus?.Trim().ToUpperInvariant(), out var status))
            throw new BadRequestException(
                $"'{req.LineStatus}' is not a line status. Use PENDING, CAN_SUPPLY, PARTIAL, CANNOT_SUPPLY or UNDER_REVIEW.");
        if (req.ProcurementLeadDays < 0)
            throw new BadRequestException("The procurement lead time cannot be negative.");

        decimal?  canSupply      = null;
        DateTime? delivery       = null;
        int?      reasonId       = null;
        string?   rejectionNotes = null;
        Guid?     altProduct     = null;
        Guid?     altVariant     = null;
        string?   altNotes       = null;

        switch (status)
        {
            case SaleInquiryLineStatus.CanSupply:
                delivery = req.EstimatedDeliveryDate?.Date
                    ?? throw new BadRequestException("A line that can be supplied needs an estimated delivery date.");
                break;

            case SaleInquiryLineStatus.Partial:
                if (req.CanSupplyQuantity is not { } quantity || quantity <= 0 || quantity >= line.RequestedQuantity)
                    throw new BadRequestException(
                        $"A partially supplied line needs a can-supply quantity greater than 0 and less than the " +
                        $"requested {line.RequestedQuantity:0.####}.");
                EnsureQuantityScale(quantity, "The can-supply quantity");
                canSupply = quantity;
                delivery = req.EstimatedDeliveryDate?.Date
                    ?? throw new BadRequestException("A partially supplied line needs an estimated delivery date.");
                break;

            case SaleInquiryLineStatus.CannotSupply:
                reasonId       = await ResolveRejectionReasonAsync(req.RejectionReasonUuid, line.RejectionReasonId);
                rejectionNotes = Text(req.RejectionNotes, RejectionNotesMax, "Rejection notes");
                (altProduct, altVariant) = await ResolveCatalogAsync(
                    req.AlternativeProductUuid, req.AlternativeVariantUuid,
                    line.AlternativeProductUuid, line.AlternativeVariantUuid, "alternative variant");
                altNotes = Text(req.AlternativeNotes, AlternativeNotesMax, "Alternative notes");
                if (altProduct is not null || altVariant is not null)
                    delivery = req.EstimatedDeliveryDate?.Date;
                break;

            case SaleInquiryLineStatus.UnderReview:
                delivery = req.EstimatedDeliveryDate?.Date;
                break;
        }

        var newStatus = EnumCode<SaleInquiryLineStatus>.Of(status);
        var changed = line.LineStatus != newStatus
                   || line.CanSupplyQuantity != canSupply
                   || line.EstimatedDeliveryDate != delivery
                   || line.RejectionReasonId != reasonId
                   || line.RejectionNotes != rejectionNotes
                   || line.AlternativeProductUuid != altProduct
                   || line.AlternativeVariantUuid != altVariant
                   || line.AlternativeNotes != altNotes
                   || line.RequiresProcurement != req.RequiresProcurement
                   || line.ProcurementLeadDays != req.ProcurementLeadDays;

        line.LineStatus             = newStatus;
        line.CanSupplyQuantity      = canSupply;
        line.EstimatedDeliveryDate  = delivery;
        line.RejectionReasonId      = reasonId;
        line.RejectionNotes         = rejectionNotes;
        line.AlternativeProductUuid = altProduct;
        line.AlternativeVariantUuid = altVariant;
        line.AlternativeNotes       = altNotes;
        line.RequiresProcurement    = req.RequiresProcurement;
        line.ProcurementLeadDays    = req.ProcurementLeadDays;

        if (status == SaleInquiryLineStatus.Pending)
        {
            line.ReviewedByUserId = null;
            line.ReviewedAt       = null;
        }
        else if (changed)
        {
            line.ReviewedByUserId = userId;
            line.ReviewedAt       = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// BR-C1-04 — a reason of this organization that is active. A line already on a reason that has since been
    /// deactivated may keep it (BR-C5-03: deactivated reasons stay on historical lines); only setting one needs it active.
    /// </summary>
    private async Task<int> ResolveRejectionReasonAsync(Guid? reasonUuid, int? currentReasonId)
    {
        if (reasonUuid is null || reasonUuid == Guid.Empty)
            throw new BadRequestException("A line that cannot be supplied needs a rejection reason.");

        var org = Org;
        var reason = await _db.RejectionReasons.AsNoTracking()
            .Where(r => r.UUID == reasonUuid && r.OrganizationId == org)
            .Select(r => new { r.Id, r.IsActive })
            .FirstOrDefaultAsync();

        if (reason is null || (!reason.IsActive && reason.Id != currentReasonId))
            throw new BadRequestException("The rejection reason was not found or is inactive.");
        return reason.Id;
    }

    /// <summary>
    /// A variant named on a line must be a known, active catalog variant (when the catalog is reachable), and its
    /// product is filled in or checked against it. Unchanged values are not re-checked, so a variant deactivated
    /// after it was recorded does not block editing the rest of the line.
    /// </summary>
    private async Task<(Guid? Product, Guid? Variant)> ResolveCatalogAsync(
        Guid? product, Guid? variant, Guid? currentProduct, Guid? currentVariant, string what)
    {
        product = product == Guid.Empty ? null : product;
        variant = variant == Guid.Empty ? null : variant;

        if (variant is null || _variants is null) return (product, variant);
        if (variant == currentVariant && (product is null || product == currentProduct)) return (currentProduct ?? product, variant);

        var described = await _variants.DescribeVariantsAsync([variant.Value]);
        if (!described.TryGetValue(variant.Value, out var v))
            throw new BadRequestException($"The {what} was not found in the catalog or is inactive.");
        if (product is not null && product != v.ProductUuid)
            throw new BadRequestException($"The {what} does not belong to the selected product.");
        return (v.ProductUuid, variant);
    }

    private static void EnsureQuantityScale(decimal quantity, string label)
    {
        if (decimal.Round(quantity, 4) != quantity)
            throw new BadRequestException($"{label} can have at most 4 decimal places.");
    }

    /// <summary>Trimmed; blank becomes null; longer than the column → 400.</summary>
    private static string? Text(string? value, int max, string label)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Length > max)
            throw new BadRequestException($"{label} cannot be longer than {max} characters.");
        return trimmed;
    }

    // ── Read helpers ────────────────────────────────────────────────────────────

    private async Task<IReadOnlyDictionary<int, string>> UserNamesAsync(IReadOnlyList<int> userIds)
    {
        if (_users is null || userIds.Count == 0) return new Dictionary<int, string>();
        var found = await _users.GetUsersAsync(userIds);
        return found.GroupBy(u => u.UserId).ToDictionary(g => g.Key, g => g.First().DisplayName);
    }

    private static string? NameOf(IReadOnlyDictionary<int, string> names, int? userId) =>
        userId is { } id && names.TryGetValue(id, out var name) ? name : null;

    private static SaleInquiryLineModel ToLineModel(SaleInquiryLine l,
        IReadOnlyDictionary<Guid, VariantDescription> variants, IReadOnlyDictionary<int, string> users)
    {
        VariantDescription? variant = l.VariantUuid is { } v && variants.TryGetValue(v, out var d) ? d : null;
        VariantDescription? alternative = l.AlternativeVariantUuid is { } a && variants.TryGetValue(a, out var ad) ? ad : null;

        return new SaleInquiryLineModel
        {
            Uuid                       = l.UUID,
            LineNumber                 = l.LineNumber,
            ProductUuid                = l.ProductUuid,
            VariantUuid                = l.VariantUuid,
            VariantSku                 = variant?.Sku,
            VariantName                = variant?.DisplayName,
            ProductDescription         = l.ProductDescription,
            RequestedQuantity          = l.RequestedQuantity,
            RequestedUomCode           = l.RequestedUomCode,
            RequestedDeliveryDate      = l.RequestedDeliveryDate,
            LineStatus                 = l.LineStatus,
            CanSupplyQuantity          = l.CanSupplyQuantity,
            EstimatedDeliveryDate      = l.EstimatedDeliveryDate,
            RejectionReasonUuid        = l.RejectionReason?.UUID,
            RejectionReasonCode        = l.RejectionReason?.Code,
            RejectionReasonDescription = l.RejectionReason?.Description,
            RejectionNotes             = l.RejectionNotes,
            AlternativeProductUuid     = l.AlternativeProductUuid,
            AlternativeVariantUuid     = l.AlternativeVariantUuid,
            AlternativeVariantSku      = alternative?.Sku,
            AlternativeVariantName     = alternative?.DisplayName,
            AlternativeNotes           = l.AlternativeNotes,
            RequiresProcurement        = l.RequiresProcurement,
            ProcurementLeadDays        = l.ProcurementLeadDays,
            ReviewedByUserId           = l.ReviewedByUserId,
            ReviewedByUserName         = NameOf(users, l.ReviewedByUserId),
            ReviewedAt                 = l.ReviewedAt,
            Notes                      = l.Notes
        };
    }
}
